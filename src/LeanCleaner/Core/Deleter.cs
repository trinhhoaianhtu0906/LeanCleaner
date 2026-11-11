using System.Runtime.InteropServices;
using System.Diagnostics;

namespace LeanCleaner.Core;

/// <summary>Kết quả xóa: số byte đã đưa vào Thùng rác, số mục thành công, danh sách mục bị bỏ qua kèm lý do.</summary>
public sealed record DeleteResult(long BytesRecycled, int Succeeded, IReadOnlyList<(string Path, string Reason)> Skipped);

/// <summary>Đưa mục người dùng chọn vào Thùng rác, kiểm tra lại SafetyGuard ngay trước mỗi lần xóa.</summary>
public sealed class Deleter
{
    private const string InUse = "Đang được dùng hoặc không có quyền";
    private static readonly EnumerationOptions ChildOpts = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private readonly SafetyGuard _guard;
    private readonly AppLog _log;
    private readonly Func<string, long> _quotaFor;
    private const string TooBig = "Quá lớn để vào Thùng rác — hãy tự xóa nếu cần";
    private const string BinOff = "Thùng rác của ổ này đang tắt";

    /// <summary>Tạo Deleter dùng guard và log đã cho.</summary>
    public Deleter(SafetyGuard guard, AppLog log) : this(guard, log, RecycleQuota) { }

    /// <summary>Dành cho kiểm thử: quotaFor(driveRoot) trả hạn mức Thùng rác (byte; 0 = tắt).</summary>
    internal Deleter(SafetyGuard guard, AppLog log, Func<string, long> quotaFor)
    { _guard = guard; _log = log; _quotaFor = quotaFor; }

    private static bool IsFixedDrive(string root)
    {
        try { return new DriveInfo(root).DriveType == DriveType.Fixed; }
        catch (Exception) { return false; }
    }

