namespace LeanCleaner;

/// <summary>Điểm vào của ứng dụng.</summary>
internal static class Program
{
    /// <summary>Chạy ứng dụng, chỉ cho phép một instance.</summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, @"Local\LeanCleaner.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("LeanCleaner đang chạy.", "LeanCleaner");
            return;
        }
        Application.Run(new MainForm());
    }
}
