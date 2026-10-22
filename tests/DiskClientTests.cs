using System.Net;
using Xunit;

namespace YaDiskFileSync.Tests;

public class TokenSourceTests
{
    private static YandexOAuth OAuth(FakeHttp http) => new(http.Client(), "cid", "s");

    [Fact]
    public async Task NotSignedIn_IsAuth()
    {
        var src = new TokenSource(new Secrets(), () => null, () => { });
        var e = await Assert.ThrowsAsync<DiskException>(() => src.GetAsync(false, default));
        Assert.Equal(DiskErrorKind.Auth, e.Kind);
    }

    [Fact]
    public async Task Fresh_ReturnsWithoutRefresh()
    {
        var http = new FakeHttp();
        var s = new Secrets { AccessToken = "A", RefreshToken = "R", ExpiresAt = DateTime.UtcNow.AddDays(200) };
        Assert.Equal("A", await new TokenSource(s, () => OAuth(http), () => { }).GetAsync(false, default));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Force_Refreshes_AndSaves()
    {
        int saved = 0;
        var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"access_token":"A2","refresh_token":"R2","expires_in":31536000}""");
        var s = new Secrets { AccessToken = "A", RefreshToken = "R", ExpiresAt = DateTime.UtcNow.AddDays(200) };
        Assert.Equal("A2", await new TokenSource(s, () => OAuth(http), () => saved++).GetAsync(true, default));
        Assert.Equal("R2", s.RefreshToken);
        Assert.Equal(1, saved);
    }

    [Fact]
    public async Task NearExpiry_RefreshFailsOffline_KeepsOldToken()
    {
        var s = new Secrets { AccessToken = "A", RefreshToken = "R", ExpiresAt = DateTime.UtcNow.AddDays(3) };
        Assert.Equal("A", await new TokenSource(s, () => OAuth(new FakeHttp().Fail()), () => { }).GetAsync(false, default));
    }

    [Fact]
    public async Task Force_RefreshRevoked_IsAuth()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var s = new Secrets { AccessToken = "A", RefreshToken = "R", ExpiresAt = DateTime.UtcNow.AddDays(200) };
        var e = await Assert.ThrowsAsync<DiskException>(() => new TokenSource(s, () => OAuth(http), () => { }).GetAsync(true, default));
        Assert.Equal(DiskErrorKind.Auth, e.Kind);
    }

    [Fact]
    public async Task Force_Offline_IsTransient()
    {
        var s = new Secrets { AccessToken = "A", RefreshToken = "R", ExpiresAt = DateTime.UtcNow.AddDays(200) };
        var e = await Assert.ThrowsAsync<DiskException>(() =>
            new TokenSource(s, () => OAuth(new FakeHttp().Fail()), () => { }).GetAsync(true, default));
        Assert.Equal(DiskErrorKind.Transient, e.Kind);
    }

    [Fact]
    public async Task Force_NoRefreshToken_IsAuth()
    {
        var s = new Secrets { AccessToken = "A" };
        var e = await Assert.ThrowsAsync<DiskException>(() => new TokenSource(s, () => OAuth(new FakeHttp()), () => { }).GetAsync(true, default));
        Assert.Equal(DiskErrorKind.Auth, e.Kind);
    }
}
