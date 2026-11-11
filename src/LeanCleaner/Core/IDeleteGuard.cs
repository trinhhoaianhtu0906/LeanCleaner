namespace LeanCleaner.Core;

/// <summary>Cổng kiểm tra an toàn trước khi xóa; Deleter chỉ xóa khi guard chấp thuận.</summary>
public interface IDeleteGuard
{
    /// <summary>Kiểm tra một đường dẫn có được phép xóa theo quy tắc không; reason là lý do từ chối.</summary>
    bool IsDeletable(string path, JunkRule rule, out string reason);
}
