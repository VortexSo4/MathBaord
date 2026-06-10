using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Reflection;

namespace MathBoard.Core;

public static class Settings
{
    private static readonly string Path = "MathBoardSettings.json";

    // ====================== ОДИН ИСТОЧНИК ПРАВДЫ ======================
    private static class Defaults
    {
        public static readonly List<string> Colors = 
        [
            "#D3D3D3",
            "#FF8383",
            "#3A994C"
        ];

        public static readonly string DefaultBackgroundColor = "#121212";

        public const string LibraryRootPath = "Lessons";
        public const int AutoSaveIntervalMinutes = 1;
        public const bool LibraryPanelOnTop = true;

        public const float RadialMenuLongPressThreshold = 1.0f;
        public const float RadialMenuOpenThreshold = 0.3f;
        public const float RadialMenuEscapeTime = 0.12f;
        public const float RadialMenuEscapeDistance = 2f;
        public const bool RadialMenuCloseOnToolSelect = false;

        public const float CameraZoomSpeed = 0.15f;
        public const float CameraMinZoom = 0.1f;
        public const float CameraMaxZoom = 30f;
        public const float CameraPanSpeed = 35f;

        public const float DefaultBrushWidth = 8f;
        public const float DefaultEraserSize = 8f;
        public const float MinBrushWidth = 4f;
        public const float MaxBrushWidth = 90f;

        public const float RadialMenuOuterRadius = 145f;
        public const float RadialMenuInnerRadius = 56f;
        public const float RadialMenuCenterRadius = 46f;

        public const int StrokeCircleSegments = 14;
        public const int UIRingSegments = 128;
    }

    // ====================== Настройки ======================
    public static List<Vector4> Colors { get; private set; } = Defaults.Colors.Select(HexToVector4).ToList();
    
    public static Setting<Vector4> BackgroundColor { get; } = 
        new(HexToVector4(Defaults.DefaultBackgroundColor));

    public static Setting<string> LibraryRootPath { get; } = new(Defaults.LibraryRootPath);
    public static Setting<int> AutoSaveIntervalMinutes { get; } = new(Defaults.AutoSaveIntervalMinutes);
    public static Setting<bool> LibraryPanelOnTop { get; } = new(Defaults.LibraryPanelOnTop);

    public static Setting<float> RadialMenuLongPressThreshold { get; } = new(Defaults.RadialMenuLongPressThreshold);
    public static Setting<float> RadialMenuOpenThreshold { get; } = new(Defaults.RadialMenuOpenThreshold);
    public static Setting<float> RadialMenuEscapeTime { get; } = new(Defaults.RadialMenuEscapeTime);
    public static Setting<float> RadialMenuEscapeDistance { get; } = new(Defaults.RadialMenuEscapeDistance);
    public static Setting<bool> RadialMenuCloseOnToolSelect { get; } = new(Defaults.RadialMenuCloseOnToolSelect);

    public static Setting<float> CameraZoomSpeed { get; } = new(Defaults.CameraZoomSpeed);
    public static Setting<float> CameraMinZoom { get; } = new(Defaults.CameraMinZoom);
    public static Setting<float> CameraMaxZoom { get; } = new(Defaults.CameraMaxZoom);
    public static Setting<float> CameraPanSpeed { get; } = new(Defaults.CameraPanSpeed);

    public static Setting<float> DefaultBrushWidth { get; } = new(Defaults.DefaultBrushWidth);
    public static Setting<float> DefaultEraserSize { get; } = new(Defaults.DefaultEraserSize);
    public static Setting<float> MinBrushWidth { get; } = new(Defaults.MinBrushWidth);
    public static Setting<float> MaxBrushWidth { get; } = new(Defaults.MaxBrushWidth);

    public static Setting<float> RadialMenuOuterRadius { get; } = new(Defaults.RadialMenuOuterRadius);
    public static Setting<float> RadialMenuInnerRadius { get; } = new(Defaults.RadialMenuInnerRadius);
    public static Setting<float> RadialMenuCenterRadius { get; } = new(Defaults.RadialMenuCenterRadius);

    public static Setting<int> StrokeCircleSegments { get; } = new(Defaults.StrokeCircleSegments);
    public static Setting<int> UIRingSegments { get; } = new(Defaults.UIRingSegments);

    // ====================== Вспомогательный класс ======================
    public class Setting<T>
    {
        public T Value { get; set; }
        public T Default { get; }

        public Setting(T defaultValue)
        {
            Default = defaultValue;
            Value = defaultValue;
        }

        public static implicit operator T(Setting<T> s) => s.Value;
        public static implicit operator Setting<T>(T value) => new(value) { Value = value };
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true
    };

    // ====================== АВТОМАТИКА (рефлексия) ======================
    private static readonly Dictionary<string, object> _settingProperties = 
        typeof(Settings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType.IsGenericType && 
                       p.PropertyType.GetGenericTypeDefinition() == typeof(Setting<>))
            .ToDictionary(p => p.Name, p => p.GetValue(null)!);

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
            var data = JsonSerializer.Deserialize<SettingsData>(json, _jsonOptions);

            if (data?.Values != null)
            {
                foreach (var kvp in data.Values)
                {
                    if (_settingProperties.TryGetValue(kvp.Key, out var settingObj))
                    {
                        var settingType = settingObj.GetType();
                        var valueProp = settingType.GetProperty("Value");
                        valueProp?.SetValue(settingObj, kvp.Value);
                    }
                }
            }

            if (data?.Colors?.Count > 3)
                data.Colors = data.Colors.Take(3).ToList();
        }
        catch { /* fallback */ }
    }

    public static void Save()
    {
        try
        {
            var values = new Dictionary<string, object>();

            foreach (var kvp in _settingProperties)
            {
                var settingObj = kvp.Value;
                var valueProp = settingObj.GetType().GetProperty("Value");
                values[kvp.Key] = valueProp?.GetValue(settingObj)!;
            }

            var data = new SettingsData
            {
                Colors = Colors.Select(Vector4ToHex).ToList(),
                Values = values
            };

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(Path, json);
        }
        catch { }
    }
    
    private static Vector4 HexToVector4(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return new Vector4(1, 1, 1, 1);

        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
            byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
            byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
            return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
        }
        return new Vector4(1, 1, 1, 1); // fallback
    }

    private static string Vector4ToHex(Vector4 color)
    {
        byte r = (byte)Math.Clamp(color.X * 255, 0, 255);
        byte g = (byte)Math.Clamp(color.Y * 255, 0, 255);
        byte b = (byte)Math.Clamp(color.Z * 255, 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    public static void ResetToDefaults()
    {
        Colors = Defaults.Colors.Select(HexToVector4).ToList();
        BackgroundColor.Value = HexToVector4(Defaults.DefaultBackgroundColor);

        foreach (var kvp in _settingProperties)
        {
            var settingObj = kvp.Value;
            var defaultProp = settingObj.GetType().GetProperty("Default");
            var valueProp = settingObj.GetType().GetProperty("Value");

            var defaultValue = defaultProp?.GetValue(settingObj);
            valueProp?.SetValue(settingObj, defaultValue);
        }

        Save();
    }

    private class SettingsData
    {
        public List<string> Colors { get; set; } = [];
        public Dictionary<string, object> Values { get; set; } = new();
    }
}