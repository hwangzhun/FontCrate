using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public class ConcurrentActivationTests
{
    private sealed class Recorder : IProgress<OperationProgress>
    {
        public List<OperationProgress> Reports { get; } = [];
        public void Report(OperationProgress value) => Reports.Add(value);
    }
    private static async Task<IReadOnlyList<FaceRow>> Index(Sandbox s, params byte[][] fonts)
    {
        await s.Repository.InitializeAsync();
        for (int i = 0; i < fonts.Length; i++) File.WriteAllBytes(Path.Combine(s.Fonts, $"font{i}.ttf"), fonts[i]);
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(await s.Repository.AddSourceAsync(s.Fonts), false, null, default);
        return (await s.Repository.QueryAsync(new())).Rows;
    }

    [Fact] public async Task BatchLoadsConcurrentlyWithinLimitAndPreservesResultOrder()
    {
        using var s = new Sandbox();
        var rows = await Index(s, Enumerable.Range(0, 20).Select(i => FontData.Font($"Font{i:D2}")).ToArray());
        var platform = new FakePlatform(s.Root) { LoadDelay = 150 };
        var ops = new FontOperations(s.Repository, platform); var progress = new Recorder();
        var result = await ops.RunAsync("activate", rows.Reverse().ToArray(), default, progress);
        Assert.All(result, r => Assert.True(r.Success, r.Message));
        Assert.Equal(rows.Reverse().Select(r => r.Hash), result.Select(r => r.Hash));
        Assert.InRange(platform.MaxConcurrentLoads, 2, Math.Clamp(Environment.ProcessorCount, 2, 8));
        Assert.Equal(1, platform.Enumerations); Assert.Equal(1, platform.Notifications);
        Assert.Equal(Enumerable.Range(0, rows.Count + 1), progress.Reports.Select(p => p.Completed));
        Assert.All(progress.Reports, p => Assert.Equal(rows.Count, p.Total));
        Assert.All((await s.Repository.QueryAsync(new())).Rows, r => Assert.True(r.Active));
        Assert.Equal(rows.Count, (await ops.ExitAsync()).Count); Assert.Equal(0, platform.Loads.Values.Sum());
        Assert.Empty(await s.Repository.OperationsAsync());
    }

    [Fact] public async Task BatchNameConflictUsesSelectionOrderAndIncludesCollectionFaces()
    {
        using var s = new Sandbox();
        var rows = await Index(s, FontData.Collection("Shared", "Other"), FontData.Font("Shared", "Version 2"));
        var collection = rows.First(r => r.FullName.StartsWith("Other"));
        var other = rows.First(r => r.FullName.StartsWith("Shared") && r.Hash != collection.Hash);
        var platform = new FakePlatform(s.Root);
        var result = await new FontOperations(s.Repository, platform).RunAsync("activate", [collection, other], default);
        Assert.True(result[0].Success); Assert.False(result[1].Success);
        Assert.Equal(1, platform.Loads.Values.Sum()); Assert.Contains("名称冲突", result[1].Message);
    }

    [Fact] public async Task DuplicateSelectionLoadsOnceAndRepeatedBatchIsIdempotent()
    {
        using var s = new Sandbox(); var rows = await Index(s, FontData.Font("One"));
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        Assert.Single(await ops.RunAsync("activate", [rows[0], rows[0]], default));
        Assert.True((await ops.RunAsync("activate", rows, default)).Single().Success);
        Assert.Equal(1, platform.Loads.Values.Sum());
    }

    [Fact] public async Task LoadFailureRollsBackOnlyFailedFileAndOthersFinish()
    {
        using var s = new Sandbox(); var rows = await Index(s, FontData.Font("One"), FontData.Font("Two"));
        var platform = new FakePlatform(s.Root) { FailLoadHash = rows[0].Hash };
        var result = await new FontOperations(s.Repository, platform).RunAsync("activate", rows, default);
        Assert.False(result[0].Success); Assert.True(result[1].Success);
        Assert.Single(Directory.GetFiles(platform.TemporaryDirectory));
        Assert.Equal(rows[1].Hash, (await s.Repository.OperationsAsync()).Single().Hash);
    }

    [Fact] public async Task CancelStopsDispatchAndWaitsForStartedLoads()
    {
        using var s = new Sandbox();
        var rows = await Index(s, Enumerable.Range(0, 20).Select(i => FontData.Font($"Font{i:D2}")).ToArray());
        using var cancellation = new CancellationTokenSource();
        var platform = new FakePlatform(s.Root) { LoadDelay = 150, Loading = _ => cancellation.Cancel() };
        var ops = new FontOperations(s.Repository, platform); var progress = new Recorder();
        var result = await ops.RunAsync("activate", rows, cancellation.Token, progress);
        Assert.InRange(result.Count, 1, Math.Clamp(Environment.ProcessorCount, 2, 8));
        Assert.Contains(result, r => r.Success);
        Assert.Equal(result.Count, progress.Reports[^1].Completed);
        Assert.Equal(result.Count(r => r.Success), platform.Loads.Values.Sum());
        Assert.Equal(result.Count(r => r.Success), (await s.Repository.OperationsAsync()).Count);
        await ops.ExitAsync(); Assert.Equal(0, platform.Loads.Values.Sum());
        Assert.Empty(Directory.GetFiles(platform.TemporaryDirectory));
    }

    [Fact] public async Task ExitWaitsForEntireActivationBatchBeforeCleaningLoads()
    {
        using var s = new Sandbox(); var rows = await Index(s, FontData.Font("One"));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakePlatform(s.Root)
        {
            Loading = _ => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
        };
        var ops = new FontOperations(s.Repository, platform);
        var activation = ops.RunAsync("activate", rows, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var exit = ops.ExitAsync();
            Assert.False(exit.IsCompleted);
            release.Set();
            Assert.True((await activation).Single().Success);
            Assert.True((await exit).Single().Success);
            Assert.Equal(0, platform.Loads.Values.Sum()); Assert.Empty(await s.Repository.OperationsAsync());
        }
        finally { release.Set(); await activation; }
    }

    [Fact] public async Task EmptyBatchDoesNotEnumerateOrNotifySystem()
    {
        using var s = new Sandbox(); var platform = new FakePlatform(s.Root); var progress = new Recorder();
        Assert.Empty(await new FontOperations(s.Repository, platform).RunAsync("activate", [], default, progress));
        Assert.Equal(0, platform.Enumerations); Assert.Equal(0, platform.Notifications);
        Assert.Equal(new OperationProgress(0, 0), progress.Reports.Single());
    }

    [Fact] public async Task SystemAndExistingTemporaryConflictsRemainProtected()
    {
        using var s = new Sandbox();
        var rows = await Index(s, FontData.Font("Same", "Version 1"), FontData.Font("Same", "Version 2"));
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        Assert.True((await ops.RunAsync("activate", [rows[0]], default)).Single().Success);
        Assert.False((await ops.RunAsync("activate", [rows[1]], default)).Single().Success);
        await ops.ExitAsync();
        platform.External.Add(new("system.ttf", "external", await s.Repository.FacesForFileAsync(rows[0].Hash)));
        Assert.False((await ops.RunAsync("activate", [rows[1]], default)).Single().Success);
        Assert.Equal(0, platform.Loads.Values.Sum());
    }
}
