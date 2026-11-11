using System.Globalization;
using LeanCleaner.Core;

namespace LeanCleaner;

/// <summary>Tab RAM: liệt kê ứng dụng theo nhóm và đóng các mục đã chọn.</summary>
public class RamPanel : UserControl
{
    private readonly AppLog _log;
    private AppSettings _settings = AppSettings.CreateDefault();
    private readonly Button _scan = new() { Text = "Quét", AutoSize = true };
    private readonly Button _close = new() { Text = "Tắt các mục đã chọn", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Text = "RAM: …", Margin = new Padding(8, 7, 3, 0) };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true };
    private bool _busy;

    /// <summary>Báo khi bắt đầu/kết thúc một thao tác (true = đang bận).</summary>
    public event Action<bool>? BusyChanged;

    /// <summary>Tạo tab RAM.</summary>
    public RamPanel(AppLog log)
    {
        _log = log;
        _list.Columns.Add("Ứng dụng", 300);
        _list.Columns.Add("RAM", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Lý do", 160);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4), Controls = { _scan, _status } };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4), Controls = { _close } };
        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(bottom);
        _scan.Click += async (_, _) => await RunSafe(ScanAsync);
        _close.Click += async (_, _) => await RunSafe(CloseSelectedAsync);
    }

    /// <summary>Áp dụng cấu hình mới (danh sách app được bảo vệ).</summary>
    public void Configure(AppSettings settings) => _settings = settings;

    /// <summary>Quét lại danh sách (sau khi cài đặt đổi).</summary>
    public async void Rescan() => await RunSafe(ScanAsync);

    /// <summary>Quét ngay (dùng khi cửa sổ hiện lần đầu).</summary>
    public async void ScanNow() => await RunSafe(ScanAsync);

    private async Task RunSafe(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _scan.Enabled = _close.Enabled = false;
        BusyChanged?.Invoke(true);
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "LeanCleaner", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _busy = false; _scan.Enabled = _close.Enabled = true; BusyChanged?.Invoke(false); }
    }

    private async Task ScanAsync()
    {
        var prot = _settings.ProtectedProcesses.ToList();
        var (groups, mem) = await Task.Run(() => (ProcessScanner.Group(ProcessScanner.Snapshot(prot)), ProcessScanner.GetMemoryStatus()));
        const double GB = 1024.0 * 1024 * 1024;
        var used = mem.TotalBytes - mem.AvailableBytes;
        _status.Text = string.Format(CultureInfo.GetCultureInfo("vi-VN"), "RAM: {0:0.0}/{1:0.0} GB ({2}%)",
            used / GB, mem.TotalBytes / GB, mem.TotalBytes == 0 ? 0 : (int)Math.Round(100.0 * used / mem.TotalBytes));
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var g in groups)
        {
            var name = g.Pids.Count > 1 ? $"{g.Name} ({g.Pids.Count} tiến trình)" : g.Name;
            _list.Items.Add(new ListViewItem(new[] { name, Fmt.Bytes(g.TotalBytes), g.Reason }) { Tag = g, Checked = g.Suggested });
        }
        _list.EndUpdate();
    }

    private async Task CloseSelectedAsync()
    {
        var sel = _list.CheckedItems.Cast<ListViewItem>().Select(i => (AppGroup)i.Tag!).ToList();
        var prot = new HashSet<string>(_settings.ProtectedProcesses, StringComparer.OrdinalIgnoreCase);
        sel.RemoveAll(g => prot.Contains(g.Name));
        if (sel.Count == 0) return;
        var names = string.Join(", ", sel.Take(8).Select(g => g.Name)) + (sel.Count > 8 ? ", …" : "");
        if (MessageBox.Show(this, $"Đóng {sel.Count} ứng dụng: {names}? Dữ liệu chưa lưu có thể mất.", "LeanCleaner",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var closer = new ProcessCloser(_log);
        var alive = await Task.Run(() => closer.CloseGracefullyAsync(sel, TimeSpan.FromSeconds(5)));
        var needAdmin = false;
        foreach (var g in alive)
        {
            if (MessageBox.Show(this, $"“{g.Name}” chưa đóng (có thể đang chờ lưu hoặc chạy nền). Buộc tắt?", "LeanCleaner",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) continue;
            var killed = await Task.Run(() => closer.ForceKill(g));
            if (killed < g.Pids.Count) needAdmin = true;
        }
        if (needAdmin) MessageBox.Show(this, "Một số tiến trình cần quyền admin để tắt.", "LeanCleaner");
        await ScanAsync();
    }
}
