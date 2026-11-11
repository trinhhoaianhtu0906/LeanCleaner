using System.Globalization;

namespace LeanCleaner.Core;

/// <summary>Nhật ký hành động chỉ-ghi-thêm, an toàn đa luồng, không bao giờ ném lỗi.</summary>
public sealed class AppLog
{
    private readonly string _filePath;
    private readonly object _lock = new();

    /// <summary>Tạo log ghi vào filePath.</summary>
    public AppLog(string filePath) => _filePath = filePath;

    /// <summary>Đường dẫn mặc định: %APPDATA%\LeanCleaner\log.txt.</summary>
    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeanCleaner", "log.txt");

    /// <summary>Ghi một dòng "thời gian, action, target, bytes, result" cách nhau bằng tab; lỗi IO bị bỏ qua.</summary>
    public void Write(string action, string target, long bytes, string result)
    {
        var line = string.Join('\t', DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Clean(action), Clean(target), bytes.ToString(CultureInfo.InvariantCulture), Clean(result));
        try
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
        }
        catch (Exception) { /* log không được làm hỏng thao tác chính */ }
    }

    private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
