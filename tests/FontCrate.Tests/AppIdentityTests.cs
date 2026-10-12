using FontCrate.App;
using Xunit;

namespace FontCrate.Tests;

public class AppIdentityTests
{
    [Fact] public void NewInstallationUsesFontCrateDirectory()
    {
        using var s = new Sandbox();
        Assert.Equal(Path.Combine(s.Root, "FontCrate"), AppIdentity.ResolveDataDirectory(s.Root));
    }

    [Fact] public void UpgradeReusesLegacyDatabaseAndJournaledFontCopyPaths()
    {
        using var s = new Sandbox();
        string legacy = Path.Combine(s.Root, "FontLite"); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "library.db"), "existing database");
        Assert.Equal(legacy, AppIdentity.ResolveDataDirectory(s.Root));
        Assert.Equal("existing database", File.ReadAllText(Path.Combine(legacy, "library.db")));
        Assert.False(Directory.Exists(Path.Combine(s.Root, "FontCrate")));
    }

    [Fact] public void ExistingFontCrateDataTakesPrecedence()
    {
        using var s = new Sandbox();
        string current = Path.Combine(s.Root, "FontCrate"); Directory.CreateDirectory(current);
        Directory.CreateDirectory(Path.Combine(s.Root, "FontLite"));
        Assert.Equal(current, AppIdentity.ResolveDataDirectory(s.Root));
    }
}
