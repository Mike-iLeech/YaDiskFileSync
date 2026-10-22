using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace YaDiskFileSync;

internal enum DiskErrorKind
{
    /// <summary>Сеть, 429, 5xx, 423 — повторим позже сами.</summary>
    Transient,
    /// <summary>Токен не принят даже после обновления — нужен новый вход.</summary>
    Auth,
    /// <summary>Нет места, файл слишком большой и т.п. — повтор не поможет, нужен человек.</summary>
    Fatal,
}

internal sealed class DiskException(string message, DiskErrorKind kind, Exception? inner = null) : Exception(message, inner)
{
    public DiskErrorKind Kind { get; } = kind;
}

/// <summary>То, что движку синхронизации нужно от Яндекс.Диска (в тестах — подделка).</summary>
internal interface IDisk
{
    Task UploadAsync(string path, byte[] data, CancellationToken ct);
    Task EnsureFolderAsync(string path, CancellationToken ct);
    Task<IReadOnlyList<string>> ListFileNamesAsync(string folder, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}

/// <summary>Выдаёт действующий токен; force — текущий не приняли, нужно обновить.</summary>
internal interface ITokenSource
{
    Task<string> GetAsync(bool force, CancellationToken ct);
}

/// <summary>REST API Яндекс.Диска (cloud-api.yandex.net), только папка приложения app:/.</summary>
internal sealed class DiskClient(HttpClient http, ITokenSource tokens) : IDisk
{
    public const string Api = "https://cloud-api.yandex.net/v1/disk";

    public async Task UploadAsync(string path, byte[] data, CancellationToken ct)
    {
        string href;
        using (var doc = await ApiAsync(HttpMethod.Get, $"/resources/upload?path={Uri.EscapeDataString(path)}&overwrite=true", ct))
            href = doc?.RootElement.TryGetProperty("href", out var h) == true ? h.GetString() ?? "" : "";
        if (!Uri.TryCreate(href, UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttps)
            throw new DiskException("Яндекс.Диск не выдал ссылку для загрузки.", DiskErrorKind.Transient);

        // по ссылке загрузки токен не нужен и не передаётся
        using var put = new HttpRequestMessage(HttpMethod.Put, target) { Content = new ByteArrayContent(data) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await SendAsync(put, ct);
        if (resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.Accepted or HttpStatusCode.OK) return;
        throw Map(resp.StatusCode, null);
    }

    public async Task EnsureFolderAsync(string path, CancellationToken ct)
    {
        try { (await ApiAsync(HttpMethod.Put, $"/resources?path={Uri.EscapeDataString(path)}", ct))?.Dispose(); }
        catch (DiskException e) when (e.InnerException is HttpConflict) { /* папка уже есть */ }
    }

    public async Task<IReadOnlyList<string>> ListFileNamesAsync(string folder, CancellationToken ct)
    {
        var names = new List<string>();
        const int page = 200;
        for (int offset = 0; ; offset += page)
        {
            using var doc = await ApiAsync(HttpMethod.Get,
                $"/resources?path={Uri.EscapeDataString(folder)}&limit={page}&offset={offset}&fields=_embedded.items.name,_embedded.items.type,_embedded.total", ct);
            if (doc == null || !doc.RootElement.TryGetProperty("_embedded", out var emb)) return names;
            int got = 0;
            if (emb.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray())
                {
                    got++;
                    if (it.TryGetProperty("type", out var t) && t.GetString() == "file" && it.TryGetProperty("name", out var n) && n.GetString() is { } name)
                        names.Add(name);
                }
            if (got < page) return names;
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        // старые копии самой программы: в Корзину Диска не кладём, иначе она копит место
        try { (await ApiAsync(HttpMethod.Delete, $"/resources?path={Uri.EscapeDataString(path)}&permanently=true", ct))?.Dispose(); }
        catch (DiskException e) when (e.InnerException is HttpNotFound) { /* уже удалена */ }
    }

    /// <summary>Запрос к API с токеном; 401 — один раз обновляем токен и повторяем. null — пустой ответ.</summary>
    private async Task<JsonDocument?> ApiAsync(HttpMethod method, string relative, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            string token = await tokens.GetAsync(force: attempt > 0, ct);
            using var req = new HttpRequestMessage(method, Api + relative);
            req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;

            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw Map(resp.StatusCode, body);
            if (string.IsNullOrWhiteSpace(body)) return null;
            try { return JsonDocument.Parse(body); }
            catch (JsonException e) { throw new DiskException("Яндекс.Диск ответил непонятно.", DiskErrorKind.Transient, e); }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try { return await http.SendAsync(req, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new DiskException("Нет связи с Яндекс.Диском.", DiskErrorKind.Transient, e); }
    }

    internal static DiskException Map(HttpStatusCode status, string? body)
    {
        string? apiError = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e)) apiError = e.GetString();
            }
        }
        catch (JsonException) { }

        return (int)status switch
        {
            401 => new DiskException("Яндекс.Диск не принял вход — войдите заново.", DiskErrorKind.Auth),
            403 => new DiskException("Доступ запрещён: возможно, место на Диске кончилось или приложению не хватает прав.", DiskErrorKind.Fatal),
            404 => new DiskException("Папка на Диске не найдена.", DiskErrorKind.Transient, new HttpNotFound()),
            409 => new DiskException("Ресурс уже существует.", DiskErrorKind.Transient, new HttpConflict()),
            413 => new DiskException("Файл слишком большой для вашего тарифа Яндекс.Диска.", DiskErrorKind.Fatal),
            423 => new DiskException("Яндекс.Диск временно не принимает файлы.", DiskErrorKind.Transient),
            429 => new DiskException("Слишком много запросов — повторим позже.", DiskErrorKind.Transient),
            507 => new DiskException("На Яндекс.Диске закончилось место.", DiskErrorKind.Fatal),
            >= 500 => new DiskException($"Яндекс.Диск временно недоступен (HTTP {(int)status}).", DiskErrorKind.Transient),
            _ => new DiskException($"Яндекс.Диск ответил ошибкой {(int)status}{(apiError != null ? " (" + apiError + ")" : "")}.", DiskErrorKind.Fatal),
        };
    }

