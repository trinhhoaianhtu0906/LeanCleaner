using System.Security.Principal;

namespace LeanCleaner.Core;

/// <summary>Helper chạy với quyền admin: đưa nội dung 3 thư mục rác Windows vào Thùng rác và/hoặc thu gọn .vhdx.</summary>
public static class AdminRunner
{
    private static readonly IReadOnlyList<SkipInfo> NoSkips = Array.Empty<SkipInfo>();
    private static readonly IReadOnlyList<CompactInfo> NoCompact = Array.Empty<CompactInfo>();

    /// <summary>Chạy helper với môi trường thật; trả về mã thoát (0 ok, 1 lỗi bất ngờ, 2 chưa admin, 3 yêu cầu sai).</summary>
    public static int Run(string requestPath)
    {
        string workDir;
        try { workDir = Path.GetDirectoryName(requestPath)!; }
        catch (Exception) { return 3; }
        return Run(requestPath, KnownDirs.Current(), new AppLog(AppLog.DefaultFilePath), IsElevated,
                   new DiskpartCompactor(workDir));
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Phần lõi có thể thay thế phụ thuộc để kiểm thử; không bao giờ ném ra ngoài.</summary>
    internal static int Run(string requestPath, KnownDirs d, AppLog log, Func<bool> isElevated,
                            IVhdxCompactor compactor, Func<string, long>? quotaFor = null)
    {
        string resultPath;
        try { resultPath = Path.Combine(Path.GetDirectoryName(requestPath)!, "result.json"); }
        catch (Exception) { return 3; }
        void Write(AdminResult r)
        {
            try { File.WriteAllText(resultPath, r.ToJson()); } catch (Exception) { /* vẫn trả mã thoát */ }
        }

        try
        {
            if (!isElevated())
            {
                Write(new AdminResult(0, 0, NoSkips, NoCompact, "Không có quyền admin"));
                return 2;
            }
            AdminRequest? req = null;
            try { req = AdminRequest.Parse(File.ReadAllText(requestPath)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* coi như không đọc được */ }
            if (req is null)
            {
                Write(new AdminResult(0, 0, NoSkips, NoCompact, "Yêu cầu không hợp lệ"));
                return 3;
            }

            var skipped = new List<SkipInfo>();
            long bytes = 0;
            int ok = 0;
            var items = new List<ScanItem>();
            foreach (var op in AdminTargets.DeleteOps.Where(req.Ops.Contains))
            {
                var root = AdminTargets.RootOf(op, d);
                if (Directory.Exists(root)) items.Add(new ScanItem(AdminTargets.RuleFor(op, d), root, 0, true, true));
            }
            if (items.Count > 0)
            {
                var guard = new AdminGuard(d);
                var deleter = quotaFor is null ? new Deleter(guard, log) : new Deleter(guard, log, quotaFor);
                var res = deleter.RecycleAsync(items, CancellationToken.None).GetAwaiter().GetResult();
                bytes = res.BytesRecycled;
                ok = res.Succeeded;
                skipped.AddRange(res.Skipped.Select(s => new SkipInfo(s.Path, s.Reason)));
            }

            var compacted = new List<CompactInfo>();
            if (req.Ops.Contains(AdminOp.CompactVhdx))
            {
                var allowed = AdminRequest.FilterVhdx(req.Vhdx, AdminTargets.FindVhdx(d), skipped);
                if (allowed.Count > 0)
                {
                    compactor.ShutdownWsl();
                    foreach (var v in allowed)
                    {
                        var c = compactor.Compact(v);
                        compacted.Add(c);
                        log.Write("COMPACT", v, c.Before - c.After, $"{c.Before} -> {c.After}" + (c.Error is null ? "" : " | " + c.Error));
                    }
                }
            }
            Write(new AdminResult(bytes, ok, skipped, compacted, null));
            return 0;
        }
        catch (Exception e)
        {
            Write(new AdminResult(0, 0, NoSkips, NoCompact, e.Message));
            return 1;
        }
    }
}
