namespace LeanCleaner.Core;

/// <summary>Một dòng ở tab đĩa. ContentsOnly=true: xóa con của Path, giữ lại Path (thư mục cache).</summary>
public sealed record ScanItem(JunkRule Rule, string Path, long Bytes, bool IsDirectory, bool ContentsOnly);

/// <summary>Kết quả quét; Cancelled=true nếu bị hủy giữa chừng (Items là kết quả một phần).</summary>
public sealed record ScanResult(IReadOnlyList<ScanItem> Items, bool Cancelled);
