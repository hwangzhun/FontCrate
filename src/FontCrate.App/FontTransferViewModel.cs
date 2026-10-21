using FontCrate.Core;

namespace FontCrate.App;

public partial class MainViewModel
{
    private readonly FontTransferService? transfers;
    private const string FontFileFilter = "字体文件 (*.ttf;*.otf;*.ttc;*.otc)|*.ttf;*.otf;*.ttc;*.otc";

    public Task ImportFontsAsync()
    {
        if (!CanManage || transfers is null) return Task.CompletedTask;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入字体", Filter = FontFileFilter, Multiselect = true };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        string[] paths = dialog.FileNames;
        return StartTransfer("导入字体", (ct, progress) => transfers.ImportAsync(paths, ct, progress));
    }

    public Task ExportSelectionAsync()
    {
        if (!CanManage || transfers is null) return Task.CompletedTask;
        var faces = Selection().DistinctBy(f => f.Hash).ToArray();
        if (faces.Length == 0) return Task.CompletedTask;
        string destination;
        bool singleFile = faces.Length == 1;
        if (singleFile)
        {
            string? source = faces[0].Path ?? faces[0].KnownPath;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出字体", FileName = Path.GetFileName(source) ?? FontTransferService.SafeFileName(faces[0].FullName) + ".ttf",
                Filter = FontFileFilter, AddExtension = true, DefaultExt = Path.GetExtension(source) ?? ".ttf", OverwritePrompt = true
            };
            if (dialog.ShowDialog() != true) return Task.CompletedTask;
            destination = dialog.FileName;
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择字体导出目录" };
            if (dialog.ShowDialog() != true) return Task.CompletedTask;
            destination = dialog.FolderName;
        }
        return StartTransfer("导出字体", (ct, progress) => transfers.ExportFilesAsync(faces, destination, singleFile, ct, progress));
    }

    public Task ExportTagAsync(long tagId, string tagName)
    {
        if (!CanManage || transfers is null) return Task.CompletedTask;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出标签下的全部字体", FileName = FontTransferService.SafeFileName(tagName) + ".zip",
            Filter = "ZIP 压缩包 (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        string destination = dialog.FileName;
        return StartTransfer("导出标签字体", async (ct, progress) =>
        {
            // Query the tag itself; never reuse the filtered management list.
            var faces = (await repository.QueryAsync(new(TagId: tagId))).Rows;
            return await transfers.ExportZipAsync(faces, destination, ct, progress);
        });
    }

    private Task StartTransfer(string title, Func<CancellationToken, IProgress<OperationProgress>, Task<IReadOnlyList<FontTransferResult>>> action)
    {
        if (!CanManage) return Task.CompletedTask;
        return operationTask = TransferCoreAsync(title, action);
    }

    private Task TransferCoreAsync(string title, Func<CancellationToken, IProgress<OperationProgress>, Task<IReadOnlyList<FontTransferResult>>> action) => Safe(async () =>
    {
        long generation = ++operationGeneration;
        operationCancellation = new CancellationTokenSource();
        var token = operationCancellation.Token;
        OperationProgress.Begin(title); OperationCancelable = true; Operating = true;
        try
        {
            var progress = new Progress<OperationProgress>(p =>
            {
                if (generation != operationGeneration || !Operating) return;
                OperationProgress.Report(p.Completed, p.Total, true, title,
                    $"{p.Completed}/{p.Total} 个文件" + (p.CurrentFont is null ? "" : " · " + p.CurrentFont));
            });
            var results = await Task.Run(() => action(token, progress));
            ResultText = string.Join("\n", results.Select(r => $"{(r.Success ? "✓" : "✕")} {r.Source}：{r.Message}" +
                (r.OutputPath is null ? "" : " → " + r.OutputPath)));
            Summarize(title, results.Select(r => r.Success), token.IsCancellationRequested, sourceAction: true);
            int success = results.Count(r => r.Success), failed = results.Count(r => !r.Success);
            Status = $"{title}{(token.IsCancellationRequested ? "已取消" : "完成")}：{success} 成功，{failed} 失败";
            ++operationGeneration;
            OperationProgress.Finish(Status, token.IsCancellationRequested ? "Canceled" : failed > 0 ? "Warning" : "Succeeded", !token.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Status = title + "已取消，未完成部分未继续执行"; ResultText = Status;
            ResultSummary = new(title, "Canceled", Note: Status, SourceAction: true);
            OperationProgress.Finish(Status, "Canceled", false);
        }
        catch (Exception ex) { RecordTaskFailure(title, ex); ResultSummary = ResultSummary with { SourceAction = true }; OperationProgress.Finish(ex.Message, "Failed", false); throw; }
        finally
        {
            ++operationGeneration;
            try { await ReloadAsync(); }
            finally { Operating = false; OperationCancelable = false; operationCancellation.Dispose(); operationCancellation = null; }
        }
    });
}
