using System.Net;
using System.Text;
using Xunit;

namespace YaDiskFileSync.Tests;

public class DiskApiTests
{
    private const string Href = """{"href":"https://uploader1.disk.yandex.net/upload-target/123","method":"PUT","templated":false}""";

    private static DiskClient Client(FakeHttp http, string token = "T") => new(http.Client(), new FixedToken(token));

    private static Task Upload(DiskClient c, string path, byte[] data) =>
        c.UploadAsync(path, new MemoryStream(data), data.Length, null, default);

    [Fact]
    public async Task Upload_GetsLink_ThenPutsWithoutToken_WithLength()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, Href).Reply(HttpStatusCode.Created);
        long progress = 0;
        // как в движке: поток без перемотки (HashingStream) — длину HttpClient сам не узнает
        using var data = new HashingStream(new MemoryStream(new byte[] { 1, 2, 3 }), reading: true);
        await Client(http, "TOK").UploadAsync("disk:/Папка/Бух учёт.xlsx", data, 3, n => progress += n, default);

        Assert.Equal(2, http.Requests.Count);
        var get = http.Requests[0];
        Assert.Equal(HttpMethod.Get, get.Method);
        Assert.StartsWith("https://cloud-api.yandex.net/v1/disk/resources/upload?path=disk%3A%2F%D0%9F", get.Url);
        Assert.Contains("overwrite=true", get.Url);
        Assert.Equal("OAuth TOK", get.Auth);

        var put = http.Requests[1];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("https://uploader1.disk.yandex.net/upload-target/123", put.Url);
        Assert.Null(put.Auth);
        Assert.Equal(3, http.ContentLengths[1]);   // не chunked: Яндекс ждёт длину
        Assert.Equal(3, progress);
    }

    [Fact]
    public async Task Upload_NonHttpsHref_Rejected()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"href":"http://evil/x"}""");
        var e = await Assert.ThrowsAsync<DiskException>(() => Upload(Client(http), "disk:/a.xlsx", new byte[1]));
        Assert.Single(http.Requests);
        Assert.Equal(DiskErrorKind.Transient, e.Kind);
    }

    [Fact]
    public async Task Unauthorized_RetriesOnceWithFreshToken()
    {
        var tokens = new FixedToken("T");
        var http = new FakeHttp().Reply(HttpStatusCode.Unauthorized, """{"error":"UnauthorizedError"}""")
            .Reply(HttpStatusCode.OK, Href).Reply(HttpStatusCode.Created);
        await new DiskClient(http.Client(), tokens).UploadAsync("disk:/a.xlsx", new MemoryStream(new byte[1]), 1, null, default);
        Assert.Equal(1, tokens.Forced);
        Assert.Equal("OAuth T1", http.Requests[1].Auth);
    }

    [Fact]
    public async Task Unauthorized_Twice_IsAuthError()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.Unauthorized).Reply(HttpStatusCode.Unauthorized);
        var e = await Assert.ThrowsAsync<DiskException>(() => Upload(Client(http), "disk:/a.xlsx", new byte[1]));
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
        var http = new FakeHttp().Reply(HttpStatusCode.OK, Href).Reply(code);
        var e = await Assert.ThrowsAsync<DiskException>(() => Upload(Client(http), "disk:/a.xlsx", new byte[1]));
        Assert.Equal((DiskErrorKind)kindN, e.Kind);
    }

    [Fact]
    public void Forbidden_ExplainsMissingRights() =>
        Assert.Contains("права на весь Диск", DiskClient.Map(HttpStatusCode.Forbidden, null).Message);

    [Fact]
    public async Task Network_Down_IsTransient()
    {
        var e = await Assert.ThrowsAsync<DiskException>(() => Upload(Client(new FakeHttp().Fail()), "disk:/a.xlsx", new byte[1]));
        Assert.Equal(DiskErrorKind.Transient, e.Kind);
    }

    [Fact]
    public async Task Download_FollowsLink_WritesBytes()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"href":"https://downloader.disk.yandex.ru/disk/abc"}""")
            .ReplyBytes(HttpStatusCode.OK, Encoding.UTF8.GetBytes("содержимое"));
        var ms = new MemoryStream();
        long progress = 0;
        await Client(http).DownloadAsync("disk:/a.txt", ms, n => progress += n, default);
        Assert.Equal("содержимое", Encoding.UTF8.GetString(ms.ToArray()));
        Assert.Contains("/resources/download?path=disk%3A%2Fa.txt", http.Requests[0].Url);
        Assert.Null(http.Requests[1].Auth);
        Assert.Equal(ms.Length, progress);
    }

    [Fact]
    public async Task EnsureFolder_ExistingIsFine()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.Conflict, """{"error":"DiskPathPointsToExistentDirectoryError"}""");
        await Client(http).EnsureFolderAsync("disk:/YaDiskSync", default);
        Assert.Equal(HttpMethod.Put, http.Requests[0].Method);
    }

    [Fact]
    public async Task EnsureFolder_CreatesMissingParents()
    {
        const string NoParent = """{"error":"DiskPathDoesntExistsError"}""";
        var http = new FakeHttp()
            .Reply(HttpStatusCode.Conflict, NoParent)   // a/b/c — нет a/b
            .Reply(HttpStatusCode.Conflict, NoParent)   // a/b — нет a
            .Reply(HttpStatusCode.Created)              // a
            .Reply(HttpStatusCode.Created)              // a/b
            .Reply(HttpStatusCode.Created);             // a/b/c
        await Client(http).EnsureFolderAsync("disk:/a/b/c", default);
        Assert.Equal(5, http.Requests.Count);
        Assert.EndsWith("path=disk%3A%2Fa", http.Requests[2].Url);
        Assert.EndsWith("path=disk%3A%2Fa%2Fb%2Fc", http.Requests[4].Url);
    }

    [Fact]
    public async Task Delete_ToTrash_AndMissingIsFine()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.NotFound);
        await Client(http).DeleteAsync("disk:/YaDiskSync/x.xlsx", default);
        Assert.Contains("permanently=false", http.Requests[0].Url);
        Assert.Equal(HttpMethod.Delete, http.Requests[0].Method);
    }

    [Fact]
    public async Task ListTree_Recurses_Pages_ParsesMeta()
    {
        string Item(string name, string type, string? sha = null) => type == "dir"
            ? $$"""{"name":"{{name}}","type":"dir","modified":"2026-10-22T10:00:00+03:00"}"""
            : $$"""{"name":"{{name}}","type":"file","size":5,"sha256":"{{sha ?? "ab"}}","modified":"2026-10-22T10:00:00+03:00"}""";
        string Page(params string[] items) => "{\"type\":\"dir\",\"_embedded\":{\"items\":[" + string.Join(",", items) + "]}}";
        var http = new FakeHttp()
            .Reply(HttpStatusCode.OK, Page(Item("Отчёты", "dir"), Item("a.xlsx", "file", "abcdef")))
            .Reply(HttpStatusCode.OK, Page(Item("q.txt", "file")));
        var list = (await Client(http).ListTreeAsync("disk:/YaDiskSync", default))!;
        Assert.Equal(3, list.Count);
        var a = Assert.Single(list, x => x.Rel == "a.xlsx");
        Assert.Equal("ABCDEF", a.Sha256);
        Assert.Equal(5, a.Size);
        Assert.Equal(new DateTime(2026, 10, 22, 7, 0, 0, DateTimeKind.Utc), a.Modified);
        Assert.Contains(list, x => x.Rel == "Отчёты" && x.IsDir);
        Assert.Contains(list, x => x.Rel == "Отчёты/q.txt");
        Assert.Contains("path=disk%3A%2FYaDiskSync%2F%D0%9E", http.Requests[1].Url);
    }

    [Fact]
    public async Task ListTree_MissingRoot_IsNull()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.NotFound, """{"error":"DiskNotFoundError"}""");
        Assert.Null(await Client(http).ListTreeAsync("disk:/Нет", default));
    }

    [Fact]
    public async Task ListTree_RootIsFile_Fatal()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"type":"file","name":"x"}""");
        var e = await Assert.ThrowsAsync<DiskException>(() => Client(http).ListTreeAsync("disk:/x", default));
        Assert.Equal(DiskErrorKind.Fatal, e.Kind);
    }

    [Fact]
    public async Task ListTree_ListingError_Throws_NotEmpty()
    {
        // ошибка обхода не должна выглядеть как «на Диске пусто» — иначе всё локальное сочтётся удалённым там
        var http = new FakeHttp().Reply(HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<DiskException>(() => Client(http).ListTreeAsync("disk:/YaDiskSync", default));
    }

    [Fact]
    public async Task GetUser_ParsesUidAndLogin()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"user":{"uid":"123","login":"mike","display_name":"Mike"}}""");
        var u = await Client(http).GetUserAsync(default);
        Assert.Equal(new DiskUser("123", "mike"), u);
    }

    [Theory]
    [InlineData("/YaDiskSync", "disk:/YaDiskSync")]
    [InlineData("YaDiskSync/", "disk:/YaDiskSync")]
    [InlineData("disk:/A//B/", "disk:/A/B")]
    [InlineData(" \\Документы\\Бух ", "disk:/Документы/Бух")]
    [InlineData("/", "disk:/")]
    [InlineData("", "disk:/")]
    public void DiskPath_Normalize(string input, string expected) => Assert.Equal(expected, DiskPath.Normalize(input));

    [Fact]
    public void DiskPath_Helpers()
    {
        Assert.Equal("disk:/A/b.txt", DiskPath.Combine("disk:/A", "b.txt"));
        Assert.Equal("disk:/A/ b.txt ", DiskPath.Combine("disk:/A", " b.txt "));   // имена файлов — как есть
        Assert.Equal("disk:/A", DiskPath.Parent("disk:/A/B"));
        Assert.Equal("disk:/", DiskPath.Parent("disk:/A"));
        Assert.Null(DiskPath.Parent("disk:/"));
        Assert.True(DiskPath.IsRoot("/"));
        Assert.Equal("/A/B", DiskPath.Display("disk:/A/B"));
        Assert.Equal("https://disk.yandex.ru/client/disk/%D0%91%D1%83%D1%85%20%D1%83%D1%87%D1%91%D1%82", DiskPath.WebUrl("/Бух учёт"));
    }
}
