using System.Diagnostics;
using LeanCleaner.Core;

namespace LeanCleaner.Tests;

/// <summary>Kiểm thử SafetyGuard trên cây thư mục tạm.</summary>
public sealed class SafetyGuardTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "lc-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public SafetyGuardTests()
    {
        _root = Path.Combine(_base, "root");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "f.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_base, "elsewhere"));
        File.WriteAllText(Path.Combine(_base, "elsewhere", "e.txt"), "x");
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

    private static SafetyGuard Guard(IEnumerable<string>? excluded = null,
        IEnumerable<string>? exact = null, IEnumerable<string>? trees = null) =>
        new(excluded ?? Array.Empty<string>(), exact ?? Array.Empty<string>(), trees ?? Array.Empty<string>());

    [Fact]
    public void FileUnderAllowedRoot_IsDeletable()
    {
        Assert.True(Guard().IsDeletable(Path.Combine(_root, "sub", "f.txt"), Rule(), out var reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void AllowedRootItself_IsNotDeletable()
    {
        Assert.False(Guard().IsDeletable(_root, Rule(), out var reason));
        Assert.Equal("Ngoài vùng cho phép", reason);
        Assert.False(Guard().IsDeletable(_root + "\\", Rule(), out _));
    }

    [Fact]
    public void OutsideAllowedRoots_IsNotDeletable()
    {
        Assert.False(Guard().IsDeletable(Path.Combine(_base, "elsewhere", "e.txt"), Rule(), out var reason));
        Assert.Equal("Ngoài vùng cho phép", reason);
        // sibling with common prefix
        Assert.False(Guard().IsDeletable(_root + "x\\f.txt", Rule(), out _));
    }

    [Fact]
    public void Excluded_AndAncestorOfExcluded()
    {
        var ex = Path.Combine(_root, "sub");
        var g = Guard(new[] { ex });
        Assert.False(g.IsDeletable(ex, Rule(), out var reason));
        Assert.Equal("Nằm trong thư mục loại trừ", reason);
        Assert.False(g.IsDeletable(Path.Combine(ex, "f.txt"), Rule(), out _));
        Assert.True(g.IsExcluded(_root));          // ancestor
        Assert.True(g.IsExcluded(ex));
        Assert.True(g.IsExcluded(Path.Combine(ex, "f.txt")));
        Assert.False(g.IsExcluded(Path.Combine(_base, "elsewhere")));
    }

    [Fact]
    public void ExclusionIsCaseInsensitive()
    {
        var g = Guard(new[] { Path.Combine(_root, "SUB") });
        Assert.False(g.IsDeletable(Path.Combine(_root.ToLowerInvariant(), "sub", "f.txt"), Rule(), out _));
        Assert.True(g.IsExcluded(Path.Combine(_root, "sub")));
    }

    [Fact]
    public void Traversal_ResolvesOutsideAndIsDenied()
    {
        var p = Path.Combine(_root, "..", "elsewhere", "e.txt");
        Assert.False(Guard().IsDeletable(p, Rule(), out var reason));
        Assert.Equal("Ngoài vùng cho phép", reason);
        Assert.False(Guard().IsDeletable(Path.Combine(_root, "..", "..", "elsewhere"), Rule(), out _));
    }

    [Fact]
    public void ExclusionBoundary_PrefixSiblingNotExcluded()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bc"));
        File.WriteAllText(Path.Combine(_root, "bc", "file"), "x");
        var g = Guard(new[] { Path.Combine(_root, "b") });
        Assert.False(g.IsExcluded(Path.Combine(_root, "bc", "file")));
        Assert.True(g.IsDeletable(Path.Combine(_root, "bc", "file"), Rule(), out _));
    }

    [Fact]
    public void HardForbidden_ExactAndTree()
    {
        var sub = Path.Combine(_root, "sub");
        var g = Guard(exact: new[] { sub });
        Assert.False(g.IsDeletable(sub, Rule(), out var reason));
        Assert.Equal("Vùng hệ thống", reason);
        Assert.True(g.IsDeletable(Path.Combine(sub, "f.txt"), Rule(), out _)); // exact only

        var g2 = Guard(trees: new[] { sub });
        Assert.False(g2.IsDeletable(sub, Rule(), out _));
        Assert.False(g2.IsDeletable(Path.Combine(sub, "f.txt"), Rule(), out reason));
        Assert.Equal("Vùng hệ thống", reason);
    }

    [Fact]
    public void Junction_AndFileThroughJunction_AreDenied()
    {
        var link = Path.Combine(_root, "link");
        var target = Path.Combine(_base, "elsewhere");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var p = Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }

        var g = Guard();
        Assert.False(g.IsDeletable(link, Rule(), out var reason));
        Assert.Equal("Là liên kết (junction/symlink)", reason);
        Assert.False(g.IsDeletable(Path.Combine(link, "e.txt"), Rule(), out reason));
        Assert.Equal("Là liên kết (junction/symlink)", reason);
        Directory.Delete(link);
        Assert.True(File.Exists(Path.Combine(target, "e.txt")));
    }

    [Fact]
    public void NeedsAdminRule_IsRejected()
    {
        var rule = new JunkRule("x", "g", new[] { _root }, RiskLevel.Low, false, false, NeedsAdmin: true);
        var g = new SafetyGuard(Array.Empty<string>());
        Assert.False(g.IsDeletable(Path.Combine(_root, "sub", "f.txt"), rule, out var reason));
        Assert.Equal("Cần quyền admin", reason);
    }

    [Fact]
    public void ReportOnlyRule_IsDenied()
    {
        Assert.False(Guard().IsDeletable(Path.Combine(_root, "sub", "f.txt"), Rule(reportOnly: true), out var reason));
        Assert.Equal("Chỉ báo cáo", reason);
    }

    [Fact]
    public void NonExistingPath_EvaluatesRules1To4()
    {
        Assert.True(Guard().IsDeletable(Path.Combine(_root, "nope", "x.bin"), Rule(), out _));
    }

    [Fact]
    public void InvalidPath_IsDenied()
    {
        Assert.False(Guard().IsDeletable("C:\\a\0b", Rule(), out var reason));
        Assert.Equal("Đường dẫn không hợp lệ", reason);
    }

    [Fact]
    public void MalformedPaths_FailClosed()
    {
        var g = Guard();
        foreach (var p in new[] { _root + "\\nope\\*", _root + "\\sub\\*.txt", _root + "\\sub\\f.txt:ads",
                                  "sub\\f.txt", "C:foo", "\\\\?\\" + _root + "\\sub\\f.txt", "\\\\.\\" + _root, _root + "\\a?b" })
        {
            Assert.False(g.IsDeletable(p, Rule(), out var reason), p);
            Assert.Equal("Đường dẫn không hợp lệ", reason);
        }
    }

    [Fact]
    public void NullRule_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Guard().IsDeletable(_root, null!, out _));

    [Fact]
    public void RelativeExcludedPath_IsIgnored()
    {
        var g = Guard(new[] { "sub" });
        Assert.True(g.IsDeletable(Path.Combine(_root, "sub", "f.txt"), Rule(), out _));
    }

    [Fact]
    public void AncestorOfDeepExclusion_IsNotDeletable()
    {
        var g = Guard(new[] { Path.Combine(_root, "sub", "deep") });
        Assert.False(g.IsDeletable(Path.Combine(_root, "sub"), Rule(), out var reason));
        Assert.Equal("Nằm trong thư mục loại trừ", reason);
    }

    [Fact]
    public void Normalize_HandlesSlashesAndRoots()
    {
        Assert.Equal("C:\\a\\b", SafetyGuard.Normalize("C:/a/b/"));
        Assert.Equal("C:\\", SafetyGuard.Normalize("C:\\"));
        Assert.Equal("C:\\", SafetyGuard.Normalize("C:/"));
    }

    [Fact]
    public void RealCtor_DeniesDriveRootAndProfile()
    {
        var g = new SafetyGuard(Array.Empty<string>());
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rule = new JunkRule("t", "g", new[] { Path.GetPathRoot(profile)! }, RiskLevel.Low, false, false);
        Assert.False(g.IsDeletable(profile, rule, out var reason));
        Assert.Equal("Vùng hệ thống", reason);
        Assert.False(g.IsDeletable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "x.dll"), rule, out _));
    }
}
