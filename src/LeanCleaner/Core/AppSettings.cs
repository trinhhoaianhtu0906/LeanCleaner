using System.Text.Json;

namespace LeanCleaner.Core;

/// <summary>Cấu hình ứng dụng, lưu dạng JSON trong %APPDATA%\LeanCleaner.</summary>
public sealed class AppSettings
{
    private const int DefaultLargeFileMinMB = 500;
    private const int DefaultLargeFileUnusedDays = 90;
    private const int DefaultInstallerAgeDays = 30;
    private const int DefaultTempAgeDays = 1;
    private const int DefaultMaxLargeFiles = 200;

    private static readonly string[] DefaultExcludedFolders =
    {
        "Documents", "Desktop", "Pictures", "Videos", "Music", "OneDrive",
        "Personal Project", "Backup", ".ssh", ".claude", ".gnupg"
    };

    private static readonly string[] DefaultProtected =
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "conhost", "audiodg",
        "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "TextInputHost", "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray",
        "LeanCleaner"
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Các thư mục không bao giờ được quét/xóa.</summary>
    public List<string> ExcludedPaths { get; set; } = new();
    /// <summary>Ngưỡng kích thước (MB) của file lớn.</summary>
    public int LargeFileMinMB { get; set; } = DefaultLargeFileMinMB;
    /// <summary>Số ngày không dùng để coi file lớn là ít dùng.</summary>
    public int LargeFileUnusedDays { get; set; } = DefaultLargeFileUnusedDays;
    /// <summary>Tuổi tối thiểu (ngày) của file cài đặt.</summary>
    public int InstallerAgeDays { get; set; } = DefaultInstallerAgeDays;
    /// <summary>Tuổi tối thiểu (ngày) của file tạm.</summary>
    public int TempAgeDays { get; set; } = DefaultTempAgeDays;
    /// <summary>Số file lớn tối đa giữ lại trong kết quả.</summary>
    public int MaxLargeFiles { get; set; } = DefaultMaxLargeFiles;
    /// <summary>Tên tiến trình (không .exe) không bao giờ bị đóng.</summary>
    public List<string> ProtectedProcesses { get; set; } = new();

    /// <summary>Tạo cấu hình mặc định.</summary>
    public static AppSettings CreateDefault()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new AppSettings
        {
            ExcludedPaths = DefaultExcludedFolders.Select(f => Path.Combine(profile, f)).ToList(),
            ProtectedProcesses = DefaultProtected.ToList()
        };
    }

    /// <summary>Đọc cấu hình; file thiếu/hỏng hoặc giá trị sai thì dùng mặc định (và ghi lại file mặc định khi file thiếu/hỏng).</summary>
    public static AppSettings Load(string filePath)
    {
        AppSettings? loaded = null;
        try
        {
            if (File.Exists(filePath))
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            loaded = null;
        }

        if (loaded == null)
        {
            var d = CreateDefault();
            TrySave(d, filePath);
            return d;
        }

        var def = CreateDefault();
        loaded.ExcludedPaths = CleanOrDefault(loaded.ExcludedPaths, def.ExcludedPaths);
        loaded.ProtectedProcesses = CleanOrDefault(loaded.ProtectedProcesses, def.ProtectedProcesses);
        if (loaded.LargeFileMinMB <= 0) loaded.LargeFileMinMB = DefaultLargeFileMinMB;
        if (loaded.LargeFileUnusedDays <= 0) loaded.LargeFileUnusedDays = DefaultLargeFileUnusedDays;
        if (loaded.InstallerAgeDays <= 0) loaded.InstallerAgeDays = DefaultInstallerAgeDays;
        if (loaded.TempAgeDays <= 0) loaded.TempAgeDays = DefaultTempAgeDays;
        if (loaded.MaxLargeFiles <= 0) loaded.MaxLargeFiles = DefaultMaxLargeFiles;
        return loaded;
    }

    /// <summary>Lưu cấu hình ra file JSON (tạo thư mục nếu cần).</summary>
    public void Save(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Đường dẫn mặc định: %APPDATA%\LeanCleaner\settings.json.</summary>
    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeanCleaner", "settings.json");

    private static List<string> CleanOrDefault(List<string>? list, List<string> fallback)
    {
        var cleaned = list?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        return cleaned == null || cleaned.Count == 0 ? fallback : cleaned;
    }

    private static void TrySave(AppSettings s, string filePath)
    {
        try { s.Save(filePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
