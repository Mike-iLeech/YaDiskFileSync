namespace YaDiskFileSync;

/// <summary>
/// Следит за папкой синхронизации со всеми подпапками. Excel сохраняет через временный файл и
/// переименование, поэтому после последнего события ждём паузу (Debounce) — за это время сохранение
/// заканчивается, и проход видит уже готовый файл. События про служебные и временные файлы отбрасываем.
/// </summary>
internal sealed class FolderWatch : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly string _root;
    private readonly Action _changed;
    private readonly System.Threading.Timer _timer;
    private FileSystemWatcher? _fsw;
    private readonly object _gate = new();
    private bool _disposed;

    public FolderWatch(string root, Action changed)
    {
        _root = Path.GetFullPath(root);
        _changed = changed;
        _timer = new System.Threading.Timer(_ => _changed(), null, Timeout.Infinite, Timeout.Infinite);
        Attach();
    }

    public string Root => _root;
    public bool Attached { get { lock (_gate) return _fsw != null; } }

    private void Attach()
    {
        lock (_gate) { if (!_disposed) AttachLocked(); }
    }

    private void AttachLocked()
    {
        _fsw?.Dispose();
        _fsw = null;
        if (!Directory.Exists(_root)) return;
        var w = new FileSystemWatcher(_root)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
        };
        w.Changed += (_, e) => On(e.Name);
        w.Created += (_, e) => On(e.Name);
        w.Deleted += (_, e) => On(e.Name);
        w.Renamed += (_, e) => { On(e.OldName); On(e.Name); };
        // переполнение буфера или пропавшая сеть: события могли потеряться — переподключаемся и сверяем всё
        w.Error += (_, _) => { Attach(); Kick(); };
        w.EnableRaisingEvents = true;
        _fsw = w;
    }

    private void On(string? name)
    {
        if (IsRelevant(name)) Kick();
    }

    /// <summary>Событие про синхронизируемый путь? Служебную папку, блокировки Office и временные файлы отбрасываем.</summary>
    internal static bool IsRelevant(string? relName) =>
        relName != null && !Ignore.Rel(relName.Replace('\\', '/'));

    /// <summary>Перезапускает отсчёт паузы.</summary>
    public void Kick()
    {
        // события приходят из пула потоков и могут опоздать к Dispose — исключение там уронило бы программу
        lock (_gate) { if (!_disposed) _timer.Change(Debounce, Timeout.InfiniteTimeSpan); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _fsw?.Dispose();
            _fsw = null;
            _timer.Dispose();
        }
    }
}
