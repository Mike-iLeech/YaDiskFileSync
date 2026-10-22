using System.Net;
using Xunit;

namespace YaDiskFileSync.Tests;

public class DiskClientTests
{
    private const string Href = """{"href":"https://uploader1.disk.yandex.net/upload-target/123","method":"PUT","templated":false}""";

    [Fact]
    public async Task Upload_GetsLink_ThenPutsWithoutToken()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, Href).Reply(HttpStatusCode.Created);
        await new DiskClient(http.Client(), new FixedToken("TOK")).UploadAsync("app:/Бух учёт.xlsx", new byte[] { 1, 2, 3 }, default);

        Assert.Equal(2, http.Requests.Count);
        var get = http.Requests[0];
        Assert.Equal(HttpMethod.Get, get.Method);
        Assert.StartsWith("https://cloud-api.yandex.net/v1/disk/resources/upload?path=app%3A%2F", get.Url);
        Assert.Contains("overwrite=true", get.Url);
        Assert.Equal("OAuth TOK", get.Auth);

        var put = http.Requests[1];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("https://uploader1.disk.yandex.net/upload-target/123", put.Url);
        Assert.Null(put.Auth);
    }

    [Fact]
    public async Task Upload_Accepted202_IsOk()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, Href).Reply(HttpStatusCode.Accepted);
        await new DiskClient(http.Client(), new FixedToken()).UploadAsync("app:/a.xlsx", new byte[1], default);
    }

    [Fact]
    public async Task Upload_NonHttpsHref_Rejected()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"href":"http://evil/x"}""");
        var e = await Assert.ThrowsAsync<DiskException>(() =>
            new DiskClient(http.Client(), new FixedToken()).UploadAsync("app:/a.xlsx", new byte[1], default));
        Assert.Single(http.Requests);
        Assert.Equal(DiskErrorKind.Transient, e.Kind);
    }

    [Fact]
    public async Task Unauthorized_RetriesOnceWithFreshToken()
    {
        var tokens = new FixedToken("T");
        var http = new FakeHttp().Reply(HttpStatusCode.Unauthorized, """{"error":"UnauthorizedError"}""")
            .Reply(HttpStatusCode.OK, Href).Reply(HttpStatusCode.Created);
        await new DiskClient(http.Client(), tokens).UploadAsync("app:/a.xlsx", new byte[1], default);
        Assert.Equal(1, tokens.Forced);
        Assert.Equal("OAuth T1", http.Requests[1].Auth);
    }

    [Fact]
    public async Task Unauthorized_Twice_IsAuthError()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.Unauthorized).Reply(HttpStatusCode.Unauthorized);
        var e = await Assert.ThrowsAsync<DiskException>(() =>
            new DiskClient(http.Client(), new FixedToken()).UploadAsync("app:/a.xlsx", new byte[1], default));
        Assert.Equal(DiskErrorKind.Auth, e.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.InsufficientStorage, (int)DiskErrorKind.Fatal)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, (int)DiskErrorKind.Fatal)]
    [InlineData(HttpStatusCode.Forbidden, (int)DiskErrorKind.Fatal)]
    [InlineData(HttpStatusCode.ServiceUnavailable, (int)DiskErrorKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, (int)DiskErrorKind.Transient)]
    [InlineData(HttpStatusCode.Locked, (int)DiskErrorKind.Transient)]
    public async Task Put_Errors_Mapped(HttpStatusCode code, int kindN)
    {
        var kind = (DiskErrorKind)kindN;
        var http = new FakeHttp().Reply(HttpStatusCode.OK, Href).Reply(code);
        var e = await Assert.ThrowsAsync<DiskException>(() =>
            new DiskClient(http.Client(), new FixedToken()).UploadAsync("app:/a.xlsx", new byte[1], default));
        Assert.Equal(kind, e.Kind);
    }

    [Fact]
    public async Task Network_Down_IsTransient()
    {
        var e = await Assert.ThrowsAsync<DiskException>(() =>
            new DiskClient(new FakeHttp().Fail().Client(), new FixedToken()).UploadAsync("app:/a.xlsx", new byte[1], default));
        Assert.Equal(DiskErrorKind.Transient, e.Kind);
    }

    [Fact]
    public async Task EnsureFolder_ExistingIsFine()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.Conflict, """{"error":"DiskPathPointsToExistentDirectoryError"}""");
        await new DiskClient(http.Client(), new FixedToken()).EnsureFolderAsync("app:/История", default);
        Assert.Equal(HttpMethod.Put, http.Requests[0].Method);
    }

    [Fact]
    public async Task List_PagesAndSkipsFolders()
    {
        string Page(int from, int count) =>
            "{\"_embedded\":{\"items\":[" + string.Join(",", Enumerable.Range(from, count).Select(i =>
                i == from ? $"{{\"name\":\"dir{i}\",\"type\":\"dir\"}}" : $"{{\"name\":\"f{i}\",\"type\":\"file\"}}")) + "]}}";
        var http = new FakeHttp().Reply(HttpStatusCode.OK, Page(0, 200)).Reply(HttpStatusCode.OK, Page(200, 3));
        var names = await new DiskClient(http.Client(), new FixedToken()).ListFileNamesAsync("app:/История", default);
        Assert.Equal(199 + 2, names.Count);
        Assert.Contains("offset=200", http.Requests[1].Url);
    }

    [Fact]
    public async Task List_MissingFolder_IsTransientNotFound()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.NotFound, """{"error":"DiskNotFoundError"}""");
        var e = await Assert.ThrowsAsync<DiskException>(() =>
            new DiskClient(http.Client(), new FixedToken()).ListFileNamesAsync("app:/История", default));
        Assert.Equal(DiskErrorKind.Transient, e.Kind);
    }

    [Fact]
    public async Task Delete_Permanent_AndMissingIsFine()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.NotFound);
        await new DiskClient(http.Client(), new FixedToken()).DeleteAsync("app:/История/x.xlsx", default);
        Assert.Contains("permanently=true", http.Requests[0].Url);
        Assert.Equal(HttpMethod.Delete, http.Requests[0].Method);
    }
}

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
