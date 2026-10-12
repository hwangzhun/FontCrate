using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public class OperationTests
{
    [Fact] public async Task FailureAfterRegistryWriteStillRollsBackRegistration()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root) { FailAfterRegistration = true };
        Assert.False((await new FontOperations(s.Repository, platform).RunAsync("install", [face], default)).Single().Success);
        Assert.Empty(platform.Registry); Assert.Empty(Directory.GetFiles(platform.UserFontsDirectory));
    }
    [Fact] public async Task RecoveredPersistentInstallCanBeUninstalled()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root);
        await new FontOperations(s.Repository, platform).RunAsync("install", [face], default);
        var fresh = new FontOperations(s.Repository, platform); await fresh.RecoverAsync(default);
        Assert.True((await fresh.RunAsync("uninstall", [face], default)).Single().Success);
        Assert.Empty(platform.Registry); Assert.Equal(0, platform.Loads.Values.Sum());
    }
    [Fact] public async Task FailedRecoveryUnloadRetainsTemporaryFile()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root);
        await new FontOperations(s.Repository, platform).RunAsync("activate", [face], default); platform.FailUnload = true;
        var fresh = new FontOperations(s.Repository, platform); Assert.False((await fresh.RecoverAsync(default)).Single().Success);
        Assert.True(File.Exists(platform.Loads.Keys.Single())); Assert.Equal(1, platform.Loads.Values.Sum());
        platform.FailUnload = false; Assert.True((await fresh.RecoverAsync(default)).Single().Success); Assert.Equal(0, platform.Loads.Values.Sum());
    }
    [Fact] public async Task RecoveryAfterOsAlreadyUnloadedFontDoesNotReloadIt()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root);
        await new FontOperations(s.Repository, platform).RunAsync("activate", [face], default);
        platform.Loads[platform.Loads.Keys.Single()] = 0;
        Assert.True((await new FontOperations(s.Repository, platform).RecoverAsync(default)).Single().Success);
        Assert.Empty(Directory.GetFiles(platform.TemporaryDirectory)); Assert.Equal(0, platform.Loads.Values.Sum());
    }
    [Fact] public async Task PreparedInstallCopyIsRecoveredWithoutTouchingOtherRegistrations()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root);
        Directory.CreateDirectory(platform.UserFontsDirectory); string path = Path.Combine(platform.UserFontsDirectory, "FontCrate-test.ttf"); File.Copy(face.Path!, path);
        await s.Repository.BeginOperationAsync(face.Hash, "install", path, "FontCrate test"); platform.Registry["External"] = "outside.ttf";
        Assert.True((await new FontOperations(s.Repository, platform).RecoverAsync(default)).Single().Success);
        Assert.False(File.Exists(path)); Assert.Equal("outside.ttf", platform.Registry["External"]);
    }
    [Fact] public async Task ExternallyModifiedRegistryIsNotRemovedByRecovery()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        await ops.RunAsync("install", [face], default); string key = platform.Registry.Keys.Single(); platform.Registry[key] = "external.ttf";
        Assert.False((await ops.RecoverAsync(default)).Single().Success); Assert.Equal("external.ttf", platform.Registry[key]);
        Assert.False((await s.Repository.QueryAsync(new())).Rows.Single().Owned);
    }
    [Fact] public async Task CancellationBeforeOperationDoesNotLoadAnything()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root); using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FontOperations(s.Repository, platform).RunAsync("activate", [face], token.Token));
        Assert.Empty(platform.Loads); Assert.Empty(platform.Registry);
    }
    [Fact] public async Task ActivationIsIdempotentAndExitRemovesOnlyItsOwnLoad()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        Assert.True((await ops.RunAsync("activate", [face], default)).Single().Success);
        await ops.RunAsync("activate", [face], default); Assert.Equal(1, platform.Loads.Values.Sum());
        Assert.True((await s.Repository.QueryAsync(new())).Rows.Single().Active);
        Assert.True((await ops.ExitAsync()).Single().Success); Assert.Equal(0, platform.Loads.Values.Sum());
        Assert.Empty(Directory.GetFiles(platform.TemporaryDirectory));
    }
    [Fact] public async Task ActivationToInstallationKeepsPersistentFontAfterExit()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        await ops.RunAsync("activate", [face], default); Assert.True((await ops.RunAsync("install", [face], default)).Single().Success);
        await ops.ExitAsync(); Assert.Single(platform.Registry); Assert.Equal(1, platform.Loads.Values.Sum());
        var row = (await s.Repository.QueryAsync(new())).Rows.Single(); Assert.True(row.Installed); Assert.True(row.Owned); Assert.False(row.Active);
        Assert.True((await ops.RunAsync("uninstall", [row], default)).Single().Success); Assert.Empty(platform.Registry); Assert.Equal(0, platform.Loads.Values.Sum());
    }
    [Fact] public async Task ExitNotifiesAfterUnloadingEvenWhenCopyCleanupFails()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        Assert.True((await ops.RunAsync("activate", [face], default)).Single().Success);
        string copy = platform.Loads.Keys.Single();
        File.AppendAllText(copy, "externally modified");
        int notifications = platform.Notifications;
        Assert.False((await ops.ExitAsync()).Single().Success);
        Assert.Equal(0, platform.Loads.Values.Sum());
        Assert.Equal(notifications + 1, platform.Notifications);
        Assert.True(File.Exists(copy));
    }
    [Fact] public async Task SameNameDifferentVersionDoesNotOverwriteExistingFont()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root);
        var parsed = await s.Repository.FacesForFileAsync(face.Hash); platform.External.Add(new("external.ttf", "other-hash", parsed));
        var result = await new FontOperations(s.Repository, platform).RunAsync("install", [face], default);
        Assert.False(result.Single().Success); Assert.Empty(platform.Registry); Assert.Empty(platform.Loads);
    }
    [Fact] public async Task FailedInstallRollsBackFilesAndRegistry()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root) { FailLoad = true };
        var result = await new FontOperations(s.Repository, platform).RunAsync("install", [face], default);
        Assert.False(result.Single().Success); Assert.Empty(platform.Registry); Assert.Empty(Directory.GetFiles(platform.UserFontsDirectory));
        Assert.Empty(await s.Repository.OperationsAsync());
    }
    [Fact] public async Task RegistryFailureRollsBackCopy()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root) { FailRegistration = true };
        Assert.False((await new FontOperations(s.Repository, platform).RunAsync("install", [face], default)).Single().Success);
        Assert.Empty(Directory.GetFiles(platform.UserFontsDirectory)); Assert.Empty(platform.Registry);
    }
    [Fact] public async Task ExternalInstallationCannotBeUninstalled()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root);
        Assert.False((await new FontOperations(s.Repository, platform).RunAsync("uninstall", [face], default)).Single().Success);
    }
    [Fact] public async Task TamperedOwnedCopyIsNeverDeletedOrUnregistered()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        await ops.RunAsync("install", [face], default); string path = platform.Registry.Values.Single(); File.WriteAllText(path, "external content");
        Assert.False((await ops.RunAsync("uninstall", [face], default)).Single().Success); Assert.True(File.Exists(path)); Assert.Single(platform.Registry);
        Assert.False((await ops.RecoverAsync(default)).Single().Success); Assert.True(File.Exists(path)); Assert.Single(platform.Registry);
    }
    [Fact] public async Task FailedStopRetainsActiveRecordAndCanBeRetried()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        await ops.RunAsync("activate", [face], default); platform.FailUnload = true;
        Assert.False((await ops.ExitAsync()).Single().Success); Assert.True(File.Exists(platform.Loads.Keys.Single()));
        Assert.Equal("pending", (await s.Repository.OperationsAsync()).Single().Stage);
        platform.FailUnload = false; Assert.True((await ops.ExitAsync()).Single().Success);
    }
    [Fact] public async Task RecoveryCleansCrashedTemporaryFontWithoutRestoringIt()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); var platform = new FakePlatform(s.Root);
        await new FontOperations(s.Repository, platform).RunAsync("activate", [face], default);
        var fresh = new FontOperations(s.Repository, platform); Assert.True((await fresh.RecoverAsync(default)).Single().Success);
        Assert.Equal(0, platform.Loads.Values.Sum()); Assert.False((await s.Repository.QueryAsync(new())).Rows.Single().Active);
    }
    [Fact] public async Task CollectionOperationAffectsEveryFaceButLoadsOnlyOnce()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync(); File.WriteAllBytes(Path.Combine(s.Fonts, "c.ttc"), FontData.Collection("First", "Second"));
        var source = await s.Repository.AddSourceAsync(s.Fonts); await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, null, default);
        var rows = (await s.Repository.QueryAsync(new())).Rows; var platform = new FakePlatform(s.Root);
        var result = await new FontOperations(s.Repository, platform).RunAsync("activate", rows, default);
        Assert.Single(result); Assert.Equal(1, platform.Loads.Values.Sum()); Assert.All((await s.Repository.QueryAsync(new())).Rows, r => Assert.True(r.Active));
    }
    [Fact] public async Task SourceModificationBlocksActivation()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); File.WriteAllBytes(face.Path!, FontData.Font("Changed")); var platform = new FakePlatform(s.Root);
        Assert.False((await new FontOperations(s.Repository, platform).RunAsync("activate", [face], default)).Single().Success); Assert.Empty(platform.Loads);
    }
}
