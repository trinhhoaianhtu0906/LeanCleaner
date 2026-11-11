using LeanCleaner.Core;

namespace LeanCleaner;

/// <summary>Tab Ổ đĩa: quét rác, chọn và chuyển vào Thùng rác.</summary>
public class DiskPanel : UserControl
{
    private readonly AppLog _log;
    private AppSettings _settings = AppSettings.CreateDefault();
    private SafetyGuard _guard = new(Array.Empty<string>());
    private readonly Button _scan = new() { Text = "Quét", AutoSize = true };
    private readonly Button _stop = new() { Text = "Dừng", AutoSize = true, Enabled = false };
    private readonly ProgressBar _bar = new() { Width = 120, Height = 18, Margin = new Padding(6, 6, 6, 0) };
    private readonly Label _progress = new() { AutoEllipsis = true, Width = 350, Margin = new Padding(3, 7, 3, 0) };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, ShowGroups = true };
    private readonly Label _selected = new() { AutoSize = true, Text = "Đã chọn: 0 MB", Margin = new Padding(3, 7, 12, 0) };
    private readonly Button _delete = new() { Text = "Xóa vào Thùng rác", AutoSize = true };
    private readonly Button _empty = new() { Text = "Dọn Thùng rác", AutoSize = true };
    private CancellationTokenSource? _cts;
    private bool _busy, _loading, _scanning;
    private string _note = "";

    /// <summary>Báo khi bắt đầu/kết thúc một thao tác (true = đang bận).</summary>
    public event Action<bool>? BusyChanged;

    /// <summary>Tạo tab đĩa.</summary>
    public DiskPanel(AppLog log)
    {
        _log = log;
        _list.Columns.Add("Mục", 430);
        _list.Columns.Add("Dung lượng", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Rủi ro", 70);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4), Controls = { _scan, _stop, _bar, _progress } };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4), Controls = { _selected, _delete, _empty } };
        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(bottom);
        _scan.Click += async (_, _) => await RunSafe(ScanAsync);
        _stop.Click += (_, _) => _cts?.Cancel();
        _delete.Click += async (_, _) => await RunSafe(DeleteAsync);
        _empty.Click += async (_, _) => await RunSafe(EmptyBinAsync);
        _list.ItemCheck += (_, e) =>
        {
            if (!_loading && _list.Items[e.Index].Tag is ScanItem { Rule.ReportOnly: true }) e.NewValue = e.CurrentValue;
        };
        _list.ItemChecked += (_, _) => { if (!_loading) UpdateSelected(); };
        HandleCreated += async (_, _) => await RefreshBinAsync();
    }

    /// <summary>Áp dụng cấu hình và guard mới.</summary>
    public void Configure(AppSettings settings, SafetyGuard guard) { _settings = settings; _guard = guard; }

    /// <summary>Xóa danh sách kết quả cũ (khi cài đặt đổi) và nhắc quét lại.</summary>
    public void MarkStale()
    {
        if (_busy) return;
        _loading = true;
        _list.Items.Clear();
        _loading = false;
        UpdateSelected();
        _note = "Cài đặt đã đổi — hãy quét lại";
        _progress.Text = _note;
    }

    /// <summary>Hủy thao tác đang chạy (dùng khi đóng cửa sổ).</summary>
    public void CancelWork() => _cts?.Cancel();

    private void SetBusy(bool busy, bool scanning = false)
    {
        _busy = busy;
        BusyChanged?.Invoke(busy);
        _scan.Enabled = _delete.Enabled = _empty.Enabled = !busy;
        _stop.Enabled = scanning;
        _bar.Style = scanning ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        _bar.MarqueeAnimationSpeed = scanning ? 30 : 0;
    }

    private async Task RunSafe(Func<Task> action)
    {
        if (_busy) return;
        SetBusy(true);
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "LeanCleaner", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { SetBusy(false); _progress.Text = _note; }
    }

    private async Task ScanAsync()
    {
        _note = "";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetBusy(true, scanning: true);
        _scanning = true;
        ScanResult result;
        try
        {
            var scanner = new StorageScanner(_settings, _guard, KnownDirs.Current());
            result = await scanner.ScanAsync(new Progress<string>(p => { if (_scanning) _progress.Text = p; }), _cts.Token);
        }
        finally { _scanning = false; }
        if (result.Cancelled) _note = "Quét bị dừng — kết quả chưa đầy đủ";
        Populate(result.Items, !result.Cancelled);
        await RefreshBinAsync();
    }

    private void Populate(IReadOnlyList<ScanItem> items, bool preCheck)
    {
        _loading = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();
        var groups = new Dictionary<string, ListViewGroup>();
        foreach (var name in JunkRules.GroupOrder.Concat(items.Select(i => i.Rule.Group)).Distinct())
        {
            var g = new ListViewGroup(name);
            groups[name] = g;
            _list.Groups.Add(g);
        }
        foreach (var it in items.OrderByDescending(i => i.Bytes))
        {
            var text = it.Path + (it.ContentsOnly ? " (nội dung)" : "") + (it.Rule.ReportOnly ? " — chỉ xem (cần admin)" : "");
            var risk = it.Rule.Risk switch { RiskLevel.Low => "Thấp", RiskLevel.Medium => "Vừa", _ => "—" };
            var li = new ListViewItem(new[] { text, Fmt.Bytes(it.Bytes), risk }) { Tag = it, Group = groups[it.Rule.Group] };
            if (it.Rule.ReportOnly) li.ForeColor = SystemColors.GrayText;
            else li.Checked = preCheck && it.Rule.PreChecked;
            _list.Items.Add(li);
        }
        _list.EndUpdate();
        _loading = false;
        UpdateSelected();
    }

    private List<ScanItem> SelectedItems() =>
        Selection.RemoveNested(_list.CheckedItems.Cast<ListViewItem>().Select(i => (ScanItem)i.Tag!).Where(i => !i.Rule.ReportOnly));

    private void UpdateSelected() => _selected.Text = "Đã chọn: " + Fmt.Bytes(SelectedItems().Sum(i => i.Bytes));

    private async Task DeleteAsync()
    {
        var sel = SelectedItems();
        if (sel.Count == 0) return;
        if (MessageBox.Show(this, $"Chuyển {sel.Count} mục ({Fmt.Bytes(sel.Sum(i => i.Bytes))}) vào Thùng rác?", "LeanCleaner",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetBusy(true, scanning: true);
        _scanning = true;
        DeleteResult res;
        try
        {
            res = await new Deleter(_guard, _log).RecycleAsync(sel, _cts.Token, new Progress<string>(p => { if (_scanning) _progress.Text = p; }));
        }
        finally { _scanning = false; SetBusy(true); }
        var msg = $"Đã chuyển {Fmt.Bytes(res.BytesRecycled)} vào Thùng rác. Bỏ qua {res.Skipped.Count} mục.";
        if (res.Skipped.Count > 0)
        {
            msg += Environment.NewLine + string.Join(Environment.NewLine, res.Skipped.Take(10).Select(s => $"• {s.Path}: {s.Reason}"));
            if (res.Skipped.Count > 10) msg += Environment.NewLine + "…";
        }
        MessageBox.Show(this, msg, "LeanCleaner");
        await ScanAsync();
    }

    private async Task EmptyBinAsync()
    {
        var bytes = await Task.Run(Deleter.QueryRecycleBinBytes);
        if (MessageBox.Show(this, $"Xóa vĩnh viễn {Fmt.Bytes(bytes)} trong Thùng rác? Không thể hoàn tác.", "LeanCleaner",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await Task.Run(() => new Deleter(_guard, _log).EmptyRecycleBin());
        await RefreshBinAsync();
    }

    private async Task RefreshBinAsync()
    {
        try
        {
            var bytes = await Task.Run(Deleter.QueryRecycleBinBytes);
            _empty.Text = $"Dọn Thùng rác ({Fmt.Bytes(bytes)})";
        }
        catch (Exception) { /* nhãn giữ nguyên */ }
    }
}
