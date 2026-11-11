using System.Globalization;

namespace LeanCleaner;

/// <summary>Định dạng hiển thị.</summary>
public static class Fmt
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    /// <summary>Dung lượng dạng "512 MB" hoặc "1,8 GB" (từ 1 GB trở lên dùng GB, 1 chữ số thập phân).</summary>
    public static string Bytes(long b)
    {
        const double MB = 1024.0 * 1024, GB = MB * 1024;
        return b >= GB ? (b / GB).ToString("0.0", Vi) + " GB" : Math.Round(b / MB).ToString("0", Vi) + " MB";
    }
}
