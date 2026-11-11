using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LeanCleaner.Core;

/// <summary>Thao tác file an toàn cho helper admin trong thư mục ipc do người dùng thường ghi được: không đi theo liên kết, chỉ tạo file mới, kiểm tra đường dẫn cuối.</summary>
internal static class SecureIpc
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint DELETE = 0x00010000;
    private const uint FILE_LIST_DIRECTORY = 0x1;
    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint CREATE_NEW = 1;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const int FileDispositionInfo = 4;
    private const int ProcessRedirectionTrustPolicy = 16;

    /// <summary>Mở thư mục ipc (không theo liên kết, không cho xóa/đổi tên khi còn giữ handle); null nếu không mở được, là reparse point, không phải thư mục hoặc đường dẫn cuối khác đường dẫn mong đợi (cha bị thay bằng liên kết).</summary>
    internal static SafeFileHandle? OpenDirNoReparse(string dir)
    {
        string expected;
        try { expected = SafetyGuard.Normalize(dir); }
        catch (Exception) { return null; }
        // Phải xin quyền đọc dữ liệu (FILE_LIST_DIRECTORY): handle chỉ có FILE_READ_ATTRIBUTES không tham gia kiểm tra chia sẻ nên không chặn được đổi tên.
        var h = CreateFileW(expected, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        try
        {
            if (!GetFileInformationByHandle(h, out var info)) { h.Dispose(); return null; }
            var attr = (FileAttributes)info.FileAttributes;
            if (attr.HasFlag(FileAttributes.ReparsePoint) || !attr.HasFlag(FileAttributes.Directory)
                || !string.Equals(FinalPath(h), expected, StringComparison.OrdinalIgnoreCase))
            { h.Dispose(); return null; }
            return h;
        }
        catch (Exception) { h.Dispose(); return null; }
    }

    /// <summary>Tạo file MỚI (thất bại nếu đã tồn tại, kể cả liên kết) không theo liên kết, rồi kiểm tra đường dẫn cuối đúng <paramref name="path"/>; sai thì xóa file vừa tạo và ném IOException.</summary>
    internal static FileStream CreateNew(string path)
    {
        var expected = SafetyGuard.Normalize(path);
        var h = CreateFileW(expected, GENERIC_WRITE | DELETE, 0, IntPtr.Zero, CREATE_NEW,
                            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new IOException("Không tạo được file: " + new Win32Exception(err).Message);
        }
        try
        {
            if (!string.Equals(FinalPath(h), expected, StringComparison.OrdinalIgnoreCase))
            {
                var del = 1;
                SetFileInformationByHandle(h, FileDispositionInfo, ref del, sizeof(int));
                throw new IOException("Đường dẫn file không như mong đợi");
            }
            return new FileStream(h, FileAccess.Write);
        }
        catch (Exception)
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>Mở file đã có để đọc (chặn ghi/xóa khi còn mở), không theo liên kết, kiểm tra đường dẫn cuối đúng <paramref name="path"/>.</summary>
    internal static FileStream OpenReadNoReparse(string path)
    {
        var expected = SafetyGuard.Normalize(path);
        var h = CreateFileW(expected, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING,
                            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new IOException("Không mở được file: " + new Win32Exception(err).Message);
        }
        try
        {
            if (!GetFileInformationByHandle(h, out var info)
                || ((FileAttributes)info.FileAttributes).HasFlag(FileAttributes.ReparsePoint)
                || !string.Equals(FinalPath(h), expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("File không an toàn");
            return new FileStream(h, FileAccess.Read);
        }
        catch (Exception)
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>Ghi <paramref name="content"/> (UTF-8 không BOM) vào file mới tạo an toàn; false nếu không ghi được (không bao giờ ném).</summary>
    internal static bool TryWriteNew(string path, string content)
    {
        try
        {
            using var fs = CreateNew(path);
            var bytes = new UTF8Encoding(false).GetBytes(content);
            fs.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Bật (best-effort) chính sách chỉ tin liên kết chuyển hướng do admin tạo; lỗi trên Windows cũ bị bỏ qua.</summary>
    internal static void EnforceRedirectionTrust()
    {
        try
        {
            var flags = 1u; // EnforceRedirectionTrust
            SetProcessMitigationPolicy(ProcessRedirectionTrustPolicy, ref flags, sizeof(uint));
        }
        catch (Exception) { /* Windows cũ không hỗ trợ */ }
    }

    /// <summary>Đường dẫn cuối của handle (dạng DOS), bỏ tiền tố \\?\ (và \\?\UNC\ → \\).</summary>
    internal static string FinalPath(SafeFileHandle h)
    {
        var sb = new StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n > sb.Capacity)
        {
            sb = new StringBuilder((int)n + 1);
            n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        }
        if (n == 0 || n > sb.Capacity) throw new IOException("Không đọc được đường dẫn cuối");
        var p = sb.ToString(0, (int)n);
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p.Substring(8);
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);
        var root = Path.GetPathRoot(p) ?? "";
        return p.Length > root.Length ? p.TrimEnd('\\') : p;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
                                                     uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, ref int lpFileInformation, int dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(int mitigationPolicy, ref uint lpBuffer, int dwLength);
}
