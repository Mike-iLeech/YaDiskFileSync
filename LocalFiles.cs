using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace YaDiskFileSync;

/// <summary>
/// База синхронизации на диске компьютера. Привязана к учётной записи и паре папок: сменили что-то
/// из этого — база пустая, и первая сверка только объединяет файлы, ничего не удаляя.
/// </summary>
internal sealed class StateStore(string fileName)
{
    public static string DefaultFile => Path.Combine(Settings.Dir, "state.json");

    private sealed class Doc
    {
        public string Key { get; set; } = "";
        public Dictionary<string, BaseEntry> Entries { get; set; } = new();
    }

    public static string KeyFor(string? uid, string localRoot, string remoteRoot) =>
        $"{uid}|{Path.GetFullPath(localRoot).TrimEnd('\\').ToUpperInvariant()}|{DiskPath.Normalize(remoteRoot)}";   // Диск различает регистр, Windows — нет

    public Dictionary<string, BaseEntry> Load(string key)
    {
        try
        {
            if (File.Exists(fileName) && JsonSerializer.Deserialize<Doc>(File.ReadAllBytes(fileName)) is { } d && d.Key == key)
                return new Dictionary<string, BaseEntry>(d.Entries, Planner.Paths);
        }
        catch { /* битая база — начнём заново: первая сверка ничего не удаляет */ }
        return new Dictionary<string, BaseEntry>(Planner.Paths);
    }

    public void Save(string key, IReadOnlyDictionary<string, BaseEntry> entries) =>
        AtomicFile.Write(fileName, JsonSerializer.SerializeToUtf8Bytes(new Doc { Key = key, Entries = new(entries, Planner.Paths) }));
}

/// <summary>Удаление «в Корзину» на компьютере.</summary>
internal interface ILocalTrash
{
    void Recycle(string path);
}

/// <summary>
/// Корзина Windows через SHFileOperation. Если файл в Корзину не помещается (слишком большой, сетевой диск),
/// Windows спросит пользователя, а не удалит молча — за это отвечает FOF_WANTNUKEWARNING.
/// </summary>
internal sealed class RecycleBin : ILocalTrash
{
    public void Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted)
            throw new IOException($"Не удалось переместить в Корзину «{Path.GetFileName(path)}» (код {rc}).");
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040,
        FOF_NOERRORUI = 0x0400, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}

/// <summary>Обход локальной папки синхронизации.</summary>
internal static class LocalScan
{
    /// <summary>Почему путь в этом проходе не трогаем; Retry — временно (занят), иначе — не синхронизируется вовсе.</summary>
    internal sealed record BusyInfo(string Why, bool Retry);

