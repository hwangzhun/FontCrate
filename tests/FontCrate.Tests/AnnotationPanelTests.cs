using FontCrate.App;
using FontCrate.Core;
using Xunit;

namespace FontCrate.Tests;

public class AnnotationPanelTests
{
    private static AnnotationPanelViewModel Panel(Repository repository, bool collections = false,
        Func<FaceRow[]>? selection = null, Func<NamedItem, Task<bool>>? confirmation = null)
    {
        AnnotationPanelViewModel panel = null!;
        panel = new(repository, collections, async () => panel.Refresh(await repository.NamedItemCountsAsync(collections)),
            selection ?? (() => []), confirmation ?? (_ => Task.FromResult(true)));
        return panel;
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SearchPreservesHiddenFilterAndAlwaysKeepsAll(bool collections)
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        var chinese = await s.Repository.CreateNamedItemAsync("中文项目", collections);
        await s.Repository.CreateNamedItemAsync("English", collections);
        var panel = Panel(s.Repository, collections); panel.Refresh(await s.Repository.NamedItemCountsAsync(collections));
        panel.SelectedVisibleItem = panel.Items.Single(x => x.Id == chinese.Id);
        int changes = 0; panel.FilterChanged += () => changes++;
        panel.SearchText = "ENGLISH";
        Assert.Equal(chinese.Id, panel.SelectedFilter!.Id); Assert.Null(panel.SelectedVisibleItem);
        Assert.Equal(new[] { "全部" + panel.Title, "English" }, panel.VisibleItems.Select(x => x.Name));
        panel.SearchText = "不存在";
        Assert.True(panel.NoMatches); Assert.Equal(0, Assert.Single(panel.VisibleItems).Id); Assert.Equal(0, changes);
        panel.SearchText = "中文";
        Assert.Equal(chinese.Id, panel.SelectedVisibleItem!.Id); Assert.False(panel.NoMatches);
        panel.SelectedVisibleItem = panel.VisibleItems[0];
        Assert.Equal(0, panel.SelectedFilter!.Id); Assert.Equal(1, changes);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FailedEditKeepsInputAndCancelDoesNotWrite(bool collections)
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        await s.Repository.CreateNamedItemAsync("Existing", collections);
        var panel = Panel(s.Repository, collections); panel.Refresh(await s.Repository.NamedItemCountsAsync(collections));
        Assert.False(panel.RenameCommand.CanExecute(null)); Assert.False(panel.DeleteCommand.CanExecute(null));
        panel.NewCommand.Execute(null); panel.EditName = "Existing"; await panel.SaveCommand.ExecuteAsync(null);
        Assert.True(panel.Editing); Assert.Equal("Existing", panel.EditName); Assert.NotEmpty(panel.Error); Assert.False(panel.Busy);
        panel.CancelCommand.Execute(null);
        Assert.False(panel.Editing); Assert.Empty(panel.Error); Assert.Single(await s.Repository.NamedItemsAsync(collections));
        panel.NewCommand.Execute(null); panel.EditName = " 新建 "; await panel.SaveCommand.ExecuteAsync(null);
        Assert.False(panel.Editing); Assert.Equal(2, panel.Items.Count); Assert.Contains(panel.Items, x => x.Name == "新建" && x.FaceCount == 0);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RenameReordersItemsAndKeepsFilterAndAssignment(bool collections)
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        var a = await s.Repository.CreateNamedItemAsync("A", collections); await s.Repository.CreateNamedItemAsync("M", collections);
        var panel = Panel(s.Repository, collections); panel.Refresh(await s.Repository.NamedItemCountsAsync(collections));
        panel.SelectedVisibleItem = panel.Items[0]; panel.AssignmentItem = panel.Items[0];
        panel.RenameCommand.Execute(null); panel.EditName = "Z"; await panel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "M", "Z" }, panel.Items.Select(x => x.Name));
        Assert.Equal(new[] { "全部" + panel.Title, "M", "Z" }, panel.VisibleItems.Select(x => x.Name));
        Assert.Equal(a.Id, panel.SelectedFilter!.Id); Assert.Equal("Z", panel.SelectedVisibleItem!.Name);
        Assert.Equal(a.Id, panel.AssignmentItem!.Id); Assert.Equal("Z", panel.AssignmentItem.Name);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task DeleteConfirmationControlsRemovalAndActiveFilterReset(bool collections)
    {
        using var s = new Sandbox(); var face = await s.IndexAsync();
        var item = await s.Repository.CreateNamedItemAsync("Delete", collections);
        await s.Repository.AnnotateByIdAsync([face.Id], item.Id, collections);
        bool approved = false; NamedItem? confirmed = null;
        var panel = Panel(s.Repository, collections, confirmation: x => { confirmed = x; return Task.FromResult(approved); });
        panel.Refresh(await s.Repository.NamedItemCountsAsync(collections)); panel.SelectedVisibleItem = panel.Items[0]; panel.AssignmentItem = panel.Items[0];
        await panel.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(1, confirmed!.FaceCount); Assert.Single(panel.Items); Assert.Equal(item.Id, panel.SelectedFilter!.Id);
        approved = true; await panel.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(panel.Items); Assert.Equal(0, panel.SelectedFilter!.Id); Assert.Equal(0, panel.SelectedVisibleItem!.Id);
        Assert.Null(panel.AssignmentItem); Assert.True(File.Exists(face.Path)); Assert.False(panel.DeleteCommand.CanExecute(null));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task MembershipCommandsRequireFontsAndTargetAndRefreshCounts(bool collections)
    {
        using var s = new Sandbox(); var face = await s.IndexAsync(); await s.Repository.CreateNamedItemAsync("Membership", collections);
        FaceRow[] selection = []; var panel = Panel(s.Repository, collections, () => selection);
        panel.Refresh(await s.Repository.NamedItemCountsAsync(collections));
        Assert.False(panel.AddToFontsCommand.CanExecute(null)); panel.AssignmentItem = panel.Items[0];
        Assert.False(panel.AddToFontsCommand.CanExecute(null)); selection = [face]; panel.NotifySelectionChanged();
        Assert.True(panel.AddToFontsCommand.CanExecute(null)); await panel.AddToFontsCommand.ExecuteAsync(null);
        Assert.Equal(1, panel.AssignmentItem!.FaceCount); await panel.RemoveFromFontsCommand.ExecuteAsync(null);
        Assert.Equal(0, panel.AssignmentItem!.FaceCount); Assert.Single(panel.Items);
    }
    [Fact]
    public async Task RefreshRestoresSelectionsWhenBoundControlsClearReplacedItems()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        var item = await s.Repository.CreateNamedItemAsync("Before", false);
        var panel = Panel(s.Repository); panel.Refresh(await s.Repository.NamedItemCountsAsync(false));
        panel.SelectedVisibleItem = panel.Items[0]; panel.AssignmentItem = panel.Items[0];
        // Emulate WPF controls clearing selection when their selected object is replaced.
        panel.Items.CollectionChanged += (_, _) => panel.AssignmentItem = null;
        panel.VisibleItems.CollectionChanged += (_, _) => panel.SelectedVisibleItem = null;
        await s.Repository.RenameNamedItemAsync(item.Id, "After", false);
        panel.Refresh(await s.Repository.NamedItemCountsAsync(false));
        Assert.Equal(item.Id, panel.AssignmentItem!.Id); Assert.Equal("After", panel.AssignmentItem.Name);
        Assert.Equal(item.Id, panel.SelectedFilter!.Id); Assert.Equal("After", panel.SelectedVisibleItem!.Name);
    }
    [Fact]
    public async Task BusyStateBlocksDuplicateSubmissionUntilRefreshCompletes()
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var panel = new AnnotationPanelViewModel(s.Repository, false, async () => { entered.SetResult(); await release.Task; }, () => [], _ => Task.FromResult(true));
        panel.NewCommand.Execute(null); panel.EditName = "Once";
        var write = panel.SaveCommand.ExecuteAsync(null); await entered.Task;
        Assert.True(panel.Busy); Assert.False(panel.Ready); Assert.False(panel.NewCommand.CanExecute(null));
        Assert.False(panel.SaveCommand.CanExecute(null)); Assert.False(panel.CancelCommand.CanExecute(null));
        await panel.SaveCommand.ExecuteAsync(null); Assert.Single(await s.Repository.NamedItemsAsync(false));
        release.SetResult(); await write; Assert.True(panel.Ready);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task PendingConfirmationBlocksDuplicateDeletionAndCancellationKeepsData(bool collections)
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        var item = await s.Repository.CreateNamedItemAsync("Keep", collections);
        var entered = new TaskCompletionSource(); var answer = new TaskCompletionSource<bool>();
        int prompts = 0;
        var panel = Panel(s.Repository, collections, confirmation: _ =>
        { prompts++; entered.TrySetResult(); return answer.Task; });
        panel.Refresh(await s.Repository.NamedItemCountsAsync(collections)); panel.SelectedVisibleItem = panel.Items.Single();
        var pending = panel.DeleteCommand.ExecuteAsync(null); await entered.Task;
        Assert.True(panel.Busy); Assert.False(panel.DeleteCommand.CanExecute(null));
        await panel.DeleteCommand.ExecuteAsync(null); Assert.Equal(1, prompts);
        answer.SetResult(false); await pending;
        Assert.Equal(item.Id, Assert.Single(await s.Repository.NamedItemsAsync(collections)).Id);
        Assert.Equal(item.Id, panel.SelectedFilter!.Id); Assert.False(panel.Busy);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LibraryFilterChoicesAreIndependentOfManagerSearch(bool collections)
    {
        using var s = new Sandbox(); await s.Repository.InitializeAsync();
        var item = await s.Repository.CreateNamedItemAsync("Visible in library", collections);
        var panel = Panel(s.Repository, collections); panel.Refresh(await s.Repository.NamedItemCountsAsync(collections));
        panel.SearchText = "no match";
        Assert.Single(panel.VisibleItems);
        panel.SelectedFilter = panel.FilterItems.Single(x => x.Id == item.Id);
        Assert.Equal(item.Id, panel.SelectedFilter.Id); Assert.Null(panel.SelectedVisibleItem);
        Assert.Equal(2, panel.FilterItems.Count);
        panel.SearchText = "";
        Assert.Equal(item.Id, panel.SelectedVisibleItem!.Id);
    }

}
