namespace LeanCleaner.Core;

/// <summary>Cách quét một mục tiêu.</summary>
public enum TargetKind
{
    /// <summary>Thư mục cache: 1 dòng, ContentsOnly.</summary>
    WholeDir,
    /// <summary>1 dòng cho mỗi mục con trực tiếp đủ tuổi.</summary>
    TopLevelChildren,
    /// <summary>N file lớn nhất ít dùng.</summary>
    LargeFiles,
    /// <summary>1 dòng, chỉ báo kích thước.</summary>
    ReportOnlySize
}

/// <summary>Một mục tiêu quét gắn với quy tắc; Dir có thể là đường dẫn file (ReportOnlySize).</summary>
public sealed record RuleTarget(JunkRule Rule, string Dir, TargetKind Kind, int MinAgeDays = 0,
                                IReadOnlyList<string>? Extensions = null, string? FilePattern = null);

/// <summary>Các thư mục hệ thống đã biết, tách riêng để kiểm thử dùng cây giả.</summary>
public sealed record KnownDirs(string UserProfile, string LocalAppData, string Temp, string WinDir)
{
    /// <summary>Lấy từ môi trường hiện tại.</summary>
    public static KnownDirs Current() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.GetTempPath().TrimEnd('\\', '/'),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows));
}

/// <summary>Danh mục quy tắc dọn rác cố định.</summary>
public static class JunkRules
{
    /// <summary>Nhóm hiển thị theo thứ tự.</summary>
    public static readonly string[] GroupOrder = { "Rác cố định", "File lớn ít dùng", "Bộ cài cũ", "Cache lập trình" };

    private const string GFixed = "Rác cố định", GLarge = "File lớn ít dùng", GInst = "Bộ cài cũ", GDev = "Cache lập trình";
    private static readonly string[] InstallerExt = { ".exe", ".msi", ".zip", ".7z", ".rar", ".iso" };
    private static readonly string[] CacheNames = { "Cache", "Code Cache", "GPUCache" };

    private static readonly EnumerationOptions SafeOpts = new() { IgnoreInaccessible = true };

    /// <summary>Temp chỉ hợp lệ khi không trùng/chứa hồ sơ người dùng và không phải gốc ổ đĩa (TEMP cấu hình sai).</summary>
    private static bool IsSafeTemp(KnownDirs d)
    {
        static string N(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            var temp = N(d.Temp);
            var prof = N(d.UserProfile);
            if (string.Equals(N(Path.GetPathRoot(Path.GetFullPath(d.Temp)) ?? ""), temp, StringComparison.OrdinalIgnoreCase)) return false;
            if (prof.Equals(temp, StringComparison.OrdinalIgnoreCase)) return false;
            if (prof.StartsWith(temp + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Dựng danh sách mục tiêu; bỏ qua mục tiêu không tồn tại.</summary>
    public static IReadOnlyList<RuleTarget> Build(AppSettings s, KnownDirs d, CancellationToken ct = default)
    {
        var list = new List<RuleTarget>();
        string L(params string[] p) => Path.Combine(new[] { d.LocalAppData }.Concat(p).ToArray());
        void AddDir(string name, string group, string dir, TargetKind kind, RiskLevel risk, bool pre, bool reportOnly = false,
                    int age = 0, IReadOnlyList<string>? ext = null)
        {
            if (!Directory.Exists(dir)) return;
            list.Add(new RuleTarget(new JunkRule(name, group, new[] { dir }, risk, pre, reportOnly), dir, kind, age, ext));
        }

        if (IsSafeTemp(d))
            AddDir("File tạm (Temp)", GFixed, d.Temp, TargetKind.TopLevelChildren, RiskLevel.Low, true, age: s.TempAgeDays);

        foreach (var (label, rel) in new[] { ("Chrome", @"Google\Chrome"), ("Edge", @"Microsoft\Edge"), ("Comet", @"Perplexity\Comet") })
        {
            var userData = L(rel, "User Data");
            if (!Directory.Exists(userData)) continue;
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(userData, "*", SafeOpts))
                    foreach (var c in CacheNames)
                        AddDir($"Cache {label} ({Path.GetFileName(profile)}) - {c}", GFixed, Path.Combine(profile, c),
                               TargetKind.WholeDir, RiskLevel.Low, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* bỏ qua nguồn lỗi */ }
        }

        AddDir("CrashDumps", GFixed, L("CrashDumps"), TargetKind.WholeDir, RiskLevel.Low, true);
        AddDir("Windows Error Reporting", GFixed, L("Microsoft", "Windows", "WER"), TargetKind.WholeDir, RiskLevel.Low, true);
        AddDir("D3DSCache", GFixed, L("D3DSCache"), TargetKind.WholeDir, RiskLevel.Low, true);
        AddDir("INetCache", GFixed, L("Microsoft", "Windows", "INetCache"), TargetKind.WholeDir, RiskLevel.Low, true);

        foreach (var op in AdminTargets.DeleteOps)
        {
            var root = AdminTargets.RootOf(op, d);
            if (Directory.Exists(root))
                list.Add(new RuleTarget(AdminTargets.RuleFor(op, d), root, TargetKind.WholeDir));
        }

        AddDir("File lớn ít dùng", GLarge, d.UserProfile, TargetKind.LargeFiles, RiskLevel.Medium, false);
        AddDir("Bộ cài cũ (Downloads)", GInst, Path.Combine(d.UserProfile, "Downloads"), TargetKind.TopLevelChildren,
               RiskLevel.Medium, false, age: s.InstallerAgeDays, ext: InstallerExt);

        AddDir("Cache pip", GDev, L("pip", "cache"), TargetKind.WholeDir, RiskLevel.Low, false);
        AddDir("Cache npm", GDev, L("npm-cache"), TargetKind.WholeDir, RiskLevel.Low, false);
        AddDir("Cache NuGet", GDev, L("NuGet", "v3-cache"), TargetKind.WholeDir, RiskLevel.Low, false);
        AddDir("Cache Yarn", GDev, L("Yarn", "Cache"), TargetKind.WholeDir, RiskLevel.Low, false);

        foreach (var f in AdminTargets.FindVhdx(d, ct))
            list.Add(new RuleTarget(new JunkRule("Đĩa ảo Docker/WSL: " + Path.GetFileName(Path.GetDirectoryName(f)),
                GDev, new[] { f }, RiskLevel.None, false, true, Op: AdminOp.CompactVhdx), f, TargetKind.ReportOnlySize));

        return list;
    }
}
