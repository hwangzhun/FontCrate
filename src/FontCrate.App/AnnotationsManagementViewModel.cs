using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FontCrate.Core;

namespace FontCrate.App;

public sealed class AnnotationsManagementViewModel : IDisposable
{
    public CategoryManagementViewModel Tags { get; }
    public AnnotationsManagementViewModel(MainViewModel owner, ConfirmationService dialogs, Action viewFonts)
    { Tags = new(owner, dialogs, viewFonts); }
    public void Dispose() { Tags.Dispose(); }
}

public partial class CategoryManagementViewModel : FontBrowserViewModel
{
    private readonly ConfirmationService dialogs;
    private readonly Action viewFonts;
    private IReadOnlyList<NamedItem> all = [];
    private CancellationTokenSource? searchCancellation;
    private bool syncingItems;
    public ObservableCollection<NamedItem> Items { get; } = [];
    [ObservableProperty] private NamedItem? selectedItem;
    [ObservableProperty] private string search = "";
    public string Kind => "标签";
    public string NewLabel => "新建" + Kind;
    public string Summary => $"{all.Count} 个{Kind}";
    public bool HasItem => SelectedItem is not null;
    public bool NoItems => Items.Count == 0;
    protected override FaceQuery? CurrentQuery => SelectedItem is null ? null : new(FontSearch,
        TagId: SelectedItem.Id);
    public CategoryManagementViewModel(MainViewModel owner, ConfirmationService dialogs, Action viewFonts) : base(owner)
    { this.dialogs = dialogs; this.viewFonts = viewFonts; }
    partial void OnSelectedItemChanged(NamedItem? oldValue, NamedItem? newValue)
    {
        OnPropertyChanged(nameof(HasItem));
        if (syncingItems) return;
        if (oldValue?.Id != newValue?.Id) { Fonts.Clear(); SetSelection([], null); }
        NotifyState(); ScheduleFonts();
    }
    partial void OnSearchChanged(string value) => _ = SearchAsync();
    private async Task SearchAsync()
    {
        searchCancellation?.Cancel(); searchCancellation?.Dispose(); searchCancellation = new(); var token = searchCancellation.Token;
        try { await Task.Delay(200, token); RebuildItems(); } catch (OperationCanceledException) { }
    }
    private void RebuildItems()
    {
        var selected = SelectedItem;
        syncingItems = true;
        try
        {
            SyncEntries(Items, all.Where(s => s.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray(), s => s.Id);
            SelectedItem = Items.FirstOrDefault(s => s.Id == selected?.Id) ?? Items.FirstOrDefault();
        }
        finally { syncingItems = false; }
        if (selected?.Id != SelectedItem?.Id) { Fonts.Clear(); SetSelection([], null); ScheduleFonts(); }
        OnPropertyChanged(nameof(NoItems)); OnPropertyChanged(nameof(Summary)); NotifyState();
    }
    protected override async Task RefreshEntriesAsync(long generation)
    { var entries = await Repository.NamedItemCountsAsync(); if (CurrentRefresh(generation)) { all = entries; RebuildItems(); } }
    private bool CanRun() => CanInteract;
    private bool CanItem() => CanInteract && HasItem;
    private bool CanRemove() => CanItem() && SelectedFonts.Length > 0;
    protected override void NotifyCommands()
    {
        NewCommand.NotifyCanExecuteChanged(); RenameCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged();
        AddFontsCommand.NotifyCanExecuteChanged(); RemoveFontsCommand.NotifyCanExecuteChanged(); ViewCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanRun))] private Task NewAsync() => RunAsync(async () =>
    {
        long? created = null;
        await ManagementDialogs.EditNameAsync(dialogs, "新建" + Kind, "", async name =>
        { var item = await Repository.CreateNamedItemAsync(name); created = item.Id; });
        if (created is long id)
        {
            Search = ""; var entries = await Repository.NamedItemCountsAsync(); all = entries; RebuildItems();
            SelectedItem = Items.FirstOrDefault(i => i.Id == id);
        }
    });
    [RelayCommand(CanExecute = nameof(CanItem))] private Task RenameAsync()
    {
        var item = SelectedItem!;
        return RunAsync(() => ManagementDialogs.EditNameAsync(dialogs, "重命名" + Kind, item.Name,
            name => Repository.RenameNamedItemAsync(item.Id, name)));
    }
    [RelayCommand(CanExecute = nameof(CanItem))] private Task DeleteAsync()
    {
        var item = SelectedItem!;
        return RunAsync(async () =>
        {
            var current = (await Repository.NamedItemCountsAsync()).FirstOrDefault(i => i.Id == item.Id);
            if (current is null) return;
            if (await dialogs.ConfirmAsync("删除" + Kind, $"删除“{current.Name}”及其 {current.FaceCount} 个字体关联？\n字体文件与其他分类关联会保留。", "删除"))
                await Repository.DeleteNamedItemAsync(item.Id);
        });
    }
    [RelayCommand(CanExecute = nameof(CanItem))] private Task AddFontsAsync()
    {
        var item = SelectedItem!;
        return RunAsync(async () =>
        {
            long cancellationVersion = dialogs.CancellationVersion;
            var members = await Repository.QueryAsync(new(TagId: item.Id));
            if (Owner.IsClosing || dialogs.CancellationVersion != cancellationVersion) return;
            var ids = await ManagementDialogs.ChooseFontsAsync(dialogs, Owner, members.Rows.Select(f => f.Id).ToHashSet(), item.Name);
            if (ids.Length > 0) await Repository.AnnotateByIdAsync(ids, item.Id);
        });
    }
    [RelayCommand(CanExecute = nameof(CanRemove))] private Task RemoveFontsAsync()
    {
        var item = SelectedItem!; var ids = SelectedFonts.Select(f => f.Id).ToArray();
        return RunAsync(async () =>
        {
            if (await dialogs.ConfirmAsync("移除关联", $"从“{item.Name}”移除所选 {ids.Length} 个字体关联？\n字体文件及其他分类关联会保留。", "移除关联"))
                await Repository.AnnotateByIdAsync(ids, item.Id, remove: true);
        });
    }
    private bool CanExport() => CanItem() && SelectedItem!.FaceCount > 0;
    [RelayCommand(CanExecute = nameof(CanExport))] private Task ExportAsync() =>
        Owner.ExportTagAsync(SelectedItem!.Id, SelectedItem.Name);
    [RelayCommand(CanExecute = nameof(CanItem))] private async Task ViewAsync()
    { await Owner.ShowCategoryInLibraryAsync(SelectedItem!.Id); viewFonts(); }
    public override void Dispose() { searchCancellation?.Cancel(); searchCancellation?.Dispose(); searchCancellation = null; base.Dispose(); }
}

public sealed class FontPickerViewModel(MainViewModel owner, HashSet<string> excluded) : FontBrowserViewModel(owner)
{
    public override string SearchPlaceholder => "搜索已有字体（已关联项已排除）";
    protected override FaceQuery CurrentQuery => new(FontSearch);
    protected override IEnumerable<FaceRow> FilterFonts(IEnumerable<FaceRow> fonts) => fonts.Where(f => !excluded.Contains(f.Id));
}
