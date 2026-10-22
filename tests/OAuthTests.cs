using System.Net;
using System.Text;
using Xunit;

namespace YaDiskFileSync.Tests;

public class OAuthTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    private static DeviceCode Code(int seconds = 300) =>
        new("dc", "1234567", "https://oauth.yandex.ru/device", TimeSpan.FromSeconds(5), DateTime.UtcNow.AddSeconds(seconds));

    [Fact]
    public async Task DeviceCode_Parsed()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK,
            """{"device_code":"abc","user_code":"7654321","verification_url":"https://ya.ru/device","interval":7,"expires_in":300}""");
        var dc = await new YandexOAuth(http.Client(), "cid", "sec").RequestDeviceCodeAsync("ПК");
        Assert.Equal("abc", dc.Code);
        Assert.Equal("7654321", dc.UserCode);
        Assert.Equal(TimeSpan.FromSeconds(7), dc.Interval);
        Assert.Contains("client_id=cid", http.Requests[0].Body);
        Assert.Null(http.Requests[0].Auth);   // секрет в запрос кода не уходит
    }

    [Fact]
    public async Task Wait_PendingThenToken_SecretInBasicHeader()
    {
        var http = new FakeHttp()
            .Reply(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}""")
            .Reply(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}""")
            .Reply(HttpStatusCode.OK, """{"access_token":"A","refresh_token":"R","expires_in":31536000,"token_type":"bearer"}""");
        var t = await new YandexOAuth(http.Client(), "cid", "sec").WaitForTokenAsync(Code(), delay: NoDelay);
        Assert.Equal("A", t.AccessToken);
        Assert.Equal("R", t.RefreshToken);
        Assert.True(t.ExpiresAt > DateTime.UtcNow.AddDays(360));
        Assert.Equal(3, http.Requests.Count);
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("cid:sec"));
        Assert.All(http.Requests, r => Assert.Equal("Basic " + basic, r.Auth));
        Assert.Contains("grant_type=device_code", http.Requests[2].Body);
        Assert.DoesNotContain("sec", http.Requests[2].Body);
    }

    [Fact]
    public async Task Wait_NoSecret_ClientIdInBody()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"access_token":"A","expires_in":100}""");
        await new YandexOAuth(http.Client(), "cid", null).WaitForTokenAsync(Code(), delay: NoDelay);
        Assert.Null(http.Requests[0].Auth);
        Assert.Contains("client_id=cid", http.Requests[0].Body);
    }

    [Fact]
    public async Task Wait_Expired_Throws()
    {
        var http = new FakeHttp();
        var e = await Assert.ThrowsAsync<OAuthException>(() =>
            new YandexOAuth(http.Client(), "cid", "s").WaitForTokenAsync(Code(-1), delay: NoDelay));
        Assert.Equal("expired", e.Error);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Wait_InvalidClient_StopsWithMessage()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.BadRequest, """{"error":"invalid_client","error_description":"x"}""");
        var e = await Assert.ThrowsAsync<OAuthException>(() =>
            new YandexOAuth(http.Client(), "cid", "s").WaitForTokenAsync(Code(), delay: NoDelay));
        Assert.Equal("invalid_client", e.Error);
        Assert.Contains("ClientID", e.Message);
    }

    [Fact]
    public async Task Wait_Cancelled_Stops()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new YandexOAuth(new FakeHttp().Client(), "cid", "s").WaitForTokenAsync(Code(), cts.Token, NoDelay));
    }

    [Fact]
    public async Task Network_Error_IsOAuthException()
    {
        var e = await Assert.ThrowsAsync<OAuthException>(() =>
            new YandexOAuth(new FakeHttp().Fail().Client(), "cid", "s").RefreshAsync("R"));
        Assert.Equal("network", e.Error);
    }

    [Fact]
    public async Task NonJson_Response_IsOAuthException()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.BadGateway, "<html>");
        var e = await Assert.ThrowsAsync<OAuthException>(() => new YandexOAuth(http.Client(), "cid", "s").RefreshAsync("R"));
        Assert.Equal("bad_response", e.Error);
    }

    [Fact]
    public async Task Refresh_SendsRefreshToken()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"access_token":"A2","refresh_token":"R2","expires_in":100}""");
        var t = await new YandexOAuth(http.Client(), "cid", "s").RefreshAsync("R1");
        Assert.Equal("A2", t.AccessToken);
        Assert.Contains("grant_type=refresh_token", http.Requests[0].Body);
        Assert.Contains("refresh_token=R1", http.Requests[0].Body);
    }
}
