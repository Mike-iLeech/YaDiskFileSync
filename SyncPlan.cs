namespace YaDiskFileSync;

/// <summary>
/// Что было при последней успешной синхронизации пути — «база» для трёхсторонней сверки.
/// Без базы нельзя отличить «удалили здесь» от «появилось там».
/// </summary>
internal sealed class BaseEntry
{
    public bool IsDir { get; set; }
    public string? Sha256 { get; set; }
    public long Size { get; set; }
    /// <summary>Время изменения локального файла (UTC) на момент синхронизации.</summary>
    public DateTime LocalMtime { get; set; }
    /// <summary>Время изменения на Диске, если оно было известно.</summary>
    public DateTime? RemoteModified { get; set; }
    /// <summary>
    /// Только что загрузили, а Диск мог ответить 202 и применить файл чуть позже: до PendingUntil прежняя версия
    /// (PrevRemoteSha) в списке Диска — не «изменение на Диске», а ещё не применённая загрузка.
    /// </summary>
    public string? PrevRemoteSha { get; set; }
    public DateTime? PendingUntil { get; set; }
}

/// <summary>Файл или папка на компьютере. Sha256 у файла посчитан или взят из базы, если файл не менялся.</summary>
internal sealed record LocalItem(string Rel, bool IsDir, long Size, DateTime Mtime, string? Sha256);

internal enum OpKind
{
    /// <summary>Совпадает с обеих сторон — только записать в базу.</summary>
    Record,
    /// <summary>Пропал с обеих сторон — убрать из базы.</summary>
    Forget,
    Upload,
    Download,
    MkdirRemote,
    MkdirLocal,
    /// <summary>В Корзину Диска.</summary>
    DeleteRemote,
    /// <summary>В Корзину Windows.</summary>
    DeleteLocal,
    /// <summary>Тип не совпадает (здесь файл, там папка) — не трогаем, сообщаем.</summary>
    Skip,
}

/// <summary>
/// Действие над путём. ReplacesLocalChanges — скачивание затирает локальные изменения, которых нет на Диске
/// (конфликт, победил Диск): прежний локальный файл сначала уходит в Корзину Windows. ReplacesRemoteChanges —
/// наоборот (победил компьютер): версия на Диске сначала уходит в Корзину Диска.
/// </summary>
internal sealed record SyncOp(OpKind Kind, string Rel, bool IsDir, string Reason, bool ReplacesLocalChanges = false,
    bool ReplacesRemoteChanges = false)
{
    public LocalItem? Local { get; init; }
    public RemoteItem? Remote { get; init; }
}

internal sealed record SyncPlan(IReadOnlyList<SyncOp> Ops, int BaseFiles)
{
    public int LocalDeletes => Ops.Count(o => o.Kind == OpKind.DeleteLocal && !o.IsDir);
    public int RemoteDeletes => Ops.Count(o => o.Kind == OpKind.DeleteRemote && !o.IsDir);

    /// <summary>Пути файлов, которые этот план удалит (с пометкой стороны) — для подтверждения.</summary>
    public IReadOnlySet<string> DeletionKeys =>
        Ops.Where(o => o.Kind is OpKind.DeleteLocal or OpKind.DeleteRemote && !o.IsDir)
           .Select(o => (o.Kind == OpKind.DeleteLocal ? "L:" : "R:") + o.Rel.ToUpperInvariant()).ToHashSet();

    /// <summary>
    /// Массовое удаление: с одной стороны пропало больше половины файлов (и хотя бы два).
    /// Похоже на сбой (отключили диск, удалили папку не там) — без подтверждения не удаляем.
    /// </summary>
    public bool NeedsConfirmation(bool remoteRootMissing) =>
        (remoteRootMissing && LocalDeletes > 0) || IsMass(LocalDeletes) || IsMass(RemoteDeletes);

    private bool IsMass(int deletes) => deletes >= 2 && deletes * 2 > BaseFiles;
}

/// <summary>
/// Трёхсторонняя сверка «база — компьютер — Диск». Правила (решения пользователя от 22.10.2026):
/// изменилось с одной стороны — переносим на другую; изменилось с обеих — побеждает более новая версия;
/// удалили с одной стороны — удаляем с другой в Корзину, но изменение важнее удаления.
/// </summary>
internal static class Planner
{
    public static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    public static SyncPlan Plan(
        IReadOnlyDictionary<string, BaseEntry> baseState,
        IReadOnlyDictionary<string, LocalItem> local,
        IReadOnlyDictionary<string, RemoteItem> remote, DateTime? nowUtc = null)
    {
        DateTime now = nowUtc ?? DateTime.UtcNow;
        var ops = new List<SyncOp>();
        var all = new SortedSet<string>(Paths);
        all.UnionWith(baseState.Keys);
        all.UnionWith(local.Keys);
        all.UnionWith(remote.Keys);

        foreach (string rel in all)
        {
            baseState.TryGetValue(rel, out var b);
            local.TryGetValue(rel, out var l);
            remote.TryGetValue(rel, out var r);

            if (l != null && r != null && l.IsDir != r.IsDir)
            {
                ops.Add(new SyncOp(OpKind.Skip, rel, l.IsDir,
                    l.IsDir ? "на компьютере папка, а на Диске файл с тем же именем" : "на компьютере файл, а на Диске папка с тем же именем")
                    { Local = l, Remote = r });
                continue;
            }
            bool isDir = l?.IsDir ?? r?.IsDir ?? b!.IsDir;
            if (b != null && b.IsDir != isDir) b = null;   // был файл, стала папка (или наоборот) — база про другое

            if (isDir) PlanDir(ops, rel, b, l, r);
            else PlanFile(ops, rel, b, l, r, now);
        }

        DropDirDeletesWithSurvivors(ops, local, remote);
        return new SyncPlan(ops, baseState.Values.Count(e => !e.IsDir));
    }

