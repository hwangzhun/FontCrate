using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public class FontTypeTests
{
    [Theory]
    [InlineData("font.ttf", "TTF")]
    [InlineData("font.OTF", "OTF")]
    [InlineData("collection.ttc", "TTC")]
    [InlineData("collection.otc", "OTC")]
    [InlineData("web.woff", "WOFF")]
    [InlineData("web.WoFf2", "WOFF2")]
    [InlineData("font", "未知")]
    [InlineData("font.bin", "未知")]
    [InlineData(null, "未知")]
    public void FileTypeUsesCaseInsensitiveFileExtension(string? path, string expected)
        => Assert.Equal(expected, FontFileType.FromPath(path));

    [Fact] public async Task OfflineIndexedFontRetainsFileTypeWithoutPretendingSourceIsAvailable()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(filename: "font.OTF");
        Assert.Equal("OTF", face.FileType);
        Directory.Move(s.Fonts, s.Fonts + "-offline");
        await new Scanner(s.Repository, new SfntParser()).ScanAsync((await s.Repository.SourcesAsync()).Single(), false, null, default);
        var offline = (await s.Repository.QueryAsync(new())).Rows.Single();
        Assert.Null(offline.Path); Assert.True(offline.SourceUnavailable); Assert.Equal("OTF", offline.FileType);
    }

    [Fact] public async Task RevokingTemporaryFontDoesNotRemoveSourceOrPersistentInstallation()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var platform = new FakePlatform(s.Root); var operations = new FontOperations(s.Repository, platform);
        Assert.True((await operations.RunAsync("activate", [face], default)).Single().Success);
        Assert.True((await operations.RunAsync("deactivate", [face], default)).Single().Success);
        Assert.True(File.Exists(face.Path)); Assert.Empty(platform.Registry);
        Assert.False((await s.Repository.QueryAsync(new())).Rows.Single().Active);
        Assert.Equal(0, platform.Loads.Values.Sum());
        Assert.True((await operations.RunAsync("install", [face], default)).Single().Success);
        Assert.True((await operations.RunAsync("deactivate", [face], default)).Single().Success);
        Assert.Single(platform.Registry); Assert.True(File.Exists(face.Path));
        Assert.True((await s.Repository.QueryAsync(new())).Rows.Single().Installed);
    }
}
