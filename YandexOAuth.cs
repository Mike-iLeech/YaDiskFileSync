using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace YaDiskFileSync;

internal sealed record DeviceCode(string Code, string UserCode, string VerificationUrl, TimeSpan Interval, DateTime ExpiresAt);

internal sealed record TokenSet(string AccessToken, string? RefreshToken, DateTime ExpiresAt);

internal sealed class OAuthException(string message, string? error = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Код ошибки Яндекс OAuth (invalid_grant, invalid_client…), если сервер его вернул.</summary>
    public string? Error { get; } = error;
}

/// <summary>
/// Вход по коду подтверждения (device code): программа показывает короткий код, пользователь вводит его
/// на oauth.yandex.ru/device и разрешает доступ. Пароль от Яндекса программа не видит.
/// </summary>
internal sealed class YandexOAuth(HttpClient http, string clientId, string? clientSecret)
{
    public const string Host = "https://oauth.yandex.ru";

    public async Task<DeviceCode> RequestDeviceCodeAsync(string deviceName, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string> { ["client_id"] = clientId, ["device_name"] = deviceName };
        using var doc = await PostAsync("/device/code", form, ct);
        var r = doc.RootElement;
        string code = Str(r, "device_code") ?? throw new OAuthException("Яндекс не выдал код подтверждения.");
        string user = Str(r, "user_code") ?? throw new OAuthException("Яндекс не выдал код подтверждения.");
        string url = Str(r, "verification_url") ?? Host + "/device";
        int interval = Int(r, "interval") ?? 5;
        int expires = Int(r, "expires_in") ?? 300;
        return new DeviceCode(code, user, url, TimeSpan.FromSeconds(Math.Max(1, interval)), DateTime.UtcNow.AddSeconds(expires));
    }

    /// <summary>Опрашивает Яндекс, пока пользователь не подтвердит код (или код не истечёт).</summary>
    public async Task<TokenSet> WaitForTokenAsync(DeviceCode dc, CancellationToken ct = default, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > dc.ExpiresAt)
                throw new OAuthException("Код подтверждения истёк. Нажмите «Войти» ещё раз.", "expired");
            await delay(dc.Interval, ct);
            try
            {
                return await TokenAsync(new() { ["grant_type"] = "device_code", ["code"] = dc.Code }, ct);
            }
            catch (OAuthException e) when (e.Error is "authorization_pending" or "slow_down")
            {
                // пользователь ещё не ввёл код
            }
        }
    }

    public Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, ct);

    private async Task<TokenSet> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var doc = await PostAsync("/token", form, ct, basicAuth: true);
        return ParseToken(doc.RootElement);
    }

    internal static TokenSet ParseToken(JsonElement r)
    {
        string access = Str(r, "access_token") ?? throw new OAuthException("Яндекс не выдал токен.");
        int expires = Int(r, "expires_in") ?? 365 * 24 * 3600;
        return new TokenSet(access, Str(r, "refresh_token"), DateTime.UtcNow.AddSeconds(expires));
    }

    private async Task<JsonDocument> PostAsync(string path, Dictionary<string, string> form, CancellationToken ct, bool basicAuth = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Host + path) { Content = new FormUrlEncodedContent(form) };
        if (basicAuth)
        {
            // ClientID и секрет — заголовком Basic; без секрета передаём ClientID в теле
            if (!string.IsNullOrEmpty(clientSecret))
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
            else
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>(form) { ["client_id"] = clientId });
        }

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new OAuthException("Нет связи с Яндексом.", "network", e); }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException e)
            { throw new OAuthException($"Яндекс ответил непонятно (HTTP {(int)resp.StatusCode}).", "bad_response", e); }

            if (resp.IsSuccessStatusCode) return doc;
            using (doc)
            {
                string? err = Str(doc.RootElement, "error");
                string? desc = Str(doc.RootElement, "error_description");
                throw new OAuthException(Explain(err, desc, (int)resp.StatusCode), err ?? $"http_{(int)resp.StatusCode}");
            }
        }
    }

    internal static string Explain(string? error, string? description, int status) => error switch
    {
        "invalid_client" => "Яндекс не узнал приложение: проверьте ClientID и секрет.",
        "unauthorized_client" => "Приложение на oauth.yandex.ru ещё не одобрено или заблокировано.",
        "invalid_grant" => "Вход устарел или отозван — войдите заново.",
        "invalid_scope" => "Права приложения изменились — войдите заново.",
        "authorization_pending" => "Ожидаем подтверждения кода.",
        _ => $"Ошибка входа в Яндекс: {description ?? error ?? "HTTP " + status}.",
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
}
