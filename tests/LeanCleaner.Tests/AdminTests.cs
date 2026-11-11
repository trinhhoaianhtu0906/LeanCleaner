using System.Diagnostics;
using LeanCleaner.Core;

namespace LeanCleaner.Tests;

/// <summary>Kiểm thử AdminTargets, AdminGuard và việc nối vào JunkRules (cây giả trong thư mục tạm).</summary>
public sealed class AdminTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "lc-admin-" + Guid.NewGuid().ToString("N"));
    private readonly KnownDirs _d;

    public AdminTests()
    {
        var win = Path.Combine(_base, "Win");
        foreach (var rel in new[] { "Temp", "LiveKernelReports", @"SoftwareDistribution\Download", "Temp2" })
            Directory.CreateDirectory(Path.Combine(win, rel));
        var user = Path.Combine(_base, "User");
        var local = Path.Combine(user, "AppData", "Local");
        Directory.CreateDirectory(local);
        _d = new KnownDirs(user, local, Path.Combine(_base, "UserTemp"), win);
    }

    /// <summary>Gỡ junction trước rồi xóa cây tạm.</summary>
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

    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private AdminGuard Guard() => new(_d);
    private JunkRule R(AdminOp op) => AdminTargets.RuleFor(op, _d);

    [Fact]
    public void AdminGuard_AllowsDirectChildOfEachRoot()
    {
        foreach (var op in AdminTargets.DeleteOps)
        {
            var root = AdminTargets.RootOf(op, _d);
            var f = Path.Combine(root, "a.tmp");
            var sub = Path.Combine(root, "subdir");
            File.WriteAllText(f, "x");
            Directory.CreateDirectory(sub);
            Assert.True(Guard().IsDeletable(f, R(op), out var reason), reason);
            Assert.True(Guard().IsDeletable(sub, R(op), out reason), reason);
        }
    }

    [Fact]
    public void AdminGuard_RejectsRootItself()
    {
        var root = AdminTargets.RootOf(AdminOp.WinTemp, _d);
        Assert.False(Guard().IsDeletable(root, R(AdminOp.WinTemp), out var reason));
        Assert.Equal("Ngoài vùng cho phép", reason);
    }

    [Fact]
    public void AdminGuard_RejectsGrandchild()
    {
        var root = AdminTargets.RootOf(AdminOp.WinTemp, _d);
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        var f = Path.Combine(root, "sub", "x.txt");
        File.WriteAllText(f, "x");
        Assert.False(Guard().IsDeletable(f, R(AdminOp.WinTemp), out var reason));
        Assert.Equal("Ngoài vùng cho phép", reason);
    }

    [Fact]
    public void AdminGuard_RejectsSiblingTemp2()
    {
        var f = Path.Combine(_d.WinDir, "Temp2", "x");
        File.WriteAllText(f, "x");
        Assert.False(Guard().IsDeletable(f, R(AdminOp.WinTemp), out var reason));
        Assert.Equal("Ngoài vùng cho phép", reason);
    }

    [Fact]
    public void AdminGuard_RejectsDotDotEscape()
    {
        var root = AdminTargets.RootOf(AdminOp.WinTemp, _d);
        File.WriteAllText(Path.Combine(_d.WinDir, "Temp2", "x"), "x");
        var p = root + @"\..\Temp2\x";
        Assert.False(Guard().IsDeletable(p, R(AdminOp.WinTemp), out var reason));
        Assert.Equal("Ngoài vùng cho phép", reason);
    }

    [Fact]
    public void AdminGuard_RejectsWildcard()
    {
        var root = AdminTargets.RootOf(AdminOp.WinTemp, _d);
        Assert.False(Guard().IsDeletable(Path.Combine(root, "*"), R(AdminOp.WinTemp), out var reason));
        Assert.Equal("Đường dẫn không hợp lệ", reason);
    }

    [Fact]
    public void AdminGuard_RejectsJunctionChild()
    {
        var root = AdminTargets.RootOf(AdminOp.WinTemp, _d);
        var target = Path.Combine(_base, "elsewhere");
        Directory.CreateDirectory(target);
        var link = Path.Combine(root, "link");
        Junction(link, target);
        Assert.False(Guard().IsDeletable(link, R(AdminOp.WinTemp), out var reason));
        Assert.Equal("Là liên kết (junction/symlink)", reason);
        Directory.Delete(link);
    }

    [Fact]
    public void AdminGuard_RejectsRuleWithoutNeedsAdmin()
    {
        var root = AdminTargets.RootOf(AdminOp.WinTemp, _d);
        var f = Path.Combine(root, "a.tmp");
        File.WriteAllText(f, "x");
        var rule = R(AdminOp.WinTemp) with { NeedsAdmin = false };
        Assert.False(Guard().IsDeletable(f, rule, out var reason));
        Assert.Equal("Không phải mục admin", reason);
    }

    [Fact]
    public void AdminGuard_RejectsMismatchedOp()
    {
        var f = Path.Combine(AdminTargets.RootOf(AdminOp.WinTemp, _d), "a.tmp");
        File.WriteAllText(f, "x");
        Assert.False(Guard().IsDeletable(f, R(AdminOp.LiveKernelReports), out var reason));
        Assert.Equal("Ngoài vùng cho phép", reason);
    }

    [Fact]
    public void FindVhdx_FindsDockerAndWslFiles_SkipsJunction()
    {
        var local = _d.LocalAppData;
        var dockerData = Path.Combine(local, "Docker", "wsl", "disk");
        Directory.CreateDirectory(dockerData);
        var dockerVhdx = Path.Combine(dockerData, "docker_data.vhdx");
        File.WriteAllText(dockerVhdx, "x");
        var ubuntu = Path.Combine(local, "Packages", "Ubuntu_1", "LocalState");
        Directory.CreateDirectory(ubuntu);
        var wslVhdx = Path.Combine(ubuntu, "ext4.vhdx");
        File.WriteAllText(wslVhdx, "x");

        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "evil.vhdx"), "x");
        Junction(Path.Combine(local, "Docker", "wsl", "jlink"), outside);

        var found = AdminTargets.FindVhdx(_d);
        Assert.Equal(2, found.Count);
        Assert.Contains(dockerVhdx, found);
        Assert.Contains(wslVhdx, found);
        Directory.Delete(Path.Combine(local, "Docker", "wsl", "jlink"));
    }

    [Fact]
    public void Build_AdminRulesAreCheckableWholeDir()
    {
        var vhdxDir = Path.Combine(_d.LocalAppData, "Docker", "wsl", "disk");
        Directory.CreateDirectory(vhdxDir);
        File.WriteAllText(Path.Combine(vhdxDir, "d.vhdx"), "x");

        var targets = JunkRules.Build(new AppSettings(), _d);
        var t = Assert.Single(targets, x => x.Rule.Op == AdminOp.WinTemp);
        Assert.Equal(TargetKind.WholeDir, t.Kind);
        Assert.True(t.Rule.NeedsAdmin);
        Assert.False(t.Rule.ReportOnly);
        Assert.Equal(3, targets.Count(x => x.Rule.NeedsAdmin));

        var v = Assert.Single(targets, x => x.Rule.Op == AdminOp.CompactVhdx);
        Assert.True(v.Rule.ReportOnly);
        Assert.Equal(TargetKind.ReportOnlySize, v.Kind);
    }
}