    private static void PlanFile(List<SyncOp> ops, string rel, BaseEntry? b, LocalItem? l, RemoteItem? r, DateTime now)
    {
        bool lChanged = l != null && (b == null || !SameHash(l.Sha256, b.Sha256));

        // загрузка отправлена, но Диск мог её ещё не применить (ответ 202): файла там ещё нет или там прежняя версия.
        // Это не «удалён на Диске» и не «изменён на Диске»; не применилась за UploadSettle — загружаем снова
        if (b?.PendingUntil != null && l != null && (r == null || (b.PrevRemoteSha != null && SameHash(r.Sha256, b.PrevRemoteSha))))
        {
            if (lChanged || b.PendingUntil <= now)
                ops.Add(new SyncOp(OpKind.Upload, rel, false, lChanged ? "изменён на компьютере" : "Диск не применил загрузку — загружаю снова")
                    { Local = l, Remote = r });
            return;
        }

        bool rChanged = r != null && (b == null || RemoteDiffers(r, b, now));

        if (l != null && r != null)
        {
            if (r.Sha256 != null && SameHash(l.Sha256, r.Sha256))
            {
                // база уже такая — делать нечего; иначе (первая встреча, «потрогали» файл) обновляем её
                if (b == null || !SameHash(b.Sha256, l.Sha256) || b.Size != l.Size || b.LocalMtime != l.Mtime || b.RemoteModified != r.Modified ||
                    b.PendingUntil != null)
                    ops.Add(new SyncOp(OpKind.Record, rel, false, "совпадает") { Local = l, Remote = r });
            }
            else if (!lChanged && !rChanged)
                return;
            else if (lChanged && !rChanged)
                ops.Add(new SyncOp(OpKind.Upload, rel, false, b == null ? "новый на компьютере" : "изменён на компьютере") { Local = l, Remote = r });
            else if (!lChanged && rChanged)
                // если наша загрузка ещё не подтвердилась, локальная версия может быть только здесь — её в Корзину, а не в никуда
                ops.Add(new SyncOp(OpKind.Download, rel, false, "изменён на Диске", ReplacesLocalChanges: b?.PendingUntil != null)
                    { Local = l, Remote = r });
            else if (LocalIsNewer(l, r))
                ops.Add(new SyncOp(OpKind.Upload, rel, false, "изменён с обеих сторон, на компьютере новее", ReplacesRemoteChanges: true)
                    { Local = l, Remote = r });
            else
                ops.Add(new SyncOp(OpKind.Download, rel, false, "изменён с обеих сторон, на Диске новее", ReplacesLocalChanges: true)
                    { Local = l, Remote = r });
        }
        else if (l != null)
        {
            if (b == null) ops.Add(new SyncOp(OpKind.Upload, rel, false, "новый на компьютере") { Local = l });
            else if (lChanged) ops.Add(new SyncOp(OpKind.Upload, rel, false, "удалён на Диске, но изменён на компьютере") { Local = l });
            else ops.Add(new SyncOp(OpKind.DeleteLocal, rel, false, "удалён на Диске") { Local = l });
        }
        else if (r != null)
        {
            if (b == null) ops.Add(new SyncOp(OpKind.Download, rel, false, "новый на Диске") { Remote = r });
            else if (rChanged) ops.Add(new SyncOp(OpKind.Download, rel, false, "удалён на компьютере, но изменён на Диске") { Remote = r });
            else ops.Add(new SyncOp(OpKind.DeleteRemote, rel, false, "удалён на компьютере") { Remote = r });
        }
        else ops.Add(new SyncOp(OpKind.Forget, rel, false, "удалён с обеих сторон"));
    }

    private static void PlanDir(List<SyncOp> ops, string rel, BaseEntry? b, LocalItem? l, RemoteItem? r)
    {
        if (l != null && r != null) { if (b == null) ops.Add(new SyncOp(OpKind.Record, rel, true, "есть с обеих сторон") { Local = l, Remote = r }); }
        else if (l != null) ops.Add(b == null
            ? new SyncOp(OpKind.MkdirRemote, rel, true, "новая папка на компьютере") { Local = l }
            : new SyncOp(OpKind.DeleteLocal, rel, true, "папка удалена на Диске") { Local = l });
        else if (r != null) ops.Add(b == null
            ? new SyncOp(OpKind.MkdirLocal, rel, true, "новая папка на Диске") { Remote = r }
            : new SyncOp(OpKind.DeleteRemote, rel, true, "папка удалена на компьютере") { Remote = r });
        else ops.Add(new SyncOp(OpKind.Forget, rel, true, "папка удалена с обеих сторон"));
    }

