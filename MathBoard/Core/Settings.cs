using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;

namespace MathBoard.Core;

public static class Settings
{
    private static readonly string SettingPath = Path.Combine(AppPaths.Data, "MathBoardSettings.json");

    // ====================== ОДИН ИСТОЧНИК ПРАВДЫ ======================
    private static class Defaults
    {
        public const string Language = "EN_US";

        public static readonly string DefaultBackgroundColor = "#121212";

        public static readonly string LibraryRootPath = Path.Combine(AppPaths.Data, "Lessons");
        public const int AutoSaveIntervalMinutes = 1;
        public const string RenderBackend = "Auto";
        public const float UiScale = 0f;
        public const string OnScreenKeyboard = "Auto";
        public const string GridMode = "None";

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

        public static readonly QuickButtonConfig[] QuickButtons =
        [
            new(UiAction.ToggleLibrary, 0f, 0f, 34f, 34f),
            new(UiAction.Redo, 1f, 1f, -34f, -398f),
            new(UiAction.Save, 1f, 0f, -34f, 34f),
            new(UiAction.Brush, 1f, 1f, -138f, -190f),
            new(UiAction.Eraser, 1f, 1f, -86f, -190f),
            new(UiAction.Select, 1f, 1f, -34f, -242f),
            new(UiAction.Pan, 1f, 1f, -34f, -190f),
            new(UiAction.Color, 0.5f, 1f, -52f, -34f, HexToVector4("#B7B7B7")),
            new(UiAction.Color, 0.5f, 1f, 0f, -34f, HexToVector4("#FF8383")),
            new(UiAction.Color, 0.5f, 1f, 52f, -34f, HexToVector4("#3A994C")),
            new(UiAction.Color, 0.5f, 1f, 104f, -34f, HexToVector4("#FFD54F")),
            new(UiAction.Color, 0.5f, 1f, -104f, -34f, HexToVector4("#5B9BFF")),
            new(UiAction.ZoomOut, 1f, 1f, -138f, -86f),
            new(UiAction.FitView, 1f, 1f, -34f, -34f),
            new(UiAction.ZoomIn, 1f, 1f, -34f, -86f),
            new(UiAction.Undo, 1f, 1f, -86f, -398f),
            new(UiAction.Shapes, 1f, 1f, -86f, -242f),
            new(UiAction.InsertImage, 1f, 1f, -86f, -294f),
            new(UiAction.Screenshot, 1f, 1f, -34f, -294f),
            new(UiAction.Export, 1f, 0f, -86f, 34f),
            new(UiAction.Grid, 1f, 1f, -138f, -34f),
            new(UiAction.Fill, 1f, 1f, -138f, -242f),
            new(UiAction.ZoomLevel, 1f, 1f, -86f, -86f),
            new(UiAction.Minimap, 1f, 1f, -86f, -34f),
            new(UiAction.ClearBoard, 1f, 0f, -138f, 34f),
            new(UiAction.FormatPainter, 1f, 1f, -138f, -294f)
        ];
    }

    // ====================== Настройки ======================
    public static Setting<string> Language { get; } = new(Defaults.Language);

    public static Setting<Vector4> BackgroundColor { get; } =
        new(HexToVector4(Defaults.DefaultBackgroundColor));

    public static Setting<bool> LightTheme { get; } = new(false);
    public static Setting<Vector4> BackgroundColorLight { get; } = new(HexToVector4("#F7F7F4"));

    /// <summary>The board's background in the current theme; each theme keeps its own.</summary>
    public static Vector4 Background
    {
        get => LightTheme ? BackgroundColorLight : BackgroundColor;
        set
        {
            if (LightTheme) BackgroundColorLight.Value = value;
            else BackgroundColor.Value = value;
        }
    }

    public static Setting<string> LibraryRootPath { get; } = new(Defaults.LibraryRootPath);
    public static Setting<int> AutoSaveIntervalMinutes { get; } = new(Defaults.AutoSaveIntervalMinutes);

    /// <summary>Preferred render backend name, or "Auto". Takes effect on the next start.</summary>
    public static Setting<string> RenderBackend { get; } = new(Defaults.RenderBackend);

    /// <summary>Interface scale; 0 follows the monitor's DPI.</summary>
    public static Setting<float> UiScale { get; } = new(Defaults.UiScale);

    /// <summary>"Auto" shows the touch keyboard for text fields when no hardware keyboard is attached; "Always" or "Never".</summary>
    public static Setting<string> OnScreenKeyboard { get; } = new(Defaults.OnScreenKeyboard);

    public static Setting<string> LastOpenedFile { get; } = new("");

    /// <summary>How far (logical px) a pen may tremble and still count as held still: for the radial menu and shape recognition.</summary>
    public static Setting<float> HoldTolerance { get; } = new(12f);

    /// <summary>A touch at least this wide (a palm or a fist) erases; 0 turns it off.</summary>
    public static Setting<float> PalmEraseMillimetres { get; } = new(30f);
    // Off until the board's stylus ends are measured: see the "Touch down" lines in the log.
    public static Setting<float> StylusEraseMillimetres { get; } = new(0f);

    /// <summary>A selection box smaller than this (logical px) counts as a tap and selects one thing.</summary>
    public static Setting<float> SelectionTapSize { get; } = new(20f);

