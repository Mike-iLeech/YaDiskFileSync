using Xunit;

namespace YaDiskFileSync.Tests;

/// <summary>
/// Полная матрица трёхсторонней сверки для одного файла: база ∈ {нет, X} × компьютер ∈ {нет, X, A} ×
/// Диск ∈ {нет, X, A, B}. X — содержимое базы, A и B — разные новые версии.
/// </summary>
public class PlannerTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
    private const string F = "Бухгалтерия.xlsx";

    private static Dictionary<string, BaseEntry> Base(string? sha) => sha == null
        ? new(Planner.Paths)
        : new(Planner.Paths) { [F] = new BaseEntry { Sha256 = sha, Size = 1, LocalMtime = T0, RemoteModified = T0 } };

    private static Dictionary<string, LocalItem> Local(string? sha, DateTime? mtime = null) => sha == null
        ? new(Planner.Paths)
        : new(Planner.Paths) { [F] = new LocalItem(F, false, 1, mtime ?? (sha == "X" ? T0 : T0.AddMinutes(5)), sha) };

    private static Dictionary<string, RemoteItem> Remote(string? sha, DateTime? mod = null) => sha == null
        ? new(Planner.Paths)
        : new(Planner.Paths) { [F] = new RemoteItem(F, false, 1, sha, mod ?? (sha == "X" ? T0 : T0.AddMinutes(5))) };

    private static SyncOp? One(string? b, string? l, string? r, DateTime? lm = null, DateTime? rm = null)
    {
        var plan = Planner.Plan(Base(b), Local(l, lm), Remote(r, rm));
        return Assert.Single(plan.Ops.DefaultIfEmpty()) is { } op ? op : null;
    }

    [Theory]
    // нет базы — первая встреча
    [InlineData(null, "A", null, "Upload")]
    [InlineData(null, null, "B", "Download")]
    [InlineData(null, "A", "A", "Record")]
    // база есть
    [InlineData("X", null, null, "Forget")]
    [InlineData("X", "X", null, "DeleteLocal")]        // удалён на Диске → в Корзину Windows
    [InlineData("X", "A", null, "Upload")]             // удалён на Диске, но изменён здесь → изменение важнее
    [InlineData("X", null, "X", "DeleteRemote")]       // удалён здесь → в Корзину Диска
    [InlineData("X", null, "B", "Download")]           // удалён здесь, но изменён на Диске → изменение важнее
    [InlineData("X", "X", "X", null)]                  // ничего не делать
    [InlineData("X", "A", "X", "Upload")]
    [InlineData("X", "X", "B", "Download")]
    [InlineData("X", "A", "A", "Record")]              // одинаково изменили с обеих сторон
    public void Matrix(string? b, string? l, string? r, string? expected)
    {
        var op = One(b, l, r);
        Assert.Equal(expected, op?.Kind.ToString());
        Assert.False(op?.ReplacesLocalChanges ?? false);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("X")]
    public void Conflict_LocalNewer_Uploads(string? b)
    {
        var op = One(b, "A", "B", lm: T0.AddMinutes(10), rm: T0.AddMinutes(5))!;
        Assert.Equal(OpKind.Upload, op.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("X")]
    public void Conflict_RemoteNewer_Downloads_AndRecyclesLocal(string? b)
    {
        var op = One(b, "A", "B", lm: T0.AddMinutes(5), rm: T0.AddMinutes(10))!;
        Assert.Equal(OpKind.Download, op.Kind);
        Assert.True(op.ReplacesLocalChanges);   // проигравшая локальная версия — в Корзину, а не в никуда
    }

    [Fact]
    public void Conflict_SameTime_LocalWins()
    {
        var op = One("X", "A", "B", lm: T0.AddMinutes(5), rm: T0.AddMinutes(5))!;
        Assert.Equal(OpKind.Upload, op.Kind);
    }

    [Fact]
    public void Touched_SameContent_OnlyRecordsNewTime()
    {
        var plan = Planner.Plan(Base("X"), Local("X", T0.AddHours(1)), Remote("X"));
        Assert.Equal(OpKind.Record, Assert.Single(plan.Ops).Kind);
    }

    [Fact]
    public void RemoteWithoutSha_SameSizeAndTime_Unchanged()
    {
        var plan = Planner.Plan(Base("X"), Local("X"), new Dictionary<string, RemoteItem>(Planner.Paths) { [F] = new RemoteItem(F, false, 1, null, T0) });
        Assert.Empty(plan.Ops);
    }

    [Fact]
    public void RemoteWithoutSha_NewTime_Downloads()
    {
        var plan = Planner.Plan(Base("X"), Local("X"), new Dictionary<string, RemoteItem>(Planner.Paths) { [F] = new RemoteItem(F, false, 1, null, T0.AddMinutes(1)) });
        Assert.Equal(OpKind.Download, Assert.Single(plan.Ops).Kind);
    }

    [Fact]
    public void CaseInsensitive_SameFile()
    {
        var plan = Planner.Plan(Base("X"),
            new Dictionary<string, LocalItem>(Planner.Paths) { ["бухгалтерия.XLSX"] = new LocalItem("бухгалтерия.XLSX", false, 1, T0, "X") },
            Remote("X"));
        Assert.Empty(plan.Ops);
    }

    [Fact]
    public void FileVsDir_Skipped_NothingDeleted()
    {
        var plan = Planner.Plan(Base("X"),
            new Dictionary<string, LocalItem>(Planner.Paths) { [F] = new LocalItem(F, true, 0, default, null) },
            Remote("X"));
        Assert.Equal(OpKind.Skip, Assert.Single(plan.Ops).Kind);
    }

    // ---------- папки ----------

    private static Dictionary<string, LocalItem> L(params (string Rel, bool Dir, string? Sha)[] items) =>
        items.ToDictionary(i => i.Rel, i => new LocalItem(i.Rel, i.Dir, i.Dir ? 0 : 1, T0, i.Sha), Planner.Paths);

    private static Dictionary<string, RemoteItem> R(params (string Rel, bool Dir, string? Sha)[] items) =>
        items.ToDictionary(i => i.Rel, i => new RemoteItem(i.Rel, i.Dir, i.Dir ? 0 : 1, i.Sha, T0), Planner.Paths);

    private static Dictionary<string, BaseEntry> B(params (string Rel, bool Dir, string? Sha)[] items) =>
        items.ToDictionary(i => i.Rel, i => new BaseEntry { IsDir = i.Dir, Sha256 = i.Sha, Size = i.Dir ? 0 : 1, LocalMtime = T0, RemoteModified = T0 }, Planner.Paths);

    [Fact]
    public void NewDirs_CreatedOnOtherSide()
    {
        var plan = Planner.Plan(B(), L(("a", true, null)), R(("b", true, null)));
        Assert.Contains(plan.Ops, o => o.Kind == OpKind.MkdirRemote && o.Rel == "a");
        Assert.Contains(plan.Ops, o => o.Kind == OpKind.MkdirLocal && o.Rel == "b");
    }

    [Fact]
    public void DirDeletedLocally_DeletedRemotely_WithFiles()
    {
        var plan = Planner.Plan(B(("d", true, null), ("d/f", false, "X")), L(), R(("d", true, null), ("d/f", false, "X")));
        Assert.Contains(plan.Ops, o => o.Kind == OpKind.DeleteRemote && o.Rel == "d" && o.IsDir);
        Assert.Contains(plan.Ops, o => o.Kind == OpKind.DeleteRemote && o.Rel == "d/f");
    }

    [Fact]
    public void DirDeletedLocally_ButFileChangedOnDisk_DirKept()
    {
        // папку удалили на компьютере, а файл в ней на Диске изменили: файл скачается, папка не удаляется
        var plan = Planner.Plan(B(("d", true, null), ("d/e", true, null), ("d/e/f", false, "X")), L(),
            R(("d", true, null), ("d/e", true, null), ("d/e/f", false, "B")));
        Assert.Contains(plan.Ops, o => o.Kind == OpKind.Download && o.Rel == "d/e/f");
        Assert.DoesNotContain(plan.Ops, o => o.Kind == OpKind.DeleteRemote);
        Assert.Contains(plan.Ops, o => o.Kind == OpKind.MkdirLocal && o.Rel == "d");
        Assert.Contains(plan.Ops, o => o.Kind == OpKind.MkdirLocal && o.Rel == "d/e");
    }

    [Fact]
    public void DirDeletedOnDisk_ButNewLocalFile_DirKept()
    {
        var plan = Planner.Plan(B(("d", true, null), ("d/f", false, "X")),
            L(("d", true, null), ("d/f", false, "X"), ("d/new", false, "A")), R());
        Assert.Contains(plan.Ops, o => o.Kind == OpKind.DeleteLocal && o.Rel == "d/f");
        Assert.Contains(plan.Ops, o => o.Kind == OpKind.Upload && o.Rel == "d/new");
        Assert.DoesNotContain(plan.Ops, o => o.Kind == OpKind.DeleteLocal && o.IsDir);
        Assert.Contains(plan.Ops, o => o.Kind == OpKind.MkdirRemote && o.Rel == "d");
    }

    // ---------- массовое удаление ----------

    private static SyncPlan Deleting(int total, int deleted, bool localSide)
    {
        var b = new Dictionary<string, BaseEntry>(Planner.Paths);
        var l = new Dictionary<string, LocalItem>(Planner.Paths);
        var r = new Dictionary<string, RemoteItem>(Planner.Paths);
        for (int i = 0; i < total; i++)
        {
            string f = $"f{i}";
            b[f] = new BaseEntry { Sha256 = "X", Size = 1, LocalMtime = T0, RemoteModified = T0 };
            bool gone = i < deleted;
            if (!(gone && localSide)) l[f] = new LocalItem(f, false, 1, T0, "X");
            if (!(gone && !localSide)) r[f] = new RemoteItem(f, false, 1, "X", T0);
        }
        return Planner.Plan(b, l, r);
    }

    [Theory]
    [InlineData(1, 1, false)]    // единственный файл — просто удаляем
    [InlineData(10, 5, false)]   // ровно половина — ещё нормально
    [InlineData(10, 6, true)]
    [InlineData(3, 2, true)]
    [InlineData(100, 1, false)]
    public void MassDeletion_NeedsConfirmation(int total, int deleted, bool expected)
    {
        Assert.Equal(expected, Deleting(total, deleted, localSide: true).NeedsConfirmation(false));
        Assert.Equal(expected, Deleting(total, deleted, localSide: false).NeedsConfirmation(false));
    }

    [Fact]
    public void RemoteRootMissing_AnyDeletion_NeedsConfirmation()
    {
        var plan = Deleting(10, 10, localSide: false);
        Assert.Equal(10, plan.LocalDeletes);
        Assert.True(plan.NeedsConfirmation(remoteRootMissing: true));
        Assert.False(Deleting(10, 0, localSide: false).NeedsConfirmation(remoteRootMissing: true));
    }

    [Fact]
    public void DeletionKeys_DistinguishSides()
    {
        var keys = Deleting(4, 3, localSide: true).DeletionKeys;
        Assert.Equal(3, keys.Count);
        Assert.All(keys, k => Assert.StartsWith("R:", k));
    }

    // ---------- исключения ----------

    [Theory]
    [InlineData("~$Книга.xlsx", true)]
    [InlineData("A1B2C3D4", false)]             // как временный файл Excel, но это решает обход по возрасту файла
    [InlineData("Книга.xlsx.tmp", true)]
    [InlineData("desktop.ini", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData(".yadisksync", true)]
    [InlineData(".~lock.Книга.xlsx#", true)]
    [InlineData("Книга.xlsx", false)]
    [InlineData("A1B2C3D4.xlsx", false)]
    [InlineData("Отчёт 2026", false)]
    public void Ignore_Names(string name, bool ignored) => Assert.Equal(ignored, Ignore.Name(name));

    [Theory]
    [InlineData("A1B2C3D4", true)]
    [InlineData("20241231", true)]
    [InlineData("A1B2C3D4.xlsx", false)]
    [InlineData("A1B2C3D", false)]
    public void LooksLikeExcelTemp(string name, bool expected) => Assert.Equal(expected, Ignore.LooksLikeExcelTemp(name));

    [Fact]
    public void Ignore_ServiceDirAnywhereInPath() => Assert.True(Ignore.Rel(".yadisksync/tmp/abc"));
}
