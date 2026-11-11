using LeanCleaner.Core;

namespace LeanCleaner;

/// <summary>Xử lý tập mục được chọn.</summary>
public static class Selection
{
    /// <summary>Bỏ các mục trùng hoặc nằm bên trong một mục khác cũng được chọn (tránh tính/xóa hai lần).</summary>
    public static List<ScanItem> RemoveNested(IEnumerable<ScanItem> items)
    {
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ScanItem>();
        foreach (var (item, norm) in items.Select(i => (i, SafetyGuard.Normalize(i.Path))).OrderBy(t => t.Item2.Length))
        {
            var nested = false;
            for (var cur = norm; cur != null && !nested; cur = Path.GetDirectoryName(cur))
                nested = chosen.Contains(cur);
            if (nested) continue;
            chosen.Add(norm);
            result.Add(item);
        }
        return result;
    }
}
