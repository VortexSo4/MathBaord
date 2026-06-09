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
        public static readonly List<Vector4> Colors = 
        [
            new(0.0f, 0.0f, 0.0f, 1.0f),
            new(0.0f, 0.3f, 0.8f, 1.0f),
            new(0.8f, 0.1f, 0.1f, 1.0f),
            new(0.1f, 0.7f, 0.1f, 1.0f)
        ];

        public const string LibraryRootPath = "Lessons";
        public const int AutoSaveIntervalMinutes = 1;
        public const bool LibraryPanelOnTop = true;

        public const float RadialMenuLongPressThreshold = 1.0f;
        public const float RadialMenuOpenThreshold = 0.3f;
        public const float RadialMenuEscapeTime = 0.12f;
        public const float RadialMenuEscapeDistance = 12f;
        public const bool RadialMenuCloseOnToolSelect = true;

        public const float CameraZoomSpeed = 0.15f;
        public const float CameraMinZoom = 0.1f;
        public const float CameraMaxZoom = 30f;
        public const float CameraPanSpeed = 35f;

        public const float DefaultBrushWidth = 22f;
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
    public static List<Vector4> Colors { get; private set; } = new(Defaults.Colors);

    // ←←← Добавляешь новую настройку **только здесь** (одна строка):
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

            if (data?.Colors?.Count > 0)
                Colors = data.Colors;
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
                Colors = Colors,
                Values = values
            };

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(Path, json);
        }
        catch { }
    }

    public static void ResetToDefaults()
    {
        Colors = new(Defaults.Colors);

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
        public List<Vector4> Colors { get; set; } = new(Defaults.Colors);
        public Dictionary<string, object> Values { get; set; } = new();
    }
}