using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public class ProgressTests
{
    private sealed class Recorder<T>(Action<T>? onReport = null) : IProgress<T>
    {
        public List<T> Reports { get; } = [];
        public void Report(T value) { Reports.Add(value); onReport?.Invoke(value); }
    }

    [Fact] public async Task EmptyScanHasKnownZeroTotalAndCompletes()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        var source = await s.Repository.AddSourceAsync(s.Fonts);
        var progress = new Recorder<ScanProgress>();
        var result = await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, progress, default);
        Assert.True(result.Complete);
        Assert.False(progress.Reports[0].EnumerationComplete);
        var last = progress.Reports[^1];
        Assert.True(last.EnumerationComplete); Assert.Equal("完成", last.Phase);
        Assert.Equal(0, last.Discovered); Assert.Equal(0, last.Processed);
    }

    [Fact] public async Task ScanCountsPersistedGoodBadAndUnchangedFiles()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        File.WriteAllBytes(Path.Combine(s.Fonts, "one.ttf"), FontData.Font("One"));
        File.WriteAllBytes(Path.Combine(s.Fonts, "two.ttf"), FontData.Font("Two"));
        File.WriteAllBytes(Path.Combine(s.Fonts, "bad.ttf"), [0, 1]);
        var source = await s.Repository.AddSourceAsync(s.Fonts);
        var scanner = new Scanner(s.Repository, new SfntParser());
        for (int run = 0; run < 2; run++)
        {
            var progress = new Recorder<ScanProgress>();
            await scanner.ScanAsync(source, false, progress, default);
            var last = progress.Reports[^1];
            Assert.Equal(3, last.Discovered); Assert.Equal(3, last.Processed); Assert.Equal(1, last.Errors);
            Assert.True(last.EnumerationComplete);
            Assert.Equal(run == 0 ? 2 : 0, last.Parsed); Assert.Equal(run == 0 ? 0 : 2, last.Unchanged);
            Assert.All(progress.Reports, p => Assert.InRange(p.Processed, 0, p.Discovered));
            Assert.Equal(progress.Reports.Select(p => p.Processed).Order(), progress.Reports.Select(p => p.Processed));
            Assert.Equal(2, (await s.Repository.QueryAsync(new())).Rows.Count);
        }
    }

    [Fact] public async Task MultipleSourcesHaveIndependentTotals()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        var other = Path.Combine(s.Root, "other"); Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(s.Fonts, "one.ttf"), FontData.Font("One"));
        File.WriteAllBytes(Path.Combine(other, "two.ttf"), FontData.Font("Two"));
        var scanner = new Scanner(s.Repository, new SfntParser());
        foreach (var dir in new[] { s.Fonts, other })
        {
            var progress = new Recorder<ScanProgress>();
            await scanner.ScanAsync(await s.Repository.AddSourceAsync(dir), false, progress, default);
            Assert.Equal(0, progress.Reports[0].Processed);
            Assert.Equal(1, progress.Reports[^1].Processed); Assert.Equal(1, progress.Reports[^1].Discovered);
        }
        Assert.Equal(2, (await s.Repository.QueryAsync(new())).Total);
    }

    [Fact] public async Task OfflineSourceReportsFailureWithoutPretendingAFileWasProcessed()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var source = (await s.Repository.SourcesAsync()).Single(); Directory.Move(s.Fonts, s.Fonts + "-offline");
        var progress = new Recorder<ScanProgress>();
        var result = await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, progress, default);
        Assert.False(result.Complete);
        Assert.Equal("来源不可用", progress.Reports[^1].Phase);
        Assert.Equal(1, progress.Reports[^1].Errors); Assert.Equal(0, progress.Reports[^1].Processed);
        Assert.True(progress.Reports[^1].EnumerationComplete);
        Assert.Equal(face.Id, (await s.Repository.QueryAsync(new())).Rows.Single().Id);
    }

    [Fact] public async Task CancelScanReportsActualCountAndPreservesPreviouslyIndexedLocations()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        File.Delete(face.Path!);
        for (int i = 0; i < 10; i++) File.WriteAllBytes(Path.Combine(s.Fonts, $"slow{i}.ttf"), FontData.Font($"Slow{i}"));
        var source = (await s.Repository.SourcesAsync()).Single();
        using var cancellation = new CancellationTokenSource();
        var progress = new Recorder<ScanProgress>(p => { if (p.Phase == "目录枚举完成") cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Scanner(s.Repository, new CountingParser { Delay = 500 })
            .ScanAsync(source, false, progress, cancellation.Token));
        var last = progress.Reports[^1];
        Assert.Equal("已取消", last.Phase); Assert.True(last.Processed < last.Discovered);
        Assert.NotNull((await s.Repository.QueryAsync(new())).Rows.Single(f => f.Id == face.Id).Path);
    }

    [Fact] public async Task CollectionProgressCountsTheFileOnce()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        File.WriteAllBytes(Path.Combine(s.Fonts, "collection.ttc"), FontData.Collection("First", "Second"));
        var source = await s.Repository.AddSourceAsync(s.Fonts);
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, null, default);
        var rows = (await s.Repository.QueryAsync(new())).Rows;
        var platform = new FakePlatform(s.Root); var progress = new Recorder<OperationProgress>();
        var results = await new FontOperations(s.Repository, platform).RunAsync("activate", rows, default, progress);
        Assert.Single(results); Assert.Equal(1, platform.Loads.Values.Sum());
        Assert.Equal(0, progress.Reports[0].Completed);
        Assert.All(progress.Reports, p => Assert.Equal(1, p.Total));
        Assert.Equal(1, progress.Reports[^1].Completed);
    }

    [Fact] public async Task OperationProgressAdvancesForBothFailureAndSuccess()
    {
        using var s = new Sandbox(); var rows = await IndexTwo(s);
        var platform = new FakePlatform(s.Root);
        platform.External.Add(new("external.ttf", "different-content", await s.Repository.FacesForFileAsync(rows[0].Hash)));
        var progress = new Recorder<OperationProgress>();
        var results = await new FontOperations(s.Repository, platform).RunAsync("install", rows, default, progress);
        Assert.False(results[0].Success); Assert.True(results[1].Success);
        Assert.Equal(new[] { 0, 1, 2 }, progress.Reports.Select(p => p.Completed));
        Assert.All(progress.Reports, p => Assert.Equal(2, p.Total));
    }

    [Fact] public async Task CancelOperationStopsAtActualCompletedCount()
    {
        using var s = new Sandbox(); var rows = await IndexTwo(s);
        using var cancellation = new CancellationTokenSource();
        var progress = new Recorder<OperationProgress>(p => { if (p.Completed == 1) cancellation.Cancel(); });
        var platform = new FakePlatform(s.Root);
        var results = await new FontOperations(s.Repository, platform).RunAsync("activate", rows, cancellation.Token, progress);
        Assert.InRange(results.Count, 1, 2);
        Assert.Contains(results, r => r.Success);
        Assert.Equal(results.Count(r => r.Success), platform.Loads.Values.Sum());
        Assert.Equal(results.Count, progress.Reports[^1].Completed); Assert.Equal(2, progress.Reports[^1].Total);
    }

    [Theory]
    [InlineData(false, false, true, false, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(false, true, false, true, false)]
    public void FontStatusPreservesConcurrentInstallationActivationAndUnavailableSource(
        bool installed, bool active, bool available, bool duplicate, bool inactive)
    {
        var row = new FaceRow("id", "hash", 0, "Family", "Regular", "Font", "PS", "1", 400, 5,
            false, false, false, available ? "font.ttf" : null, duplicate ? 2 : 1, installed, active, false);
        Assert.Equal(inactive, row.Inactive); Assert.Equal(!available, row.SourceUnavailable);
        Assert.Equal(duplicate, row.Duplicate);
        if (installed) Assert.Contains("已安装", row.Status);
        if (active) Assert.Contains("临时启用", row.Status);
        if (!available) Assert.Contains("来源不可用", row.Status);
        if (inactive) Assert.Contains("未安装 / 未启用", row.Status);
        if (duplicate) Assert.Contains("2 个位置", row.Status);
    }

    private static async Task<IReadOnlyList<FaceRow>> IndexTwo(Sandbox s)
    {
        await s.Repository.InitializeAsync();
        File.WriteAllBytes(Path.Combine(s.Fonts, "one.ttf"), FontData.Font("One"));
        File.WriteAllBytes(Path.Combine(s.Fonts, "two.ttf"), FontData.Font("Two"));
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(await s.Repository.AddSourceAsync(s.Fonts), false, null, default);
        return (await s.Repository.QueryAsync(new())).Rows;
    }
}
