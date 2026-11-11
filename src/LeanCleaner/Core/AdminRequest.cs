using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeanCleaner.Core;

/// <summary>Cấu hình JSON dùng chung cho request/result (camelCase, enum dạng chuỗi, không nhận số).</summary>
internal static class AdminJson
{
    /// <summary>Tùy chọn serialize/deserialize.</summary>
    internal static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        o.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return o;
    }
}

/// <summary>Yêu cầu app thường gửi cho helper admin: danh sách thao tác và các ổ ảo cần thu gọn.</summary>
/// <param name="Ops">Các thao tác admin được yêu cầu.</param>
/// <param name="Vhdx">Các đường dẫn .vhdx cần thu gọn (sẽ được lọc theo danh sách hợp lệ).</param>
public sealed record AdminRequest(IReadOnlyList<AdminOp> Ops, IReadOnlyList<string> Vhdx)
{
    /// <summary>Thông báo lý do loại đường dẫn vhdx không hợp lệ.</summary>
    private const string NotAllowed = "Không nằm trong danh sách ổ ảo hợp lệ";

    /// <summary>Các thư mục người dùng loại trừ (chỉ có thể thu hẹp phạm vi xóa); thiếu trong JSON thì rỗng.</summary>
    public IReadOnlyList<string> Excluded { get; init; } = Array.Empty<string>();

    /// <summary>Chuyển yêu cầu thành chuỗi JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, AdminJson.Options);

    /// <summary>Đọc yêu cầu từ JSON; trả về null (không bao giờ ném) nếu sai định dạng, thiếu/rỗng ops, op lạ, vhdx/excluded không phải mảng chuỗi hoặc có phần tử null; mục excluded sai dạng bị bỏ qua.</summary>
    public static AdminRequest? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json, AdminJson.Options);
            if (dto?.Ops is null || dto.Ops.Count == 0) return null;
            var vhdx = dto.Vhdx ?? new List<string?>();
            if (vhdx.Any(v => v is null)) return null;
            var excluded = dto.Excluded ?? new List<string?>();
            if (excluded.Any(v => v is null)) return null;
            var names = Enum.GetNames<AdminOp>();
            var ops = new List<AdminOp>();
            foreach (var name in dto.Ops)
            {
                // Chỉ nhận đúng tên (phân biệt hoa/thường); từ chối số, danh sách phẩy, khoảng trắng thừa.
                if (name is null || Array.IndexOf(names, name) < 0) return null;
                ops.Add(Enum.Parse<AdminOp>(name));
            }
            return new AdminRequest(ops, vhdx.Select(v => v!).ToArray())
                { Excluded = excluded.Where(SafetyGuard.IsWellFormed).Select(v => v!).ToArray() };
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Giữ lại các đường dẫn được yêu cầu mà khi chuẩn hóa trùng một đường dẫn hợp lệ (không phân biệt hoa/thường, bỏ trùng, giữ thứ tự); phần còn lại ghi vào <paramref name="skipped"/>.</summary>
    public static IReadOnlyList<string> FilterVhdx(IEnumerable<string> requested, IEnumerable<string> allowed, List<SkipInfo> skipped)
    {
        var allow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in allowed)
        {
            if (!SafetyGuard.IsWellFormed(a)) continue;
            try { var n = SafetyGuard.Normalize(a); allow.TryAdd(n, n); } catch (Exception) { /* bỏ qua */ }
        }
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in requested)
        {
            string? n = null;
            if (r is not null && SafetyGuard.IsWellFormed(r))
            {
                try { n = SafetyGuard.Normalize(r); } catch (Exception) { n = null; }
            }
            if (n is not null && allow.TryGetValue(n, out var canonical))
            {
                if (seen.Add(n)) kept.Add(canonical);
            }
            else skipped.Add(new SkipInfo(r ?? "", NotAllowed));
        }
        return kept;
    }

    /// <summary>Dạng trung gian cho phép phát hiện trường thiếu/null khi đọc JSON.</summary>
    private sealed class Dto
    {
        public List<string?>? Ops { get; set; }
        public List<string?>? Vhdx { get; set; }
        public List<string?>? Excluded { get; set; }
    }
}

/// <summary>Một mục bị bỏ qua cùng lý do.</summary>
/// <param name="Path">Đường dẫn bị bỏ qua.</param>
/// <param name="Reason">Lý do bỏ qua.</param>
public sealed record SkipInfo(string Path, string Reason);

/// <summary>Kết quả thu gọn một ổ ảo.</summary>
/// <param name="Path">Đường dẫn .vhdx.</param>
/// <param name="Before">Kích thước trước (byte).</param>
/// <param name="After">Kích thước sau (byte).</param>
/// <param name="Error">Thông báo lỗi, null nếu thành công.</param>
public sealed record CompactInfo(string Path, long Before, long After, string? Error);

/// <summary>Kết quả helper admin ghi ra result.json.</summary>
/// <param name="RecycledBytes">Tổng byte đã chuyển vào Thùng rác.</param>
/// <param name="Succeeded">Số mục đã xử lý thành công.</param>
/// <param name="Skipped">Các mục bị bỏ qua.</param>
/// <param name="Compacted">Kết quả thu gọn từng ổ ảo.</param>
/// <param name="Error">Lỗi chung (ví dụ chưa có quyền admin), null nếu không có.</param>
public sealed record AdminResult(long RecycledBytes, int Succeeded, IReadOnlyList<SkipInfo> Skipped, IReadOnlyList<CompactInfo> Compacted, string? Error)
{
    /// <summary>Các dòng nhật ký helper ghi trong bộ nhớ; app thường làm sạch rồi ghi vào log.txt. Thiếu trong JSON thì rỗng.</summary>
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

    /// <summary>Chuyển kết quả thành chuỗi JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, AdminJson.Options);

    /// <summary>Đọc kết quả từ JSON; trả về null (không ném) nếu không hợp lệ hoặc log có phần tử null.</summary>
    public static AdminResult? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var r = JsonSerializer.Deserialize<AdminResult>(json, AdminJson.Options);
            if (r is null) return null;
            var log = r.Log ?? Array.Empty<string>();
            if (log.Any(l => l is null)) return null;
            return r with { Skipped = r.Skipped ?? Array.Empty<SkipInfo>(), Compacted = r.Compacted ?? Array.Empty<CompactInfo>(), Log = log };
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
