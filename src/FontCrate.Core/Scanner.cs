using System.Threading.Channels;

namespace FontCrate.Core;

public sealed class Scanner(Repository repository, IFontParser parser)
{
    private readonly SemaphoreSlim scanGate = new(1);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".ttc", ".otc" };
    public async Task<ScanResult> ScanAsync(Source source, bool verify, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        await scanGate.WaitAsync(ct);
        try { return await ScanCore(source, verify, progress, ct); }
        finally { scanGate.Release(); }
    }
    private async Task<ScanResult> ScanCore(Source source, bool verify, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        string scan = Guid.NewGuid().ToString("N");
        int discovered = 0, parsed = 0, unchanged = 0, errors = 0, processed = 0;
        int enumerationComplete = 0;
        var reportGate = new object();
        long lastReport = 0;
        bool complete = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;
        var paths = Channel.CreateBounded<string>(new BoundedChannelOptions(128) { SingleWriter = true });
        var results = Channel.CreateBounded<IndexedFile>(new BoundedChannelOptions(128) { SingleReader = true });
        void Report(string phase)
        {
            lock (reportGate)
            {
                long now = Environment.TickCount64;
                if (phase == "扫描中" && now - lastReport < 200) return;
                lastReport = now;
                progress?.Report(new(Volatile.Read(ref discovered), Volatile.Read(ref parsed), Volatile.Read(ref unchanged),
                    Volatile.Read(ref errors), phase, Volatile.Read(ref processed), Volatile.Read(ref enumerationComplete) != 0));
            }
        }

        async Task Guard(Func<Task> action) { try { await action(); } catch { await linked.CancelAsync(); throw; } }

        Report("枚举目录");
        try
        {
            // Enumerate the root before altering availability: Directory.Exists hides permission failures.
            using var rootCheck = Directory.EnumerateFileSystemEntries(source.Path).GetEnumerator(); rootCheck.MoveNext();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await repository.SetSourceAvailabilityAsync(source.Id, false);
            await repository.RecordErrorAsync(source.Id, source.Path, e.Message);
            errors = 1; enumerationComplete = 1;
            Report("来源不可用");
            return new(false, 0, 0, 0, 1);
        }
        await repository.SetSourceAvailabilityAsync(source.Id, true);
        var producer = Task.Run(() => Guard(async () =>
        {
            try
            {
                var dirs = new Stack<string>(); dirs.Push(source.Path);
                while (dirs.TryPop(out var dir))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                        {
                            token.ThrowIfCancellationRequested();
                            var attrs = File.GetAttributes(entry);
                            if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                            if ((attrs & FileAttributes.Directory) != 0) dirs.Push(entry);
                            else if (Extensions.Contains(Path.GetExtension(entry)))
                            { Interlocked.Increment(ref discovered); await paths.Writer.WriteAsync(entry, token); }
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { complete = false; Interlocked.Increment(ref errors); await repository.RecordErrorAsync(source.Id, dir, e.Message); }
                }
                Volatile.Write(ref enumerationComplete, 1);
                Report("目录枚举完成");
            }
            finally { paths.Writer.TryComplete(); }
        }), CancellationToken.None);

        async Task Worker()
        {
            await foreach (string path in paths.Reader.ReadAllAsync(token))
            {
                IndexedFile item;
                long size = 0, modified = 0;
                try
                {
                    var info = new FileInfo(path); size = info.Length; modified = info.LastWriteTimeUtc.Ticks;
                    var known = await repository.FindLocationAsync(source.Id, path);
                    if (!verify && known?.Hash is not null && known.Size == size && known.ModifiedTicks == modified)
                    { item = new(path, size, modified, known.Hash, null); Interlocked.Increment(ref unchanged); }
                    else
                    {
                        string hash = await FontIdentity.HashAsync(path, token);
                        IReadOnlyList<ParsedFace>? faces = null;
                        if (!await repository.HasFileAsync(hash))
                        {
                            faces = await parser.ParseAsync(path, token);
                            if (faces.Count == 0) throw new InvalidDataException("文件不包含可用的字体 face");
                            if (await FontIdentity.HashAsync(path, token) != hash) throw new IOException("解析期间文件内容发生变化，请重扫");
                            Interlocked.Increment(ref parsed);
                        }
                        else Interlocked.Increment(ref unchanged);
                        info.Refresh();
                        if (info.Length != size || info.LastWriteTimeUtc.Ticks != modified) throw new IOException("扫描期间文件发生变化，请重扫");
                        item = new(path, size, modified, hash, faces);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                { Interlocked.Increment(ref errors); item = new(path, size, modified, null, null, e.Message); }
                await results.Writer.WriteAsync(item, token);
            }
        }
        var workers = Enumerable.Range(0, 2).Select(_ => Task.Run(() => Guard(Worker), CancellationToken.None)).ToArray();
        var closer = Task.Run(async () => { try { await Task.WhenAll(workers); } finally { results.Writer.TryComplete(); } });
        var writer = Task.Run(() => Guard(async () =>
        {
            var batch = new List<IndexedFile>(100);
            await foreach (var item in results.Reader.ReadAllAsync(token))
            {
                batch.Add(item);
                if (batch.Count >= 100)
                {
                    await repository.SaveBatchAsync(source.Id, scan, batch);
                    Interlocked.Add(ref processed, batch.Count); batch.Clear(); Report("扫描中");
                }
            }
            if (batch.Count > 0)
            {
                await repository.SaveBatchAsync(source.Id, scan, batch);
                Interlocked.Add(ref processed, batch.Count); Report("扫描中");
            }
        }), CancellationToken.None);
        try { await Task.WhenAll(producer, closer, writer); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { complete = false; await repository.FinishScanAsync(source.Id, scan, false); Report("已取消"); throw; }
        await repository.FinishScanAsync(source.Id, scan, complete);
        Report(complete ? "完成" : "部分目录不可读，保留既有位置");
        return new(complete, discovered, parsed, unchanged, errors);
    }
}
