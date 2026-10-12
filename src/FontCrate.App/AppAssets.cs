using System.Windows;
using System.Windows.Media.Imaging;

namespace FontCrate.App;

public static class AppAssets
{
    public static BitmapSource Logo { get; } = LoadLogo();

    public static Stream OpenTrayIcon() => OpenResource("FontCrate.ico");

    private static Stream OpenResource(string name)
    {
        // Qualify the assembly explicitly so resource lookup also works from a hosted entry point.
        var uri = new Uri($"pack://application:,,,/FontCrate;component/Assets/{name}", UriKind.Absolute);
        return Application.GetResourceStream(uri)?.Stream
            ?? throw new FileNotFoundException($"缺少 FontCrate 内嵌资源：{name}");
    }

    private static BitmapSource LoadLogo()
    {
        using var stream = OpenResource("FontCrate.png");
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