    private sealed class HttpConflict : Exception;
    private sealed class HttpNotFound : Exception;
}

/// <summary>Токены из <see cref="Secrets"/> с обновлением по refresh_token.</summary>
internal sealed class TokenSource(Secrets secrets, Func<YandexOAuth?> oauth, Action save) : ITokenSource
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string> GetAsync(bool force, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!secrets.SignedIn)
                throw new DiskException("Не выполнен вход в Яндекс.", DiskErrorKind.Auth);
            // токен живёт около года; обновляем заранее, за 30 дней до конца
            bool stale = secrets.ExpiresAt is { } exp && exp - DateTime.UtcNow < TimeSpan.FromDays(30);
            if ((force || stale) && !string.IsNullOrEmpty(secrets.RefreshToken) && oauth() is { } o)
            {
                try
                {
                    var t = await o.RefreshAsync(secrets.RefreshToken, ct);
                    secrets.AccessToken = t.AccessToken;
                    secrets.RefreshToken = t.RefreshToken ?? secrets.RefreshToken;
                    secrets.ExpiresAt = t.ExpiresAt;
                    save();
                }
                catch (OAuthException e) when (e.Error is "invalid_grant" or "invalid_client" or "unauthorized_client")
                {
                    throw new DiskException(e.Message, DiskErrorKind.Auth, e);
                }
                catch (OAuthException e) when (!force)
                {
                    // заблаговременное обновление не вышло (нет сети) — текущий токен ещё рабочий
                    _ = e;
                }
                catch (OAuthException e)
                {
                    throw new DiskException(e.Message, DiskErrorKind.Transient, e);
                }
            }
            else if (force)
            {
                throw new DiskException("Яндекс.Диск не принял вход — войдите заново.", DiskErrorKind.Auth);
            }
            return secrets.AccessToken!;
        }
        finally { _lock.Release(); }
    }
}
