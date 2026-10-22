namespace YaDiskFileSync;

/// <summary>
/// Следит за папкой файла. Excel сохраняет не «на место»: пишет временный файл и переименовывает
/// его в исходное имя, поэтому ловим и изменение, и создание, и переименование в наше имя.
/// После последнего события ждём паузу (Debounce) — за это время Excel заканчивает сохранение.
/// </summary>
internal sealed class FileWatch : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly string _path;
    private readonly Action _changed;
    private readonly System.Threading.Timer _timer;
    private FileSystemWatcher? _fsw;

    public FileWatch(string path, Action changed)
    {
        _path = Path.GetFullPath(path);
        _changed = changed;
        _timer = new System.Threading.Timer(_ => _changed(), null, Timeout.Infinite, Timeout.Infinite);
        Attach();
    }

    private void Attach()
    {
        _fsw?.Dispose();
        _fsw = null;
        string? dir = Path.GetDirectoryName(_path);
        if (dir == null || !Directory.Exists(dir)) return;
        var w = new FileSystemWatcher(dir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
        };
        w.Changed += (_, e) => On(e.Name);
        w.Created += (_, e) => On(e.Name);
        w.Renamed += (_, e) => On(e.Name);
        // переполнение буфера или пропавшая сеть: события могли потеряться — переподключаемся и проверяем файл
        w.Error += (_, _) => { Attach(); Kick(); };
        w.EnableRaisingEvents = true;
        _fsw = w;
    }

    private void On(string? name)
    {
        if (IsTarget(name, Path.GetFileName(_path))) Kick();
    }

    /// <summary>Событие про наш файл? Блокировку Excel «~$Книга.xlsx» и временные файлы отбрасываем.</summary>
    internal static bool IsTarget(string? eventName, string target) =>
        eventName != null && string.Equals(Path.GetFileName(eventName), target, StringComparison.OrdinalIgnoreCase);

    /// <summary>Перезапускает отсчёт паузы.</summary>
    public void Kick() => _timer.Change(Debounce, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _fsw?.Dispose();
        _timer.Dispose();
    }
}
