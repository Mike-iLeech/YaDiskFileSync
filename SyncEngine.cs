using System.IO.Compression;
using System.Security.Cryptography;

namespace YaDiskFileSync;

internal enum SyncState
{
    /// <summary>Файл не выбран.</summary>
    NotConfigured,
    /// <summary>Нужно войти в Яндекс.</summary>
    NeedLogin,
    /// <summary>Есть изменения, ждём загрузки.</summary>
    Pending,
    Uploading,
    Synced,
    /// <summary>Временная ошибка (сеть, файл занят) — повторим сами.</summary>
    Retrying,
    /// <summary>Ошибка, которую повтор не исправит (нет места и т.п.) или файла нет.</summary>
    Error,
}

internal sealed record SyncStatus(SyncState State, string Message, DateTime? LastSynced = null, DateTime? NextRetry = null);

/// <summary>Снимок файла, прочитанный целиком и без помех от Excel.</summary>
internal sealed record FileSnapshot(byte[] Data, string Sha256);

/// <summary>
/// Грузит выбранный файл на Диск: основная копия «app:/имя.xlsx» и дневная копия в «app:/История».
/// Запросы склеиваются: пока идёт загрузка, новые сохранения дают ещё ровно один проход.
/// </summary>
internal sealed class SyncEngine : IDisposable
{
    /// <summary>Паузы между повторами при временных ошибках; дальше — последняя.</summary>
    internal static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
    };

    private readonly Func<IDisk?> _disk;
    private readonly Settings _settings;
    private readonly Action _saveSettings;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private bool _dirty;
    private Task? _loop;
    private CancellationTokenSource? _retry;   // отложенный повтор — всегда не больше одного

    public event Action<SyncStatus>? StatusChanged;
    public SyncStatus Status { get; private set; }

    /// <summary>Чтение файла: сколько раз пробовать, если Excel его держит или ещё пишет.</summary>
    internal int ReadAttempts { get; set; } = 10;
    internal TimeSpan ReadRetryPause { get; set; } = TimeSpan.FromSeconds(1);

    public SyncEngine(Func<IDisk?> disk, Settings settings, Action saveSettings,
        Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _disk = disk;
        _settings = settings;
        _saveSettings = saveSettings;
        _now = now ?? (() => DateTime.Now);
        _delay = delay ?? Task.Delay;
        Status = new SyncStatus(SyncState.NotConfigured, "Файл не выбран.", settings.LastUploadedAt);
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    /// <summary>Файл изменился (или нажали «Синхронизировать»): загрузить при первой возможности.</summary>
    public void Request()
    {
        lock (_gate) _dirty = true;
        _wake.Release();
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            while (_wake.CurrentCount > 0) _wake.Wait(0);   // несколько сигналов подряд = один проход

            bool run;
            lock (_gate) { run = _dirty; _dirty = false; }
            if (!run) continue;

            _retry?.Cancel();
            SyncStatus result;
            try { result = await RunOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                // непредвиденное (странный ответ сервера и т.п.) не должно останавливать синхронизацию навсегда
                result = new SyncStatus(SyncState.Retrying, "Сбой: " + e.Message, _settings.LastUploadedAt);
            }
            if (result.State == SyncState.Retrying)
            {
                var pause = Backoff[Math.Min(failures, Backoff.Length - 1)];
                failures++;
                Publish(result with { NextRetry = _now() + pause });
                _retry = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = RetryLaterAsync(pause, _retry.Token);
            }
            else
            {
                failures = 0;
                Publish(result);
            }
        }
    }

    private async Task RetryLaterAsync(TimeSpan pause, CancellationToken ct)
    {
        try { await _delay(pause, ct); }
        catch (OperationCanceledException) { return; }
        Request();
    }

    /// <summary>Один проход синхронизации. Возвращает итоговое состояние (не публикует его).</summary>
    internal async Task<SyncStatus> RunOnceAsync(CancellationToken ct)
    {
        string? path = _settings.FilePath;
        if (string.IsNullOrEmpty(path))
            return new SyncStatus(SyncState.NotConfigured, "Файл не выбран.", _settings.LastUploadedAt);
        if (_disk() is not { } disk)
            return new SyncStatus(SyncState.NeedLogin, "Войдите в Яндекс.", _settings.LastUploadedAt);
        if (!File.Exists(path))
            return new SyncStatus(SyncState.Error, $"Файл не найден: {path}", _settings.LastUploadedAt);

        FileSnapshot snap;
        try { snap = await ReadStableAsync(path, ct); }
        catch (IOException e)
        { return new SyncStatus(SyncState.Retrying, "Файл занят или ещё сохраняется: " + e.Message, _settings.LastUploadedAt); }
        catch (UnauthorizedAccessException)
        { return new SyncStatus(SyncState.Error, "Нет доступа к файлу.", _settings.LastUploadedAt); }

        if (snap.Sha256 == _settings.LastUploadedHash && string.Equals(path, _settings.LastUploadedPath, StringComparison.OrdinalIgnoreCase))
            return new SyncStatus(SyncState.Synced, "Изменений нет.", _settings.LastUploadedAt);

        Publish(new SyncStatus(SyncState.Uploading, "Загрузка на Яндекс.Диск…", _settings.LastUploadedAt));
        string name = Path.GetFileName(path);
        DateTime now = _now();
        try
        {
            await disk.UploadAsync("app:/" + name, snap.Data, ct);
            await disk.EnsureFolderAsync(History.Folder, ct);
            await disk.UploadAsync($"{History.Folder}/{History.SnapshotName(name, now)}", snap.Data, ct);
        }
        catch (DiskException e)
        {
            return new SyncStatus(e.Kind switch
            {
                DiskErrorKind.Auth => SyncState.NeedLogin,
                DiskErrorKind.Transient => SyncState.Retrying,
                _ => SyncState.Error,
            }, e.Message, _settings.LastUploadedAt);
        }

        _settings.LastUploadedHash = snap.Sha256;
        _settings.LastUploadedPath = path;
        _settings.LastUploadedAt = now;
        try { _saveSettings(); } catch { /* не сохранилось — в худшем случае загрузим ещё раз */ }

        // чистка старых копий не должна портить итог: файл уже на Диске
        string note = "";
        try
        {
            var names = await disk.ListFileNamesAsync(History.Folder, ct);
            foreach (string old in History.Excess(name, names, _settings.KeepCopies))
                await disk.DeleteAsync($"{History.Folder}/{old}", ct);
        }
        catch (DiskException e) { note = " (старые копии не удалены: " + e.Message + ")"; }

        return new SyncStatus(SyncState.Synced, "Синхронизировано" + note, now);
    }

    /// <summary>
    /// Читает файл, не мешая Excel (он держит файл открытым), и проверяет, что за время чтения файл
    /// не менялся. Книга Excel — zip-архив: начало не «PK» значит, что файл ещё пишется.
    /// </summary>
    internal async Task<FileSnapshot> ReadStableAsync(string path, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var before = new FileInfo(path);
                (long len, DateTime mtime) = (before.Length, before.LastWriteTimeUtc);
                byte[] data;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    data = new byte[fs.Length];
                    await fs.ReadExactlyAsync(data, ct);
                }
                var after = new FileInfo(path);
                if (after.Length != len || after.LastWriteTimeUtc != mtime || data.Length != len)
                    throw new IOException("файл менялся во время чтения");
                if (IsZipBook(path) && !IsCompleteZip(data))
                    throw new IOException("файл записан не полностью");
                return new FileSnapshot(data, Convert.ToHexString(SHA256.HashData(data)));
            }
            catch (Exception e) when (e is IOException && e is not FileNotFoundException && attempt < ReadAttempts)
            {
                await _delay(ReadRetryPause, ct);
            }
        }
    }

    private static bool IsZipBook(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm" or ".xlsb" or ".xltx" or ".xltm";

    /// <summary>
    /// Zip пишется с начала, а оглавление — в самом конце: недописанный архив открыть не получится.
    /// Читаем оглавление и распаковываем каждую запись: обрезанные данные дают ошибку.
    /// </summary>
    internal static bool IsCompleteZip(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count == 0) return false;
            foreach (var entry in zip.Entries)
            {
                using var s = entry.Open();
                s.CopyTo(Stream.Null);
            }
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException) { return false; }
    }

    private void Publish(SyncStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }

    /// <summary>Сообщить состояние, не связанное с проходом (выбрали файл, вышли из Яндекса).</summary>
    public void Set(SyncStatus s) => Publish(s);

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _stop.Dispose();
    }
}
