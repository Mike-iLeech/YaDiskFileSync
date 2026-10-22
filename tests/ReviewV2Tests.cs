using System.Net;
using System.Text;
using Xunit;

namespace YaDiskFileSync.Tests;

/// <summary>Сценарии из независимого ревью 2.0.0 (VERDICT: FAIL — H1–H3, M1–M5, L3).</summary>
public class ReviewV2Tests : IDisposable
{
    private const string R = "disk:/YaDiskSync";
    private readonly string _tmp = Directory.CreateTempSubdirectory("ydfs-r2-").FullName;
    private readonly string _root;
    private readonly FakeDisk _disk = new();
    private readonly FakeTrash _trash;
    private readonly Settings _settings;
    private readonly StateStore _store;
    private static readonly DateTime Old = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    public ReviewV2Tests()
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

    private SyncEngine Engine(Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(() => _disk, _settings, () => { }, _store, _trash, () => new DateTime(2026, 10, 22, 12, 0, 0), delay ?? ((_, _) => Task.CompletedTask));

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

    // ---------- H1: пробелы в именах ----------

    [Fact]
    public async Task H1_LeadingSpaceName_IsSeparateFile()
    {
        Local("a.txt", "IMPORTANT");
        var e = Engine();
        await Synced(e);
        Local(" a.txt", "other", Old.AddMinutes(1));
        await Synced(e);
        await Synced(e);
        Assert.Equal("IMPORTANT", _disk.Text(D("a.txt")));
        Assert.Equal("other", _disk.Text(D(" a.txt")));
        Assert.Equal("IMPORTANT", LocalText("a.txt"));
        Assert.Equal("other", LocalText(" a.txt"));
        Assert.Empty(_trash.Recycled);
    }

    // ---------- H2: регистр букв и горячий цикл ----------

    [Fact]
    public async Task H2_CaseDiffers_FirstSync_DownloadsByRealRemoteName()
    {
        Local("Отчёт.txt", "мой", Old);
        _disk.PutFile(D("отчёт.txt"), "с Диска, новее", modified: new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc));
        var e = Engine();
        await Synced(e);
        Assert.Equal("с Диска, новее", LocalText("Отчёт.txt"));
        Assert.False(_disk.Has(D("Отчёт.txt")));   // второй копии с другим регистром не появилось
        await Synced(e);
    }

    [Fact]
    public async Task H2_CaseDiffers_UploadOverwritesRealRemoteName()
    {
        _disk.PutFile(D("отчёт.txt"), "старый с Диска", modified: Old);
        Local("Отчёт.txt", "мой, новее", new DateTime(2026, 10, 22, 11, 0, 0, DateTimeKind.Utc));
        await Synced(Engine());
        Assert.Equal("мой, новее", _disk.Text(D("отчёт.txt")));
        Assert.False(_disk.Has(D("Отчёт.txt")));
    }

    [Fact]
    public async Task H2_NewFileInFolderWithOtherCase_GoesIntoExistingRemoteFolder()
    {
        _disk.PutDir(D("Папка"));
        Local("папка/новый.txt", "x");
        await Synced(Engine());
        Assert.Equal("x", _disk.Text(D("Папка/новый.txt")));
        Assert.False(_disk.Has(D("папка")));
    }

    [Fact]
    public async Task H2_PersistentNotFound_NoHotLoop_NotSynced()
    {
        _disk.PutFile(D("a.txt"), "x");
        _disk.Hook = (op, _) => op == "download" ? new DiskException("нет", DiskErrorKind.Transient, new DiskClient.HttpNotFound()) : null;
        var old = SyncEngine.SoonDelay;
        SyncEngine.SoonDelay = TimeSpan.FromMilliseconds(20);
        try
        {
            var states = new List<SyncState>();
            using var e = Engine(Task.Delay);
            e.StatusChanged += s => { lock (states) states.Add(s.State); };
            e.Start();
            e.Request();
            await Task.Delay(1500);
            Assert.InRange(_disk.Lists, 2, SyncEngine.MaxAgain + 2);   // несколько коротких повторов, потом паузы Backoff
            lock (states)
            {
                Assert.DoesNotContain(SyncState.Synced, states);
                Assert.Equal(SyncState.Retrying, states.Last(s => s != SyncState.Syncing));
            }
        }
        finally { SyncEngine.SoonDelay = old; }
    }

    // ---------- H3: «8 цифр» и папки с несинхронизируемым ----------

    [Fact]
    public async Task H3_DateNamedFolderAndOldFile_Synced()
    {
        Local("2024/20241231/акт.txt", "акт");
        Local("20241231", "файл без расширения", Old);
        await Synced(Engine());
        Assert.Equal("акт", _disk.Text(D("2024/20241231/акт.txt")));
        Assert.Equal("файл без расширения", _disk.Text(D("20241231")));
    }

