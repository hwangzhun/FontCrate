namespace FontCrate.Core;

public record Source(long Id, string Path, bool Available);
public record SourceSummary(long Id, string Path, bool Available, int FileCount, int FaceCount)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : Path;
    public string Status => Available ? "可用" : "不可用";
    public string Counts => $"{FileCount} 个文件 · {FaceCount} 个字体";
}
public record NamedItem(long Id, string Name, int? FaceCount = null);
public record UnicodeRange(int First, int Last);
public record ParsedFace(int Index, string Family, string Style, string FullName, string PostScriptName,
    string Version, int Weight, int Width, bool Italic, bool Variable, IReadOnlyList<UnicodeRange> Coverage);
public record IndexedFile(string Path, long Size, long ModifiedTicks, string? Hash,
    IReadOnlyList<ParsedFace>? Faces, string? Error = null);
public record FaceRow(string Id, string Hash, int Index, string Family, string Style, string FullName,
    string PostScriptName, string Version, int Weight, int Width, bool Italic, bool Variable,
    bool Favorite, string? Path, int Locations, bool Installed, bool Active, bool Owned, string? KnownPath = null)
{
    public string FileType => FontFileType.FromPath(Path ?? KnownPath);
    public bool Inactive => !Installed && !Active;
    public bool SourceUnavailable => Path is null;
    public bool Duplicate => Locations > 1;
    public string Status => $"{(Installed ? "已安装 " : "")}{(Active ? "临时启用 " : "")}{(Path is null ? "来源不可用 " : "")}{(Locations > 1 ? $"{Locations} 个位置" : "")}{(Inactive ? " 未安装 / 未启用" : "")}".Trim();
}
public record FaceQuery(string Text = "", long? SourceId = null, long? TagId = null,
    bool FavoritesOnly = false, string State = "全部");
public record FacePage(IReadOnlyList<FaceRow> Rows, int Total);
public record Location(string Path, bool Available);
public record ScanProgress(int Discovered, int Parsed, int Unchanged, int Errors, string Phase,
    int Processed = 0, bool EnumerationComplete = false);
public record OperationProgress(int Completed, int Total, string? CurrentFont = null);
public record ScanResult(bool Complete, int Discovered, int Parsed, int Unchanged, int Errors);
public record ScanError(string Path, string Message);
public record PreviewRequest(FaceRow Face, string Text, float Size, float Dpi, long Generation, int Width = 640, IReadOnlyList<int>? GridCodes = null,
    uint ForegroundArgb = 0xFF1F293B, uint BackgroundArgb = 0xFFFFFFFF);
public record PreviewResult(byte[] Pixels, int Width, int Height, int Stride, IReadOnlyList<int> Missing, long Generation);
public record OperationResult(string Hash, bool Success, string Message);
public record OperationRecord(long Id, string Hash, string Kind, string Stage, string Path, string RegistryName,
    string Message, bool Loaded);
public record SystemFont(string Path, string? Hash, IReadOnlyList<ParsedFace> Faces);

public interface IFontParser { Task<IReadOnlyList<ParsedFace>> ParseAsync(string path, CancellationToken cancellationToken); }
public interface IPreviewService : IDisposable
{
    Task<PreviewResult> RenderAsync(PreviewRequest request, CancellationToken cancellationToken);
    void ClearCache();
}
public interface IFontPlatform
{
    string TemporaryDirectory { get; }
    string UserFontsDirectory { get; }
    int Load(string path);
    bool Unload(string path);
    bool IsLoaded(string path);
    void NotifyChanged();
    string? GetRegistration(string name);
    void SetRegistration(string name, string path);
    void RemoveRegistration(string name);
    Task<IReadOnlyList<SystemFont>> EnumerateAsync(CancellationToken cancellationToken);
}

public static class FontIdentity
{
    public static string FaceId(string hash, int index) => $"{hash}:{index}";
    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
    public static bool NameConflicts(ParsedFace a, ParsedFace b) =>
        (!string.IsNullOrEmpty(a.FullName) && a.FullName.Equals(b.FullName, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrEmpty(a.PostScriptName) && a.PostScriptName.Equals(b.PostScriptName, StringComparison.OrdinalIgnoreCase)) ||
        (a.Family.Equals(b.Family, StringComparison.OrdinalIgnoreCase) &&
            (a.Style.Length == 0 || b.Style.Length == 0 || a.Style.Equals(b.Style, StringComparison.OrdinalIgnoreCase)));
}

public static class FontFileType
{
    public static string FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "未知";
        string extension = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return extension switch
        {
            "TTF" or "OTF" or "TTC" or "OTC" or "WOFF" or "WOFF2" => extension,
            _ => "未知"
        };
    }
}
