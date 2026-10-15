using System.IO.Compression;
using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public sealed class FontTransferTests
{
    private static FontTransferService Service(Sandbox s, IFontParser? parser = null)
    {
        parser ??= new SfntParser();
        return new(s.Repository, parser, new Scanner(s.Repository, parser), Path.Combine(s.Root, "ImportedFonts"));
    }

    [Fact]
    public async Task ImportCopiesIndexesDeduplicatesAndPreservesAnnotations()
    {
        using var s = new Sandbox();
        var face = await s.IndexAsync();
        var tag = await s.Repository.CreateNamedItemAsync("标签", [face.Id]);
        await s.Repository.SetFavoriteAsync([face.Id], true);
        string original = Path.Combine(s.Fonts, "test.ttf");
        var service = Service(s);
        var first = Assert.Single(await service.ImportAsync([original], CancellationToken.None));
        Assert.True(first.Success);
        Assert.Equal("test.ttf", Path.GetFileName(first.OutputPath));
        Assert.Equal(face.Hash, Path.GetFileName(Path.GetDirectoryName(first.OutputPath)));
        var duplicate = Assert.Single(await service.ImportAsync([original], CancellationToken.None));
        Assert.True(duplicate.Success);
        Assert.Equal(first.OutputPath, duplicate.OutputPath);
        File.Delete(original);
        var source = (await s.Repository.SourcesAsync()).Single(x => x.Path == s.Fonts);
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, null, CancellationToken.None);
        var imported = Assert.Single((await s.Repository.QueryAsync(new(TagId: tag.Id))).Rows);
        Assert.True(imported.Favorite);
        Assert.Equal(first.OutputPath, imported.Path);
        Assert.Single(await new SfntParser().ParseAsync(imported.Path!, CancellationToken.None));
        Assert.False(imported.Installed);
        Assert.False(imported.Active);
    }

    [Fact]
    public async Task ImportSameNamesDifferentContentsAndInvalidFile()
    {
        using var s = new Sandbox();
        await s.Repository.InitializeAsync();
        string a = Path.Combine(s.Fonts, "same.ttf"), b = Path.Combine(s.Root, "same.ttf"), bad = Path.Combine(s.Root, "bad.otf");
        await File.WriteAllBytesAsync(a, FontData.Font("A"));
        await File.WriteAllBytesAsync(b, FontData.Font("B"));
        await File.WriteAllTextAsync(bad, "invalid");
        var result = await Service(s).ImportAsync([a, b, bad], CancellationToken.None);
        Assert.Equal(2, result.Count(r => r.Success));
        Assert.Single(result, r => !r.Success);
        Assert.Equal(2, (await s.Repository.QueryAsync(new())).Total);
        Assert.Empty(Directory.GetFiles(Path.Combine(s.Root, "ImportedFonts"), "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ExportFallsBackToVerifiedSourceAndNeverOverwritesBatchFiles()
    {
        using var s = new Sandbox();
        var face = await s.IndexAsync();
        string backup = Path.Combine(s.Root, "backup"); Directory.CreateDirectory(backup);
        string original = Path.Combine(s.Fonts, "test.ttf");
        File.Copy(original, Path.Combine(backup, "test.ttf"));
        var backupSource = await s.Repository.AddSourceAsync(backup);
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(backupSource, false, null, CancellationToken.None);
        await File.WriteAllTextAsync(original, "changed");
        string output = Path.Combine(s.Root, "output"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "test.ttf"), "keep");
        var result = Assert.Single(await Service(s).ExportFilesAsync([face, face], output, false, CancellationToken.None));
        Assert.True(result.Success);
        Assert.Equal("test (2).ttf", Path.GetFileName(result.OutputPath));
        Assert.Equal(face.Hash, await FontIdentity.HashAsync(result.OutputPath!, CancellationToken.None));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(output, "test.ttf")));
    }

    [Fact]
    public async Task ZipExportsWholeCollectionsOnceAndResolvesSameNames()
    {
        using var s = new Sandbox();
        await s.Repository.InitializeAsync();
        await File.WriteAllBytesAsync(Path.Combine(s.Fonts, "same.ttc"), FontData.Collection("A", "B"));
        string other = Path.Combine(s.Fonts, "other"); Directory.CreateDirectory(other);
        await File.WriteAllBytesAsync(Path.Combine(other, "same.ttc"), FontData.Collection("C", "D"));
        var source = await s.Repository.AddSourceAsync(s.Fonts);
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, null, CancellationToken.None);
        var faces = (await s.Repository.QueryAsync(new())).Rows;
        var tag = await s.Repository.CreateNamedItemAsync("全体", faces.Select(f => f.Id).ToArray());
        var tagged = (await s.Repository.QueryAsync(new(TagId: tag.Id))).Rows;
        string output = Path.Combine(s.Root, "all.zip");
        var results = await Service(s).ExportZipAsync(tagged, output, CancellationToken.None);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(new[] { "same (2).ttc", "same.ttc" }, zip.Entries.Select(e => e.FullName).Order().ToArray());
        foreach (var entry in zip.Entries)
        {
            string extracted = Path.Combine(s.Root, entry.Name);
            entry.ExtractToFile(extracted);
            Assert.Equal(2, (await new SfntParser().ParseAsync(extracted, CancellationToken.None)).Count);
        }
    }

    [Fact]
    public async Task MissingSourcesProducePartialZipAndAllFailuresPreserveExistingZip()
    {
        using var s = new Sandbox();
        var face = await s.IndexAsync();
        await File.WriteAllBytesAsync(Path.Combine(s.Fonts, "second.ttf"), FontData.Font("Second"));
        var source = (await s.Repository.SourcesAsync()).Single();
        await new Scanner(s.Repository, new SfntParser()).ScanAsync(source, false, null, CancellationToken.None);
        var faces = (await s.Repository.QueryAsync(new())).Rows;
        File.Delete(Path.Combine(s.Fonts, "second.ttf"));
        string output = Path.Combine(s.Root, "partial.zip");
        var service = Service(s);
        var partial = await service.ExportZipAsync(faces, output, CancellationToken.None);
        Assert.Single(partial, r => r.Success);
        Assert.Single(partial, r => !r.Success);
        using (var zip = ZipFile.OpenRead(output)) Assert.Single(zip.Entries);
        byte[] before = await File.ReadAllBytesAsync(output);
        File.Delete(Path.Combine(s.Fonts, "test.ttf"));
        Assert.False(Assert.Single(await service.ExportZipAsync([face], output, CancellationToken.None)).Success);
        Assert.Equal(before, await File.ReadAllBytesAsync(output));
        string empty = Path.Combine(s.Root, "empty.zip");
        await service.ExportZipAsync([], empty, CancellationToken.None);
        Assert.False(File.Exists(empty));
    }

    [Fact]
    public async Task SingleExportReplacesTargetButRejectsSourceOverwrite()
    {
        using var s = new Sandbox();
        var face = await s.IndexAsync();
        string output = Path.Combine(s.Root, "saved.ttf");
        await File.WriteAllTextAsync(output, "old");
        var service = Service(s);
        Assert.True(Assert.Single(await service.ExportFilesAsync([face], output, true, CancellationToken.None)).Success);
        Assert.Equal(face.Hash, await FontIdentity.HashAsync(output, CancellationToken.None));
        Assert.False(Assert.Single(await service.ExportFilesAsync([face], face.Path!, true, CancellationToken.None)).Success);
        Assert.Equal(face.Hash, await FontIdentity.HashAsync(face.Path!, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(s.Root, ".fontcrate-*.tmp"));
    }

    [Fact]
    public async Task CancelZipLeavesNoArchiveOrTemporaryFiles()
    {
        using var s = new Sandbox();
        var face = await s.IndexAsync();
        string output = Path.Combine(s.Root, "cancel.zip");
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Completed > 0) cts.Cancel(); });
        var results = await Service(s).ExportZipAsync([face], output, cts.Token, progress);
        Assert.Empty(results);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(s.Root, ".fontcrate-*.tmp"));
    }

    [Fact]
    public async Task CancelImportStillIndexesCommittedCopies()
    {
        using var s = new Sandbox();
        await s.Repository.InitializeAsync();
        string a = Path.Combine(s.Fonts, "a.ttf"), b = Path.Combine(s.Fonts, "b.ttf");
        await File.WriteAllBytesAsync(a, FontData.Font("A"));
        await File.WriteAllBytesAsync(b, FontData.Font("B"));
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Completed > 0) cts.Cancel(); });
        var results = await Service(s).ImportAsync([a, b], cts.Token, progress);
        Assert.True(Assert.Single(results).Success);
        Assert.Equal("A", Assert.Single((await s.Repository.QueryAsync(new())).Rows).Family);
        Assert.Empty(Directory.GetFiles(Path.Combine(s.Root, "ImportedFonts"), "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CancelDuringCompressionPreservesExistingArchive()
    {
        using var s = new Sandbox();
        var face = await s.IndexAsync();
        string output = Path.Combine(s.Root, "existing.zip");
        await File.WriteAllTextAsync(output, "keep");
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Completed > 1) cts.Cancel(); });
        Assert.Empty(await Service(s).ExportZipAsync([face], output, cts.Token, progress));
        Assert.Equal("keep", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(s.Root, ".fontcrate-*.tmp"));
    }

    [Fact]
    public async Task FailedArchiveCommitCleansStagedFiles()
    {
        using var s = new Sandbox();
        var face = await s.IndexAsync();
        string output = Path.Combine(s.Root, "directory.zip");
        Directory.CreateDirectory(output);
        var error = await Record.ExceptionAsync(() => Service(s).ExportZipAsync([face], output, CancellationToken.None));
        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected failure: {error}");
        Assert.Empty(Directory.GetFiles(s.Root, ".fontcrate-*.tmp"));
        Assert.True(Directory.Exists(output));
    }

    [Fact]
    public async Task CancelDuringImportValidationRemovesUncommittedSnapshot()
    {
        using var s = new Sandbox();
        var face = await s.IndexAsync();
        using var cts = new CancellationTokenSource();
        var service = Service(s, new CancelingParser(cts));
        Assert.Empty(await service.ImportAsync([face.Path!], cts.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(s.Root, "ImportedFonts"), "*", SearchOption.AllDirectories));
        Assert.Single(await s.Repository.SourcesAsync());
    }

    private sealed class CancelingParser(CancellationTokenSource cts) : IFontParser
    {
        public Task<IReadOnlyList<ParsedFace>> ParseAsync(string path, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Expected cancellation");
        }
    }

    [Theory]
    [InlineData("A/B:C*D?", "A_B_C_D_")]
    [InlineData("CON", "_CON")]
    [InlineData(" . ", "字体")]
    public void ExportNamesAreSafeOnWindows(string input, string expected) => Assert.Equal(expected, FontTransferService.SafeFileName(input));

    private sealed class InlineProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    { public void Report(OperationProgress value) => report(value); }
}
