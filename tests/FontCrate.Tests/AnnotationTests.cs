using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public class AnnotationTests
{
    [Fact]
    public async Task EmptyItemsPersistWithoutFonts()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        var item = await s.Repository.CreateNamedItemAsync("  中文项目  ");
        Assert.Equal("中文项目", item.Name); Assert.Equal(0, item.FaceCount);
        var reopened = new Repository(Path.Combine(s.Root, "library.db")); await reopened.InitializeAsync();
        Assert.Equal(item, Assert.Single(await reopened.NamedItemCountsAsync()));
        Assert.Empty((await reopened.QueryAsync(new())).Rows);
    }
    [Fact]
    public async Task RenamePreservesIdentityMembershipAndAllowsCaseChanges()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var item = await s.Repository.CreateNamedItemAsync("Old");
        await s.Repository.AnnotateByIdAsync([face.Id], item.Id);
        await s.Repository.RenameNamedItemAsync(item.Id, "old");
        await s.Repository.RenameNamedItemAsync(item.Id, "新名称");
        var renamed = Assert.Single(await s.Repository.NamedItemCountsAsync());
        Assert.Equal(item.Id, renamed.Id); Assert.Equal("新名称", renamed.Name); Assert.Equal(1, renamed.FaceCount);
        Assert.Equal(item.Id, Assert.Single(await s.Repository.FaceItemsAsync(face.Id)).Id);
        Assert.Single((await s.Repository.QueryAsync(new(TagId: item.Id))).Rows);
        var reopened = new Repository(Path.Combine(s.Root, "library.db")); await reopened.InitializeAsync();
        Assert.Equal(renamed, Assert.Single(await reopened.NamedItemCountsAsync()));
        Assert.Equal(item.Id, Assert.Single(await reopened.FaceItemsAsync(face.Id)).Id);
    }
    [Fact]
    public async Task DeleteOnlyRemovesTargetAndItsAssociations()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var target = await s.Repository.CreateNamedItemAsync("Target");
        var other = await s.Repository.CreateNamedItemAsync("Other");
        await s.Repository.AnnotateByIdAsync([face.Id], target.Id);
        await s.Repository.AnnotateByIdAsync([face.Id], other.Id);
        await s.Repository.SetFavoriteAsync([face.Id], true);
        await s.Repository.DeleteNamedItemAsync(target.Id);
        Assert.Equal(other.Id, Assert.Single(await s.Repository.FaceItemsAsync(face.Id)).Id);
        Assert.True(Assert.Single((await s.Repository.QueryAsync(new())).Rows).Favorite); Assert.True(File.Exists(face.Path));
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(s.Root, "library.db"), Pooling = false }.ToString()); await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM face_tags WHERE tag_id=$id";
        cmd.Parameters.AddWithValue("$id", target.Id); Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }
    [Fact]
    public async Task BatchMembershipIsIdempotentAndCountsUnavailableFaces()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        File.WriteAllBytes(Path.Combine(s.Fonts, "a.ttf"), FontData.Font("A"));
        File.WriteAllBytes(Path.Combine(s.Fonts, "b.ttf"), FontData.Font("B"));
        var source = await s.Repository.AddSourceAsync(s.Fonts);
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, null, default);
        var ids = (await s.Repository.QueryAsync(new())).Rows.Select(x => x.Id).ToArray();
        var item = await s.Repository.CreateNamedItemAsync("Batch");
        await s.Repository.AnnotateByIdAsync(ids.Concat(ids), item.Id);
        await s.Repository.AnnotateByIdAsync(ids, item.Id);
        await s.Repository.SetSourceAvailabilityAsync(source.Id, false);
        Assert.Equal(2, Assert.Single(await s.Repository.NamedItemCountsAsync()).FaceCount);
        await s.Repository.AnnotateByIdAsync([ids[0]], item.Id, true);
        Assert.Equal(1, Assert.Single(await s.Repository.NamedItemCountsAsync()).FaceCount);
        await s.Repository.AnnotateByIdAsync(ids, item.Id, true);
        Assert.Equal(0, Assert.Single(await s.Repository.NamedItemCountsAsync()).FaceCount);
    }
    [Fact]
    public async Task ValidationAndNameConflictsLeaveExistingDataIntact()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        await s.Repository.CreateNamedItemAsync("First");
        var second = await s.Repository.CreateNamedItemAsync("Second");
        await s.Repository.AnnotateByIdAsync([face.Id], second.Id);
        foreach (string invalid in new[] { "", " \t\n ", new string('字', 101) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => s.Repository.CreateNamedItemAsync(invalid));
            await Assert.ThrowsAsync<ArgumentException>(() => s.Repository.RenameNamedItemAsync(second.Id, invalid));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => s.Repository.CreateNamedItemAsync(" first "));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Repository.RenameNamedItemAsync(second.Id, "FIRST"));
        Assert.Equal("Second", Assert.Single(await s.Repository.FaceItemsAsync(face.Id)).Name);
        Assert.Equal(2, (await s.Repository.NamedItemsAsync()).Count);
        await s.Repository.CreateNamedItemAsync(new string('字', 100));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Repository.RenameNamedItemAsync(long.MaxValue, "Missing"));
    }
    [Fact]
    public async Task FailedBatchRollsBackAndMissingTargetsAreRejected()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var item = await s.Repository.CreateNamedItemAsync("Atomic");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => s.Repository.AnnotateByIdAsync([face.Id, "missing-face"], item.Id));
        Assert.Empty(await s.Repository.FaceItemsAsync(face.Id));
        await s.Repository.DeleteNamedItemAsync(item.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Repository.AnnotateByIdAsync([face.Id], item.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Repository.AnnotateByIdAsync([face.Id], item.Id, true));
        Assert.Empty(await s.Repository.NamedItemsAsync());
    }
    [Fact]
    public async Task ExistingNameBasedAnnotationRemainsCompatible()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var item = await s.Repository.CreateNamedItemAsync("Existing");
        await s.Repository.AnnotateAsync([face.Id], " existing ");
        Assert.Equal(item.Id, Assert.Single(await s.Repository.FaceItemsAsync(face.Id)).Id);
        await s.Repository.AnnotateAsync([face.Id], "Existing", true);
        Assert.Equal(0, Assert.Single(await s.Repository.NamedItemCountsAsync()).FaceCount);
    }
}
