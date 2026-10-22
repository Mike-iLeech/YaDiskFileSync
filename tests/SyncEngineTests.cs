using System.IO.Compression;
using Xunit;

namespace YaDiskFileSync.Tests;

public class SyncEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ydfs-sync-").FullName;
    private readonly FakeDisk _disk = new();
    private readonly Settings _settings = new();
    private DateTime _now = new(2026, 10, 22, 12, 0, 0);
    private int _saved;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private SyncEngine Engine(IDisk? disk = null) =>
        new(() => disk ?? _disk, _settings, () => _saved++, () => _now, (_, _) => Task.CompletedTask)
        { ReadAttempts = 3, ReadRetryPause = TimeSpan.Zero };

    /// <summary>Настоящий zip — как книга Excel.</summary>
    private string Book(string name = "Бухгалтерия.xlsx", string content = "v1")
    {
        string path = Path.Combine(_dir, name);
        File.Delete(path);
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(z.CreateEntry("sheet.xml").Open());
            w.Write(content);
        }
        _settings.FilePath = path;
        return path;
    }

    [Fact]
    public async Task Uploads_Main_And_DailyCopy()
    {
        Book();
        var r = await Engine().RunOnceAsync(default);
        Assert.Equal(SyncState.Synced, r.State);
        Assert.True(_disk.Files.ContainsKey("app:/Бухгалтерия.xlsx"));
        Assert.True(_disk.Files.ContainsKey("app:/История/Бухгалтерия 2026-10-22.xlsx"));
        Assert.Contains("app:/История", _disk.Folders);
        Assert.Equal(_now, _settings.LastUploadedAt);
        Assert.NotNull(_settings.LastUploadedHash);
        Assert.Equal(1, _saved);
    }

    [Fact]
    public async Task SameContent_NotUploadedAgain()
    {
        Book();
        var e = Engine();
        await e.RunOnceAsync(default);
        _disk.Log.Clear();
        var r = await e.RunOnceAsync(default);
        Assert.Equal(SyncState.Synced, r.State);
        Assert.Empty(_disk.Log);
    }

    [Fact]
    public async Task SameDay_OverwritesDailyCopy_NextDay_AddsOne()
    {
        var e = Engine();
        Book(content: "v1");
        await e.RunOnceAsync(default);
        Book(content: "v2");
        await e.RunOnceAsync(default);
        Assert.Single(_disk.Files.Keys, k => k.StartsWith("app:/История/"));
        _now = _now.AddDays(1);
        Book(content: "v3");
        await e.RunOnceAsync(default);
        Assert.Equal(2, _disk.Files.Keys.Count(k => k.StartsWith("app:/История/")));
        Assert.Equal(File.ReadAllBytes(_settings.FilePath!), _disk.Files["app:/Бухгалтерия.xlsx"]);
    }

    [Fact]
    public async Task OldCopies_Rotated()
    {
        _settings.KeepCopies = 3;
        for (int d = 1; d <= 5; d++)
            _disk.Files[$"app:/История/Бухгалтерия 2026-09-0{d}.xlsx"] = new byte[1];
        _disk.Files["app:/История/Чужой 2020-01-01.xlsx"] = new byte[1];
        Book();
        await Engine().RunOnceAsync(default);
        var left = _disk.Files.Keys.Where(k => k.StartsWith("app:/История/Бухгалтерия")).OrderBy(k => k).ToList();
        Assert.Equal(new[]
        {
            "app:/История/Бухгалтерия 2026-09-04.xlsx", "app:/История/Бухгалтерия 2026-09-05.xlsx",
            "app:/История/Бухгалтерия 2026-10-22.xlsx",
        }, left);
        Assert.True(_disk.Files.ContainsKey("app:/История/Чужой 2020-01-01.xlsx"));
    }

    [Fact]
    public async Task RotationFailure_StillSynced()
    {
        Book();
        _disk.FailList = new DiskException("сбой", DiskErrorKind.Transient);
        var r = await Engine().RunOnceAsync(default);
        Assert.Equal(SyncState.Synced, r.State);
        Assert.Contains("старые копии не удалены", r.Message);
        Assert.NotNull(_settings.LastUploadedHash);
    }

    [Theory]
    [InlineData((int)DiskErrorKind.Transient, (int)SyncState.Retrying)]
    [InlineData((int)DiskErrorKind.Auth, (int)SyncState.NeedLogin)]
    [InlineData((int)DiskErrorKind.Fatal, (int)SyncState.Error)]
    public async Task UploadFailure_Mapped_HashNotSaved(int kindN, int stateN)
    {
        var kind = (DiskErrorKind)kindN; var state = (SyncState)stateN;
        Book();
        _disk.FailUploads.Enqueue(new DiskException("x", kind));
        var r = await Engine().RunOnceAsync(default);
        Assert.Equal(state, r.State);
        Assert.Null(_settings.LastUploadedHash);   // следующая попытка загрузит снова
        Assert.Equal(0, _saved);
    }

    [Fact]
    public async Task DailyCopyFails_MainUploaded_StillRetries()
    {
        Book();
        var disk = new FlakySecondUpload(_disk);
        var r = await new SyncEngine(() => disk, _settings, () => _saved++, () => _now, (_, _) => Task.CompletedTask).RunOnceAsync(default);
        Assert.Equal(SyncState.Retrying, r.State);
        Assert.Null(_settings.LastUploadedHash);
    }

    private sealed class FlakySecondUpload(FakeDisk inner) : IDisk
    {
        private int _n;
        public Task UploadAsync(string path, byte[] data, CancellationToken ct) =>
            ++_n == 2 ? throw new DiskException("сеть", DiskErrorKind.Transient) : inner.UploadAsync(path, data, ct);
        public Task EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);
        public Task<IReadOnlyList<string>> ListFileNamesAsync(string folder, CancellationToken ct) => inner.ListFileNamesAsync(folder, ct);
        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);
    }

    [Fact]
    public async Task OtherFile_SameContent_Uploaded()
    {
        string a = Book("A.xlsx", "same");
        var e = Engine();
        await e.RunOnceAsync(default);
        string b = Path.Combine(_dir, "B.xlsx");
        File.Copy(a, b);
        _settings.FilePath = b;
        await e.RunOnceAsync(default);
        Assert.True(_disk.Files.ContainsKey("app:/B.xlsx"));
    }

    [Fact]
    public async Task NotConfigured_NoLogin_Missing()
    {
        Assert.Equal(SyncState.NotConfigured, (await Engine().RunOnceAsync(default)).State);
        Book();
        Assert.Equal(SyncState.NeedLogin, (await new SyncEngine(() => null, _settings, () => { }).RunOnceAsync(default)).State);
        _settings.FilePath = Path.Combine(_dir, "нет.xlsx");
        Assert.Equal(SyncState.Error, (await Engine().RunOnceAsync(default)).State);
    }

    [Fact]
    public async Task Locked_ForWriting_StillReads()
    {
        // Excel держит книгу открытой с запретом записи другим — читать это не мешает
        string p = Book();
        using var hold = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.Equal(SyncState.Synced, (await Engine().RunOnceAsync(default)).State);
    }

    [Fact]
    public async Task ExclusiveLock_IsRetrying()
    {
        string p = Book();
        using var hold = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var r = await Engine().RunOnceAsync(default);
        Assert.Equal(SyncState.Retrying, r.State);
        Assert.Empty(_disk.Files);
    }

    [Fact]
    public async Task HalfWrittenXlsx_NotUploaded()
    {
        string p = Path.Combine(_dir, "Б.xlsx");
        File.WriteAllBytes(p, new byte[] { 0, 0, 0, 0, 1 });
        _settings.FilePath = p;
        var r = await Engine().RunOnceAsync(default);
        Assert.Equal(SyncState.Retrying, r.State);
        Assert.Empty(_disk.Files);
    }

    [Fact]
    public async Task NonZipFormats_NotChecked()
    {
        string p = Path.Combine(_dir, "Старый.xls");
        File.WriteAllBytes(p, new byte[] { 0xD0, 0xCF, 0x11, 0xE0 });
        _settings.FilePath = p;
        Assert.Equal(SyncState.Synced, (await Engine().RunOnceAsync(default)).State);
    }

    [Fact]
    public async Task Loop_CoalescesRequests_AndPublishes()
    {
        Book();
        var states = new List<SyncState>();
        var done = new TaskCompletionSource();
        using var e = Engine();
        e.StatusChanged += s => { lock (states) states.Add(s.State); if (s.State == SyncState.Synced) done.TrySetResult(); };
        e.Start();
        e.Request(); e.Request(); e.Request();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Assert.Equal(2, _disk.Log.Count(l => l.StartsWith("upload")));   // одна загрузка: основной файл + копия
        lock (states) Assert.Contains(SyncState.Uploading, states);
    }

    [Fact]
    public async Task Loop_TransientError_RetriesItself()
    {
        Book();
        _disk.FailUploads.Enqueue(new DiskException("сеть", DiskErrorKind.Transient));
        var synced = new TaskCompletionSource();
        SyncStatus? retry = null;
        using var e = Engine();
        e.StatusChanged += s =>
        {
            if (s.State == SyncState.Retrying) retry = s;
            if (s.State == SyncState.Synced) synced.TrySetResult();
        };
        e.Start();
        e.Request();
        await synced.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(retry);
        Assert.Equal(_now + SyncEngine.Backoff[0], retry!.NextRetry);
    }
}
