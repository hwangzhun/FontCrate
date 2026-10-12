using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FontCrate.Core;

namespace FontCrate.App;

public partial class AnnotationPanelViewModel : ObservableObject
{
    private readonly Repository repository;
    private readonly bool collections;
    private readonly Func<Task> refresh;
    private readonly Func<FaceRow[]> selection;
    private readonly Func<NamedItem, Task<bool>> confirmDelete;
    private readonly NamedItem all;
    private bool syncing;
    private long? editId;
    public event Action? FilterChanged;
    public string Title => collections ? "合集" : "标签";
    public string SearchLabel => $"搜索{Title}名称";
    public string AssignmentLabel => $"选择要添加或移除的{Title}";
    public ObservableCollection<NamedItem> Items { get; } = [];
    public ObservableCollection<NamedItem> FilterItems { get; } = [];
    public ObservableCollection<NamedItem> VisibleItems { get; } = [];
    [ObservableProperty] private NamedItem? selectedFilter, selectedVisibleItem, assignmentItem;
    [ObservableProperty] private string searchText = "", editName = "", error = "", editorTitle = "";
    [ObservableProperty] private bool editing, busy, noMatches;
    public bool Ready => !Busy;
    public string ActiveFilterText => SelectedFilter?.Id is > 0 ? $"当前筛选：{SelectedFilter.Name}" : "";
    public bool HasActiveFilter => SelectedFilter?.Id is > 0;

    public AnnotationPanelViewModel(Repository repository, bool collections, Func<Task> refresh, Func<FaceRow[]> selection, Func<NamedItem, Task<bool>> confirmDelete)
    {
        this.repository = repository; this.collections = collections; this.refresh = refresh; this.selection = selection;
        this.confirmDelete = confirmDelete;
        all = new(0, $"全部{Title}"); SelectedFilter = all;
    }
    partial void OnSelectedFilterChanged(NamedItem? value)
    {
        OnPropertyChanged(nameof(ActiveFilterText)); OnPropertyChanged(nameof(HasActiveFilter));
        RenameCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged();
        if (!syncing) { RebuildVisibleItems(); FilterChanged?.Invoke(); }
    }
    partial void OnSelectedVisibleItemChanged(NamedItem? value)
    { if (!syncing && value is not null) SelectedFilter = value; }
    partial void OnAssignmentItemChanged(NamedItem? value) => NotifySelectionChanged();
    partial void OnSearchTextChanged(string value) => RebuildVisibleItems();
    partial void OnBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(Ready)); NewCommand.NotifyCanExecuteChanged(); RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); NotifySelectionChanged();
    }
    partial void OnEditingChanged(bool value)
    { SaveCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); }
    public void NotifySelectionChanged()
    { AddToFontsCommand.NotifyCanExecuteChanged(); RemoveFromFontsCommand.NotifyCanExecuteChanged(); }

    public void Refresh(IReadOnlyList<NamedItem> values)
    {
        long filterId = SelectedFilter?.Id ?? 0; long? assignmentId = AssignmentItem?.Id;
        syncing = true;
        try
        {
            Sync(Items, values);
            Sync(FilterItems, new[] { all }.Concat(values).ToArray());
            SelectedFilter = Items.FirstOrDefault(x => x.Id == filterId) ?? all;
            AssignmentItem = Items.FirstOrDefault(x => x.Id == assignmentId);
            RebuildVisibleItems();
        }
        finally { syncing = false; }
    }
    private void RebuildVisibleItems()
    {
        bool previous = syncing; syncing = true;
        try
        {
            var matches = Items.Where(x => x.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            Sync(VisibleItems, new[] { all }.Concat(matches).ToArray());
            SelectedVisibleItem = VisibleItems.FirstOrDefault(x => x.Id == SelectedFilter?.Id);
            NoMatches = matches.Length == 0;
        }
        finally { syncing = previous; }
    }
    // Keep unchanged instances while allowing renamed entries to move to their sorted position.
    private static void Sync(ObservableCollection<NamedItem> target, IReadOnlyList<NamedItem> desired)
    {
        var ids = desired.Select(x => x.Id).ToHashSet();
        foreach (var old in target.Where(x => !ids.Contains(x.Id)).ToArray()) target.Remove(old);
        for (int i = 0; i < desired.Count; i++)
        {
            var existing = target.FirstOrDefault(x => x.Id == desired[i].Id);
            if (existing is null) target.Insert(i, desired[i]);
            else
            {
                int index = target.IndexOf(existing);
                if (existing != desired[i]) target[index] = desired[i];
                if (index != i) target.Move(index, i);
            }
        }
    }
    private bool CanManage() => !Busy && SelectedFilter?.Id is > 0;
    private bool CanNew() => !Busy;
    private bool CanEdit() => !Busy && Editing;
    private bool CanApply() => !Busy && AssignmentItem?.Id is > 0 && selection().Length > 0;
    [RelayCommand(CanExecute = nameof(CanNew))]
    private void New() { editId = null; EditName = ""; Error = ""; EditorTitle = $"新建{Title}"; Editing = true; }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private void Rename()
    { editId = SelectedFilter!.Id; EditName = SelectedFilter.Name; Error = ""; EditorTitle = $"重命名{Title}"; Editing = true; }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Cancel() { Editing = false; EditName = ""; Error = ""; editId = null; }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveAsync()
    {
        if (!CanEdit()) return;
        Busy = true; Error = "";
        try
        {
            if (editId is long id) await repository.RenameNamedItemAsync(id, EditName, collections);
            else await repository.CreateNamedItemAsync(EditName, collections);
            Editing = false; EditName = ""; editId = null;
            await refresh();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task DeleteAsync()
    {
        if (!CanManage()) return;
        long id = SelectedFilter!.Id; Busy = true; Error = "";
        try
        {
            var item = (await repository.NamedItemCountsAsync(collections)).FirstOrDefault(x => x.Id == id);
            if (item is null) { await refresh(); return; }
            if (!await confirmDelete(item)) return;
            await repository.DeleteNamedItemAsync(id, collections);
            if (editId == id) { Editing = false; editId = null; EditName = ""; }
            await refresh();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanApply))] private Task AddToFontsAsync() => Apply(false);
    [RelayCommand(CanExecute = nameof(CanApply))] private Task RemoveFromFontsAsync() => Apply(true);
    private async Task Apply(bool remove)
    {
        if (!CanApply()) return;
        long id = AssignmentItem!.Id; var ids = selection().Select(x => x.Id).ToArray();
        Busy = true; Error = "";
        try { await repository.AnnotateByIdAsync(ids, id, collections, remove); await refresh(); }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
}
