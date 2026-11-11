namespace LeanCleaner.Core;

/// <summary>Mức rủi ro của một quy tắc dọn dẹp.</summary>
public enum RiskLevel { None, Low, Medium }

/// <summary>Một quy tắc dọn dẹp. AllowedRoots = các thư mục duy nhất mà quy tắc này được phép xóa bên dưới.</summary>
public sealed record JunkRule(string Name, string Group, IReadOnlyList<string> AllowedRoots,
                              RiskLevel Risk, bool PreChecked, bool ReportOnly);
