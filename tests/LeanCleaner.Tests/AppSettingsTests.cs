using LeanCleaner.Core;

namespace LeanCleaner.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LeanCleanerTests_" + Guid.NewGuid().ToString("N"));
    private string NewFile() => Path.Combine(_dir, "sub", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void CreateDefault_HasExpectedValues()
    {
        var s = AppSettings.CreateDefault();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(11, s.ExcludedPaths.Count);
        Assert.All(s.ExcludedPaths, p => Assert.StartsWith(profile, p));
        Assert.Contains(Path.Combine(profile, "Personal Project"), s.ExcludedPaths);
        Assert.Contains("explorer", s.ProtectedProcesses);
        Assert.Contains("LeanCleaner", s.ProtectedProcesses);
        Assert.Equal(500, s.LargeFileMinMB);
        Assert.Equal(90, s.LargeFileUnusedDays);
        Assert.Equal(30, s.InstallerAgeDays);
        Assert.Equal(1, s.TempAgeDays);
        Assert.Equal(200, s.MaxLargeFiles);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var f = NewFile();
        var s = AppSettings.CreateDefault();
        s.LargeFileMinMB = 123;
        s.TempAgeDays = 7;
        s.ExcludedPaths = new() { @"C:\x" };
        s.ProtectedProcesses = new() { "foo" };
        s.Save(f);
        var l = AppSettings.Load(f);
        Assert.Equal(123, l.LargeFileMinMB);
        Assert.Equal(7, l.TempAgeDays);
        Assert.Equal(new[] { @"C:\x" }, l.ExcludedPaths);
        Assert.Equal(new[] { "foo" }, l.ProtectedProcesses);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsAndCreatesFile()
    {
        var f = NewFile();
        var l = AppSettings.Load(f);
        Assert.Equal(500, l.LargeFileMinMB);
        Assert.Equal(11, l.ExcludedPaths.Count);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaultsAndRewrites()
    {
        var f = NewFile();
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, "{not json");
        var l = AppSettings.Load(f);
        Assert.Equal(500, l.LargeFileMinMB);
        var again = AppSettings.Load(f);
        Assert.Equal(11, again.ExcludedPaths.Count);
        var text = File.ReadAllText(f);
        Assert.NotEqual("{not json", text);
        using var doc = System.Text.Json.JsonDocument.Parse(text);
    }

    [Fact]
    public void Load_EmptyObject_UsesDefaultLists()
    {
        var f = NewFile();
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, "{}");
        var l = AppSettings.Load(f);
        Assert.Equal(11, l.ExcludedPaths.Count);
        Assert.Equal(AppSettings.CreateDefault().ProtectedProcesses, l.ProtectedProcesses);
    }

    [Fact]
    public void Load_ListWithNullElement_DropsNull()
    {
        var f = NewFile();
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, "{\"ExcludedPaths\": [\"C:\\\\x\", null]}");
        var l = AppSettings.Load(f);
        Assert.Equal(new[] { @"C:\x" }, l.ExcludedPaths);
    }

    [Fact]
    public void Load_NullListAndZeroNumber_FallBackToDefaults()
    {
        var f = NewFile();
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, "{\"ExcludedPaths\": null, \"LargeFileMinMB\": 0}");
        var l = AppSettings.Load(f);
        Assert.Equal(11, l.ExcludedPaths.Count);
        Assert.Equal(500, l.LargeFileMinMB);
    }
}
