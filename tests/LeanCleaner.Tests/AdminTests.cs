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
    public void FindVhdx_SkipsJunctionedPackage()
    {
        var outside = Path.Combine(_base, "outside2");
        Directory.CreateDirectory(Path.Combine(outside, "LocalState"));
        File.WriteAllText(Path.Combine(outside, "LocalState", "ext4.vhdx"), "x");
        var packages = Path.Combine(_d.LocalAppData, "Packages");
        Directory.CreateDirectory(packages);
        var pkgLink = Path.Combine(packages, "Evil_1");
        Junction(pkgLink, outside);

        var realPkg = Path.Combine(packages, "Real_1");
        Directory.CreateDirectory(realPkg);
        var realState = Path.Combine(realPkg, "LocalState");
        Directory.CreateDirectory(Path.Combine(_base, "outside3"));
        File.WriteAllText(Path.Combine(_base, "outside3", "ext4.vhdx"), "x");
        Junction(realState, Path.Combine(_base, "outside3"));

        Assert.Empty(AdminTargets.FindVhdx(_d));
        Directory.Delete(pkgLink);
        Directory.Delete(realState);
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

/// <summary>Kiểm thử giao thức request/result giữa app thường và helper admin.</summary>
public sealed class AdminProtocolTests
{
    [Fact]
    public void Request_RoundTrip()
    {
        var r = new AdminRequest(new[] { AdminOp.WinTemp, AdminOp.CompactVhdx }, new[] { @"C:\a\b.vhdx" });
        var json = r.ToJson();
        Assert.Contains("\"ops\"", json);
        Assert.Contains("\"WinTemp\"", json);
        var back = AdminRequest.Parse(json);
        Assert.NotNull(back);
        Assert.Equal(r.Ops, back!.Ops);
        Assert.Equal(r.Vhdx, back.Vhdx);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"ops\":[1]}")]
    [InlineData("{\"ops\":[\"7\"]}")]
    [InlineData("{\"ops\":[\"-1\"]}")]
    [InlineData("{\"ops\":[\"WinTemp, CompactVhdx\"]}")]
    [InlineData("{\"ops\":[\"wintemp\"]}")]
    [InlineData("{\"ops\":[\" WinTemp\"]}")]
    [InlineData("{\"ops\":[null]}")]
    [InlineData("{\"ops\":[\"WinTemp\"],\"vhdx\":[null]}")]
    [InlineData("{\"ops\":[\"WinTemp\"],\"vhdx\":\"x\"}")]
    [InlineData("{\"ops\":[\"WinTemp\"],\"vhdx\":[1]}")]
    public void Request_InvalidJson_Null(string json) => Assert.Null(AdminRequest.Parse(json));

    [Fact]
    public void Request_NullInput_Null() => Assert.Null(AdminRequest.Parse(null!));

    [Fact]
    public void Request_UnknownOp_Null() => Assert.Null(AdminRequest.Parse("{\"ops\":[\"Bogus\"]}"));

    [Fact]
    public void Request_EmptyOps_Null()
    {
        Assert.Null(AdminRequest.Parse("{\"ops\":[]}"));
        Assert.Null(AdminRequest.Parse("{\"vhdx\":[]}"));
    }

    [Fact]
    public void Request_MissingVhdx_EmptyList()
    {
        var r = AdminRequest.Parse("{\"ops\":[\"WinTemp\"]}");
        Assert.NotNull(r);
        Assert.Empty(r!.Vhdx);
    }

    [Fact]
    public void Result_RoundTrip()
    {
        var r = new AdminResult(123, 4,
            new[] { new SkipInfo(@"C:\x", "lý do") },
            new[] { new CompactInfo(@"C:\a.vhdx", 100, 50, null), new CompactInfo(@"C:\b.vhdx", 1, 1, "lỗi") },
            null);
        var back = AdminResult.Parse(r.ToJson());
        Assert.NotNull(back);
        Assert.Equal(123, back!.RecycledBytes);
        Assert.Equal(4, back.Succeeded);
        Assert.Equal(r.Skipped, back.Skipped);
        Assert.Equal(r.Compacted, back.Compacted);
        Assert.Null(back.Error);
        Assert.Null(AdminResult.Parse("garbage"));
    }

    [Fact]
    public void FilterVhdx_DropsOutsidePaths()
    {
        var a = @"C:\Users\u\AppData\Local\Docker\wsl\disk\docker_data.vhdx";
        var skipped = new List<SkipInfo>();
        var kept = AdminRequest.FilterVhdx(
            new[] { a.ToUpperInvariant(), @"C:\Windows\System32\x.vhdx", "bad\0path" },
            new[] { a }, skipped);
        Assert.Single(kept);
        Assert.Equal(a, kept[0], ignoreCase: true);
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, s => Assert.Equal("Không nằm trong danh sách ổ ảo hợp lệ", s.Reason));
    }
}

/// <summary>Bộ nén ổ ảo giả, ghi lại các lệnh gọi.</summary>
internal sealed class FakeCompactor : IVhdxCompactor
{
    public int Shutdowns;
    public List<string> Compacted = new();
    public string? FailFirstWith;

    public void ShutdownWsl() => Shutdowns++;

    public CompactInfo Compact(string vhdxPath)
    {
        Compacted.Add(vhdxPath);
        if (FailFirstWith is not null && Compacted.Count == 1) return new CompactInfo(vhdxPath, 10, 10, FailFirstWith);
        return new CompactInfo(vhdxPath, 10, 5, null);
    }
}

