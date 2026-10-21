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
        var alpha = await repo.CreateNamedItemAsync("Alpha"); var beta = await repo.CreateNamedItemAsync("Beta");
        await repo.AnnotateByIdAsync([faces[0].Id], alpha.Id);
        await repo.AnnotateByIdAsync([faces[1].Id], beta.Id);
        using var previews = new TestPreview(); var dialogs = new ConfirmationService();
        var owner = new MainViewModel(repo, scanner, previews, new FontOperations(repo, new TestPlatform(root)), Dispatcher.CurrentDispatcher, dialogs);
        await owner.InitializeAsync();
        // Exercise the actual template with data: an empty-library layout cannot catch
        // Run.Text attempting to write back to the computed FaceRow.FileType property.
        var bindingPage = new FontLibraryPage(owner);
        var bindingGrid = (System.Windows.Controls.DataGrid)bindingPage.FindName("FontList");
        var bindingColumn = (System.Windows.Controls.DataGridTemplateColumn)bindingGrid.Columns[0];
        var cell = (System.Windows.Controls.StackPanel)bindingColumn.CellTemplate.LoadContent();
        var metadata = cell.Children.OfType<System.Windows.Controls.TextBlock>().ElementAt(1);
        var runs = metadata.Inlines.OfType<System.Windows.Documents.Run>().ToArray();
        foreach (var face in new[] { faces[0], faces[1] with { Style = "Recycled style" } })
        {
            cell.DataContext = face;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            runs[0].GetBindingExpression(System.Windows.Documents.Run.TextProperty)!.UpdateTarget();
            runs[2].GetBindingExpression(System.Windows.Documents.Run.TextProperty)!.UpdateTarget();
            Check(runs[0].Text == face.FileType && runs[2].Text == face.Style,
                "Font metadata binding failed on initial load or recycled data context");
        }
        Console.WriteLine("PASS populated font metadata template and recycled data context");
        Check(!owner.LibraryLoading && !owner.ShowLibraryMessage && !owner.HasFilters, "Loaded library state is incorrect");
        owner.ListPreviewText = "Independent list sample";
        owner.PreviewPresetCommand.Execute("List");
        Check(owner.PreviewText == owner.ListPreviewText, "Copy list preview failed");
        owner.ListPreviewText = "Changed list sample";
        Check(owner.PreviewText != owner.ListPreviewText, "Preview unexpectedly remained synchronized");
        owner.PreviewPresetCommand.Execute("Chinese");
        Check(owner.PreviewText.Contains("字体"), "Chinese preview preset failed");
        owner.CommitPreviewSize("72"); owner.CommitPreviewSize("NaN");
        Check(owner.FontSize == 72, "Invalid font size did not retain previous value");
        Console.WriteLine("PASS library loading state, independent preview presets and size validation");
        owner.SelectedSource = owner.SourceItems.Single(s => s.Id == sources[0].Id);
        owner.TagPanel.SelectedFilter = owner.TagPanel.Items.Single(s => s.Id == beta.Id);
        owner.Search = "unrelated library search";
        using var sourceManager = new SourcesManagementViewModel(owner, dialogs, () => { });
        using var categories = new AnnotationsManagementViewModel(owner, dialogs, () => { });
        await sourceManager.RefreshAsync(); await categories.Tags.RefreshAsync();
        sourceManager.SelectedSource = sourceManager.Sources.Single(s => s.Id == sources[1].Id); await sourceManager.LoadFontsAsync();
        categories.Tags.SelectedItem = categories.Tags.Items.Single(i => i.Id == alpha.Id); await categories.Tags.LoadFontsAsync();
        Check(categories.Tags.Fonts.Count == 1, "Independent association query failed");
        Check(owner.SelectedSource.Id == sources[0].Id && owner.TagPanel.SelectedFilter.Id == beta.Id && owner.Search.StartsWith("unrelated"), "Management changed library filters");
        Console.WriteLine("PASS independent source/category filters and association lists");
        categories.Tags.SetSelection([categories.Tags.Fonts[0]], categories.Tags.Fonts[0]);
        await repo.AnnotateByIdAsync([faces[1].Id, faces[1].Id], alpha.Id);
        await categories.Tags.RefreshAsync(); await Task.Delay(250);
        Check(categories.Tags.Fonts.Count == 2 && categories.Tags.SelectedFonts.Single().Id == faces[0].Id, "Refresh lost selection or duplicated membership");
        Check(owner.TagPanel.Items.Single(i => i.Id == alpha.Id).FaceCount == 2, "Library classification count did not refresh");
        Console.WriteLine("PASS cross-page counts, idempotent membership and stable selected fonts");
        categories.Tags.SelectedItem = categories.Tags.Items.Single(i => i.Id == alpha.Id);
        var stale = categories.Tags.LoadFontsAsync();
        categories.Tags.SelectedItem = categories.Tags.Items.Single(i => i.Id == beta.Id);
        var latest = categories.Tags.LoadFontsAsync(); await Task.WhenAll(stale, latest);
        Check(categories.Tags.Fonts.Single().Id == faces[1].Id, "Stale query overwrote current category");
        using (var picker = new FontPickerViewModel(owner, faces.Select(f => f.Id).ToHashSet()))
        { await picker.RefreshAsync(); Check(picker.Fonts.Count == 0, "Picker included existing members"); }
        var disposedPicker = new FontPickerViewModel(owner, []);
        disposedPicker.SetSelection([faces[0]], faces[0]);
        disposedPicker.Dispose();
        // WPF may deliver Unloaded after ContentDialog.ShowAsync has completed.
        // This sequence used to cancel an already disposed CancellationTokenSource.
        disposedPicker.SuspendPreview(); disposedPicker.ResumePreview(); disposedPicker.Dispose();
        await disposedPicker.LoadFontsAsync();
        Console.WriteLine("PASS late preview lifecycle callbacks after picker disposal");
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
        if (verifyLayout) window.Show();
        using var sourcePage = new SourcesPage(owner, dialogs, () => { });
        using var categoryPage = new AnnotationsPage(owner, dialogs, () => { });
        var browser = new ManagementFontBrowser();
        Check(sourcePage.DataContext is SourcesManagementViewModel && categoryPage.DataContext is AnnotationsManagementViewModel, "Management page data contexts are incorrect");
        Console.WriteLine("PASS compiled management pages and shared font browser resources");
        if (verifyLayout)
        {
            VerifyLayout(sourcePage, "sources");
            VerifyLayout(categoryPage, "tags");
            Console.WriteLine("PASS full-height aligned management panes at compact and large sizes");
        }
        else Console.WriteLine("SKIP visual layout checks (--skip-layout; requires working WPF font rendering)");
        ContentDialog ActiveDialog() => (ContentDialog)typeof(ConfirmationService).GetField("active", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialogs)!;
        int saves = 0;
        var editing = ManagementDialogs.EditNameAsync(dialogs, "Test name", "", async name =>
        { await repo.CreateNamedItemAsync(name); saves++; });
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
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var accepted = ManagementDialogs.ChooseFontsAsync(dialogs, owner, [], "Lifecycle regression");
            for (int i = 0; i < 100 && !dialogs.IsOpen; i++) await Task.Delay(10);
            Check(dialogs.IsOpen, "Repeated font picker failed to open");
            var pickerDialog = ActiveDialog();
            var pickerBrowser = (ManagementFontBrowser)pickerDialog.Content;
            var pickerModel = (FontPickerViewModel)pickerBrowser.DataContext;
            var pickerList = (Wpf.Ui.Controls.ListView)pickerBrowser.FindName("FontList");
            pickerList.SelectedItems.Add(pickerModel.Fonts.Single(f => f.Id == faces[0].Id));
            pickerDialog.TemplateButtonCommand.Execute(ContentDialogButton.Primary);
            var selectedIds = await accepted;
            Check(selectedIds.SequenceEqual(new[] { faces[0].Id }), "Picker lost accepted selection");
            Check(pickerBrowser.DataContext is null, "Completed picker retained a disposed model");
            pickerBrowser.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            pickerModel.SuspendPreview();
            await repo.AnnotateByIdAsync(selectedIds, beta.Id);
            await categories.Tags.RefreshAsync();
            Check((await repo.QueryAsync(new(TagId: beta.Id))).Rows.Count == 2, "Accepted fonts were not saved exactly once");
        }
        owner.ClearFiltersCommand.Execute(null);
        owner.SelectedFaces = faces;
        await owner.AddSelectionToTagAsync(beta.Id);
        Check(owner.TagPanel.Items.Single(t => t.Id == beta.Id).FaceCount == 2, "Library batch assignment did not refresh tag counts");
        Check(owner.SelectedFaces.Select(f => f.Id).ToHashSet().SetEquals(faces.Select(f => f.Id)), "Library assignment lost multi-selection");
        await owner.AddSelectionToTagAsync(long.MaxValue);
        Check(owner.Status.Contains("已不存在"), "Invalid tag failed without visible feedback");
        var beforeNewTag = (await repo.NamedItemsAsync()).Count;
        var canceledNewTag = owner.NewTagForSelectionAsync();
        dialogs.Cancel(); await canceledNewTag;
        Check((await repo.NamedItemsAsync()).Count == beforeNewTag, "Canceled library tag creation wrote data");
        var newLibraryTag = owner.NewTagForSelectionAsync();
        var newTagDialog = ActiveDialog();
        ((Wpf.Ui.Controls.TextBox)((System.Windows.Controls.StackPanel)newTagDialog.Content).Children[1]).Text = "Library selection";
        newTagDialog.TemplateButtonCommand.Execute(ContentDialogButton.Primary); await newLibraryTag;
        Check((await repo.NamedItemCountsAsync()).Single(t => t.Name == "Library selection").FaceCount == 2,
            "Library new-tag action did not attach the selected fonts");
        if (verifyLayout)
        {
            // Measure a standalone page: hosted window sizes can be constrained by the runner monitor.
            var libraryPage = new FontLibraryPage(owner);
            var workspace = (System.Windows.Controls.Grid)libraryPage.FindName("LibraryWorkspace");
            Check(workspace.ColumnDefinitions[0].Width == new GridLength(7, GridUnitType.Star) &&
                workspace.ColumnDefinitions[2].Width == new GridLength(3, GridUnitType.Star), "Library does not default to 70/30");
            libraryPage.Measure(new Size(1600, 900));
            libraryPage.Arrange(new Rect(new Size(1600, 900))); libraryPage.UpdateLayout();
            double contentWidth = workspace.ActualWidth - workspace.ColumnDefinitions[1].ActualWidth;
            Check(Math.Abs(workspace.ColumnDefinitions[2].ActualWidth / contentWidth - .3) < .01, $"Library preview is not 30% at a wide size: {workspace.ColumnDefinitions[2].ActualWidth}/{contentWidth}");
            libraryPage.Measure(new Size(1050, 700));
            libraryPage.Arrange(new Rect(new Size(1050, 700))); libraryPage.UpdateLayout();
            Check(workspace.ColumnDefinitions[0].ActualWidth >= 320 && workspace.ColumnDefinitions[2].ActualWidth >= 320,
                "Library panes violate minimum widths");
            Check(workspace.Children.OfType<System.Windows.Controls.GridSplitter>().Any(), "Library splitter was removed");
            var fontList = (System.Windows.Controls.DataGrid)libraryPage.FindName("FontList");
            fontList.SelectedItems.Clear();
            fontList.SelectedItems.Add(fontList.Items[0]); fontList.SelectedItems.Add(fontList.Items[1]);
            fontList.ScrollIntoView(fontList.Items[1]); fontList.UpdateLayout();
            var selectedRow = (System.Windows.Controls.DataGridRow)fontList.ItemContainerGenerator.ContainerFromItem(fontList.Items[1]);
            selectedRow.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
            Check(fontList.SelectedItems.Count == 2, "Right-click on selected row lost multi-selection");
            fontList.ScrollIntoView(fontList.Items[2]); fontList.UpdateLayout();
            var otherRow = (System.Windows.Controls.DataGridRow)fontList.ItemContainerGenerator.ContainerFromItem(fontList.Items[2]);
            otherRow.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
            Check(fontList.SelectedItems.Count == 1 && Equals(fontList.SelectedItem, fontList.Items[2]),
                "Right-click on unselected row did not replace selection");
            var selectionIds = owner.SelectedFaces.Select(f => f.Id).ToArray();
            var previewText = owner.PreviewText;
            libraryPage.Measure(new Size(800, 700));
            libraryPage.Arrange(new Rect(new Size(800, 700))); libraryPage.UpdateLayout();
            var listPane = (FrameworkElement)libraryPage.FindName("ListPane");
            var previewPane = (FrameworkElement)libraryPage.FindName("PreviewPane");
            Check(listPane.Visibility == Visibility.Visible && previewPane.Visibility == Visibility.Collapsed, "Compact library did not default to list");
            ((Wpf.Ui.Controls.Button)libraryPage.FindName("PreviewToggle")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            libraryPage.UpdateLayout();
            Check(listPane.Visibility == Visibility.Collapsed && previewPane.Visibility == Visibility.Visible, "Compact preview switch failed");
            ((Wpf.Ui.Controls.Button)libraryPage.FindName("PreviewBack")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            libraryPage.UpdateLayout();
            Check(listPane.Visibility == Visibility.Visible && previewPane.Visibility == Visibility.Collapsed, "Return to font list failed");
            Check(selectionIds.SequenceEqual(owner.SelectedFaces.Select(f => f.Id)) && owner.PreviewText == previewText, "Layout switch lost selection or preview text");
            // Repeated measure/arrange must settle: preview height cannot feed back into its parent.
            var previewContent = (FrameworkElement)libraryPage.FindName("PreviewContent");
            foreach (var size in new[] { new Size(1400, 960), new Size(1200, 800), new Size(1050, 700) })
            {
                libraryPage.Measure(size); libraryPage.Arrange(new Rect(size)); libraryPage.UpdateLayout();
                var initial = previewContent.RenderSize;
                for (int pass = 0; pass < 8; pass++)
                {
                    libraryPage.InvalidateMeasure(); libraryPage.Measure(size);
                    libraryPage.Arrange(new Rect(size)); libraryPage.UpdateLayout();
                    Check(Math.Abs(previewContent.ActualHeight - initial.Height) < 1,
                        $"Preview layout did not converge at {size}: {initial.Height} -> {previewContent.ActualHeight}");
                    Check(double.IsFinite(previewContent.ActualHeight) && previewContent.ActualHeight < 1500,
                        "Preview height grew beyond the bounded viewport");
                }
            }
            Console.WriteLine("PASS stable repeated preview layout at three window sizes");
            Console.WriteLine("PASS library wide/compact layouts, preview switching and stable selection");
        }
        Console.WriteLine("PASS accepted picker teardown, repeated tagging and library batch assignment");
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
        Check(AppAssets.ReadLegalText("LICENSE").Contains("GNU GENERAL PUBLIC LICENSE"), "Embedded GPL license missing");
        Check(AppAssets.ReadLegalText("NOTICES.txt").Contains("WPF"), "Embedded third-party notices missing");
        window.Close();
        // Regression: the user's trace showed UnWatch throwing after HWND destruction.
        theme.Dispose(); theme.Dispose();
        sourcePage.Dispose(); categoryPage.Dispose(); sourceManager.Dispose(); categories.Dispose();
        await owner.ShutdownAsync();
        // Clear only this verifier's pool before removing its isolated database.
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "library.db"), ForeignKeys = true }.ToString();

        VerifyPaneGeometry((System.Windows.Controls.Grid)sourcePage.FindName("Workspace"));
        var tagControl = (CategoryManagementControl)categoryPage.FindName("TagManagement");
        VerifyPaneGeometry((System.Windows.Controls.Grid)tagControl.FindName("Workspace"));
        Console.WriteLine("PASS source and tag card geometry without font rendering");
        // Let already-started asynchronous reads finish after disposing their subscribers.
        for (int attempt = 0; ; attempt++)
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(cs)) Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); break; }
            catch (IOException) when (attempt < 20) { await Task.Delay(100); }
        }
    }
    private static void VerifyPaneGeometry(System.Windows.Controls.Grid workspace)
    {
        var cards = workspace.Children.OfType<Card>().ToArray();
        var contents = cards.Select(c => c.Content).ToArray();
        var visibility = cards.Select(c => c.Visibility).ToArray();
        var columns = cards.Select(System.Windows.Controls.Grid.GetColumn).ToArray();
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
            isolated.ColumnDefinitions[0].Width = new GridLength(280);
            isolated.ColumnDefinitions[1].Width = new GridLength(16);
            isolated.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            for (int i = 0; i < cards.Length; i++)
            {
                cards[i].Content = new System.Windows.Controls.Grid(); cards[i].Visibility = Visibility.Visible;
                System.Windows.Controls.Grid.SetColumn(cards[i], i == 0 ? 0 : 2);
            }
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
            for (int i = 0; i < cards.Length; i++)
            {
                cards[i].Content = contents[i]; cards[i].Visibility = visibility[i];
                System.Windows.Controls.Grid.SetColumn(cards[i], columns[i]);
            }
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
            bool compact = workspace.ActualWidth < 800;
            double contentHeight = workspace.RowDefinitions[1].ActualHeight;
            Check(list.Visibility == Visibility.Visible, $"{name}: list is not initially visible");
            Check(detail.Visibility == (compact ? Visibility.Collapsed : Visibility.Visible), $"{name}: incorrect detail visibility");
            Check(Math.Abs(list.ActualHeight - contentHeight) < 1, $"{name}: list does not fill content row");
            if (compact)
            {
                var switcher = (System.Windows.Controls.WrapPanel)workspace.FindName("PaneSwitch");
                var buttons = switcher.Children.OfType<Wpf.Ui.Controls.Button>().ToArray();
                buttons[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); page.UpdateLayout();
                Check(detail.Visibility == Visibility.Visible && list.Visibility == Visibility.Collapsed, $"{name}: detail switch failed");
                Check(Math.Abs(detail.ActualHeight - contentHeight) < 1 && detail.ActualWidth <= workspace.ActualWidth, $"{name}: detail exceeds content bounds");
                buttons[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); page.UpdateLayout();
                Check(list.Visibility == Visibility.Visible && detail.Visibility == Visibility.Collapsed, $"{name}: return to list failed");
            }
            else
            {
                var listBounds = list.TransformToAncestor(page).TransformBounds(new Rect(list.RenderSize));
                var detailBounds = detail.TransformToAncestor(page).TransformBounds(new Rect(detail.RenderSize));
                Check(Math.Abs(listBounds.Top - detailBounds.Top) < 1 && Math.Abs(listBounds.Bottom - detailBounds.Bottom) < 1,
                    $"{name}: pane edges are not aligned at {size}");
                Check(listBounds.Bottom <= size.Height && detailBounds.Right <= size.Width && detailBounds.Left >= listBounds.Right,
                    $"{name}: panes overflow at {size}");
            }
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
