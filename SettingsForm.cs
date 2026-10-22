using System.Diagnostics;

namespace YaDiskFileSync;

/// <summary>Что пользователь выбрал в окне настроек.</summary>
internal sealed record SettingsChoice(string LocalRoot, string RemoteRoot, int PollSeconds, bool ShowInExplorer, bool Autostart,
    int UpdateCheckHours, bool AutoUpdate);

/// <summary>Окно настроек: учётная запись, папка на компьютере, папка на Диске, синхронизация, обновления.</summary>
internal sealed class SettingsForm : Form
{
    private readonly Label _account = new() { AutoSize = true, MaximumSize = new Size(380, 0) };
    private readonly Button _login = new() { AutoSize = true };
    private readonly TextBox _local = new() { Width = 360, ReadOnly = true };
    private readonly TextBox _remote = new() { Width = 360 };
    private readonly ComboBox _poll = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly CheckBox _explorer = new() { AutoSize = true, Text = "Показывать папку в панели навигации Проводника" };
    private readonly CheckBox _autostart = new() { AutoSize = true, Text = "Запускать вместе с Windows" };
    private readonly ComboBox _updates = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly CheckBox _autoUpdate = new() { AutoSize = true, Text = "Устанавливать обновления сами" };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.FromArgb(200, 40, 40), MaximumSize = new Size(500, 0) };
    private readonly Func<bool> _signedIn;
    private readonly Func<string?> _accountName;

    public SettingsChoice? Choice { get; private set; }

    /// <param name="loginToggle">Войти (если не вошли) или выйти; окно после этого обновляет строку учётной записи.</param>
    /// <param name="listFolders">Подпапки на Диске для окна выбора; null — выбор недоступен (не вошли).</param>
    public SettingsForm(Settings s, bool autostart, Func<bool> signedIn, Func<string?> accountName, Action loginToggle,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? listFolders, Action checkUpdates)
    {
        _signedIn = signedIn;
        _accountName = accountName;
        Text = "Настройки — YaDisk File Sync";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = new Font("Segoe UI", 9.5f);
        Icon = IconArt.MakeIcon(32, IconArt.Blue);

        _local.Text = s.LocalRoot ?? Settings.DefaultLocalRoot;
        _remote.Text = DiskPath.Display(s.RemoteRoot ?? Settings.DefaultRemoteRoot);
        foreach (int sec in Settings.PollChoices) _poll.Items.Add(Settings.PollName(sec));
        _poll.SelectedIndex = Math.Max(0, Array.IndexOf(Settings.PollChoices, s.PollSeconds) is var i && i >= 0 ? i : 1);
        _explorer.Checked = s.ShowInExplorer;
        _autostart.Checked = autostart;
        foreach (int h in TrayApp.UpdateCheckChoices) _updates.Items.Add(TrayApp.IntervalName(h));
        _updates.SelectedIndex = Math.Max(0, Array.IndexOf(TrayApp.UpdateCheckChoices, s.UpdateCheckHours));
        _autoUpdate.Checked = s.AutoUpdate;

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };

        // учётная запись
        _login.Click += (_, _) => { loginToggle(); RefreshAccount(); };
        root.Controls.Add(Group("Учётная запись Яндекса", Row(_account, _login)));
        RefreshAccount();

        // папка на компьютере
        var change = new Button { Text = "Изменить…", AutoSize = true };
        change.Click += (_, _) => PickLocal();
        var open = new Button { Text = "Открыть", AutoSize = true };
        open.Click += (_, _) => { if (Directory.Exists(_local.Text)) try { Process.Start("explorer.exe", $"\"{_local.Text}\""); } catch { } };
        root.Controls.Add(Group("Папка на компьютере", Row(_local, change, open),
            Note("Всё, что лежит в этой папке, синхронизируется с Диском в обе стороны.")));

        // папка на Диске
        var pick = new Button { Text = "Выбрать…", AutoSize = true, Enabled = listFolders != null };
        pick.Click += (_, _) =>
        {
            if (listFolders == null) return;
            using var dlg = new RemoteFolderPicker(listFolders, DiskPath.Normalize(_remote.Text));
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Selected is { } sel) _remote.Text = DiskPath.Display(sel);
        };
        root.Controls.Add(Group("Папка на Яндекс.Диске", Row(_remote, pick),
            Note("Например /YaDiskSync или /Документы/Бухгалтерия. Если такой папки нет — она будет создана.")));

        // синхронизация
        root.Controls.Add(Group("Синхронизация", Row(new Label { AutoSize = true, Text = "Проверять изменения на Диске:", Anchor = AnchorStyles.Left }, _poll),
            _explorer, _autostart));

        // обновления
        var check = new Button { Text = $"Проверить сейчас (версия {Updater.Current.ToString(3)})", AutoSize = true };
        check.Click += (_, _) => checkUpdates();
        root.Controls.Add(Group("Обновления программы", Row(new Label { AutoSize = true, Text = "Проверять:", Anchor = AnchorStyles.Left }, _updates),
            _autoUpdate, check));

        root.Controls.Add(_error);
        var ok = new Button { Text = "Сохранить", AutoSize = true };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        root.Controls.Add(Row(ok, cancel));
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void RefreshAccount()
    {
        bool inside = _signedIn();
        _account.Text = inside ? $"Вход выполнен: {_accountName() ?? "учётная запись Яндекса"}" : "Вход не выполнен.";
        _login.Text = inside ? "Выйти" : "Войти…";
    }

    private void PickLocal()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = $"Где создать папку {Settings.DefaultFolderName}",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        string cur = _local.Text;
        dlg.InitialDirectory = Path.GetDirectoryName(cur.TrimEnd('\\')) is { } p && Directory.Exists(p) ? p : "";
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _local.Text = FolderIn(dlg.SelectedPath);
    }

    /// <summary>Как у Яндекс.Диска: пользователь выбирает место, программа кладёт туда свою папку YaDiskSync.</summary>
    internal static string FolderIn(string chosen)
    {
        chosen = chosen.TrimEnd('\\');
        return Path.GetFileName(chosen).Equals(Settings.DefaultFolderName, StringComparison.OrdinalIgnoreCase)
            ? chosen : Path.Combine(chosen.Length == 2 && chosen[1] == ':' ? chosen + "\\" : chosen, Settings.DefaultFolderName);
    }

    private void Accept()
    {
        string local = _local.Text.Trim();
        string remote = DiskPath.Normalize(_remote.Text);
        if (ValidateLocal(local) is { } le) { _error.Text = le; return; }
        if (DiskPath.IsRoot(remote)) { _error.Text = "Укажите папку на Диске, а не весь Диск целиком."; return; }
        if (remote.Length > 1024) { _error.Text = "Слишком длинный путь на Диске."; return; }
        Choice = new SettingsChoice(Path.GetFullPath(local), remote, Settings.PollChoices[Math.Max(0, _poll.SelectedIndex)],
            _explorer.Checked, _autostart.Checked, TrayApp.UpdateCheckChoices[Math.Max(0, _updates.SelectedIndex)], _autoUpdate.Checked);
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Нельзя синхронизировать корень диска, папку Windows, весь профиль и служебную папку программы.</summary>
    internal static string? ValidateLocal(string path)
    {
        if (path.Length == 0) return "Выберите папку на компьютере.";
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch (Exception) { return "Неправильный путь к папке."; }
        bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        bool Under(string inner, string outer) => inner.StartsWith(outer.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Same(inner, outer);
        if (Path.GetPathRoot(full) is { } r && Same(full, r)) return "Нельзя синхронизировать весь диск — выберите папку.";
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (win.Length > 0 && Under(full, win)) return "Нельзя синхронизировать системную папку Windows.";
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0 && Same(full, profile)) return "Нельзя синхронизировать всю папку пользователя — выберите или создайте папку внутри.";
        if (Under(full, Settings.Dir) || Under(Settings.Dir, full)) return "Эта папка содержит служебные файлы программы — выберите другую.";
        foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            if (Environment.GetFolderPath(sf) is { Length: > 0 } pf && Under(full, pf)) return "Нельзя синхронизировать папку программ.";
        return null;
    }

    private static GroupBox Group(string title, params Control[] content)
    {
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        flow.Controls.AddRange(content);
        var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), MinimumSize = new Size(520, 0) };
        g.Controls.Add(flow);
        return g;
    }

    private static FlowLayoutPanel Row(params Control[] items)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        p.Controls.AddRange(items);
        return p;
    }

    private static Label Note(string text) =>
        new() { AutoSize = true, MaximumSize = new Size(500, 0), ForeColor = SystemColors.GrayText, Text = text };
}

