using System.Diagnostics;
using LeanCleaner.Core;

namespace LeanCleaner.Tests;

/// <summary>Kiểm thử ProcessScanner và ProcessCloser.</summary>
public sealed class ProcessTests : IDisposable
{
    private const long MB = 1024L * 1024;
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), "lc-proc-" + Guid.NewGuid().ToString("N") + ".log");

    /// <summary>Xóa file log tạm.</summary>
    public void Dispose()
    {
        try { File.Delete(_logPath); } catch (IOException) { }
    }

    [Fact]
    public void Group_MergesByNameCaseInsensitive_AndSuggestsHeavy()
    {
        var groups = ProcessScanner.Group(new[]
        {
            new ProcSnapshot("chrome", 1, 200 * MB, true),
            new ProcSnapshot("Chrome", 2, 400 * MB, false),
            new ProcSnapshot("zalo", 3, 100 * MB, false)
        });

        Assert.Equal("chrome", groups[0].Name);
        Assert.Equal(600 * MB, groups[0].TotalBytes);
        Assert.Equal(2, groups[0].Pids.Count);
        Assert.True(groups[0].HasWindow);
        Assert.True(groups[0].Suggested);
        Assert.Equal("Nặng", groups[0].Reason);
        var zalo = groups.Single(g => g.Name == "zalo");
        Assert.False(zalo.Suggested);
        Assert.Equal("", zalo.Reason);
    }

    [Fact]
    public void Group_BackgroundSuggestedOnlyWithoutWindow()
    {
        var groups = ProcessScanner.Group(new[]
        {
            new ProcSnapshot("agent", 1, 250 * MB, false),
            new ProcSnapshot("editor", 2, 250 * MB, true)
        });

        var agent = groups.Single(g => g.Name == "agent");
        Assert.True(agent.Suggested);
        Assert.Equal("Chạy nền", agent.Reason);
        Assert.False(groups.Single(g => g.Name == "editor").Suggested);
    }

    [Fact]
    public void Snapshot_ExcludesProtectedAndSelf()
    {
        var self = Process.GetCurrentProcess();
        var snap = ProcessScanner.Snapshot(new[] { self.ProcessName, "explorer" });

        Assert.DoesNotContain(snap, p => p.Pid == self.Id);
        Assert.DoesNotContain(snap, p => p.Name.Equals(self.ProcessName, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snap, p => p.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetMemoryStatus_ReturnsPlausibleValues()
    {
        var m = ProcessScanner.GetMemoryStatus();
        Assert.True(m.TotalBytes > m.AvailableBytes);
        Assert.True(m.AvailableBytes > 0);
    }

    [Fact]
    public async Task CloseGracefully_ClosesNotepad()
    {
        // Không đụng vào Notepad của người dùng: bỏ qua nếu đã có Notepad đang chạy.
        var existing = Process.GetProcessesByName("Notepad");
        var before = existing.Select(p => p.Id).ToHashSet();
        foreach (var p in existing) p.Dispose();
        if (before.Count > 0)
        {
            Console.WriteLine("SKIP: Notepad đang chạy sẵn, không chạy test để tránh đóng nhầm.");
            return;
        }

        Process? started = null;
        Process? target = null;
        try
        {
            started = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            target = await FindNotepadWithWindowAsync(started, before, TimeSpan.FromSeconds(10));
            if (target == null)
            {
                Console.WriteLine("SKIP: không tìm thấy cửa sổ Notepad trong 10 giây.");
                return;
            }

            var group = new AppGroup(target.ProcessName, new[] { target.Id }, 0, true, false, "");
            var closer = new ProcessCloser(new AppLog(_logPath));
            var left = await closer.CloseGracefullyAsync(new[] { group }, TimeSpan.FromSeconds(5));

            Assert.Empty(left);
            Assert.True(target.WaitForExit(2000));
        }
        finally
        {
            KillQuietly(target);
            KillQuietly(started);
            foreach (var p in Process.GetProcessesByName("Notepad"))
            {
                // chỉ dọn tiến trình mới sinh sau khi test bắt đầu
                try { if (!before.Contains(p.Id)) p.Kill(false); } catch (Exception) { }
                p.Dispose();
            }
            if (!ReferenceEquals(target, started)) target?.Dispose();
            started?.Dispose();
        }
    }

    [Fact]
    public async Task CloseGracefully_WindowlessGroupReturnsAliveWithoutWaiting()
    {
        var psi = new ProcessStartInfo("ping", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var proc = Process.Start(psi)!;
        try
        {
            var group = new AppGroup("ping", new[] { proc.Id }, 0, false, false, "");
            var sw = Stopwatch.StartNew();
            var left = await new ProcessCloser(new AppLog(_logPath)).CloseGracefullyAsync(new[] { group }, TimeSpan.FromSeconds(5));
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"đã chờ {sw.Elapsed}");
            Assert.Single(left);
            Assert.Equal(new[] { proc.Id }, left[0].Pids);
        }
        finally { KillQuietly(proc); }
    }

    [Fact]
    public void ForceKill_KillsWindowlessProcess()
    {
        var psi = new ProcessStartInfo("ping", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var proc = Process.Start(psi)!;
        try
        {
            var group = new AppGroup("PING", new[] { proc.Id }, 0, false, false, "");
            var killed = new ProcessCloser(new AppLog(_logPath)).ForceKill(group);

            Assert.Equal(1, killed);
            Assert.True(proc.WaitForExit(5000));
        }
        finally { KillQuietly(proc); }
    }

    [Fact]
    public void ForceKill_IgnoresPidWhoseNameDiffers()
    {
        var psi = new ProcessStartInfo("ping", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var proc = Process.Start(psi)!;
        try
        {
            var group = new AppGroup("notping", new[] { proc.Id }, 0, false, false, "");
            Assert.Equal(0, new ProcessCloser(new AppLog(_logPath)).ForceKill(group));
            Assert.False(proc.HasExited);
        }
        finally { KillQuietly(proc); }
    }

    private static async Task<Process?> FindNotepadWithWindowAsync(Process? started, HashSet<int> before, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            if (started != null)
            try
            {
                started.Refresh();
                if (!started.HasExited && started.MainWindowHandle != IntPtr.Zero) return started;
            }
            catch (InvalidOperationException) { }

            // Win11: Notepad đóng gói có thể chạy lại thành tiến trình khác
            Process? best = null;
            foreach (var p in Process.GetProcessesByName("Notepad"))
            {
                try
                {
                    p.Refresh();
                    if (!before.Contains(p.Id) && p.MainWindowHandle != IntPtr.Zero
                        && (best == null || p.StartTime > best.StartTime))
                    {
                        best?.Dispose();
                        best = p;
                        continue;
                    }
                }
                catch (Exception) { }
                p.Dispose();
            }
            if (best != null) return best;
            await Task.Delay(200);
        }
        return null;
    }

    private static void KillQuietly(Process? p)
    {
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(true); } catch (Exception) { }
    }
}
