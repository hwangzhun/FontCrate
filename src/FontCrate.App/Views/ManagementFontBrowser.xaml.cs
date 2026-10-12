using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FontCrate.Core;

namespace FontCrate.App.Views;

public partial class ManagementFontBrowser : UserControl
{
    private readonly Dictionary<Image, CancellationTokenSource> requests = [];
    private FontBrowserViewModel? model;
    private bool restoring;
    private ScrollViewer? scroll;
    private double savedOffset;
    public ManagementFontBrowser()
    {
        InitializeComponent(); DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => { Attach(); model?.ResumePreview(); };
        Unloaded += (_, _) => { SaveScroll(); CancelRequests(); if (model is not null) { model.PreviewSettingsChanged -= RefreshThumbnails; model.SelectionRestored -= RestoreSelection; model.BeforeFontsChanged -= SaveScroll; model.SuspendPreview(); } model = null; };
    }
    private void Attach()
    {
        if (model is not null) { model.PreviewSettingsChanged -= RefreshThumbnails; model.SelectionRestored -= RestoreSelection; model.BeforeFontsChanged -= SaveScroll; }
        model = DataContext as FontBrowserViewModel;
        if (model is not null) { model.PreviewSettingsChanged += RefreshThumbnails; model.SelectionRestored += RestoreSelection; model.BeforeFontsChanged += SaveScroll; if (IsLoaded) RestoreSelection(model.SelectedFonts); }
    }
    private void SaveScroll()
    { scroll ??= FindScroll(FontList); savedOffset = scroll?.VerticalOffset ?? 0; }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScroll(child) is { } nested) return nested;
        }
        return null;
    }
    private void RestoreSelection(FaceRow[] selected)
    {
        restoring = true;
        try
        {
            foreach (var item in FontList.SelectedItems.Cast<FaceRow>().Where(f => !selected.Contains(f)).ToArray()) FontList.SelectedItems.Remove(item);
            foreach (var item in selected) if (!FontList.SelectedItems.Contains(item)) FontList.SelectedItems.Add(item);
        }
        finally { restoring = false; }
        if (scroll is not null) Dispatcher.InvokeAsync(() => scroll.ScrollToVerticalOffset(savedOffset), DispatcherPriority.Loaded);
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!restoring && DataContext is FontBrowserViewModel vm) vm.SetSelection(FontList.SelectedItems.Cast<FaceRow>(), FontList.SelectedItem as FaceRow); }
    private void ThumbnailLoaded(object sender, RoutedEventArgs e) => _ = LoadThumbnailAsync((Image)sender);
    private void ThumbnailUnloaded(object sender, RoutedEventArgs e) => Cancel((Image)sender);
    private void ThumbnailChanged(object sender, DependencyPropertyChangedEventArgs e)
    { var image = (Image)sender; Cancel(image); if (image.IsLoaded) _ = LoadThumbnailAsync(image); }
    private void Cancel(Image image)
    { if (requests.Remove(image, out var token)) { token.Cancel(); token.Dispose(); } image.Source = null; image.ToolTip = null; }
    private void CancelRequests() { foreach (var image in requests.Keys.ToArray()) Cancel(image); }
    private void RefreshThumbnails() { foreach (var image in requests.Keys.ToArray()) _ = LoadThumbnailAsync(image); }
    private async Task LoadThumbnailAsync(Image image)
    {
        Cancel(image);
        if (DataContext is not FontBrowserViewModel vm || image.DataContext is not FaceRow face || face.SourceUnavailable || !image.IsLoaded) return;
        var source = new CancellationTokenSource(); var token = source.Token; requests[image] = source;
        try { var bitmap = await vm.RenderThumbnailAsync(face, token); if (!token.IsCancellationRequested && image.DataContext is FaceRow current && current.Id == face.Id) image.Source = bitmap; }
        catch (OperationCanceledException) { }
        catch { if (!token.IsCancellationRequested) image.ToolTip = "此字体暂时无法预览"; }
    }
}
