using System.IO.Compression;

namespace FontCrate.Core;

public record FontTransferResult(string Source, bool Success, string Message, string? OutputPath = null);

/// <summary>Transfers original font files without changing installation or annotation state.</summary>
public sealed class FontTransferService(Repository repository, IFontParser parser, Scanner scanner, string importedDirectory)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".ttc", ".otc" };
    private readonly SemaphoreSlim gate = new(1);

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        string safe = new(name.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray());
        safe = safe.Trim().TrimEnd('.', ' ');
        if (safe.Length == 0) return "字体";
        string stem = safe.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) safe = "_" + safe;
        if (safe.Length <= 160) return safe;
        string extension = Path.GetExtension(safe);
        if (extension.Length > 10) extension = "";
        return safe[..(160 - extension.Length)] + extension;
    }

    public async Task<IReadOnlyList<FontTransferResult>> ImportAsync(IReadOnlyList<string> paths, CancellationToken ct,
        IProgress<OperationProgress>? progress = null)
    {
        await gate.WaitAsync(ct);
        try
        {
            var results = new List<FontTransferResult>();
            var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Source? source = null;
            progress?.Report(new(0, files.Length));
            try
            {
                foreach (string path in files)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        if (!Extensions.Contains(Path.GetExtension(path))) throw new InvalidDataException("仅支持 TTF、OTF、TTC、OTC 字体文件");
                        // Parse a private snapshot, so validation and the committed copy refer to the same bytes.
                        Directory.CreateDirectory(importedDirectory);
                        string temporary = Path.Combine(importedDirectory, Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            await CopyAsync(path, temporary, ct);
                            string hash = await FontIdentity.HashAsync(temporary, ct);
                            // DirectWrite needs the original font extension when opening the snapshot.
                            string snapshot = temporary + Path.GetExtension(path).ToLowerInvariant();
                            File.Move(temporary, snapshot); temporary = snapshot;
                            if ((await parser.ParseAsync(temporary, ct)).Count == 0) throw new InvalidDataException("文件不包含可用字体");
                            if (await FontIdentity.HashAsync(temporary, ct) != hash) throw new IOException("解析期间内容发生变化");
                            string directory = Path.Combine(importedDirectory, hash);
                            Directory.CreateDirectory(directory);
                            string? existing = Directory.EnumerateFiles(directory).FirstOrDefault();
                            string target = existing ?? Path.Combine(directory, SafeFileName(Path.GetFileName(path)));
                            if (existing is not null && await FontIdentity.HashAsync(existing, ct) != hash)
                                throw new IOException("已导入副本内容发生变化，请先核对来源");
                            ct.ThrowIfCancellationRequested();
                            source ??= await repository.AddSourceAsync(importedDirectory);
                            if (existing is null) File.Move(temporary, target);
                            results.Add(new(path, true, existing is null ? "已导入" : "已存在，未重复复制", target));
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex) when (ex is not OperationCanceledException) { results.Add(new(path, false, ex.Message)); }
                    progress?.Report(new(results.Count, files.Length, Path.GetFileName(path)));
                }
            }
            finally
            {
                // Reconcile committed files even when cancellation interrupts the remaining batch.
                if (source is not null)
                {
                    progress?.Report(new(results.Count, files.Length, "正在将已导入字体纳入字体库…"));
                    var scan = await scanner.ScanAsync(source, false, null, CancellationToken.None);
                    if (scan.Errors > 0 || !scan.Complete) results.Add(new(importedDirectory, false, "副本已保存，但部分字体索引失败；请查看扫描错误并重新扫描"));
                }
            }
            return results;
        }
        finally { gate.Release(); }
    }

    public Task<IReadOnlyList<FontTransferResult>> ExportFilesAsync(IReadOnlyList<FaceRow> faces, string destination,
        bool singleFile, CancellationToken ct, IProgress<OperationProgress>? progress = null) =>
        ExportAsync(faces, destination, singleFile, false, ct, progress);

    public Task<IReadOnlyList<FontTransferResult>> ExportZipAsync(IReadOnlyList<FaceRow> faces, string destination,
        CancellationToken ct, IProgress<OperationProgress>? progress = null) =>
        ExportAsync(faces, destination, false, true, ct, progress);

    private async Task<IReadOnlyList<FontTransferResult>> ExportAsync(IReadOnlyList<FaceRow> faces, string destination,
        bool singleFile, bool zip, CancellationToken ct, IProgress<OperationProgress>? progress)
    {
        await gate.WaitAsync(ct);
        string? archiveTemp = null;
        try
        {
            var files = faces.DistinctBy(f => f.Hash).ToArray();
            if (singleFile && files.Length != 1) throw new ArgumentException("单文件导出需要一个字体文件");
            destination = Path.GetFullPath(destination);
            if (!zip && !singleFile) Directory.CreateDirectory(destination);
            string directory = zip || singleFile ? Path.GetDirectoryName(destination)! : destination;
            var results = new List<FontTransferResult>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var staged = new List<(FaceRow Face, string Temp, string Source)>();
            progress?.Report(new(0, zip ? files.Length * 2 : files.Length));
            try
            {
                foreach (var face in files)
                {
                    if (ct.IsCancellationRequested) break;
                    string temp = Path.Combine(directory, ".fontcrate-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        string source = await StageSourceAsync(face, temp, ct);
                        if ((zip || singleFile) && string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("不能覆盖字体来源文件");
                        if (zip) staged.Add((face, temp, source));
                        else
                        {
                            string target = singleFile ? destination : UniqueTarget(destination, Path.GetFileName(source), names);
                            ct.ThrowIfCancellationRequested();
                            File.Move(temp, target, singleFile);
                            results.Add(new(face.FullName, true, "已导出", target));
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex) when (ex is not OperationCanceledException) { results.Add(new(face.FullName, false, ex.Message)); }
                    finally { if (!staged.Any(s => s.Temp == temp) && File.Exists(temp)) File.Delete(temp); }
                    progress?.Report(new(results.Count + staged.Count, zip ? files.Length * 2 : files.Length, face.FullName));
                }
                if (zip && staged.Count > 0 && !ct.IsCancellationRequested)
                {
                    archiveTemp = Path.Combine(directory, ".fontcrate-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (var archive = ZipFile.Open(archiveTemp, ZipArchiveMode.Create))
                    {
                        int written = 0;
                        foreach (var item in staged)
                        {
                            ct.ThrowIfCancellationRequested();
                            string name = UniqueTarget(null, Path.GetFileName(item.Source), names);
                            var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                            await using var output = entry.Open();
                            await using var input = File.OpenRead(item.Temp);
                            await input.CopyToAsync(output, ct);
                            progress?.Report(new(files.Length + ++written, files.Length * 2, "正在打包：" + item.Face.FullName));
                        }
                    }
                    ct.ThrowIfCancellationRequested();
                    File.Move(archiveTemp, destination, true);
                    results.AddRange(staged.Select(s => new FontTransferResult(s.Face.FullName, true, "已打包导出", destination)));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally
            {
                foreach (var item in staged) if (File.Exists(item.Temp)) File.Delete(item.Temp);
            }
            return results;
        }
        finally
        {
            try { if (archiveTemp is not null && File.Exists(archiveTemp)) File.Delete(archiveTemp); }
            finally { gate.Release(); }
        }
    }

    private async Task<string> StageSourceAsync(FaceRow face, string temp, CancellationToken ct)
    {
        foreach (var location in await repository.LocationsAsync(face.Hash))
        {
            if (!location.Available) continue;
            try
            {
                await CopyAsync(location.Path, temp, ct);
                if (await FontIdentity.HashAsync(temp, ct) == face.Hash) return location.Path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (File.Exists(temp)) File.Delete(temp);
        }
        throw new IOException("来源不可用或内容已变化，请重新扫描");
    }

    private static string UniqueTarget(string? directory, string name, HashSet<string> names)
    {
        name = SafeFileName(name);
        string candidate = name;
        for (int i = 2; names.Contains(candidate) || directory is not null &&
            (File.Exists(Path.Combine(directory, candidate)) || Directory.Exists(Path.Combine(directory, candidate))); i++)
            candidate = Path.GetFileNameWithoutExtension(name) + $" ({i})" + Path.GetExtension(name);
        names.Add(candidate);
        return directory is null ? candidate : Path.Combine(directory, candidate);
    }

    private static async Task CopyAsync(string source, string target, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await input.CopyToAsync(output, ct);
        await output.FlushAsync(ct);
    }
}
