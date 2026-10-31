using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FontCrate.Core;

public static class PerformanceLog
{
    private static readonly object Gate = new();
    private static readonly AsyncLocal<Context?> Current = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private sealed record Context(string BatchId, string Action, string? FontHash = null, string? FontPath = null, string? ParentBatchId = null);
    private static string? directory, explicitPath;
    private static string session = Guid.NewGuid().ToString("N");
    private static TimeProvider clock = TimeProvider.System;
    private static DateOnly? cleanedDate;

    // Preserve explicit-path configuration for existing callers and diagnostic tools.
    public static string? Path
    {
        get { lock (Gate) return ResolvePath(clock.GetLocalNow()); }
        set { lock (Gate) { directory = null; explicitPath = value; cleanedDate = null; } }
    }
    public static void Initialize(string logDirectory, TimeProvider? timeProvider = null)
    {
        lock (Gate)
        {
            directory = logDirectory; explicitPath = null; cleanedDate = null;
            clock = timeProvider ?? TimeProvider.System;
            session = Guid.NewGuid().ToString("N");
            try { PrepareDirectory(clock.GetLocalNow()); }
            catch (Exception e) when (IsLoggingFailure(e)) { }
        }
    }
    private static string? ResolvePath(DateTimeOffset now) => directory is null ? explicitPath :
        System.IO.Path.Combine(directory, $"fontcrate-{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.jsonl");
    private static bool IsLoggingFailure(Exception e) => e is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or ArgumentException or NotSupportedException or JsonException;
    private static void PrepareDirectory(DateTimeOffset now)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        var today = DateOnly.FromDateTime(now.DateTime);
        if (cleanedDate == today) return;
        foreach (string file in Directory.EnumerateFiles(directory, "fontcrate-*.jsonl"))
        {
            string name = System.IO.Path.GetFileName(file);
            if (name.Length != 26 || !name.StartsWith("fontcrate-", StringComparison.Ordinal) ||
                !name.EndsWith(".jsonl", StringComparison.Ordinal) ||
                !DateOnly.TryParseExact(name.AsSpan(10, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) || date >= today.AddDays(-13)) continue;
            try { File.Delete(file); }
            catch (Exception e) when (IsLoggingFailure(e)) { }
        }
        cleanedDate = today;
    }
    public static void Write(string operation, double milliseconds, string? detail = null) =>
        TryWrite(operation, milliseconds, detail);

    /// <returns>The file that received this entry, or null if writing failed.</returns>
    public static string? TryWrite(string operation, double milliseconds = 0, string? detail = null,
        string? level = null, object? data = null, Exception? exception = null, string? stage = null, string? outcome = null)
    {
        lock (Gate)
        {
            try
            {
                var now = clock.GetLocalNow();
                string? path = ResolvePath(now);
                if (path is null) return null;
                PrepareDirectory(now);
                var context = Current.Value;
                string line = JsonSerializer.Serialize(new
                {
                    time = now, session, process = Environment.ProcessId, thread = Environment.CurrentManagedThreadId,
                    level = level ?? (exception is not null || operation.Contains("error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Debug"),
                    operation, milliseconds, detail, batchId = context?.BatchId, parentBatchId = context?.ParentBatchId, action = context?.Action,
                    fontHash = context?.FontHash, path = context?.FontPath, stage, outcome, data,
                    exception = exception?.ToString()
                });
                // Opening and closing per entry flushes errors immediately and leaves no buffered tail on shutdown.
                File.AppendAllText(path, line + Environment.NewLine, Utf8);
                return path;
            }
            catch (Exception e) when (IsLoggingFailure(e)) { return null; }
        }
    }
    public static void Error(string operation, Exception exception, object? data = null) =>
        TryWrite(operation, level: "Error", exception: exception, data: data, outcome: "failed");

    private sealed class Scope(Context? previous) : IDisposable
    { public void Dispose() => Current.Value = previous; }
    private static IDisposable Push(Context context)
    {
        var previous = Current.Value; Current.Value = context; return new Scope(previous);
    }
    public static async Task<T> BatchAsync<T>(string action, Func<Task<T>> work, object? data = null,
        Func<T, object?>? summarize = null, CancellationToken ct = default)
    {
        using var scope = Push(new(Guid.NewGuid().ToString("N"), action, ParentBatchId: Current.Value?.BatchId));
        return await StepAsync(action + "-batch", work, data, summarize, ct);
    }
    public static async Task<T> FontAsync<T>(string action, string hash, string? path, Func<Task<T>> work)
    {
        var parent = Current.Value ?? new(Guid.NewGuid().ToString("N"), action);
        using var scope = Push(parent with { FontHash = hash, FontPath = path });
        return await StepAsync("font-" + action, work);
    }
    public static IDisposable FontScope(string hash, string? path)
    {
        var parent = Current.Value ?? new(Guid.NewGuid().ToString("N"), "font");
        return Push(parent with { FontHash = hash, FontPath = path });
    }
    public static async Task<T> StepAsync<T>(string stage, Func<Task<T>> work, object? data = null,
        Func<T, object?>? summarize = null, CancellationToken ct = default)
    {
        var elapsed = Stopwatch.StartNew();
        TryWrite(stage + "-start", data: data, stage: stage, outcome: "started");
        try
        {
            T result = await work();
            TryWrite(stage + "-complete", elapsed.Elapsed.TotalMilliseconds, level: "Info", stage: stage,
                outcome: ct.IsCancellationRequested ? "canceled" : "completed",
                data: CompletionData(result, data, summarize));
            return result;
        }
        catch (OperationCanceledException e)
        {
            TryWrite(stage + "-canceled", elapsed.Elapsed.TotalMilliseconds, level: "Info", stage: stage,
                outcome: "canceled", data: data, exception: e); throw;
        }
        catch (Exception e)
        {
            TryWrite(stage + "-error", elapsed.Elapsed.TotalMilliseconds, level: "Error", stage: stage,
                outcome: "failed", data: data, exception: e); throw;
        }
    }
    public static Task StepAsync(string stage, Func<Task> work, object? data = null) =>
        StepAsync(stage, async () => { await work(); return true; }, data);
    private static object? CompletionData<T>(T result, object? data, Func<T, object?>? summarize)
    {
        // Diagnostic callbacks must not turn a successful business operation into a failure.
        try
        {
            if (summarize is not null) return summarize(result);
            return new { context = data, result = result is string or bool or int or long ? (object?)result : null };
        }
        catch (Exception e) { Error("log-summary-error", e); return data; }
    }
    public static T Step<T>(string stage, Func<T> work, object? data = null)
    {
        var elapsed = Stopwatch.StartNew();
        TryWrite(stage + "-start", data: data, stage: stage, outcome: "started");
        try
        {
            T result = work();
            TryWrite(stage + "-complete", elapsed.Elapsed.TotalMilliseconds, stage: stage, outcome: "completed",
                data: new { context = data, result });
            return result;
        }
        catch (Exception e)
        {
            TryWrite(stage + "-error", elapsed.Elapsed.TotalMilliseconds, level: "Error", stage: stage,
                outcome: e is OperationCanceledException ? "canceled" : "failed", data: data, exception: e); throw;
        }
    }
    public static void Step(string stage, Action work, object? data = null) =>
        Step(stage, () => { work(); return true; }, data);
}
