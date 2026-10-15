using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FontCrate.App;

public sealed record ThemeOption(ThemeMode Mode, string Name);

public sealed class ThemeService : ObservableObject, IDisposable
{
    private readonly SettingsStore store;
    private ThemeMode mode;
    private string notice;
    private Window? window;
    private bool watching;
    public ThemeOption[] Options { get; } = [new(ThemeMode.System, "跟随系统"), new(ThemeMode.Light, "浅色"), new(ThemeMode.Dark, "深色")];
    public event Action<uint, uint>? PreviewColorsChanged;
    public uint ForegroundArgb { get; private set; } = 0xFF1F293B;
    public uint BackgroundArgb { get; private set; } = 0xFFFFFFFF;
    public string Notice { get => notice; private set { SetProperty(ref notice, value); OnPropertyChanged(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public ThemeMode Mode
    {
        get => mode;
        set
        {
            if (!Enum.IsDefined(value) || !SetProperty(ref mode, value)) return;
            Apply();
            try { store.Save(new(value)); Notice = ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Notice = "本次主题已应用，但设置未能保存：" + ex.Message; }
        }
    }
    public ThemeService(SettingsStore store)
    {
        this.store = store;
        var result = store.Load(); mode = result.Settings.Theme; notice = result.Notice;
        ApplicationThemeManager.Changed += OnThemeChanged;
        Apply();
    }
    public void Attach(Window target)
    {
        window = target;
        target.Loaded += OnWindowLoaded;
    }
    private void OnWindowLoaded(object sender, RoutedEventArgs e) { UpdateWatcher(); UpdateColors(); }
    private void Apply()
    {
        UpdateWatcher();
        if (Mode == ThemeMode.System) ApplicationThemeManager.ApplySystemTheme();
        else ApplicationThemeManager.Apply(Mode == ThemeMode.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None);
        UpdateColors();
    }
    private void UpdateWatcher()
    {
        if (window?.IsLoaded != true) return;
        if (Mode == ThemeMode.System && !watching)
        { SystemThemeWatcher.Watch(window, WindowBackdropType.None); watching = true; }
        else if (Mode != ThemeMode.System && watching)
        { SystemThemeWatcher.UnWatch(window); watching = false; }
    }
    private void OnThemeChanged(ApplicationTheme theme, Color accent) => UpdateColors();
    private void UpdateColors()
    {
        var app = System.Windows.Application.Current;
        Color foreground = (app.TryFindResource("TextFillColorPrimaryBrush") as SolidColorBrush)?.Color ?? Colors.Black;
        Color background = (app.TryFindResource("ApplicationBackgroundBrush") as SolidColorBrush)?.Color ?? Colors.White;
        // The bitmap is an opaque canvas; WPF's semi-transparent text brush keeps its alpha.
        background.A = 255;
        app.Resources["PreviewForeground"] = new SolidColorBrush(foreground);
        app.Resources["PreviewBackground"] = new SolidColorBrush(background);
        uint fg = Argb(foreground), bg = Argb(background);
        if (fg == ForegroundArgb && bg == BackgroundArgb) return;
        ForegroundArgb = fg; BackgroundArgb = bg;
        PreviewColorsChanged?.Invoke(fg, bg);
    }
    private static uint Argb(Color c) => (uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;
    public void Dispose()
    {
        ApplicationThemeManager.Changed -= OnThemeChanged;
        if (window is not null)
        {
            window.Loaded -= OnWindowLoaded;
            // IsLoaded can stay true during Application.OnExit after the HWND
            // has been destroyed. UnWatch requires an existing native handle.
            if (watching && new WindowInteropHelper(window).Handle != IntPtr.Zero) SystemThemeWatcher.UnWatch(window);
            watching = false;
            window = null;
        }
    }
}