    /// <summary>
    /// Папку удаляем, только если в ней ничего не остаётся: файл, который сохраняем (изменён, новый,
    /// пропущен), спасает и все папки над ним — вместо удаления они создаются заново на другой стороне.
    /// </summary>
    private static void DropDirDeletesWithSurvivors(List<SyncOp> ops,
        IReadOnlyDictionary<string, LocalItem> local, IReadOnlyDictionary<string, RemoteItem> remote)
    {
        var deletedFiles = new HashSet<string>(Paths);
        foreach (var o in ops)
            if (o.Kind is OpKind.DeleteLocal or OpKind.DeleteRemote && !o.IsDir) deletedFiles.Add(o.Rel);
        var deletedDirs = ops.Where(o => o.Kind is OpKind.DeleteLocal or OpKind.DeleteRemote && o.IsDir).Select(o => o.Rel).ToHashSet(Paths);

        for (int i = 0; i < ops.Count; i++)
        {
            var o = ops[i];
            if (!o.IsDir || o.Kind is not (OpKind.DeleteLocal or OpKind.DeleteRemote)) continue;
            string prefix = o.Rel + "/";
            // на стороне, где папку удаляем, остаётся хоть что-то, кроме удаляемого?
            IEnumerable<(string Rel, bool Dir)> side = o.Kind == OpKind.DeleteLocal
                ? local.Values.Select(x => (x.Rel, x.IsDir))
                : remote.Values.Select(x => (x.Rel, x.IsDir));
            bool survivor = side.Any(x => x.Rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                          !(x.Dir ? deletedDirs.Contains(x.Rel) : deletedFiles.Contains(x.Rel)));
            if (survivor)
            {
                ops[i] = o.Kind == OpKind.DeleteLocal
                    ? new SyncOp(OpKind.MkdirRemote, o.Rel, true, "в удалённой на Диске папке остались изменённые файлы") { Local = o.Local }
                    : new SyncOp(OpKind.MkdirLocal, o.Rel, true, "в удалённой на компьютере папке остались изменённые файлы") { Remote = o.Remote };
                // папки выше тоже нужны — повторяем, пока есть что спасать
                deletedDirs.Remove(o.Rel);
                i = -1;
            }
        }
    }

    internal static bool SameHash(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Файл на Диске отличается от базы: по SHA-256, а если его нет — по размеру и времени.</summary>
    internal static bool RemoteDiffers(RemoteItem r, BaseEntry b, DateTime now)
    {
        if (b.PrevRemoteSha != null && b.PendingUntil > now && SameHash(r.Sha256, b.PrevRemoteSha)) return false;
        return RemoteDiffers(r, b);
    }

    internal static bool RemoteDiffers(RemoteItem r, BaseEntry b) =>
        r.Sha256 != null && b.Sha256 != null
            ? !SameHash(r.Sha256, b.Sha256)
            : r.Size != b.Size || (r.Modified != null && b.RemoteModified != null && r.Modified != b.RemoteModified);

    /// <summary>Конфликт: побеждает более новая версия; при равенстве — компьютер.</summary>
    internal static bool LocalIsNewer(LocalItem l, RemoteItem r) => r.Modified is not { } rm || l.Mtime >= rm;
}

/// <summary>Что не синхронизируем: служебные файлы Office и Windows, временные файлы, своя служебная папка.</summary>
internal static class Ignore
{
    public const string ServiceDir = ".yadisksync";

    public static bool Name(string name)
    {
        if (name.Length == 0) return true;
        if (name.Equals(ServiceDir, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;            // блокировка Office
        if (name.StartsWith(".~lock.", StringComparison.Ordinal)) return true;       // блокировка LibreOffice
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        string ext = Path.GetExtension(name);
        if (ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".partial", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Похоже на временный файл сохранения Excel (8 шестнадцатеричных цифр без расширения). Так же может называться
    /// и обычный файл («20241231»), поэтому это не исключение навсегда: обход откладывает только свежие такие файлы.
    /// </summary>
    public static bool LooksLikeExcelTemp(string name) =>
        name.Length == 8 && Path.GetExtension(name).Length == 0 && name.All(Uri.IsHexDigit);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Имя с Диска, которое нельзя создать в Windows: запрещённые символы, пробел или точка в конце, CON/NUL….
    /// Такие файлы не скачиваем (иначе Windows молча изменит имя и получится путаница), а сообщаем о них.
    /// </summary>
    public static bool WindowsCannot(string rel) => rel.Split('/').Any(part =>
        part.Length == 0 || part.EndsWith(' ') || part.EndsWith('.') ||
        part.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*', '\\' }) >= 0 || part.Any(c => c < 32) ||
        Reserved.Contains(Path.GetFileNameWithoutExtension(part)));

    /// <summary>Путь (относительный, через «/») попадает под исключения хоть одной своей частью.</summary>
    public static bool Rel(string rel) => rel.Split('/').Any(Name);
}
