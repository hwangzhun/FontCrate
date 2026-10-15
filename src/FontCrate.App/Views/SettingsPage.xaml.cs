using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace FontCrate.App.Views;

public sealed record ThirdPartyComponent(string Name, string License, string Source);

public partial class SettingsPage : Page
{
    private readonly MainViewModel model;
    public string VersionText => $"FontCrate {AppIdentity.VersionLabel}";
    public string DescriptionText => AppIdentity.Description;
    public string AuthorText => "作者：" + AppIdentity.Author;
    public string LicenseText => "开源许可证：" + AppIdentity.License;
    public bool HasRepository => Uri.TryCreate(AppIdentity.RepositoryUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    public string ProjectLicenseText { get; }
    public string ThirdPartyText { get; }
    public IReadOnlyList<ThirdPartyComponent> ThirdPartyComponents { get; }
    public SettingsPage(MainViewModel model, ThemeService theme)
    {
        this.model = model;
        try { ProjectLicenseText = AppAssets.ReadLegalText("LICENSE"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ProjectLicenseText = "许可证文件无法读取：" + ex.Message; }
        try { ThirdPartyText = AppAssets.ReadLegalText("NOTICES.txt"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ThirdPartyText = "声明文件无法读取：" + ex.Message; }
        ThirdPartyComponents = ThirdPartyText.Split('\n')
            .Where(line => line.StartsWith("| ") && !line.StartsWith("| Dependency"))
            .Select(line => line.Split('|').Select(value => value.Trim()).ToArray())
            .Where(parts => parts.Length >= 5)
            .Select(parts => new ThirdPartyComponent(parts[1], parts[2], parts[3].Split(';')[0].Trim()))
            .ToArray();
        InitializeComponent(); DataContext = theme;
    }
    private void OpenRepository(object sender, RoutedEventArgs e)
    {
        if (!HasRepository) return;
        try { Process.Start(new ProcessStartInfo(AppIdentity.RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception ex) { model.Status = "无法打开项目仓库：" + ex.Message; }
    }
    private void OpenComponent(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            OpenTarget(url);
    }
    private void OpenThirdPartyNotices(object sender, RoutedEventArgs e) => OpenLegalFiles(true);
    private void OpenLicenses(object sender, RoutedEventArgs e) => OpenLegalFiles(false);
    private void OpenLegalFiles(bool notices)
    {
        try
        {
            string directory = AppAssets.ExportLegalFiles();
            OpenTarget(notices ? Path.Combine(directory, "THIRD-PARTY-NOTICES.txt") : directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { model.Status = "无法打开许可证：" + ex.Message; }
    }
    private void OpenTarget(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { model.Status = "无法打开：" + ex.Message; }
    }
    private void Exit(object sender, RoutedEventArgs e) => model.ExitCommand.Execute(null);
}
