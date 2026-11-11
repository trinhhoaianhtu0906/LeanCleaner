using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LeanCleaner.Core;

/// <summary>Ảnh chụp một tiến trình: tên, pid, bộ nhớ riêng, có cửa sổ chính không.</summary>
public sealed record ProcSnapshot(string Name, int Pid, long PrivateBytes, bool HasWindow);

/// <summary>Các tiến trình cùng tên gộp thành một ứng dụng, kèm gợi ý đóng.</summary>
public sealed record AppGroup(string Name, IReadOnlyList<int> Pids, long TotalBytes, bool HasWindow, bool Suggested, string Reason);

/// <summary>Tình trạng RAM vật lý của máy.</summary>
public sealed record MemoryStatus(long TotalBytes, long AvailableBytes);

/// <summary>Chụp danh sách tiến trình, gom nhóm theo ứng dụng và đưa ra gợi ý.</summary>
public static class ProcessScanner
{
    /// <summary>Nhóm từ ngần này RAM trở lên được gợi ý là "Nặng".</summary>
    public const long HeavyBytes = 500L * 1024 * 1024;
    /// <summary>Nhóm không cửa sổ từ ngần này RAM trở lên được gợi ý là "Chạy nền".</summary>
    public const long BackgroundBytes = 200L * 1024 * 1024;

    /// <summary>Chụp tiến trình cùng phiên đăng nhập, bỏ tên được bảo vệ (không phân biệt hoa thường) và chính tiến trình này.</summary>
    public static IReadOnlyList<ProcSnapshot> Snapshot(IEnumerable<string> protectedNames)
    {
        var prot = new HashSet<string>(protectedNames, StringComparer.OrdinalIgnoreCase);
        var result = new List<ProcSnapshot>();
        using var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self.Id || p.SessionId != self.SessionId || prot.Contains(p.ProcessName)) continue;
                result.Add(new ProcSnapshot(p.ProcessName, p.Id, p.PrivateMemorySize64, p.MainWindowHandle != IntPtr.Zero));
            }
            catch (Exception) { /* tiến trình vừa thoát hoặc không đủ quyền */ }
            finally { p.Dispose(); }
        }
        return result;
    }

    /// <summary>Gộp theo tên (không phân biệt hoa thường), gợi ý đóng, sắp xếp RAM giảm dần.</summary>
    public static IReadOnlyList<AppGroup> Group(IEnumerable<ProcSnapshot> procs)
    {
        var map = new Dictionary<string, (string Name, List<int> Pids, long Total, bool Win)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs)
        {
            if (!map.TryGetValue(p.Name, out var g)) g = (p.Name, new List<int>(), 0, false);
            g.Pids.Add(p.Pid);
            map[p.Name] = (g.Name, g.Pids, g.Total + p.PrivateBytes, g.Win || p.HasWindow);
        }

        return map.Values
            .Select(g =>
            {
                var (suggested, reason) = g.Total >= HeavyBytes ? (true, "Nặng")
                    : !g.Win && g.Total >= BackgroundBytes ? (true, "Chạy nền")
                    : (false, "");
                return new AppGroup(g.Name, g.Pids, g.Total, g.Win, suggested, reason);
            })
            .OrderByDescending(g => g.TotalBytes)
            .ToList();
    }

    /// <summary>Đọc RAM vật lý tổng/còn trống qua GlobalMemoryStatusEx.</summary>
    public static MemoryStatus GetMemoryStatus()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref s)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new MemoryStatus((long)s.ullTotalPhys, (long)s.ullAvailPhys);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
