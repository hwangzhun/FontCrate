using System.Text.Json;
using System.Text.Json.Serialization;

namespace FontCrate.App;

[JsonConverter(typeof(JsonStringEnumConverter<ThemeMode>))]
public enum ThemeMode { System, Light, Dark }
public sealed record AppSettings(ThemeMode Theme = ThemeMode.System);
public sealed record SettingsLoadResult(AppSettings Settings, string Notice);

public sealed class SettingsStore(string path)
{
    public SettingsLoadResult Load()
    {
        if (!File.Exists(path)) return new(new(), "尚无主题设置，已默认跟随系统。可在设置中选择外观。");
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            if (settings is null || !Enum.IsDefined(settings.Theme)) throw new JsonException("无效的主题设置");
            return new(settings, "");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(new(), "主题设置无法读取，已恢复为跟随系统。" + ex.Message);
        }
    }
    public void Save(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme)) throw new ArgumentOutOfRangeException(nameof(settings));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
