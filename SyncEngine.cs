namespace YaDiskFileSync;

internal enum SyncState
{
    /// <summary>Папки не настроены.</summary>
    NotConfigured,
    /// <summary>Нужно войти в Яндекс.</summary>
    NeedLogin,
    /// <summary>Есть изменения, ждём прохода.</summary>
    Pending,
    Syncing,
    Synced,
    /// <summary>Временная ошибка (сеть, файл занят) — повторим сами.</summary>
    Retrying,
    /// <summary>Ошибка, которую повтор не исправит (нет места, нет прав, нет папки).</summary>
    Error,
    /// <summary>Пропало слишком много файлов — ждём решения пользователя.</summary>
    NeedsConfirm,
}

internal sealed record SyncStatus(SyncState State, string Message, DateTime? LastSynced = null, DateTime? NextRetry = null);

/// <summary>Массовое удаление, ждущее решения пользователя.</summary>
internal sealed record DeletionQuestion(int LocalDeletes, int RemoteDeletes, int BaseFiles, bool RemoteRootMissing,
    IReadOnlyList<string> Samples, IReadOnlySet<string> Keys);

internal enum RowState { Queued, Active, Done, Failed, Synced }

/// <summary>Строка окна «Статус синхронизации».</summary>
internal sealed record StatusRow(string Rel, string Action, RowState State, long Done, long Total, string? Error);

