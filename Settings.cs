using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace YaDiskFileSync;

internal sealed class Settings
{
    public const string DefaultFolderName = "YaDiskSync";
    public const int DefaultPollSeconds = 60;
    internal static readonly int[] PollChoices = { 30, 60, 300, 900 };

    /// <summary>Папка на компьютере, которую синхронизируем.</summary>
    public string? LocalRoot { get; set; }
    /// <summary>Папка на Диске: «disk:/YaDiskSync».</summary>
    public string? RemoteRoot { get; set; }
    /// <summary>ClientID приложения, зарегистрированного пользователем на oauth.yandex.ru.</summary>
    public string? ClientId { get; set; }
    /// <summary>Учётная запись Яндекса, под которой вошли (uid — для привязки базы синхронизации).</summary>
    public string? AccountUid { get; set; }
    public string? AccountLogin { get; set; }
    /// <summary>Как часто проверять изменения на Диске, в секундах.</summary>
    public int PollSeconds { get; set; } = DefaultPollSeconds;
    /// <summary>Показывать папку в панели навигации Проводника.</summary>
    public bool ShowInExplorer { get; set; } = true;
    public DateTime? LastSyncedAt { get; set; }
    /// <summary>Файл, который синхронизировала версия 1.x (поле FilePath её настроек) — чтобы подсказать, куда его деть.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("FilePath")]
    public string? LegacyFilePath { get; set; }

    public bool AutoUpdate { get; set; } = true;
    /// <summary>Как часто проверять обновления, в часах; 0 — не проверять.</summary>
    public int UpdateCheckHours { get; set; } = 24;
    public string? SkippedUpdate { get; set; }

    public static string DefaultLocalRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultFolderName);
    public static string DefaultRemoteRoot => "disk:/" + DefaultFolderName;

    internal static TimeSpan Poll(int seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, 15, 3600));

    internal static string PollName(int seconds) => seconds switch
    {
        < 60 => $"Раз в {seconds} с",
        60 => "Раз в минуту",
        _ => $"Раз в {seconds / 60} мин",
    };

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
    /// <summary>
    /// Под какие права получены токены: 0/1 — версия 1.x (только папка приложения), 2 — весь Диск.
    /// Токены старых прав не годятся — нужен новый вход.
    /// </summary>
    public int Scope { get; set; }
    public const int CurrentScope = 2;

    public bool SignedIn => !string.IsNullOrEmpty(AccessToken);

    /// <summary>
    /// Замок и «поколение» входа: вход/выход меняют поколение, и фоновое обновление токена,
    /// начатое до них, свой результат уже не записывает.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore] public object Sync { get; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public int Generation { get; set; }

    private static readonly byte[] Entropy = "YaDiskFileSync.v1"u8.ToArray();
    private static string FileName => Path.Combine(Settings.Dir, "secrets.bin");

    public static Secrets Load()
    {
        try
        {
            if (!File.Exists(FileName)) return new Secrets();
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FileName), Entropy, DataProtectionScope.CurrentUser);
            return Upgrade(JsonSerializer.Deserialize<Secrets>(plain) ?? new Secrets());
        }
        catch { return new Secrets(); }   // чужой профиль или битый файл — войти заново
    }

    /// <summary>Токены версии 1.x выданы только на папку приложения — забываем их; ClientID и секрет оставляем.</summary>
    internal static Secrets Upgrade(Secrets s)
    {
        if (s.Scope < CurrentScope) s.ClearTokens();
        return s;
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
    internal const string ValueName = "YaDiskFileSync";

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
