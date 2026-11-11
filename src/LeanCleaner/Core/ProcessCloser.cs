using System.Diagnostics;
using System.ComponentModel;

namespace LeanCleaner.Core;

/// <summary>Đóng ứng dụng nhẹ nhàng (CloseMainWindow); chỉ buộc thoát khi người dùng đã xác nhận.</summary>
public sealed class ProcessCloser
{
    private readonly AppLog _log;

    /// <summary>Tạo bộ đóng tiến trình, ghi nhật ký vào log.</summary>
    public ProcessCloser(AppLog log) => _log = log;

    /// <summary>Gửi yêu cầu đóng cửa sổ, chỉ chờ (chung một timeout) các tiến trình đã nhận yêu cầu; nhóm không cửa sổ hoặc bị từ chối truy cập được trả về là còn sống (Pids chỉ gồm pid còn sống).</summary>
    public async Task<IReadOnlyList<AppGroup>> CloseGracefullyAsync(IEnumerable<AppGroup> groups, TimeSpan timeout)
    {
        var list = groups.ToList();
        var opened = new List<(AppGroup Group, Process Proc, bool Sent)>();
        var denied = new List<(AppGroup Group, int Pid)>();
        try
        {
            foreach (var g in list)
            {
                foreach (var pid in g.Pids)
                {
                    var status = TryOpen(pid, g.Name, out var p);
                    if (status == OpenStatus.Gone) continue;
                    if (status == OpenStatus.Denied)
                    {
                        denied.Add((g, pid));
                        _log.Write("CLOSE", $"{g.Name}#{pid}", 0, "access-denied");
                        continue;
                    }

                    var sent = false;
                    try
                    {
                        if (p!.MainWindowHandle != IntPtr.Zero)
                        {
                            sent = p.CloseMainWindow();
                            _log.Write("CLOSE", $"{g.Name}#{pid}", 0, sent ? "requested" : "not-accepted");
                        }
                        else
                        {
                            _log.Write("CLOSE", $"{g.Name}#{pid}", 0, "no-window");
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                    opened.Add((g, p!, sent));
                }
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await Task.WhenAll(opened.Where(o => o.Sent).Select(o => WaitSafe(o.Proc, cts.Token)));
            }
            catch (OperationCanceledException) { }

            var alive = new List<AppGroup>();
            foreach (var g in list)
            {
                var live = opened.Where(o => ReferenceEquals(o.Group, g) && IsAlive(o.Proc)).Select(o => o.Proc.Id)
                    .Concat(denied.Where(d => ReferenceEquals(d.Group, g)).Select(d => d.Pid)).ToList();
                if (live.Count > 0) alive.Add(g with { Pids = live });
            }
            return alive;
        }
        finally
        {
            foreach (var o in opened) o.Proc.Dispose();
        }
    }

    /// <summary>Buộc kết thúc từng pid còn sống của nhóm (không kill cây tiến trình, vì nhóm đã chứa mọi pid cùng tên); pid bị từ chối truy cập không được tính. Trả về số tiến trình đã kill.</summary>
    public int ForceKill(AppGroup group)
    {
        var killed = 0;
        foreach (var pid in group.Pids)
        {
            var status = TryOpen(pid, group.Name, out var p);
            if (status == OpenStatus.Gone) continue;
            if (status == OpenStatus.Denied)
            {
                _log.Write("KILL", $"{group.Name}#{pid}", 0, "access-denied");
                continue;
            }
            using (p)
            {
                try
                {
                    p!.Kill(entireProcessTree: false);
                    killed++;
                    _log.Write("KILL", $"{group.Name}#{pid}", 0, "ok");
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    _log.Write("KILL", $"{group.Name}#{pid}", 0, ex is Win32Exception ? "access-denied" : "gone");
                }
            }
        }
        return killed;
    }

    private enum OpenStatus { Ok, Gone, Denied }

    /// <summary>Mở tiến trình nếu còn sống, cùng phiên và tên khớp nhóm (tránh nhầm pid bị tái sử dụng). Gone = đã thoát/không khớp; Denied = không đủ quyền kiểm tra.</summary>
    private static OpenStatus TryOpen(int pid, string expectedName, out Process? proc)
    {
        proc = null;
        Process? p = null;
        try
        {
            p = Process.GetProcessById(pid);
            using var self = Process.GetCurrentProcess();
            if (p.HasExited || p.SessionId != self.SessionId
                || !string.Equals(p.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                p.Dispose();
                return OpenStatus.Gone;
            }
            proc = p;
            return OpenStatus.Ok;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            p?.Dispose();
            return OpenStatus.Gone;
        }
        catch (Win32Exception)
        {
            p?.Dispose();
            return OpenStatus.Denied;
        }
    }

    private static async Task WaitSafe(Process p, CancellationToken ct)
    {
        try { await p.WaitForExitAsync(ct); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    private static bool IsAlive(Process p)
    {
        try { return !p.HasExited; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
    }
}
