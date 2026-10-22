using System.Diagnostics;
using System.Net.Http.Headers;

namespace YaDiskFileSync;

internal sealed class TrayApp : IDisposable
{
    internal static readonly int[] UpdateCheckChoices = { 1, 3, 6, 12, 24, 0 };
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateNoticeDelay = TimeSpan.FromSeconds(8);

    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly Dictionary<Color, Icon> _icons = new();
    private readonly Settings _settings = Settings.Load();
    private readonly Secrets _secrets = Secrets.Load();
    private readonly object _saveLock = new();
    private readonly HttpClient _http;       // вход и обновления: обычные короткие запросы
    private readonly HttpClient _transfer;   // Диск: файлы любого размера, таймауты считает сам DiskClient
    private readonly TokenSource _tokens;
    private readonly SyncEngine _engine;
    private readonly System.Windows.Forms.Timer _poll = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 30_000 };
    private FolderWatch? _watch;
    private Form? _dialog;              // открытое окно входа или настроек — одно за раз
    private StatusForm? _statusForm;
    private bool _asking;
    private SyncState? _shownProblem;   // о проблеме сообщаем один раз, а не на каждом повторе
    private Action? _balloonAction;
    private ReleaseInfo? _pendingUpdate;
    private bool _updating;
    private readonly Version? _rolledBack;
    private string? _lastAutoError;
    private readonly bool _needsNewRights;

    public UpdateHandoff? Handoff { get; private set; }

    public TrayApp(string? updatedFrom = null, string? rolledBack = null)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YaDiskFileSync", Updater.Current.ToString()));
        _transfer = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _transfer.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YaDiskFileSync", Updater.Current.ToString()));
        // ClientID есть, а токенов нет — значит, вход был в версии 1.x с правами только на папку приложения
        _needsNewRights = !_secrets.SignedIn && !string.IsNullOrEmpty(_settings.ClientId);
        _tokens = new TokenSource(_secrets, OAuth, SaveSecrets);
        _engine = new SyncEngine(() => _secrets.SignedIn ? new DiskClient(_transfer, new PinnedTokens(_tokens, _secrets)) : null, _settings, SaveSettings,
            new StateStore(StateStore.DefaultFile), new RecycleBin());
        _engine.StatusChanged += s => _ui.Post(_ => Show(s), null);

        _menu.Opening += (_, _) => BuildMenu();
        _tray.ContextMenuStrip = _menu;
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenFolder(); };
        _tray.BalloonTipClicked += (_, _) => { var a = _balloonAction; _balloonAction = null; a?.Invoke(); };
        _tray.Visible = true;

        _rolledBack = Updater.ParseVersion(rolledBack);
        if (rolledBack != null)
        {
            _settings.SkippedUpdate = rolledBack;
            SaveSettings();
            Balloon("Обновление отменено",
                $"Версия {_rolledBack?.ToString(3) ?? rolledBack} не запустилась — возвращена {Updater.Current.ToString(3)}.", ToolTipIcon.Warning);
        }
        var notes = Updater.TakeNotes(Updater.Current);
        if (updatedFrom != null && rolledBack == null)
        {
            string what = notes is { } n && Updater.BalloonText(n.Notes) is { Length: > 0 } t ? t : "Щёлкните, чтобы посмотреть, что нового.";
            string url = notes?.PageUrl is { Length: > 0 } u ? u : $"https://github.com/{Updater.Repo}/releases/latest";
            Balloon($"YaDisk File Sync обновлён до {Updater.Current.ToString(3)}", what, ToolTipIcon.Info, () => OpenUrl(url));
        }

        Show(Initial());
        ApplyExplorer();
        _engine.Start();
        Watch();
        _poll.Interval = (int)Settings.Poll(_settings.PollSeconds).TotalMilliseconds;
        _poll.Tick += (_, _) => { Watch(); _engine.Request(); };
        _poll.Start();

        _updateTimer.Tick += (_, _) =>
        {
            if (CheckInterval(_settings.UpdateCheckHours) is not { } every) { _updateTimer.Stop(); return; }
            _updateTimer.Interval = (int)every.TotalMilliseconds;
            CheckUpdates(manual: false);
        };
        ScheduleUpdateChecks();

        // первый запуск: ведём по шагам — вход с инструкцией, затем настройки папок
        if (!_secrets.SignedIn) _ui.Post(_ => Login(), null);
        else if (!Configured) _ui.Post(_ => OpenSettings(), null);
    }

    private DiskClient Disk() => new(_transfer, _tokens);

    private YandexOAuth? OAuth() =>
        string.IsNullOrEmpty(_settings.ClientId) ? null : new YandexOAuth(_http, _settings.ClientId, _secrets.ClientSecret);

    private bool Configured => !string.IsNullOrEmpty(_settings.LocalRoot) && !string.IsNullOrEmpty(_settings.RemoteRoot);

    private SyncStatus Initial() =>
        !_secrets.SignedIn ? new SyncStatus(SyncState.NeedLogin, "Войдите в Яндекс.", _settings.LastSyncedAt)
        : !Configured ? new SyncStatus(SyncState.NotConfigured, "Папки не настроены — откройте «Настройки».", _settings.LastSyncedAt)
        : new SyncStatus(SyncState.Pending, "Сверяю папки…", _settings.LastSyncedAt);

    /// <summary>(Пере)подключает слежение за папкой — например, когда подключили диск, на котором она лежит.</summary>
    private void Watch()
    {
        string? root = _settings.LocalRoot;
        if (_watch != null && root != null && _watch.Root.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
            && _watch.Attached == Directory.Exists(root)) return;
        _watch?.Dispose();
        _watch = null;
        if (string.IsNullOrEmpty(root)) return;
        _watch = new FolderWatch(root, () =>
        {
            _ui.Post(_ =>
            {
                if (_engine.Status.State is SyncState.Synced or SyncState.Retrying)
                    Show(new SyncStatus(SyncState.Pending, "Есть изменения…", _settings.LastSyncedAt));
            }, null);
            _engine.Request();
        });
        _engine.Request();
    }

    // ---------- состояние ----------

    private void Show(SyncStatus s)
    {
        var color = IconArt.For(s.State);
        if (!_icons.TryGetValue(color, out var icon))
            _icons[color] = icon = IconArt.MakeIcon(Math.Max(16, SystemInformation.SmallIconSize.Width), color);
        _tray.Icon = icon;
        _tray.Text = Truncate("YaDisk File Sync\n" + StatusLine(s), 127);

        if (s.State is SyncState.Error or SyncState.NeedLogin or SyncState.NeedsConfirm)
        {
            if (_shownProblem != s.State && _dialog == null)
            {
                if (s.State == SyncState.NeedsConfirm)
                    Balloon("Синхронизация на паузе", s.Message + " Щёлкните, чтобы решить.", ToolTipIcon.Warning, AskDeletions);
                else
                    Balloon(s.State == SyncState.NeedLogin ? "Нужно войти в Яндекс" : "Не всё синхронизировано", s.Message, ToolTipIcon.Warning,
                        s.State == SyncState.NeedLogin ? Login : OpenStatus);
            }
            _shownProblem = s.State;
        }
        else if (s.State == SyncState.Synced) _shownProblem = null;
    }

    internal static string StatusLine(SyncStatus s) => s.State switch
    {
        SyncState.Synced => s.LastSynced is { } t ? $"{s.Message}: {When(t)}" : s.Message,
        SyncState.Retrying => s.NextRetry is { } r ? $"{s.Message} Повтор в {r:HH:mm}." : s.Message,
        _ => s.Message,
    };

    private static string When(DateTime t) =>
        t.Date == DateTime.Today ? $"сегодня в {t:HH:mm}" : t.Date == DateTime.Today.AddDays(-1) ? $"вчера в {t:HH:mm}" : $"{t:dd.MM.yyyy HH:mm}";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---------- действия ----------

    private void SyncNow()
    {
        if (!_secrets.SignedIn) { Login(); return; }
        if (!Configured) { OpenSettings(); return; }
        Show(new SyncStatus(SyncState.Pending, "Синхронизирую…", _settings.LastSyncedAt));
        Watch();
        _engine.Request();
    }

    private void OpenFolder()
    {
        if (!Configured) { OpenSettings(); return; }
        string root = _settings.LocalRoot!;
        try
        {
            Directory.CreateDirectory(root);
            Process.Start("explorer.exe", $"\"{root}\"");
        }
        catch (Exception e) { Balloon("Не удалось открыть папку", e.Message, ToolTipIcon.Warning); }
    }

    private void OpenStatus()
    {
        if (_statusForm is { IsDisposed: false }) { _statusForm.Activate(); return; }
        _statusForm = new StatusForm(_engine);
        _statusForm.FormClosed += (_, _) => { _statusForm?.Dispose(); _statusForm = null; };
        _statusForm.Show();
    }

    /// <summary>Пропало много файлов: удалить и на другой стороне или вернуть их.</summary>
    private void AskDeletions()
    {
        if (_asking || _engine.Question is not { } q) return;
        _asking = true;
        try
        {
            string sample = q.Samples.Count > 0 ? "\n\nНапример:\n" + string.Join("\n", q.Samples.Take(8).Select(x => "  " + x.Replace('/', '\\'))) : "";
            string where = q.RemoteRootMissing ? "на компьютере" : q.RemoteDeletes > 0 && q.LocalDeletes > 0 ? "с обеих сторон" :
                q.RemoteDeletes > 0 ? "на Яндекс.Диске" : "на компьютере";
            var answer = MessageBox.Show(
                SyncEngine.Explain(q) + sample +
                $"\n\nДа — удалить их и {where} (в Корзину, оттуда можно восстановить)." +
                "\nНет — вернуть файлы: скопировать обратно туда, где они пропали." +
                "\nОтмена — решить позже (синхронизация остаётся на паузе).",
                "YaDisk File Sync — пропали файлы", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes) _engine.Resolve(q, delete: true);
            else if (answer == DialogResult.No) _engine.Resolve(q, delete: false);
        }
        finally { _asking = false; }
    }

    private void Login()
    {
        if (_dialog != null) { _dialog.Activate(); return; }
        using var form = new LoginForm(_http, _settings.ClientId, _secrets.ClientSecret, VerifyAsync, rightsChanged: _needsNewRights);
        _dialog = form;
        try
        {
            if (form.ShowDialog() != DialogResult.OK || form.Token is not { } t || form.User is not { } user) return;
            // вошли под другой учётной записью — её Диск объединится с этой папкой; без согласия не начинаем
            if (Configured && _settings.AccountUid is { } oldUid && oldUid != user.Uid && MessageBox.Show(
                    $"Вы вошли как «{user.Login}», а папка {_settings.LocalRoot} до сих пор синхронизировалась " +
                    $"с Диском «{_settings.AccountLogin ?? "другой учётной записи"}».\n\n" +
                    $"Объединить её с папкой {DiskPath.Display(_settings.RemoteRoot!)} на Диске «{user.Login}»? Файлы обеих сторон " +
                    "окажутся и там, и там; при совпадении имён побеждает более новая версия, а проигравшая уходит в Корзину.",
                    "YaDisk File Sync — другая учётная запись", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            _settings.ClientId = form.ClientId;
            lock (_secrets.Sync)
            {
                // всё сразу и под замком: проход не увидит новый токен со старой учётной записью
                _secrets.Generation++;
                _secrets.ClientSecret = form.ClientSecret.Length > 0 ? form.ClientSecret : null;
                _secrets.AccessToken = t.AccessToken;
                _secrets.RefreshToken = t.RefreshToken;
                _secrets.ExpiresAt = t.ExpiresAt;
                _secrets.Scope = Secrets.CurrentScope;
                _settings.AccountUid = user.Uid;
                _settings.AccountLogin = user.Login;
                SaveSecrets();
            }
            SaveSettings();
            Balloon("Вход выполнен", Configured ? "Синхронизация продолжается." : "Осталось выбрать папки в настройках.", ToolTipIcon.Info);
        }
        finally { _dialog = null; }
        if (!Configured) OpenSettings();
        else { Show(Initial()); _engine.Request(); }
    }

    /// <summary>Чей это токен и видно ли с ним весь Диск (без прав «Чтение всего Диска» будет 403).</summary>
    private async Task<DiskUser> VerifyAsync(string token, CancellationToken ct)
    {
        var client = new DiskClient(_transfer, new StaticToken(token));
        var user = await client.GetUserAsync(ct) ?? throw new DiskException("Яндекс.Диск не сообщил учётную запись.", DiskErrorKind.Transient);
        await client.ListFoldersAsync("disk:/", ct);
        return user;
    }

    /// <summary>Выход: папка и файлы на компьютере остаются, синхронизация просто останавливается.</summary>
    private bool Logout()
    {
        if (MessageBox.Show("Выйти из Яндекса?\n\nПапка и все файлы на компьютере останутся на месте; синхронизация остановится, " +
                            "пока вы не войдёте снова.", "YaDisk File Sync", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return false;
        lock (_secrets.Sync)
        {
            _secrets.Generation++;
            _secrets.ClearTokens();
            SaveSecrets();
        }
        _settings.AccountLogin = null;
        SaveSettings();
        _engine.Set(Initial());
        return true;
    }

    private void OpenSettings()
    {
        if (_dialog != null) { _dialog.Activate(); return; }
        bool autostart = SafeAutostart() || (!Configured && Updater.ExePath != null);
        using var form = new SettingsForm(_settings, autostart, () => _secrets.SignedIn, () => _settings.AccountLogin,
            () =>
            {
                if (_secrets.SignedIn) Logout();
                else { var d = _dialog; _dialog = null; try { Login(); } finally { _dialog = d; } }
            },
            _secrets.SignedIn ? (path, ct) => Disk().ListFoldersAsync(path, ct) : null,
            () => CheckUpdates(manual: true));
        _dialog = form;
        try
        {
            if (form.ShowDialog() != DialogResult.OK || form.Choice is not { } c) return;
            Apply(c);
        }
        finally { _dialog = null; }
    }

    private void Apply(SettingsChoice c)
    {
        bool foldersChanged = Configured &&
            (!string.Equals(Path.GetFullPath(_settings.LocalRoot!), c.LocalRoot, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(DiskPath.Normalize(_settings.RemoteRoot!), c.RemoteRoot, StringComparison.OrdinalIgnoreCase));
        if (foldersChanged && MessageBox.Show(
                $"Синхронизировать {c.LocalRoot}\nс папкой Диска {DiskPath.Display(c.RemoteRoot)}?\n\n" +
                "Содержимое двух папок будет объединено. При совпадении имён побеждает более новая версия, проигравшая уходит " +
                "в Корзину (Windows или Диска). Прежняя папка на компьютере останется как есть.",
                "YaDisk File Sync", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        try { Directory.CreateDirectory(c.LocalRoot); }
        catch (Exception e)
        {
            MessageBox.Show("Не удалось создать папку: " + e.Message, "YaDisk File Sync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _settings.LocalRoot = c.LocalRoot;
        _settings.RemoteRoot = c.RemoteRoot;
        _settings.PollSeconds = c.PollSeconds;
        _settings.ShowInExplorer = c.ShowInExplorer;
        bool scheduleChanged = _settings.UpdateCheckHours != c.UpdateCheckHours;
        _settings.UpdateCheckHours = c.UpdateCheckHours;
        _settings.AutoUpdate = c.AutoUpdate;
        SaveSettings();

        try { if (SafeAutostart() != c.Autostart) Autostart.Enabled = c.Autostart; }
        catch (Exception e) { Balloon("Автозапуск", e.Message, ToolTipIcon.Error); }
        ApplyExplorer();
        _poll.Interval = (int)Settings.Poll(_settings.PollSeconds).TotalMilliseconds;
        if (scheduleChanged) ScheduleUpdateChecks();
        Show(Initial());
        Watch();
        _engine.Request();
    }

    /// <summary>Пункт папки в панели навигации Проводника и значок на самой папке.</summary>
    private void ApplyExplorer()
    {
        string? exe = Environment.ProcessPath;
        try
        {
            if (_settings.ShowInExplorer && Configured && exe != null && Updater.ExePath != null)
            {
                Directory.CreateDirectory(_settings.LocalRoot!);
                Explorer.Register(_settings.LocalRoot!, exe);
                Explorer.SetFolderIcon(_settings.LocalRoot!, exe);
            }
            else if (!_settings.ShowInExplorer && Explorer.RegisteredFolder() != null) Explorer.Unregister();
        }
        catch (Exception e) { Balloon("Не удалось добавить папку в Проводник", e.Message, ToolTipIcon.Warning); }
    }

    private void SaveSettings()
    {
        lock (_saveLock)
        {
            try { _settings.Save(); }
            catch (Exception e) { _ui.Post(_ => Balloon("Не удалось сохранить настройки", e.Message, ToolTipIcon.Error), null); }
        }
    }

    private void SaveSecrets()
    {
        lock (_saveLock)
        {
            try { _secrets.Save(); }
            catch (Exception e) { _ui.Post(_ => Balloon("Не удалось сохранить вход", e.Message, ToolTipIcon.Error), null); }
        }
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem(Truncate(StatusLine(_engine.Status), 90)) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem($"Открыть папку {Settings.DefaultFolderName}", null, (_, _) => OpenFolder())
            { Font = new Font(_menu.Font, FontStyle.Bold), Enabled = Configured });
        if (_settings.RemoteRoot is { } rr)
            _menu.Items.Add(new ToolStripMenuItem("Открыть папку на Яндекс.Диске", null, (_, _) => OpenUrl(DiskPath.WebUrl(rr))));

        var sync = new ToolStripMenuItem("Синхронизация");
        sync.DropDownItems.Add(new ToolStripMenuItem("Синхронизировать сейчас", null, (_, _) => SyncNow())
            { Enabled = _secrets.SignedIn && Configured });
        sync.DropDownItems.Add(new ToolStripMenuItem("Статус синхронизации", null, (_, _) => OpenStatus()));
        _menu.Items.Add(sync);
        if (_engine.Question != null)
            _menu.Items.Add(new ToolStripMenuItem("Пропали файлы — решить…", null, (_, _) => AskDeletions()) { Font = new Font(_menu.Font, FontStyle.Bold) });
        if (!_secrets.SignedIn || _engine.Status.State == SyncState.NeedLogin)
            _menu.Items.Add(new ToolStripMenuItem("Войти в Яндекс…", null, (_, _) => Login()));
        _menu.Items.Add(new ToolStripMenuItem("Настройки…", null, (_, _) => OpenSettings()));
        if (_pendingUpdate is { } upd && Updater.IsNewer(upd))
            _menu.Items.Add(new ToolStripMenuItem($"Установить обновление {upd.Version.ToString(3)}", null,
                (_, _) => InstallUpdate(upd, manual: true)) { Enabled = !_updating });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => Exit()));
    }

    private static bool SafeAutostart()
    {
        try { return Autostart.Enabled; } catch { return false; }
    }

    // ---------- обновление программы ----------

    internal static TimeSpan? CheckInterval(int hours) =>
        hours <= 0 ? null : TimeSpan.FromHours(Math.Clamp(hours, 1, 168));

    internal static string IntervalName(int hours) => hours switch
    {
        <= 0 => "Никогда",
        1 => "Раз в час",
        24 => "Раз в сутки",
        _ => $"Раз в {hours} ч",
    };

    private void ScheduleUpdateChecks()
    {
        _updateTimer.Stop();
        if (Updater.ExePath == null || CheckInterval(_settings.UpdateCheckHours) == null) return;
        _updateTimer.Interval = (int)FirstCheckDelay.TotalMilliseconds;
        _updateTimer.Start();
    }

    private async void CheckUpdates(bool manual)
    {
        if (_updating || Handoff != null) return;
        if (Updater.ExePath == null)
        {
            if (manual) Balloon("Обновление", "Обновляется только собранный YaDiskFileSync.exe.", ToolTipIcon.Info);
            return;
        }
        ReleaseInfo? r;
        try { r = await Updater.FetchLatestAsync(); }
        catch (Exception e)   // async void: исключение отсюда уронило бы программу
        {
            if (manual) Balloon("Не удалось проверить обновления", e.Message, ToolTipIcon.Warning);
            return;
        }
        if (r == null || !Updater.IsNewer(r))
        {
            _pendingUpdate = null;
            if (manual) Balloon("Обновлений нет", $"Установлена последняя версия {Updater.Current.ToString(3)}.", ToolTipIcon.Info);
            return;
        }
        _pendingUpdate = r;
        bool skipped = Updater.ParseVersion(_settings.SkippedUpdate) == r.Version || _rolledBack == r.Version;
        if (manual || (_settings.AutoUpdate && !skipped)) InstallUpdate(r, manual);
        else if (_lastAutoError != "offer:" + r.Tag)
        {
            _lastAutoError = "offer:" + r.Tag;
            Balloon("Доступно обновление", $"Версия {r.Version.ToString(3)}: ПКМ по значку → «Установить обновление».", ToolTipIcon.Info,
                () => OpenUrl(r.PageUrl));
        }
    }

    /// <summary>Не перезапускаемся посреди синхронизации или открытого окна — дождёмся следующей проверки.</summary>
    private bool Busy => _engine.Status.State is SyncState.Syncing or SyncState.Pending || _dialog is LoginForm || _asking;

    private async void InstallUpdate(ReleaseInfo r, bool manual)
    {
        if (_updating || Handoff != null || Updater.ExePath is not { } exe) return;
        if (!manual && (Busy || _dialog != null)) return;
        _updating = true;
        try
        {
            Balloon("Найдено обновление YaDisk File Sync",
                $"Версия {r.Version.ToString(3)}. Устанавливаю — программа перезапустится через несколько секунд.", ToolTipIcon.Info,
                () => OpenUrl(r.PageUrl));
            string file = await Updater.DownloadAsync(r, exe);
            await Task.Delay(UpdateNoticeDelay);
            if (Busy || (!manual && (_dialog != null || !_settings.AutoUpdate || CheckInterval(_settings.UpdateCheckHours) == null)))
            {
                try { File.Delete(file); } catch { }
                Balloon("Обновление отложено", $"Версию {r.Version.ToString(3)} можно установить из меню по правой кнопке.", ToolTipIcon.Info);
                return;
            }
            Updater.SaveNotes(r);
            Handoff = Updater.InstallAndStart(file, exe, r.Version);
            Exit();
        }
        catch (Exception e)
        {
            string msg = e is UpdateException ? e.Message : "Не удалось установить обновление: " + e.Message;
            if (manual || _lastAutoError != r.Tag + msg)
            {
                _lastAutoError = r.Tag + msg;
                Balloon("Обновление не установлено", msg, ToolTipIcon.Warning);
            }
        }
        finally { _updating = false; }
    }

    // ---------- прочее ----------

    private void Balloon(string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonAction = onClick;
        _tray.ShowBalloonTip(5000, title, Truncate(text, 250), icon);
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void Exit()
    {
        _tray.Visible = false;
        _dialog?.Close();
        _statusForm?.Close();
        Application.ExitThread();
    }

    public void Dispose()
    {
        _poll.Dispose();
        _updateTimer.Dispose();
        _watch?.Dispose();
        _engine.Dispose();
        _statusForm?.Dispose();
        _tray.Dispose();
        _menu.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
        _http.Dispose();
        _transfer.Dispose();
    }
}
