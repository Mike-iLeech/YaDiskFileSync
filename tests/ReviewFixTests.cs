using System.IO.Compression;
using System.Net;
using Xunit;

namespace YaDiskFileSync.Tests;

/// <summary>Сценарии из независимых ревью (1.0.0) — проверка целостности книги, гонки входа и выхода.</summary>
public class ReviewFixTests
{
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
    public void CompleteZip_Accepted() => Assert.True(LocalScan.IsCompleteBook(Zip()));

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.95)]
    public void TruncatedZip_StartsWithPK_ButRejected(double keep)
    {
        byte[] full = Zip();
        byte[] cut = full[..(int)(full.Length * keep)];
        Assert.Equal((byte)'P', cut[0]);
        Assert.Equal((byte)'K', cut[1]);
        Assert.False(LocalScan.IsCompleteBook(cut));
    }

    /// <summary>Заголовок OLE/CFB, как у книги, зашифрованной паролем (EncryptedPackage).</summary>
    private static byte[] Cfb(int length)
    {
        var b = new byte[length];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(b, 0);
        return b;
    }

    [Fact]
    public void PasswordProtectedBook_Accepted() => Assert.True(LocalScan.IsCompleteBook(Cfb(512 * 40)));

    [Theory]
    [InlineData(512 * 40 - 100)]   // оборван посреди сектора
    [InlineData(512)]              // один заголовок без данных
    public void PasswordProtectedBook_Truncated_Rejected(int length) => Assert.False(LocalScan.IsCompleteBook(Cfb(length)));

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
    public async Task EnsureFolder_OtherConflict_Throws()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.Conflict, """{"error":"SomeOtherError"}""");
        var e = await Assert.ThrowsAsync<DiskException>(() =>
            new DiskClient(http.Client(), new FixedToken()).EnsureFolderAsync("disk:/a", default));
        Assert.Equal(DiskErrorKind.Transient, e.Kind);
    }
}
