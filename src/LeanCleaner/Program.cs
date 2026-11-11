using LeanCleaner.Core;

namespace LeanCleaner;

/// <summary>Điểm vào của ứng dụng.</summary>
internal static class Program
{
    /// <summary>Chạy ứng dụng (một instance) hoặc chế độ helper admin khi có "--admin &lt;request.json&gt;".</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--admin") return AdminRunner.Run(args[1]);
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, @"Local\LeanCleaner.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("LeanCleaner đang chạy.", "LeanCleaner");
            return 0;
        }
        Application.Run(new MainForm());
        return 0;
    }
}
