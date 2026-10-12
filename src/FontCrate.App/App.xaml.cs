using System.Windows;
using FontCrate.Core;
using FontCrate.Windows;
using Forms = System.Windows.Forms;

namespace FontCrate.App;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private Mutex? installerGuard;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private MainViewModel? model;
    private PreviewService? previews;
    private bool exiting;
    private bool closePromptOpen;
    private bool shutdownReady;
    private ThemeService? theme;
    private ConfirmationService? confirmations;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Inno Setup checks this stable name before installation and uninstallation.
        installerGuard = new Mutex(false, "Local\\FontCrate-Running");
        // Keep the legacy mutex so the old and new applications cannot edit the same database concurrently.
        string instanceName = "Local\\FontLite-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.UserName)))[..16];
        instance = new Mutex(true, instanceName, out bool first);
        if (!first) { MessageBox.Show("FontCrate 已在运行，请从托盘打开。", "FontCrate"); Shutdown(); return; }
        try
        {
            var startup = System.Diagnostics.Stopwatch.StartNew();
            string data = AppIdentity.ResolveDataDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var repo = new Repository(Path.Combine(data, "library.db")); await repo.InitializeAsync();
            PerformanceLog.Path = Path.Combine(data, "performance.jsonl");
            var parser = new DirectWriteParser(); var scanner = new Scanner(repo, parser);
            previews = new PreviewService(); var platform = new WindowsFontPlatform(data, parser);
            var operations = new FontOperations(repo, platform);
            confirmations = new ConfirmationService();
            theme = new ThemeService(new SettingsStore(Path.Combine(data, "settings.json")));
            model = new MainViewModel(repo, scanner, previews, operations, Dispatcher, confirmations);
            model.InitialViewReady += () => PerformanceLog.Write("startup-interactive", startup.Elapsed.TotalMilliseconds);
            model.ExitRequested += async () => await ExitAsync();
            var window = new MainWindow(model, theme, confirmations); MainWindow = window;
            window.Closing += async (_, args) =>
            {
                if (shutdownReady) return;
                args.Cancel = true;
                if (exiting) return;
                if (closePromptOpen || confirmations.IsOpen) return;
                closePromptOpen = true;
                try
                {
                    await window.SuspendTaskDialogAsync();
                    var choice = await confirmations.ChooseCloseAsync();
                    if (choice == Wpf.Ui.Controls.ContentDialogResult.Primary)
                    {
                        window.ResumeTaskDialog();
                        await ExitAsync();
                    }
                    else if (choice == Wpf.Ui.Controls.ContentDialogResult.Secondary)
                    {
                        window.Hide(); model.ClearPreviews();
                    }
                }
                finally { closePromptOpen = false; window.ResumeTaskDialog(); }
            };
            window.Show();
            using (var iconStream = AppAssets.OpenTrayIcon())
            using (var icon = new System.Drawing.Icon(iconStream, 32, 32)) trayIcon = (System.Drawing.Icon)icon.Clone();
            tray = new Forms.NotifyIcon { Text = "FontCrate", Icon = trayIcon, Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开 FontCrate", null, (_, _) => Dispatcher.Invoke(ShowWindow));
            menu.Items.Add("退出并停用临时字体", null, (_, _) => Dispatcher.InvokeAsync(async () => await ExitAsync()));
            tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
            // Let the native dialog host receive its first layout before displaying startup progress.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            if (!exiting) await model.InitializeAsync();
        }
        catch (Exception ex)
        {
            string? errorLog = null;
            try
            {
                string directory = AppIdentity.ResolveDataDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                Directory.CreateDirectory(directory);
                errorLog = Path.Combine(directory, "startup-error.log");
                File.WriteAllText(errorLog, ex.ToString());
            }
            catch { errorLog = null; }
            MessageBox.Show(ex.Message + "\n\n具体错误：" + ex.GetBaseException().Message +
                (errorLog is null ? "" : "\n\n完整日志：" + errorLog), "FontCrate 启动失败");
            tray?.Dispose(); shutdownReady = true; Shutdown(1);
        }
    }
    private void ShowWindow() { MainWindow.Show(); MainWindow.WindowState = WindowState.Normal; MainWindow.Activate(); }
    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        confirmations?.Cancel();
        ShowWindow();
        if (MainWindow is MainWindow window) window.ResumeTaskDialog();
        try
        {
            if (model is not null)
            {
                var failures = await model.ShutdownAsync();
                if (failures.Count > 0)
                {
                    exiting = false; ShowWindow();
                    await InformExitFailureAsync("部分临时字体未能停用，请关闭使用这些字体的软件后重试。\n" + string.Join("\n", failures.Select(x => x.Message)));
                    return;
                }
            }
            tray?.Dispose(); previews?.Dispose(); shutdownReady = true; Shutdown();
        }
        catch (Exception ex)
        {
            exiting = false;
            if (model is not null) { model.Status = "退出清理失败：" + ex.Message; model.ResultText = ex.ToString(); }
            await InformExitFailureAsync("退出清理失败，请重试。\n" + ex.Message);
        }
    }
    private async Task InformExitFailureAsync(string message)
    {
        var window = MainWindow as MainWindow;
        if (window is not null) await window.SuspendTaskDialogAsync();
        try { if (confirmations is not null) await confirmations.InformAsync("FontCrate 保持运行", message); }
        finally { window?.ResumeTaskDialog(); }
    }
    protected override void OnExit(ExitEventArgs e) { theme?.Dispose(); tray?.Dispose(); trayIcon?.Dispose(); instance?.Dispose(); installerGuard?.Dispose(); base.OnExit(e); }
}
