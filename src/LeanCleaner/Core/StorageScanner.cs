using System.Diagnostics;

namespace LeanCleaner.Core;

/// <summary>Kích thước và thời điểm ghi mới nhất của một cây thư mục (tính trong một lượt duyệt).</summary>
internal readonly record struct DirStats(long Bytes, DateTime NewestWriteUtc);

/// <summary>Quét đĩa theo danh mục JunkRules, duyệt lặp (không đệ quy), bộ nhớ có giới hạn.</summary>
public sealed class StorageScanner
{
    private static readonly string[] LargeSkipExt = { ".vhdx", ".vhd", ".vmdk", ".qcow2" };
    private static readonly EnumerationOptions EnumOpts = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
    };

    private readonly AppSettings _settings;
    private readonly SafetyGuard _guard;
    private readonly KnownDirs _dirs;

    /// <summary>Tạo scanner với cấu hình, guard và các thư mục đã biết.</summary>
    public StorageScanner(AppSettings settings, SafetyGuard guard, KnownDirs dirs)
    {
        _settings = settings;
        _guard = guard;
        _dirs = dirs;
    }

    /// <summary>Quét nền; hủy qua ct trả về kết quả một phần với Cancelled=true (không ném lỗi).</summary>
    public Task<ScanResult> ScanAsync(IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => Scan(progress, ct), CancellationToken.None);

    private ScanResult Scan(IProgress<string>? progress, CancellationToken ct)
    {
        var items = new List<ScanItem>();
        var sw = Stopwatch.StartNew();
        long last = -1000;
        void OnDir(string dir)
        {
            if (progress == null || sw.ElapsedMilliseconds - last < 100) return;
            last = sw.ElapsedMilliseconds;
            progress.Report(dir);
        }

        foreach (var t in JunkRules.Build(_settings, _dirs, ct))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                switch (t.Kind)
                {
                    case TargetKind.WholeDir: ScanWhole(t, items, ct, OnDir); break;
                    case TargetKind.TopLevelChildren: ScanChildren(t, items, ct, OnDir); break;
                    case TargetKind.LargeFiles: ScanLarge(t, items, ct, OnDir); break;
                    case TargetKind.ReportOnlySize: ScanReport(t, items, ct, OnDir); break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* bỏ qua mục tiêu lỗi */ }
        }

        var order = JunkRules.GroupOrder;
        var sorted = items
            .OrderBy(i => Array.IndexOf(order, i.Rule.Group))
            .ThenByDescending(i => i.Bytes)
            .ToList();
        return new ScanResult(sorted, ct.IsCancellationRequested);
    }

    private static bool IsReparse(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private void ScanWhole(RuleTarget t, List<ScanItem> items, CancellationToken ct, Action<string> onDir)
    {
        if (_guard.IsExcluded(t.Dir) || IsReparse(t.Dir)) return;
        var st = DirSize(t.Dir, _guard, ct, onDir);
        if (ct.IsCancellationRequested) return;
        /// Gốc cần admin: luôn liệt kê dù đo được 0 byte (user thường bị Access denied), helper nâng quyền vẫn dọn được.
        if (st.Bytes > 0 || t.Rule.NeedsAdmin) items.Add(new ScanItem(t.Rule, t.Dir, st.Bytes, true, true));
    }

    private void ScanChildren(RuleTarget t, List<ScanItem> items, CancellationToken ct, Action<string> onDir)
    {
        var cutoff = DateTime.UtcNow.AddDays(-t.MinAgeDays);
        var hasExt = t.Extensions is { Count: > 0 };
        foreach (var child in new DirectoryInfo(t.Dir).EnumerateFileSystemInfos("*", EnumOpts))
        {
            if (ct.IsCancellationRequested) return;
            long bytes;
            DateTime newest;
            bool isDir = child is DirectoryInfo;
            if (isDir)
            {
                if (hasExt || _guard.IsExcluded(child.FullName)) continue;
                var st = DirSize(child.FullName, _guard, ct, onDir);
                if (ct.IsCancellationRequested) return;
                bytes = st.Bytes;
                newest = st.NewestWriteUtc;
            }
            else
            {
                var fi = (FileInfo)child;
                if (hasExt && !t.Extensions!.Contains(fi.Extension, StringComparer.OrdinalIgnoreCase)) continue;
                bytes = fi.Length;
                newest = fi.LastWriteTimeUtc;
            }
            if (bytes <= 0 || newest > cutoff) continue;
            if (!_guard.IsDeletable(child.FullName, t.Rule, out _)) continue;
            items.Add(new ScanItem(t.Rule, child.FullName, bytes, isDir, false));
        }
    }

    private void ScanLarge(RuleTarget t, List<ScanItem> items, CancellationToken ct, Action<string> onDir)
    {
        long minBytes = (long)_settings.LargeFileMinMB * 1024 * 1024;
        var unusedCutoff = DateTime.UtcNow.AddDays(-_settings.LargeFileUnusedDays);
        int cap = Math.Max(1, _settings.MaxLargeFiles);
        var skipDirs = new[]
        {
            Path.Combine(_dirs.UserProfile, "AppData", "Local", "Docker"),
            Path.Combine(_dirs.UserProfile, "AppData", "Roaming", "Claude", "vm_bundles")
        }.Select(SafetyGuard.Normalize).ToList();

        var heap = new PriorityQueue<FileInfo, long>();
        Walk(t.Dir, _guard, ct, onDir,
            p => skipDirs.Contains(SafetyGuard.Normalize(p), StringComparer.OrdinalIgnoreCase),
            f =>
            {
                long len = f.Length;
                if (len < minBytes) return;
                if (LargeSkipExt.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)) return;
                var used = f.LastWriteTimeUtc > f.LastAccessTimeUtc ? f.LastWriteTimeUtc : f.LastAccessTimeUtc;
                if (used > unusedCutoff) return;
                if (heap.Count == cap)
                {
                    heap.TryPeek(out _, out var min);
                    if (len <= min) return;
                    if (!_guard.IsDeletable(f.FullName, t.Rule, out _)) return;
                    heap.DequeueEnqueue(f, len);
                }
                else if (_guard.IsDeletable(f.FullName, t.Rule, out _))
                    heap.Enqueue(f, len);
            });
        if (ct.IsCancellationRequested) return;
        while (heap.TryDequeue(out var f, out var len))
            items.Add(new ScanItem(t.Rule, f.FullName, len, false, false));
    }

    private void ScanReport(RuleTarget t, List<ScanItem> items, CancellationToken ct, Action<string> onDir)
    {
        if (File.Exists(t.Dir))
        {
            var fi = new FileInfo(t.Dir);
            if (fi.Length > 0) items.Add(new ScanItem(t.Rule, t.Dir, fi.Length, false, false));
            return;
        }
        if (_guard.IsExcluded(t.Dir) || IsReparse(t.Dir)) return;
        var st = DirSize(t.Dir, _guard, ct, onDir);
        if (ct.IsCancellationRequested) return;
        if (st.Bytes > 0) items.Add(new ScanItem(t.Rule, t.Dir, st.Bytes, true, false));
    }

    /// <summary>Tổng kích thước và thời điểm ghi mới nhất của cây thư mục, duyệt lặp; hủy thì trả kết quả một phần.</summary>
    internal static DirStats DirSize(string dir, SafetyGuard guard, CancellationToken ct, Action<string>? onDir)
    {
        long bytes = 0;
        var newest = DateTime.MinValue; // chỉ tính file: thư mục rỗng có Bytes=0 nên không được phát ra
        Walk(dir, guard, ct, onDir, null, f =>
        {
            bytes += f.Length;
            if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
        });
        return new DirStats(bytes, newest);
    }

    /// <summary>Duyệt lặp bằng Stack: gọi onFile cho từng file; bỏ qua reparse point, thư mục loại trừ và skipDir.</summary>
    private static void Walk(string root, SafetyGuard guard, CancellationToken ct, Action<string>? onDir,
                             Func<string, bool>? skipDir, Action<FileInfo> onFile)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) return;
            var d = stack.Pop();
            onDir?.Invoke(d);
            try
            {
                foreach (var e in new DirectoryInfo(d).EnumerateFileSystemInfos("*", EnumOpts))
                {
                    if (e is DirectoryInfo sub)
                    {
                        if (guard.IsExcluded(sub.FullName) || (skipDir?.Invoke(sub.FullName) ?? false)) continue;
                        stack.Push(sub.FullName);
                    }
                    else onFile((FileInfo)e);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* bỏ qua thư mục không đọc được */ }
        }
    }
}
