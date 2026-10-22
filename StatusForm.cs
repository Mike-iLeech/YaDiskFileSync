namespace YaDiskFileSync;

/// <summary>
/// «Статус синхронизации»: все файлы папки и что с ними происходит — процент и скорость передачи.
/// Список виртуальный (ListView.VirtualMode): тысячи файлов не тормозят обновление раз в полсекунды.
/// </summary>
internal sealed class StatusForm : Form
{
    private readonly SyncEngine _engine;
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, VirtualMode = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label _head = new() { Dock = DockStyle.Top, AutoSize = false, Height = 44, Padding = new Padding(8, 6, 8, 0) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private IReadOnlyList<StatusRow> _rows = Array.Empty<StatusRow>();
    // для скорости: сколько байт было у пути при прошлом замере
    private readonly Dictionary<string, (long Done, DateTime At, double Speed)> _speed = new(Planner.Paths);

    public StatusForm(SyncEngine engine)
    {
        _engine = engine;
        Text = "Статус синхронизации — YaDisk File Sync";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 520);
        MinimumSize = new Size(520, 300);
        Font = new Font("Segoe UI", 9.5f);
        Icon = IconArt.MakeIcon(32, IconArt.Blue);

        _list.Columns.Add("Файл", 330);
        _list.Columns.Add("Действие", 190);
        _list.Columns.Add("Состояние", 120);
        _list.Columns.Add("%", 55, HorizontalAlignment.Right);
        _list.Columns.Add("Скорость", 85, HorizontalAlignment.Right);
        _list.Columns.Add("Размер", 80, HorizontalAlignment.Right);
        _list.RetrieveVirtualItem += (_, e) => e.Item = Item(e.ItemIndex);

        Controls.Add(_list);
        Controls.Add(_head);
        _timer.Tick += (_, _) => Reload();
        Shown += (_, _) => { Reload(); _timer.Start(); };
    }

    private void Reload()
    {
        var now = DateTime.UtcNow;
        var rows = _engine.Rows();
        foreach (var r in rows)
        {
            if (r.State != RowState.Active) { _speed.Remove(r.Rel); continue; }
            if (_speed.TryGetValue(r.Rel, out var prev))
            {
                double sec = (now - prev.At).TotalSeconds;
                if (sec >= 0.4)
                {
                    double v = Math.Max(0, r.Done - prev.Done) / sec;
                    // сглаживаем, чтобы цифра не прыгала
                    _speed[r.Rel] = (r.Done, now, prev.Speed <= 0 ? v : prev.Speed * 0.6 + v * 0.4);
                }
            }
            else _speed[r.Rel] = (r.Done, now, 0);
        }
        _rows = rows.OrderBy(r => r.State switch
            {
                RowState.Active => 0, RowState.Failed => 1, RowState.Queued => 2, RowState.Done => 3, _ => 4,
            }).ThenBy(r => r.Rel, Planner.Paths).ToList();
        _list.VirtualListSize = _rows.Count;
        _list.Invalidate();

        var st = _engine.Status;
        int active = rows.Count(r => r.State is RowState.Active or RowState.Queued && r.Action.Length > 0);
        int failed = rows.Count(r => r.State == RowState.Failed);
        int synced = rows.Count(r => r.State is RowState.Synced or RowState.Done);
        _head.Text = TrayApp.StatusLine(st) + Environment.NewLine +
                     $"Файлов синхронизировано: {synced}" + (active > 0 ? $", в очереди: {active}" : "") + (failed > 0 ? $", с проблемами: {failed}" : "");
    }

    private ListViewItem Item(int i)
    {
        if (i < 0 || i >= _rows.Count) return new ListViewItem("");
        var r = _rows[i];
        string state = r.State switch
        {
            RowState.Active => "Идёт",
            RowState.Queued => r.Error ?? "Ожидает",
            RowState.Done => "Готово",
            RowState.Failed => r.Error ?? "Ошибка",
            _ => "Синхронизирован",
        };
        bool transfer = r.Total > 0 && r.State is RowState.Active or RowState.Done or RowState.Queued && r.Action.Length > 0;
        string pct = r.State is RowState.Synced or RowState.Done ? "100%"
            : r.Total > 0 ? $"{Math.Min(100, r.Done * 100 / r.Total)}%" : r.State == RowState.Active ? "…" : "";
        string speed = r.State == RowState.Active && _speed.TryGetValue(r.Rel, out var s) && s.Speed > 0 ? HumanSize(s.Speed) + "/с" : "";
        var item = new ListViewItem(r.Rel.Replace('/', '\\'));
        item.SubItems.Add(r.Action);
        item.SubItems.Add(state);
        item.SubItems.Add(transfer || r.State is RowState.Synced ? pct : "");
        item.SubItems.Add(speed);
        item.SubItems.Add(r.Total > 0 ? HumanSize(r.Total) : "");
        if (r.State == RowState.Failed) item.ForeColor = Color.FromArgb(190, 40, 40);
        else if (r.State == RowState.Active) item.ForeColor = Color.FromArgb(30, 100, 200);
        return item;
    }

    internal static string HumanSize(double bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return u == 0 ? $"{bytes:0} {units[u]}" : $"{bytes:0.#} {units[u]}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
