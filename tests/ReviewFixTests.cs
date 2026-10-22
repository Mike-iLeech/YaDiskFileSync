using System.IO.Compression;
using System.Net;
using Xunit;

namespace YaDiskFileSync.Tests;

/// <summary>Сценарии из независимого ревью 1.0.0.</summary>
public class ReviewFixTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ydfs-fix-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] Zip(int entries = 3)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i < entries; i++)
            {
                using var w = new StreamWriter(z.CreateEntry($"xl/sheet{i}.xml").Open());
                w.Write(new string('x', 5000) + i);
            }
        return ms.ToArray();
    }

    [Fact]
    public void CompleteZip_Accepted() => Assert.True(SyncEngine.IsCompleteZip(Zip()));

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.95)]
    public void TruncatedZip_StartsWithPK_ButRejected(double keep)
    {
        byte[] full = Zip();
        byte[] cut = full[..(int)(full.Length * keep)];
        Assert.Equal((byte)'P', cut[0]);
        Assert.Equal((byte)'K', cut[1]);
        Assert.False(SyncEngine.IsCompleteZip(cut));
    }

    [Fact]
    public async Task TruncatedXlsx_NotUploaded()
    {
        byte[] full = Zip();
        string p = Path.Combine(_dir, "Б.xlsx");
        File.WriteAllBytes(p, full[..(full.Length - 30)]);
        var disk = new FakeDisk();
        var settings = new Settings { FilePath = p };
        var e = new SyncEngine(() => disk, settings, () => { }, null, (_, _) => Task.CompletedTask) { ReadAttempts = 2, ReadRetryPause = TimeSpan.Zero };
        Assert.Equal(SyncState.Retrying, (await e.RunOnceAsync(default)).State);
        Assert.Empty(disk.Files);
    }

    private sealed class ThrowingDisk(FakeDisk inner) : IDisk
    {
        public int Calls;
        public Task UploadAsync(string path, byte[] data, CancellationToken ct) =>
            Interlocked.Increment(ref Calls) == 1 ? throw new InvalidOperationException("странный ответ") : inner.UploadAsync(path, data, ct);
        public Task EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);
        public Task<IReadOnlyList<string>> ListFileNamesAsync(string folder, CancellationToken ct) => inner.ListFileNamesAsync(folder, ct);
        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);
    }

    [Fact]
    public async Task Loop_SurvivesUnexpectedException()
    {
        string p = Path.Combine(_dir, "Б.xlsx");
        File.WriteAllBytes(p, Zip());
        var disk = new ThrowingDisk(new FakeDisk());
        var synced = new TaskCompletionSource();
        SyncStatus? failed = null;
        using var e = new SyncEngine(() => disk, new Settings { FilePath = p }, () => { }, null, (_, _) => Task.CompletedTask);
        e.StatusChanged += s =>
        {
            if (s.State == SyncState.Retrying) failed = s;
            if (s.State == SyncState.Synced) synced.TrySetResult();
        };
        e.Start();
        e.Request();
        await synced.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("странный ответ", failed!.Message);
    }

    [Fact]
    public async Task Refresh_DuringLogout_DoesNotResurrectTokens()
    {
        var s = new Secrets { AccessToken = "A", RefreshToken = "R", ExpiresAt = DateTime.UtcNow.AddDays(200) };
        int saved = 0;
        var http = new FakeHttp().Then(_ =>
        {
            // пока Яндекс отвечал, пользователь нажал «Выйти»
            lock (s.Sync) { s.Generation++; s.ClearTokens(); }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"access_token":"A2","refresh_token":"R2","expires_in":100}""") };
        });
        var src = new TokenSource(s, () => new YandexOAuth(http.Client(), "cid", "s"), () => saved++);
        await Assert.ThrowsAsync<DiskException>(() => src.GetAsync(true, default));
        Assert.False(s.SignedIn);
        Assert.Null(s.RefreshToken);
        Assert.Equal(0, saved);
    }

    [Fact]
    public void Generation_NotSaved()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new Secrets { Generation = 5, AccessToken = "A" });
        Assert.DoesNotContain("Generation", json);
        Assert.DoesNotContain("Sync", json);
    }

    [Fact]
    public void FileWatch_KickAfterDispose_NoThrow()
    {
        string p = Path.Combine(_dir, "a.xlsx");
        File.WriteAllText(p, "x");
        var w = new FileWatch(p, () => { });
        w.Dispose();
        w.Kick();
        w.Dispose();
    }

    [Fact]
    public async Task EnsureFolder_OtherConflict_Throws()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.Conflict, """{"error":"DiskPathDoesntExistsError"}""");
        var e = await Assert.ThrowsAsync<DiskException>(() =>
            new DiskClient(http.Client(), new FixedToken()).EnsureFolderAsync("app:/История", default));
        Assert.Equal("Папка на Диске не найдена.", e.Message);
    }
}
