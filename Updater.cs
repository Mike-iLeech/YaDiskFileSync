using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace YaDiskFileSync;

internal sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string AssetUrl, long AssetSize, string? Sha256,
    string Notes = "");

internal sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Запущенная новая версия, которая ещё должна отозваться.</summary>
internal sealed record UpdateHandoff(Process Process, EventWaitHandle Ready, string ExePath, Version Version);

/// <summary>
/// Автообновление из GitHub Releases.
/// Работающий EXE нельзя перезаписать, но можно переименовать: текущий уходит в .old,
/// новый встаёт на его место и запускается, а .old удаляет уже новая версия при старте.
/// </summary>
internal static class Updater
{
    public const string Repo = "Mike-iLeech/YaDiskFileSync";
    public const string AssetName = "YaDiskFileSync.exe";
    public const string UpdatedFlag = "--updated";
    public const string RolledBackFlag = "--rolled-back";
    public const string ReadyEventName = @"Local\YaDiskFileSync.UpdateReady";
    private const string ApiLatest = $"https://api.github.com/repos/{Repo}/releases/latest";

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YaDiskFileSync", Current.ToString()));
        return c;
    }

    /// <summary>
    /// Обновлять на месте можно только опубликованный однофайловый EXE. У dotnet run / отладки
    /// в bin тоже есть YaDiskFileSync.exe, но сборка там лежит отдельным файлом (Location не пуст).
    /// </summary>
    public static string? ExePath
    {
        get
        {
            string? p = Environment.ProcessPath;
#pragma warning disable IL3000 // в однофайловом EXE Location пуст — именно это и проверяем
            bool singleFile = string.IsNullOrEmpty(typeof(Updater).Assembly.Location);
#pragma warning restore IL3000
            return singleFile && p != null && Path.GetFileName(p).Equals(AssetName, StringComparison.OrdinalIgnoreCase)
                ? p : null;
        }
    }

    /// <summary>'v1.2' / '1.2.0.0' → 1.2.0.0; хвостовые нули не важны. null — тег не версия.</summary>
    public static Version? ParseVersion(string? text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"\d+(\.\d+){0,3}");
        return m.Success && Version.TryParse(m.Value.Contains('.') ? m.Value : m.Value + ".0", out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static bool IsNewer(ReleaseInfo r) => r.Version > Current;

    /// <summary>Разбирает ответ /releases/latest. null — в релизе нет нашего EXE или тег не версия.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var version = ParseVersion(tag);
        if (version == null || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var a in assets.EnumerateArray())
        {
            if (!a.TryGetProperty("name", out var n) || !AssetName.Equals(n.GetString(), StringComparison.OrdinalIgnoreCase))
                continue;
            string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url)) return null;
            long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
            string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..].ToLowerInvariant() : null;
            string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            string notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            return new ReleaseInfo(version, tag, page, url, size, sha, notes);
        }
        return null;
    }

    public static async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiLatest);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        { throw new UpdateException("Нет связи с GitHub.", e); }

        using (resp)
        {
            if ((int)resp.StatusCode == 404) return null;            // ещё нет ни одного релиза
            if ((int)resp.StatusCode is 403 or 429)
                throw new UpdateException("GitHub временно ограничил число запросов.");
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException($"GitHub ответил ошибкой {(int)resp.StatusCode}.");
            string body;
            try { body = await resp.Content.ReadAsStringAsync(ct); }
            catch (Exception e) { throw new UpdateException("Связь с GitHub оборвалась.", e); }
            try { return ParseRelease(body); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            { throw new UpdateException("Не удалось разобрать ответ GitHub.", e); }
        }
    }

    /// <summary>Качает EXE рядом с текущим (.new) и сверяет размер и SHA-256. Возвращает путь к файлу.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo r, string exePath, CancellationToken ct = default)
    {
        if (r.Sha256 == null)
            throw new UpdateException("У релиза нет контрольной суммы — обновление не установлено.");

        string tmp = exePath + ".new";
        try
        {
            using (var resp = await Http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, ct);
            }

            var info = new FileInfo(tmp);
            if (r.AssetSize > 0 && info.Length != r.AssetSize)
                throw new UpdateException("Файл обновления скачался не полностью.");

            string actual;
            await using (var fs = File.OpenRead(tmp))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (actual != r.Sha256)
                throw new UpdateException("Контрольная сумма обновления не совпала — файл отброшен.");
            return tmp;
        }
        catch (Exception e)
        {
            TryDelete(tmp);
            if (e is UpdateException) throw;
            if (e is UnauthorizedAccessException)
                throw new UpdateException("Нет прав на запись в папку программы — переместите её, например, в Документы.", e);
            throw new UpdateException("Не удалось скачать обновление: " + e.Message, e);
        }
    }

    /// <summary>
    /// Подменяет EXE и запускает новую версию. Вызывающий после этого убирает значок, отпускает мьютекс
    /// и вызывает <see cref="ConfirmOrRollback"/>: новая версия должна отозваться, иначе вернётся старая.
    /// </summary>
    public static UpdateHandoff InstallAndStart(string newFile, string exePath, Version version)
    {
        var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName);
        try
        {
            ready.Reset();
            var proc = InstallAndRestart(newFile, exePath);
            return new UpdateHandoff(proc, ready, exePath, version);
        }
        catch
        {
            ready.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Ждёт, пока новая версия отзовётся через <see cref="ReadyEventName"/>. Не отозвалась или упала —
    /// гасит её, возвращает старый EXE и запускает его. true — обновление прижилось.
    /// </summary>
    public static bool ConfirmOrRollback(UpdateHandoff h, TimeSpan? timeout = null)
    {
        using var _ = h.Ready;
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        while (DateTime.UtcNow < deadline)
        {
            if (h.Ready.WaitOne(250)) return true;
            if (h.Process.HasExited) break;
        }

        try
        {
            if (!h.Process.HasExited) { h.Process.Kill(); h.Process.WaitForExit(5000); }
        }
        catch { /* уже вышла */ }

        if (!Rollback(h.ExePath)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(h.ExePath, $"{RolledBackFlag} {h.Version}") { UseShellExecute = false });
        }
        catch { /* старая версия на месте — запустится при следующем входе */ }
        return false;
    }

    /// <summary>Возвращает .old на место EXE (неудачная новая версия удаляется).</summary>
    public static bool Rollback(string exePath)
    {
        string old = exePath + ".old";
        if (!File.Exists(old)) return false;
        for (int i = 0; i < 20; i++)
        {
            try
            {
                File.Move(old, exePath, overwrite: true);
                return true;
            }
            catch (IOException) { Thread.Sleep(250); }       // упавший процесс ещё держит файл
            catch (UnauthorizedAccessException) { Thread.Sleep(250); }
        }
        return false;
    }

    internal static Process InstallAndRestart(string newFile, string exePath)
    {
        string old = exePath + ".old";
        TryDelete(old);
        try
        {
            File.Move(exePath, old);
        }
        catch (Exception e)
        {
            TryDelete(newFile);
            throw new UpdateException("Не удалось заменить программу: " + e.Message, e);
        }
        try
        {
            File.Move(newFile, exePath);
        }
        catch (Exception e)
        {
            try { File.Move(old, exePath); } catch { /* откат не удался — .old остаётся рядом */ }
            TryDelete(newFile);
            throw new UpdateException("Не удалось заменить программу: " + e.Message, e);
        }

        try
        {
            return Process.Start(new ProcessStartInfo(exePath, $"{UpdatedFlag} {Current}") { UseShellExecute = false })
                   ?? throw new UpdateException("Новая версия не запустилась.");
        }
        catch (Exception e)
        {
            // новая версия не стартует — возвращаем старую на место
            try { File.Move(exePath, newFile, true); File.Move(old, exePath); } catch { }
            TryDelete(newFile);
            throw e as UpdateException ?? new UpdateException("Новая версия не запустилась: " + e.Message, e);
        }
    }

    // ---------- что нового: старая версия оставляет заметки, новая показывает их после перезапуска ----------

    internal static string NotesFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YaDiskFileSync", "update-notes.txt");

    /// <summary>Перед перезапуском: версия, страница релиза и описание — для уведомления новой версии.</summary>
    public static void SaveNotes(ReleaseInfo r)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NotesFile)!);
            File.WriteAllText(NotesFile, $"{r.Version}\n{r.PageUrl}\n{r.Notes}");
        }
        catch { /* без заметок уведомление просто будет короче */ }
    }

    /// <summary>Забирает заметки (файл удаляется). null — заметок нет или они к другой версии.</summary>
    public static (string Notes, string PageUrl)? TakeNotes(Version current)
    {
        try
        {
            if (!File.Exists(NotesFile)) return null;
            string[] parts = File.ReadAllText(NotesFile).Split('\n', 3);
            File.Delete(NotesFile);
            if (parts.Length < 2 || ParseVersion(parts[0]) != current) return null;
            return (parts.Length > 2 ? parts[2] : "", parts[1].Trim());
        }
        catch { return null; }
    }

    /// <summary>Описание релиза (markdown из CHANGELOG) → короткий текст для уведомления Windows.</summary>
    public static string BalloonText(string notes, int max = 200)
    {
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.StartsWith("- ") || l.StartsWith("* ") ? "• " + l[2..].Trim() : l)
            .Select(l => l.Replace("**", "").Replace("`", ""));
        string text = string.Join("\n", lines);
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Новая версия отзывается старой: «я запустилась», после этого старая выходит окончательно.</summary>
    public static void SignalReady()
    {
        if (EventWaitHandle.TryOpenExisting(ReadyEventName, out var ev))
            using (ev) ev.Set();
    }

    /// <summary>Удаляет .old/.new, оставшиеся от прошлого обновления.</summary>
    public static void Cleanup()
    {
        if (ExePath is not { } exe) return;
        // старый процесс запущен из .old и выходит только после нашего сигнала — ждём до минуты
        for (int i = 0; i < 120 && File.Exists(exe + ".old"); i++)
        {
            TryDelete(exe + ".old");
            if (File.Exists(exe + ".old")) Thread.Sleep(500);
        }
        TryDelete(exe + ".new");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
