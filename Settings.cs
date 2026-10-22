using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace YaDiskFileSync;

internal sealed class Settings
{
    /// <summary>Файл, который синхронизируем.</summary>
    public string? FilePath { get; set; }
    /// <summary>ClientID приложения, зарегистрированного пользователем на oauth.yandex.ru.</summary>
    public string? ClientId { get; set; }
    /// <summary>Сколько дневных копий держать на Диске.</summary>
    public int KeepCopies { get; set; } = 30;

    /// <summary>SHA-256 последней загруженной версии — повторно ту же не грузим.</summary>
    public string? LastUploadedHash { get; set; }
    /// <summary>Какой файл был загружен с этим хешем (сменили файл — грузим заново).</summary>
    public string? LastUploadedPath { get; set; }
    public DateTime? LastUploadedAt { get; set; }

    public bool AutoUpdate { get; set; } = true;
    /// <summary>Как часто проверять обновления, в часах; 0 — не проверять.</summary>
    public int UpdateCheckHours { get; set; } = 24;
    public string? SkippedUpdate { get; set; }

    internal static string Dir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YaDiskFileSync");
    private static string FileName => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FileName))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FileName)) ?? new Settings();
        }
        catch { /* битый файл — начинаем с чистых настроек */ }
        return new Settings();
    }

    public void Save() =>
        AtomicFile.Write(FileName, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
}

internal static class AtomicFile
{
    /// <summary>Через временный файл: оборванная запись не портит старое содержимое.</summary>
    public static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Секреты OAuth. Хранятся зашифрованными DPAPI — расшифровать может только этот пользователь Windows.</summary>
internal sealed class Secrets
{
    public string? ClientSecret { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public bool SignedIn => !string.IsNullOrEmpty(AccessToken);

    private static readonly byte[] Entropy = "YaDiskFileSync.v1"u8.ToArray();
    private static string FileName => Path.Combine(Settings.Dir, "secrets.bin");

    public static Secrets Load()
    {
        try
        {
            if (!File.Exists(FileName)) return new Secrets();
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FileName), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Secrets>(plain) ?? new Secrets();
        }
        catch { return new Secrets(); }   // чужой профиль или битый файл — войти заново
    }

    public void Save() =>
        AtomicFile.Write(FileName, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(this), Entropy, DataProtectionScope.CurrentUser));

    public void ClearTokens()
    {
        AccessToken = null;
        RefreshToken = null;
        ExpiresAt = null;
    }
}

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "YaDiskFileSync";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(k?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(ValueName, Command);
            else k.DeleteValue(ValueName, false);
        }
    }
}
