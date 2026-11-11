using System.Diagnostics;

namespace LeanCleaner.Core;

/// <summary>Thu gọn ổ ảo .vhdx và tắt WSL trước đó.</summary>
public interface IVhdxCompactor
{
    /// <summary>Tắt WSL để giải phóng các file .vhdx.</summary>
    void ShutdownWsl();

    /// <summary>Thu gọn một file .vhdx; không bao giờ ném, lỗi nằm trong <see cref="CompactInfo.Error"/>.</summary>
    CompactInfo Compact(string vhdxPath);
}

/// <summary>Thu gọn .vhdx bằng diskpart (attach readonly + compact vdisk).</summary>
public sealed class DiskpartCompactor : IVhdxCompactor
{
    private readonly string _workDir;

    /// <summary>Tạo bộ thu gọn; file script compact.txt được ghi vào <paramref name="workDir"/>.</summary>
    public DiskpartCompactor(string workDir) => _workDir = workDir;

    /// <summary>Chạy "wsl.exe --shutdown", không hiện cửa sổ, chờ tối đa 60 giây.</summary>
    public void ShutdownWsl()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wsl.exe"), "--shutdown")
            { CreateNoWindow = true, UseShellExecute = false };
        using var p = Process.Start(psi);
        if (p is null) return;
        if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch (Exception) { /* bỏ qua */ } }
    }

    /// <summary>Thu gọn một file .vhdx bằng diskpart, chờ tối đa 30 phút.</summary>
    public CompactInfo Compact(string vhdxPath)
    {
        long before = 0;
        try
        {
            before = new FileInfo(vhdxPath).Length;
            try
            {
                if (new DirectoryInfo(_workDir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return new CompactInfo(vhdxPath, before, before, "Thư mục tạm không an toàn");
            }
            catch (Exception)
            {
                return new CompactInfo(vhdxPath, before, before, "Thư mục tạm không an toàn");
            }
            var code = RunScript(
                $"select vdisk file=\"{vhdxPath}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\n",
                TimeSpan.FromMinutes(30));
            string? error = code switch { null => "Quá thời gian", 0 => null, _ => $"diskpart mã {code}" };
            if (error is not null)
            {
                // Best-effort: diskpart dừng ở lệnh lỗi / bị kill nên có thể còn attach; gỡ ra, bỏ qua kết quả.
                try { RunScript($"select vdisk file=\"{vhdxPath}\"\r\ndetach vdisk\r\n", TimeSpan.FromSeconds(60)); }
                catch (Exception) { /* bỏ qua */ }
            }
            return new CompactInfo(vhdxPath, before, new FileInfo(vhdxPath).Length, error);
        }
        catch (Exception e)
        {
            return new CompactInfo(vhdxPath, before, before, e.Message);
        }
    }

    /// <summary>Ghi script vào file mới (giữ khóa ghi tới khi diskpart thoát) rồi chạy diskpart; trả về mã thoát, hoặc null nếu quá thời gian (đã kill).</summary>
    private int? RunScript(string content, TimeSpan timeout)
    {
        var script = Path.Combine(_workDir, "compact-" + Guid.NewGuid().ToString("N") + ".txt");
        using var fs = new FileStream(script, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true))
        {
            w.Write(content);
            w.Flush();
        }
        fs.Flush();
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), $"/s \"{script}\"")
            { CreateNoWindow = true, UseShellExecute = false };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Không chạy được diskpart");
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(true); p.WaitForExit(10_000); } catch (Exception) { /* bỏ qua */ }
            return null;
        }
        return p.ExitCode;
    }
}
