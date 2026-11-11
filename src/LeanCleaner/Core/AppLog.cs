using System.Globalization;
using System.Text;

namespace LeanCleaner.Core;

/// <summary>Nhật ký hành động chỉ-ghi-thêm, an toàn đa luồng, không bao giờ ném lỗi; ghi vào file hoặc giữ trong bộ nhớ.</summary>
public sealed class AppLog
{
    /// <summary>Độ dài tối đa một dòng khi nhận dòng từ helper admin.</summary>
    internal const int MaxForeignLineLength = 2000;

    private readonly string? _filePath;
    private readonly List<string>? _lines;
    private readonly object _lock = new();

    /// <summary>Tạo log ghi vào filePath.</summary>
    public AppLog(string filePath) => _filePath = filePath;

    private AppLog() => _lines = new List<string>();

    /// <summary>Tạo log chỉ giữ trong bộ nhớ (helper admin dùng để không ghi file vào thư mục người dùng).</summary>
    public static AppLog CreateInMemory() => new();

    /// <summary>Các dòng đã ghi khi ở chế độ bộ nhớ (bản sao); rỗng với log ghi file.</summary>
    public IReadOnlyList<string> Lines
    {
        get { lock (_lock) return _lines is null ? Array.Empty<string>() : _lines.ToArray(); }
    }

    /// <summary>Đường dẫn mặc định: %APPDATA%\LeanCleaner\log.txt.</summary>
    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeanCleaner", "log.txt");

    /// <summary>Ghi một dòng "thời gian, action, target, bytes, result" cách nhau bằng tab; lỗi IO bị bỏ qua.</summary>
    public void Write(string action, string target, long bytes, string result)
    {
        var line = string.Join('\t', DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Clean(action), Clean(target), bytes.ToString(CultureInfo.InvariantCulture), Clean(result));
        Append(new[] { line });
    }

    /// <summary>Ghi thêm các dòng nhận từ helper admin sau khi làm sạch (bỏ CR/LF/ký tự điều khiển, cắt độ dài); lỗi IO bị bỏ qua.</summary>
    public void WriteForeignLines(IEnumerable<string?> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var clean = lines.Select(SanitizeForeign).Where(l => l.Length > 0).ToList();
        if (clean.Count > 0) Append(clean);
    }

    /// <summary>Làm sạch một dòng từ nguồn không tin cậy: giữ tab, thay ký tự điều khiển (kể cả CR/LF, U+2028/U+2029) bằng khoảng trắng, cắt tối đa <see cref="MaxForeignLineLength"/> ký tự.</summary>
    internal static string SanitizeForeign(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, MaxForeignLineLength));
        foreach (var ch in s)
        {
            if (sb.Length >= MaxForeignLineLength) break;
            var bad = ch != '\t' && (char.IsControl(ch) || ch == (char)0x2028 || ch == (char)0x2029);
            sb.Append(bad ? ' ' : ch);
        }
        return sb.ToString().TrimEnd();
    }

    private void Append(IReadOnlyCollection<string> lines)
    {
        try
        {
            lock (_lock)
            {
                if (_lines is not null) { _lines.AddRange(lines); return; }
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_filePath!, string.Concat(lines.Select(l => l + Environment.NewLine)));
            }
        }
        catch (Exception) { /* log không được làm hỏng thao tác chính */ }
    }

    private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
