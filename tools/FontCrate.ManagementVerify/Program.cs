using System.IO;
using System.Reflection;
using System.Xml.Linq;
using System.Windows.Markup;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using FontCrate.App;
using FontCrate.App.Views;
using FontCrate.Core;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        // A plain Application avoids running FontCrate's real startup when pumping the dispatcher.
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Assembly.Load("Wpf.Ui");
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppResources.xaml")!;
        var xml = XDocument.Load(resource); XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = xml.Root!.Element(presentation + "Application.Resources")!.Element(presentation + "ResourceDictionary")!;
        foreach (var attribute in xml.Root.Attributes().Where(a => a.IsNamespaceDeclaration)) dictionary.SetAttributeValue(attribute.Name, attribute.Value);
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        int result = 1; var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { await VerifyAsync(!args.Contains("--skip-layout")); result = 0; Console.WriteLine("Management verification passed (no system fonts changed)."); }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame); return result;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task VerifyAsync(bool verifyLayout)
    {
        string root = Path.Combine(Path.GetTempPath(), "FontCrate-management-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repo = new Repository(Path.Combine(root, "library.db")); await repo.InitializeAsync();
        var parser = new TestParser(); var scanner = new Scanner(repo, parser);
        foreach (var name in new[] { "First", "Second" })
        {
            string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".ttf"), name);
            var source = await repo.AddSourceAsync(folder); await scanner.ScanAsync(source, false, null, default);
        }
        var sources = await repo.SourcesAsync(); var faces = (await repo.QueryAsync(new())).Rows;
        var alpha = await repo.CreateNamedItemAsync("Alpha", false); var beta = await repo.CreateNamedItemAsync("Beta", false);
        var group = await repo.CreateNamedItemAsync("Group", true);
        await repo.AnnotateByIdAsync([faces[0].Id], alpha.Id, false);
        await repo.AnnotateByIdAsync([faces[1].Id], beta.Id, false);
        await repo.AnnotateByIdAsync([faces[0].Id], group.Id, true);
        using var previews = new TestPreview(); var dialogs = new ConfirmationService();
        var owner = new MainViewModel(repo, scanner, previews, new FontOperations(repo, new TestPlatform(root)), Dispatcher.CurrentDispatcher, dialogs);
        await owner.InitializeAsync();
        owner.SelectedSource = owner.SourceItems.Single(s => s.Id == sources[0].Id);
        owner.TagPanel.SelectedFilter = owner.TagPanel.Items.Single(s => s.Id == beta.Id);
        owner.Search = "unrelated library search";
        using var sourceManager = new SourcesManagementViewModel(owner, dialogs, () => { });
        using var categories = new AnnotationsManagementViewModel(owner, dialogs, () => { });
        await sourceManager.RefreshAsync(); await categories.Tags.RefreshAsync(); await categories.Collections.RefreshAsync();
        sourceManager.SelectedSource = sourceManager.Sources.Single(s => s.Id == sources[1].Id); await sourceManager.LoadFontsAsync();
        categories.Tags.SelectedItem = categories.Tags.Items.Single(i => i.Id == alpha.Id); await categories.Tags.LoadFontsAsync();
        Check(categories.Tags.Fonts.Count == 1 && categories.Collections.Fonts.Count == 1, "Independent association query failed");
        Check(owner.SelectedSource.Id == sources[0].Id && owner.TagPanel.SelectedFilter.Id == beta.Id && owner.Search.StartsWith("unrelated"), "Management changed library filters");
        Console.WriteLine("PASS independent source/category filters and association lists");
        categories.Tags.SetSelection([categories.Tags.Fonts[0]], categories.Tags.Fonts[0]);
        await repo.AnnotateByIdAsync([faces[1].Id, faces[1].Id], alpha.Id, false);
        await categories.Tags.RefreshAsync(); await Task.Delay(250);
        Check(categories.Tags.Fonts.Count == 2 && categories.Tags.SelectedFonts.Single().Id == faces[0].Id, "Refresh lost selection or duplicated membership");
        Check(owner.TagPanel.Items.Single(i => i.Id == alpha.Id).FaceCount == 2, "Library classification count did not refresh");
        Check(categories.Collections.Fonts.Count == 1, "Tag changes affected collections");
        Console.WriteLine("PASS cross-page counts, idempotent membership and stable selected fonts");
        categories.Tags.SelectedItem = categories.Tags.Items.Single(i => i.Id == alpha.Id);
        var stale = categories.Tags.LoadFontsAsync();
        categories.Tags.SelectedItem = categories.Tags.Items.Single(i => i.Id == beta.Id);
        var latest = categories.Tags.LoadFontsAsync(); await Task.WhenAll(stale, latest);
        Check(categories.Tags.Fonts.Single().Id == faces[1].Id, "Stale query overwrote current category");
        using (var picker = new FontPickerViewModel(owner, faces.Select(f => f.Id).ToHashSet()))
        { await picker.RefreshAsync(); Check(picker.Fonts.Count == 0, "Picker included existing members"); }
        Console.WriteLine("PASS rapid category changes and exclusion of existing members");
        File.WriteAllText(Path.Combine(sources[0].Path, "Unscanned.ttf"), "unscanned");
        File.WriteAllText(Path.Combine(sources[1].Path, "New.ttf"), "new");
        parser.Paths.Clear(); await owner.ScanSourceAsync(sources[1].Id);
        Check(parser.Paths.All(p => Path.GetDirectoryName(p) == sources[1].Path), "Scoped scan parsed another source");
        var summaries = await repo.SourceSummariesAsync();
        Check(summaries.Single(s => s.Id == sources[0].Id).FaceCount == 1 && summaries.Single(s => s.Id == sources[1].Id).FaceCount == 2, "Scoped scan changed wrong source counts");
        Check(owner.SelectedSource.Id == sources[0].Id, "Scoped scan changed library source filter");
        Console.WriteLine("PASS MainViewModel explicit source scan and preserved library selection");
        using var theme = new ThemeService(new SettingsStore(Path.Combine(root, "settings.json")));
        var window = new MainWindow(owner, theme, dialogs);
        using var sourcePage = new SourcesPage(owner, dialogs, () => { });
        using var categoryPage = new AnnotationsPage(owner, dialogs, () => { });
        var browser = new ManagementFontBrowser();
        Check(sourcePage.DataContext is SourcesManagementViewModel && categoryPage.DataContext is AnnotationsManagementViewModel, "Management page data contexts are incorrect");
        Console.WriteLine("PASS compiled management pages and shared font browser resources");
        if (verifyLayout)
        {
            VerifyLayout(sourcePage, "sources");
            VerifyLayout(categoryPage, "tags");
            Descendants(categoryPage).OfType<TabView>().Single().SelectedIndex = 1;
            VerifyLayout(categoryPage, "collections");
            Console.WriteLine("PASS full-height aligned management panes at compact and large sizes");
        }
        else Console.WriteLine("SKIP visual layout checks (--skip-layout; requires working WPF font rendering)");
        ContentDialog ActiveDialog() => (ContentDialog)typeof(ConfirmationService).GetField("active", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialogs)!;
        int saves = 0;
        var editing = ManagementDialogs.EditNameAsync(dialogs, "Test name", "", async name =>
        { await repo.CreateNamedItemAsync(name, false); saves++; });
        var editDialog = ActiveDialog();
        editDialog.TemplateButtonCommand.Execute(ContentDialogButton.Primary);
        await Task.Delay(50);
        Check(!editing.IsCompleted && saves == 0, "Invalid name closed or saved the dialog");
        ((Wpf.Ui.Controls.TextBox)((System.Windows.Controls.StackPanel)editDialog.Content).Children[1]).Text = "Gamma";
        editDialog.TemplateButtonCommand.Execute(ContentDialogButton.Primary); await editing;
        Check(saves == 1, "Valid name did not save once");
        var canceledEdit = ManagementDialogs.EditNameAsync(dialogs, "Cancel name", "Canceled", _ => { saves++; return Task.CompletedTask; });
        dialogs.Cancel(); await canceledEdit;
        Check(saves == 1, "Canceled name dialog saved changes");
        var picking = ManagementDialogs.ChooseFontsAsync(dialogs, owner, [], "Alpha");
        for (int i = 0; i < 100 && !dialogs.IsOpen; i++) await Task.Delay(10);
        Check(dialogs.IsOpen, "Font picker failed to open");
        ActiveDialog().TemplateButtonCommand.Execute(ContentDialogButton.Primary);
        Check(!picking.IsCompleted, "Empty font selection closed the picker");
        dialogs.Cancel(); Check((await picking).Length == 0, "Canceled font picker returned selected fonts");
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledSave = ManagementDialogs.EditNameAsync(dialogs, "Cancel pending save", "Pending", async _ =>
        { writeStarted.SetResult(); await finishWrite.Task; throw new IOException("Simulated save failure"); });
        ActiveDialog().TemplateButtonCommand.Execute(ContentDialogButton.Primary); await writeStarted.Task;
        dialogs.Cancel(); Check(!canceledSave.IsCompleted, "Cancellation did not wait for in-flight save");
        finishWrite.SetResult(); await canceledSave.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!dialogs.IsOpen, "Canceled failed save left dialog open");
        var canceledLoading = ManagementDialogs.ChooseFontsAsync(dialogs, owner, [], "Canceled while loading");
        dialogs.Cancel(); Check((await canceledLoading).Length == 0 && !dialogs.IsOpen, "Canceled loading reopened a picker");
        Console.WriteLine("PASS native dialog name validation, successful save and canceled/empty font selection");
        window.Close(); await owner.ShutdownAsync();
        // Clear only this verifier's pool before removing its isolated database.
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "library.db"), ForeignKeys = true }.ToString();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(cs)) Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        Directory.Delete(root, true);
        VerifyPaneGeometry((System.Windows.Controls.Grid)sourcePage.FindName("Workspace"));
        var categoryTabs = (TabView)categoryPage.FindName("CategoryTabs");
        Check(ReferenceEquals(categoryTabs.Style, categoryTabs.FindResource(typeof(System.Windows.Controls.TabControl))), "Category tabs do not use the WPF-UI TabControl style");
        foreach (var tab in categoryTabs.Items.OfType<TabViewItem>())
            Check(ReferenceEquals(tab.Style, tab.FindResource(typeof(System.Windows.Controls.TabItem))), "Category tab header does not use the WPF-UI TabItem style");
        Console.WriteLine("PASS WPF-UI tab control and header styles");
        foreach (var tab in categoryTabs.Items.OfType<TabViewItem>())
            VerifyPaneGeometry((System.Windows.Controls.Grid)((CategoryManagementControl)tab.Content).FindName("Workspace"));
        Console.WriteLine("PASS source, tag and collection card geometry without font rendering");
    }
    private static void VerifyPaneGeometry(System.Windows.Controls.Grid workspace)
    {
        var cards = workspace.Children.OfType<Card>().ToArray();
        var contents = cards.Select(c => c.Content).ToArray();
        // A fresh root avoids the page layout manager restoring old arrange bounds.
        var isolated = new System.Windows.Controls.Grid();
        foreach (var column in workspace.ColumnDefinitions)
            isolated.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            { Width = column.Width, MinWidth = column.MinWidth, MaxWidth = column.MaxWidth });
        foreach (var card in cards)
        {
            workspace.Children.Remove(card);
            isolated.Children.Add(card);
        }
        try
        {
            // Exercise the actual WPF-UI card templates without Wine's font shaper.
            foreach (var card in cards) card.Content = new System.Windows.Controls.Grid();
            foreach (var size in new[] { new Size(800, 500), new Size(1200, 760), new Size(800, 500) })
            {
                isolated.Measure(size);
                isolated.Arrange(new Rect(size));
                isolated.UpdateLayout();
                foreach (var card in cards)
                {
                    var bounds = card.TransformToAncestor(isolated).TransformBounds(new Rect(card.RenderSize));
                    Check(Math.Abs(bounds.Top) < 1 && Math.Abs(bounds.Bottom - size.Height) < 1,
                        $"{card.Name}: card bounds {bounds} do not fill workspace {isolated.RenderSize} at {size}");
                    var border = (System.Windows.Controls.Border)card.Template.FindName("ContentBorder", card);
                    Check(Math.Abs(border.ActualHeight - card.ActualHeight) < 1,
                        $"{card.Name}: WPF-UI content border does not fill the card");
                }
            }
        }
        finally
        {
            for (int i = 0; i < cards.Length; i++) cards[i].Content = contents[i];
            foreach (var card in cards)
            {
                isolated.Children.Remove(card);
                workspace.Children.Add(card);
            }
        }
    }
    private static void VerifyLayout(System.Windows.Controls.Page page, string name)
    {
        Check(!System.Windows.Controls.ScrollViewer.GetCanContentScroll(page), $"{name}: outer navigation scrolling is enabled");
        foreach (var size in new[] { new Size(800, 560), new Size(1200, 820), new Size(800, 560) })
        {
            page.Measure(size);
            page.Arrange(new Rect(size));
            page.UpdateLayout();
            var panes = Descendants(page).OfType<Card>().Where(c => c.Name is "ListCard" or "DetailCard").ToArray();
            Check(panes.Length == 2, $"{name}: missing visible management panes");
            var list = panes.Single(c => c.Name == "ListCard");
            var detail = panes.Single(c => c.Name == "DetailCard");
            var workspace = (System.Windows.Controls.Grid)list.Parent;
            Check(double.IsNaN(workspace.Height), $"{name}: workspace uses a fixed height");
            Check(Math.Abs(list.ActualHeight - workspace.ActualHeight) < 1 && Math.Abs(detail.ActualHeight - workspace.ActualHeight) < 1,
                $"{name}: panes do not fill the workspace at {size}");
            var listBounds = list.TransformToAncestor(page).TransformBounds(new Rect(list.RenderSize));
            var detailBounds = detail.TransformToAncestor(page).TransformBounds(new Rect(detail.RenderSize));
            Check(Math.Abs(listBounds.Top - detailBounds.Top) < 1 && Math.Abs(listBounds.Bottom - detailBounds.Bottom) < 1,
                $"{name}: pane edges are not aligned at {size}");
            Check(listBounds.Bottom <= size.Height && detailBounds.Right <= size.Width && detailBounds.Left >= listBounds.Right,
                $"{name}: panes overflow or stack at {size}");
            var selector = Descendants(list).OfType<Wpf.Ui.Controls.ListView>().Single();
            Check(selector.SelectionMode == System.Windows.Controls.SelectionMode.Single, $"{name}: sidebar allows multiple selection");
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private sealed class TestParser : IFontParser
    {
        public List<string> Paths { get; } = [];
        public Task<IReadOnlyList<ParsedFace>> ParseAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path); string name = Path.GetFileNameWithoutExtension(path);
            return Task.FromResult<IReadOnlyList<ParsedFace>>([new(0, name, "Regular", name, name, "1", 400, 5, false, false, [])]);
        }
    }
    private sealed class TestPreview : IPreviewService
    {
        public Task<PreviewResult> RenderAsync(PreviewRequest request, CancellationToken cancellationToken) => Task.FromResult(new PreviewResult(new byte[4], 1, 1, 4, [], request.Generation));
        public void ClearCache() { }
        public void Dispose() { }
    }
    private sealed class TestPlatform(string root) : IFontPlatform
    {
        public string TemporaryDirectory => Path.Combine(root, "temporary");
        public string UserFontsDirectory => Path.Combine(root, "installed");
        public int Load(string path) => throw new InvalidOperationException("Verifier must never load fonts");
        public bool Unload(string path) => throw new InvalidOperationException("Verifier must never unload fonts");
        public bool IsLoaded(string path) => false;
        public void NotifyChanged() { }
        public string? GetRegistration(string name) => null;
        public void SetRegistration(string name, string path) => throw new InvalidOperationException();
        public void RemoveRegistration(string name) => throw new InvalidOperationException();
        public Task<IReadOnlyList<SystemFont>> EnumerateAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SystemFont>>([]);
    }
}
