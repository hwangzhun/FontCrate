using System.Text.Json;

namespace FontCrate.Core;

public static class PerformanceLog
{
    private static readonly object Gate = new();
    public static string? Path { get; set; }
    public static void Write(string operation, double milliseconds, string? detail = null)
    {
        lock (Gate)
        {
            if (Path is null) return;
            try
            {
                if (File.Exists(Path) && new FileInfo(Path).Length > 1024 * 1024) File.Move(Path, Path + ".1", true);
                File.AppendAllText(Path, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, operation, milliseconds, detail }) + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
