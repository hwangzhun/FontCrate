using System.Windows;
using System.Windows.Media;
using System.ComponentModel;
using FontCrate.App.Views;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace FontCrate.App;

public partial class MainWindow : FluentWindow
{
    private readonly FontLibraryPage library;
    private bool? compactNavigation;
    private ContentDialog? progressDialog;
    private Task? progressDialogTask;
    private bool taskDialogSuspended;
    private bool closed;
    public ThemeService Theme { get; }
    public MainWindow(MainViewModel model, ThemeService theme, ConfirmationService confirmations)
    {
        Theme = theme;
        InitializeComponent(); DataContext = model;
        confirmations.Attach(DialogHost);
        model.PropertyChanged += OnTaskStateChanged;
        library = new FontLibraryPage(model);
        var pages = new Dictionary<Type, Func<object>>
        {
            [typeof(FontLibraryPage)] = () => library,
            [typeof(SourcesPage)] = () => new SourcesPage(model, confirmations, ViewFonts),
            [typeof(AnnotationsPage)] = () => new AnnotationsPage(model, confirmations, ViewFonts),
            [typeof(TasksPage)] = () => new TasksPage(model),
            [typeof(SettingsPage)] = () => new SettingsPage(model, theme)
        };
        var provider = new PageProvider(pages);
        Navigation.SetPageProviderService(provider);
        model.SourcesRequested += () => Navigation.Navigate(typeof(SourcesPage));
        model.ResultsRequested += () => Navigation.Navigate(typeof(TasksPage));
        theme.PreviewColorsChanged += model.SetPreviewColors;
        model.SetPreviewColors(theme.ForegroundArgb, theme.BackgroundArgb);
        theme.Attach(this);
        SizeChanged += (_, _) => UpdateNavigation();
        Loaded += (_, _) => { UpdateNavigation(); ViewFonts(); };
        DpiChanged += (_, e) => library.RefreshDpi((float)e.NewDpi.PixelsPerInchX);
        IsVisibleChanged += (_, _) => { if (!IsVisible) confirmations.Cancel(); };
        Closed += (_, _) =>
        {
            provider.Dispose();
            closed = true;
            progressDialog?.Hide();
            theme.PreviewColorsChanged -= model.SetPreviewColors;
            model.PropertyChanged -= OnTaskStateChanged;
        };
    }
    private void OnTaskStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel model) return;
        if (e.PropertyName == nameof(MainViewModel.OperationCancelable))
        {
            if (progressDialog is not null) UpdateProgressButtons(progressDialog, model);
            return;
        }
        if (e.PropertyName != nameof(MainViewModel.TaskBusy)) return;
        if (!model.TaskBusy) progressDialog?.Hide();
        else StartProgressDialog(model);
    }
    private void StartProgressDialog(MainViewModel model)
    {
        if (model.TaskBusy && progressDialog is null && !closed && !taskDialogSuspended)
            progressDialogTask = ShowProgressDialogAsync(model);
    }
    public async Task SuspendTaskDialogAsync()
    {
        taskDialogSuspended = true;
        progressDialog?.Hide();
        if (progressDialogTask is not null) await progressDialogTask;
    }
    public void ResumeTaskDialog()
    {
        taskDialogSuspended = false;
        if (DataContext is MainViewModel model) StartProgressDialog(model);
    }
    private static void UpdateProgressButtons(ContentDialog dialog, MainViewModel model)
    {
        bool cancelable = model.Scanning || model.OperationCancelable;
        dialog.CloseButtonText = model.Scanning ? "取消扫描" : model.OperationCancelable ? "取消剩余操作" : "请稍候…";
        // Keep an existing footer in place during cancellation so the dialog cannot change height.
        dialog.IsFooterVisible = true;
        if (dialog.Template?.FindName("CloseButton", dialog) is UIElement button)
            button.SetCurrentValue(IsEnabledProperty, cancelable);
    }
    private async Task ShowProgressDialogAsync(MainViewModel model)
    {
        var dialog = new ContentDialog(DialogHost)
        {
            Title = "字体任务进度",
            Content = model.Scanning ? model.ScanProgress : model.OperationProgress,
            ContentTemplate = (DataTemplate)FindResource("ProgressDialogTemplate"),
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
            IsFooterVisible = true,
            DefaultButton = ContentDialogButton.Close
        };
        UpdateProgressButtons(dialog, model);
        // The library manages the modal surface, focus, theme and background input blocking.
        // Cancellation keeps the dialog open until the task has finished its rollback and refresh.
        dialog.Closing += (_, args) => args.Cancel = model.TaskBusy && !closed && !taskDialogSuspended;
        dialog.Opened += (_, _) => UpdateProgressButtons(dialog, model);
        dialog.ButtonClicked += (_, args) =>
        {
            if (args.Button != ContentDialogButton.Close) return;
            if (model.Scanning) model.CancelScanCommand.Execute(null);
            else if (model.OperationCancelable) model.CancelOperationCommand.Execute(null);
            if (dialog.Template?.FindName("CloseButton", dialog) is UIElement button)
                button.SetCurrentValue(IsEnabledProperty, false);
        };
        progressDialog = dialog;
        bool finished = false;
        try { await dialog.ShowAsync(); finished = true; }
        catch (Exception ex) { model.Status = ex.Message; model.ResultText = ex.ToString(); }
        finally { if (ReferenceEquals(progressDialog, dialog)) progressDialog = null; }
        // A new task may start before the previous dialog's asynchronous removal finishes.
        if (finished) StartProgressDialog(model);
    }
    private void ViewFonts() => Navigation.Navigate(typeof(FontLibraryPage));
    private void UpdateNavigation()
    {
        bool compact = ActualWidth < 1200;
        if (compactNavigation == compact) return;
        compactNavigation = compact;
        Navigation.PaneDisplayMode = compact ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.Left;
        Navigation.IsPaneOpen = !compact;
    }
    private sealed class PageProvider(Dictionary<Type, Func<object>> factories) : INavigationViewPageProvider, IDisposable
    {
        private readonly Dictionary<Type, object> pages = [];
        public void Dispose() { foreach (var page in pages.Values.OfType<IDisposable>()) page.Dispose(); }
        public object? GetPage(Type pageType)
        {
            if (pages.TryGetValue(pageType, out var page)) return page;
            if (!factories.TryGetValue(pageType, out var create)) return null;
            page = create();
            pages.Add(pageType, page);
            return page;
        }
    }
}
