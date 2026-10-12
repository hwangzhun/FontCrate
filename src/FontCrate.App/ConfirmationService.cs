using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace FontCrate.App;

public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string title, string message, string accept = "确认");
    Task InformAsync(string title, string message);
}

public sealed class ConfirmationService : IConfirmationService
{
    private ContentDialogHost? host;
    private ContentDialog? active;
    public long CancellationVersion { get; private set; }
    public bool IsOpen => active is not null;
    public void Attach(ContentDialogHost dialogHost) => host = dialogHost;
    // Hiding the window or exiting never implicitly approves a pending operation.
    public void Cancel() { CancellationVersion++; active?.Hide(ContentDialogResult.None); }
    public Task<bool> ConfirmAsync(string title, string message, string accept = "确认") => ShowAsync(title, message, accept);
    public async Task InformAsync(string title, string message) => await ShowAsync(title, message, null);
    public async Task<ContentDialogResult> ChooseCloseAsync()
    {
        if (host is null || active is not null) return ContentDialogResult.None;
        var dialog = new ContentDialog(host)
        {
            Title = "关闭 FontCrate",
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "完全退出会取消正在进行的任务，等待清理完成并卸载临时字体。\n\n最小化到托盘会继续运行，临时字体仍然可用。",
                TextWrapping = TextWrapping.Wrap, Width = 480
            },
            PrimaryButtonText = "完全退出", SecondaryButtonText = "最小化到托盘", CloseButtonText = "取消",
            IsPrimaryButtonEnabled = true, IsSecondaryButtonEnabled = true,
            DefaultButton = ContentDialogButton.Close
        };
        active = dialog;
        try { return await dialog.ShowAsync(); }
        finally { active = null; }
    }
    public async Task<bool> ShowContentAsync(ContentDialog dialog)
    {
        if (host is null || active is not null) return false;
        dialog.DialogHostEx = host;
        active = dialog;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { active = null; }
    }
    private async Task<bool> ShowAsync(string title, string message, string? accept)
    {
        if (host is null || active is not null) return false;
        var dialog = new ContentDialog(host)
        {
            Title = title, PrimaryButtonText = accept ?? "", CloseButtonText = accept is null ? "关闭" : "取消",
            IsPrimaryButtonEnabled = accept is not null, IsSecondaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer
            {
                MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 }
            }
        };
        active = dialog;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { active = null; }
    }
}
