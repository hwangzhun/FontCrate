using System.Text.Json;
using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

// PerformanceLog is process-wide; these tests must not change its destination while other tests run.
[CollectionDefinition("Diagnostic logging", DisableParallelization = true)]
public sealed class DiagnosticLoggingCollection { }

[Collection("Diagnostic logging")]
public sealed class PerformanceLogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fontcrate-日志-" + Guid.NewGuid().ToString("N"));
    private string Logs => Path.Combine(root, "logs");
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow = new(2026, 10, 31, 15, 59, 58, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test UTC+8", TimeSpan.FromHours(8), "Test", "Test");
    }
    public PerformanceLogTests() { PerformanceLog.Path = null; Directory.CreateDirectory(root); }
    public void Dispose() { PerformanceLog.Path = null; Directory.Delete(root, true); }
    private JsonElement[] Read() => Directory.GetFiles(Logs, "fontcrate-*.jsonl").SelectMany(File.ReadAllLines)
        .Select(line => { using var doc = JsonDocument.Parse(line); return doc.RootElement.Clone(); }).ToArray();
    private static string? Event(JsonElement entry) => entry.GetProperty("operation").GetString();

    [Fact] public void CreatesDirectoryAndAppendsAcrossSessionsWithoutBom()
    {
        var clock = new Clock(); PerformanceLog.Initialize(Logs, clock);
        string expected = Path.Combine(Logs, "fontcrate-2026-10-31.jsonl");
        Assert.Equal(expected, PerformanceLog.TryWrite("first", detail: "中文字体\n多行内容"));
        PerformanceLog.Initialize(Logs, clock); PerformanceLog.Write("second", 12.5);
        var entries = Read(); Assert.Equal(2, entries.Length);
        Assert.NotEqual(entries[0].GetProperty("session").GetString(), entries[1].GetProperty("session").GetString());
        Assert.Equal("中文字体\n多行内容", entries[0].GetProperty("detail").GetString());
        Assert.EndsWith("+08:00", entries[0].GetProperty("time").GetString());
        Assert.Equal(12.5, entries[1].GetProperty("milliseconds").GetDouble());
        Assert.True(entries[0].GetProperty("process").GetInt32() > 0);
        Assert.True(entries[0].GetProperty("thread").GetInt32() > 0);
        Assert.Equal((byte)'{', File.ReadAllBytes(expected)[0]);
        Assert.Single(Directory.GetFiles(Logs));
    }

    [Fact] public void RetentionKeepsFourteenLocalDatesAndIgnoresOtherFiles()
    {
        Directory.CreateDirectory(Logs);
        string[] keep = ["fontcrate-2026-10-18.jsonl", "fontcrate-2026-10-31.jsonl", "fontcrate-2026-11-01.jsonl",
            "fontcrate-2026-10-01.jsonl.1", "other-2026-10-01.jsonl", "fontcrate-2026-02-30.jsonl",
            "fontcrate-2026-10-01-extra.jsonl", "fontcrate-2026-10-1.jsonl"];
        foreach (string name in keep.Concat(["fontcrate-2026-10-17.jsonl", "fontcrate-2025-01-01.jsonl"]))
            File.WriteAllText(Path.Combine(Logs, name), "");
        PerformanceLog.Initialize(Logs, new Clock());
        Assert.All(keep, name => Assert.True(File.Exists(Path.Combine(Logs, name)), name));
        Assert.False(File.Exists(Path.Combine(Logs, "fontcrate-2026-10-17.jsonl")));
        Assert.False(File.Exists(Path.Combine(Logs, "fontcrate-2025-01-01.jsonl")));
    }

    [Fact] public void MidnightSwitchesFileAndRunsRetentionAgain()
    {
        var clock = new Clock(); PerformanceLog.Initialize(Logs, clock);
        string oldest = Path.Combine(Logs, "fontcrate-2026-10-18.jsonl"); File.WriteAllText(oldest, "");
        PerformanceLog.Write("before-midnight", 0);
        Assert.True(File.Exists(oldest));
        clock.UtcNow = clock.UtcNow.AddSeconds(4);
        PerformanceLog.Write("after-midnight", 0);
        Assert.False(File.Exists(oldest));
        Assert.Equal("before-midnight", Event(Read().Single(e => e.GetProperty("time").GetDateTimeOffset().Day == 31)));
        Assert.EndsWith("fontcrate-2026-11-01.jsonl", PerformanceLog.Path);
        Assert.Equal(2, Directory.GetFiles(Logs).Length);
    }

    [Fact] public async Task ConcurrentBatchesKeepFontContextAndProduceValidJsonLines()
    {
        PerformanceLog.Initialize(Logs, new Clock());
        await Task.WhenAll(Enumerable.Range(0, 4).Select(batch => PerformanceLog.BatchAsync($"batch-{batch}", async () =>
        {
            await Task.WhenAll(Enumerable.Range(0, 16).Select(font => Task.Run(() =>
                PerformanceLog.FontAsync("test", $"hash-{batch}-{font}", $"font-{batch}-{font}.ttf", async () =>
                {
                    await Task.Yield();
                    PerformanceLog.Write("inside-font", 1);
                    return true;
                }))));
            return true;
        })));
        var entries = Read();
        var batches = entries.Where(e => Event(e)!.EndsWith("-batch-start", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, batches.Length);
        Assert.Equal(4, batches.Select(e => e.GetProperty("batchId").GetString()).Distinct().Count());
        var fonts = entries.Where(e => Event(e) == "inside-font").ToArray(); Assert.Equal(64, fonts.Length);
        foreach (var entry in fonts)
        {
            string hash = entry.GetProperty("fontHash").GetString()!;
            string action = entry.GetProperty("action").GetString()!;
            Assert.StartsWith("hash-" + action[6..] + "-", hash);
            Assert.Equal(hash.Replace("hash-", "font-") + ".ttf", entry.GetProperty("path").GetString());
            Assert.Equal(batches.Single(e => e.GetProperty("action").GetString() == action).GetProperty("batchId").GetString(), entry.GetProperty("batchId").GetString());
        }
        PerformanceLog.Write("outside-batch", 0);
        Assert.Equal(JsonValueKind.Null, Read()[^1].GetProperty("batchId").ValueKind);
    }

    [Fact] public async Task FailedUnloadAndRetryRetainFullExceptionAndBatchSummary()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        PerformanceLog.Initialize(Logs, new Clock());
        await ops.RunAsync("activate", [face], default);
        platform.FailUnload = true;
        Assert.False(Assert.Single(await ops.RunAsync("deactivate", [face], default)).Success);
        platform.FailUnload = false;
        Assert.True(Assert.Single(await ops.RunAsync("deactivate", [face], default)).Success);
        var entries = Read();
        var failure = Assert.Single(entries, e => Event(e) == "font-deactivate-error");
        Assert.Equal(face.Hash, failure.GetProperty("fontHash").GetString());
        Assert.Equal("Error", failure.GetProperty("level").GetString());
        Assert.Contains("IOException", failure.GetProperty("exception").GetString());
        Assert.Contains("DeactivateCore", failure.GetProperty("exception").GetString());
        var summaries = entries.Where(e => Event(e) == "deactivate-batch-complete").ToArray();
        Assert.Equal(2, summaries.Length);
        Assert.Equal(1, summaries[0].GetProperty("data").GetProperty("failed").GetInt32());
        Assert.Equal(1, summaries[1].GetProperty("data").GetProperty("succeeded").GetInt32());
        Assert.NotEqual(summaries[0].GetProperty("batchId").GetString(), summaries[1].GetProperty("batchId").GetString());
        Assert.Contains(entries, e => Event(e) == "journal-update-start" && e.GetProperty("data").GetProperty("stage").GetString() == "pending");
        Assert.Equal(2, entries.Count(e => Event(e) == "windows-unload-start"));
        Assert.Contains(entries, e => Event(e) == "font-cleanup-retry" && e.GetProperty("data").GetProperty("attempt").GetInt32() == 2);
        Assert.Contains(entries, e => Event(e) == "copy-validation-complete");
        Assert.Contains(entries, e => Event(e) == "copy-delete-complete");
        Assert.Contains(entries, e => Event(e) == "font-change-notify-complete");
    }

    [Fact] public async Task CleanupFailureIsLoggedAndRetrySkipsSecondNativeUnload()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        await ops.RunAsync("activate", [face], default);
        string copy = platform.Loads.Keys.Single(); byte[] original = File.ReadAllBytes(copy);
        File.AppendAllText(copy, "modified"); PerformanceLog.Initialize(Logs, new Clock());
        Assert.False(Assert.Single(await ops.ExitAsync()).Success);
        File.WriteAllBytes(copy, original);
        Assert.True(Assert.Single(await ops.ExitAsync()).Success);
        var entries = Read();
        Assert.Contains(entries, e => Event(e) == "copy-cleanup-error" && e.GetProperty("exception").GetString()!.Contains("拒绝删除"));
        Assert.Contains(entries, e => Event(e) == "font-cleanup-retry");
        Assert.Single(entries, e => Event(e) == "windows-unload-start");
        Assert.Equal(2, entries.Count(e => Event(e) == "font-change-notify-complete"));
    }

    [Fact] public async Task CancellationIsRecordedSeparatelyAndStartedCleanupCompletes()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        for (int i = 0; i < 12; i++) File.WriteAllBytes(Path.Combine(s.Fonts, $"font{i}.ttf"), FontData.Font($"Font{i}"));
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(await s.Repository.AddSourceAsync(s.Fonts), false, null, default);
        var rows = (await s.Repository.QueryAsync(new())).Rows;
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        await ops.RunAsync("activate", rows, default);
        using var cancellation = new CancellationTokenSource(); platform.Unloading = _ => cancellation.Cancel();
        PerformanceLog.Initialize(Logs, new Clock());
        var result = await ops.RunAsync("deactivate", rows, cancellation.Token);
        Assert.InRange(result.Count, 1, Math.Clamp(Environment.ProcessorCount, 2, 8));
        Assert.All(result, r => Assert.True(r.Success));
        var complete = Assert.Single(Read(), e => Event(e) == "deactivate-batch-complete");
        Assert.Equal("canceled", complete.GetProperty("outcome").GetString());
        Assert.Equal(rows.Count - result.Count, complete.GetProperty("data").GetProperty("remaining").GetInt32());
        Assert.Equal(0, complete.GetProperty("data").GetProperty("failed").GetInt32());
        await ops.ExitAsync();
    }

    [Fact] public async Task UnwritableLogDirectoryDoesNotPreventFontOperations()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        File.WriteAllText(Logs, "file blocking log directory creation");
        PerformanceLog.Initialize(Logs, new Clock());
        Assert.Null(PerformanceLog.TryWrite("cannot-write"));
        var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
        Assert.True(Assert.Single(await ops.RunAsync("activate", [face], default)).Success);
        Assert.True(Assert.Single(await ops.ExitAsync()).Success);
        Assert.Equal(0, platform.Loads.Values.Sum()); Assert.Empty(await s.Repository.OperationsAsync());
        Assert.Equal("file blocking log directory creation", File.ReadAllText(Logs));
    }

    [Fact] public async Task LockedLogFileDoesNotPreventFontOperationsAndWritingRecovers()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        PerformanceLog.Initialize(Logs, new Clock());
        string path = PerformanceLog.Path!;
        using (var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(PerformanceLog.TryWrite("locked"));
            var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
            Assert.True(Assert.Single(await ops.RunAsync("activate", [face], default)).Success);
            Assert.True(Assert.Single(await ops.ExitAsync()).Success);
        }
        Assert.Equal(path, PerformanceLog.TryWrite("writable-again"));
        Assert.Equal("writable-again", Event(Assert.Single(Read())));
    }

    [Fact] public async Task PermissionDeniedDoesNotPreventFontOperations()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        using var s = new Sandbox(); var face = await s.IndexAsync();
        Directory.CreateDirectory(Logs);
        File.SetUnixFileMode(Logs, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            PerformanceLog.Initialize(Logs, new Clock());
            Assert.Null(PerformanceLog.TryWrite("permission-denied"));
            var platform = new FakePlatform(s.Root); var ops = new FontOperations(s.Repository, platform);
            Assert.True(Assert.Single(await ops.RunAsync("activate", [face], default)).Success);
            Assert.True(Assert.Single(await ops.ExitAsync()).Success);
            Assert.Empty(Directory.GetFiles(Logs)); Assert.Empty(await s.Repository.OperationsAsync());
        }
        finally { File.SetUnixFileMode(Logs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact] public async Task DiagnosticMetadataFailureDoesNotChangeBusinessResult()
    {
        PerformanceLog.Initialize(Logs, new Clock());
        Assert.Null(PerformanceLog.TryWrite("unsupported-data", data: typeof(PerformanceLogTests)));
        int result = await PerformanceLog.StepAsync("test-summary", () => Task.FromResult(42),
            summarize: _ => throw new InvalidOperationException("diagnostic failure"));
        Assert.Equal(42, result);
        Assert.Contains(Read(), entry => Event(entry) == "log-summary-error" &&
            entry.GetProperty("exception").GetString()!.Contains("diagnostic failure"));
    }

    [Fact] public async Task ScanImportExportAndRecoveryWriteDetailedEvents()
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        PerformanceLog.Initialize(Logs, new Clock());
        var scanner = new Scanner(s.Repository, new SfntParser());
        File.WriteAllBytes(Path.Combine(s.Fonts, "new.ttf"), FontData.Font("New Font"));
        var source = await s.Repository.AddSourceAsync(s.Fonts);
        await scanner.ScanAsync(source, true, null, default);
        var transfer = new FontTransferService(s.Repository, new SfntParser(), scanner, Path.Combine(s.Root, "imported"));
        Assert.All(await transfer.ImportAsync([face.Path!], default), r => Assert.True(r.Success));
        Assert.All(await transfer.ExportZipAsync([face], Path.Combine(s.Root, "export.zip"), default), r => Assert.True(r.Success));
        var platform = new FakePlatform(s.Root);
        await new FontOperations(s.Repository, platform).RunAsync("activate", [face], default);
        Assert.True(Assert.Single(await new FontOperations(s.Repository, platform).RecoverAsync(default)).Success);
        string[] expected = ["scan-file-start", "scan-file-complete", "scan-parse-complete", "scan-save-complete",
            "import-file-complete", "import-batch-complete", "zip-entry-complete", "zip-commit-complete",
            "export-zip-batch-complete", "recovery-record-start", "recovery-record-complete", "recover-batch-complete"];
        var entries = Read();
        Assert.All(expected, name => Assert.Contains(entries, e => Event(e) == name));
        Assert.Contains(entries, e => Event(e) == "scan-batch-start" && e.GetProperty("parentBatchId").ValueKind == JsonValueKind.String);
    }
}
