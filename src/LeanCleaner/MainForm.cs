using LeanCleaner.Core;

namespace LeanCleaner;

/// <summary>Cửa sổ chính: tab RAM, tab Ổ đĩa và nút Cài đặt.</summary>
public class MainForm : Form
{
    private AppSettings _settings;
    private readonly RamPanel _ram;
    private readonly DiskPanel _disk;
    private bool _ramStarted;
    private bool _ramBusy, _diskBusy;
    private Button _btnSettings = null!;

    /// <summary>Tạo cửa sổ, nạp cấu hình và dựng giao diện.</summary>
    public MainForm()
    {
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "LeanCleaner";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); // title bar/taskbar use the exe's embedded icon
        ClientSize = new Size(760, 500);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;

        _settings = AppSettings.Load(AppSettings.DefaultFilePath);
        var log = new AppLog(AppLog.DefaultFilePath);
        _ram = new RamPanel(log) { Dock = DockStyle.Fill };
        _disk = new DiskPanel(log) { Dock = DockStyle.Fill };
        ApplySettings();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var tRam = new TabPage("RAM");
        var tDisk = new TabPage("Ổ đĩa");
        tRam.Controls.Add(_ram);
        tDisk.Controls.Add(_disk);
        tabs.TabPages.AddRange(new[] { tRam, tDisk });
        Controls.Add(tabs);

        var btnSettings = _btnSettings = new Button { Text = "⚙ Cài đặt", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnSettings.Location = new Point(ClientSize.Width - btnSettings.PreferredSize.Width - 6, 0);
        btnSettings.Click += (_, _) => OpenSettings();
        Controls.Add(btnSettings);
        btnSettings.BringToFront();

        _ram.BusyChanged += b => { _ramBusy = b; _btnSettings.Enabled = !_ramBusy && !_diskBusy; };
        _disk.BusyChanged += b => { _diskBusy = b; _btnSettings.Enabled = !_ramBusy && !_diskBusy; };
        Shown += (_, _) => StartRam();
        FormClosing += (_, e) =>
        {
            if (!_ramBusy && !_diskBusy) return;
            if (MessageBox.Show(this, "Đang xử lý, vẫn thoát?", "LeanCleaner", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            { e.Cancel = true; return; }
            _disk.CancelWork();
        };
    }

    private void StartRam()
    {
        if (_ramStarted) return;
        _ramStarted = true;
        _ram.ScanNow();
    }

    private void ApplySettings()
    {
        var guard = new SafetyGuard(_settings.ExcludedPaths);
        _ram.Configure(_settings);
        _disk.Configure(_settings, guard);
    }

    private void OpenSettings()
    {
        try
        {
            using var dlg = new SettingsForm(_settings);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _settings = AppSettings.Load(AppSettings.DefaultFilePath);
            ApplySettings();
            _disk.MarkStale();
            _ram.Rescan();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "LeanCleaner", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
