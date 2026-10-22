using System.Diagnostics;
using System.Net.Http.Headers;

namespace YaDiskFileSync;

internal sealed class TrayApp : IDisposable
{
    /// <summary>Сверка файла с последней загрузкой на случай пропущенных событий (сон, сетевая папка).</summary>
    private static readonly TimeSpan SafetyCheck = TimeSpan.FromMinutes(10);
    internal static readonly int[] UpdateCheckChoices = { 1, 3, 6, 12, 24, 0 };
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateNoticeDelay = TimeSpan.FromSeconds(8);
    private const string DiskAppsUrl = "https://disk.yandex.ru/client/disk/%D0%9F%D1%80%D0%B8%D0%BB%D0%BE%D0%B6%D0%B5%D0%BD%D0%B8%D1%8F";

    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly Dictionary<Color, Icon> _icons = new();
    private readonly Settings _settings = Settings.Load();
    private readonly Secrets _secrets = Secrets.Load();
    private readonly object _saveLock = new();
    private readonly HttpClient _http;
    private readonly TokenSource _tokens;
    private readonly SyncEngine _engine;
    private readonly System.Windows.Forms.Timer _safety = new() { Interval = (int)SafetyCheck.TotalMilliseconds };
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 30_000 };
    private FileWatch? _watch;
    private LoginForm? _login;
    private SyncState? _shownProblem;   // о проблеме сообщаем один раз, а не на каждом повторе
    private string? _balloonUrl;
    private ReleaseInfo? _pendingUpdate;
    private bool _updating;
    private readonly Version? _rolledBack;
    private string? _lastAutoError;

    public UpdateHandoff? Handoff { get; private set; }

    public TrayApp(string? updatedFrom = null, string? rolledBack = null)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YaDiskFileSync", Updater.Current.ToString()));
        _tokens = new TokenSource(_secrets, OAuth, SaveSecrets);
        _engine = new SyncEngine(() => _secrets.SignedIn ? new DiskClient(_http, _tokens) : null, _settings, SaveSettings);
        _engine.StatusChanged += s => _ui.Post(_ => Show(s), null);

        _menu.Opening += (_, _) => BuildMenu();
        _tray.ContextMenuStrip = _menu;
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) SyncNow(); };
        _tray.BalloonTipClicked += (_, _) => OpenUrl(_balloonUrl);
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
            Balloon($"YaDisk File Sync обновлён до {Updater.Current.ToString(3)}", what, ToolTipIcon.Info,
                notes?.PageUrl is { Length: > 0 } url ? url : $"https://github.com/{Updater.Repo}/releases/latest");
        }

        Show(Initial());
        _engine.Start();
        Watch();
        _safety.Tick += (_, _) => { Watch(); _engine.Request(); };
        _safety.Start();

        _updateTimer.Tick += (_, _) =>
        {
            if (CheckInterval(_settings.UpdateCheckHours) is not { } every) { _updateTimer.Stop(); return; }
            _updateTimer.Interval = (int)every.TotalMilliseconds;
            CheckUpdates(manual: false);
        };
        ScheduleUpdateChecks();

        // первый запуск: сразу ведём по шагам — вход, затем выбор файла
        if (!_secrets.SignedIn) _ui.Post(_ => Login(), null);
        else if (string.IsNullOrEmpty(_settings.FilePath)) _ui.Post(_ => ChooseFile(), null);
    }

    private YandexOAuth? OAuth() =>
        string.IsNullOrEmpty(_settings.ClientId) ? null : new YandexOAuth(_http, _settings.ClientId, _secrets.ClientSecret);

    private SyncStatus Initial() =>
        !_secrets.SignedIn ? new SyncStatus(SyncState.NeedLogin, "Войдите в Яндекс.", _settings.LastUploadedAt)
        : string.IsNullOrEmpty(_settings.FilePath) ? new SyncStatus(SyncState.NotConfigured, "Файл не выбран.", _settings.LastUploadedAt)
        : new SyncStatus(SyncState.Pending, "Проверяю файл…", _settings.LastUploadedAt);

    /// <summary>(Пере)подключает слежение за выбранным файлом и сразу сверяет его с Диском.</summary>
    private void Watch()
    {
        string? path = _settings.FilePath;
        if (_watch != null && _watchPath == path && _watchDirExists == DirExists(path)) return;
        _watch?.Dispose();
        _watch = null;
        _watchPath = path;
        _watchDirExists = DirExists(path);
        if (string.IsNullOrEmpty(path)) return;
        _watch = new FileWatch(path, () =>
        {
            _ui.Post(_ => { if (_engine.Status.State is not (SyncState.Uploading or SyncState.NeedLogin)) Show(new SyncStatus(SyncState.Pending, "Есть изменения…", _settings.LastUploadedAt)); }, null);
            _engine.Request();
        });
        _engine.Request();
    }

    private string? _watchPath;
    private bool _watchDirExists;
    private static bool DirExists(string? path) => path != null && Path.GetDirectoryName(path) is { } d && Directory.Exists(d);

    // ---------- состояние ----------

    private void Show(SyncStatus s)
    {
        var color = IconArt.For(s.State);
        if (!_icons.TryGetValue(color, out var icon))
            _icons[color] = icon = IconArt.MakeIcon(Math.Max(16, SystemInformation.SmallIconSize.Width), color);
        _tray.Icon = icon;
        _tray.Text = Truncate("YaDisk File Sync\n" + StatusLine(s), 127);

        if (s.State is SyncState.Error or SyncState.NeedLogin)
        {
            if (_shownProblem != s.State && _login == null)
                Balloon(s.State == SyncState.NeedLogin ? "Нужно войти в Яндекс" : "Файл не синхронизирован", s.Message, ToolTipIcon.Warning);
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
        if (string.IsNullOrEmpty(_settings.FilePath)) { ChooseFile(); return; }
        // «синхронизировать» вручную — загрузить даже без изменений (вдруг файл на Диске удалили)
        _settings.LastUploadedHash = null;
        Show(new SyncStatus(SyncState.Pending, "Синхронизирую…", _settings.LastUploadedAt));
        _engine.Request();
    }

    private void ChooseFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Какой файл синхронизировать с Яндекс.Диском",
            Filter = "Книги Excel|*.xlsx;*.xlsm;*.xls;*.xlsb|Все файлы|*.*",
            CheckFileExists = true,
        };
        if (_settings.FilePath is { } cur && Path.GetDirectoryName(cur) is { } d && Directory.Exists(d)) dlg.InitialDirectory = d;
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _settings.FilePath = dlg.FileName;
        SaveSettings();
        Show(new SyncStatus(SyncState.Pending, "Проверяю файл…", _settings.LastUploadedAt));
        Watch();
    }

    private void Login()
    {
        if (_login != null) { _login.Activate(); return; }
        _login = new LoginForm(_http, _settings.ClientId, _secrets.ClientSecret);
        try
        {
            if (_login.ShowDialog() != DialogResult.OK || _login.Token is not { } t) return;
            _settings.ClientId = _login.ClientId;
            _secrets.ClientSecret = _login.ClientSecret.Length > 0 ? _login.ClientSecret : null;
            _secrets.AccessToken = t.AccessToken;
            _secrets.RefreshToken = t.RefreshToken;
            _secrets.ExpiresAt = t.ExpiresAt;
            SaveSecrets();
            _settings.LastUploadedHash = null;   // новый вход (может, другой аккаунт) — загрузить заново
            SaveSettings();
            Balloon("Вход выполнен", "Файл будет загружаться в папку «Приложения» на Яндекс.Диске.", ToolTipIcon.Info);
        }
        finally
        {
            _login.Dispose();
            _login = null;
        }
        if (string.IsNullOrEmpty(_settings.FilePath)) ChooseFile();
        else { Show(Initial()); _engine.Request(); }
    }

    private void Logout()
    {
        if (MessageBox.Show("Выйти из Яндекса? Файл перестанет загружаться на Диск, пока вы не войдёте снова.",
                "YaDisk File Sync", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _secrets.ClearTokens();
        SaveSecrets();
        Show(Initial());
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
        _menu.Items.Add(new ToolStripMenuItem("Синхронизировать сейчас", null, (_, _) => SyncNow())
        { Enabled = _secrets.SignedIn && _settings.FilePath != null, Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripSeparator());

        string fileText = _settings.FilePath is { } p ? "Файл: " + Truncate(Path.GetFileName(p), 50) : "Выбрать файл…";
        _menu.Items.Add(new ToolStripMenuItem(fileText, null, (_, _) => ChooseFile()) { ToolTipText = _settings.FilePath ?? "" });
        if (_settings.FilePath is { } path && File.Exists(path))
            _menu.Items.Add(new ToolStripMenuItem("Показать файл в папке", null,
                (_, _) => { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { } }));
        _menu.Items.Add(new ToolStripMenuItem("Открыть Яндекс.Диск (папка «Приложения»)", null, (_, _) => OpenUrl(DiskAppsUrl)));
        if (!_secrets.SignedIn || _engine.Status.State == SyncState.NeedLogin)
            _menu.Items.Add(new ToolStripMenuItem("Войти в Яндекс…", null, (_, _) => Login()));
        if (_secrets.SignedIn)
            _menu.Items.Add(new ToolStripMenuItem("Выйти из Яндекса", null, (_, _) => Logout()));

        var keep = new ToolStripMenuItem($"Хранить копий по дням: {_settings.KeepCopies}");
        foreach (int n in new[] { 7, 14, 30, 60, 90 })
        {
            int k = n;
            keep.DropDownItems.Add(new ToolStripMenuItem(k.ToString(), null, (_, _) => { _settings.KeepCopies = k; SaveSettings(); })
            { Checked = k == _settings.KeepCopies });
        }
        _menu.Items.Add(keep);
        _menu.Items.Add(new ToolStripSeparator());

        var auto = new ToolStripMenuItem("Запускать вместе с Windows") { Checked = SafeAutostart() };
        auto.Click += (_, _) =>
        {
            try { Autostart.Enabled = !auto.Checked; }
            catch (Exception ex) { Balloon("Автозапуск", ex.Message, ToolTipIcon.Error); }
        };
        _menu.Items.Add(auto);

        var checkEvery = new ToolStripMenuItem($"Проверять обновления: {IntervalName(_settings.UpdateCheckHours).ToLowerInvariant()}");
        foreach (int hours in UpdateCheckChoices)
        {
            int h = hours;
            checkEvery.DropDownItems.Add(new ToolStripMenuItem(IntervalName(h), null, (_, _) =>
            {
                if (_settings.UpdateCheckHours == h) return;
                _settings.UpdateCheckHours = h;
                SaveSettings();
                ScheduleUpdateChecks();
            }) { Checked = h == _settings.UpdateCheckHours });
        }
        _menu.Items.Add(checkEvery);
        var autoUpd = new ToolStripMenuItem("Устанавливать обновления сами") { Checked = _settings.AutoUpdate };
        autoUpd.Click += (_, _) => { _settings.AutoUpdate = !_settings.AutoUpdate; SaveSettings(); };
        _menu.Items.Add(autoUpd);
        if (_pendingUpdate is { } upd && Updater.IsNewer(upd))
            _menu.Items.Add(new ToolStripMenuItem($"Установить обновление {upd.Version.ToString(3)}", null,
                (_, _) => InstallUpdate(upd, manual: true)) { Enabled = !_updating });
        else
            _menu.Items.Add(new ToolStripMenuItem($"Проверить обновления (версия {Updater.Current.ToString(3)})", null,
                (_, _) => CheckUpdates(manual: true)) { Enabled = !_updating });
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
            if (manual) Balloon("Не удалось проверить обновления", e is UpdateException ? e.Message : e.Message, ToolTipIcon.Warning);
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
            Balloon("Доступно обновление", $"Версия {r.Version.ToString(3)}: ПКМ по значку → «Установить обновление».", ToolTipIcon.Info, r.PageUrl);
        }
    }

    /// <summary>Не перезапускаемся посреди загрузки файла — дождёмся следующей проверки.</summary>
    private bool Busy => _engine.Status.State is SyncState.Uploading or SyncState.Pending || _login != null;

    private async void InstallUpdate(ReleaseInfo r, bool manual)
    {
        if (_updating || Handoff != null || Updater.ExePath is not { } exe) return;
        if (!manual && Busy) return;
        _updating = true;
        try
        {
            Balloon("Найдено обновление YaDisk File Sync",
                $"Версия {r.Version.ToString(3)}. Устанавливаю — программа перезапустится через несколько секунд.", ToolTipIcon.Info, r.PageUrl);
            string file = await Updater.DownloadAsync(r, exe);
            await Task.Delay(UpdateNoticeDelay);
            if (Busy || (!manual && (!_settings.AutoUpdate || CheckInterval(_settings.UpdateCheckHours) == null)))
            {
                try { File.Delete(file); } catch { }
                Balloon("Обновление отложено", $"Версию {r.Version.ToString(3)} можно установить из меню по правой кнопке.", ToolTipIcon.Info, r.PageUrl);
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

    private void Balloon(string title, string text, ToolTipIcon icon, string? url = null)
    {
        _balloonUrl = url;
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
        Application.ExitThread();
    }

    public void Dispose()
    {
        _safety.Dispose();
        _updateTimer.Dispose();
        _watch?.Dispose();
        _engine.Dispose();
        _tray.Dispose();
        _menu.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
        _http.Dispose();
    }
}
