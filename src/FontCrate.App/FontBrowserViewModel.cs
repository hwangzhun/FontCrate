using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FontCrate.Core;

namespace FontCrate.App;

public abstract partial class FontBrowserViewModel : ObservableObject, IDisposable
{
    protected readonly MainViewModel Owner;
    protected readonly Repository Repository;
    private readonly Dispatcher dispatcher;
    private CancellationTokenSource? searchCancellation, refreshCancellation, previewCancellation;
    private long queryGeneration, refreshGeneration;
    private bool disposed, previewActive;
    public bool IsReloadingFonts { get; private set; }
    public event Action? PreviewSettingsChanged;
    public event Action<FaceRow[]>? SelectionRestored;
    public event Action? BeforeFontsChanged;
    public ResettableCollection<FaceRow> Fonts { get; } = [];
    public FaceRow[] SelectedFonts { get; private set; } = [];
    [ObservableProperty] private string fontSearch = "", error = "", previewMessage = "选择字体查看预览";
    [ObservableProperty] private bool busy, loading;
    [ObservableProperty] private FaceRow? selectedFont;
    [ObservableProperty] private ImageSource? previewImage;
    public bool CanInteract => !Busy && !Loading && Owner.CanManage;
    public bool NoFonts => !Loading && Fonts.Count == 0;
    public virtual string SearchPlaceholder => "搜索字体名称或样式";
    public string FontCountText => $"{Fonts.Count} 个字体 · 已选 {SelectedFonts.Length} 个";
    protected FontBrowserViewModel(MainViewModel owner)
    {
        Owner = owner; Repository = owner.ManagementRepository; dispatcher = Dispatcher.CurrentDispatcher;
        Repository.Changed += OnRepositoryChanged; Owner.PropertyChanged += OnOwnerChanged;
        Owner.ListPreviewsChanged += OnPreviewSettingsChanged;
    }
    protected static void SyncEntries<T>(ObservableCollection<T> target, IReadOnlyList<T> desired, Func<T, long> key)
    {
        var ids = desired.Select(key).ToHashSet();
        for (int i = target.Count - 1; i >= 0; i--) if (!ids.Contains(key(target[i]))) target.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && key(target[i]) == key(desired[i]))
            { if (!EqualityComparer<T>.Default.Equals(target[i], desired[i])) target[i] = desired[i]; continue; }
            int found = -1;
            for (int j = i + 1; j < target.Count; j++) if (key(target[j]) == key(desired[i])) { found = j; break; }
            if (found >= 0) { target.Move(found, i); target[i] = desired[i]; }
            else target.Insert(i, desired[i]);
        }
    }
    protected abstract FaceQuery? CurrentQuery { get; }
    protected virtual IEnumerable<FaceRow> FilterFonts(IEnumerable<FaceRow> fonts) => fonts;
    protected virtual Task RefreshEntriesAsync(long generation) => Task.CompletedTask;
    protected bool CurrentRefresh(long generation) => !disposed && generation == refreshGeneration;
    protected virtual void NotifyCommands() { }
    protected void NotifyState()
    {
        OnPropertyChanged(nameof(CanInteract)); OnPropertyChanged(nameof(NoFonts));
        OnPropertyChanged(nameof(FontCountText)); NotifyCommands();
    }
    partial void OnBusyChanged(bool value) => NotifyState();
    partial void OnLoadingChanged(bool value) => NotifyState();
    partial void OnSelectedFontChanged(FaceRow? value) => _ = UpdatePreviewAsync();
    partial void OnFontSearchChanged(string value) => ScheduleFonts();
    public void SetSelection(IEnumerable<FaceRow> fonts, FaceRow? focused)
    { if (disposed || IsReloadingFonts) return; SelectedFonts = fonts.ToArray(); SelectedFont = focused; NotifyState(); }
    protected void ScheduleFonts() { if (disposed) return; Loading = true; _ = DebounceFontsAsync(); }
    private async Task DebounceFontsAsync()
    {
        searchCancellation?.Cancel(); searchCancellation?.Dispose();
        searchCancellation = new(); var token = searchCancellation.Token;
        ++queryGeneration;
        try { await Task.Delay(200, token); if (!disposed) await LoadFontsAsync(); }
        catch (OperationCanceledException) { }
    }
    private void OnRepositoryChanged()
    {
        // Check at notification time: the owning management operation performs its
        // own final refresh, even if its queued dispatcher callback runs later.
        if (disposed || Busy) return;
        dispatcher.InvokeAsync(() => { if (!disposed && !Busy) _ = DebounceRefreshAsync(); });
    }
    private async Task DebounceRefreshAsync()
    {
        refreshCancellation?.Cancel(); refreshCancellation?.Dispose(); refreshCancellation = new();
        var token = refreshCancellation.Token;
        try { await Task.Delay(200, token); await RefreshAsync(); }
        catch (OperationCanceledException) { }
    }
    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(MainViewModel.TaskBusy) or nameof(MainViewModel.Confirming)) NotifyState(); }
    public async Task RefreshAsync()
    {
        if (disposed) return;
        long generation = ++refreshGeneration;
        try { await RefreshEntriesAsync(generation); if (CurrentRefresh(generation)) await LoadFontsAsync(); }
        catch (Exception ex) { if (CurrentRefresh(generation)) Error = ex.Message; PerformanceLog.Write("management-refresh-error", 0, ex.ToString()); }
    }
    public async Task LoadFontsAsync()
    {
        if (disposed) return;
        long generation = ++queryGeneration; Loading = true;
        PerformanceLog.Write("management-fonts-start", 0, $"view={GetType().Name}; generation={generation}");
        var query = CurrentQuery;
        try
        {
            var rows = query is null ? [] : (await Repository.QueryAsync(query)).Rows;
            if (generation != queryGeneration || disposed) return;
            var selectedIds = SelectedFonts.Select(f => f.Id).ToHashSet(); string? focused = SelectedFont?.Id;
            var desired = FilterFonts(rows).ToArray();
            if (!Fonts.SequenceEqual(desired))
            {
                IsReloadingFonts = true;
                try
                {
                    BeforeFontsChanged?.Invoke(); Fonts.ReplaceAll(desired);
                    SelectedFonts = Fonts.Where(f => selectedIds.Contains(f.Id)).ToArray();
                    SelectedFont = Fonts.FirstOrDefault(f => f.Id == focused);
                    SelectionRestored?.Invoke(SelectedFonts);
                }
                finally { IsReloadingFonts = false; }
            }
        }
        catch (Exception ex) { if (generation == queryGeneration && !disposed) Error = ex.Message; PerformanceLog.Write("management-fonts-error", 0, ex.ToString()); }
        finally
        {
            PerformanceLog.Write("management-fonts-end", 0, $"view={GetType().Name}; generation={generation}; rows={Fonts.Count}; disposed={disposed}");
            if (generation == queryGeneration && !disposed) { Loading = false; NotifyState(); }
        }
    }
    protected async Task RunAsync(Func<Task> action)
    {
        if (!CanInteract) return;
        Busy = true; Error = "";
        try { await Owner.ManageAsync(action); await RefreshAsync(); }
        catch (Exception ex) { if (!disposed) Error = ex.Message; PerformanceLog.Write("management-operation-error", 0, ex.ToString()); }
        finally { Busy = false; }
    }
    private void OnPreviewSettingsChanged()
    { PreviewSettingsChanged?.Invoke(); _ = UpdatePreviewAsync(); }
    private async Task UpdatePreviewAsync()
    {
        if (disposed) return;
        previewCancellation?.Cancel(); previewCancellation?.Dispose(); previewCancellation = new();
        var token = previewCancellation.Token; var face = SelectedFont; PreviewImage = null;
        PreviewMessage = face is null ? "选择字体查看预览" : face.SourceUnavailable ? "来源不可用，保留索引与分类关联；恢复来源后可预览。" : "正在生成预览…";
        if (face is null || face.SourceUnavailable || disposed || !previewActive) return;
        try
        {
            var image = await Owner.RenderManagementPreviewAsync(face, false, token);
            if (!token.IsCancellationRequested && !disposed) { PreviewImage = image; PreviewMessage = face.FullName; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && !disposed) PreviewMessage = "无法预览：" + ex.Message; }
    }
    public Task<ImageSource> RenderThumbnailAsync(FaceRow face, CancellationToken token) => Owner.RenderManagementPreviewAsync(face, true, token);
    public void SuspendPreview()
    { if (disposed) return; previewActive = false; previewCancellation?.Cancel(); PreviewImage = null; }
    public void ResumePreview()
    { if (disposed) return; previewActive = true; _ = UpdatePreviewAsync(); }
    public virtual void Dispose()
    {
        if (disposed) return;
        disposed = true; ++queryGeneration; ++refreshGeneration;
        Repository.Changed -= OnRepositoryChanged; Owner.PropertyChanged -= OnOwnerChanged;
        Owner.ListPreviewsChanged -= OnPreviewSettingsChanged;
        searchCancellation?.Cancel(); searchCancellation?.Dispose(); refreshCancellation?.Cancel(); refreshCancellation?.Dispose();
        previewCancellation?.Cancel(); previewCancellation?.Dispose();
        searchCancellation = refreshCancellation = previewCancellation = null;
    }
}
