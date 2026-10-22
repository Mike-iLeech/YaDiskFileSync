using System.IO.Compression;
using System.Text;
using Xunit;

namespace YaDiskFileSync.Tests;

public class SyncEngineTests : IDisposable
{
    private const string R = "disk:/YaDiskSync";
    private readonly string _tmp = Directory.CreateTempSubdirectory("ydfs-eng-").FullName;
    private readonly string _root;
    private readonly FakeDisk _disk = new();
    private readonly FakeTrash _trash;
    private readonly Settings _settings;
    private readonly StateStore _store;
    private static readonly DateTime Old = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    public SyncEngineTests()
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

    private SyncEngine Engine(Func<IDisk?>? disk = null) =>
        new(disk ?? (() => _disk), _settings, () => { }, _store, _trash, () => new DateTime(2026, 10, 22, 12, 0, 0), (_, _) => Task.CompletedTask);

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', '\\'));

    private void Local(string rel, string content, DateTime? mtime = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), content);
        File.SetLastWriteTimeUtc(P(rel), mtime ?? Old);
    }

    private string? LocalText(string rel) => File.Exists(P(rel)) ? File.ReadAllText(P(rel)) : null;
    private static string D(string rel) => R + "/" + rel;

    private async Task<SyncStatus> Pass(SyncEngine e) => await e.RunOnceAsync(default);

    private async Task<SyncStatus> Synced(SyncEngine e)
    {
        var s = await Pass(e);
        Assert.True(s.State == SyncState.Synced, $"{s.State}: {s.Message}");
        return s;
    }

    // ---------- первая синхронизация ----------

    [Fact]
    public async Task FirstSync_MergesBothSides_DeletesNothing()
    {
        Local("только-здесь.txt", "L");
        Local("Отчёты/квартал.txt", "Q");
        Local("одинаковый.txt", "same");
        _disk.PutFile(D("только-там.txt"), "R");
        _disk.PutFile(D("одинаковый.txt"), "same");
        _disk.PutDir(D("Пустая"));

        await Synced(Engine());

        Assert.Equal("L", _disk.Text(D("только-здесь.txt")));
        Assert.Equal("Q", _disk.Text(D("Отчёты/квартал.txt")));
        Assert.Equal("R", LocalText("только-там.txt"));
        Assert.True(Directory.Exists(P("Пустая")));
        Assert.Empty(_disk.Trash);
        Assert.Empty(_trash.Recycled);
        Assert.DoesNotContain(_disk.Log, l => l.Contains("одинаковый"));   // совпадающее не гоняем
    }

    [Fact]
    public async Task SecondPass_NoTransfers()
    {
        Local("a.txt", "1");
        _disk.PutFile(D("b.txt"), "2");
        var e = Engine();
        await Synced(e);
        _disk.Log.Clear();
        await Synced(e);
        Assert.Empty(_disk.Log);
    }

    [Fact]
    public async Task Download_SetsRemoteTime_SoNextPassSeesNoChange()
    {
        _disk.PutFile(D("b.txt"), "2", modified: new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc));
        var e = Engine();
        await Synced(e);
        Assert.Equal(new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(P("b.txt")));
        _disk.Log.Clear();
        await Synced(e);
        Assert.Empty(_disk.Log);
    }

    // ---------- изменения ----------

    [Fact]
    public async Task LocalEdit_Uploaded()
    {
        Local("a.txt", "v1");
        var e = Engine();
        await Synced(e);
        Local("a.txt", "v2", Old.AddMinutes(1));
        await Synced(e);
        Assert.Equal("v2", _disk.Text(D("a.txt")));
    }

    [Fact]
    public async Task RemoteEdit_Downloaded_NoRecycle()
    {
        Local("a.txt", "v1");
        var e = Engine();
        await Synced(e);
        _disk.PutFile(D("a.txt"), "v2");
        await Synced(e);
        Assert.Equal("v2", LocalText("a.txt"));
        Assert.Empty(_trash.Recycled);   // локальная версия была синхронизирована — она и так в истории Диска
    }

    [Fact]
    public async Task Conflict_RemoteNewer_Wins_LocalLoserRecycled()
    {
        Local("a.txt", "v1");
        var e = Engine();
        await Synced(e);
        Local("a.txt", "мои правки", new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc));
        _disk.PutFile(D("a.txt"), "правки с Диска", modified: new DateTime(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc));
        await Synced(e);
        Assert.Equal("правки с Диска", LocalText("a.txt"));
        string recycled = Assert.Single(_trash.Recycled);
        Assert.Equal(P("a.txt"), recycled);
        Assert.Equal("мои правки", File.ReadAllText(Directory.GetFiles(Path.Combine(_tmp, "trash")).Single()));
    }

    [Fact]
    public async Task Conflict_LocalNewer_Wins()
    {
        Local("a.txt", "v1");
        var e = Engine();
        await Synced(e);
        _disk.PutFile(D("a.txt"), "правки с Диска", modified: new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc));
        Local("a.txt", "мои правки", new DateTime(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc));
        await Synced(e);
        Assert.Equal("мои правки", _disk.Text(D("a.txt")));
        Assert.Equal("мои правки", LocalText("a.txt"));
    }

    // ---------- удаления ----------

    [Fact]
    public async Task LocalDelete_GoesToDiskTrash()
    {
        Local("a.txt", "1");
        Local("b.txt", "2");
        Local("c.txt", "3");
        var e = Engine();
        await Synced(e);
        File.Delete(P("a.txt"));
        await Synced(e);
        Assert.False(_disk.Has(D("a.txt")));
        Assert.Contains(FakeDisk.N(D("a.txt")), _disk.Trash);
        Assert.True(_disk.Has(D("b.txt")));
    }

    [Fact]
    public async Task RemoteDelete_GoesToRecycleBin()
    {
        Local("a.txt", "1");
        Local("b.txt", "2");
        Local("c.txt", "3");
        var e = Engine();
        await Synced(e);
        await _disk.DeleteAsync(D("a.txt"), default);
        await Synced(e);
        Assert.False(File.Exists(P("a.txt")));
        Assert.Equal(P("a.txt"), Assert.Single(_trash.Recycled));
    }

    [Fact]
    public async Task RemoteDelete_ButLocalEdited_Reuploaded()
    {
        Local("a.txt", "1");
        Local("b.txt", "2");
        var e = Engine();
        await Synced(e);
        await _disk.DeleteAsync(D("a.txt"), default);
        Local("a.txt", "правка", Old.AddMinutes(5));
        await Synced(e);
        Assert.Equal("правка", _disk.Text(D("a.txt")));
        Assert.Empty(_trash.Recycled);
    }

    [Fact]
    public async Task FolderDeletedLocally_DeletedOnDisk()
    {
        Local("Папка/a.txt", "1");
        Local("b.txt", "2");
        Local("c.txt", "3");
        Local("d.txt", "4");
        var e = Engine();
        await Synced(e);
        Directory.Delete(P("Папка"), true);
        await Synced(e);
        Assert.False(_disk.Has(D("Папка")));
        Assert.Contains(FakeDisk.N(D("Папка")), _disk.Trash);
    }

    [Fact]
    public async Task FolderDeletedOnDisk_RecycledLocally()
    {
        Local("Папка/a.txt", "1");
        Local("b.txt", "2");
        Local("c.txt", "3");
        Local("d.txt", "4");
        var e = Engine();
        await Synced(e);
        await _disk.DeleteAsync(D("Папка"), default);
        await Synced(e);
        Assert.False(Directory.Exists(P("Папка")));
        Assert.Contains(P("Папка"), _trash.Recycled);
    }

    // ---------- защита от массового удаления ----------

    private async Task<SyncEngine> FourFilesSynced()
    {
        foreach (var n in new[] { "a", "b", "c", "d" }) Local(n + ".txt", n);
        var e = Engine();
        await Synced(e);
        return e;
    }

    [Fact]
    public async Task MassLocalDeletion_Paused_DiskUntouched()
    {
        var e = await FourFilesSynced();
        foreach (var n in new[] { "a", "b", "c" }) File.Delete(P(n + ".txt"));
        var s = await Pass(e);
        Assert.Equal(SyncState.NeedsConfirm, s.State);
        Assert.Equal(3, e.Question!.RemoteDeletes);
        Assert.Empty(_disk.Trash);
        // повторный проход без ответа — снова пауза, ничего не удалено
        Assert.Equal(SyncState.NeedsConfirm, (await Pass(e)).State);
        Assert.Empty(_disk.Trash);
    }

    [Fact]
    public async Task MassDeletion_Confirmed_Deletes()
    {
        var e = await FourFilesSynced();
        foreach (var n in new[] { "a", "b", "c" }) File.Delete(P(n + ".txt"));
        await Pass(e);
        e.Resolve(delete: true);
        await Synced(e);
        Assert.Equal(3, _disk.Trash.Count);
        Assert.True(_disk.Has(D("d.txt")));
    }

    [Fact]
    public async Task MassDeletion_Restore_CopiesBack()
    {
        var e = await FourFilesSynced();
        foreach (var n in new[] { "a", "b", "c" }) File.Delete(P(n + ".txt"));
        await Pass(e);
        e.Resolve(delete: false);
        await Synced(e);
        Assert.Empty(_disk.Trash);
        Assert.Equal("a", LocalText("a.txt"));
        Assert.Equal("c", LocalText("c.txt"));
    }

    [Fact]
    public async Task Confirmation_DoesNotCoverNewDeletions()
    {
        var e = await FourFilesSynced();
        foreach (var n in new[] { "a", "b", "c" }) File.Delete(P(n + ".txt"));
        await Pass(e);
        e.Resolve(delete: true);
        File.Delete(P("d.txt"));   // пока думали, пропал ещё один — согласие было не на это
        Assert.Equal(SyncState.NeedsConfirm, (await Pass(e)).State);
        Assert.Empty(_disk.Trash);
    }

    [Fact]
    public async Task RemoteRootDeleted_Paused_LocalFilesKept()
    {
        var e = await FourFilesSynced();
        await _disk.DeleteAsync(R, default);
        var s = await Pass(e);
        Assert.Equal(SyncState.NeedsConfirm, s.State);
        Assert.True(e.Question!.RemoteRootMissing);
        Assert.Empty(_trash.Recycled);
        e.Resolve(delete: false);
        await Synced(e);
        Assert.Equal("a", _disk.Text(D("a.txt")));
        Assert.Empty(_trash.Recycled);
    }

    [Fact]
    public async Task LocalRootMissing_WithHistory_Error_DiskUntouched()
    {
        var e = await FourFilesSynced();
        Directory.Delete(_root, true);
        var s = await Pass(e);
        Assert.Equal(SyncState.Error, s.State);
        Assert.False(Directory.Exists(_root));   // пустую не создаём — иначе всё на Диске «удалено»
        Assert.Empty(_disk.Trash);
    }

    [Fact]
    public async Task LocalRootMissing_FirstTime_Created()
    {
        Directory.Delete(_root, true);
        _disk.PutFile(D("a.txt"), "1");
        await Synced(Engine());
        Assert.Equal("1", LocalText("a.txt"));
    }

    [Fact]
    public async Task ChangingRemoteFolder_StartsFresh_NoDeletions()
    {
        var e = await FourFilesSynced();
        _disk.PutDir("disk:/Другая");
        _disk.PutFile("disk:/Другая/x.txt", "x");
        _settings.RemoteRoot = "disk:/Другая";
        await Synced(e);
        Assert.Equal("x", LocalText("x.txt"));
        Assert.Equal("a", _disk.Text("disk:/Другая/a.txt"));
        Assert.Empty(_trash.Recycled);
        Assert.Empty(_disk.Trash);
    }

    [Fact]
    public async Task OtherAccount_StartsFresh()
    {
        var e = await FourFilesSynced();
        var other = new FakeDisk();
        other.PutDir(R);
        _settings.AccountUid = "uid-2";
        await Synced(Engine(() => other));
        Assert.Equal("a", other.Text(D("a.txt")));
        Assert.Empty(_trash.Recycled);
    }

    // ---------- занятые и служебные файлы ----------

    [Fact]
    public async Task LockedNewFile_Skipped_NotTreatedAsDeleted()
    {
        Local("a.txt", "1");
        Local("b.txt", "2");
        var e = Engine();
        await Synced(e);
        Local("a.txt", "правка", Old.AddMinutes(1));
        _disk.PutFile(D("a.txt"), "с Диска");
        using (new FileStream(P("a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var s = await Pass(e);
            Assert.Equal(SyncState.Retrying, s.State);
            Assert.Contains(e.Rows(), r => r.Rel == "a.txt" && r.State == RowState.Failed);
        }
        Assert.Equal("правка", LocalText("a.txt"));
        Assert.Equal("с Диска", _disk.Text(D("a.txt")));
    }

    [Fact]
    public async Task OfficeTempAndService_NotSynced()
    {
        Local("~$Книга.xlsx", "lock");
        Local("A1B2C3D4", "excel temp");
        Local("x.tmp", "tmp");
        Local("Книга.txt", "ok");
        await Synced(Engine());
        Assert.True(_disk.Has(D("Книга.txt")));
        Assert.False(_disk.Has(D("~$Книга.xlsx")));
        Assert.False(_disk.Has(D("A1B2C3D4")));
        Assert.False(_disk.Has(D("x.tmp")));
        Assert.False(_disk.Has(D(".yadisksync")));
    }

    [Fact]
    public async Task TruncatedExcelBook_NotUploaded()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(z.CreateEntry("xl/sheet.xml").Open())) w.Write(new string('x', 20000));
        byte[] full = ms.ToArray();
        File.WriteAllBytes(P("Книга.xlsx"), full[..(full.Length / 2)]);
        var s = await Pass(Engine());
        Assert.Equal(SyncState.Retrying, s.State);
        Assert.False(_disk.Has(D("Книга.xlsx")));
    }

    // ---------- гонки ----------

    [Fact]
    public async Task FileChangedDuringUpload_UploadedAgainNextPass()
    {
        Local("a.txt", "v1");
        bool once = false;
        _disk.Hook = (op, path) =>
        {
            if (op == "uploaded" && !once) { once = true; Local("a.txt", "v2", Old.AddMinutes(1)); }
            return null;
        };
        var e = Engine();
        await Pass(e);
        Assert.Equal("v1", _disk.Text(D("a.txt")));
        await Synced(e);
        Assert.Equal("v2", _disk.Text(D("a.txt")));
        Assert.Equal("v2", LocalText("a.txt"));   // и ничего не скачали поверх
    }

    [Fact]
    public async Task FileRewrittenInPlaceDuringUpload_LatestEditWins()
    {
        // программа переписывает файл «на месте», пока мы его читаем: на Диск ушло не то, что считали при обходе.
        // В базе должно оказаться то, что реально ушло, — иначе следующий проход увидит ложный конфликт,
        // и более «новая» версия с Диска затрёт последнюю правку
        Local("a.txt", "v1-old");
        var e = Engine();
        await Synced(e);
        Local("a.txt", "v2-new", Old.AddMinutes(1));
        _disk.Hook = (op, _) =>
        {
            if (op == "upload") WriteInPlace("v3-mid");                        // до чтения: уйдёт v3
            if (op == "uploaded") Local("a.txt", "v4-last", Old.AddMinutes(2)); // после: последняя правка
            return null;
        };
        await Pass(e);
        _disk.Hook = null;
        Assert.Equal("v3-mid", _disk.Text(D("a.txt")));
        await Synced(e);
        Assert.Equal("v4-last", _disk.Text(D("a.txt")));
        Assert.Equal("v4-last", LocalText("a.txt"));
        Assert.Empty(_trash.Recycled);
    }

    private void WriteInPlace(string content)
    {
        var t = File.GetLastWriteTimeUtc(P("a.txt"));
        using (var fs = new FileStream(P("a.txt"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            fs.Write(Encoding.UTF8.GetBytes(content));
        File.SetLastWriteTimeUtc(P("a.txt"), t);
    }

    [Fact]
    public async Task HalfWrittenBook_RemoteChanged_NotReplaced()
    {
        // книгу ещё пишут (не дописан zip), а на Диске новая версия: ни скачивать поверх, ни загружать половину нельзя
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(z.CreateEntry("xl/sheet.xml").Open())) w.Write(new string('x', 20000));
        byte[] full = ms.ToArray();
        File.WriteAllBytes(P("Книга.xlsx"), full);
        File.SetLastWriteTimeUtc(P("Книга.xlsx"), Old);
        var e = Engine();
        await Synced(e);
        File.WriteAllBytes(P("Книга.xlsx"), full[..(full.Length / 2)]);
        File.SetLastWriteTimeUtc(P("Книга.xlsx"), Old.AddMinutes(1));
        _disk.PutFile(D("Книга.xlsx"), "версия с Диска");
        Assert.Equal(SyncState.Retrying, (await Pass(e)).State);
        Assert.Equal(full.Length / 2, new FileInfo(P("Книга.xlsx")).Length);
        Assert.Equal("версия с Диска", _disk.Text(D("Книга.xlsx")));
        Assert.Empty(_trash.Recycled);
    }

    [Fact]
    public async Task LocalEditDuringDownload_NotOverwritten()
    {
        Local("a.txt", "v1");
        var e = Engine();
        await Synced(e);
        _disk.PutFile(D("a.txt"), "с Диска");
        _disk.Hook = (op, _) =>
        {
            if (op == "download") Local("a.txt", "свежая правка", Old.AddMinutes(30));
            return null;
        };
        await Pass(e);
        Assert.Equal("свежая правка", LocalText("a.txt"));
        Assert.Empty(_trash.Recycled);
    }

    [Fact]
    public async Task NetworkDropMidPass_DoneWorkKept()
    {
        Local("a.txt", "1");
        Local("b.txt", "2");
        _disk.Hook = (op, path) => op == "upload" && path.EndsWith("b.txt") ? new DiskException("нет сети", DiskErrorKind.Transient) : null;
        var e = Engine();
        Assert.Equal(SyncState.Retrying, (await Pass(e)).State);
        Assert.Equal("1", _disk.Text(D("a.txt")));
        _disk.Hook = null;
        _disk.Log.Clear();
        await Synced(e);
        Assert.Equal(new[] { "upload " + FakeDisk.N(D("b.txt")) }, _disk.Log);
    }

    [Fact]
    public async Task FatalOnOneFile_OthersStillSynced()
    {
        Local("big.bin", "огромный");
        Local("small.txt", "ok");
        _disk.Hook = (op, path) => op == "upload" && path.EndsWith("big.bin")
            ? new DiskException("Файл слишком большой для вашего тарифа Яндекс.Диска.", DiskErrorKind.Fatal) : null;
        var s = await Pass(Engine());
        Assert.Equal(SyncState.Error, s.State);
        Assert.Contains("big.bin", s.Message);
        Assert.Equal("ok", _disk.Text(D("small.txt")));
    }

    [Fact]
    public async Task AuthError_NeedLogin()
    {
        Local("a.txt", "1");
        _disk.Hook = (op, _) => op == "list" ? new DiskException("войдите", DiskErrorKind.Auth) : null;
        Assert.Equal(SyncState.NeedLogin, (await Pass(Engine())).State);
    }

    [Fact]
    public async Task SignedOut_FilesStayOnComputer()
    {
        var e = await FourFilesSynced();
        var signedOut = Engine(() => null);
        Assert.Equal(SyncState.NeedLogin, (await Pass(signedOut)).State);
        Assert.Equal("a", LocalText("a.txt"));
        Assert.Empty(_trash.Recycled);
    }

    [Fact]
    public async Task NotConfigured_OrWholeDisk_Refused()
    {
        _settings.RemoteRoot = "disk:/";
        Assert.Equal(SyncState.NotConfigured, (await Pass(Engine())).State);
        _settings.RemoteRoot = null;
        Assert.Equal(SyncState.NotConfigured, (await Pass(Engine())).State);
    }

    [Fact]
    public async Task AccountUidFetched_WhenUnknown()
    {
        _settings.AccountUid = null;
        await Synced(Engine());
        Assert.Equal("uid-1", _settings.AccountUid);
        Assert.Equal("mike", _settings.AccountLogin);
    }

    [Fact]
    public async Task StatusRows_ShowSyncedFiles()
    {
        Local("a.txt", "12345");
        var e = Engine();
        await Synced(e);
        var row = Assert.Single(e.Rows(), r => r.Rel == "a.txt");
        Assert.Equal(RowState.Done, row.State);
        Assert.Equal(5, row.Total);
        Assert.Equal(5, row.Done);
        await Synced(e);
        Assert.Equal(RowState.Synced, Assert.Single(e.Rows(), r => r.Rel == "a.txt").State);
    }

    [Fact]
    public async Task Loop_RetriesWithBackoff_ThenSucceeds()
    {
        Local("a.txt", "1");
        int lists = 0;
        _disk.Hook = (op, _) => op == "list" && ++lists == 1 ? new DiskException("нет сети", DiskErrorKind.Transient) : null;
        var synced = new TaskCompletionSource();
        using var e = Engine();
        e.StatusChanged += s => { if (s.State == SyncState.Synced) synced.TrySetResult(); };
        e.Start();
        e.Request();
        await synced.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("1", _disk.Text(D("a.txt")));
    }
}
