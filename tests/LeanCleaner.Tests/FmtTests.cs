using LeanCleaner;
using LeanCleaner.Core;
using Xunit;

namespace LeanCleaner.Tests;

public class FmtTests
{
    [Fact] public void Zero() => Assert.Equal("0 MB", Fmt.Bytes(0));
    [Fact] public void Megabytes() => Assert.Equal("512 MB", Fmt.Bytes(512L * 1024 * 1024));
    [Fact] public void Gigabytes() => Assert.Equal("1,8 GB", Fmt.Bytes((long)(1.8 * 1024 * 1024 * 1024)));
}

public class SelectionTests
{
    private static readonly JunkRule Rule = new("r", "g", new[] { @"C:\" }, RiskLevel.None, false, false);
    private static ScanItem Item(string p) => new(Rule, p, 1, false, false);

    [Fact]
    public void DropsItemsInsideOtherSelectedItems()
    {
        var r = Selection.RemoveNested(new[]
        {
            Item(@"C:\Users\a\Downloads\big.iso"), Item(@"C:\Users\a\Downloads"), Item(@"C:\Users\a\Downloads2\x")
        });
        Assert.Equal(new[] { @"C:\Users\a\Downloads", @"C:\Users\a\Downloads2\x" }, r.Select(i => i.Path).OrderBy(x => x));
    }

    [Fact]
    public void KeepsOneOfDuplicatesAndIsCaseInsensitive()
    {
        var r = Selection.RemoveNested(new[] { Item(@"C:\A\B"), Item(@"c:\a\b\"), Item(@"C:\A\B\c") });
        Assert.Single(r);
    }
}
