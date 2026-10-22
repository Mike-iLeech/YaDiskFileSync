using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace YaDiskFileSync;

internal enum DiskErrorKind
{
    /// <summary>Сеть, 429, 5xx, 423 — повторим позже сами.</summary>
    Transient,
    /// <summary>Токен не принят даже после обновления — нужен новый вход.</summary>
    Auth,
    /// <summary>Нет места, файл слишком большой, нет прав — повтор не поможет, нужен человек.</summary>
    Fatal,
}

internal sealed class DiskException(string message, DiskErrorKind kind, Exception? inner = null) : Exception(message, inner)
{
    public DiskErrorKind Kind { get; } = kind;
    /// <summary>Ресурса нет (404).</summary>
    public bool NotFound => InnerException is DiskClient.HttpNotFound;
}

/// <summary>Файл или папка на Диске; Rel — путь относительно корня синхронизации через «/».</summary>
internal sealed record RemoteItem(string Rel, bool IsDir, long Size, string? Sha256, DateTime? Modified);

internal sealed record DiskUser(string Uid, string Login);

/// <summary>То, что движку синхронизации нужно от Яндекс.Диска (в тестах — подделка).</summary>
internal interface IDisk
{
    Task<DiskUser?> GetUserAsync(CancellationToken ct);
    /// <summary>Всё дерево под root (без самого root); null — папки root нет.</summary>
    Task<IReadOnlyList<RemoteItem>?> ListTreeAsync(string root, CancellationToken ct);
    /// <summary>Создаёт папку вместе с недостающими родителями; существующая — не ошибка.</summary>
    Task EnsureFolderAsync(string path, CancellationToken ct);
    Task UploadAsync(string path, Stream data, long length, Action<long>? progress, CancellationToken ct);
    Task DownloadAsync(string path, Stream target, Action<long>? progress, CancellationToken ct);
    /// <summary>В Корзину Диска; уже удалённый — не ошибка.</summary>
    Task DeleteAsync(string path, CancellationToken ct);
}

/// <summary>Выдаёт действующий токен; force — текущий не приняли, нужно обновить.</summary>
internal interface ITokenSource
{
    Task<string> GetAsync(bool force, CancellationToken ct);
}

