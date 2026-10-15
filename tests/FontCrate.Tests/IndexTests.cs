using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public class IndexTests
{
    [Fact] public async Task FiltersAndBatchAnnotationRemovalAreIndependent()
    {
        using var s = new Sandbox(); var row = await s.IndexAsync();
        await s.Repository.AnnotateAsync([row.Id], "Tag"); await s.Repository.AnnotateAsync([row.Id], "Group");
        var tags = await s.Repository.NamedItemsAsync(); long tag = tags.Single(t => t.Name == "Tag").Id, group = tags.Single(t => t.Name == "Group").Id;
        Assert.Single((await s.Repository.QueryAsync(new(TagId: tag))).Rows);
        await s.Repository.AnnotateAsync([row.Id], "Tag", true);
        Assert.Empty((await s.Repository.QueryAsync(new(TagId: tag))).Rows); Assert.Single((await s.Repository.QueryAsync(new(TagId: group))).Rows);
    }
    [Fact] public async Task OverlappingSourcesDoNotCountOnePathAsDuplicate()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync(); var sub = Path.Combine(s.Fonts, "child"); Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "a.ttf"), FontData.Font("One"));
        var scanner = new Scanner(s.Repository, new SfntParser());
        await scanner.ScanAsync(await s.Repository.AddSourceAsync(s.Fonts), false, null, default);
        await scanner.ScanAsync(await s.Repository.AddSourceAsync(sub), false, null, default);
        Assert.Equal(1, (await s.Repository.QueryAsync(new())).Rows.Single().Locations);
        Assert.Empty((await s.Repository.QueryAsync(new(State: "完全重复"))).Rows);
    }
    [Fact] public async Task NewerDatabaseVersionIsRejected()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(s.Root, "library.db"), Pooling = false }.ToString()))
        { await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version=99"; await cmd.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Repository.InitializeAsync());
    }
    [Fact] public async Task ParserHandlesCollectionAndSupplementaryCharacters()
    {
        using var s = new Sandbox(); string path = Path.Combine(s.Fonts, "fonts.ttc");
        File.WriteAllBytes(path, FontData.Collection("中文字体", "Second Font"));
        var faces = await new SfntParser().ParseAsync(path, default);
        Assert.Equal(2, faces.Count); Assert.Equal("中文字体", faces[0].Family); Assert.Equal(1, faces[1].Index);
        Assert.Contains(new UnicodeRange(0x20000, 0x20001), faces[0].Coverage);
    }
    [Fact] public async Task ParserRejectsTruncationAndTableOverflow()
    {
        using var s = new Sandbox(); string path = Path.Combine(s.Fonts, "bad.ttf"); var parser = new SfntParser();
        File.WriteAllBytes(path, [0, 1]); await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(path, default));
        var b = FontData.Font("Test"); FontData.Put32(b, 20, int.MaxValue); File.WriteAllBytes(path, b);
        await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(path, default));
    }
    [Fact] public async Task RepeatedScanSkipsParsingAndForcedScanDetectsHiddenChange()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync(); var parser = new CountingParser();
        string path = Path.Combine(s.Fonts, "a.ttf"); File.WriteAllBytes(path, FontData.Font("First", "Version 1"));
        var source = await s.Repository.AddSourceAsync(s.Fonts); var scanner = new Scanner(s.Repository, parser);
        await scanner.ScanAsync(source, false, null, default); int calls = parser.Calls;
        var repeat = await scanner.ScanAsync(source, false, null, default); Assert.Equal(calls, parser.Calls); Assert.Equal(1, repeat.Unchanged);
        var time = File.GetLastWriteTimeUtc(path); File.WriteAllBytes(path, FontData.Font("First", "Version 2")); File.SetLastWriteTimeUtc(path, time);
        await scanner.ScanAsync(source, false, null, default); Assert.Equal(calls, parser.Calls);
        await scanner.ScanAsync(source, true, null, default); Assert.Equal(calls + 1, parser.Calls);
        var page = await s.Repository.QueryAsync(new()); Assert.Equal(2, page.Total); Assert.Contains(page.Rows, f => f.Version == "Version 2");
    }
    [Fact] public async Task MovingAndDuplicatingContentPreservesAnnotations()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        await s.Repository.SetFavoriteAsync([face.Id], true); await s.Repository.AnnotateAsync([face.Id], "中文");
        await s.Repository.AnnotateAsync([face.Id], "项目 A");
        File.Move(Path.Combine(s.Fonts, "test.ttf"), Path.Combine(s.Fonts, "moved.ttf"));
        File.Copy(Path.Combine(s.Fonts, "moved.ttf"), Path.Combine(s.Fonts, "duplicate.otf"));
        var source = (await s.Repository.SourcesAsync()).Single(); await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, null, default);
        var row = (await s.Repository.QueryAsync(new(State: "完全重复"))).Rows.Single();
        Assert.Equal(face.Id, row.Id); Assert.True(row.Favorite); Assert.Equal(2, row.Locations);
        Assert.Equal("中文", (await s.Repository.FaceItemsAsync(row.Id)).Single(t => t.Name == "中文").Name);
        Assert.Equal("项目 A", (await s.Repository.FaceItemsAsync(row.Id)).Single(t => t.Name == "项目 A").Name);
    }
    [Fact] public async Task BadFontDoesNotStopOtherFilesAndMissingPathsOnlyChangeAfterCompleteScan()
    {
        using var s = new Sandbox(); var first = await s.IndexAsync();
        File.WriteAllBytes(Path.Combine(s.Fonts, "bad.ttf"), [0, 0, 0]); File.Delete(Path.Combine(s.Fonts, "test.ttf"));
        File.WriteAllBytes(Path.Combine(s.Fonts, "other.ttf"), FontData.Font("Other"));
        var source = (await s.Repository.SourcesAsync()).Single();
        var result = await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, null, default);
        Assert.True(result.Complete); Assert.Equal(1, result.Errors); Assert.Single(await s.Repository.ErrorsAsync());
        var page = await s.Repository.QueryAsync(new()); Assert.Equal(2, page.Total);
        Assert.Null(page.Rows.Single(f => f.Id == first.Id).Path); Assert.NotNull(page.Rows.Single(f => f.Id != first.Id).Path);
    }
    [Fact] public async Task CancellationDoesNotMarkUnseenPathsMissing()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); File.Delete(Path.Combine(s.Fonts, "test.ttf"));
        File.WriteAllBytes(Path.Combine(s.Fonts, "slow.ttf"), FontData.Font("Slow"));
        var source = (await s.Repository.SourcesAsync()).Single(); using var cancellation = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Scanner(s.Repository, new CountingParser { Delay = 2000 }).ScanAsync(source, false, null, cancellation.Token));
        Assert.NotNull((await s.Repository.QueryAsync(new())).Rows.Single().Path);
    }
    [Fact] public async Task RemovingSourcePreservesFaceAndUserData()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); await s.Repository.SetFavoriteAsync([face.Id], true);
        await s.Repository.RemoveSourceAsync((await s.Repository.SourcesAsync()).Single().Id);
        var row = (await s.Repository.QueryAsync(new(FavoritesOnly: true))).Rows.Single(); Assert.Equal(face.Id, row.Id); Assert.Null(row.Path);
    }
    [Fact] public async Task UnavailableSourceKeepsLocationsAndReportsError()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var source = (await s.Repository.SourcesAsync()).Single();
        Directory.Move(s.Fonts, s.Fonts + "-offline");
        var result = await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, null, default);
        Assert.False(result.Complete); Assert.Single(await s.Repository.LocationsAsync(face.Hash));
        Assert.False((await s.Repository.SourcesAsync()).Single().Available); Assert.Null((await s.Repository.QueryAsync(new())).Rows.Single().Path);
    }
    [Fact] public async Task TenThousandFacesUseContinuousResultsStableOrderingAndLiteralSearch()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync(); var source = await s.Repository.AddSourceAsync(s.Fonts);
        for (int start = 0; start < 10000; start += 100)
        {
            var batch = Enumerable.Range(start, 100).Select(i => new IndexedFile(Path.Combine(s.Fonts, i + ".ttf"), 1, 0, i.ToString("x64"),
                new[] { new ParsedFace(0, i == 123 ? "Literal_%" : $"Family {i:00000}", "Regular", "Font", "PS" + i, "1", 400, 5, false, false, []) })).ToArray();
            await s.Repository.SaveBatchAsync(source.Id, "test", batch);
        }
        var a = await s.Repository.QueryAsync(new()); var b = await s.Repository.QueryAsync(new());
        Assert.Equal(10000, a.Total); Assert.Equal(10000, a.Rows.Count);
        Assert.Equal(10000, a.Rows.Select(f => f.Id).Distinct().Count());
        Assert.Equal(a.Rows.Select(f => f.Id), b.Rows.Select(f => f.Id));
        Assert.Equal("Family 00000", a.Rows[0].Family);
        Assert.Equal("Literal_%", a.Rows[^1].Family);
        Assert.Equal(10000, (await s.Repository.QueryAsync(new(SourceId: source.Id))).Rows.Count);
        await s.Repository.SetFavoriteAsync(a.Rows.Take(650).Select(f => f.Id), true);
        Assert.Equal(650, (await s.Repository.QueryAsync(new(FavoritesOnly: true))).Rows.Count);
        await s.Repository.AnnotateAsync(a.Rows.Take(600).Select(f => f.Id), "Large group");
        var group = (await s.Repository.NamedItemsAsync()).Single();
        Assert.Equal(600, (await s.Repository.QueryAsync(new(TagId: group.Id))).Rows.Count);
        Assert.Single((await s.Repository.QueryAsync(new(Text: "_%"))).Rows);
    }
}
