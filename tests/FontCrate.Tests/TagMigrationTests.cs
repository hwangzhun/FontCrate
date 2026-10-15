using FontCrate.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FontCrate.Tests;

public class TagMigrationTests
{
    private static async Task ExecuteAsync(Sandbox sandbox, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(sandbox.Root, "library.db"), ForeignKeys = true, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<long> ScalarAsync(Sandbox sandbox, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(sandbox.Root, "library.db"), Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private const string LegacyTables = """
        CREATE TABLE collections(id INTEGER PRIMARY KEY,name TEXT NOT NULL UNIQUE COLLATE NOCASE);
        CREATE TABLE collection_faces(face_id TEXT REFERENCES faces(id) ON DELETE CASCADE,
          collection_id INTEGER REFERENCES collections(id) ON DELETE CASCADE,PRIMARY KEY(face_id,collection_id));
        PRAGMA user_version=1;
        """;

    [Fact]
    public async Task UpgradeMergesCaseInsensitiveNamesAndMembershipsAndPreservesEmptyCollections()
    {
        using var s = new Sandbox(); var first = await s.IndexAsync();
        File.WriteAllBytes(Path.Combine(s.Fonts, "second.ttf"), FontData.Font("Second"));
        await new Scanner(s.Repository, new SfntParser()).ScanAsync((await s.Repository.SourcesAsync()).Single(), false, null, default);
        var faces = (await s.Repository.QueryAsync(new())).Rows;
        var existing = await s.Repository.CreateNamedItemAsync("Project", [first.Id]);
        await s.Repository.CreateNamedItemAsync("Tag only");
        await s.Repository.SetFavoriteAsync([first.Id], true);
        await ExecuteAsync(s, LegacyTables + """
            INSERT INTO collections(id,name) VALUES(1,'project'),(2,'合集独有'),(3,'空合集');
            INSERT INTO collection_faces SELECT id,1 FROM faces;
            INSERT INTO collection_faces SELECT id,2 FROM faces;
            """);
        var reopened = new Repository(Path.Combine(s.Root, "library.db")); await reopened.InitializeAsync();
        var tags = await reopened.NamedItemCountsAsync();
        Assert.Equal(4, tags.Count);
        Assert.Equal(existing.Id, tags.Single(t => t.Name == "Project").Id);
        Assert.Equal(2, tags.Single(t => t.Id == existing.Id).FaceCount);
        Assert.Equal(2, tags.Single(t => t.Name == "合集独有").FaceCount);
        Assert.Equal(0, tags.Single(t => t.Name == "空合集").FaceCount);
        Assert.Equal(0, tags.Single(t => t.Name == "Tag only").FaceCount);
        Assert.Equal(faces.Select(f => f.Id), (await reopened.QueryAsync(new(TagId: existing.Id))).Rows.Select(f => f.Id));
        Assert.True((await reopened.QueryAsync(new())).Rows.Single(f => f.Id == first.Id).Favorite);
        Assert.True(File.Exists(first.Path));
        Assert.Equal(2, await ScalarAsync(s, "PRAGMA user_version"));
        Assert.Equal(0, await ScalarAsync(s, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('collections','collection_faces')"));
        await reopened.InitializeAsync();
        Assert.Equal(tags, await reopened.NamedItemCountsAsync());
    }

    [Fact]
    public async Task FailedMigrationRollsBackNamesMembershipsSchemaAndVersionAndCanRetry()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        await s.Repository.CreateNamedItemAsync("Keep");
        await ExecuteAsync(s, LegacyTables + """
            INSERT INTO collections VALUES(1,'Imported');
            INSERT INTO collection_faces SELECT id,1 FROM faces;
            CREATE TRIGGER fail_migration BEFORE INSERT ON face_tags BEGIN SELECT RAISE(ABORT,'injected migration failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => s.Repository.InitializeAsync());
        Assert.Equal(1, await ScalarAsync(s, "PRAGMA user_version"));
        Assert.Equal(2, await ScalarAsync(s, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('collections','collection_faces')"));
        Assert.Equal(1, await ScalarAsync(s, "SELECT COUNT(*) FROM collection_faces"));
        Assert.Equal("Keep", Assert.Single(await s.Repository.NamedItemsAsync()).Name);
        Assert.Empty(await s.Repository.FaceItemsAsync(face.Id));
        await ExecuteAsync(s, "DROP TRIGGER fail_migration");
        await s.Repository.InitializeAsync();
        Assert.Equal("Imported", Assert.Single(await s.Repository.FaceItemsAsync(face.Id)).Name);
    }

    [Fact]
    public async Task NewDatabaseHasOnlyTagsAndCanReopen()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync(); await s.Repository.InitializeAsync();
        Assert.Equal(2, await ScalarAsync(s, "PRAGMA user_version"));
        Assert.Equal(0, await ScalarAsync(s, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('collections','collection_faces')"));
    }

    [Fact]
    public async Task CreatingTagWithSelectedFontsIsAtomicAndDeduplicatesSelection()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        await Assert.ThrowsAsync<SqliteException>(() => s.Repository.CreateNamedItemAsync("Failed", [face.Id, "missing"]));
        Assert.Empty(await s.Repository.NamedItemsAsync());
        var tag = await s.Repository.CreateNamedItemAsync("Selection", [face.Id, face.Id]);
        Assert.Equal(1, tag.FaceCount);
        Assert.Equal(tag.Id, Assert.Single(await s.Repository.FaceItemsAsync(face.Id)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => s.Repository.CreateNamedItemAsync("selection", [face.Id]));
        Assert.Equal(1, Assert.Single(await s.Repository.NamedItemCountsAsync()).FaceCount);
    }
}
