using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FontCrate.Core;

namespace FontCrate.App.Views;

public partial class FontLibraryPage : Page
{
    private readonly MainViewModel model;
    private readonly Dictionary<Image, CancellationTokenSource> rowRequests = [];
    private ScrollViewer? listScroll;
    private bool restoringSelection, active;
    private readonly DispatcherTimer previewWidthTimer;
    private double navigationOffset, navigationHorizontalOffset;
    private double savedVerticalOffset, savedHorizontalOffset;
    private string? savedTopFace;
    private long scrollGeneration;
    public FontLibraryPage(MainViewModel model)
    {
        this.model = model; InitializeComponent(); DataContext = model;
        previewWidthTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(150) };
        previewWidthTimer.Tick += (_, _) => { previewWidthTimer.Stop(); if (active) UpdatePreviewWidth(); };
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.HasSelection) && !model.HasSelection) TagAssignment.Visibility = Visibility.Collapsed; };
        FontList.PreviewMouseRightButtonDown += OnFontRightButtonDown;
        FontList.ContextMenu = new ContextMenu();
        FontList.ContextMenuOpening += OnFontContextMenuOpening;
        model.ListPreviewsChanged += () =>
        {
            foreach (var image in rowRequests.Keys.ToArray()) StartThumbnail(image);
        };
        model.RestoreSelection += rows =>
        {
            restoringSelection = true;
            try
            {
                foreach (var row in FontList.SelectedItems.Cast<FaceRow>().Except(rows).ToArray()) FontList.SelectedItems.Remove(row);
                foreach (var row in rows) if (!FontList.SelectedItems.Contains(row)) FontList.SelectedItems.Add(row);
                model.SelectedFaces = FontList.SelectedItems.Cast<FaceRow>().ToArray();
            }
            finally { restoringSelection = false; }
        };
        model.BeforeFacesReload += reset =>
        {
            ++scrollGeneration;
            listScroll ??= FindScrollViewer(FontList);
            savedVerticalOffset = IsVisible ? listScroll?.VerticalOffset ?? navigationOffset : navigationOffset;
            savedHorizontalOffset = IsVisible ? listScroll?.HorizontalOffset ?? navigationHorizontalOffset : navigationHorizontalOffset;
            int top = (int)Math.Floor(savedVerticalOffset);
            savedTopFace = !reset && top >= 0 && top < model.Faces.Count ? model.Faces[top].Id : null;
        };
        model.AfterFacesReload += reset =>
        {
            long generation = scrollGeneration;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (generation != scrollGeneration) return;
                listScroll ??= FindScrollViewer(FontList);
                double offset = savedVerticalOffset;
                if (savedTopFace is not null)
                {
                    var anchor = model.Faces.FirstOrDefault(f => f.Id == savedTopFace);
                    if (anchor is not null) offset = model.Faces.IndexOf(anchor) + savedVerticalOffset % 1;
                }
                navigationOffset = reset ? 0 : offset;
                navigationHorizontalOffset = reset ? 0 : savedHorizontalOffset;
                if (IsVisible)
                {
                    listScroll?.ScrollToVerticalOffset(navigationOffset);
                    listScroll?.ScrollToHorizontalOffset(navigationHorizontalOffset);
                }
            }));
        };
        LibraryWorkspace.SizeChanged += (_, e) => { if (e.WidthChanged) UpdateWorkspace(); };
        Loaded += (_, _) => { UpdateActivation(); UpdateWorkspace(); };
        Unloaded += (_, _) => UpdateActivation();
        IsVisibleChanged += (_, _) => UpdateActivation();
    }
    private bool? compactWorkspace;
    private bool previewVisible = true;
    private GridLength listWidth = new(7, GridUnitType.Star), previewWidth = new(3, GridUnitType.Star);
    private void UpdateWorkspace(bool force = false)
    {
        bool compact = LibraryWorkspace.ActualWidth < 900;
        // Only a breakpoint transition or explicit toggle changes the grid.
        // Ordinary layout passes must not rewrite column sizes.
        if (!force && compactWorkspace == compact) return;
        if (compactWorkspace == false && previewVisible && PreviewPane.Visibility == Visibility.Visible)
        { listWidth = LibraryWorkspace.ColumnDefinitions[0].Width; previewWidth = LibraryWorkspace.ColumnDefinitions[2].Width; }
        if (compactWorkspace != compact)
        {
            compactWorkspace = compact; previewVisible = !compact;
        }
        bool split = !compact && previewVisible;
        ListPane.Visibility = compact && previewVisible ? Visibility.Collapsed : Visibility.Visible;
        PreviewPane.Visibility = previewVisible ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        PreviewBack.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        PreviewToggle.Content = previewVisible ? "收起预览" : "显示预览";
        Grid.SetColumn(PreviewPane, compact ? 0 : 2);
        LibraryWorkspace.ColumnDefinitions[0].MinWidth = split ? 320 : 0;
        LibraryWorkspace.ColumnDefinitions[0].Width = split ? listWidth : new GridLength(1, GridUnitType.Star);
        LibraryWorkspace.ColumnDefinitions[1].Width = new GridLength(split ? 12 : 0);
        LibraryWorkspace.ColumnDefinitions[2].MinWidth = split ? 320 : 0;
        LibraryWorkspace.ColumnDefinitions[2].Width = split ? previewWidth : new GridLength(0);
    }
    private void TogglePreview(object sender, RoutedEventArgs e)
    {
        if (compactWorkspace == false && previewVisible)
        { listWidth = LibraryWorkspace.ColumnDefinitions[0].Width; previewWidth = LibraryWorkspace.ColumnDefinitions[2].Width; }
        previewVisible = !previewVisible; UpdateWorkspace(force: true);
        if (previewVisible) { if (compactWorkspace == true) PreviewBack.Focus(); else FontSizeInput.Focus(); } else PreviewToggle.Focus();
    }
    private void CommitFontSize(object sender, RoutedEventArgs e)
    {
        model.CommitPreviewSize(FontSizeInput.Text);
        FontSizeInput.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }
    private void FontSizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitFontSize(sender, e); e.Handled = true; }
    }
    private void MoreActions(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (FrameworkElement)sender };
        menu.Items.Add(new MenuItem { Header = "停用临时字体", Command = model.DeactivateCommand });
        menu.Items.Add(new MenuItem { Header = "卸载本工具安装的字体", Command = model.UninstallCommand });
        menu.Items.Add(new Separator());
        var tags = new MenuItem { Header = "分配或移除标签…" };
        tags.Click += (_, _) => { TagAssignment.Visibility = Visibility.Visible; TagAssignment.IsExpanded = true; TagAssignment.Focus(); };
        menu.Items.Add(tags);
        var export = new MenuItem { Header = "导出所选字体…" };
        export.Click += async (_, _) => await model.ExportSelectionAsync();
        menu.Items.Add(export);
        menu.IsEnabled = model.CanManage; menu.IsOpen = true;
    }
    private void OnFontRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(FontList, source) is not DataGridRow row) return;
        if (!row.IsSelected)
        {
            FontList.SelectedItems.Clear(); row.IsSelected = true;
        }
        row.Focus();
    }
    private void OnFontContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = FontList.ContextMenu;
        menu.Items.Clear();
        var import = new MenuItem { Header = "导入字体…", IsEnabled = model.CanManage };
        import.Click += async (_, _) => await model.ImportFontsAsync();
        menu.Items.Add(import);
        var export = new MenuItem { Header = "导出所选字体…", IsEnabled = model.CanManage && FontList.SelectedItems.Count > 0 };
        export.Click += async (_, _) => await model.ExportSelectionAsync();
        menu.Items.Add(export);
        if (FontList.SelectedItems.Count == 0) return;
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "添加到标签", IsEnabled = model.CanManage && !model.TagPanel.Busy };
        foreach (var tag in model.TagPanel.Items)
        {
            var item = new MenuItem { Header = tag.Name, ToolTip = tag.Name };
            item.Click += async (_, _) => await model.AddSelectionToTagAsync(tag.Id);
            add.Items.Add(item);
        }
        if (add.Items.Count > 0) add.Items.Add(new Separator());
        var create = new MenuItem { Header = "新建标签…" };
        create.Click += async (_, _) => await model.NewTagForSelectionAsync();
        add.Items.Add(create); menu.Items.Add(add);
    }
    private void UpdateActivation()
    {
        bool visible = IsLoaded && IsVisible;
        if (active == visible) return;
        active = visible;
        if (!active)
        {
            previewWidthTimer.Stop();
            SaveListState(); model.SuspendPreviews();
            foreach (var pair in rowRequests) { pair.Value.Cancel(); pair.Key.Source = null; }
            return;
        }
        model.Dpi = (float)VisualTreeHelper.GetDpi(this).PixelsPerInchX;
        RestoreListState();
        // Apply final layout width while rendering is suspended, then resume once.
        UpdatePreviewWidth(); model.RestorePreviews();
        foreach (var image in rowRequests.Keys.ToArray()) StartThumbnail(image);
    }
    public void RefreshDpi(float dpi)
    {
        model.Dpi = dpi;
        if (!active) return;
        model.RestorePreviews();
        foreach (var image in rowRequests.Keys.ToArray()) StartThumbnail(image);
    }
    private void SaveListState()
    {
        listScroll ??= FindScrollViewer(FontList);
        navigationOffset = listScroll?.VerticalOffset ?? navigationOffset;
        navigationHorizontalOffset = listScroll?.HorizontalOffset ?? navigationHorizontalOffset;
    }
    private void RestoreListState()
    {
        var selection = model.SelectedFaces.ToArray();
        var selectedFace = model.SelectedFace;
        restoringSelection = true;
        try
        {
            if (!FontList.SelectedItems.Cast<FaceRow>().ToHashSet().SetEquals(selection))
            {
                FontList.SelectedItems.Clear();
                foreach (var face in selection) FontList.SelectedItems.Add(face);
            }
            model.SelectedFace = selectedFace;
        }
        finally { restoringSelection = false; }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            listScroll ??= FindScrollViewer(FontList);
            listScroll?.ScrollToVerticalOffset(navigationOffset);
            listScroll?.ScrollToHorizontalOffset(navigationHorizontalOffset);
        }));
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsVisible && !restoringSelection && !model.IsReloadingFaces) model.SelectedFaces = FontList.SelectedItems.Cast<FaceRow>().ToArray();
    }
    private void PreviewViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!active || previewWidthTimer is null) return;
        previewWidthTimer.Stop(); previewWidthTimer.Start();
    }
    private void UpdatePreviewWidth()
    {
        // Always reserve scrollbar space so long previews do not trigger width oscillation.
        double width = PreviewViewport.ActualWidth - PreviewViewport.Padding.Left - PreviewViewport.Padding.Right - SystemParameters.VerticalScrollBarWidth;
        if (width <= 0) return;
        int pixels = (int)Math.Clamp(Math.Floor(width), 120, 1200);
        if (pixels != model.PreviewWidth) model.PreviewWidth = pixels;
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
    private void CharacterClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image image || image.Source is null || image.ActualWidth <= 0 || image.ActualHeight <= 0) return;
        var p = e.GetPosition(image); int rows = Math.Max(1, (model.GridCodes.Count + 7) / 8);
        model.SelectCharacter((int)(p.Y / image.ActualHeight * rows) * 8 + (int)(p.X / image.ActualWidth * 8));
    }
    private void ThumbnailLoaded(object sender, RoutedEventArgs e) => StartThumbnail((Image)sender);
    private void ThumbnailContextChanged(object sender, DependencyPropertyChangedEventArgs e) => StartThumbnail((Image)sender);
    private void ThumbnailUnloaded(object sender, RoutedEventArgs e)
    {
        var image = (Image)sender;
        if (rowRequests.Remove(image, out var token)) { token.Cancel(); token.Dispose(); }
        image.Source = null;
        image.ToolTip = null;
    }
    private async void StartThumbnail(Image image)
    {
        if (rowRequests.Remove(image, out var old)) { old.Cancel(); old.Dispose(); }
        image.Source = null; image.ToolTip = null;
        if (!image.IsLoaded || !IsVisible || image.DataContext is not FaceRow face || face.Path is null) return;
        var cancellation = new CancellationTokenSource(); rowRequests[image] = cancellation;
        var token = cancellation.Token;
        try
        {
            await Task.Delay(100, token);
            var source = await model.RenderThumbnailAsync(face, token);
            if (!token.IsCancellationRequested && image.DataContext is FaceRow current && current.Id == face.Id) image.Source = source;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) image.ToolTip = ex.Message; }
    }
}
