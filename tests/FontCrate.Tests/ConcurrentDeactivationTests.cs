using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public class ConcurrentDeactivationTests
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
    private static Task<IReadOnlyList<OperationResult>> Stop(FontOperations ops, IReadOnlyList<FaceRow> rows, bool exit,
        IProgress<OperationProgress>? progress = null) => exit ? ops.ExitAsync() : ops.RunAsync("deactivate", rows, default, progress);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnloadsConcurrentlyWithinLimitAndNotifiesOnceAfterWorkersFinish(bool exit)
    {
        using var s = new Sandbox();
        var rows = await Index(s, Enumerable.Range(0, 20).Select(i => FontData.Font($"Font{i:D2}")).ToArray());
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        Assert.All(await ops.RunAsync("activate", rows, default), r => Assert.True(r.Success, r.Message));
        int notifications = platform.Notifications;
        int limit = Math.Clamp(Environment.ProcessorCount, 2, 8), enteredCount = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        platform.Unloading = _ =>
        {
            if (Interlocked.Increment(ref enteredCount) == limit) entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException();
        };
        var progress = new Recorder(); var selected = rows.Reverse().ToArray();
        var stopping = Stop(ops, selected, exit, progress);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(stopping.IsCompleted);
            Assert.Equal(limit, platform.MaxConcurrentUnloads);
            Assert.Equal(limit, platform.UnloadCalls);
            Assert.Equal(notifications, platform.Notifications);
        }
        finally { release.Set(); await stopping; }
        var result = await stopping;
        Assert.All(result, r => Assert.True(r.Success, r.Message));
        Assert.Equal(rows.Count, result.Count);
        Assert.InRange(platform.MaxConcurrentUnloads, 2, limit);
        Assert.Equal(rows.Count, platform.UnloadCalls);
        Assert.Equal(notifications + 1, platform.Notifications);
        if (!exit)
        {
            Assert.Equal(selected.Select(r => r.Hash), result.Select(r => r.Hash));
            Assert.Equal(Enumerable.Range(0, rows.Count + 1), progress.Reports.Select(p => p.Completed));
            Assert.All(progress.Reports, p => Assert.Equal(rows.Count, p.Total));
            Assert.Equal(selected.Select(r => r.FullName).Order(), progress.Reports.Skip(1).Select(p => p.CurrentFont).Order());
        }
        Assert.Equal(0, platform.Loads.Values.Sum());
        Assert.Empty(Directory.GetFiles(platform.TemporaryDirectory));
        Assert.Empty(await s.Repository.OperationsAsync());
        Assert.All((await s.Repository.QueryAsync(new())).Rows, r => Assert.False(r.Active));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnloadFailureRetainsOnlyFailedFileAndCanBeRetried(bool exit)
    {
        using var s = new Sandbox();
        var rows = await Index(s, FontData.Font("One"), FontData.Font("Two"), FontData.Font("Three"));
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        await ops.RunAsync("activate", rows, default);
        platform.FailUnloadHash = rows[0].Hash;
        var result = await Stop(ops, rows, exit);
        Assert.False(result.Single(r => r.Hash == rows[0].Hash).Success);
        Assert.All(result.Where(r => r.Hash != rows[0].Hash), r => Assert.True(r.Success, r.Message));
        Assert.Equal(1, platform.Loads.Values.Sum());
        Assert.Equal(rows[0].Hash, Path.GetFileNameWithoutExtension(Assert.Single(Directory.GetFiles(platform.TemporaryDirectory))));
        var pending = Assert.Single(await s.Repository.OperationsAsync());
        Assert.Equal(rows[0].Hash, pending.Hash); Assert.Equal("pending", pending.Stage); Assert.True(pending.Loaded);
        Assert.Equal(rows[0].Hash, Assert.Single((await s.Repository.QueryAsync(new())).Rows, r => r.Active).Hash);
        Assert.Equal(2, platform.Notifications);
        platform.FailUnloadHash = null;
        Assert.All(await Stop(ops, rows, exit), r => Assert.True(r.Success, r.Message));
        Assert.Equal(0, platform.Loads.Values.Sum()); Assert.Empty(await s.Repository.OperationsAsync());
        Assert.Empty(Directory.GetFiles(platform.TemporaryDirectory)); Assert.Equal(3, platform.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureStillNotifiesAndRetryDoesNotUnloadTwice(bool exit)
    {
        using var s = new Sandbox(); var rows = await Index(s, FontData.Font("One"), FontData.Font("Two"));
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        await ops.RunAsync("activate", rows, default);
        string copy = platform.Loads.Keys.Single(p => Path.GetFileNameWithoutExtension(p) == rows[0].Hash);
        byte[] original = File.ReadAllBytes(copy); File.AppendAllText(copy, "externally modified");
        var result = await Stop(ops, rows, exit);
        Assert.False(result.Single(r => r.Hash == rows[0].Hash).Success);
        Assert.True(result.Single(r => r.Hash == rows[1].Hash).Success);
        Assert.Equal(0, platform.Loads.Values.Sum()); Assert.Equal(2, platform.Notifications);
        Assert.True(File.Exists(copy)); Assert.False(Assert.Single(await s.Repository.OperationsAsync()).Loaded);
        File.WriteAllBytes(copy, original);
        Assert.All(await Stop(ops, rows, exit), r => Assert.True(r.Success, r.Message));
        Assert.Equal(2, platform.UnloadCalls); Assert.Equal(3, platform.Notifications);
        Assert.Empty(await s.Repository.OperationsAsync()); Assert.False(File.Exists(copy));
    }

    [Fact] public async Task CancellationStopsDispatchAndWaitsForStartedCleanupBeforeExit()
    {
        using var s = new Sandbox();
        var rows = await Index(s, Enumerable.Range(0, 20).Select(i => FontData.Font($"Font{i:D2}")).ToArray());
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        await ops.RunAsync("activate", rows, default);
        using var cancellation = new CancellationTokenSource(); using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        platform.Unloading = _ =>
        {
            cancellation.Cancel(); entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException();
        };
        var progress = new Recorder();
        var stopping = ops.RunAsync("deactivate", rows, cancellation.Token, progress);
        Task<IReadOnlyList<OperationResult>>? exiting = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(stopping.IsCompleted); Assert.Equal(1, platform.Notifications);
            exiting = ops.ExitAsync(); Assert.False(exiting.IsCompleted);
        }
        finally
        {
            release.Set(); await stopping;
            if (exiting is not null) await exiting;
        }
        var result = await stopping;
        Assert.InRange(result.Count, 1, Math.Clamp(Environment.ProcessorCount, 2, 8));
        Assert.All(result, r => Assert.True(r.Success, r.Message));
        Assert.Equal(rows.Where(r => result.Any(done => done.Hash == r.Hash)).Select(r => r.Hash), result.Select(r => r.Hash));
        Assert.Equal(Enumerable.Range(0, result.Count + 1), progress.Reports.Select(p => p.Completed));
        Assert.All(progress.Reports, p => Assert.Equal(rows.Count, p.Total));
        Assert.NotNull(exiting); var remaining = await exiting;
        Assert.Equal(rows.Count - result.Count, remaining.Count);
        Assert.All(remaining, r => Assert.True(r.Success, r.Message));
        Assert.Equal(rows.Count, platform.UnloadCalls); Assert.Equal(0, platform.Loads.Values.Sum());
        Assert.Equal(3, platform.Notifications); Assert.Empty(await s.Repository.OperationsAsync());
        Assert.Empty(Directory.GetFiles(platform.TemporaryDirectory));
    }

    [Fact] public async Task CollectionAndDuplicateSelectionUnloadOnceAndRepeatedStopIsANoop()
    {
        using var s = new Sandbox(); var rows = await Index(s, FontData.Collection("First", "Second"));
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        Assert.Single(await ops.RunAsync("activate", rows, default));
        var progress = new Recorder();
        Assert.True(Assert.Single(await ops.RunAsync("deactivate", rows.Concat(rows).ToArray(), default, progress)).Success);
        Assert.Equal(new[] { 0, 1 }, progress.Reports.Select(p => p.Completed));
        Assert.All(progress.Reports, p => Assert.Equal(1, p.Total));
        Assert.All((await s.Repository.QueryAsync(new())).Rows, r => Assert.False(r.Active));
        Assert.True(Assert.Single(await ops.RunAsync("deactivate", rows, default)).Success);
        Assert.Empty(await ops.ExitAsync()); Assert.Equal(1, platform.UnloadCalls); Assert.Equal(2, platform.Notifications);
    }

    [Fact] public async Task EmptyAndInactiveBatchesDoNotNotifyOrEnumerate()
    {
        using var s = new Sandbox(); var rows = await Index(s, FontData.Font("One"));
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform); var progress = new Recorder();
        Assert.Empty(await ops.RunAsync("deactivate", [], default, progress));
        Assert.Equal(new OperationProgress(0, 0), Assert.Single(progress.Reports));
        Assert.Empty(await ops.ExitAsync());
        Assert.True(Assert.Single(await ops.RunAsync("deactivate", rows, default)).Success);
        Assert.Equal(0, platform.UnloadCalls); Assert.Equal(0, platform.Notifications); Assert.Equal(0, platform.Enumerations);
    }

    [Fact] public async Task CancellationBeforeDeactivationLeavesAllFontsActive()
    {
        using var s = new Sandbox(); var rows = await Index(s, FontData.Font("One"));
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        await ops.RunAsync("activate", rows, default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ops.RunAsync("deactivate", rows, cancellation.Token));
        Assert.Equal(0, platform.UnloadCalls); Assert.Equal(1, platform.Notifications);
        Assert.True(Assert.Single((await s.Repository.QueryAsync(new())).Rows).Active);
        Assert.Equal("active", Assert.Single(await s.Repository.OperationsAsync()).Stage);
        Assert.True(Assert.Single(await ops.ExitAsync()).Success);
    }
}
