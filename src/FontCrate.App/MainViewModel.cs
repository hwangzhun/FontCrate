using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FontCrate.Core;

namespace FontCrate.App;

public partial class MainViewModel : ObservableObject
{
    private readonly IConfirmationService confirmations;
    private uint previewForeground = 0xFF1F293B, previewBackground = 0xFFFFFFFF;
    private readonly Repository repository;
    private readonly Scanner scanner;
    private readonly IPreviewService previews;
    private readonly FontOperations operations;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer reloadTimer;
    private CancellationTokenSource? scanCancellation, previewCancellation, operationCancellation;
    private Task? scanTask, operationTask;
    private long queryGeneration, previewGeneration, selectionGeneration;
    private int gridOffset;
    private bool resetScroll = true, syncingFilters;
    private long scanGeneration, operationGeneration;
    private bool closing, hidden = true;
    private (string FaceId, float Dpi, int Offset, uint Foreground, uint Background)? renderedGrid;
    private IReadOnlyList<UnicodeRange> coverage = [];
    public event Action? ResultsRequested;
    private Task? managementTask;
    internal Repository ManagementRepository => repository;
    public bool IsClosing => closing;
    public bool CanManage => !TaskBusy && !Confirming && !closing;
    public Task ManageAsync(Func<Task> action)
    {
        if (!CanManage) return Task.CompletedTask;
        return managementTask = ManageCoreAsync(action);
    }
    public Task AddSelectionToTagAsync(long tagId)
    {
        var ids = Selection().Select(f => f.Id).ToArray();
        return ids.Length == 0 ? Task.CompletedTask : Safe(() => ManageAsync(() => repository.AnnotateByIdAsync(ids, tagId)));
    }
    public Task NewTagForSelectionAsync()
    {
        var ids = Selection().Select(f => f.Id).ToArray();
        if (ids.Length == 0 || confirmations is not ConfirmationService dialogs) return Task.CompletedTask;
        return Safe(() => ManageAsync(() => ManagementDialogs.EditNameAsync(dialogs, "新建标签并添加所选字体", "",
            async name => { await repository.CreateNamedItemAsync(name, ids); })));
    }
    private async Task ManageCoreAsync(Func<Task> action)
    {
        Confirming = true;
        try { await action(); await ReloadAsync(); }
        finally { Confirming = false; }
    }
    public Task ScanSourceAsync(long? sourceId, bool verify = false)
    {
        if (!CanManage) return Task.CompletedTask;
        return scanTask = StartScanCore(verify, sourceId);
    }
    public async Task<ImageSource> RenderManagementPreviewAsync(FaceRow face, bool thumbnail, CancellationToken token)
    {
        var result = await previews.RenderAsync(new(face, "Aa 字体预览 0123456789", thumbnail ? 22 : 40, Dpi, 0,
            thumbnail ? 240 : 640, ForegroundArgb: previewForeground, BackgroundArgb: previewBackground), token);
        return Image(result, Dpi);
    }
    public async Task ShowSourceInLibraryAsync(long id)
    {
        ClearFilters(); await ReloadAsync();
        SelectedSource = SourceItems.FirstOrDefault(s => s.Id == id); await ReloadAsync();
    }
    public async Task ShowCategoryInLibraryAsync(long id)
    {
        ClearFilters(); await ReloadAsync();
        var panel = TagPanel;
        panel.SelectedFilter = panel.Items.FirstOrDefault(s => s.Id == id); await ReloadAsync();
    }
    public event Action? ExitRequested;
    public event Action? InitialViewReady;
    public event Action? ListPreviewsChanged;
    public event Action<IReadOnlyList<FaceRow>>? RestoreSelection;
    public ResettableCollection<FaceRow> Faces { get; } = [];
    public TaskProgressViewModel ScanProgress { get; } = new("字体扫描");
    public TaskProgressViewModel OperationProgress { get; } = new("字体操作");
    public bool TaskBusy => Scanning || Operating;
    public bool TaskIdle => !TaskBusy;
    partial void OnScanningChanged(bool value) => NotifyTaskState();
    partial void OnOperatingChanged(bool value) => NotifyTaskState();
    partial void OnConfirmingChanged(bool value) => OnPropertyChanged(nameof(CanManage));
    private void NotifyTaskState()
    {
        OnPropertyChanged(nameof(TaskBusy)); OnPropertyChanged(nameof(TaskIdle)); OnPropertyChanged(nameof(CanManage));
    }
    public bool IsReloadingFaces { get; private set; }
    public event Action<bool>? BeforeFacesReload;
    public event Action<bool>? AfterFacesReload;
    public ObservableCollection<NamedItem> SourceItems { get; } = [];
    public AnnotationPanelViewModel TagPanel { get; }
    private IReadOnlyList<FaceRow> selectedFaces = [];
    public IReadOnlyList<FaceRow> SelectedFaces
    {
        get => selectedFaces;
        set { selectedFaces = value; OnPropertyChanged(nameof(SelectionCount)); OnPropertyChanged(nameof(HasSelection)); TagPanel.NotifySelectionChanged(); }
    }
    public int SelectionCount => SelectedFaces.Count;
    public IReadOnlyList<int> GridCodes { get; private set; } = [];
    public string[] States { get; } = ["全部", "已安装", "临时启用", "来源不可用", "完全重复"];
    [ObservableProperty] private string search = "";
    [ObservableProperty] private NamedItem? selectedSource;
    [ObservableProperty] private bool favoritesOnly, scanning, operating, operationCancelable, confirming;
    [ObservableProperty] private string state = "全部";
    [ObservableProperty] private FaceRow? selectedFace;
    [ObservableProperty] private string previewText = "字体之美，始于选择。\nThe quick brown fox jumps over the lazy dog. 0123456789";
    [ObservableProperty] private string listPreviewText = "字体 Aa 0123";
    [ObservableProperty] private double fontSize = 48;
    [ObservableProperty] private int previewWidth = 560;
    [ObservableProperty] private ImageSource? previewImage, gridImage;
    [ObservableProperty] private string missingText = "", details = "请选择字体", locationsText = "";
    [ObservableProperty] private string status = "准备就绪", gridPageText = "", resultText = "";
    [ObservableProperty] private int total;
    public float Dpi { get; set; } = 96;
    public MainViewModel(Repository repository, Scanner scanner, IPreviewService previews, FontOperations operations, Dispatcher dispatcher, IConfirmationService confirmations, FontTransferService? transfers = null)
    {
        this.transfers = transfers;
        this.confirmations = confirmations;
        TagPanel = new(repository, ReloadAsync, Selection, item => ConfirmAnnotationDeletion(item, "标签"));
        TagPanel.FilterChanged += () => { OnPropertyChanged(nameof(FilterSummary)); NotifyLibraryState(); Schedule(); };
        this.repository = repository; this.scanner = scanner; this.previews = previews; this.operations = operations; this.dispatcher = dispatcher;
        reloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, async (sender, _) =>
        { ((DispatcherTimer)sender!).Stop(); await Safe(ReloadAsync); }, dispatcher); reloadTimer.Stop();
        repository.Changed += OnRepositoryChanged;
    }
    private void OnRepositoryChanged()
    {
        if (closing || Confirming || TagPanel.Busy) return;
        dispatcher.InvokeAsync(() => { if (!closing && !Confirming && !TagPanel.Busy) Schedule(false); });
    }
    private void Schedule(bool reset = true)
    { if (closing || syncingFilters) return; resetScroll |= reset; ++queryGeneration; reloadTimer.Stop(); reloadTimer.Start(); }
    partial void OnSearchChanged(string value) { OnPropertyChanged(nameof(FilterSummary)); NotifyLibraryState(); Schedule(); }
    partial void OnSelectedSourceChanged(NamedItem? value) { OnPropertyChanged(nameof(FilterSummary)); NotifyLibraryState(); Schedule(); }
    partial void OnFavoritesOnlyChanged(bool value) { OnPropertyChanged(nameof(FilterSummary)); NotifyLibraryState(); Schedule(); }
    partial void OnStateChanged(string value) { OnPropertyChanged(nameof(FilterSummary)); NotifyLibraryState(); Schedule(); }
    partial void OnSelectedFaceChanged(FaceRow? value) { TagPanel.NotifySelectionChanged(); if (IsReloadingFaces) return; gridOffset = 0; _ = Safe(UpdateSelectionAsync); }
    partial void OnListPreviewTextChanged(string value) => ListPreviewsChanged?.Invoke();
    partial void OnPreviewTextChanged(string value) => _ = Safe(RenderPreviewAsync);
    partial void OnFontSizeChanged(double value) => _ = Safe(RenderPreviewAsync);
    partial void OnPreviewWidthChanged(int value) => _ = Safe(RenderPreviewAsync);
    public Task InitializeAsync() => operationTask = InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        Status = "正在加载字体库与核对操作记录…";
        OperationProgress.Begin(Status); Operating = true;
        try
        {
            await ReloadAsync(); InitialViewReady?.Invoke();
            var result = await Task.Run(() => operations.RecoverAsync(CancellationToken.None));
            ResultText = string.Join("\n", result.Select(x => x.Message));
            Summarize("启动状态核对", result.Select(x => x.Success));
            Status = result.Any(x => !x.Success) ? "部分操作待处理，请查看结果" : "准备就绪";
            OperationProgress.Finish(Status, result.Any(x => !x.Success) ? "Warning" : "Succeeded");
        }
        catch (Exception ex) { RecordTaskFailure(OperationProgress.Message, ex); OperationProgress.Finish(ex.Message, "Failed", false); throw; }
        finally { try { await ReloadAsync(); } finally { Operating = false; } }
    }
    private static long? Filter(NamedItem? item) => item is null || item.Id == 0 ? null : item.Id;
    public string FilterSummary => $"搜索：{(string.IsNullOrWhiteSpace(Search) ? "全部" : Search)} · 来源：{SelectedSource?.Name ?? "全部来源"} · 标签：{TagPanel.SelectedFilter?.Name ?? "全部标签"} · 收藏：{(FavoritesOnly ? "仅收藏" : "全部")} · 状态：{State}";
    [RelayCommand] private void ClearFilters()
    {
        Search = ""; SelectedSource = SourceItems.FirstOrDefault();
        TagPanel.SelectedFilter = TagPanel.FilterItems.FirstOrDefault();
        FavoritesOnly = false; State = "全部";
    }
    private Task<bool> ConfirmAnnotationDeletion(NamedItem item, string title) => ConfirmActionAsync(
        $"删除{title}", $"删除{title}“{item.Name}”？\n将移除与 {item.FaceCount} 个字体的关联，字体文件、收藏及其他标注会保留。", "删除");
    private async Task<bool> ConfirmActionAsync(string title, string message, string accept = "确认")
    {
        if (Confirming || closing) return false;
        Confirming = true;
        try { return await confirmations.ConfirmAsync(title, message, accept) && !closing; }
        finally { Confirming = false; }
    }
    private async Task ReloadAsync()
    {
        var elapsed = Stopwatch.StartNew();
        long generation = ++queryGeneration;
        LibraryLoading = true;
        try
        {
            PerformanceLog.Write("library-refresh-start", 0, $"generation={generation}");
            reloadTimer.Stop();
            var sources = await repository.SourcesAsync();
            var tags = await repository.NamedItemCountsAsync();
            if (generation != queryGeneration || closing) return;
            long sourceSelection = SelectedSource?.Id ?? 0;
            long tagSelection = TagPanel.SelectedFilter?.Id ?? 0;
            syncingFilters = true;
            try
            {
                SyncItems(SourceItems, sources.Select(s => new NamedItem(s.Id, (s.Available ? "" : "[不可用] ") + s.Path)), "全部来源");
                SelectedSource = SourceItems.FirstOrDefault(s => s.Id == sourceSelection) ?? SourceItems[0];
                TagPanel.Refresh(tags);
                if (sourceSelection != SelectedSource.Id || tagSelection != TagPanel.SelectedFilter?.Id)
                    resetScroll = true;
            }
            finally { syncingFilters = false; }
            OnPropertyChanged(nameof(FilterSummary)); NotifyLibraryState();
            var q = new FaceQuery(Search, Filter(SelectedSource), Filter(TagPanel.SelectedFilter), FavoritesOnly, State);
            var page = await repository.QueryAsync(q);
            if (generation != queryGeneration || closing) return;
            var previousFace = SelectedFace;
            string? selected = previousFace?.Id; var selectedIds = SelectedFaces.Select(f => f.Id).ToHashSet();
            bool scrollToTop = resetScroll; resetScroll = false;
            BeforeFacesReload?.Invoke(scrollToTop);
            IsReloadingFaces = true;
            try
            {
                if (!Faces.SequenceEqual(page.Rows)) Faces.ReplaceAll(page.Rows);
                SelectedFace = Faces.FirstOrDefault(f => f.Id == selected);
                SelectedFaces = Faces.Where(f => selectedIds.Contains(f.Id)).ToArray();
                RestoreSelection?.Invoke(SelectedFaces.OrderBy(f => f.Id == selected ? 0 : 1).ToArray());
                // WPF can change SelectedItem while restoring a multi-selection.
                SelectedFace = Faces.FirstOrDefault(f => f.Id == selected);
            }
            finally { IsReloadingFaces = false; AfterFacesReload?.Invoke(scrollToTop); }
            Total = page.Total;
            if (previousFace != SelectedFace) { gridOffset = 0; await Safe(UpdateSelectionAsync); }
            else await Safe(RefreshAnnotationDetailsAsync);
            PerformanceLog.Write("query-display", elapsed.Elapsed.TotalMilliseconds, $"generation={generation}; total={Total}");
        }
        finally { if (generation == queryGeneration) LibraryLoading = false; }
    }

    private static void SyncItems(ObservableCollection<NamedItem> target, IEnumerable<NamedItem> values, string all)
    {
        var desired = new[] { new NamedItem(0, all) }.Concat(values).ToArray();
        if (target.SequenceEqual(desired)) return;
        // Preserve instances for the selected filter whenever possible.
        foreach (var old in target.Where(x => !desired.Any(y => y.Id == x.Id)).ToArray()) target.Remove(old);
        for (int i = 0; i < desired.Length; i++)
        {
            var existing = target.FirstOrDefault(x => x.Id == desired[i].Id);
            if (existing is null) target.Insert(Math.Min(i, target.Count), desired[i]);
            else
            {
                int index = target.IndexOf(existing);
                if (existing != desired[i]) target[index] = desired[i];
                if (index != i) target.Move(index, i);
            }
        }
    }
    [RelayCommand] private async Task AddDirectoryAsync()
    {
        if (TaskBusy || Confirming || closing) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择字体目录", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        await Safe(async () => { var source = await repository.AddSourceAsync(dialog.FolderName); await ReloadAsync(); SelectedSource = SourceItems.First(s => s.Id == source.Id); await StartScan(false); });
    }
    [RelayCommand] private async Task RemoveSourceAsync()
    {
        if (TaskBusy || Confirming || closing || SelectedSource?.Id is null or 0) return;
        var id = SelectedSource.Id;
        if (!await ConfirmActionAsync("移除来源", "移除此来源的索引位置？原文件和字体标注会保留。", "移除")) return;
        await Safe(async () => { SelectedSource = null; await repository.RemoveSourceAsync(id); await ReloadAsync(); });
    }
    [RelayCommand] private Task ScanAsync() => Safe(() => StartScan(false));
    [RelayCommand] private Task VerifyAsync() => Safe(() => StartScan(true));
    private Task StartScan(bool verify)
    {
        if (TaskBusy || Confirming || closing) return Task.CompletedTask;
        return scanTask = StartScanCore(verify, Filter(SelectedSource));
    }
    private async Task StartScanCore(bool verify, long? sourceId)
    {
        if (TaskBusy || Confirming || closing) return;
        scanCancellation = new CancellationTokenSource();
        long generation = ++scanGeneration;
        ScanProgress.Begin(verify ? "正在校验字体目录…" : "正在扫描字体目录…");
        Scanning = true;
        var token = scanCancellation.Token;
        var scanElapsed = Stopwatch.StartNew();
        int sourceNumber = 0, sourceCount = 0, errors = 0, processed = 0, scanSuccess = 0;
        bool complete = true;
        ScanProgress? latestProgress = null;
        var scanWork = Task.Run(async () =>
        {
            var sources = (await repository.SourcesAsync()).Where(s => sourceId is null || s.Id == sourceId).ToArray();
            sourceCount = sources.Length;
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                int number = ++sourceNumber;
                await dispatcher.InvokeAsync(() => ScanProgress.Begin($"来源 {number}/{sourceCount} · {source.Path}"));
                var progress = new CallbackProgress<ScanProgress>(p =>
                {
                    latestProgress = p;
                    dispatcher.InvokeAsync(() =>
                    {
                    if (!Scanning || generation != scanGeneration || sourceNumber != number) return;
                    ScanProgress.Report(p.Processed, p.Discovered, p.EnumerationComplete,
                        $"来源 {number}/{sourceCount} · {p.Phase}",
                        $"已处理 {p.Processed}/{p.Discovered} · 解析 {p.Parsed} · 跳过 {p.Unchanged} · 错误 {p.Errors}");
                    });
                });
                var result = await scanner.ScanAsync(source, verify, progress, token);
                errors += result.Errors; processed += result.Discovered; scanSuccess += result.Parsed + result.Unchanged; complete &= result.Complete;
                latestProgress = null;
                PerformanceLog.Write(verify ? "scan-verify" : "scan-incremental", scanElapsed.Elapsed.TotalMilliseconds,
                    $"parsed={result.Parsed};unchanged={result.Unchanged};errors={result.Errors};complete={result.Complete}");
            }
        }, token);
        try
        {
            await scanWork;
            // Stop late progress callbacks before entering the reconciliation phase.
            ++scanGeneration;
            ScanProgress.Indeterminate = true;
            ScanProgress.Message = "扫描结束，正在核对系统字体状态…";
            ScanProgress.Caption = "正在核对系统状态…";
            await Task.Run(() => operations.RefreshAsync(token));
            Status = sourceCount == 0 ? "没有可扫描的来源，请添加目录" : $"扫描结束：{sourceCount} 个来源，{processed} 个文件，{errors} 个错误";
            ResultText = Status;
            ResultSummary = new("字体扫描", errors > 0 || !complete || sourceCount == 0 ? "Warning" : "Succeeded", scanSuccess, errors, Note: $"已发现 {processed} 个文件 · {errors} 个错误（含来源错误）", SourceAction: true);
            ScanProgress.Finish(Status, errors > 0 || !complete || sourceCount == 0 ? "Warning" : "Succeeded");
        }
        catch (OperationCanceledException)
        {
            if (latestProgress is { } p)
                ScanProgress.Report(p.Processed, p.Discovered, p.EnumerationComplete, "扫描已取消", $"已处理 {p.Processed}/{p.Discovered}");
            Status = "扫描已取消，既有位置保留";
            ResultText = Status + (latestProgress is { } snapshot ? $" · 当前来源已处理 {snapshot.Processed}/{snapshot.Discovered}" : "");
            ResultSummary = new("字体扫描", "Canceled", scanSuccess + (latestProgress?.Parsed ?? 0) + (latestProgress?.Unchanged ?? 0), errors + (latestProgress?.Errors ?? 0), Note: ResultText, SourceAction: true);
            ScanProgress.Finish(Status + (latestProgress is { } last ? $" · 当前来源已处理 {last.Processed}/{last.Discovered} 个文件" : ""), "Canceled", false);
        }
        catch (Exception ex) { RecordTaskFailure("字体扫描", ex); ScanProgress.Finish(ex.Message, "Failed", false); throw; }
        finally
        {
            ++scanGeneration;
            try { await ReloadAsync(); }
            finally { Scanning = false; scanCancellation.Dispose(); scanCancellation = null; }
        }
    }
    [RelayCommand] private void CancelScan()
    {
        scanCancellation?.Cancel();
        ScanProgress.Message = "正在取消扫描并收尾…";
    }
    private FaceRow[] Selection() => SelectedFaces.Count > 0 ? SelectedFaces.ToArray() : SelectedFace is null ? [] : [SelectedFace];
    [RelayCommand] private Task FavoriteAsync() => Safe(async () => { var selection = Selection(); await repository.SetFavoriteAsync(selection.Select(f => f.Id), !selection.All(f => f.Favorite)); });
    [RelayCommand] private Task ActivateAsync() => Operate("activate");
    [RelayCommand] private Task DeactivateAsync() => Operate("deactivate");
    [RelayCommand] private Task InstallAsync() => Operate("install");
    [RelayCommand] private Task UninstallAsync() => Operate("uninstall");
    private Task Operate(string action)
    {
        if (TaskBusy || Confirming || closing) return Task.CompletedTask;
        return operationTask = OperateCore(action);
    }
    private Task OperateCore(string action) => Safe(async () =>
    {
        if (TaskBusy || Confirming || closing) return;
        var selection = Selection(); if (selection.Length == 0) return;
        string actionName = action switch { "activate" => "临时启用", "deactivate" => "停用临时字体", "install" => "安装到当前用户", _ => "卸载本工具安装的字体" };
        string actionDescription = action switch
        {
            "deactivate" => "仅撤销本工具的临时启用，保留原始字体文件和持久安装。\n",
            "uninstall" => "仅移除本工具持久安装的字体，不删除来源目录中的原文件。\n",
            _ => ""
        };
        // Reserve the preparation phase too, so a scan cannot start while querying confirmation details.
        Confirming = true;
        bool accepted;
        try
        {
            var impacted = new List<string>();
            foreach (var hash in selection.Select(f => f.Hash).Distinct())
            {
                var faces = await repository.FacesForFileAsync(hash);
                impacted.AddRange(faces.Select(f => f.FullName));
            }
            if (closing) return;
            accepted = await confirmations.ConfirmAsync("确认文件级字体操作", actionDescription +
                $"{actionName}将影响 {selection.Select(f => f.Hash).Distinct().Count()} 个文件中的 {impacted.Count} 个 face：\n" +
                string.Join("\n", impacted.Take(16)) + (impacted.Count > 16 ? "\n…" : ""), actionName);
        }
        finally { Confirming = false; }
        if (!accepted) return;
        if (TaskBusy || closing) return;
        long generation = ++operationGeneration;
        OperationProgress.Begin(actionName, false); Operating = true;
        operationCancellation = new CancellationTokenSource(); OperationCancelable = true; var token = operationCancellation.Token;
        try
        {
            var progress = new Progress<OperationProgress>(p =>
            {
                if (!Operating || generation != operationGeneration) return;
                OperationProgress.Report(p.Completed, p.Total, true, actionName,
                    $"{p.Completed}/{p.Total} 个文件" + (p.CurrentFont is null ? "" : $" · {p.CurrentFont}"));
            });
            var task = Task.Run(() => operations.RunAsync(action, selection, token, progress));
            var result = await task;
            ResultText = string.Join("\n", result.Select(r => $"{(r.Success ? "✓" : "✕")} {r.Hash[..8]}：{r.Message}"));
            Summarize(actionName, result.Select(r => r.Success), token.IsCancellationRequested, selection.Select(f => f.Hash).Distinct().Count());
            Status = $"{(token.IsCancellationRequested ? "操作已取消" : "操作完成")}：{result.Count(r => r.Success)} 成功，{result.Count(r => !r.Success)} 失败";
            ++operationGeneration;
            OperationProgress.Report(result.Count, selection.Select(f => f.Hash).Distinct().Count(), true, actionName, $"已处理 {result.Count} 个文件");
            OperationProgress.Finish(Status, token.IsCancellationRequested ? "Canceled" : result.Any(r => !r.Success) ? "Failed" : "Succeeded",
                !token.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Status = "操作已取消，未完成部分未继续执行"; ResultText = Status;
            ResultSummary = new(actionName, "Canceled", Note: Status);
            OperationProgress.Finish(Status, "Canceled", false);
        }
        catch (Exception ex) { RecordTaskFailure(OperationProgress.Message, ex); OperationProgress.Finish(ex.Message, "Failed", false); throw; }
        finally
        {
            ++operationGeneration;
            try { await ReloadAsync(); }
            finally { Operating = false; OperationCancelable = false; operationCancellation.Dispose(); operationCancellation = null; }
        }
    });
    [RelayCommand] private void CancelOperation()
    {
        operationCancellation?.Cancel(); OperationCancelable = false;
        OperationProgress.Message = "正在取消剩余操作，等待已开始文件完成或回滚…";
    }
    [RelayCommand] private Task RecoverAsync()
    {
        if (TaskBusy || Confirming || closing) return Task.CompletedTask;
        return operationTask = RecoverCore();
    }
    private Task RecoverCore() => Safe(async () =>
    {
        if (TaskBusy || Confirming || closing) return;
        OperationProgress.Begin("正在核对与恢复字体状态…"); Operating = true;
        try
        {
            var results = await Task.Run(() => operations.RecoverAsync(CancellationToken.None));
            ResultText = string.Join("\n", results.Select(r => r.Message));
            Summarize("字体状态核对与清理", results.Select(r => r.Success));
            Status = results.Any(r => !r.Success) ? "部分状态待处理，请查看操作结果" : "状态核对完成";
            OperationProgress.Finish(Status, results.Any(r => !r.Success) ? "Warning" : "Succeeded");
        }
        catch (Exception ex) { RecordTaskFailure(OperationProgress.Message, ex); OperationProgress.Finish(ex.Message, "Failed", false); throw; }
        finally { try { await ReloadAsync(); } finally { Operating = false; } }
    });
    [RelayCommand] private void Locate()
    {
        if (SelectedFace?.Path is not string path) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.Arguments = "/select,\"" + path + "\""; Process.Start(start);
    }
    [RelayCommand] private Task ShowErrorsAsync() => Safe(async () => { var errors = await repository.ErrorsAsync(); ResultSummary = new("扫描错误记录", errors.Count == 0 ? "Succeeded" : "Warning", Failed: errors.Count, SourceAction: true); ResultText = errors.Count == 0 ? "没有扫描错误" : string.Join("\n", errors.Select(e => e.Path + "\n" + e.Message)); ResultsRequested?.Invoke(); });
    [RelayCommand] private void Exit() => ExitRequested?.Invoke();
    private async Task UpdateSelectionAsync()
    {
        long version = ++selectionGeneration; coverage = [];
        previewCancellation?.Cancel(); PreviewImage = null; GridImage = null; MissingText = "";
        var face = SelectedFace; if (face is null) { Details = "请选择字体"; LocationsText = ""; coverage = []; return; }
        var parsed = (await repository.FacesForFileAsync(face.Hash)).First(f => f.Index == face.Index);
        var locations = await repository.LocationsAsync(face.Hash);
        var tags = await repository.FaceItemsAsync(face.Id);
        if (SelectedFace?.Id != face.Id || version != selectionGeneration) return;
        coverage = parsed.Coverage;
        SetDetails(face, tags);
        LocationsText = string.Join("\n", locations.Select(l => (l.Available ? "" : "[不可用] ") + l.Path));
        await RenderPreviewAsync();
    }
    private async Task RefreshAnnotationDetailsAsync()
    {
        var face = SelectedFace; long version = selectionGeneration;
        if (face is null) return;
        var tags = await repository.FaceItemsAsync(face.Id);

        if (SelectedFace?.Id == face.Id && version == selectionGeneration) SetDetails(face, tags);
    }
    private void SetDetails(FaceRow face, IReadOnlyList<NamedItem> tags)
    {
        Details = $"{face.FullName}\n{face.FileType} · {face.Style} · 字重 {face.Weight} · 宽度 {face.Width}\n{face.PostScriptName}\n{face.Version}\nface #{face.Index} · {(face.Variable ? "变量字体 / 默认实例" : "静态字体")}\n{face.Status}\n标签：{string.Join("、", tags.Select(t => t.Name))}\nSHA-256：{face.Hash}";
    }
    private async Task RenderPreviewAsync()
    {
        previewCancellation?.Cancel(); previewCancellation?.Dispose(); previewCancellation = new CancellationTokenSource();
        var token = previewCancellation.Token; long generation = ++previewGeneration;
        var face = SelectedFace; if (face is null || hidden || closing) return;
        if (face.Path is null) { MissingText = "字体来源不可用"; return; }
        try
        {
            await Task.Delay(120, token);
            var elapsed = Stopwatch.StartNew();
            var result = await previews.RenderAsync(new(face, PreviewText, (float)FontSize, Dpi, generation, PreviewWidth, ForegroundArgb: previewForeground, BackgroundArgb: previewBackground), token);
            if (generation != previewGeneration || token.IsCancellationRequested) return;
            PreviewImage = Image(result, Dpi); MissingText = result.Missing.Count == 0 ? "预览文字均有字符映射" : "缺字：" + string.Join(" ", result.Missing.Select(c => $"U+{c:X4}"));
            PerformanceLog.Write("preview-display", elapsed.Elapsed.TotalMilliseconds);
            // The glyph grid does not depend on preview text, font size or viewport width.
            var gridKey = (face.Id, Dpi, gridOffset, previewForeground, previewBackground);
            if (GridImage is not null && renderedGrid == gridKey) return;
            var codes = CoveragePage(gridOffset); GridCodes = codes;
            long count = coverage.Sum(r => (long)r.Last - r.First + 1);
            GridPageText = $"{(codes.Count == 0 ? 0 : gridOffset + 1)}–{gridOffset + codes.Count} / {count} 个字符";
            var grid = await previews.RenderAsync(new(face, "", 24, Dpi, generation, 512, codes, previewForeground, previewBackground), token);
            if (generation == previewGeneration && !token.IsCancellationRequested)
            {
                GridImage = Image(grid, Dpi); renderedGrid = gridKey;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (generation == previewGeneration) MissingText = e.Message; }
    }
    private List<int> CoveragePage(int skip)
    {
        var result = new List<int>(64);
        foreach (var range in coverage)
        {
            int count = range.Last - range.First + 1;
            if (skip >= count) { skip -= count; continue; }
            for (int c = range.First + skip; c <= range.Last && result.Count < 64; c++) if (Rune.IsValid(c)) result.Add(c);
            skip = 0; if (result.Count == 64) break;
        }
        return result;
    }
    private static ImageSource Image(PreviewResult r, float dpi)
    { var image = BitmapSource.Create(r.Width, r.Height, dpi, dpi, PixelFormats.Pbgra32, null, r.Pixels, r.Stride); image.Freeze(); return image; }
    public async Task<ImageSource> RenderThumbnailAsync(FaceRow face, CancellationToken ct)
    {
        var result = await previews.RenderAsync(new(face, ListPreviewText.Replace('\r', ' ').Replace('\n', ' '), 24, Dpi, 0, 320, ForegroundArgb: previewForeground, BackgroundArgb: previewBackground), ct);
        return Image(result, Dpi);
    }
    [RelayCommand] private async Task PreviousCharactersAsync() { gridOffset = Math.Max(0, gridOffset - 64); await RenderPreviewAsync(); }
    [RelayCommand] private async Task NextCharactersAsync() { if (gridOffset + 64 < coverage.Sum(r => (long)r.Last - r.First + 1)) gridOffset += 64; await RenderPreviewAsync(); }
    public void SelectCharacter(int index) { if (index >= 0 && index < GridCodes.Count) PreviewText = char.ConvertFromUtf32(GridCodes[index]); }
    public void SetPreviewColors(uint foreground, uint background)
    {
        if (foreground == previewForeground && background == previewBackground) return;
        previewForeground = foreground; previewBackground = background;
        ++previewGeneration; previewCancellation?.Cancel(); PreviewImage = null; GridImage = null;
        previews.ClearCache(); ListPreviewsChanged?.Invoke(); _ = Safe(RenderPreviewAsync);
    }
    public void SuspendPreviews() { hidden = true; ++previewGeneration; previewCancellation?.Cancel(); }
    public void ClearPreviews() { SuspendPreviews(); PreviewImage = null; GridImage = null; previews.ClearCache(); }
    public void RestorePreviews() { hidden = false; _ = Safe(RenderPreviewAsync); }
    private async Task Safe(Func<Task> work)
    { try { await work(); } catch (OperationCanceledException) { } catch (Exception e) { if (!ReferenceEquals(presentedError, e)) RecordTaskFailure("操作异常", e); PerformanceLog.Write("ui-operation-error", 0, e.ToString()); } }
    public async Task<IReadOnlyList<OperationResult>> ShutdownAsync()
    {
        closing = true; reloadTimer.Stop(); scanCancellation?.Cancel(); previewCancellation?.Cancel(); operationCancellation?.Cancel();
        if (managementTask is not null) { try { await managementTask; } catch { } }
        if (scanTask is not null) { try { await scanTask; } catch { } }
        if (operationTask is not null) { try { await operationTask; } catch { } }
        OperationCancelable = false;
        OperationProgress.Begin("正在卸载临时字体并清理操作记录…"); Operating = true;
        try
        {
            var results = await Task.Run(operations.ExitAsync);
            var failures = results.Where(r => !r.Success).ToArray();
            ResultText = string.Join("\n", results.Select(r => r.Message));
            Summarize("字体状态核对与清理", results.Select(r => r.Success));
            OperationProgress.Finish(failures.Length == 0 ? "临时字体已清理，可以退出" : "部分临时字体未能停用", failures.Length == 0 ? "Succeeded" : "Failed");
            if (failures.Length > 0) closing = false;
            else repository.Changed -= OnRepositoryChanged;
            return failures;
        }
        catch (Exception ex)
        {
            closing = false;
            OperationProgress.Finish(ex.Message, "Failed", false);
            throw;
        }
        finally { Operating = false; }
    }
}
