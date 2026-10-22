using Xunit;

namespace YaDiskFileSync.Tests;

public class UpdateNotesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("btqc-notes-").FullName;
    private readonly string _savedPath = Updater.NotesFile;

    public UpdateNotesTests() => Updater.NotesFile = Path.Combine(_dir, "update-notes.txt");

    public void Dispose()
    {
        Updater.NotesFile = _savedPath;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ReleaseInfo R(string ver, string notes) =>
        new(Version.Parse(ver), "v" + ver, $"https://github.com/Mike-iLeech/YaDiskFileSync/releases/tag/v{ver}", "", 0, null, notes);

    [Fact]
    public void ParseRelease_TakesBody()
    {
        var r = Updater.ParseRelease("""
            {"tag_name":"v1.0.1","html_url":"https://x/r","body":"- Уведомление\r\n- Раз в час",
             "assets":[{"name":"YaDiskFileSync.exe","browser_download_url":"https://x/e","size":1}]}
            """);
        Assert.Equal("- Уведомление\r\n- Раз в час", r!.Notes);
    }

    [Fact]
    public void Notes_RoundTrip_ForThisVersion_AndFileRemoved()
    {
        Updater.SaveNotes(R("1.0.1", "- Уведомление\n- Раз в час"));
        var n = Updater.TakeNotes(new Version(1, 0, 1, 0));
        Assert.NotNull(n);
        Assert.Equal("- Уведомление\n- Раз в час", n!.Value.Notes);
        Assert.EndsWith("/releases/tag/v1.0.1", n.Value.PageUrl);
        Assert.False(File.Exists(Updater.NotesFile));
        Assert.Null(Updater.TakeNotes(new Version(1, 0, 1, 0)));   // второй раз не показываем
    }

    [Fact]
    public void Notes_ForOtherVersion_Ignored_AndRemoved()
    {
        // откат: заметки к 1.0.2, а запустилась 1.0.1
        Updater.SaveNotes(R("1.0.2", "- что-то"));
        Assert.Null(Updater.TakeNotes(new Version(1, 0, 1, 0)));
        Assert.False(File.Exists(Updater.NotesFile));
    }

    [Fact]
    public void NoNotesFile_Null() => Assert.Null(Updater.TakeNotes(new Version(1, 0, 1, 0)));

    [Fact]
    public void BalloonText_FromChangelogMarkdown()
    {
        string text = Updater.BalloonText("## 1.0.1\r\n\r\n- **Уведомление** о `обновлении`\r\n- Раз в час\r\n");
        Assert.Equal("• Уведомление о обновлении\n• Раз в час", text);
    }

    [Fact]
    public void BalloonText_Truncated()
    {
        string text = Updater.BalloonText("- " + new string('я', 500), max: 50);
        Assert.Equal(50, text.Length);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void BalloonText_Empty() => Assert.Equal("", Updater.BalloonText(""));
}
