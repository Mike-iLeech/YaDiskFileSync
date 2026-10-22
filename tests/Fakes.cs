using System.Net;
using System.Text;

namespace YaDiskFileSync.Tests;

/// <summary>HTTP-подделка: отвечает по очереди заданными ответами и запоминает запросы.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _answers = new();
    public List<(HttpMethod Method, string Url, string? Auth, string Body)> Requests { get; } = new();

    public FakeHttp Reply(HttpStatusCode code, string body = "") =>
        Then(_ => new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    public FakeHttp Then(Func<HttpRequestMessage, HttpResponseMessage> f)
    {
        _answers.Enqueue(f);
        return this;
    }

    public FakeHttp Fail() => Then(_ => throw new HttpRequestException("нет сети"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
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

/// <summary>Диск в памяти.</summary>
internal sealed class FakeDisk : IDisk
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public List<string> Folders { get; } = new();
    public List<string> Log { get; } = new();
    public Queue<DiskException> FailUploads { get; } = new();
    public DiskException? FailList { get; set; }

    public Task UploadAsync(string path, byte[] data, CancellationToken ct)
    {
        if (FailUploads.Count > 0) throw FailUploads.Dequeue();
        Log.Add("upload " + path);
        Files[path] = data.ToArray();
        return Task.CompletedTask;
    }

    public Task EnsureFolderAsync(string path, CancellationToken ct)
    {
        if (!Folders.Contains(path)) Folders.Add(path);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListFileNamesAsync(string folder, CancellationToken ct)
    {
        if (FailList != null) throw FailList;
        IReadOnlyList<string> r = Files.Keys.Where(k => k.StartsWith(folder + "/")).Select(k => k[(folder.Length + 1)..]).ToList();
        return Task.FromResult(r);
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        Log.Add("delete " + path);
        Files.Remove(path);
        return Task.CompletedTask;
    }
}