/// <summary>Выбор папки на Яндекс.Диске: дерево, подпапки подгружаются при раскрытии.</summary>
internal sealed class RemoteFolderPicker : Form
{
    private readonly TreeView _tree = new() { Width = 420, Height = 380, HideSelection = false };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>> _list;
    private readonly CancellationTokenSource _cts = new();
    private const string Placeholder = "\u0001";

    public string? Selected { get; private set; }

    public RemoteFolderPicker(Func<string, CancellationToken, Task<IReadOnlyList<string>>> list, string current)
    {
        _list = list;
        Text = "Папка на Яндекс.Диске";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        Font = new Font("Segoe UI", 9.5f);

        var root = new TreeNode("Яндекс.Диск") { Tag = "disk:/" };
        root.Nodes.Add(Placeholder);
        _tree.Nodes.Add(root);
        _tree.BeforeExpand += async (_, e) => await LoadAsync(e.Node!);

        var ok = new Button { Text = "Выбрать", AutoSize = true };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (_tree.SelectedNode?.Tag is string p && !DiskPath.IsRoot(p)) { Selected = p; DialogResult = DialogResult.OK; Close(); }
            else _status.Text = "Выберите папку (не весь Диск).";
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        layout.Controls.AddRange(new Control[] { _tree, _status, buttons });
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += async (_, _) => { root.Expand(); await ExpandToAsync(current); };
    }

    private async Task LoadAsync(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Text != Placeholder) return;
        _status.Text = "Загружаю…";
        try
        {
            var names = await _list((string)node.Tag!, _cts.Token);
            if (IsDisposed) return;
            node.Nodes.Clear();
            foreach (string n in names)
            {
                var child = new TreeNode(n) { Tag = DiskPath.Combine((string)node.Tag!, n) };
                child.Nodes.Add(Placeholder);
                node.Nodes.Add(child);
            }
            _status.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!IsDisposed) _status.Text = "Не удалось загрузить: " + e.Message; }
    }

    private async Task ExpandToAsync(string path)
    {
        var node = _tree.Nodes[0];
        await LoadAsync(node);
        foreach (string part in DiskPath.Display(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = node.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (next == null) break;
            node = next;
            await LoadAsync(node);
            node.Expand();
        }
        _tree.SelectedNode = node;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
