using FontCrate.App;
using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public class UiSettingsTests
{
    [Fact]
    public void MissingSettingsDefaultToSystemAndExplainFallback()
    {
        using var sandbox = new Sandbox();
        var result = new SettingsStore(Path.Combine(sandbox.Root, "settings.json")).Load();
        Assert.Equal(ThemeMode.System, result.Settings.Theme);
        Assert.NotEmpty(result.Notice);
    }
    [Theory]
    [InlineData(ThemeMode.System)][InlineData(ThemeMode.Light)][InlineData(ThemeMode.Dark)]
    public void ThemeChoiceSurvivesRestartAndReplacesPreviousChoice(ThemeMode mode)
    {
        using var sandbox = new Sandbox();
        string path = Path.Combine(sandbox.Root, "settings.json");
        var store = new SettingsStore(path);
        store.Save(new(ThemeMode.Light)); store.Save(new(mode));
        var loaded = new SettingsStore(path).Load();
        Assert.Equal(mode, loaded.Settings.Theme); Assert.Empty(loaded.Notice);
        Assert.False(File.Exists(path + ".tmp"));
    }
    [Theory]
    [InlineData("not json")][InlineData("null")][InlineData("{\"Theme\":\"Unknown\"}")][InlineData("{\"Theme\":99}")]
    public void CorruptSettingsDefaultToSystemWithoutDeletingTheFile(string json)
    {
        using var sandbox = new Sandbox(); string path = Path.Combine(sandbox.Root, "settings.json");
        File.WriteAllText(path, json);
        var result = new SettingsStore(path).Load();
        Assert.Equal(ThemeMode.System, result.Settings.Theme); Assert.NotEmpty(result.Notice);
        Assert.Equal(json, File.ReadAllText(path));
    }
    [Fact]
    public async Task PreviewCacheSeparatesColorsButReusesIdenticalPixelsAcrossGenerations()
    {
        using var sandbox = new Sandbox(); var face = await sandbox.IndexAsync();
        var light = new PreviewRequest(face, "字体 Aa", 24, 144, 1, GridCodes: [65, 66]);
        var key = PreviewCacheKey.From(light);
        Assert.Equal(key, PreviewCacheKey.From(light with { Generation = 100, GridCodes = new[] { 65, 66 } }));
        Assert.NotEqual(key, PreviewCacheKey.From(light with { ForegroundArgb = 0xFFFFFFFF }));
        Assert.NotEqual(key, PreviewCacheKey.From(light with { BackgroundArgb = 0xFF202020 }));
        Assert.NotEqual(key, PreviewCacheKey.From(light with { GridCodes = [66, 65] }));
        Assert.Equal(0xFFFFFFFFu, light.BackgroundArgb);
        Assert.Equal(0xFF1F293Bu, light.ForegroundArgb);
    }
}
