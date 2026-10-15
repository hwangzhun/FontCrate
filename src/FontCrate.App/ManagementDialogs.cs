using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FontCrate.Core;
using FontCrate.App.Views;
using Wpf.Ui.Controls;

namespace FontCrate.App;

public static class ManagementDialogs
{
    public static async Task EditNameAsync(ConfirmationService service, string title, string value, Func<string, Task> save)
    {
        var input = new Wpf.Ui.Controls.TextBox { Text = value, MaxLength = 100, Margin = new Thickness(0, 0, 0, 8) };
        var error = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap };
        error.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        var content = new StackPanel { Width = 440 };
        content.Children.Add(new System.Windows.Controls.TextBlock { Text = "名称为 1–100 个字符，同类名称不能重复。", Margin = new Thickness(0, 0, 0, 12) });
        content.Children.Add(input); content.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = title, Content = content, PrimaryButtonText = "保存", CloseButtonText = "取消",
            IsPrimaryButtonEnabled = true, IsSecondaryButtonEnabled = false, DefaultButton = ContentDialogButton.Primary
        };
        long cancellationVersion = service.CancellationVersion;
        bool submitting = false, saved = false;
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.Closing += (_, e) => e.Cancel = submitting || (e.Result == ContentDialogResult.Primary && !saved);
        dialog.ButtonClicked += async (_, e) =>
        {
            if (e.Button != ContentDialogButton.Primary) return;
            if (submitting) return;
            submitting = true; error.Text = "";
            try
            {
                await Task.Yield();
                if (service.CancellationVersion != cancellationVersion) { submitting = false; dialog.Hide(ContentDialogResult.None); return; }
                await save(input.Text); saved = true; submitting = false;
                dialog.Hide(service.CancellationVersion == cancellationVersion ? ContentDialogResult.Primary : ContentDialogResult.None);
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                if (service.CancellationVersion != cancellationVersion) { submitting = false; dialog.Hide(ContentDialogResult.None); }
            }
            finally { submitting = false; }
        };
        await service.ShowContentAsync(dialog);
    }
    public static async Task<string[]> ChooseFontsAsync(ConfirmationService service, MainViewModel owner, HashSet<string> excluded, string name)
    {
        long cancellationVersion = service.CancellationVersion;
        PerformanceLog.Write("font-picker-start", 0, $"excluded={excluded.Count}");
        using var picker = new FontPickerViewModel(owner, excluded);
        var browser = new ManagementFontBrowser { DataContext = picker, Width = 600, Height = 420 };
        var dialog = new ContentDialog
        {
            Title = "添加字体到“" + name + "”", Content = browser, PrimaryButtonText = "添加所选字体", CloseButtonText = "取消",
            IsPrimaryButtonEnabled = true, IsSecondaryButtonEnabled = false, DefaultButton = ContentDialogButton.Primary
        };
        dialog.ButtonClicked += (_, e) =>
        {
            if (e.Button == ContentDialogButton.Primary && picker.SelectedFonts.Length == 0)
            { picker.Error = "请先选择要添加的字体。已关联的字体不会出现在列表中。"; }
        };
        dialog.Closing += (_, e) => e.Cancel = e.Result == ContentDialogResult.Primary && picker.SelectedFonts.Length == 0;
        try
        {
            await picker.RefreshAsync();
            if (owner.IsClosing || service.CancellationVersion != cancellationVersion) return [];
            if (!await service.ShowContentAsync(dialog)) return [];
            PerformanceLog.Write("font-picker-accepted", 0, $"selected={picker.SelectedFonts.Length}");
            return picker.SelectedFonts.Select(f => f.Id).ToArray();
        }
        finally
        {
            // ContentDialog can finish before WPF delivers Unloaded. Detach while
            // the picker is still alive so late lifecycle events cannot cancel a
            // disposed preview token or start another preview.
            browser.DataContext = null;
            dialog.Content = null;
            PerformanceLog.Write("font-picker-detached", 0);
        }
    }
}
