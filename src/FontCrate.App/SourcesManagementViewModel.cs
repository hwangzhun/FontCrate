using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FontCrate.Core;

namespace FontCrate.App;

public partial class SourcesManagementViewModel : FontBrowserViewModel
{
    private readonly ConfirmationService dialogs;
    private readonly Action viewFonts;
    private IReadOnlyList<SourceSummary> all = [];
    private CancellationTokenSource? searchCancellation;
    private bool syncingItems;
    public ObservableCollection<SourceSummary> Sources { get; } = [];
    [ObservableProperty] private SourceSummary? selectedSource;
    [ObservableProperty] private string search = "";
    public string Summary => $"{all.Count} 个来源 · {all.Count(s => !s.Available)} 个不可用";
    public bool HasSource => SelectedSource is not null;
    public bool NoSources => Sources.Count == 0;
    protected override FaceQuery? CurrentQuery => SelectedSource is null ? null : new(FontSearch, SourceId: SelectedSource.Id);
    public SourcesManagementViewModel(MainViewModel owner, ConfirmationService dialogs, Action viewFonts) : base(owner)
    { this.dialogs = dialogs; this.viewFonts = viewFonts; }
    partial void OnSelectedSourceChanged(SourceSummary? oldValue, SourceSummary? newValue)
    {
        OnPropertyChanged(nameof(HasSource));
        if (syncingItems) return;
        if (oldValue?.Id != newValue?.Id) { Fonts.Clear(); SetSelection([], null); }
        NotifyState(); ScheduleFonts();
    }
    partial void OnSearchChanged(string value) => _ = SearchAsync();
    private async Task SearchAsync()
    {
        searchCancellation?.Cancel(); searchCancellation?.Dispose(); searchCancellation = new(); var token = searchCancellation.Token;
        try { await Task.Delay(200, token); RebuildSources(); } catch (OperationCanceledException) { }
    }
    private void RebuildSources()
    {
        var selected = SelectedSource;
        syncingItems = true;
        try
        {
            SyncEntries(Sources, all.Where(s => s.Path.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray(), s => s.Id);
            SelectedSource = Sources.FirstOrDefault(s => s.Id == selected?.Id) ?? Sources.FirstOrDefault();
        }
        finally { syncingItems = false; }
        if (selected?.Id != SelectedSource?.Id) { Fonts.Clear(); SetSelection([], null); ScheduleFonts(); }
        OnPropertyChanged(nameof(NoSources)); OnPropertyChanged(nameof(Summary)); NotifyState();
    }
    protected override async Task RefreshEntriesAsync(long generation)
    { var entries = await Repository.SourceSummariesAsync(); if (CurrentRefresh(generation)) { all = entries; RebuildSources(); } }
    private bool CanSource() => CanInteract && HasSource;
    protected override void NotifyCommands()
    {
        AddCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged(); ScanCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged(); ScanAllCommand.NotifyCanExecuteChanged(); OpenFolderCommand.NotifyCanExecuteChanged(); ViewCommand.NotifyCanExecuteChanged();
    }
    private bool CanRun() => CanInteract;
    [RelayCommand(CanExecute = nameof(CanRun))] private async Task AddAsync()
    {
        long? added = null;
        await RunAsync(async () =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "添加字体来源", Multiselect = false };
            if (picker.ShowDialog() != true) return;
            var source = await Repository.AddSourceAsync(picker.FolderName); added = source.Id; Search = "";
        });
        if (added is long id && Owner.CanManage)
        { SelectedSource = Sources.FirstOrDefault(s => s.Id == id); await Owner.ScanSourceAsync(id); await RefreshAsync(); }
    }
    [RelayCommand(CanExecute = nameof(CanSource))] private Task RemoveAsync()
    {
        var source = SelectedSource!;
        return RunAsync(async () =>
        {
            if (await dialogs.ConfirmAsync("移除来源", $"移除 {source.Path} 的索引位置？\n原文件、标签和临时字体状态会保留。", "移除"))
                await Repository.RemoveSourceAsync(source.Id);
        });
    }
    [RelayCommand(CanExecute = nameof(CanSource))] private Task ScanAsync() => ScanCore(false);
    [RelayCommand(CanExecute = nameof(CanSource))] private Task VerifyAsync() => ScanCore(true);
    private async Task ScanCore(bool verify)
    { try { await Owner.ScanSourceAsync(SelectedSource!.Id, verify); await RefreshAsync(); } catch (Exception ex) { Error = ex.Message; } }
    [RelayCommand(CanExecute = nameof(CanRun))] private async Task ScanAllAsync()
    { try { await Owner.ScanSourceAsync(null); await RefreshAsync(); } catch (Exception ex) { Error = ex.Message; } }
    [RelayCommand(CanExecute = nameof(CanSource))] private void OpenFolder()
    {
        try
        {
            if (!Directory.Exists(SelectedSource!.Path)) throw new DirectoryNotFoundException("目录当前不可用，请检查磁盘或网络连接。");
            Process.Start(new ProcessStartInfo(SelectedSource.Path) { UseShellExecute = true });
        }
        catch (Exception ex) { Error = "无法打开目录：" + ex.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanSource))] private async Task ViewAsync()
    { await Owner.ShowSourceInLibraryAsync(SelectedSource!.Id); viewFonts(); }
    public override void Dispose() { searchCancellation?.Cancel(); searchCancellation?.Dispose(); searchCancellation = null; base.Dispose(); }
}
