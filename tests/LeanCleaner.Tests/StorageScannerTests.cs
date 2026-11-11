using System.Diagnostics;
using LeanCleaner.Core;

namespace LeanCleaner.Tests;

/// <summary>Kiểm thử StorageScanner trên cây KnownDirs giả trong thư mục tạm.</summary>
public sealed class StorageScannerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "lc-scan-" + Guid.NewGuid().ToString("N"));
    private readonly KnownDirs _dirs;
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-200);

    public StorageScannerTests()
    {
        _dirs = new KnownDirs(Path.Combine(_base, "User"), Path.Combine(_base, "Local"),
                              Path.Combine(_base, "Temp"), Path.Combine(_base, "Win"));
        foreach (var d in new[] { _dirs.UserProfile, _dirs.LocalAppData, _dirs.Temp, _dirs.WinDir })
            Directory.CreateDirectory(d);
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

    private static string MakeFile(string path, long len, DateTime? when = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(len);
        if (when is { } w) { File.SetLastWriteTimeUtc(path, w); File.SetLastAccessTimeUtc(path, w); }
        return path;
    }

    private StorageScanner Scanner(AppSettings? s = null, params string[] excluded) =>
        new(s ?? new AppSettings(), new SafetyGuard(excluded), _dirs);

    [Fact]
    public async Task WholeDir_SumsNestedFiles_ContentsOnly()
    {
        var cache = Path.Combine(_dirs.LocalAppData, "CrashDumps");
        MakeFile(Path.Combine(cache, "a.dmp"), 1000);
        MakeFile(Path.Combine(cache, "x", "y", "b.dmp"), 2500);
        var r = await Scanner().ScanAsync(null, CancellationToken.None);
        var item = Assert.Single(r.Items, i => i.Path == cache);
        Assert.Equal(3500, item.Bytes);
        Assert.True(item.ContentsOnly);
        Assert.True(item.IsDirectory);
    }

    [Fact]
    public async Task Temp_OldIncluded_FreshAndDirWithFreshChildExcluded()
    {
        var oldFile = MakeFile(Path.Combine(_dirs.Temp, "old.tmp"), 100, Old);
        var fresh = MakeFile(Path.Combine(_dirs.Temp, "fresh.tmp"), 100, DateTime.UtcNow);
        MakeFile(Path.Combine(_dirs.Temp, "dir", "old.tmp"), 100, Old);
        MakeFile(Path.Combine(_dirs.Temp, "dir", "new.tmp"), 100, DateTime.UtcNow);
        var oldDir = Path.Combine(_dirs.Temp, "olddir");
        MakeFile(Path.Combine(oldDir, "a.tmp"), 50, Old);
        var r = await Scanner().ScanAsync(null, CancellationToken.None);
        var paths = r.Items.Select(i => i.Path).ToList();
        Assert.Contains(oldFile, paths);
        Assert.Contains(oldDir, paths);
        Assert.DoesNotContain(fresh, paths);
        Assert.DoesNotContain(Path.Combine(_dirs.Temp, "dir"), paths);
        Assert.All(r.Items.Where(i => i.Path.StartsWith(_dirs.Temp)), i => Assert.False(i.ContentsOnly));
    }

    [Fact]
    public async Task Downloads_OldExeIncluded_OldTxtExcluded()
    {
        var dl = Path.Combine(_dirs.UserProfile, "Downloads");
        var exe = MakeFile(Path.Combine(dl, "setup.exe"), 100, Old);
        var txt = MakeFile(Path.Combine(dl, "notes.txt"), 100, Old);
        var r = await Scanner().ScanAsync(null, CancellationToken.None);
        Assert.Contains(r.Items, i => i.Path == exe && i.Rule.Group == "Bộ cài cũ");
        Assert.DoesNotContain(r.Items, i => i.Path == txt);
    }

    [Fact]
    public async Task LargeFiles_KeepsTopN()
    {
        var s = new AppSettings { LargeFileMinMB = 1, MaxLargeFiles = 2 };
        var a = MakeFile(Path.Combine(_dirs.UserProfile, "a.bin"), 2 * 1024 * 1024, Old);
        var b = MakeFile(Path.Combine(_dirs.UserProfile, "sub", "b.bin"), 4 * 1024 * 1024, Old);
        var c = MakeFile(Path.Combine(_dirs.UserProfile, "c.bin"), 3 * 1024 * 1024, Old);
        MakeFile(Path.Combine(_dirs.UserProfile, "tiny.bin"), 10, Old);
        MakeFile(Path.Combine(_dirs.UserProfile, "disk.vhdx"), 9 * 1024 * 1024, Old);
        var r = await Scanner(s).ScanAsync(null, CancellationToken.None);
        var large = r.Items.Where(i => i.Rule.Group == "File lớn ít dùng").ToList();
        Assert.Equal(new[] { b, c }, large.Select(i => i.Path).ToArray());
        Assert.DoesNotContain(r.Items, i => i.Path == a);
    }

    [Fact]
    public async Task ExcludedSubtree_NeverReported()
    {
        var s = new AppSettings { LargeFileMinMB = 1 };
        var docs = Path.Combine(_dirs.UserProfile, "Documents");
        var secret = MakeFile(Path.Combine(docs, "big.bin"), 2 * 1024 * 1024, Old);
        var r = await Scanner(s, docs).ScanAsync(null, CancellationToken.None);
        Assert.DoesNotContain(r.Items, i => i.Path.StartsWith(docs, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(r.Items, i => i.Path == secret);
    }

    [Fact]
    public async Task Junction_NotFollowed()
    {
        var target = Path.Combine(_base, "elsewhere");
        MakeFile(Path.Combine(target, "huge.bin"), 50_000);
        var cache = Path.Combine(_dirs.LocalAppData, "D3DSCache");
        MakeFile(Path.Combine(cache, "own.bin"), 1000);
        var link = Path.Combine(cache, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, RedirectStandardOutput = true, UseShellExecute = false };
        Process.Start(psi)!.WaitForExit();
        Assert.True(Directory.Exists(link));
        var r = await Scanner().ScanAsync(null, CancellationToken.None);
        Assert.Equal(1000, Assert.Single(r.Items, i => i.Path == cache).Bytes);
        Directory.Delete(link);
    }

    [Fact]
    public async Task PreCancelled_ReturnsCancelled()
    {
        MakeFile(Path.Combine(_dirs.LocalAppData, "CrashDumps", "a.dmp"), 100);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await Scanner().ScanAsync(null, cts.Token);
        Assert.True(r.Cancelled);
    }

    [Fact]
    public async Task AdminWinTemp_FoundAsAdminDeletable()
    {
        var wt = Path.Combine(_dirs.WinDir, "Temp");
        MakeFile(Path.Combine(wt, "z.tmp"), 700);
        var r = await Scanner().ScanAsync(null, CancellationToken.None);
        var item = Assert.Single(r.Items, i => i.Path == wt);
        Assert.False(item.Rule.ReportOnly);
        Assert.True(item.Rule.NeedsAdmin);
        Assert.Equal(700, item.Bytes);
    }

    [Fact]
    public async Task BrowserProfileCache_Found_AndDockerVhdxReported()
    {
        var cache = Path.Combine(_dirs.LocalAppData, "Google", "Chrome", "User Data", "Profile 1", "Cache");
        MakeFile(Path.Combine(cache, "f_1"), 300);
        var vhdx = MakeFile(Path.Combine(_dirs.LocalAppData, "Docker", "wsl", "data", "ext4.vhdx"), 5000);
        var r = await Scanner().ScanAsync(null, CancellationToken.None);
        Assert.Contains(r.Items, i => i.Path == cache && i.Rule.PreChecked);
        Assert.Contains(r.Items, i => i.Path == vhdx && i.Rule.ReportOnly && i.Bytes == 5000);
    }

    [Fact]
    public void DirSize_ReturnsNewestWrite()
    {
        var d = Path.Combine(_base, "ds");
        MakeFile(Path.Combine(d, "a"), 10, Old);
        var newer = DateTime.UtcNow.AddDays(-3);
        MakeFile(Path.Combine(d, "s", "b"), 20, newer);
        var st = StorageScanner.DirSize(d, new SafetyGuard(Array.Empty<string>()), CancellationToken.None, null);
        Assert.Equal(30, st.Bytes);
        Assert.True(Math.Abs((st.NewestWriteUtc - newer).TotalSeconds) < 2);
    }

    [Fact]
    public void JunkRules_Build_MissingLocalAppData_DoesNotThrow()
    {
        var d = new KnownDirs(_dirs.UserProfile, Path.Combine(_base, "NoSuchLocal"), _dirs.Temp, _dirs.WinDir);
        var list = JunkRules.Build(new AppSettings(), d);
        Assert.NotNull(list);
    }

    [Fact]
    public void JunkRules_Build_PreCancelled_ReturnsPromptly()
    {
        var docker = Path.Combine(_dirs.LocalAppData, "Docker", "wsl");
        MakeFile(Path.Combine(docker, "d", "x.vhdx"), 5, Old);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sw = Stopwatch.StartNew();
        var list = JunkRules.Build(new AppSettings(), _dirs, cts.Token);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(list, t => t.Dir.EndsWith(".vhdx"));
    }

    [Fact]
    public void JunkRules_Build_SkipsTempThatContainsProfileOrIsDriveRoot()
    {
        var s = new AppSettings();
        // Temp là tổ tiên của UserProfile
        var anc = new KnownDirs(_dirs.UserProfile, _dirs.LocalAppData, _base, _dirs.WinDir);
        Assert.DoesNotContain(JunkRules.Build(s, anc), t => t.Dir == _base);
        // Temp trùng UserProfile
        var same = new KnownDirs(_dirs.UserProfile, _dirs.LocalAppData, _dirs.UserProfile, _dirs.WinDir);
        Assert.DoesNotContain(JunkRules.Build(s, same), t => t.Kind == TargetKind.TopLevelChildren && t.Dir == _dirs.UserProfile);
        // Temp là gốc ổ đĩa
        var root = Path.GetPathRoot(_base)!;
        var drive = new KnownDirs(_dirs.UserProfile, _dirs.LocalAppData, root, _dirs.WinDir);
        Assert.DoesNotContain(JunkRules.Build(s, drive), t => t.Dir == root);
        // Temp bình thường vẫn có
        Assert.Contains(JunkRules.Build(s, _dirs), t => t.Dir == _dirs.Temp);
    }

    [Fact]
    public async Task AdminRoot_EmptyOrUnreadable_StillListed()
    {
        var lkr = Path.Combine(_dirs.WinDir, "LiveKernelReports");
        Directory.CreateDirectory(lkr);
        var r = await Scanner().ScanAsync(null, CancellationToken.None);
        var item = Assert.Single(r.Items, i => i.Path == lkr);
        Assert.Equal(0, item.Bytes);
        Assert.True(item.Rule.NeedsAdmin);
    }

    [Fact]
    public async Task NonAdminWholeDir_Empty_NotListed()
    {
        var d3d = Path.Combine(_dirs.LocalAppData, "D3DSCache");
        Directory.CreateDirectory(d3d);
        var r = await Scanner().ScanAsync(null, CancellationToken.None);
        Assert.DoesNotContain(r.Items, i => i.Path == d3d);
    }
}