    /// <summary>Hạn mức Thùng rác của ổ (byte): đọc BitBucket trong registry; lỗi thì 5% dung lượng ổ; 0 nếu Thùng rác tắt.</summary>
    private static long RecycleQuota(string root)
    {
        long fallback = 0;
        try { fallback = new DriveInfo(root).TotalSize / 20; } catch (Exception) { }
        try
        {
            var sb = new System.Text.StringBuilder(64);
            if (!NativeMethods.GetVolumeNameForVolumeMountPointW(root, sb, sb.Capacity)) return fallback;
            var name = sb.ToString(); // dạng Volume{GUID}
            int i = name.IndexOf('{'), j = name.IndexOf('}');
            if (i < 0 || j < i) return fallback;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + name.Substring(i, j - i + 1));
            if (key == null) return fallback;
            if (key.GetValue("NukeOnDelete") is int nuke && nuke == 1) return 0;
            if (key.GetValue("MaxCapacity") is int mb && mb > 0) return mb * 1024L * 1024L;
        }
        catch (Exception) { }
        return fallback;
    }

    /// <summary>Đưa các mục vào Thùng rác (chạy nền); dừng giữa các mục khi ct bị hủy và trả phần đã làm.</summary>
    public Task<DeleteResult> RecycleAsync(IEnumerable<ScanItem> items, CancellationToken ct, IProgress<string>? progress = null) =>
        Task.Run(() => Recycle(items, ct, progress));

    /// <summary>Đưa một đường dẫn vào Thùng rác qua SHFileOperation, không hiện hộp thoại; true nếu mục đã biến mất.</summary>
    private static bool ShellRecycle(string path)
    {
        var op = new NativeMethods.SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = NativeMethods.FO_DELETE,
            pFrom = path + "\0\0",
            pTo = null,
            fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_NOERRORUI
                     | NativeMethods.FOF_SILENT | NativeMethods.FOF_WANTNUKEWARNING
        };
        NativeMethods.SHFileOperationW(ref op);
        return !File.Exists(path) && !Directory.Exists(path);
    }

    private DeleteResult Recycle(IEnumerable<ScanItem> items, CancellationToken ct, IProgress<string>? progress)
    {
        var sw = Stopwatch.StartNew();
        long lastReport = -1000;
        void Report(string path)
        {
            if (progress == null || sw.ElapsedMilliseconds - lastReport < 100) return;
            lastReport = sw.ElapsedMilliseconds;
            progress.Report(path);
        }

        long bytes = 0;
        int ok = 0;
        var skipped = new List<(string, string)>();

        void Skip(string path, string reason)
        {
            skipped.Add((path, reason));
            _log.Write("SKIP", path, 0, reason);
        }

        var quotaCache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        void Try(string path, JunkRule rule, bool isDir)
        {
            try
            {
                Report(path);
                long size = isDir ? StorageScanner.DirSize(path, _guard, ct, null).Bytes : new FileInfo(path).Length;
                if (ct.IsCancellationRequested) return; // số đo có thể dở dang: không xóa mục này

                var root = Path.GetPathRoot(path) ?? "";
                if (!IsFixedDrive(root)) { Skip(path, TooBig); return; }
                if (!quotaCache.TryGetValue(root, out var quota)) quotaCache[root] = quota = _quotaFor(root);
                if (quota <= 0) { Skip(path, BinOff); return; }
                if (size > quota) { Skip(path, TooBig); return; }

                if (!_guard.IsDeletable(path, rule, out var reason)) { Skip(path, reason); return; }
                if (!ShellRecycle(path)) { Skip(path, InUse); return; }
                bytes += size;
                ok++;
                _log.Write("RECYCLE", path, size, "ok");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                Skip(path, InUse);
            }
            catch (Exception e)
            {
                Skip(path, e.GetType().Name);
            }
        }

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;
            if (item.Rule.ReportOnly) { Skip(item.Path, "Chỉ báo cáo"); continue; }

            if (!item.ContentsOnly)
            {
                Try(item.Path, item.Rule, item.IsDirectory);
                continue;
            }

            try
            {
                if (File.GetAttributes(item.Path).HasFlag(FileAttributes.ReparsePoint))
                { Skip(item.Path, "Là liên kết (junction/symlink)"); continue; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Skip(item.Path, InUse); continue; }

            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(item.Path).EnumerateFileSystemInfos("*", ChildOpts).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Skip(item.Path, InUse); continue; }

            foreach (var c in children)
            {
                if (ct.IsCancellationRequested) break;
                Try(c.FullName, item.Rule, c is DirectoryInfo);
            }
        }
        return new DeleteResult(bytes, ok, skipped);
    }

    /// <summary>Tổng dung lượng Thùng rác (byte); 0 nếu lỗi.</summary>
    public static long QueryRecycleBinBytes()
    {
        try
        {
            var info = new NativeMethods.SHQUERYRBINFO { cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
            return NativeMethods.SHQueryRecycleBin(null, ref info) == 0 ? Math.Max(0, info.i64Size) : 0;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Làm rỗng Thùng rác (không hỏi lại) và ghi log; chỉ gọi khi người dùng chủ động yêu cầu.</summary>
    public void EmptyRecycleBin()
    {
        try
        {
            long before = QueryRecycleBinBytes();
            if (before == 0) return; // đã rỗng
            int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
            _log.Write("EMPTY_RECYCLE_BIN", "Recycle Bin", before, hr == 0 ? "ok" : $"hresult 0x{hr:X8}");
        }
        catch (Exception e) { _log.Write("EMPTY_RECYCLE_BIN", "Recycle Bin", 0, e.GetType().Name); }
    }

    private static class NativeMethods
    {
        public const uint FO_DELETE = 3;
        public const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCTW
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

        public const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeNameForVolumeMountPointW(string lpszVolumeMountPoint, System.Text.StringBuilder lpszVolumeName, int cchBufferLength);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
    }
}
