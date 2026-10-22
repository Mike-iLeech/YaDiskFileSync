using System.Diagnostics;
using Xunit;

namespace YaDiskFileSync.Tests;

/// <summary>Сценарии второго раунда независимого ревью 2.0.0 (HIGH-1, M-1…M-6, L-3, L-6).</summary>
public class ReviewV2Round2Tests : IDisposable
{
    private const string R = "disk:/YaDiskSync";
    private readonly string _tmp = Directory.CreateTempSubdirectory("ydfs-r3-").FullName;
    private readonly string _root;
    private readonly FakeDisk _disk = new();
    private readonly FakeTrash _trash;
    private readonly Settings _settings;
    private readonly StateStore _store;
    private static readonly DateTime Old = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T0 = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);

    public ReviewV2Round2Tests()
    {
        _root = Path.Combine(_tmp, "YaDiskSync");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_tmp, "trash"));
        _trash = new FakeTrash(Path.Combine(_tmp, "trash"));
        _settings = new Settings { LocalRoot = _root, RemoteRoot = R, AccountUid = "uid-1" };
        _store = new StateStore(Path.Combine(_tmp, "state.json"));
        _disk.PutDir(R);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private SyncEngine Engine() =>
        new(() => _disk, _settings, () => { }, _store, _trash, () => new DateTime(2026, 10, 22, 12, 0, 0), (_, _) => Task.CompletedTask);

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', '\\'));

    private void Local(string rel, string content, DateTime? mtime = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), content);
        File.SetLastWriteTimeUtc(P(rel), mtime ?? Old);
    }

    private string? LocalText(string rel) => File.Exists(P(rel)) ? File.ReadAllText(P(rel)) : null;
    private static string D(string rel) => R + "/" + rel;

    private static async Task<SyncStatus> Synced(SyncEngine e)
    {
        var s = await e.RunOnceAsync(default);
        Assert.True(s.State == SyncState.Synced, $"{s.State}: {s.Message}");
        return s;
    }

    // ---------- HIGH-1: новый файл, загрузка принята (202), а Диск его ещё не показывает ----------

    [Fact]
    public async Task High1_NewFile_NotYetOnDisk_NotRecycled()
    {
        Local("старый.txt", "1");
        var e = Engine();
        await Synced(e);
        _disk.DelayApply = true;
        Local("новый.txt", "только что создан");
        await Synced(e);
        await Synced(e);   // Диск ещё не показывает файл — это не «удалён на Диске»
        Assert.Equal("только что создан", LocalText("новый.txt"));
        Assert.Empty(_trash.Recycled);
        _disk.DelayApply = false;
        _disk.ApplyPending();
        _disk.Log.Clear();
        await Synced(e);
        Assert.Empty(_disk.Log);
    }

    [Fact]
    public async Task High1_ManyNewFiles_NoDeletionQuestion()
    {
        Local("старый.txt", "1");
        var e = Engine();
        await Synced(e);
        _disk.DelayApply = true;
        foreach (var n in new[] { "a", "b", "c", "d", "f" }) Local(n + ".txt", n);
        await Synced(e);
        var s = await Synced(e);
        Assert.Null(e.Question);
        Assert.Empty(_trash.Recycled);
        _ = s;
    }

    // ---------- M-1: Диск так и не применил загрузку ----------

    private static Dictionary<string, BaseEntry> PendingBase(string? prev, DateTime until) => new(Planner.Paths)
    {
        ["f"] = new BaseEntry { Sha256 = "NEW", Size = 1, LocalMtime = T0, PrevRemoteSha = prev, PendingUntil = until },
    };

    private static Dictionary<string, LocalItem> L() => new(Planner.Paths) { ["f"] = new LocalItem("f", false, 1, T0, "NEW") };
    private static Dictionary<string, RemoteItem> Rm(string? sha) => sha == null
        ? new(Planner.Paths) : new(Planner.Paths) { ["f"] = new RemoteItem("f", false, 1, sha, T0.AddMinutes(-30)) };

    [Theory]
    [InlineData("OLD", "OLD")]   // изменённый файл: на Диске всё ещё прежняя версия
    [InlineData(null, null)]     // новый файл: на Диске его всё ещё нет
    public void M1_SettleExpired_ReuploadsInsteadOfOverwritingOrDeleting(string? prev, string? remote)
    {
        var within = Planner.Plan(PendingBase(prev, T0.AddMinutes(10)), L(), Rm(remote), T0);
        Assert.Empty(within.Ops);
        var expired = Planner.Plan(PendingBase(prev, T0.AddMinutes(-1)), L(), Rm(remote), T0);
        var op = Assert.Single(expired.Ops);
        Assert.Equal(OpKind.Upload, op.Kind);
    }

    [Fact]
    public void M1_Pending_RemoteShowsSent_RecordClearsPending()
    {
        var plan = Planner.Plan(PendingBase("OLD", T0.AddMinutes(10)), L(), Rm("NEW"), T0);
        Assert.Equal(OpKind.Record, Assert.Single(plan.Ops).Kind);
    }

    [Fact]
    public void M1_Pending_ButChangedOnDiskByOthers_IsConflict()
    {
        var plan = Planner.Plan(PendingBase("OLD", T0.AddMinutes(10)), L(), Rm("THIRD"), T0);
        Assert.Equal(OpKind.Download, Assert.Single(plan.Ops).Kind);   // Диск новее — проигравшая локальная в Корзину
        Assert.True(plan.Ops[0].ReplacesLocalChanges);
    }

    // ---------- M-2: другой регистр папки на Диске — другая папка ----------

    [Fact]
    public void M2_RemoteRootCase_DifferentBase() =>
        Assert.NotEqual(StateStore.KeyFor("u", @"C:\YaDiskSync", "disk:/YaDiskSync"), StateStore.KeyFor("u", @"C:\YaDiskSync", "disk:/yadisksync"));

    [Fact]
    public async Task M2_SwitchToOtherCaseFolder_NothingRecycled()
    {
        foreach (var n in new[] { "a", "b", "c", "d", "f" }) Local(n + ".txt", n);
        await Synced(Engine());
        _disk.PutDir("disk:/yadisksync");
        _disk.PutFile("disk:/yadisksync/a.txt", "a");
        _settings.RemoteRoot = "disk:/yadisksync";
        await Synced(Engine());
        Assert.Empty(_trash.Recycled);
        Assert.Equal("d", _disk.Text("disk:/yadisksync/d.txt"));
    }

    // ---------- M-3: здесь папка, там файл — остальное синхронизируется ----------

    [Fact]
    public async Task M3_FolderVsFile_InsideSkipped_RestContinues()
    {
        Local("Docs/a.txt", "внутри");
        Local("zz.txt", "после");
        _disk.PutFile(D("Docs"), "файл с тем же именем");
        var e = Engine();
        var s = await Synced(e);
        Assert.Contains("пропущено", s.Message);
        Assert.Equal("после", _disk.Text(D("zz.txt")));
        Assert.Equal("файл с тем же именем", _disk.Text(D("Docs")));
        Assert.Equal("внутри", LocalText("Docs/a.txt"));
        Assert.Contains(e.Rows(), r => r.Rel == "Docs/a.txt" && r.State == RowState.Failed);
    }

    // ---------- M-4: старый пустой или битый документ Office ----------

    [Fact]
    public async Task M4_OldEmptyDocx_SyncedAsIs()
    {
        File.WriteAllBytes(P("Новый документ.docx"), Array.Empty<byte>());
        File.SetLastWriteTimeUtc(P("Новый документ.docx"), Old);
        await Synced(Engine());
        Assert.True(_disk.Has(D("Новый документ.docx")));
    }

    // ---------- M-5: папку из базы заменили ссылкой ----------

    [Fact]
    public async Task M5_FolderReplacedByJunction_DiskCopyKept()
    {
        Local("d/a.txt", "1");
        Local("b.txt", "2");
        Local("c.txt", "3");
        Local("e.txt", "4");
        var e = Engine();
        await Synced(e);
        string elsewhere = Path.Combine(_tmp, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.Delete(P("d"), true);
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{P("d")}\" \"{elsewhere}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mk.WaitForExit();
        Assert.True((File.GetAttributes(P("d")) & FileAttributes.ReparsePoint) != 0, "точка соединения не создалась");
        await Synced(e);
        Assert.Equal("1", _disk.Text(D("d/a.txt")));
        Assert.Empty(_disk.Trash);
    }

    // ---------- L-3: Excel посреди сохранения ----------

    [Fact]
    public async Task L3_ExcelMidSave_BookOnDiskNotTrashed()
    {
        Local("Книга.xlsx", "книга");
        Local("b.txt", "2");
        Local("c.txt", "3");
        var e = Engine();
        await Synced(e);
        File.Delete(P("Книга.xlsx"));                         // Excel убрал старую книгу…
        Local("5F3A2B10", "новая версия", DateTime.UtcNow);   // …и ещё не переименовал временный файл
        await e.RunOnceAsync(default);
        Assert.True(_disk.Has(D("Книга.xlsx")));
        Assert.Empty(_disk.Trash);
    }

    // ---------- L-6, M-6 ----------

    [Fact]
    public void L6_FolderNamePartsKeepSpaces() => Assert.Equal("disk:/Папка /x", DiskPath.Normalize(" /Папка /x/ "));

    [Fact]
    public void M6_Version1FilePath_Remembered()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<Settings>("""{"FilePath":"C:\\a.xlsx","ClientId":"cid"}""")!;
        Assert.Equal(@"C:\a.xlsx", s.LegacyFilePath);
    }
}