/// <summary>
/// Двусторонняя синхронизация папки на компьютере с папкой на Диске. Один проход: обойти обе стороны,
/// сверить с базой (<see cref="Planner"/>), выполнить действия. Проходы склеиваются: пока идёт один,
/// новые события дают ещё ровно один.
/// </summary>
internal sealed class SyncEngine : IDisposable
{
    /// <summary>Паузы между повторами при временных ошибках; дальше — последняя.</summary>
    internal static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
    };
    /// <summary>Проход не завершился (файл поменялся по ходу) — следующий через эту паузу, а не сразу.</summary>
    internal static TimeSpan SoonDelay { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Сколько таких незавершённых проходов подряд терпим, прежде чем считать это ошибкой и уйти в паузы Backoff.</summary>
    internal const int MaxAgain = 3;
    /// <summary>Сколько после загрузки ждём, пока Диск применит файл (ответ 202).</summary>
    internal static readonly TimeSpan UploadSettle = TimeSpan.FromMinutes(3);

    private readonly Func<IDisk?> _disk;
    private readonly Settings _settings;
    private readonly Action _saveSettings;
    private readonly StateStore _store;
    private readonly ILocalTrash _trash;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private bool _dirty;
    private Task? _loop;
    private CancellationTokenSource? _retry;   // отложенный повтор — всегда не больше одного
    private bool _soon;                        // проход просит следующий через SoonDelay
    private int _againStreak;

    // решение пользователя о массовом удалении: какие удаления разрешены / какие файлы вернуть
    private IReadOnlySet<string>? _allowedDeletes;
    private IReadOnlySet<string>? _restore;

    // для окна статуса
    private readonly object _rowsGate = new();
    private List<Activity> _activity = new();
    private Dictionary<string, BaseEntry> _lastBase = new(Planner.Paths);

    public event Action<SyncStatus>? StatusChanged;
    public SyncStatus Status { get; private set; }
    public DeletionQuestion? Question { get; private set; }

    public SyncEngine(Func<IDisk?> disk, Settings settings, Action saveSettings, StateStore store, ILocalTrash trash,
        Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _disk = disk;
        _settings = settings;
        _saveSettings = saveSettings;
        _store = store;
        _trash = trash;
        _now = now ?? (() => DateTime.Now);
        _delay = delay ?? Task.Delay;
        Status = new SyncStatus(SyncState.NotConfigured, "Папки не настроены.", settings.LastSyncedAt);
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    /// <summary>Что-то изменилось (или пора опросить Диск): пройти при первой возможности.</summary>
    public void Request()
    {
        lock (_gate) _dirty = true;
        _wake.Release();
    }

    /// <summary>
    /// Ответ на показанный вопрос о массовом удалении: true — удалить, false — вернуть файлы туда, где пропали.
    /// Ответ относится ровно к показанным удалениям: если с тех пор пропало что-то ещё, вопрос будет задан снова.
    /// </summary>
    public void Resolve(DeletionQuestion q, bool delete)
    {
        lock (_gate)
        {
            if (delete) _allowedDeletes = q.Keys;
            else _restore = q.Keys;
        }
        if (ReferenceEquals(Question, q)) Question = null;
        Request();
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            while (_wake.CurrentCount > 0) _wake.Wait(0);   // несколько сигналов подряд = один проход

            bool run;
            lock (_gate) { run = _dirty; _dirty = false; }
            if (!run) continue;

            _retry?.Cancel();
            _retry?.Dispose();
            _retry = null;
            _soon = false;
            SyncStatus result;
            try { result = await RunOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                // непредвиденное (странный ответ сервера и т.п.) не должно останавливать синхронизацию навсегда
                result = new SyncStatus(SyncState.Retrying, "Сбой: " + e.Message, _settings.LastSyncedAt);
            }
            if (result.State == SyncState.Retrying)
            {
                var pause = Backoff[Math.Min(failures, Backoff.Length - 1)];
                failures++;
                Publish(result with { NextRetry = _now() + pause });
                _retry = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = RetryLaterAsync(pause, _retry.Token);
            }
            else
            {
                failures = 0;
                Publish(result);
                if (_soon)
                {
                    _retry = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _ = RetryLaterAsync(SoonDelay, _retry.Token);
                }
            }
        }
    }

    private async Task RetryLaterAsync(TimeSpan pause, CancellationToken ct)
    {
        try { await _delay(pause, ct); }
        catch (OperationCanceledException) { return; }
        Request();
    }

    private SyncStatus Say(SyncState s, string msg) => new(s, msg, _settings.LastSyncedAt);

    /// <summary>Один проход синхронизации. Возвращает итоговое состояние (не публикует его).</summary>
    internal async Task<SyncStatus> RunOnceAsync(CancellationToken ct)
    {
        _soon = false;
        string? localRoot = _settings.LocalRoot, remoteRoot = _settings.RemoteRoot;
        if (string.IsNullOrEmpty(localRoot) || string.IsNullOrEmpty(remoteRoot) || DiskPath.IsRoot(remoteRoot))
            return Say(SyncState.NotConfigured, "Папки не настроены.");
        remoteRoot = DiskPath.Normalize(remoteRoot);
        // клиент Диска привязан к текущему входу: если пользователь перезайдёт посреди прохода, запросы
        // с новым токеном по плану, построенному для старой учётной записи, не уйдут
        if (_disk() is not { } disk)
            return Say(SyncState.NeedLogin, "Войдите в Яндекс.");

        try
        {
            if (string.IsNullOrEmpty(_settings.AccountUid))
            {
                var user = await disk.GetUserAsync(ct) ?? throw new DiskException("Яндекс.Диск не сообщил учётную запись.", DiskErrorKind.Transient);
                _settings.AccountUid = user.Uid;
                _settings.AccountLogin = user.Login;
                TrySaveSettings();
            }
            string key = StateStore.KeyFor(_settings.AccountUid, localRoot, remoteRoot);
            var baseState = _store.Load(key);

            if (!Directory.Exists(localRoot))
            {
                // пропавшая папка с историей — отключённый диск или ошибка; создавать пустую и удалять всё на Диске нельзя
                if (baseState.Count > 0)
                    return Say(SyncState.Error, $"Папка не найдена: {localRoot}. Подключите диск или выберите папку в настройках.");
                Directory.CreateDirectory(localRoot);
            }
            Explorer.HideServiceDir(localRoot);

            ApplyRestore(baseState, key);

            Publish(Say(SyncState.Syncing, "Сверяю папки…"));
            var remoteList = await disk.ListTreeAsync(remoteRoot, ct);
            bool remoteMissing = remoteList == null;
            var remote = new Dictionary<string, RemoteItem>(Planner.Paths);
            // что лежит на Диске, но не синхронизируется: такие пути не трогаем и папки с ними целиком не удаляем
            var skipped = new Dictionary<string, string>(Planner.Paths);
            var unsynced = new List<string>();
            foreach (var r in remoteList ?? Array.Empty<RemoteItem>())
            {
                if (Ignore.Rel(r.Rel)) { unsynced.Add(r.Rel); continue; }
                if (Ignore.WindowsCannot(r.Rel))
                {
                    unsynced.Add(r.Rel);
                    skipped[r.Rel] = "имя недопустимо в Windows (запрещённые символы или пробел/точка в конце) — не скачан";
                    continue;
                }
                if (!remote.TryAdd(r.Rel, r))
                {
                    // «Отчёт.xlsx» и «отчёт.xlsx» — Windows так не умеет
                    unsynced.Add(r.Rel);
                    skipped[r.Rel] = "на Диске есть файлы, чьи имена отличаются только регистром букв";
                }
            }

            LocalScan.Result scan;
            try { scan = LocalScan.Scan(localRoot, baseState, ct); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { return Say(SyncState.Retrying, "Не удалось прочитать папку: " + e.Message); }

            var plan = Planner.Plan(baseState, scan.Items, remote);
            // пропущенный путь (здесь папка, там файл) закрывает и всё, что внутри него
            var skipDirs = plan.Ops.Where(o => o.Kind == OpKind.Skip).Select(o => o.Rel + "/").ToList();
            // Excel посреди сохранения: книги на миг нет, есть свежий временный файл — удаления в этой папке откладываем
            var savingDirs = scan.Busy.Where(b => !b.Value.Retry && Ignore.LooksLikeExcelTemp(b.Key.Split('/')[^1]))
                .Select(b => b.Key.Contains('/') ? b.Key[..b.Key.LastIndexOf('/')] : "").ToHashSet(Planner.Paths);
            var ops = new List<SyncOp>();
            foreach (var o in plan.Ops)
            {
                // с занятыми и пропущенными файлами в этом проходе ничего не делаем
                if (scan.IsBusy(o.Rel) || skipped.ContainsKey(o.Rel)) continue;
                if (skipDirs.Any(d => o.Rel.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped[o.Rel] = "внутри пути, где на компьютере папка, а на Диске файл (или наоборот)";
                    continue;
                }
                if (o.Kind == OpKind.DeleteRemote && !o.IsDir &&
                    savingDirs.Contains(o.Rel.Contains('/') ? o.Rel[..o.Rel.LastIndexOf('/')] : "")) continue;
                // папку на Диске с несинхронизируемым содержимым целиком в Корзину не отправляем — только забываем
                if (o.Kind == OpKind.DeleteRemote && o.IsDir &&
                    unsynced.Any(u => u.StartsWith(o.Rel + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    ops.Add(new SyncOp(OpKind.Forget, o.Rel, true, "в папке на Диске есть несинхронизируемые файлы — оставлена"));
                    continue;
                }
                ops.Add(o);
            }

            // «массово или нет» — по полному плану (отложенное удаление тоже пропажа: иначе больше половины ушло бы
            // по частям без вопроса); но спрашиваем только про то, что удалится сейчас, и только если такое есть —
            // иначе вопрос повторялся бы каждый опрос, хотя «Да» ничего не удаляет
            var toRun = new SyncPlan(ops, plan.BaseFiles);
            if ((plan.NeedsConfirmation(remoteMissing) || toRun.NeedsConfirmation(remoteMissing)) && toRun.DeletionKeys.Count > 0)
            {
                IReadOnlySet<string>? allowed;
                lock (_gate) allowed = _allowedDeletes;
                var keys = toRun.DeletionKeys;
                if (allowed == null || !keys.IsSubsetOf(allowed))
                {
                    var samples = ops.Where(o => o.Kind is OpKind.DeleteLocal or OpKind.DeleteRemote && !o.IsDir).Select(o => o.Rel).Take(10).ToList();
                    Question = new DeletionQuestion(toRun.LocalDeletes, toRun.RemoteDeletes, toRun.BaseFiles, remoteMissing, samples, keys);
                    SetActivity(new List<Activity>(), baseState);
                    return Say(SyncState.NeedsConfirm, Explain(Question));
                }
            }
            lock (_gate) _allowedDeletes = null;
            Question = null;

            return await ExecuteAsync(disk, key, localRoot, remoteRoot, remoteMissing, remote, baseState, ops, scan.Busy, skipped, ct);
        }
        catch (DiskException e)
        {
            return Say(e.Kind switch
            {
                DiskErrorKind.Auth => SyncState.NeedLogin,
                DiskErrorKind.Transient => SyncState.Retrying,
                _ => SyncState.Error,
            }, e.Message);
        }
    }

    internal static string Explain(DeletionQuestion q)
    {
        if (q.RemoteRootMissing)
            return "Папка на Яндекс.Диске пропала (удалена или переименована). Удалить и файлы на компьютере?";
        var parts = new List<string>();
        if (q.RemoteDeletes > 0) parts.Add($"на компьютере пропало {q.RemoteDeletes} из {q.BaseFiles} файлов");
        if (q.LocalDeletes > 0) parts.Add($"на Диске пропало {q.LocalDeletes} из {q.BaseFiles} файлов");
        return "Синхронизация на паузе: " + string.Join(", ", parts) + ". Нужно ваше решение.";
    }

    /// <summary>Пользователь выбрал «вернуть»: забываем базу этих файлов (и всех папок) — тогда они просто скопируются обратно.</summary>
    private void ApplyRestore(Dictionary<string, BaseEntry> baseState, string key)
    {
        IReadOnlySet<string>? restore;
        lock (_gate) { restore = _restore; _restore = null; }
        if (restore == null) return;
        foreach (string k in restore) baseState.Remove(k[2..]);
        foreach (var dir in baseState.Where(kv => kv.Value.IsDir).Select(kv => kv.Key).ToList()) baseState.Remove(dir);
        _store.Save(key, baseState);
    }

    /// <summary>Состояние одного прохода: как на самом деле называются пути на Диске и какие папки решили оставить.</summary>
    private sealed class PassContext(string localRoot, string remoteRoot, IEnumerable<RemoteItem> remote)
    {
        public string LocalRoot { get; } = localRoot;
        public string RemoteRoot { get; } = remoteRoot;
        // Диск различает регистр букв, Windows — нет: путь на Диске строим из настоящих имён, что там уже есть
        private readonly Dictionary<string, string> _remoteNames = remote.ToDictionary(r => r.Rel, r => r.Rel, Planner.Paths);
        public HashSet<string> KnownRemoteDirs { get; } = new(remote.Where(r => r.IsDir).Select(r => r.Rel), Planner.Paths) { "" };
        public HashSet<string> KeptLocalDirs { get; } = new(Planner.Paths);

        public string RemoteRel(string rel)
        {
            if (_remoteNames.TryGetValue(rel, out var actual)) return actual;
            int slash = rel.LastIndexOf('/');
            return slash < 0 ? rel : RemoteRel(rel[..slash]) + rel[slash..];
        }

        public string RemotePath(string rel) => DiskPath.Combine(RemoteRoot, RemoteRel(rel));
        public void Created(string rel) => _remoteNames[rel] = RemoteRel(rel);
        public string LocalPath(string rel) => Path.Combine(LocalRoot, rel.Replace('/', '\\'));
    }

    private async Task<SyncStatus> ExecuteAsync(IDisk disk, string key, string localRoot, string remoteRoot, bool remoteMissing,
        Dictionary<string, RemoteItem> remote, Dictionary<string, BaseEntry> baseState, List<SyncOp> ops,
        IReadOnlyDictionary<string, LocalScan.BusyInfo> busy, IReadOnlyDictionary<string, string> skippedRemote, CancellationToken ct)
    {
        // порядок: папки (сверху вниз) → передачи → удаление файлов → удаление папок (снизу вверх)
        static int Rank(SyncOp o) => o.Kind switch
        {
            OpKind.MkdirLocal or OpKind.MkdirRemote => 0,
            OpKind.Record or OpKind.Forget or OpKind.Skip => 1,
            OpKind.Upload or OpKind.Download => 2,
            _ => o.IsDir ? 4 : 3,
        };
        ops = ops.OrderBy(Rank).ThenBy(o => Rank(o) == 4 ? -Depth(o.Rel) : Depth(o.Rel)).ThenBy(o => o.Rel, Planner.Paths).ToList();

        var acts = ops.Where(o => o.Kind is not (OpKind.Record or OpKind.Forget))
                      .Select(o => new Activity(o.Rel, o.Kind, o.IsDir ? 0 : o.Kind == OpKind.Download ? o.Remote?.Size ?? 0 : o.Local?.Size ?? 0))
                      .ToList();
        foreach (var (rel, why) in busy)
            if (!rel.EndsWith('/'))
                acts.Add(new Activity(rel, OpKind.Skip, 0) { State = why.Retry ? RowState.Failed : RowState.Queued, Error = why.Why });
        foreach (var (rel, why) in skippedRemote) acts.Add(new Activity(rel, OpKind.Skip, 0) { State = RowState.Failed, Error = why });
        SetActivity(acts, baseState);
        var actByOp = new Dictionary<SyncOp, Activity>();
        int ai = 0;
        foreach (var o in ops) if (o.Kind is not (OpKind.Record or OpKind.Forget)) actByOp[o] = acts[ai++];

        var pass = new PassContext(localRoot, remoteRoot, remote.Values);
        if (remoteMissing) await disk.EnsureFolderAsync(remoteRoot, ct);

        int failed = 0, retry = busy.Count(b => b.Value.Retry);
        bool again = false;
        string? firstError = null, firstAgain = null;
        int sinceSave = 0;
        int transferred = ops.Count(o => o.Kind is OpKind.Upload or OpKind.Download);
        int done = 0;
        try
        {
            foreach (var op in ops)
            {
                ct.ThrowIfCancellationRequested();
                actByOp.TryGetValue(op, out var act);
                if (act != null) act.State = RowState.Active;
                if (op.Kind is OpKind.Upload or OpKind.Download)
                    Publish(Say(SyncState.Syncing, $"Синхронизирую {++done} из {transferred}: {Path.GetFileName(op.Rel)}"));
                try
                {
                    var outcome = await RunOpAsync(disk, op, act, pass, baseState, ct);
                    if (outcome == Outcome.Changed) { again = true; firstAgain ??= op.Rel; }
                    if (act != null)
                    {
                        act.State = outcome == Outcome.Changed ? RowState.Queued : RowState.Done;
                        if (outcome == Outcome.Changed) act.Error = "изменился во время синхронизации — повторю";
                    }
                }
                catch (DiskException e) when (e.Kind == DiskErrorKind.Fatal)
                {
                    failed++;
                    firstError ??= $"{op.Rel}: {e.Message}";
                    if (act != null) { act.State = RowState.Failed; act.Error = e.Message; }
                }
                catch (DiskException e) when (e.InnerException is DiskClient.HttpConflict)
                {
                    // конфликт по одному пути (там файл вместо папки и т.п.) — это проблема файла, а не всего прохода
                    failed++;
                    firstError ??= $"{op.Rel}: {e.Message}";
                    if (act != null) { act.State = RowState.Failed; act.Error = e.Message; }
                }
                catch (DiskException e) when (e.NotFound)
                {
                    // на Диске файла уже нет (удалили, пока шёл проход) — разберёмся в следующем проходе
                    again = true;
                    firstAgain ??= op.Rel;
                    if (act != null) { act.State = RowState.Queued; act.Error = e.Message; }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    retry++;
                    firstError ??= $"{op.Rel}: {e.Message}";
                    if (act != null) { act.State = RowState.Failed; act.Error = LocalError(e); }
                }
                catch (DiskException e) when (act != null)
                {
                    act.State = RowState.Failed;
                    act.Error = e.Message;
                    throw;
                }
                if (++sinceSave >= 50) { _store.Save(key, baseState); sinceSave = 0; }
            }
        }
        finally
        {
            try { _store.Save(key, baseState); } catch { /* база не записалась — в худшем случае следующий проход сверит заново */ }
            SetActivity(acts, baseState);
        }

        if (again)
        {
            // не «синхронизировано», пока остались незавершённые действия; и не по кругу без пауз
            if (++_againStreak > MaxAgain)
                return Say(SyncState.Retrying, $"Не удаётся завершить синхронизацию «{firstAgain}»: он меняется во время передачи.");
            _soon = true;
        }
        else _againStreak = 0;

        int skipped = ops.Count(o => o.Kind == OpKind.Skip) + skippedRemote.Count;
        if (failed > 0)
            return Say(SyncState.Error, failed == 1 ? "Не синхронизирован файл " + firstError : $"Не синхронизировано файлов: {failed}. {firstError}");
        if (retry > 0)
            return Say(SyncState.Retrying, retry == 1 && firstError != null ? "Не удалось: " + firstError
                : $"Файлов занято или недоступно: {retry} — повторю позже.");
        if (again)
            return Say(SyncState.Pending, "Есть изменения во время синхронизации — продолжаю…");

        _settings.LastSyncedAt = _now();
        TrySaveSettings();
        string note = skipped > 0 ? $" (пропущено: {skipped}, см. «Статус синхронизации»)" : "";
        return new SyncStatus(SyncState.Synced, "Синхронизировано" + note, _settings.LastSyncedAt);
    }

    private static string LocalError(Exception e) =>
        e is UnauthorizedAccessException ? "нет доступа к файлу" : "файл занят другой программой — повторю позже";

    private static int Depth(string rel) => rel.Count(c => c == '/');

    private enum Outcome { Done, Changed }

    private async Task<Outcome> RunOpAsync(IDisk disk, SyncOp op, Activity? act, PassContext pass,
        Dictionary<string, BaseEntry> baseState, CancellationToken ct)
    {
        string full = pass.LocalPath(op.Local?.Rel ?? op.Rel);
        switch (op.Kind)
        {
            case OpKind.Record:
                baseState[op.Rel] = op.IsDir ? new BaseEntry { IsDir = true }
                    : new BaseEntry { Sha256 = op.Local!.Sha256, Size = op.Local.Size, LocalMtime = op.Local.Mtime, RemoteModified = op.Remote!.Modified };
                return Outcome.Done;

            case OpKind.Forget:
                baseState.Remove(op.Rel);
                return Outcome.Done;

            case OpKind.Skip:
                if (act != null) { act.State = RowState.Failed; act.Error = op.Reason; }
                return Outcome.Done;

            case OpKind.MkdirLocal:
                Directory.CreateDirectory(pass.LocalPath(op.Remote?.Rel ?? op.Rel));
                baseState[op.Rel] = new BaseEntry { IsDir = true };
                return Outcome.Done;

            case OpKind.MkdirRemote:
                await EnsureRemoteDirAsync(disk, pass, op.Rel, ct);
                baseState[op.Rel] = new BaseEntry { IsDir = true };
                return Outcome.Done;

            case OpKind.Upload:
                return await UploadAsync(disk, op, act, full, pass, baseState, ct);

            case OpKind.Download:
                return await DownloadAsync(disk, op, act, pass, baseState, ct);

            case OpKind.DeleteRemote:
            {
                // пока шёл проход, на компьютере появилось то же имя (Excel сохраняет через удаление и переименование) — не удаляем
                string local = pass.LocalPath(op.Rel);
                if (File.Exists(local) || Directory.Exists(local)) return Outcome.Changed;
                await disk.DeleteAsync(pass.RemotePath(op.Rel), ct);
                baseState.Remove(op.Rel);
                if (op.IsDir) RemoveUnder(baseState, op.Rel);
                return Outcome.Done;
            }

            case OpKind.DeleteLocal:
                if (op.IsDir) return DeleteLocalDir(op, full, pass, baseState);
                if (!File.Exists(full)) { baseState.Remove(op.Rel); return Outcome.Done; }
                if (!SameAsScanned(full, op.Local!)) return Outcome.Changed;
                _trash.Recycle(full);
                baseState.Remove(op.Rel);
                return Outcome.Done;
        }
        return Outcome.Done;
    }

    /// <summary>
    /// Папку, удалённую на Диске, отправляем в Корзину целиком, только если в ней не осталось ничего, кроме
    /// мусора Проводника. Появилось что-то синхронизируемое — разберёт следующий проход; лежит то, что мы не
    /// синхронизируем (ссылки, временные файлы, оставленные подпапки), — папку оставляем и забываем.
    /// </summary>
    private Outcome DeleteLocalDir(SyncOp op, string full, PassContext pass, Dictionary<string, BaseEntry> baseState)
    {
        if (!Directory.Exists(full)) { baseState.Remove(op.Rel); return Outcome.Done; }
        bool keep = false;
        foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            string name = info.Name;
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) continue;
            bool synced = !Ignore.Name(name) && (info.Attributes & FileAttributes.ReparsePoint) == 0;
            string childRel = op.Rel + "/" + name;
            if (synced && !(info is DirectoryInfo && pass.KeptLocalDirs.Contains(childRel))) return Outcome.Changed;
            keep = true;
        }
        if (keep)
        {
            pass.KeptLocalDirs.Add(op.Rel);
            baseState.Remove(op.Rel);
            return Outcome.Done;
        }
        _trash.Recycle(full);
        baseState.Remove(op.Rel);
        return Outcome.Done;
    }

    private static void RemoveUnder(Dictionary<string, BaseEntry> baseState, string rel)
    {
        foreach (var k in baseState.Keys.Where(k => k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)).ToList())
            baseState.Remove(k);
    }

    private static bool SameAsScanned(string full, LocalItem l)
    {
        var fi = new FileInfo(full);
        return fi.Exists && fi.Length == l.Size && fi.LastWriteTimeUtc == l.Mtime;
    }

    private static async Task EnsureRemoteDirAsync(IDisk disk, PassContext pass, string rel, CancellationToken ct)
    {
        if (pass.KnownRemoteDirs.Contains(rel)) return;
        await disk.EnsureFolderAsync(pass.RemotePath(rel), ct);
        // EnsureFolderAsync создаёт и родителей — запоминаем всю цепочку
        for (string? r = rel; !string.IsNullOrEmpty(r); r = r.Contains('/') ? r[..r.LastIndexOf('/')] : null)
        {
            pass.Created(r);
            pass.KnownRemoteDirs.Add(r);
        }
    }

    private async Task<Outcome> UploadAsync(IDisk disk, SyncOp op, Activity? act, string full, PassContext pass,
        Dictionary<string, BaseEntry> baseState, CancellationToken ct)
    {
        var l = op.Local!;
        int slash = op.Rel.LastIndexOf('/');
        if (slash > 0) await EnsureRemoteDirAsync(disk, pass, op.Rel[..slash], ct);
        string remotePath = pass.RemotePath(op.Rel);

        await using var fs = LocalScan.OpenShared(full);
        if (!SameAsScanned(full, l) || fs.Length != l.Size) return Outcome.Changed;
        // конфликт, победил компьютер: версию с Диска не затираем, а отправляем в Корзину Диска
        if (op.ReplacesRemoteChanges && op.Remote != null) await disk.DeleteAsync(remotePath, ct);
        await using var hashing = new HashingStream(fs, reading: true);
        if (act != null) act.Done = 0;
        await disk.UploadAsync(remotePath, hashing, l.Size, n => { if (act != null) Interlocked.Add(ref act.Done, n); }, ct);
        string sent = hashing.Sha256Hex();
        pass.Created(op.Rel);
        // в базу — то, что реально ушло на Диск (а не то, что лежит в файле сейчас): если файл успели
        // поменять, следующий проход увидит «изменён на компьютере» и загрузит снова
        baseState[op.Rel] = new BaseEntry
        {
            Sha256 = sent, Size = hashing.Count, LocalMtime = l.Mtime, RemoteModified = null,
            // и новый файл, и новая версия: пока Диск не покажет отправленное, его отсутствие или прежняя версия — не изменение там
            PrevRemoteSha = op.Remote?.Sha256, PrevRemoteModified = op.Remote?.Modified, PendingUntil = DateTime.UtcNow + UploadSettle,
        };
        return Planner.SameHash(sent, l.Sha256) && SameAsScanned(full, l) ? Outcome.Done : Outcome.Changed;
    }

    private async Task<Outcome> DownloadAsync(IDisk disk, SyncOp op, Activity? act, PassContext pass,
        Dictionary<string, BaseEntry> baseState, CancellationToken ct)
    {
        var r = op.Remote!;
        string full = pass.LocalPath(op.Local?.Rel ?? r.Rel);
        string tmpDir = Path.Combine(pass.LocalRoot, Ignore.ServiceDir, "tmp");
        Directory.CreateDirectory(tmpDir);
        string tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));
        try
        {
            string got;
            long size;
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            await using (var hashing = new HashingStream(fs, reading: false))
            {
                if (act != null) act.Done = 0;
                await disk.DownloadAsync(DiskPath.Combine(pass.RemoteRoot, r.Rel), hashing, n => { if (act != null) Interlocked.Add(ref act.Done, n); }, ct);
                await hashing.FlushAsync(ct);
                got = hashing.Sha256Hex();
                size = hashing.Count;
            }
            // файл на Диске успели заменить, пока скачивали, — сверимся заново
            if (r.Sha256 != null ? !Planner.SameHash(got, r.Sha256) : size != r.Size) return Outcome.Changed;

            // локальный файл не должен был поменяться с момента обхода: иначе затрём свежие правки
            if (op.Local != null ? !SameAsScanned(full, op.Local) : File.Exists(full) || Directory.Exists(full)) return Outcome.Changed;

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (op.Local != null && op.ReplacesLocalChanges) _trash.Recycle(full);
            File.Move(tmp, full, overwrite: true);
            DateTime mtime = r.Modified ?? DateTime.UtcNow;
            try { File.SetLastWriteTimeUtc(full, mtime); } catch (IOException) { }
            var fi = new FileInfo(full);
            baseState[op.Rel] = new BaseEntry { Sha256 = got, Size = fi.Length, LocalMtime = fi.LastWriteTimeUtc, RemoteModified = r.Modified };
            return Outcome.Done;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    // ---------- окно статуса ----------

    internal sealed class Activity(string rel, OpKind kind, long total)
    {
        public string Rel { get; } = rel;
        public OpKind Kind { get; } = kind;
        public long Total { get; } = total;
        public long Done;
        public RowState State { get; set; } = RowState.Queued;
        public string? Error { get; set; }
    }

    private void SetActivity(List<Activity> acts, Dictionary<string, BaseEntry> baseState)
    {
        lock (_rowsGate)
        {
            _activity = acts;
            _lastBase = new Dictionary<string, BaseEntry>(baseState, Planner.Paths);
        }
    }

    /// <summary>Строки для окна статуса: текущие действия и все синхронизированные файлы.</summary>
    public IReadOnlyList<StatusRow> Rows()
    {
        lock (_rowsGate)
        {
            var rows = new List<StatusRow>();
            var seen = new HashSet<string>(Planner.Paths);
            foreach (var a in _activity)
            {
                seen.Add(a.Rel);
                rows.Add(new StatusRow(a.Rel, ActionName(a.Kind, a.Error != null && a.Kind == OpKind.Skip), a.State,
                    Interlocked.Read(ref a.Done), a.Total, a.Error));
            }
            foreach (var (rel, b) in _lastBase)
                if (!b.IsDir && seen.Add(rel)) rows.Add(new StatusRow(rel, "", RowState.Synced, b.Size, b.Size, null));
            return rows;
        }
    }

    private static string ActionName(OpKind k, bool problem) => k switch
    {
        OpKind.Upload => "Загрузка на Диск",
        OpKind.Download => "Скачивание с Диска",
        OpKind.DeleteRemote => "Удаление на Диске (в Корзину)",
        OpKind.DeleteLocal => "Удаление на компьютере (в Корзину)",
        OpKind.MkdirRemote => "Новая папка на Диске",
        OpKind.MkdirLocal => "Новая папка на компьютере",
        OpKind.Skip => problem ? "Пропущен" : "",
        _ => "",
    };

    private void TrySaveSettings()
    {
        try { _saveSettings(); } catch { }
    }

    private void Publish(SyncStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }

    /// <summary>Сообщить состояние, не связанное с проходом (сменили папки, вышли из Яндекса).</summary>
    public void Set(SyncStatus s) => Publish(s);

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _stop.Dispose();
    }
}
