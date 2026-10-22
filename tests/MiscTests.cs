using Xunit;

namespace YaDiskFileSync.Tests;

public class FileWatchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ydfs-watch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData("Бухгалтерия.xlsx", true)]
    [InlineData("БУХГАЛТЕРИЯ.XLSX", true)]
    [InlineData("~$Бухгалтерия.xlsx", false)]   // файл-блокировка Excel
    [InlineData("A1B2C3D4", false)]             // временный файл сохранения
    [InlineData("Бухгалтерия.xlsx.tmp", false)]
    [InlineData(null, false)]
    public void IsTarget(string? name, bool expected) =>
        Assert.Equal(expected, FileWatch.IsTarget(name, "Бухгалтерия.xlsx"));

    [Fact]
    public async Task ExcelStyleSave_Fires()
    {
        // как сохраняет Excel: пишет временный файл, старый переименовывает, временный — в исходное имя
        string target = Path.Combine(_dir, "Книга.xlsx");
        File.WriteAllText(target, "old");
        var fired = new TaskCompletionSource();
        using var w = new FileWatch(target, () => fired.TrySetResult());
        string tmp = Path.Combine(_dir, "5F3A2B10");
        File.WriteAllText(tmp, "new");
        File.Move(target, Path.Combine(_dir, "1C2D3E4F.tmp"));
        File.Move(tmp, target);
        await fired.Task.WaitAsync(FileWatch.Debounce + TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OtherFile_DoesNotFire()
    {
        string target = Path.Combine(_dir, "Книга.xlsx");
        File.WriteAllText(target, "x");
        int fired = 0;
        using var w = new FileWatch(target, () => Interlocked.Increment(ref fired));
        File.WriteAllText(Path.Combine(_dir, "другое.txt"), "y");
        await Task.Delay(FileWatch.Debounce + TimeSpan.FromSeconds(1));
        Assert.Equal(0, fired);
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
        new Settings { FilePath = @"C:\a.xlsx", ClientId = "cid", KeepCopies = 7, LastUploadedHash = "H" }.Save();
        var s = Settings.Load();
        Assert.Equal(@"C:\a.xlsx", s.FilePath);
        Assert.Equal(7, s.KeepCopies);
        Assert.Equal("H", s.LastUploadedHash);
    }

    [Fact]
    public void Settings_Corrupt_Defaults()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{не json");
        Assert.Equal(30, Settings.Load().KeepCopies);
    }

    [Fact]
    public void Secrets_Encrypted_RoundTrip()
    {
        new Secrets { ClientSecret = "секрет", AccessToken = "y0_TOKEN", RefreshToken = "R" }.Save();
        byte[] raw = File.ReadAllBytes(Path.Combine(_dir, "secrets.bin"));
        Assert.DoesNotContain("y0_TOKEN", System.Text.Encoding.UTF8.GetString(raw));
        var s = Secrets.Load();
        Assert.Equal("y0_TOKEN", s.AccessToken);
        Assert.Equal("секрет", s.ClientSecret);
        Assert.True(s.SignedIn);
    }

    [Fact]
    public void Secrets_Garbage_Empty()
    {
        File.WriteAllBytes(Path.Combine(_dir, "secrets.bin"), new byte[] { 1, 2, 3 });
        Assert.False(Secrets.Load().SignedIn);
    }
}
