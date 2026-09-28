using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cyclop.Stores;

/// The few switches the tray menu holds, in `%APPDATA%\Cyclop\settings.json`.
public sealed class Settings
{
    /// Screenshots from the clipboard and the system's screenshot folders go
    /// on the shelf. On by default: it is the reason the shelf exists.
    [JsonPropertyName("catchScreenshots")]
    public bool CatchScreenshots { get; set; } = true;

    static string FilePath => Support.File("settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (FileNotFoundException) { return new(); }
        catch (Exception error) when (error is IOException or JsonException)
        {
            Support.Log($"cannot read settings.json: {error.Message}");
            return new();
        }
    }

    public void Save()
    {
        try { Support.WriteAtomic(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (IOException error) { Support.Log($"cannot write settings.json: {error.Message}"); }
    }
}
