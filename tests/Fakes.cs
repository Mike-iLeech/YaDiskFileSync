using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace YaDiskFileSync.Tests;

/// <summary>HTTP-подделка: отвечает по очереди заданными ответами и запоминает запросы.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _answers = new();
    public List<(HttpMethod Method, string Url, string? Auth, string Body)> Requests { get; } = new();
    public List<long?> ContentLengths { get; } = new();

    public FakeHttp Reply(HttpStatusCode code, string body = "") =>
        Then(_ => new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    public FakeHttp ReplyBytes(HttpStatusCode code, byte[] body) =>
        Then(_ => new HttpResponseMessage(code) { Content = new ByteArrayContent(body) });

    public FakeHttp Then(Func<HttpRequestMessage, HttpResponseMessage> f)
    {
        _answers.Enqueue(f);
        return this;
    }

    public FakeHttp Fail() => Then(_ => throw new HttpRequestException("нет сети"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // длину — до чтения тела: после буферизации HttpContent узнаёт её и сам
        ContentLengths.Add(request.Content?.Headers.ContentLength);
        string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
        if (_answers.Count == 0) throw new InvalidOperationException("Неожиданный запрос: " + request.RequestUri);
        return _answers.Dequeue()(request);
    }

    public HttpClient Client() => new(this);
}

internal sealed class FixedToken(string token = "T") : ITokenSource
{
    public int Forced { get; private set; }
    public Task<string> GetAsync(bool force, CancellationToken ct)
    {
        if (force) Forced++;
        return Task.FromResult(token + (Forced > 0 ? Forced.ToString() : ""));
    }
}

/// <summary>
/// Яндекс.Диск в памяти: файлы и папки по полным путям «disk:/…» — с учётом регистра и пробелов в именах, как настоящий,
/// SHA-256 и время изменения, Корзина. Hook — вмешаться в любую операцию (ошибка, правка «с другого устройства»).
/// </summary>
internal sealed class FakeDisk : IDisk
{
    public sealed class Node
    {
        public bool IsDir;
        public byte[] Data = Array.Empty<byte>();
        public DateTime Modified;
        public bool OmitSha;   // ответ без sha256
    }

    public Dictionary<string, Node> Items { get; } = new(StringComparer.Ordinal);
    /// <summary>Загрузка принята (202), но в списке Диска пока прежняя версия — до ApplyPending().</summary>
    public bool DelayApply { get; set; }
    private readonly Dictionary<string, byte[]> _pending = new(StringComparer.Ordinal);
    public int Lists { get; private set; }

    public void ApplyPending()
    {
        foreach (var (p, d) in _pending) Items[p] = new Node { Data = d, Modified = Tick() };
        _pending.Clear();
    }
    public List<string> Trash { get; } = new();
    public List<string> Log { get; } = new();
    public DateTime Clock { get; set; } = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
    public DiskUser? User { get; set; } = new("uid-1", "mike");
    /// <summary>(операция, путь) → исключение или null; вызывается перед операцией.</summary>
    public Func<string, string, Exception?>? Hook { get; set; }

    private DateTime Tick() => Clock = Clock.AddSeconds(1);

    /// <summary>Полный путь «disk:/…» оставляем как есть (имена с пробелами — настоящие), остальное нормализуем.</summary>
    public static string N(string p) => p.StartsWith("disk:/", StringComparison.Ordinal) ? p : DiskPath.Normalize(p);

    public void PutFile(string path, string content, DateTime? modified = null)
    {
        EnsureParents(N(path));
        Items[N(path)] = new Node { Data = Encoding.UTF8.GetBytes(content), Modified = modified ?? Tick() };
    }

    public void PutDir(string path)
    {
        EnsureParents(N(path));
        Items[N(path)] = new Node { IsDir = true, Modified = Tick() };
    }

    public string? Text(string path) => Items.TryGetValue(N(path), out var n) && !n.IsDir ? Encoding.UTF8.GetString(n.Data) : null;
    public bool Has(string path) => Items.ContainsKey(N(path));

    private void EnsureParents(string path)
    {
        for (string? p = DiskPath.Parent(path); p != null && p != "disk:/"; p = DiskPath.Parent(p))
            if (!Items.ContainsKey(p)) Items[p] = new Node { IsDir = true, Modified = Clock };
    }

    private void Check(string op, string path)
    {
        if (Hook?.Invoke(op, path) is { } e) throw e;
    }

    public Task<DiskUser?> GetUserAsync(CancellationToken ct)
    {
        Check("user", "");
        return Task.FromResult(User);
    }

    public Task<IReadOnlyList<RemoteItem>?> ListTreeAsync(string root, CancellationToken ct)
    {
        root = N(root);
        Check("list", root);
        Lists++;
        if (!Items.TryGetValue(root, out var r) || !r.IsDir) return Task.FromResult<IReadOnlyList<RemoteItem>?>(null);
        string prefix = root + "/";
        IReadOnlyList<RemoteItem> list = Items.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => new RemoteItem(kv.Key[prefix.Length..], kv.Value.IsDir, kv.Value.IsDir ? 0 : kv.Value.Data.Length,
                kv.Value.IsDir || kv.Value.OmitSha ? null : Convert.ToHexString(SHA256.HashData(kv.Value.Data)), kv.Value.Modified))
            .ToList();
        return Task.FromResult<IReadOnlyList<RemoteItem>?>(list);
    }

    public Task EnsureFolderAsync(string path, CancellationToken ct)
    {
        path = N(path);
        Check("mkdir", path);
        Log.Add("mkdir " + path);
        if (Items.TryGetValue(path, out var n) && !n.IsDir) throw new DiskException("там файл", DiskErrorKind.Transient);
        EnsureParents(path);
        if (!Items.ContainsKey(path)) Items[path] = new Node { IsDir = true, Modified = Tick() };
        return Task.CompletedTask;
    }

    public async Task UploadAsync(string path, Stream data, long length, Action<long>? progress, CancellationToken ct)
    {
        path = N(path);
        Check("upload", path);
        var ms = new MemoryStream();
        await data.CopyToAsync(ms, ct);
        if (ms.Length != length) throw new DiskException("длина не совпала", DiskErrorKind.Transient);
        progress?.Invoke(ms.Length);
        string? parent = DiskPath.Parent(path);
        if (parent != null && parent != "disk:/" && !Items.ContainsKey(parent))
            throw new DiskException("Папка на Диске не найдена.", DiskErrorKind.Transient);
        Check("uploaded", path);
        Log.Add("upload " + path);
        if (DelayApply) _pending[path] = ms.ToArray();
        else Items[path] = new Node { Data = ms.ToArray(), Modified = Tick() };
    }

    public async Task DownloadAsync(string path, Stream target, Action<long>? progress, CancellationToken ct)
    {
        path = N(path);
        Check("download", path);
        if (!Items.TryGetValue(path, out var n) || n.IsDir)
            throw new DiskException("нет", DiskErrorKind.Transient, new DiskClient.HttpNotFound());
        Log.Add("download " + path);
        await target.WriteAsync(n.Data, ct);
        progress?.Invoke(n.Data.Length);
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        path = N(path);
        Check("delete", path);
        Log.Add("delete " + path);
        foreach (var k in Items.Keys.Where(k => k.Equals(path, StringComparison.Ordinal) ||
                                                k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            Items.Remove(k);
            Trash.Add(k);
        }
        return Task.CompletedTask;
    }
}

/// <summary>«Корзина Windows» для тестов: переносит в отдельную папку и запоминает.</summary>
internal sealed class FakeTrash(string dir) : ILocalTrash
{
    public List<string> Recycled { get; } = new();
    public Func<string, Exception?>? Hook { get; set; }

    public void Recycle(string path)
    {
        if (Hook?.Invoke(path) is { } e) throw e;
        Recycled.Add(path);
        string dest = Path.Combine(dir, Recycled.Count + "_" + Path.GetFileName(path));
        if (Directory.Exists(path)) Directory.Move(path, dest);
        else File.Move(path, dest);
    }
}
