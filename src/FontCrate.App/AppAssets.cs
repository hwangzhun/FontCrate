using System.Windows;
using System.Windows.Media.Imaging;

namespace FontCrate.App;

public static class AppAssets
{
    public static BitmapSource Logo { get; } = LoadLogo();

    public static Stream OpenTrayIcon() => OpenResource("FontCrate.ico");

    public static string ReadLegalText(string name)
    {
        using var stream = typeof(AppAssets).Assembly.GetManifestResourceStream("FontCrate.Legal." + name)
            ?? throw new FileNotFoundException("缺少内嵌许可证：" + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string ExportLegalFiles()
    {
        string directory = Path.Combine(AppIdentity.ResolveDataDirectory(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), "legal");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "LICENSE.txt"), ReadLegalText("LICENSE"));
        File.WriteAllText(Path.Combine(directory, "THIRD-PARTY-NOTICES.txt"), ReadLegalText("NOTICES.txt"));
        const string prefix = "FontCrate.Legal.licenses.";
        foreach (string resource in typeof(AppAssets).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = typeof(AppAssets).Assembly.GetManifestResourceStream(resource)!;
            using var file = File.Create(Path.Combine(directory, resource[prefix.Length..]));
            stream.CopyTo(file);
        }
        return directory;
    }

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
