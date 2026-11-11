using LeanCleaner.Core;

namespace LeanCleaner;

/// <summary>Hộp thoại cài đặt (modal).</summary>
public class SettingsForm : Form
{
    private readonly AppSettings _s;
    private readonly ListBox _excluded = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly NumericUpDown _largeMb, _largeDays, _installerDays, _tempDays, _maxLarge;
    private readonly TextBox _protected = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true };

    /// <summary>Tạo hộp thoại từ cấu hình hiện tại; nút Lưu ghi ra AppSettings.DefaultFilePath.</summary>
    public SettingsForm(AppSettings settings)
    {
        _s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Cài đặt";
        ClientSize = new Size(520, 460);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label { Text = "Thư mục không bao giờ quét/xóa", AutoSize = true }, 0, 0);
        foreach (var p in _s.ExcludedPaths) _excluded.Items.Add(p);
        root.Controls.Add(_excluded, 0, 1);

        var btnAdd = new Button { Text = "Thêm…", AutoSize = true };
        var btnRemove = new Button { Text = "Bớt", AutoSize = true };
        btnAdd.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog();
            if (fb.ShowDialog(this) == DialogResult.OK && !_excluded.Items.Contains(fb.SelectedPath)) _excluded.Items.Add(fb.SelectedPath);
        };
        btnRemove.Click += (_, _) => { if (_excluded.SelectedIndex >= 0) _excluded.Items.RemoveAt(_excluded.SelectedIndex); };
        root.Controls.Add(new FlowLayoutPanel { AutoSize = true, Controls = { btnAdd, btnRemove } }, 0, 2);

        _largeMb = Num(_s.LargeFileMinMB, 50, 100000);
        _largeDays = Num(_s.LargeFileUnusedDays, 1, 3650);
        _installerDays = Num(_s.InstallerAgeDays, 1, 3650);
        _tempDays = Num(_s.TempAgeDays, 1, 365);
        _maxLarge = Num(_s.MaxLargeFiles, 10, 2000);
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Fill };
        AddPair(grid, "File lớn từ (MB)", _largeMb, "Không dùng quá (ngày)", _largeDays);
        AddPair(grid, "Bộ cài cũ hơn (ngày)", _installerDays, "Temp cũ hơn (ngày)", _tempDays);
        AddPair(grid, "Số file lớn tối đa", _maxLarge, null, null);
        root.Controls.Add(grid, 0, 3);

        var protBox = new GroupBox { Text = "App không bao giờ đề xuất tắt", Dock = DockStyle.Fill };
        _protected.Text = string.Join(Environment.NewLine, _s.ProtectedProcesses);
        protBox.Controls.Add(_protected);
        root.Controls.Add(protBox, 0, 4);

        var btnSave = new Button { Text = "Lưu", AutoSize = true };
        var btnCancel = new Button { Text = "Hủy", AutoSize = true, DialogResult = DialogResult.Cancel };
        btnSave.Click += (_, _) => Save();
        root.Controls.Add(new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Controls = { btnCancel, btnSave } }, 0, 5);
        Controls.Add(root);
        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }

    private static NumericUpDown Num(int value, int min, int max) =>
        new() { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 80 };

    private static void AddPair(TableLayoutPanel g, string l1, Control c1, string? l2, Control? c2)
    {
        g.Controls.Add(new Label { Text = l1, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) });
        g.Controls.Add(c1);
        g.Controls.Add(new Label { Text = l2 ?? "", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 6, 12, 3) });
        g.Controls.Add(c2 ?? new Label());
    }

    private void Save()
    {
        try
        {
            _s.ExcludedPaths = _excluded.Items.Cast<string>().ToList();
            _s.LargeFileMinMB = (int)_largeMb.Value;
            _s.LargeFileUnusedDays = (int)_largeDays.Value;
            _s.InstallerAgeDays = (int)_installerDays.Value;
            _s.TempAgeDays = (int)_tempDays.Value;
            _s.MaxLargeFiles = (int)_maxLarge.Value;
            _s.ProtectedProcesses = _protected.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            _s.Save(AppSettings.DefaultFilePath);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "LeanCleaner", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