    [Fact]
    public async Task H3_FreshExcelTempFile_Deferred()
    {
        Local("A1B2C3D4", "excel temp", DateTime.UtcNow);
        Local("Книга.txt", "ok");
        await Synced(Engine());
        Assert.False(_disk.Has(D("A1B2C3D4")));
        Assert.True(_disk.Has(D("Книга.txt")));
    }

    [Fact]
    public async Task H3_FolderDeletedOnDisk_WithUnsyncedLocalContent_Kept_NoLoop()
    {
        Local("d/a.txt", "1");
        Local("b.txt", "2");
        Local("c.txt", "3");
        Local("e.txt", "4");
        var e = Engine();
        await Synced(e);
        Local("d/черновик.tmp", "только на ПК");
        await _disk.DeleteAsync(D("d"), default);
        await Synced(e);
        Assert.Equal("только на ПК", LocalText("d/черновик.tmp"));
        Assert.DoesNotContain(_trash.Recycled, p => p.EndsWith("\\d"));
        await Synced(e);   // папка оставлена — проход завершается, а не крутится
    }

    [Fact]
    public async Task H3_FolderDeletedLocally_WithUnsyncedRemoteContent_NotTrashed()
    {
        Local("d/a.txt", "1");
        Local("b.txt", "2");
        Local("c.txt", "3");
        Local("e.txt", "4");
        var e = Engine();
        await Synced(e);
        _disk.PutFile(D("d/~$a.txt"), "чужая блокировка Office");
        Directory.Delete(P("d"), true);
        await Synced(e);
        Assert.True(_disk.Has(D("d/~$a.txt")));
        Assert.DoesNotContain(FakeDisk.N(D("d")), _disk.Trash);
    }

    [Fact]
    public async Task RemoteNameInvalidOnWindows_Skipped_Reported()
    {
        _disk.PutFile(D("отчёт: итог.txt"), "x");
        _disk.PutFile(D("хвост. "), "y");
        _disk.PutFile(D("норм.txt"), "z");
        var e = Engine();
        var s = await Synced(e);
        Assert.Contains("пропущено: 2", s.Message);
        Assert.Equal("z", LocalText("норм.txt"));
        Assert.Contains(e.Rows(), r => r.Rel == "отчёт: итог.txt" && r.State == RowState.Failed);
    }

    [Theory]
    [InlineData("a:b.txt", true)]
    [InlineData("хвост.", true)]
    [InlineData("пробел ", true)]
    [InlineData("CON.txt", true)]
    [InlineData("папка/nul", true)]
    [InlineData(" начало.txt", false)]
    [InlineData("обычный.xlsx", false)]
    public void WindowsCannot(string rel, bool expected) => Assert.Equal(expected, Ignore.WindowsCannot(rel));

    // ---------- M1: Диск применяет загрузку не сразу ----------

    [Fact]
    public async Task M1_StaleListingAfterUpload_DoesNotRevertLocalEdit()
    {
        Local("a.txt", "v1");
        var e = Engine();
        await Synced(e);
        _disk.DelayApply = true;
        Local("a.txt", "v2", Old.AddMinutes(1));
        await Synced(e);                // загрузили v2, а Диск ещё показывает v1
        await Synced(e);                // v1 в списке — не «изменение на Диске»
        Assert.Equal("v2", LocalText("a.txt"));
        Assert.Empty(_trash.Recycled);
        _disk.DelayApply = false;
        _disk.ApplyPending();
        _disk.Log.Clear();
        await Synced(e);
        Assert.Empty(_disk.Log);
    }

    // ---------- M3: ответ относится к показанному вопросу ----------

    [Fact]
    public async Task M3_AnswerToOldQuestion_DoesNotCoverNewDeletions()
    {
        foreach (var n in new[] { "a", "b", "c", "d", "f" }) Local(n + ".txt", n);
        var e = Engine();
        await Synced(e);
        foreach (var n in new[] { "a", "b", "c" }) File.Delete(P(n + ".txt"));
        await e.RunOnceAsync(default);
        var shown = e.Question!;
        File.Delete(P("d.txt"));
        await e.RunOnceAsync(default);   // пока окно было открыто, опрос обновил вопрос
        e.Resolve(shown, delete: true);
        Assert.Equal(SyncState.NeedsConfirm, (await e.RunOnceAsync(default)).State);
        Assert.Empty(_disk.Trash);
    }

    // ---------- M4: смена входа посреди прохода ----------

    [Fact]
    public async Task M4_PinnedTokens_RefuseAfterRelogin()
    {
        var s = new Secrets { AccessToken = "A", Scope = Secrets.CurrentScope };
        var pinned = new PinnedTokens(new FixedToken("A"), s);
        Assert.Equal("A", await pinned.GetAsync(false, default));
        lock (s.Sync) { s.Generation++; s.AccessToken = "B"; }
        var ex = await Assert.ThrowsAsync<DiskException>(() => pinned.GetAsync(false, default));
        Assert.Equal(DiskErrorKind.Transient, ex.Kind);
    }

