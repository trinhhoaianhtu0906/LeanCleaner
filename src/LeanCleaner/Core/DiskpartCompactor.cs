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
            var script = Path.Combine(_workDir, "compact.txt");
            File.WriteAllText(script,
                $"select vdisk file=\"{vhdxPath}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\n");
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), $"/s \"{script}\"")
                { CreateNoWindow = true, UseShellExecute = false };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Không chạy được diskpart");
            if (!p.WaitForExit(30 * 60_000))
            {
                try { p.Kill(true); } catch (Exception) { /* bỏ qua */ }
                return new CompactInfo(vhdxPath, before, new FileInfo(vhdxPath).Length, "Quá thời gian");
            }
            var after = new FileInfo(vhdxPath).Length;
            return new CompactInfo(vhdxPath, before, after, p.ExitCode == 0 ? null : $"diskpart mã {p.ExitCode}");
        }
        catch (Exception e)
        {
            return new CompactInfo(vhdxPath, before, before, e.Message);
        }
    }
}
