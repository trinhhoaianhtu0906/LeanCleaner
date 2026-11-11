using LeanCleaner.Core;

namespace LeanCleaner.Tests;

/// <summary>Kiểm thử Deleter trên cây thư mục tạm (dùng Thùng rác thật cho file nhỏ).</summary>
public sealed class DeleterTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "lc-del-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _logPath;
    private readonly AppLog _log;

    public DeleterTests()
    {
        _root = Path.Combine(_base, "root");
        Directory.CreateDirectory(_root);
        _logPath = Path.Combine(_base, "log", "log.txt");
        _log = new AppLog(_logPath);
    }

    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_base, "*", SearchOption.AllDirectories))
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(d);
            Directory.Delete(_base, true);
        }
        catch { }
    }

    private JunkRule Rule(bool reportOnly = false) =>
        new("t", "g", new[] { _root }, RiskLevel.Low, false, reportOnly);

    private static string MakeFile(string path, long len)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(len);
        return path;
    }

    private string LogText() => File.Exists(_logPath) ? File.ReadAllText(_logPath) : "";

    private sealed class FakeGuard : IDeleteGuard
    {
        public bool Allow;
        public List<string> Calls = new();
        public bool IsDeletable(string path, JunkRule rule, out string reason)
        { Calls.Add(path); reason = Allow ? "" : "nope"; return Allow; }
    }

    [Fact]
    public async Task CustomGuard_DenyBlocksDelete()
    {
        var f = MakeFile(Path.Combine(_root, "c.tmp"), 100);
        var g = new FakeGuard { Allow = false };
        var r = await new Deleter(g, _log).RecycleAsync(new[] { new ScanItem(Rule(), f, 100, false, false) }, CancellationToken.None);
        Assert.True(File.Exists(f));
        Assert.Contains(r.Skipped, s => s.Reason == "nope");
        Assert.Contains(f, g.Calls);
    }

    [Fact]
    public async Task CustomGuard_AllowRecycles()
    {
        var f = MakeFile(Path.Combine(_root, "d.tmp"), 100);
        var g = new FakeGuard { Allow = true };
        var r = await new Deleter(g, _log).RecycleAsync(new[] { new ScanItem(Rule(), f, 100, false, false) }, CancellationToken.None);
        Assert.False(File.Exists(f));
        Assert.Equal(1, r.Succeeded);
    }

    [Fact]
    public async Task File_item_is_recycled()
    {
        var f = MakeFile(Path.Combine(_root, "a.tmp"), 1234);
        var d = new Deleter(new SafetyGuard(Array.Empty<string>()), _log);
        var r = await d.RecycleAsync(new[] { new ScanItem(Rule(), f, 1234, false, false) }, CancellationToken.None);
        Assert.False(File.Exists(f));
        Assert.Equal(1, r.Succeeded);
        Assert.Equal(1234, r.BytesRecycled);
        Assert.Contains("RECYCLE", LogText());
    }

    [Fact]
    public async Task ContentsOnly_removes_children_keeps_dir()
    {
        var dir = Path.Combine(_root, "cache");
        MakeFile(Path.Combine(dir, "1.bin"), 10);
        MakeFile(Path.Combine(dir, "2.bin"), 20);
        MakeFile(Path.Combine(dir, "sub", "3.bin"), 30);
        var d = new Deleter(new SafetyGuard(Array.Empty<string>()), _log);
        var r = await d.RecycleAsync(new[] { new ScanItem(Rule(), dir, 60, true, true) }, CancellationToken.None);
        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
        Assert.Equal(3, r.Succeeded);
        Assert.Equal(60, r.BytesRecycled);
    }

    [Fact]
    public async Task Excluded_path_is_skipped()
    {
        var ex = Path.Combine(_root, "keep");
        var f = MakeFile(Path.Combine(ex, "x.tmp"), 5);
        var d = new Deleter(new SafetyGuard(new[] { ex }), _log);
        var r = await d.RecycleAsync(new[] { new ScanItem(Rule(), f, 5, false, false) }, CancellationToken.None);
        Assert.True(File.Exists(f));
        Assert.Equal(0, r.Succeeded);
        Assert.Single(r.Skipped);
        Assert.False(string.IsNullOrEmpty(r.Skipped[0].Reason));
        Assert.Contains("SKIP", LogText());
    }

    [Fact]
    public async Task ReportOnly_is_untouched()
    {
        var f = MakeFile(Path.Combine(_root, "r.tmp"), 5);
        var d = new Deleter(new SafetyGuard(Array.Empty<string>()), _log);
        var r = await d.RecycleAsync(new[] { new ScanItem(Rule(true), f, 5, false, false) }, CancellationToken.None);
        Assert.True(File.Exists(f));
        Assert.Equal(0, r.Succeeded);
        Assert.Equal("Chỉ báo cáo", r.Skipped[0].Reason);
    }

    [Fact]
    public async Task Junction_child_is_skipped()
    {
        var outside = Path.Combine(_base, "outside");
        var of = MakeFile(Path.Combine(outside, "keep.txt"), 7);
        var dir = Path.Combine(_root, "cache");
        Directory.CreateDirectory(dir);
        var link = Path.Combine(dir, "link");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var p = System.Diagnostics.Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        var d = new Deleter(new SafetyGuard(Array.Empty<string>()), _log);
        var r = await d.RecycleAsync(new[] { new ScanItem(Rule(), dir, 7, true, true) }, CancellationToken.None);
        Assert.Equal(0, r.Succeeded);
        Assert.True(File.Exists(of));
        Directory.Delete(link);
    }

    [Fact]
    public void QueryRecycleBinBytes_does_not_throw()
    {
        Assert.True(Deleter.QueryRecycleBinBytes() >= 0);
    }

    [Fact]
    public async Task Item_over_quota_is_skipped()
    {
        var f = MakeFile(Path.Combine(_root, "big.tmp"), 1000);
        var d = new Deleter(new SafetyGuard(Array.Empty<string>()), _log, _ => 500);
        var r = await d.RecycleAsync(new[] { new ScanItem(Rule(), f, 1000, false, false) }, CancellationToken.None);
        Assert.True(File.Exists(f));
        Assert.Equal(0, r.Succeeded);
        Assert.Equal("Quá lớn để vào Thùng rác — hãy tự xóa nếu cần", r.Skipped[0].Reason);
    }

    [Fact]
    public async Task Cancelled_token_recycles_nothing()
    {
        var f = MakeFile(Path.Combine(_root, "c.tmp"), 10);
        var dir = Path.Combine(_root, "cd");
        MakeFile(Path.Combine(dir, "x"), 10);
        var d = new Deleter(new SafetyGuard(Array.Empty<string>()), _log);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await d.RecycleAsync(new[] { new ScanItem(Rule(), f, 10, false, false),
                                             new ScanItem(Rule(), dir, 10, true, false) }, cts.Token);
        Assert.Equal(0, r.Succeeded);
        Assert.True(File.Exists(f));
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task File_held_open_is_skipped_without_dialog()
    {
        var f = MakeFile(Path.Combine(_root, "locked.tmp"), 10);
        var d = new Deleter(new SafetyGuard(Array.Empty<string>()), _log);
        using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var task = d.RecycleAsync(new[] { new ScanItem(Rule(), f, 10, false, false) }, CancellationToken.None);
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(task, done);
            var r = await task;
            Assert.True(File.Exists(f));
            Assert.Equal(0, r.Succeeded);
            Assert.Equal("Đang được dùng hoặc không có quyền", Assert.Single(r.Skipped).Reason);
        }
    }

    [Fact]
    public async Task ContentsOnly_item_that_is_junction_is_skipped()
    {
        var outside = Path.Combine(_base, "outside2");
        var of = MakeFile(Path.Combine(outside, "keep.txt"), 7);
        var link = Path.Combine(_root, "linkdir");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var p = System.Diagnostics.Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        var d = new Deleter(new SafetyGuard(Array.Empty<string>()), _log);
        var r = await d.RecycleAsync(new[] { new ScanItem(Rule(), link, 7, true, true) }, CancellationToken.None);
        Assert.Equal(0, r.Succeeded);
        Assert.True(File.Exists(of));
        Assert.Equal("Là liên kết (junction/symlink)", Assert.Single(r.Skipped).Reason);
        Directory.Delete(link);
    }

    [Fact]
    public async Task Progress_is_reported_for_items()
    {
        var f = MakeFile(Path.Combine(_root, "p.tmp"), 10);
        var d = new Deleter(new SafetyGuard(Array.Empty<string>()), _log);
        var seen = new List<string>();
        var r = await d.RecycleAsync(new[] { new ScanItem(Rule(), f, 10, false, false) }, CancellationToken.None, new SyncProgress(seen));
        Assert.Equal(1, r.Succeeded);
        Assert.Contains(f, seen);
    }

    private sealed class SyncProgress(List<string> l) : IProgress<string>
    {
        public void Report(string v) => l.Add(v);
    }
}
