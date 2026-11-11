namespace LeanCleaner.Core;

/// <summary>Guard nghiêm ngặt cho helper admin: chỉ cho xóa con trực tiếp của 3 thư mục gốc cố định.</summary>
public sealed class AdminGuard : IDeleteGuard
{
    private readonly KnownDirs _d;

    /// <summary>Tạo guard cho bộ thư mục hệ thống đã biết.</summary>
    public AdminGuard(KnownDirs d) { _d = d; }

    /// <summary>Cho phép khi path là con trực tiếp của thư mục gốc đúng thao tác của rule và không phải liên kết.</summary>
    public bool IsDeletable(string path, JunkRule rule, out string reason)
    {
        ArgumentNullException.ThrowIfNull(rule);
        reason = "";
        if (!SafetyGuard.IsWellFormed(path)) { reason = "Đường dẫn không hợp lệ"; return false; }
        if (!rule.NeedsAdmin || rule.Op is not AdminOp op || Array.IndexOf(AdminTargets.DeleteOps, op) < 0)
        { reason = "Không phải mục admin"; return false; }

        try
        {
            var full = SafetyGuard.Normalize(path);
            var parent = Path.GetDirectoryName(full);
            if (parent == null
                || !string.Equals(SafetyGuard.Normalize(parent), SafetyGuard.Normalize(AdminTargets.RootOf(op, _d)),
                                  StringComparison.OrdinalIgnoreCase))
            { reason = "Ngoài vùng cho phép"; return false; }

            if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            { reason = "Là liên kết (junction/symlink)"; return false; }
        }
        catch (Exception) { reason = "Không kiểm tra được thuộc tính"; return false; }
        return true;
    }
}
