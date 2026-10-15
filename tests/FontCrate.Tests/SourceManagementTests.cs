using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public class SourceManagementTests
{
    [Fact]
    public async Task SummaryDeduplicatesContentAndCountsCollectionFacesWithoutMultiplyingJoins()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        var ttc = FontData.Collection("Collection One", "Collection Two");
        File.WriteAllBytes(Path.Combine(s.Fonts, "collection.ttc"), ttc);
        File.WriteAllBytes(Path.Combine(s.Fonts, "duplicate.ttc"), ttc);
        File.WriteAllBytes(Path.Combine(s.Fonts, "single.ttf"), FontData.Font("Single"));
        File.WriteAllBytes(Path.Combine(s.Fonts, "broken.ttf"), [0, 0]);
        var source = await s.Repository.AddSourceAsync(s.Fonts);
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, null, default);
        var summary = Assert.Single(await s.Repository.SourceSummariesAsync());
        Assert.Equal(2, summary.FileCount); Assert.Equal(3, summary.FaceCount);
        Assert.True(summary.Available); Assert.Equal(source.Id, summary.Id);
        Assert.Equal((await s.Repository.QueryAsync(new(SourceId: source.Id))).Total, summary.FaceCount);
    }
    [Fact]
    public async Task OfflineSourceRetainsStatisticsAndRemovalKeepsOtherSourceAndAnnotations()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var original = Assert.Single(await s.Repository.SourcesAsync());
        var secondFolder = Path.Combine(s.Root, "second"); Directory.CreateDirectory(secondFolder);
        File.Copy(face.Path!, Path.Combine(secondFolder, "shared.ttf"));
        var second = await s.Repository.AddSourceAsync(secondFolder);
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(second, false, null, default);
        await s.Repository.AnnotateAsync([face.Id], "Retained");
        Directory.Move(s.Fonts, s.Fonts + "-offline");
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(original, false, null, default);
        var summaries = await s.Repository.SourceSummariesAsync();
        var offline = summaries.Single(i => i.Id == original.Id);
        Assert.False(offline.Available); Assert.Equal(1, offline.FileCount); Assert.Equal(1, offline.FaceCount);
        Assert.Equal(1, summaries.Single(i => i.Id == second.Id).FaceCount);
        await s.Repository.RemoveSourceAsync(original.Id);
        Assert.Equal(second.Id, Assert.Single(await s.Repository.SourceSummariesAsync()).Id);
        Assert.Single(await s.Repository.FaceItemsAsync(face.Id));
        Assert.NotNull(Assert.Single((await s.Repository.QueryAsync(new())).Rows).Path);
    }
    [Fact]
    public async Task EmptySourcesHaveZeroCountsAndScanningOneLeavesOtherSourceUntouched()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        File.WriteAllBytes(Path.Combine(s.Fonts, "one.ttf"), FontData.Font("First"));
        var first = await s.Repository.AddSourceAsync(s.Fonts);
        var folder = Path.Combine(s.Root, "second"); Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "two.ttf"), FontData.Font("Second"));
        var second = await s.Repository.AddSourceAsync(folder);
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(first, false, null, default);
        var summaries = await s.Repository.SourceSummariesAsync();
        Assert.Equal(1, summaries.Single(i => i.Id == first.Id).FaceCount);
        Assert.Equal(0, summaries.Single(i => i.Id == second.Id).FaceCount);
        Assert.Empty((await s.Repository.QueryAsync(new(SourceId: second.Id))).Rows);
    }
}