    // ---------- M5: проигравшая версия на Диске — в Корзину Диска ----------

    [Fact]
    public async Task M5_Conflict_LocalNewer_RemoteLoserToDiskTrash()
    {
        Local("a.txt", "v1");
        var e = Engine();
        await Synced(e);
        _disk.PutFile(D("a.txt"), "правки с Диска", modified: new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc));
        Local("a.txt", "мои правки", new DateTime(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc));
        await Synced(e);
        Assert.Equal("мои правки", _disk.Text(D("a.txt")));
        Assert.Contains(FakeDisk.N(D("a.txt")), _disk.Trash);
    }

    // ---------- L3: файл вернулся на компьютер, пока шёл проход ----------

    [Fact]
    public async Task L3_FileReappearedLocally_NotDeletedOnDisk()
    {
        Local("a.txt", "1");
        Local("b.txt", "2");
        Local("c.txt", "3");
        var e = Engine();
        await Synced(e);
        File.Delete(P("a.txt"));
        Local("новый.txt", "n");
        // Excel сохраняет через удаление и переименование: пока грузили другой файл, a.txt вернулся
        _disk.Hook = (op, _) => { if (op == "upload") Local("a.txt", "1-новая", Old.AddMinutes(3)); return null; };
        await e.RunOnceAsync(default);
        _disk.Hook = null;
        Assert.True(_disk.Has(D("a.txt")));
        Assert.Empty(_disk.Trash);
        await Synced(e);
        Assert.Equal("1-новая", _disk.Text(D("a.txt")));
    }

    // ---------- L1: ответ Диска без sha256 ----------

    [Fact]
    public async Task L1_RemoteWithoutSha_DownloadedOnce()
    {
        _disk.PutFile(D("a.txt"), "без хеша");
        _disk.Items[FakeDisk.N(D("a.txt"))].OmitSha = true;
        var e = Engine();
        await Synced(e);
        Assert.Equal("без хеша", LocalText("a.txt"));
        _disk.Log.Clear();
        await Synced(e);
        Assert.Empty(_disk.Log);
    }
}

/// <summary>M2: неполный список Диска — ошибка, а не «там пусто».</summary>
public class ListTreeStrictTests
{
    private static DiskClient Client(FakeHttp http) => new(http.Client(), new FixedToken());

    [Fact]
    public async Task SubfolderVanishedDuringListing_Throws()
    {
        var http = new FakeHttp()
            .Reply(HttpStatusCode.OK, """{"type":"dir","_embedded":{"total":1,"items":[{"name":"sub","type":"dir"}]}}""")
            .Reply(HttpStatusCode.NotFound, """{"error":"DiskNotFoundError"}""");
        var e = await Assert.ThrowsAsync<DiskException>(() => Client(http).ListTreeAsync("disk:/R", default));
        Assert.Equal(DiskErrorKind.Transient, e.Kind);
    }

    [Fact]
    public async Task MissingEmbedded_Throws()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"type":"dir"}""");
        await Assert.ThrowsAsync<DiskException>(() => Client(http).ListTreeAsync("disk:/R", default));
    }

    [Fact]
    public async Task TotalMismatch_Throws()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK,
            """{"type":"dir","_embedded":{"total":3,"items":[{"name":"a","type":"file","size":1,"sha256":"aa"}]}}""");
        await Assert.ThrowsAsync<DiskException>(() => Client(http).ListTreeAsync("disk:/R", default));
    }

    [Fact]
    public async Task Pages_SortedByName_AllCollected()
    {
        string Page(int from, int count, int total) => "{\"type\":\"dir\",\"_embedded\":{\"total\":" + total + ",\"items\":[" +
            string.Join(",", Enumerable.Range(from, count).Select(i => $"{{\"name\":\"f{i:D5}\",\"type\":\"file\",\"size\":1,\"sha256\":\"aa\"}}")) + "]}}";
        var http = new FakeHttp().Reply(HttpStatusCode.OK, Page(0, 1000, 1003)).Reply(HttpStatusCode.OK, Page(1000, 3, 1003));
        var list = (await Client(http).ListTreeAsync("disk:/R", default))!;
        Assert.Equal(1003, list.Count);
        Assert.Contains("offset=1000", http.Requests[1].Url);
        Assert.Contains("sort=name", http.Requests[0].Url);
    }

    [Fact]
    public async Task NameWithSpaces_PassedAsIs()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"href":"https://downloader.disk.yandex.ru/x"}""")
            .ReplyBytes(HttpStatusCode.OK, Encoding.UTF8.GetBytes("1"));
        await Client(http).DownloadAsync("disk:/R/ a.txt", new MemoryStream(), null, default);
        Assert.Contains("path=disk%3A%2FR%2F%20a.txt", http.Requests[0].Url);
    }
}
