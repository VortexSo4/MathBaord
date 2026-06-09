using System.Numerics;
using System.Text.Json;

namespace MathBoard.Core;

public static class Settings
{
    private static readonly string Path = "MathBoardSettings.json";

    public static List<Vector4> Colors { get; private set; } = 
    [
        new Vector4(0.0f, 0.0f, 0.0f, 1.0f),   // Black
        new Vector4(0.0f, 0.3f, 0.8f, 1.0f),   // Blue
        new Vector4(0.8f, 0.1f, 0.1f, 1.0f),   // Red
        new Vector4(0.1f, 0.7f, 0.1f, 1.0f)    // Green
    ];

    public static void Load()
    {
        if (!File.Exists(Path)) 
        {
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(Path);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data?.Colors != null)
                Colors = data.Colors;
        }
        catch { /* fallback to defaults */ }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public static void Save()
    {
        try
        {
            var data = new SettingsData { Colors = Colors };
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(Path, json);
        }
        catch { }
    }

    private class SettingsData
    {
        public List<Vector4> Colors { get; set; } = [];
    }
}