/// <summary>REST API Яндекс.Диска (cloud-api.yandex.net), полный доступ к Диску.</summary>
internal sealed class DiskClient(HttpClient http, ITokenSource tokens) : IDisk
{
    public const string Api = "https://cloud-api.yandex.net/v1/disk";
    /// <summary>Сколько ждать ответа на обычный запрос API.</summary>
    internal static TimeSpan ApiTimeout { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Передача файла считается зависшей, если столько времени не идёт ни байта.</summary>
    internal static TimeSpan StallTimeout { get; set; } = TimeSpan.FromMinutes(2);
    private const int Page = 1000;

    public async Task<DiskUser?> GetUserAsync(CancellationToken ct)
    {
        using var doc = await ApiAsync(HttpMethod.Get, "?fields=user", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("user", out var u)) return null;
        string? uid = Str(u, "uid");
        return uid == null ? null : new DiskUser(uid, Str(u, "login") ?? Str(u, "display_name") ?? "");
    }

    public async Task<IReadOnlyList<RemoteItem>?> ListTreeAsync(string root, CancellationToken ct)
    {
        root = DiskPath.Normalize(root);
        var result = new List<RemoteItem>();
        var queue = new Queue<string>();
        queue.Enqueue("");
        while (queue.Count > 0)
        {
            string rel = queue.Dequeue();
            string folder = rel.Length == 0 ? root : DiskPath.Combine(root, rel);
            // неполный список опаснее ошибки: всё, чего в нём нет, сочлось бы удалённым на Диске
            static DiskException Partial(string why) => new("Список файлов на Диске получен не полностью (" + why + ") — повторю.", DiskErrorKind.Transient);
            int seen = 0;
            for (int offset = 0; ; offset += Page)
            {
                JsonDocument? doc;
                try
                {
                    doc = await ApiAsync(HttpMethod.Get, $"/resources?path={Esc(folder)}&limit={Page}&offset={offset}&sort=name" +
                        "&fields=type,_embedded.total,_embedded.items.name,_embedded.items.type,_embedded.items.size," +
                        "_embedded.items.sha256,_embedded.items.modified", ct);
                }
                catch (DiskException e) when (e.NotFound)
                {
                    if (rel.Length == 0) return null;
                    throw Partial("папка изменилась во время обхода");
                }
                using (doc)
                {
                    if (doc == null) throw Partial("пустой ответ");
                    if (Str(doc.RootElement, "type") == "file")
                    {
                        if (rel.Length == 0) throw new DiskException($"На Диске «{root}» — файл, а не папка.", DiskErrorKind.Fatal);
                        throw Partial("папка стала файлом");
                    }
                    if (!doc.RootElement.TryGetProperty("_embedded", out var emb) ||
                        !emb.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) throw Partial("нет списка");
                    int got = 0;
                    foreach (var it in items.EnumerateArray())
                    {
                        got++;
                        if (Str(it, "name") is not { Length: > 0 } name) throw Partial("элемент без имени");
                        string childRel = rel.Length == 0 ? name : rel + "/" + name;
                        bool dir = Str(it, "type") == "dir";
                        DateTime? mod = it.TryGetProperty("modified", out var m) && m.ValueKind == JsonValueKind.String && m.TryGetDateTime(out var dt)
                            ? dt.ToUniversalTime() : null;
                        long size = it.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
                        result.Add(new RemoteItem(childRel, dir, dir ? 0 : size, dir ? null : Str(it, "sha256")?.ToUpperInvariant(), mod));
                        if (dir) queue.Enqueue(childRel);
                    }
                    seen += got;
                    if (got < Page)
                    {
                        // папка менялась между страницами — сдвиг offset мог пропустить элементы
                        if (emb.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() != seen)
                            throw Partial("папка изменилась во время обхода");
                        break;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Имена подпапок одного уровня — для окна выбора папки.</summary>
    public async Task<IReadOnlyList<string>> ListFoldersAsync(string path, CancellationToken ct)
    {
        var names = new List<string>();
        for (int offset = 0; ; offset += Page)
        {
            using var doc = await ApiAsync(HttpMethod.Get, $"/resources?path={Esc(path)}&limit={Page}&offset={offset}" +
                "&sort=name&fields=_embedded.items.name,_embedded.items.type", ct);
            if (doc == null || !doc.RootElement.TryGetProperty("_embedded", out var emb) ||
                !emb.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return names;
            int got = 0;
            foreach (var it in items.EnumerateArray())
            {
                got++;
                if (Str(it, "type") == "dir" && Str(it, "name") is { Length: > 0 } n) names.Add(n);
            }
            if (got < Page) return names;
        }
    }

    public async Task EnsureFolderAsync(string path, CancellationToken ct)
    {
        try { await MkdirAsync(path, ct); }
        catch (DiskException e) when (e.InnerException is HttpConflict { ApiError: "DiskPathDoesntExistsError" })
        {
            // нет родителя — создаём цепочку сверху вниз
            string? parent = DiskPath.Parent(path);
            if (parent == null) throw;
            await EnsureFolderAsync(parent, ct);
            await MkdirAsync(path, ct);
        }
    }

    private async Task MkdirAsync(string path, CancellationToken ct)
    {
        try { (await ApiAsync(HttpMethod.Put, $"/resources?path={Esc(path)}", ct))?.Dispose(); }
        catch (DiskException e) when (e.InnerException is HttpConflict { ApiError: "DiskPathPointsToExistentDirectoryError" }) { }
    }

    public async Task UploadAsync(string path, Stream data, long length, Action<long>? progress, CancellationToken ct)
    {
        string href;
        using (var doc = await ApiAsync(HttpMethod.Get, $"/resources/upload?path={Esc(path)}&overwrite=true", ct))
            href = doc == null ? "" : Str(doc.RootElement, "href") ?? "";
        var target = TransferUri(href, "загрузки");

        using var stall = new StallGuard(ct);
        // по ссылке загрузки токен не нужен и не передаётся
        using var put = new HttpRequestMessage(HttpMethod.Put, target)
        { Content = new StreamContent(new ProgressStream(data, n => { stall.Tick(); progress?.Invoke(n); }), 81920) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        put.Content.Headers.ContentLength = length;
        using var resp = await SendAsync(put, HttpCompletionOption.ResponseContentRead, stall.Token, ct);
        if (resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.Accepted or HttpStatusCode.OK) return;
        throw Map(resp.StatusCode, null);
    }

    public async Task DownloadAsync(string path, Stream target, Action<long>? progress, CancellationToken ct)
    {
        string href;
        using (var doc = await ApiAsync(HttpMethod.Get, $"/resources/download?path={Esc(path)}", ct))
            href = doc == null ? "" : Str(doc.RootElement, "href") ?? "";
        var source = TransferUri(href, "скачивания");

        using var stall = new StallGuard(ct);
        using var get = new HttpRequestMessage(HttpMethod.Get, source);
        using var resp = await SendAsync(get, HttpCompletionOption.ResponseHeadersRead, stall.Token, ct);
        if (!resp.IsSuccessStatusCode) throw Map(resp.StatusCode, null);
        try
        {
            await using var body = await resp.Content.ReadAsStreamAsync(stall.Token);
            byte[] buf = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buf, stall.Token)) > 0)
            {
                stall.Tick();
                await target.WriteAsync(buf.AsMemory(0, n), ct);
                progress?.Invoke(n);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        { throw new DiskException("Связь с Яндекс.Диском прервалась при скачивании.", DiskErrorKind.Transient, e); }
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        // permanently=false — в Корзину Диска, оттуда можно восстановить
        try { (await ApiAsync(HttpMethod.Delete, $"/resources?path={Esc(path)}&permanently=false", ct))?.Dispose(); }
        catch (DiskException e) when (e.NotFound) { }
    }

    private static Uri TransferUri(string href, string what) =>
        Uri.TryCreate(href, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            ? u : throw new DiskException($"Яндекс.Диск не выдал ссылку для {what}.", DiskErrorKind.Transient);

    /// <summary>Запрос к API с токеном; 401 — один раз обновляем токен и повторяем. null — пустой ответ.</summary>
    private async Task<JsonDocument?> ApiAsync(HttpMethod method, string relative, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            string token = await tokens.GetAsync(force: attempt > 0, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ApiTimeout);
            using var req = new HttpRequestMessage(method, Api + relative);
            req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;

            string body;
            try { body = await resp.Content.ReadAsStringAsync(timeout.Token); }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
            { throw new DiskException("Связь с Яндекс.Диском прервалась.", DiskErrorKind.Transient, e); }
            if (!resp.IsSuccessStatusCode) throw Map(resp.StatusCode, body);
            if (string.IsNullOrWhiteSpace(body)) return null;
            try { return JsonDocument.Parse(body); }
            catch (JsonException e) { throw new DiskException("Яндекс.Диск ответил непонятно.", DiskErrorKind.Transient, e); }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption option, CancellationToken token, CancellationToken user)
    {
        try { return await http.SendAsync(req, option, token); }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !user.IsCancellationRequested)
        { throw new DiskException("Нет связи с Яндекс.Диском.", DiskErrorKind.Transient, e); }
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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
            403 => new DiskException("Доступ запрещён: приложению не выданы права на весь Диск (чтение и запись) — " +
                                     "добавьте их на oauth.yandex.ru и войдите заново.", DiskErrorKind.Fatal),
            404 => new DiskException("На Диске нет такого файла или папки.", DiskErrorKind.Transient, new HttpNotFound()),
            409 => new DiskException(apiError == "DiskPathDoesntExistsError" ? "Папка на Диске не найдена."
                    : "Конфликт на Яндекс.Диске" + (apiError != null ? " (" + apiError + ")." : "."),
                DiskErrorKind.Transient, new HttpConflict(apiError)),
            413 => new DiskException("Файл слишком большой для вашего тарифа Яндекс.Диска.", DiskErrorKind.Fatal),
            423 => new DiskException("Яндекс.Диск временно не принимает изменения.", DiskErrorKind.Transient),
            429 => new DiskException("Слишком много запросов — повторим позже.", DiskErrorKind.Transient),
            507 => new DiskException("На Яндекс.Диске закончилось место.", DiskErrorKind.Fatal),
            >= 500 => new DiskException($"Яндекс.Диск временно недоступен (HTTP {(int)status}).", DiskErrorKind.Transient),
            _ => new DiskException($"Яндекс.Диск ответил ошибкой {(int)status}{(apiError != null ? " (" + apiError + ")" : "")}.", DiskErrorKind.Fatal),
        };
    }

    internal sealed class HttpConflict(string? apiError) : Exception
    {
        public string? ApiError { get; } = apiError;
    }
    internal sealed class HttpNotFound : Exception;

    /// <summary>Отменяет передачу, если данные перестали идти.</summary>
    private sealed class StallGuard : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        public StallGuard(CancellationToken user)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(user);
            _cts.CancelAfter(StallTimeout);
        }
        public CancellationToken Token => _cts.Token;
        public void Tick() { try { _cts.CancelAfter(StallTimeout); } catch (ObjectDisposedException) { } }
        public void Dispose() => _cts.Dispose();
    }
}

/// <summary>Поток-обёртка: сообщает, сколько байт прочитано.</summary>
internal sealed class ProgressStream(Stream inner, Action<long> read) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = inner.Read(buffer, offset, count);
        if (n > 0) read(n);
        return n;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int n = await inner.ReadAsync(buffer, ct);
        if (n > 0) read(n);
        return n;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Поток-обёртка: считает SHA-256 всего, что через него прошло.</summary>
internal sealed class HashingStream(Stream inner, bool reading) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public long Count { get; private set; }
    public string Sha256Hex() => Convert.ToHexString(_hash.GetHashAndReset());

    public override bool CanRead => reading;
    public override bool CanSeek => false;
    public override bool CanWrite => !reading;
    public override long Length => inner.Length;
    public override long Position { get => Count; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = inner.Read(buffer, offset, count);
        _hash.AppendData(buffer, offset, n);
        Count += n;
        return n;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int n = await inner.ReadAsync(buffer, ct);
        _hash.AppendData(buffer.Span[..n]);
        Count += n;
        return n;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        _hash.AppendData(buffer, offset, count);
        Count += count;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct);
        _hash.AppendData(buffer.Span);
        Count += buffer.Length;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Пути на Диске: «disk:/Папка/Подпапка».</summary>
internal static class DiskPath
{
    /// <summary>
    /// Путь, введённый человеком (настройки): «/Папка», «Папка/», «disk:/Папка» → «disk:/Папка»; корень — «disk:/».
    /// Обрезаются только пробелы по краям всей строки, имена частей остаются как есть («/Папка /x» ≠ «/Папка/x»).
    /// </summary>
    public static string Normalize(string path)
    {
        string p = path.Trim().Replace('\\', '/');
        if (p.StartsWith("disk:", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "disk:/" + string.Join('/', parts);
    }

    /// <summary>Корень (уже нормализованный) + относительный путь как есть, без обрезки пробелов.</summary>
    public static string Combine(string root, string rel) => root.EndsWith('/') ? root + rel : root + "/" + rel;

    public static string? Parent(string path)
    {
        if (path == "disk:/") return null;
        int i = path.LastIndexOf('/');
        return i < "disk:/".Length ? "disk:/" : path[..i];
    }

    /// <summary>Для показа человеку: «/Папка/Подпапка».</summary>
    public static string Display(string path) => Normalize(path)[5..];

    public static bool IsRoot(string path) => Normalize(path) == "disk:/";

    /// <summary>Ссылка на папку в веб-интерфейсе Диска.</summary>
    public static string WebUrl(string path) =>
        "https://disk.yandex.ru/client/disk" + string.Concat(Display(path).Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => "/" + Uri.EscapeDataString(s)));
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
            int generation;
            lock (secrets.Sync)
            {
                if (!secrets.SignedIn)
                    throw new DiskException("Не выполнен вход в Яндекс.", DiskErrorKind.Auth);
                generation = secrets.Generation;
            }
            // токен живёт около года; обновляем заранее, за 30 дней до конца
            bool stale = secrets.ExpiresAt is { } exp && exp - DateTime.UtcNow < TimeSpan.FromDays(30);
            if ((force || stale) && !string.IsNullOrEmpty(secrets.RefreshToken) && oauth() is { } o)
            {
                try
                {
                    var t = await o.RefreshAsync(secrets.RefreshToken, ct);
                    lock (secrets.Sync)
                    {
                        // пока обновляли, пользователь вышел или вошёл заново — его выбор главнее
                        if (secrets.Generation != generation || !secrets.SignedIn)
                            throw new DiskException("Вход изменился — повторим.", DiskErrorKind.Transient);
                        secrets.AccessToken = t.AccessToken;
                        secrets.RefreshToken = t.RefreshToken ?? secrets.RefreshToken;
                        secrets.ExpiresAt = t.ExpiresAt;
                        save();
                    }
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
            lock (secrets.Sync)
            {
                if (!secrets.SignedIn) throw new DiskException("Не выполнен вход в Яндекс.", DiskErrorKind.Auth);
                return secrets.AccessToken!;
            }
        }
        finally { _lock.Release(); }
    }
}

/// <summary>Один конкретный токен (проверка сразу после входа, до сохранения).</summary>
internal sealed class StaticToken(string token) : ITokenSource
{
    public Task<string> GetAsync(bool force, CancellationToken ct) => force
        ? throw new DiskException("Яндекс.Диск не принял вход — войдите заново.", DiskErrorKind.Auth)
        : Task.FromResult(token);
}

/// <summary>
/// Токены, привязанные к входу на момент создания: если пользователь вышел или вошёл заново (возможно, под другой
/// учётной записью), запросы прохода, начатого до этого, не уходят — план строился для прежнего Диска.
/// </summary>
internal sealed class PinnedTokens(ITokenSource inner, Secrets secrets) : ITokenSource
{
    private readonly int _generation = Generation(secrets);

    private static int Generation(Secrets s)
    {
        lock (s.Sync) return s.Generation;
    }

    public Task<string> GetAsync(bool force, CancellationToken ct)
    {
        if (Generation(secrets) != _generation)
            throw new DiskException("Вход изменился — синхронизация начнётся заново.", DiskErrorKind.Transient);
        return inner.GetAsync(force, ct);
    }
}
