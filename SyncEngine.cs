namespace YaDiskFileSync;

internal enum SyncState
{
    /// <summary>Папки не настроены.</summary>
    NotConfigured,
    /// <summary>Нужно войти в Яндекс.</summary>
    NeedLogin,
    /// <summary>Есть изменения, ждём прохода.</summary>
    Pending,
    Syncing,
    Synced,
    /// <summary>Временная ошибка (сеть, файл занят) — повторим сами.</summary>
    Retrying,
    /// <summary>Ошибка, которую повтор не исправит (нет места, нет прав, нет папки).</summary>
    Error,
    /// <summary>Пропало слишком много файлов — ждём решения пользователя.</summary>
    NeedsConfirm,
}

internal sealed record SyncStatus(SyncState State, string Message, DateTime? LastSynced = null, DateTime? NextRetry = null);

/// <summary>Массовое удаление, ждущее решения пользователя.</summary>
internal sealed record DeletionQuestion(int LocalDeletes, int RemoteDeletes, int BaseFiles, bool RemoteRootMissing,
    IReadOnlyList<string> Samples, IReadOnlySet<string> Keys);

internal enum RowState { Queued, Active, Done, Failed, Synced }

/// <summary>Строка окна «Статус синхронизации».</summary>
internal sealed record StatusRow(string Rel, string Action, RowState State, long Done, long Total, string? Error);

