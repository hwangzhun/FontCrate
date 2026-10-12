namespace FontCrate.App;

public static class AppIdentity
{
    public static string VersionLabel => "v" + (typeof(AppIdentity).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    public const string Name = "FontCrate";
    public const string Description = "轻量字体管理工具。集中扫描与预览本地字体，使用标签、合集和收藏整理字体库，并为其他应用临时启用字体或安装到当前用户。";
    public const string Author = "Hwangzhun";
    public const string RepositoryUrl = "https://github.com/hwangzhun/FontCrate/";
    public const string License = "GNU GPL v3（GPL-3.0-only）";

    public static string ResolveDataDirectory(string localAppData)
    {
        string current = Path.Combine(localAppData, Name);
        string legacy = Path.Combine(localAppData, "FontLite");
        // Reuse the old database and owned font copies without changing their journaled paths.
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }
}