    /// <summary>Busy с ключом «папка/» закрывает всё, что внутри этой папки.</summary>
    internal sealed record Result(Dictionary<string, LocalItem> Items, Dictionary<string, BusyInfo> Busy)
    {
        public bool IsBusy(string rel) => Busy.ContainsKey(rel) ||
            Busy.Keys.Any(k => k.EndsWith('/') && rel.StartsWith(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Сколько файл с именем как у временного файла Excel считается временным.</summary>
    internal static readonly TimeSpan ExcelTempAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Все файлы и папки под root, кроме исключённых. SHA-256 считается только у новых и изменившихся
    /// (размер или время не совпали с базой). Файл, который сейчас не прочитать (Excel ещё пишет), попадает
    /// в Busy — с ним в этом проходе ничего не делаем, но и удалённым его не считаем.
    /// </summary>
    public static Result Scan(string root, IReadOnlyDictionary<string, BaseEntry> baseState, CancellationToken ct, DateTime? nowUtc = null)
    {
        DateTime now = nowUtc ?? DateTime.UtcNow;
        var items = new Dictionary<string, LocalItem>(Planner.Paths);
        var busy = new Dictionary<string, BusyInfo>(Planner.Paths);
        var stack = new Stack<(string Dir, string Rel)>();
        stack.Push((root, ""));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, relDir) = stack.Pop();
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (Ignore.Name(info.Name)) continue;
                string rel = relDir.Length == 0 ? info.Name : relDir + "/" + info.Name;
                // ссылки, точки соединения и файлы «по запросу» (OneDrive) не обходим: могут вести по кругу,
                // за пределы папки или скачиваться при чтении. Но и удалёнными их не считаем — то, что было
                // синхронизировано под этим именем, на Диске не трогаем
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var why = new BusyInfo("ссылка или файл «по запросу» — не синхронизируется", false);
                    busy[rel] = why;
                    if (info is DirectoryInfo) busy[rel + "/"] = why;
                    continue;
                }
                if (info is DirectoryInfo)
                {
                    items[rel] = new LocalItem(rel, true, 0, default, null);
                    stack.Push((info.FullName, rel));
                    continue;
                }
                var fi = (FileInfo)info;
                long size = fi.Length;
                DateTime mtime = fi.LastWriteTimeUtc;
                string? sha = null;
                if (Ignore.LooksLikeExcelTemp(info.Name) && now - mtime < ExcelTempAge)
                    busy[rel] = new BusyInfo("временный файл Excel — подожду", false);   // скорее всего, его сейчас переименуют в книгу
                else if (baseState.TryGetValue(rel, out var b) && !b.IsDir && b.Size == size && b.LocalMtime == mtime)
                    sha = b.Sha256;   // не менялся с последней синхронизации
                else
                {
                    // «недописанность» проверяем только у свежих файлов: старый битый или пустой (Проводник → Создать)
                    // документ — это уже его содержимое, синхронизируем как есть
                    try { sha = HashStable(fi.FullName, size, mtime, checkBook: now - mtime < ExcelTempAge); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        busy[rel] = new BusyInfo(e is UnauthorizedAccessException ? "нет доступа к файлу" : "файл занят или ещё сохраняется", true);
                    }
                }
                items[rel] = new LocalItem(rel, false, size, mtime, sha);
            }
        }
        return new Result(items, busy);
    }

    /// <summary>SHA-256 файла; файл должен не меняться во время чтения, а книга Excel — быть дописанной.</summary>
    internal static string HashStable(string path, long size, DateTime mtime, bool checkBook = true)
    {
        using var fs = OpenShared(path);
        if (fs.Length != size) throw new IOException("файл менялся во время чтения");
        string sha = Convert.ToHexString(SHA256.HashData(fs));
        var after = new FileInfo(path);
        if (after.Length != size || after.LastWriteTimeUtc != mtime) throw new IOException("файл менялся во время чтения");
        if (checkBook && IsZipBook(path))
        {
            fs.Position = 0;
            if (!IsCompleteBook(fs)) throw new IOException("книга записана не полностью");
        }
        return sha;
    }

    /// <summary>Открыть, не мешая программе, у которой файл открыт (Excel держит его всё время работы).</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);

    internal static bool IsZipBook(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm" or ".xlsb" or ".xltx" or ".xltm" or ".docx" or ".pptx";

    private static readonly byte[] CfbSignature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    /// <summary>
    /// Книга целая? Обычная книга — zip. Книга с паролем — контейнер OLE (сигнатура как у старого .xls,
    /// хотя расширение .xlsx): его содержимое не проверить, проверяем хотя бы целое число секторов.
    /// </summary>
    internal static bool IsCompleteBook(Stream data)
    {
        long len = data.Length;
        Span<byte> head = stackalloc byte[8];
        if (len >= 8)
        {
            long pos = data.Position;
            data.ReadExactly(head);
            data.Position = pos;
            if (head.SequenceEqual(CfbSignature)) return len >= 1024 && len % 512 == 0;
        }
        return IsCompleteZip(data);
    }

    internal static bool IsCompleteBook(byte[] data) => IsCompleteBook(new MemoryStream(data, writable: false));

    /// <summary>
    /// Zip пишется с начала, а оглавление — в самом конце: недописанный архив открыть не получится.
    /// Читаем оглавление и распаковываем каждую запись: обрезанные данные дают ошибку.
    /// </summary>
    internal static bool IsCompleteZip(Stream data)
    {
        try
        {
            using var zip = new ZipArchive(data, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count == 0) return false;
            foreach (var entry in zip.Entries)
            {
                using var s = entry.Open();
                s.CopyTo(Stream.Null);
            }
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException) { return false; }
    }
}