/// <summary>
/// Двусторонняя синхронизация папки на компьютере с папкой на Диске. Один проход: обойти обе стороны,
/// сверить с базой (<see cref="Planner"/>), выполнить действия. Проходы склеиваются: пока идёт один,
/// новые события дают ещё ровно один.
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
    private readonly StateStore _store;
    private readonly ILocalTrash _trash;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private bool _dirty;
    private Task? _loop;
    private CancellationTokenSource? _retry;   // отложенный повтор — всегда не больше одного

    // решение пользователя о массовом удалении: какие удаления разрешены / какие файлы вернуть
    private IReadOnlySet<string>? _allowedDeletes;
    private IReadOnlySet<string>? _restore;

    // для окна статуса
    private readonly object _rowsGate = new();
    private List<Activity> _activity = new();
    private Dictionary<string, BaseEntry> _lastBase = new(Planner.Paths);

    public event Action<SyncStatus>? StatusChanged;
    public SyncStatus Status { get; private set; }
    public DeletionQuestion? Question { get; private set; }

    public SyncEngine(Func<IDisk?> disk, Settings settings, Action saveSettings, StateStore store, ILocalTrash trash,
        Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _disk = disk;
        _settings = settings;
        _saveSettings = saveSettings;
        _store = store;
        _trash = trash;
        _now = now ?? (() => DateTime.Now);
        _delay = delay ?? Task.Delay;
        Status = new SyncStatus(SyncState.NotConfigured, "Папки не настроены.", settings.LastSyncedAt);
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    /// <summary>Что-то изменилось (или пора опросить Диск): пройти при первой возможности.</summary>
    public void Request()
    {
        lock (_gate) _dirty = true;
        _wake.Release();
    }

    /// <summary>Ответ на вопрос о массовом удалении: true — удалить, false — вернуть файлы на ту сторону, где пропали.</summary>
    public void Resolve(bool delete)
    {
        if (Question is not { } q) return;
        lock (_gate)
        {
            if (delete) _allowedDeletes = q.Keys;
            else _restore = q.Keys;
        }
        Question = null;
        Request();
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
            _retry?.Dispose();
            _retry = null;
            SyncStatus result;
            try { result = await RunOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                // непредвиденное (странный ответ сервера и т.п.) не должно останавливать синхронизацию навсегда
                result = new SyncStatus(SyncState.Retrying, "Сбой: " + e.Message, _settings.LastSyncedAt);
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

    private SyncStatus Say(SyncState s, string msg) => new(s, msg, _settings.LastSyncedAt);

    /// <summary>Один проход синхронизации. Возвращает итоговое состояние (не публикует его).</summary>
    internal async Task<SyncStatus> RunOnceAsync(CancellationToken ct)
    {
        string? localRoot = _settings.LocalRoot, remoteRoot = _settings.RemoteRoot;
        if (string.IsNullOrEmpty(localRoot) || string.IsNullOrEmpty(remoteRoot) || DiskPath.IsRoot(remoteRoot))
            return Say(SyncState.NotConfigured, "Папки не настроены.");
        if (_disk() is not { } disk)
            return Say(SyncState.NeedLogin, "Войдите в Яндекс.");

        try
        {
            if (string.IsNullOrEmpty(_settings.AccountUid))
            {
                var user = await disk.GetUserAsync(ct) ?? throw new DiskException("Яндекс.Диск не сообщил учётную запись.", DiskErrorKind.Transient);
                _settings.AccountUid = user.Uid;
                _settings.AccountLogin = user.Login;
                TrySaveSettings();
            }
            string key = StateStore.KeyFor(_settings.AccountUid, localRoot, remoteRoot);
            var baseState = _store.Load(key);

            if (!Directory.Exists(localRoot))
            {
                // пропавшая папка с историей — отключённый диск или ошибка; создавать пустую и удалять всё на Диске нельзя
                if (baseState.Count > 0)
                    return Say(SyncState.Error, $"Папка не найдена: {localRoot}. Подключите диск или выберите папку в настройках.");
                Directory.CreateDirectory(localRoot);
            }
            Explorer.HideServiceDir(localRoot);

            ApplyRestore(baseState, key);

            Publish(Say(SyncState.Syncing, "Сверяю папки…"));
            var remoteList = await disk.ListTreeAsync(remoteRoot, ct);
            bool remoteMissing = remoteList == null;
            var remote = new Dictionary<string, RemoteItem>(Planner.Paths);
            var dupes = new List<string>();
            foreach (var r in remoteList ?? Array.Empty<RemoteItem>())
            {
                if (Ignore.Rel(r.Rel)) continue;
                if (!remote.TryAdd(r.Rel, r)) dupes.Add(r.Rel);   // «Отчёт.xlsx» и «отчёт.xlsx» — Windows так не умеет
            }

            LocalScan.Result scan;
            try { scan = LocalScan.Scan(localRoot, baseState, ct); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { return Say(SyncState.Retrying, "Не удалось прочитать папку: " + e.Message); }

            var plan = Planner.Plan(baseState, scan.Items, remote);
            // с занятыми файлами в этом проходе ничего не делаем
            var ops = plan.Ops.Where(o => !scan.Busy.ContainsKey(o.Rel) && !dupes.Contains(o.Rel, Planner.Paths)).ToList();

            if (plan.NeedsConfirmation(remoteMissing))
            {
                IReadOnlySet<string>? allowed;
                lock (_gate) allowed = _allowedDeletes;
                var keys = plan.DeletionKeys;
                if (allowed == null || !keys.IsSubsetOf(allowed))
                {
                    var samples = ops.Where(o => o.Kind is OpKind.DeleteLocal or OpKind.DeleteRemote && !o.IsDir).Select(o => o.Rel).Take(10).ToList();
                    Question = new DeletionQuestion(plan.LocalDeletes, plan.RemoteDeletes, plan.BaseFiles, remoteMissing, samples, keys);
                    SetActivity(new List<Activity>(), baseState);
                    return Say(SyncState.NeedsConfirm, Explain(Question));
                }
            }
            lock (_gate) _allowedDeletes = null;
            Question = null;

            return await ExecuteAsync(disk, key, localRoot, remoteRoot, remoteMissing, remote, baseState, ops, scan.Busy, dupes, ct);
        }
        catch (DiskException e)
        {
            return Say(e.Kind switch
            {
                DiskErrorKind.Auth => SyncState.NeedLogin,
                DiskErrorKind.Transient => SyncState.Retrying,
                _ => SyncState.Error,
            }, e.Message);
        }
    }

    internal static string Explain(DeletionQuestion q)
    {
        if (q.RemoteRootMissing)
            return "Папка на Яндекс.Диске пропала (удалена или переименована). Удалить и файлы на компьютере?";
        var parts = new List<string>();
        if (q.RemoteDeletes > 0) parts.Add($"на компьютере пропало {q.RemoteDeletes} из {q.BaseFiles} файлов");
        if (q.LocalDeletes > 0) parts.Add($"на Диске пропало {q.LocalDeletes} из {q.BaseFiles} файлов");
        return "Синхронизация на паузе: " + string.Join(", ", parts) + ". Нужно ваше решение.";
    }

    /// <summary>Пользователь выбрал «вернуть»: забываем базу этих файлов (и всех папок) — тогда они просто скопируются обратно.</summary>
    private void ApplyRestore(Dictionary<string, BaseEntry> baseState, string key)
    {
        IReadOnlySet<string>? restore;
        lock (_gate) { restore = _restore; _restore = null; }
        if (restore == null) return;
        foreach (string k in restore) baseState.Remove(k[2..]);
        foreach (var dir in baseState.Where(kv => kv.Value.IsDir).Select(kv => kv.Key).ToList()) baseState.Remove(dir);
        _store.Save(key, baseState);
    }

    private async Task<SyncStatus> ExecuteAsync(IDisk disk, string key, string localRoot, string remoteRoot, bool remoteMissing,
        Dictionary<string, RemoteItem> remote, Dictionary<string, BaseEntry> baseState, List<SyncOp> ops,
        IReadOnlyDictionary<string, string> busy, List<string> dupes, CancellationToken ct)
    {
        // порядок: папки (сверху вниз) → передачи → удаление файлов → удаление папок (снизу вверх)
        static int Rank(SyncOp o) => o.Kind switch
        {
            OpKind.MkdirLocal or OpKind.MkdirRemote => 0,
            OpKind.Record or OpKind.Forget or OpKind.Skip => 1,
            OpKind.Upload or OpKind.Download => 2,
            _ => o.IsDir ? 4 : 3,
        };
        ops = ops.OrderBy(Rank).ThenBy(o => Rank(o) == 4 ? -Depth(o.Rel) : Depth(o.Rel)).ThenBy(o => o.Rel, Planner.Paths).ToList();

        var acts = ops.Where(o => o.Kind is not (OpKind.Record or OpKind.Forget))
                      .Select(o => new Activity(o.Rel, o.Kind, o.IsDir ? 0 : o.Kind == OpKind.Download ? o.Remote?.Size ?? 0 : o.Local?.Size ?? 0))
                      .ToList();
        foreach (var (rel, why) in busy) acts.Add(new Activity(rel, OpKind.Skip, 0) { State = RowState.Failed, Error = why });
        foreach (string rel in dupes) acts.Add(new Activity(rel, OpKind.Skip, 0)
            { State = RowState.Failed, Error = "на Диске есть файлы, чьи имена отличаются только регистром букв" });
        SetActivity(acts, baseState);
        var actByOp = new Dictionary<SyncOp, Activity>();
        int ai = 0;
        foreach (var o in ops) if (o.Kind is not (OpKind.Record or OpKind.Forget)) actByOp[o] = acts[ai++];

        var knownRemoteDirs = new HashSet<string>(remote.Values.Where(r => r.IsDir).Select(r => r.Rel), Planner.Paths) { "" };
        if (remoteMissing)
        {
            await disk.EnsureFolderAsync(remoteRoot, ct);
        }

        int failed = 0, retry = busy.Count;
        bool again = false;
        string? firstError = null;
        int sinceSave = 0;
        int transferred = ops.Count(o => o.Kind is OpKind.Upload or OpKind.Download);
        int done = 0;
        try
        {
            foreach (var op in ops)
            {
                ct.ThrowIfCancellationRequested();
                actByOp.TryGetValue(op, out var act);
                if (act != null) act.State = RowState.Active;
                if (op.Kind is OpKind.Upload or OpKind.Download)
                    Publish(Say(SyncState.Syncing, $"Синхронизирую {++done} из {transferred}: {Path.GetFileName(op.Rel)}"));
                try
                {
                    var outcome = await RunOpAsync(disk, op, act, localRoot, remoteRoot, baseState, knownRemoteDirs, ct);
                    if (outcome == Outcome.Changed) again = true;
                    if (act != null)
                    {
                        act.State = outcome == Outcome.Changed ? RowState.Queued : RowState.Done;
                        if (outcome == Outcome.Changed) act.Error = "файл изменился во время синхронизации — повторю";
                    }
                }
                catch (DiskException e) when (e.Kind == DiskErrorKind.Fatal)
                {
                    failed++;
                    firstError ??= $"{op.Rel}: {e.Message}";
                    if (act != null) { act.State = RowState.Failed; act.Error = e.Message; }
                }
                catch (DiskException e) when (e.NotFound)
                {
                    // на Диске файла уже нет (удалили, пока шёл проход) — разберёмся в следующем проходе
                    again = true;
                    if (act != null) { act.State = RowState.Queued; act.Error = e.Message; }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    retry++;
                    firstError ??= $"{op.Rel}: {e.Message}";
                    if (act != null) { act.State = RowState.Failed; act.Error = LocalError(e); }
                }
                catch (DiskException e) when (act != null)
                {
                    act.State = RowState.Failed;
                    act.Error = e.Message;
                    throw;
                }
                if (++sinceSave >= 50) { _store.Save(key, baseState); sinceSave = 0; }
            }
        }
        finally
        {
            try { _store.Save(key, baseState); } catch { /* база не записалась — в худшем случае следующий проход сверит заново */ }
            SetActivity(acts, baseState);
        }

        int skipped = ops.Count(o => o.Kind == OpKind.Skip);
        if (again) Request();
        if (failed > 0)
            return Say(SyncState.Error, failed == 1 ? "Не синхронизирован файл " + firstError : $"Не синхронизировано файлов: {failed}. {firstError}");
        if (retry > 0)
            return Say(SyncState.Retrying, retry == 1 && firstError != null ? "Не удалось: " + firstError
                : $"Файлов занято или недоступно: {retry} — повторю позже.");

        _settings.LastSyncedAt = _now();
        TrySaveSettings();
        string note = skipped + dupes.Count > 0 ? $" (пропущено: {skipped + dupes.Count}, см. «Статус синхронизации»)" : "";
        return new SyncStatus(SyncState.Synced, "Синхронизировано" + note, _settings.LastSyncedAt);
    }

    private static string LocalError(Exception e) =>
        e is UnauthorizedAccessException ? "нет доступа к файлу" : "файл занят другой программой — повторю позже";

    private static int Depth(string rel) => rel.Count(c => c == '/');

    private enum Outcome { Done, Changed }

    private async Task<Outcome> RunOpAsync(IDisk disk, SyncOp op, Activity? act, string localRoot, string remoteRoot,
        Dictionary<string, BaseEntry> baseState, HashSet<string> knownRemoteDirs, CancellationToken ct)
    {
        string full = Path.Combine(localRoot, op.Rel.Replace('/', '\\'));
        string remotePath = DiskPath.Combine(remoteRoot, op.Rel);
        switch (op.Kind)
        {
            case OpKind.Record:
                baseState[op.Rel] = op.IsDir ? new BaseEntry { IsDir = true }
                    : new BaseEntry { Sha256 = op.Local!.Sha256, Size = op.Local.Size, LocalMtime = op.Local.Mtime, RemoteModified = op.Remote!.Modified };
                return Outcome.Done;

            case OpKind.Forget:
                baseState.Remove(op.Rel);
                return Outcome.Done;

            case OpKind.Skip:
                if (act != null) { act.State = RowState.Failed; act.Error = op.Reason; }
                return Outcome.Done;

            case OpKind.MkdirLocal:
                Directory.CreateDirectory(full);
                baseState[op.Rel] = new BaseEntry { IsDir = true };
                return Outcome.Done;

            case OpKind.MkdirRemote:
                await EnsureRemoteDirAsync(disk, remoteRoot, op.Rel, knownRemoteDirs, ct);
                baseState[op.Rel] = new BaseEntry { IsDir = true };
                return Outcome.Done;

            case OpKind.Upload:
                return await UploadAsync(disk, op, act, full, remotePath, remoteRoot, baseState, knownRemoteDirs, ct);

            case OpKind.Download:
                return await DownloadAsync(disk, op, act, full, localRoot, remotePath, baseState, ct);

            case OpKind.DeleteRemote:
                await disk.DeleteAsync(remotePath, ct);
                baseState.Remove(op.Rel);
                if (op.IsDir) RemoveUnder(baseState, op.Rel);
                return Outcome.Done;

            case OpKind.DeleteLocal:
                if (op.IsDir)
                {
                    if (!Directory.Exists(full)) { baseState.Remove(op.Rel); return Outcome.Done; }
                    // что-то появилось, пока шёл проход — не трогаем, следующий проход это загрузит
                    if (Directory.EnumerateFileSystemEntries(full).Any(e => !Ignore.Name(Path.GetFileName(e)))) return Outcome.Changed;
                    _trash.Recycle(full);
                    baseState.Remove(op.Rel);
                    return Outcome.Done;
                }
                if (!File.Exists(full)) { baseState.Remove(op.Rel); return Outcome.Done; }
                if (!SameAsScanned(full, op.Local!)) return Outcome.Changed;
                _trash.Recycle(full);
                baseState.Remove(op.Rel);
                return Outcome.Done;
        }
        return Outcome.Done;
    }

    private static void RemoveUnder(Dictionary<string, BaseEntry> baseState, string rel)
    {
        foreach (var k in baseState.Keys.Where(k => k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)).ToList())
            baseState.Remove(k);
    }

    private static bool SameAsScanned(string full, LocalItem l)
    {
        var fi = new FileInfo(full);
        return fi.Exists && fi.Length == l.Size && fi.LastWriteTimeUtc == l.Mtime;
    }

    private static async Task EnsureRemoteDirAsync(IDisk disk, string remoteRoot, string rel, HashSet<string> known, CancellationToken ct)
    {
        if (known.Contains(rel)) return;
        await disk.EnsureFolderAsync(DiskPath.Combine(remoteRoot, rel), ct);
        // EnsureFolderAsync создаёт и родителей — запоминаем всю цепочку
        for (string? r = rel; !string.IsNullOrEmpty(r); r = r.Contains('/') ? r[..r.LastIndexOf('/')] : null) known.Add(r);
    }

    private async Task<Outcome> UploadAsync(IDisk disk, SyncOp op, Activity? act, string full, string remotePath, string remoteRoot,
        Dictionary<string, BaseEntry> baseState, HashSet<string> knownRemoteDirs, CancellationToken ct)
    {
        var l = op.Local!;
        int slash = op.Rel.LastIndexOf('/');
        if (slash > 0) await EnsureRemoteDirAsync(disk, remoteRoot, op.Rel[..slash], knownRemoteDirs, ct);

        await using var fs = LocalScan.OpenShared(full);
        if (!SameAsScanned(full, l) || fs.Length != l.Size) return Outcome.Changed;
        await using var hashing = new HashingStream(fs, reading: true);
        if (act != null) act.Done = 0;
        await disk.UploadAsync(remotePath, hashing, l.Size, n => { if (act != null) Interlocked.Add(ref act.Done, n); }, ct);
        string sent = hashing.Sha256Hex();
        // в базу — то, что реально ушло на Диск (а не то, что лежит в файле сейчас): если файл успели
        // поменять, следующий проход увидит «изменён на компьютере» и загрузит снова
        baseState[op.Rel] = new BaseEntry { Sha256 = sent, Size = hashing.Count, LocalMtime = l.Mtime, RemoteModified = null };
        return Planner.SameHash(sent, l.Sha256) && SameAsScanned(full, l) ? Outcome.Done : Outcome.Changed;
    }

    private async Task<Outcome> DownloadAsync(IDisk disk, SyncOp op, Activity? act, string full, string localRoot, string remotePath,
        Dictionary<string, BaseEntry> baseState, CancellationToken ct)
    {
        var r = op.Remote!;
        string tmpDir = Path.Combine(localRoot, Ignore.ServiceDir, "tmp");
        Directory.CreateDirectory(tmpDir);
        string tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));
        try
        {
            string got;
            long size;
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            await using (var hashing = new HashingStream(fs, reading: false))
            {
                if (act != null) act.Done = 0;
                await disk.DownloadAsync(remotePath, hashing, n => { if (act != null) Interlocked.Add(ref act.Done, n); }, ct);
                await hashing.FlushAsync(ct);
                got = hashing.Sha256Hex();
                size = hashing.Count;
            }
            // файл на Диске успели заменить, пока скачивали, — сверимся заново
            if (r.Sha256 != null ? !Planner.SameHash(got, r.Sha256) : size != r.Size) return Outcome.Changed;

            // локальный файл не должен был поменяться с момента обхода: иначе затрём свежие правки
            if (op.Local != null ? !SameAsScanned(full, op.Local) : File.Exists(full) || Directory.Exists(full)) return Outcome.Changed;

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (op.Local != null && op.ReplacesLocalChanges) _trash.Recycle(full);
            File.Move(tmp, full, overwrite: true);
            DateTime mtime = r.Modified ?? DateTime.UtcNow;
            try { File.SetLastWriteTimeUtc(full, mtime); } catch (IOException) { }
            var fi = new FileInfo(full);
            baseState[op.Rel] = new BaseEntry { Sha256 = got, Size = fi.Length, LocalMtime = fi.LastWriteTimeUtc, RemoteModified = r.Modified };
            return Outcome.Done;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    // ---------- окно статуса ----------

    internal sealed class Activity(string rel, OpKind kind, long total)
    {
        public string Rel { get; } = rel;
        public OpKind Kind { get; } = kind;
        public long Total { get; } = total;
        public long Done;
        public RowState State { get; set; } = RowState.Queued;
        public string? Error { get; set; }
    }

    private void SetActivity(List<Activity> acts, Dictionary<string, BaseEntry> baseState)
    {
        lock (_rowsGate)
        {
            _activity = acts;
            _lastBase = new Dictionary<string, BaseEntry>(baseState, Planner.Paths);
        }
    }

    /// <summary>Строки для окна статуса: текущие действия и все синхронизированные файлы.</summary>
    public IReadOnlyList<StatusRow> Rows()
    {
        lock (_rowsGate)
        {
            var rows = new List<StatusRow>();
            var seen = new HashSet<string>(Planner.Paths);
            foreach (var a in _activity)
            {
                seen.Add(a.Rel);
                rows.Add(new StatusRow(a.Rel, ActionName(a.Kind, a.Error != null && a.Kind == OpKind.Skip), a.State,
                    Interlocked.Read(ref a.Done), a.Total, a.Error));
            }
            foreach (var (rel, b) in _lastBase)
                if (!b.IsDir && seen.Add(rel)) rows.Add(new StatusRow(rel, "", RowState.Synced, b.Size, b.Size, null));
            return rows;
        }
    }

    private static string ActionName(OpKind k, bool problem) => k switch
    {
        OpKind.Upload => "Загрузка на Диск",
        OpKind.Download => "Скачивание с Диска",
        OpKind.DeleteRemote => "Удаление на Диске (в Корзину)",
        OpKind.DeleteLocal => "Удаление на компьютере (в Корзину)",
        OpKind.MkdirRemote => "Новая папка на Диске",
        OpKind.MkdirLocal => "Новая папка на компьютере",
        OpKind.Skip => problem ? "Пропущен" : "",
        _ => "",
    };

    private void TrySaveSettings()
    {
        try { _saveSettings(); } catch { }
    }

    private void Publish(SyncStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }

    /// <summary>Сообщить состояние, не связанное с проходом (сменили папки, вышли из Яндекса).</summary>
    public void Set(SyncStatus s) => Publish(s);

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _stop.Dispose();
    }
}