/// <summary>Kiểm thử AdminRunner với cây giả, bộ nén giả và Thùng rác thật (file tạm).</summary>
public sealed class AdminRunnerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "lc-runner-" + Guid.NewGuid().ToString("N"));
    private readonly KnownDirs _d;
    private readonly string _ipc;
    private readonly AppLog _log;

    public AdminRunnerTests()
    {
        var win = Path.Combine(_base, "Win");
        foreach (var rel in new[] { "Temp", "LiveKernelReports", @"SoftwareDistribution\Download" })
            Directory.CreateDirectory(Path.Combine(win, rel));
        var user = Path.Combine(_base, "User");
        var local = Path.Combine(user, "AppData", "Local");
        Directory.CreateDirectory(local);
        _d = new KnownDirs(user, local, Path.Combine(_base, "UserTemp"), win);
        _ipc = Path.Combine(_base, "ipc");
        Directory.CreateDirectory(_ipc);
        _log = new AppLog(Path.Combine(_base, "log.txt"));
    }

    /// <summary>Xóa cây tạm.</summary>
    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

    private string WriteRequest(string json)
    {
        var p = Path.Combine(_ipc, "request.json");
        File.WriteAllText(p, json);
        return p;
    }

    private AdminResult ReadResult() =>
        AdminResult.Parse(File.ReadAllText(Path.Combine(_ipc, "result.json")))!;

    private int Run(string req, bool elevated, FakeCompactor c) =>
        AdminRunner.Run(req, _d, _log, () => elevated, c, _ => long.MaxValue);

    private string Vhdx(string rel)
    {
        var f = Path.Combine(_d.LocalAppData, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, "x");
        return f;
    }

    [Fact]
    public void Runner_NotElevated_Exit2_NothingDeleted()
    {
        var f = Path.Combine(AdminTargets.RootOf(AdminOp.LiveKernelReports, _d), "a.dmp");
        File.WriteAllText(f, "x");
        var req = WriteRequest(new AdminRequest(new[] { AdminOp.LiveKernelReports }, Array.Empty<string>()).ToJson());
        var c = new FakeCompactor();
        Assert.Equal(2, Run(req, false, c));
        Assert.True(File.Exists(f));
        Assert.False(string.IsNullOrEmpty(ReadResult().Error));
        Assert.Equal(0, c.Shutdowns);
    }

    [Fact]
    public void Runner_BadRequest_Exit3()
    {
        var req = WriteRequest("{ not json");
        Assert.Equal(3, Run(req, true, new FakeCompactor()));
        Assert.Equal("Yêu cầu không hợp lệ", ReadResult().Error);
        Assert.Equal(3, Run(Path.Combine(_ipc, "missing.json"), true, new FakeCompactor()));
    }

    [Fact]
    public void Runner_RecyclesChildrenOfRoot_KeepsRoot()
    {
        var root = AdminTargets.RootOf(AdminOp.LiveKernelReports, _d);
        File.WriteAllText(Path.Combine(root, "a.dmp"), "aaaa");
        File.WriteAllText(Path.Combine(root, "b.dmp"), "bbbb");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "c.dmp"), "cccc");
        var req = WriteRequest(new AdminRequest(new[] { AdminOp.LiveKernelReports }, Array.Empty<string>()).ToJson());

        Assert.Equal(0, Run(req, true, new FakeCompactor()));
        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.GetFileSystemEntries(root));
        var r = ReadResult();
        Assert.Null(r.Error);
        Assert.Equal(3, r.Succeeded);
    }

    [Fact]
    public void Runner_IgnoresVhdxOutsideAllowed()
    {
        var outside = Path.Combine(_base, "evil.vhdx");
        File.WriteAllText(outside, "x");
        var req = WriteRequest(new AdminRequest(new[] { AdminOp.CompactVhdx }, new[] { outside }).ToJson());
        var c = new FakeCompactor();
        Assert.Equal(0, Run(req, true, c));
        Assert.Equal(0, c.Shutdowns);
        Assert.Empty(c.Compacted);
        Assert.Contains(ReadResult().Skipped, s => s.Path == outside);
    }

    [Fact]
    public void Runner_CompactsAllowedVhdx_ShutdownOnce()
    {
        var a = Vhdx(@"Docker\wsl\disk\a.vhdx");
        var b = Vhdx(@"Docker\wsl\main\b.vhdx");
        var req = WriteRequest(new AdminRequest(new[] { AdminOp.CompactVhdx }, new[] { a, b }).ToJson());
        var c = new FakeCompactor();
        Assert.Equal(0, Run(req, true, c));
        Assert.Equal(1, c.Shutdowns);
        Assert.Equal(2, c.Compacted.Count);
        Assert.Equal(2, ReadResult().Compacted.Count);
    }

    [Fact]
    public void Runner_CompactErrorOnOne_ContinuesOthers()
    {
        var a = Vhdx(@"Docker\wsl\disk\a.vhdx");
        var b = Vhdx(@"Docker\wsl\main\b.vhdx");
        var req = WriteRequest(new AdminRequest(new[] { AdminOp.CompactVhdx }, new[] { a, b }).ToJson());
        var c = new FakeCompactor { FailFirstWith = "đang bị khóa" };
        Assert.Equal(0, Run(req, true, c));
        Assert.Equal(2, c.Compacted.Count);
        var r = ReadResult();
        Assert.Equal(2, r.Compacted.Count);
        Assert.Single(r.Compacted, x => x.Error == "đang bị khóa");
        Assert.Single(r.Compacted, x => x.Error is null);
    }
}
