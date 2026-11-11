namespace LeanCleaner.Core;

/// <summary>Các mục tiêu cố định cần quyền admin: 3 thư mục rác của Windows và các file .vhdx Docker/WSL.</summary>
public static class AdminTargets
{
    /// <summary>Các thao tác admin có xóa nội dung thư mục (đưa vào Thùng rác).</summary>
    public static readonly AdminOp[] DeleteOps = { AdminOp.WinTemp, AdminOp.LiveKernelReports, AdminOp.WinUpdateDownload };

    private static readonly EnumerationOptions PackageOpts = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>Thư mục gốc cố định của một thao tác xóa; CompactVhdx không có gốc nên ném ArgumentException.</summary>
    public static string RootOf(AdminOp op, KnownDirs d) => op switch
    {
        AdminOp.WinTemp => Path.Combine(d.WinDir, "Temp"),
        AdminOp.LiveKernelReports => Path.Combine(d.WinDir, "LiveKernelReports"),
        AdminOp.WinUpdateDownload => Path.Combine(d.WinDir, "SoftwareDistribution", "Download"),
        _ => throw new ArgumentException("Thao tác không có thư mục gốc: " + op, nameof(op))
    };

    /// <summary>Quy tắc dọn rác tương ứng với một thao tác xóa admin.</summary>
    public static JunkRule RuleFor(AdminOp op, KnownDirs d)
    {
        var name = op switch
        {
            AdminOp.WinTemp => "Windows\\Temp",
            AdminOp.LiveKernelReports => "LiveKernelReports",
            AdminOp.WinUpdateDownload => "Windows Update Download",
            _ => throw new ArgumentException("Thao tác không có quy tắc xóa: " + op, nameof(op))
        };
        return new JunkRule(name, "Rác cố định", new[] { RootOf(op, d) }, RiskLevel.Low, false, false,
                            NeedsAdmin: true, Op: op);
    }

    /// <summary>Tìm các file .vhdx của Docker (Docker\wsl) và WSL (Packages\*\LocalState\ext4.vhdx), bỏ qua liên kết.</summary>
    public static IReadOnlyList<string> FindVhdx(KnownDirs d, CancellationToken ct = default)
    {
        var vhdx = new List<string>();
        var docker = Path.Combine(d.LocalAppData, "Docker", "wsl");
        try
        {
            if (!ct.IsCancellationRequested && Directory.Exists(docker))
                foreach (var f in Directory.EnumerateFiles(docker, "*.vhdx", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    if (ct.IsCancellationRequested) break;
                    vhdx.Add(f);
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* bỏ qua nguồn lỗi */ }
        var packages = Path.Combine(d.LocalAppData, "Packages");
        try
        {
            if (!ct.IsCancellationRequested && Directory.Exists(packages))
                foreach (var pkg in Directory.EnumerateDirectories(packages, "*", PackageOpts))
                {
                    if (ct.IsCancellationRequested) break;
                    var state = Path.Combine(pkg, "LocalState");
                    try { if (new DirectoryInfo(state).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                    var f = Path.Combine(state, "ext4.vhdx");
                    if (File.Exists(f)) vhdx.Add(f);
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* bỏ qua nguồn lỗi */ }
        return vhdx.Where(f =>
        {
            try { return !File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint); }
            catch (Exception) { return false; }
        }).ToList();
    }
}
