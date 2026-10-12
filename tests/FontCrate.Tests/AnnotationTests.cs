using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public class AnnotationTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task EmptyItemsPersistWithoutFonts(bool collections)
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        var item = await s.Repository.CreateNamedItemAsync("  中文项目  ", collections);
        Assert.Equal("中文项目", item.Name); Assert.Equal(0, item.FaceCount);
        var reopened = new Repository(Path.Combine(s.Root, "library.db")); await reopened.InitializeAsync();
        Assert.Equal(item, Assert.Single(await reopened.NamedItemCountsAsync(collections)));
        Assert.Empty((await reopened.QueryAsync(new())).Rows);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RenamePreservesIdentityMembershipAndAllowsCaseChanges(bool collections)
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var item = await s.Repository.CreateNamedItemAsync("Old", collections);
        await s.Repository.AnnotateByIdAsync([face.Id], item.Id, collections);
        await s.Repository.RenameNamedItemAsync(item.Id, "old", collections);
        await s.Repository.RenameNamedItemAsync(item.Id, "新名称", collections);
        var renamed = Assert.Single(await s.Repository.NamedItemCountsAsync(collections));
        Assert.Equal(item.Id, renamed.Id); Assert.Equal("新名称", renamed.Name); Assert.Equal(1, renamed.FaceCount);
        Assert.Equal(item.Id, Assert.Single(await s.Repository.FaceItemsAsync(face.Id, collections)).Id);
        Assert.Single((await s.Repository.QueryAsync(collections ? new(CollectionId: item.Id) : new(TagId: item.Id))).Rows);
        var reopened = new Repository(Path.Combine(s.Root, "library.db")); await reopened.InitializeAsync();
        Assert.Equal(renamed, Assert.Single(await reopened.NamedItemCountsAsync(collections)));
        Assert.Equal(item.Id, Assert.Single(await reopened.FaceItemsAsync(face.Id, collections)).Id);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task DeleteOnlyRemovesTargetAndItsAssociations(bool collections)
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var target = await s.Repository.CreateNamedItemAsync("Target", collections);
        var other = await s.Repository.CreateNamedItemAsync("Other", collections);
        await s.Repository.AnnotateByIdAsync([face.Id], target.Id, collections);
        await s.Repository.AnnotateByIdAsync([face.Id], other.Id, collections);
        await s.Repository.AnnotateAsync([face.Id], "Opposite", !collections);
        await s.Repository.SetFavoriteAsync([face.Id], true);
        await s.Repository.DeleteNamedItemAsync(target.Id, collections);
        Assert.Equal(other.Id, Assert.Single(await s.Repository.FaceItemsAsync(face.Id, collections)).Id);
        Assert.Single(await s.Repository.FaceItemsAsync(face.Id, !collections));
        Assert.True(Assert.Single((await s.Repository.QueryAsync(new())).Rows).Favorite); Assert.True(File.Exists(face.Path));
        using var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(s.Root, "library.db")); await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = collections ? "SELECT COUNT(*) FROM collection_faces WHERE collection_id=$id" : "SELECT COUNT(*) FROM face_tags WHERE tag_id=$id";
        cmd.Parameters.AddWithValue("$id", target.Id); Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task BatchMembershipIsIdempotentAndCountsUnavailableFaces(bool collections)
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        File.WriteAllBytes(Path.Combine(s.Fonts, "a.ttf"), FontData.Font("A"));
        File.WriteAllBytes(Path.Combine(s.Fonts, "b.ttf"), FontData.Font("B"));
        var source = await s.Repository.AddSourceAsync(s.Fonts);
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, null, default);
        var ids = (await s.Repository.QueryAsync(new())).Rows.Select(x => x.Id).ToArray();
        var item = await s.Repository.CreateNamedItemAsync("Batch", collections);
        await s.Repository.AnnotateByIdAsync(ids.Concat(ids), item.Id, collections);
        await s.Repository.AnnotateByIdAsync(ids, item.Id, collections);
        await s.Repository.SetSourceAvailabilityAsync(source.Id, false);
        Assert.Equal(2, Assert.Single(await s.Repository.NamedItemCountsAsync(collections)).FaceCount);
        await s.Repository.AnnotateByIdAsync([ids[0]], item.Id, collections, true);
        Assert.Equal(1, Assert.Single(await s.Repository.NamedItemCountsAsync(collections)).FaceCount);
        await s.Repository.AnnotateByIdAsync(ids, item.Id, collections, true);
        Assert.Equal(0, Assert.Single(await s.Repository.NamedItemCountsAsync(collections)).FaceCount);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ValidationAndNameConflictsLeaveExistingDataIntact(bool collections)
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        await s.Repository.CreateNamedItemAsync("First", collections);
        var second = await s.Repository.CreateNamedItemAsync("Second", collections);
        await s.Repository.AnnotateByIdAsync([face.Id], second.Id, collections);
        foreach (string invalid in new[] { "", " \t\n ", new string('字', 101) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => s.Repository.CreateNamedItemAsync(invalid, collections));
            await Assert.ThrowsAsync<ArgumentException>(() => s.Repository.RenameNamedItemAsync(second.Id, invalid, collections));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => s.Repository.CreateNamedItemAsync(" first ", collections));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Repository.RenameNamedItemAsync(second.Id, "FIRST", collections));
        Assert.Equal("Second", Assert.Single(await s.Repository.FaceItemsAsync(face.Id, collections)).Name);
        Assert.Equal(2, (await s.Repository.NamedItemsAsync(collections)).Count);
        await s.Repository.CreateNamedItemAsync("First", !collections);
        await s.Repository.CreateNamedItemAsync(new string('字', 100), collections);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Repository.RenameNamedItemAsync(long.MaxValue, "Missing", collections));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FailedBatchRollsBackAndMissingTargetsAreRejected(bool collections)
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var item = await s.Repository.CreateNamedItemAsync("Atomic", collections);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => s.Repository.AnnotateByIdAsync([face.Id, "missing-face"], item.Id, collections));
        Assert.Empty(await s.Repository.FaceItemsAsync(face.Id, collections));
        await s.Repository.DeleteNamedItemAsync(item.Id, collections);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Repository.AnnotateByIdAsync([face.Id], item.Id, collections));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Repository.AnnotateByIdAsync([face.Id], item.Id, collections, true));
        Assert.Empty(await s.Repository.NamedItemsAsync(collections));
    }
    [Fact]
    public async Task ExistingNameBasedAnnotationRemainsCompatible()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var item = await s.Repository.CreateNamedItemAsync("Existing", false);
        await s.Repository.AnnotateAsync([face.Id], " existing ", false);
        Assert.Equal(item.Id, Assert.Single(await s.Repository.FaceItemsAsync(face.Id, false)).Id);
        await s.Repository.AnnotateAsync([face.Id], "Existing", false, true);
        Assert.Equal(0, Assert.Single(await s.Repository.NamedItemCountsAsync(false)).FaceCount);
    }
}
