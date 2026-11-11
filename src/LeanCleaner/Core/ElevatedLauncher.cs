using System.ComponentModel;
using System.Diagnostics;

namespace LeanCleaner.Core;

/// <summary>Chạy lại chính app với quyền admin (UAC) ở chế độ --admin và đọc kết quả.</summary>
public sealed class ElevatedLauncher
{
    private readonly AppLog _log;

    /// <summary>Tạo launcher dùng log đã cho.</summary>
    public ElevatedLauncher(AppLog log) => _log = log;

    /// <summary>Gửi yêu cầu cho helper admin; trả về null nếu người dùng từ chối UAC.</summary>
    public async Task<AdminResult?> RunAsync(AdminRequest req)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "LeanCleaner", "ipc", Guid.NewGuid().ToString("N"));
        try
        {
            if (dir.Contains('"')) throw new ArgumentException("Đường dẫn ipc không hợp lệ", nameof(req));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "request.json");
            await File.WriteAllTextAsync(path, req.ToJson());
            Process? p;
            try
            {
                p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--admin \"{path}\"")
                    { UseShellExecute = true, Verb = "runas" });
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                _log.Write("ADMIN", dir, 0, "UAC bị hủy");
                return null;
            }
            if (p is null)
                return new AdminResult(0, 0, Array.Empty<SkipInfo>(), Array.Empty<CompactInfo>(), "Không khởi động được tiến trình admin");
            using (p)
            {
                await p.WaitForExitAsync();
                AdminResult? r = null;
                try { r = AdminResult.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "result.json"))); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* thiếu file */ }
                return r ?? new AdminResult(0, 0, Array.Empty<SkipInfo>(), Array.Empty<CompactInfo>(),
                                            $"Tiến trình admin lỗi (mã {p.ExitCode})");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { /* bỏ qua */ }
        }
    }
}
