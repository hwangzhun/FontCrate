using FontCrate.App;
using Xunit;

namespace FontCrate.Tests;

public class LibraryPresentationTests
{
    [Theory]
    [InlineData(true, 0, false, false, (int)LibraryEmptyState.Loading)]
    [InlineData(true, 20, true, true, (int)LibraryEmptyState.Loading)]
    [InlineData(false, 20, true, true, (int)LibraryEmptyState.Populated)]
    [InlineData(false, 0, false, false, (int)LibraryEmptyState.FirstUse)]
    [InlineData(false, 0, false, true, (int)LibraryEmptyState.NoFonts)]
    [InlineData(false, 0, true, false, (int)LibraryEmptyState.NoMatches)]
    [InlineData(false, 0, true, true, (int)LibraryEmptyState.NoMatches)]
    public void EmptyStateDistinguishesLoadingAndActionableResults(bool loading, int total, bool filtered, bool sources, int expected)
        => Assert.Equal((LibraryEmptyState)expected, LibraryPresentation.State(loading, total, filtered, sources));

    [Theory]
    [InlineData("8", 8)]
    [InlineData("192", 192)]
    [InlineData("64", 64)]
    [InlineData("", 48)]
    [InlineData("abc", 48)]
    [InlineData("NaN", 48)]
    [InlineData("Infinity", 48)]
    [InlineData("193", 48)]
    [InlineData("7", 48)]
    public void FontSizeRejectsInvalidInputAndKeepsLastValue(string input, double expected)
        => Assert.Equal(expected, LibraryPresentation.ParseSize(input, 48));

    [Fact]
    public void UnknownCountsAreNotPresentedAsZero()
    {
        var summary = new TaskResultSummary("扫描", "Warning", Note: "来源不可用");
        Assert.Equal("", summary.CountsText);
        Assert.Equal("⚠ 需要注意", summary.StateText);
    }

    [Fact]
    public void CancelledBatchSeparatesFailuresFromUnfinishedFiles()
    {
        var summary = new TaskResultSummary("安装", "Canceled", 3, 1, 10);
        Assert.Equal("3 成功 · 1 失败 · 6 未完成", summary.CountsText);
        Assert.Equal("○ 已取消", summary.StateText);
    }

    [Fact]
    public void CancellationWithUnknownTotalDoesNotInventRemainingCount()
    {
        var summary = new TaskResultSummary("导出", "Canceled", 3, 1);
        Assert.Contains("数量未知", summary.CountsText);
        Assert.DoesNotContain("0 未完成", summary.CountsText);
    }

    [Fact]
    public void CompletedBatchDoesNotDisplayUnfinishedWork()
    {
        var summary = new TaskResultSummary("安装", "Succeeded", 4, 0, 4);
        Assert.Equal("4 成功 · 0 失败", summary.CountsText);
    }
}
