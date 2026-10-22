using Xunit;

namespace YaDiskFileSync.Tests;

public class FolderWatchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ydfs-watch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData("Бухгалтерия.xlsx", true)]
    [InlineData(@"Отчёты\квартал.xlsx", true)]
    [InlineData("~$Бухгалтерия.xlsx", false)]   // файл-блокировка Excel
    [InlineData("A1B2C3D4", false)]             // временный файл сохранения
    [InlineData(@".yadisksync\tmp\abc", false)] // наши же скачивания
    [InlineData(null, false)]
    public void IsRelevant(string? name, bool expected) => Assert.Equal(expected, FolderWatch.IsRelevant(name));

    [Fact]
    public async Task ExcelStyleSave_InSubfolder_Fires()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Отчёты"));
        string target = Path.Combine(_dir, "Отчёты", "Книга.xlsx");
        File.WriteAllText(target, "old");
        var fired = new TaskCompletionSource();
        using var w = new FolderWatch(_dir, () => fired.TrySetResult());
        string tmp = Path.Combine(_dir, "Отчёты", "5F3A2B10");
        File.WriteAllText(tmp, "new");
        File.Move(target, Path.Combine(_dir, "Отчёты", "1C2D3E4F.tmp"));
        File.Move(tmp, target);
        await fired.Task.WaitAsync(FolderWatch.Debounce + TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Deletion_Fires()
    {
        string f = Path.Combine(_dir, "a.txt");
        File.WriteAllText(f, "x");
        var fired = new TaskCompletionSource();
        using var w = new FolderWatch(_dir, () => fired.TrySetResult());
        File.Delete(f);
        await fired.Task.WaitAsync(FolderWatch.Debounce + TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OnlyServiceFiles_DoNotFire()
    {
        int fired = 0;
        using var w = new FolderWatch(_dir, () => Interlocked.Increment(ref fired));
        File.WriteAllText(Path.Combine(_dir, "~$Книга.xlsx"), "lock");
        File.WriteAllText(Path.Combine(_dir, "x.tmp"), "y");
        await Task.Delay(FolderWatch.Debounce + TimeSpan.FromSeconds(1));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void KickAfterDispose_NoThrow()
    {
        var w = new FolderWatch(_dir, () => { });
        w.Dispose();
        w.Kick();
        w.Dispose();
    }

    [Fact]
    public void MissingFolder_NotAttached_NoThrow()
    {
        using var w = new FolderWatch(Path.Combine(_dir, "нет"), () => { });
        Assert.False(w.Attached);
    }
}

[Collection("settings-dir")]
public class StorageTests : IDisposable
{
    private readonly string _old = Settings.Dir;
    private readonly string _dir = Directory.CreateTempSubdirectory("ydfs-set-").FullName;

    public StorageTests() => Settings.Dir = _dir;

    public void Dispose()
    {
        Settings.Dir = _old;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        new Settings { LocalRoot = @"D:\YaDiskSync", RemoteRoot = "disk:/Бух", ClientId = "cid", PollSeconds = 300, ShowInExplorer = false }.Save();
        var s = Settings.Load();
        Assert.Equal(@"D:\YaDiskSync", s.LocalRoot);
        Assert.Equal("disk:/Бух", s.RemoteRoot);
        Assert.Equal(300, s.PollSeconds);
        Assert.False(s.ShowInExplorer);
    }

    [Fact]
    public void Settings_FromVersion1_LoadsWithoutFolders()
    {
        // настройки 1.x: один файл, дневные копии — новые поля пустые, ClientID сохраняется
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"FilePath":"C:\\a.xlsx","ClientId":"cid","KeepCopies":30,"LastUploadedHash":"H","AutoUpdate":false}""");
        var s = Settings.Load();
        Assert.Equal("cid", s.ClientId);
        Assert.Null(s.LocalRoot);
        Assert.False(s.AutoUpdate);
        Assert.Equal(Settings.DefaultPollSeconds, s.PollSeconds);
    }

    [Fact]
    public void Settings_Corrupt_Defaults()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{не json");
        Assert.True(Settings.Load().ShowInExplorer);
    }

    [Fact]
    public void Secrets_Encrypted_RoundTrip()
    {
        new Secrets { ClientSecret = "секрет", AccessToken = "y0_TOKEN", RefreshToken = "R", Scope = Secrets.CurrentScope }.Save();
        byte[] raw = File.ReadAllBytes(Path.Combine(_dir, "secrets.bin"));
        Assert.DoesNotContain("y0_TOKEN", System.Text.Encoding.UTF8.GetString(raw));
        var s = Secrets.Load();
        Assert.Equal("y0_TOKEN", s.AccessToken);
        Assert.Equal("секрет", s.ClientSecret);
        Assert.True(s.SignedIn);
    }

    [Fact]
    public void Secrets_FromVersion1_TokensDropped_SecretKept()
    {
        // токены 1.x выданы только на папку приложения — для всего Диска нужен новый вход
        new Secrets { ClientSecret = "секрет", AccessToken = "old", RefreshToken = "R" }.Save();
        var s = Secrets.Load();
        Assert.False(s.SignedIn);
        Assert.Null(s.RefreshToken);
        Assert.Equal("секрет", s.ClientSecret);
    }

    [Fact]
    public void Secrets_Garbage_Empty()
    {
        File.WriteAllBytes(Path.Combine(_dir, "secrets.bin"), new byte[] { 1, 2, 3 });
        Assert.False(Secrets.Load().SignedIn);
    }

    [Fact]
    public void StateStore_KeyMismatch_Empty()
    {
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        string key = StateStore.KeyFor("u", @"C:\YaDiskSync", "disk:/YaDiskSync");
        store.Save(key, new Dictionary<string, BaseEntry> { ["a"] = new() { Sha256 = "X" } });
        Assert.Single(store.Load(key));
        Assert.Single(store.Load(StateStore.KeyFor("u", @"c:\yadisksync\", "/YaDiskSync/")));   // то же самое, иначе записано
        Assert.Empty(store.Load(StateStore.KeyFor("u2", @"C:\YaDiskSync", "disk:/YaDiskSync")));
        Assert.Empty(store.Load(StateStore.KeyFor("u", @"D:\YaDiskSync", "disk:/YaDiskSync")));
        Assert.Empty(store.Load(StateStore.KeyFor("u", @"C:\YaDiskSync", "disk:/Другая")));
    }

    [Fact]
    public void StateStore_Corrupt_Empty()
    {
        string f = Path.Combine(_dir, "state.json");
        File.WriteAllText(f, "мусор");
        Assert.Empty(new StateStore(f).Load("k"));
    }
}

public class SettingsFormTests
{
    [Theory]
    [InlineData(@"C:\", false)]
    [InlineData(@"D:\", false)]
    [InlineData(@"C:\Windows\Temp\x", false)]
    [InlineData(@"D:\Мои\YaDiskSync", true)]
    [InlineData("", false)]
    public void ValidateLocal(string path, bool ok) => Assert.Equal(ok, SettingsForm.ValidateLocal(path) == null);

    [Fact]
    public void ValidateLocal_WholeProfile_Refused() =>
        Assert.NotNull(SettingsForm.ValidateLocal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    [Fact]
    public void ValidateLocal_AppDataDir_Refused() => Assert.NotNull(SettingsForm.ValidateLocal(Settings.Dir));

    [Theory]
    [InlineData(@"D:\Документы", @"D:\Документы\YaDiskSync")]
    [InlineData(@"D:\Документы\", @"D:\Документы\YaDiskSync")]
    [InlineData(@"D:\YaDiskSync", @"D:\YaDiskSync")]
    [InlineData(@"D:\", @"D:\YaDiskSync")]
    public void FolderIn_CreatesOwnFolder(string chosen, string expected) => Assert.Equal(expected, SettingsForm.FolderIn(chosen));

    [Fact]
    public void LoginSteps_MentionAllRights()
    {
        string all = string.Join(" ", LoginForm.Steps);
        Assert.Contains("Чтение всего Диска", all);
        Assert.Contains("Запись в любом месте на Диске", all);
        Assert.Contains("Доступ к информации о Диске", all);
        Assert.Contains(LoginForm.RedirectUri, all);
        Assert.Contains("cloud_api:disk.read", YandexOAuth.Scope);
        Assert.Contains("cloud_api:disk.write", YandexOAuth.Scope);
    }

    [Theory]
    [InlineData(30, "Раз в 30 с")]
    [InlineData(60, "Раз в минуту")]
    [InlineData(300, "Раз в 5 мин")]
    public void PollNames(int sec, string name) => Assert.Equal(name, Settings.PollName(sec));
}