    /// <summary>How far (logical px) from a tap the selection looks for the nearest thing.</summary>
    public static Setting<float> SelectionRadius { get; } = new(20f);

    /// <summary>Look for a newer release on GitHub at startup.</summary>
    public static Setting<bool> CheckUpdates { get; } = new(true);

    // Working together: the stroke being drawn is sent as it grows (or once finished), changes go out every so many
    // milliseconds, cut into packets of up to this many bytes.
    public static Setting<bool> NetLiveStrokes { get; } = new(true);
    public static Setting<float> NetSendIntervalMs { get; } = new(50f);
    public static Setting<float> NetPacketBytes { get; } = new(1100f);

    /// <summary>Background grid: "None", "Squares" or "Axes". It is not part of the document.</summary>
    public static Setting<string> GridMode { get; } = new(Defaults.GridMode);

    /// <summary>Names of the backend plugins found at startup (not persisted).</summary>
    public static List<string> AvailableBackends { get; set; } = [];

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

    public static readonly UiAction[] DefaultRadialItems =
        [UiAction.Thickness, UiAction.Eraser, UiAction.Brush, UiAction.Undo, UiAction.Redo, UiAction.Select, UiAction.Shapes];

    /// <summary>Sectors of the radial menu, clockwise from the bottom. Replace the array when changing it.</summary>
    public static Setting<UiAction[]> RadialItems { get; } = new(DefaultRadialItems);

    /// <summary>Visible quick buttons. Replace the array when changing it: the default instance is shared.</summary>
    public static Setting<QuickButtonConfig[]> QuickButtons { get; } = new(Defaults.QuickButtons);

    /// <summary>The colors of the color buttons, in layout order.</summary>
    public static List<Vector4> Palette => QuickButtons.Value.Where(b => b.Color != null).Select(b => b.Color!.Value).ToList();

    /// <summary>Replaces <paramref name="button"/> with a copy of another color and returns the copy.</summary>
    public static QuickButtonConfig SetButtonColor(QuickButtonConfig button, Vector4 color)
    {
        var buttons = QuickButtons.Value.ToArray();
        var changed = button with { Color = color };
        int index = Array.IndexOf(buttons, button);
        if (index >= 0)
        {
            buttons[index] = changed;
            QuickButtons.Value = buttons;
        }

        return changed;
    }

    public static void SetPaletteColor(int index, Vector4 color) =>
        SetButtonColor(QuickButtons.Value.Where(b => b.Color != null).ElementAt(index), color);

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
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() }
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
        if (!File.Exists(SettingPath))
        {
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingPath);
            using var doc = JsonDocument.Parse(json);

            // Before 1.1 there were exactly three color buttons (Color1..3) taking their colors from "Colors".
            var legacyColors = doc.RootElement.TryGetProperty("Colors", out var colorsElem) && colorsElem.ValueKind == JsonValueKind.Array
                ? colorsElem.Deserialize<List<string>>(_jsonOptions)!.Select(HexToVector4).ToList()
                : [];

            if (doc.RootElement.TryGetProperty("Values", out var valuesElem))
            {
                foreach (var prop in valuesElem.EnumerateObject())
                {
                    if (!_settingProperties.TryGetValue(prop.Name, out var settingObj))
                        continue;

                    var settingType = settingObj.GetType();
                    var valueProp = settingType.GetProperty("Value");
                    if (valueProp == null) continue;

                    var targetType = valueProp.PropertyType;
                    object? value = null;
                    var elem = prop.Value;

                    try
                    {
                        if (prop.Name == nameof(QuickButtons))
                            value = JsonSerializer.Deserialize(MigrateColorButtons(elem.GetRawText(), legacyColors), targetType, _jsonOptions);
                        else if (targetType == typeof(string))
                            value = elem.ValueKind == JsonValueKind.String
                                ? elem.GetString()
                                : elem.GetRawText();
                        else if (targetType == typeof(float))
                            value = elem.GetSingle();
                        else if (targetType == typeof(int))
                            value = elem.GetInt32();
                        else if (targetType == typeof(bool))
                            value = elem.GetBoolean();
                        else
                            value = elem.Deserialize(targetType, _jsonOptions);
                    }
                    catch
                    {
                        continue;
                    }

                    if (value != null)
                        valueProp.SetValue(settingObj, value);
                }
            }
        }
        catch
        {
            /* fallback to defaults */
        }
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

            var data = new SettingsData { Values = values };

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(SettingPath, json);
        }
        catch
        {
        }
    }

    private static string MigrateColorButtons(string json, List<Vector4> colors) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"Action\":\\s*\"Color([123])\"", m =>
        {
            int i = m.Groups[1].Value[0] - '1';
            var c = i < colors.Count ? colors[i] : Vector4.One;
            return FormattableString.Invariant($"\"Action\": \"Color\", \"Color\": {{\"X\": {c.X}, \"Y\": {c.Y}, \"Z\": {c.Z}, \"W\": 1}}");
        });

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

    public static void ResetToDefaults()
    {
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
        public Dictionary<string, object> Values { get; set; } = new();
    }
}

/// <summary>A quick button centered at <c>anchor * screenSize + offset</c>; color buttons carry their color.</summary>
public sealed record QuickButtonConfig(UiAction Action, float AnchorX, float AnchorY, float OffsetX, float OffsetY, Vector4? Color = null);
