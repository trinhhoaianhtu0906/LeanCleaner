using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("LeanCleaner.Tests")]

namespace LeanCleaner.Core;

/// <summary>Cổng an toàn duy nhất: mọi thao tác xóa đều phải được SafetyGuard chấp thuận.</summary>
public sealed class SafetyGuard : IDeleteGuard
{
    private readonly List<string> _excluded;
    private readonly List<string> _forbiddenExact;
    private readonly List<string> _forbiddenTrees;

    /// <summary>Tạo guard với danh sách loại trừ; vùng cấm lấy từ thư mục hệ thống thật.</summary>
    public SafetyGuard(IEnumerable<string> excludedPaths)
        : this(excludedPaths, RealForbiddenExact(), RealForbiddenTrees()) { }

    /// <summary>Dành cho kiểm thử: tự chỉ định vùng cấm.</summary>
    internal SafetyGuard(IEnumerable<string> excludedPaths, IEnumerable<string> hardForbiddenExact, IEnumerable<string> hardForbiddenTrees)
    {
        _excluded = NormalizeAll(excludedPaths);
        _forbiddenExact = NormalizeAll(hardForbiddenExact);
        _forbiddenTrees = NormalizeAll(hardForbiddenTrees);
    }

    /// <summary>Kiểm tra một đường dẫn có được phép xóa theo quy tắc không; reason là lý do từ chối.</summary>
    public bool IsDeletable(string path, JunkRule rule, out string reason)
    {
        ArgumentNullException.ThrowIfNull(rule);
        reason = "";
        if (!IsWellFormed(path)) { reason = "Đường dẫn không hợp lệ"; return false; }
        if (rule.ReportOnly) { reason = "Chỉ báo cáo"; return false; }
        if (rule.NeedsAdmin) { reason = "Cần quyền admin"; return false; }

        string full;
        try { full = Normalize(path); }
        catch (Exception) { reason = "Đường dẫn không hợp lệ"; return false; }

        // Rule 2: phải nằm dưới một allowed root (chọn root dài nhất khớp), không phải chính root.
        string? matchedRoot = null;
        foreach (var r in NormalizeAll(rule.AllowedRoots))
        {
            if (string.Equals(full, r, StringComparison.OrdinalIgnoreCase))
            { reason = "Ngoài vùng cho phép"; return false; }
            if (IsUnder(full, r) && (matchedRoot == null || r.Length > matchedRoot.Length))
                matchedRoot = r;
        }
        if (matchedRoot == null) { reason = "Ngoài vùng cho phép"; return false; }

        if (IsExcluded(full)) { reason = "Nằm trong thư mục loại trừ"; return false; }

        // Rule 4: vùng hệ thống.
        var pathRoot = Path.GetPathRoot(full);
        if ((pathRoot != null && string.Equals(full, Normalize(pathRoot), StringComparison.OrdinalIgnoreCase))
            || _forbiddenExact.Any(e => string.Equals(full, e, StringComparison.OrdinalIgnoreCase))
            || _forbiddenTrees.Any(t => IsUnder(full, t)))
        { reason = "Vùng hệ thống"; return false; }

        // Rule 5: reparse point ở chính path hoặc tổ tiên (từ cha lên tới allowed root, không gồm root).
        try
        {
            var cur = full;
            while (cur != null && !string.Equals(cur, matchedRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (IsReparse(cur)) { reason = "Là liên kết (junction/symlink)"; return false; }
                cur = Path.GetDirectoryName(cur);
            }
        }
        catch (Exception) { reason = "Không kiểm tra được thuộc tính"; return false; }

        return true;
    }

    /// <summary>True nếu path trùng, nằm dưới, hoặc là tổ tiên của một thư mục loại trừ (scanner dùng để cắt nhánh).</summary>
    public bool IsExcluded(string path)
    {
        string full;
        try { full = Normalize(path); }
        catch (Exception) { return true; } // không hợp lệ: coi như loại trừ cho an toàn
        foreach (var e in _excluded)
            if (IsUnder(full, e) || IsUnder(e, full)) return true;
        return false;
    }

    /// <summary>Chuẩn hóa đường dẫn: GetFullPath, đổi '/' thành '\', bỏ '\' cuối (giữ nguyên gốc như "C:\").</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Replace('/', '\\'));
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd('\\') : full;
    }

    /// <summary>Đường dẫn phải đầy đủ, không phải dạng device, không chứa ký tự không hợp lệ, wildcard hoặc ':' (ADS).</summary>
    internal static bool IsWellFormed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Replace('/', '\\');
        if (p.StartsWith("\\\\?\\") || p.StartsWith("\\\\.\\")) return false;
        if (!Path.IsPathFullyQualified(p)) return false;
        var rest = p[1] == ':' ? p.Substring(3) : p.Substring(2);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in rest)
            if (c != '\\' && (Array.IndexOf(invalid, c) >= 0 || c == '*' || c == '?' || c == ':')) return false;
        return true;
    }

    private static bool IsUnder(string child, string parent)
    {
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = parent.EndsWith('\\') ? parent : parent + "\\";
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparse(string p)
    {
        try { return File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static List<string> NormalizeAll(IEnumerable<string> paths)
    {
        var list = new List<string>();
        foreach (var p in paths)
        {
            if (!IsWellFormed(p)) continue;
            try { list.Add(Normalize(p)); } catch (Exception) { /* bỏ qua đường dẫn hỏng */ }
        }
        return list;
    }

    private static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    private static IEnumerable<string> RealForbiddenExact()
    {
        var profile = Env(Environment.SpecialFolder.UserProfile);
        foreach (var d in DriveInfo.GetDrives()) yield return d.RootDirectory.FullName;
        if (profile.Length == 0) yield break;
        yield return profile;
        yield return Path.Combine(profile, "AppData");
        yield return Path.Combine(profile, "AppData", "Local");
        yield return Path.Combine(profile, "AppData", "Roaming");
        var users = Path.GetDirectoryName(Normalize(profile));
        if (users != null) yield return users;
    }

    private static IEnumerable<string> RealForbiddenTrees()
    {
        var w6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        if (!string.IsNullOrEmpty(w6432)) yield return w6432;
        foreach (var f in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                                  Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
        {
            var p = Env(f);
            if (p.Length > 0) yield return p;
        }
    }
}
