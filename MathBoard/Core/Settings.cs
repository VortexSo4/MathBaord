using System.Numerics;
using System.Text.Json;

namespace MathBoard.Core;

public static class Settings
{
    private static readonly string Path = "MathBoardSettings.json";

    // Цвета (существующие)
    public static List<Vector4> Colors { get; private set; } =
    [
        new(0.0f, 0.0f, 0.0f, 1.0f),   // Black
        new(0.0f, 0.3f, 0.8f, 1.0f),   // Blue
        new(0.8f, 0.1f, 0.1f, 1.0f),   // Red
        new(0.1f, 0.7f, 0.1f, 1.0f)    // Green
    ];

    // === НОВЫЕ НАСТРОЙКИ РАДИАЛЬНОГО МЕНЮ ===
    public static float RadialMenuLongPressThreshold { get; set; } = 1.0f;      // Долгое нажатие для открытия редактора цвета (сек)
    public static float RadialMenuOpenThreshold { get; set; } = 0.6f;          // Порог открытия меню (сек)
    public static float RadialMenuEscapeTime { get; set; } = 0.6f;             // Время, в течение которого можно отменить меню движением (сек)
    public static float RadialMenuEscapeDistance { get; set; } = 8f;             // Дистанция для отмены меню (пиксели)

    // === НАСТРОЙКИ КАМЕРЫ И УПРАВЛЕНИЯ ===
    public static float CameraZoomSpeed { get; set; } = 0.15f;                   // Скорость зума (множитель)
    public static float CameraMinZoom { get; set; } = 0.1f;                      // Минимальный зум
    public static float CameraMaxZoom { get; set; } = 30f;                       // Максимальный зум
    public static float CameraPanSpeed { get; set; } = 35f;                      // Скорость панорамирования (пикселей на тик колесика)

    // === НАСТРОЙКИ РИСОВАНИЯ ===
    public static float DefaultBrushWidth { get; set; } = 22f;                   // Стандартная толщина кисти
    public static float DefaultEraserSize { get; set; } = 8f;                    // Стандартный размер ластика
    public static float MinBrushWidth { get; set; } = 4f;                        // Минимальная толщина кисти
    public static float MaxBrushWidth { get; set; } = 90f;                       // Максимальная толщина кисти

    // === НАСТРОЙКИ ИНТЕРФЕЙСА ===
    public static float RadialMenuOuterRadius { get; set; } = 145f;              // Внешний радиус радиального меню
    public static float RadialMenuInnerRadius { get; set; } = 56f;               // Внутренний радиус радиального меню
    public static float RadialMenuCenterRadius { get; set; } = 46f;              // Радиус центральной кнопки

    // === НАСТРОЙКИ КАЧЕСТВА ===
    public static int StrokeCircleSegments { get; set; } = 14;                   // Количество сегментов для кругов в штрихах
    public static int UIRingSegments { get; set; } = 128;                        // Количество сегментов для колец UI

    // Vector4 uses public fields (X,Y,Z,W), not properties.
    // System.Text.Json skips fields by default — IncludeFields fixes that.
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true
    };

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
            if (data != null)
            {
                if (data.Colors != null && data.Colors.Count > 0)
                    Colors = data.Colors;
                
                // Загружаем новые настройки
                RadialMenuLongPressThreshold = data.RadialMenuLongPressThreshold;
                RadialMenuOpenThreshold = data.RadialMenuOpenThreshold;
                RadialMenuEscapeTime = data.RadialMenuEscapeTime;
                RadialMenuEscapeDistance = data.RadialMenuEscapeDistance;
                CameraZoomSpeed = data.CameraZoomSpeed;
                CameraMinZoom = data.CameraMinZoom;
                CameraMaxZoom = data.CameraMaxZoom;
                CameraPanSpeed = data.CameraPanSpeed;
                DefaultBrushWidth = data.DefaultBrushWidth;
                DefaultEraserSize = data.DefaultEraserSize;
                MinBrushWidth = data.MinBrushWidth;
                MaxBrushWidth = data.MaxBrushWidth;
                RadialMenuOuterRadius = data.RadialMenuOuterRadius;
                RadialMenuInnerRadius = data.RadialMenuInnerRadius;
                RadialMenuCenterRadius = data.RadialMenuCenterRadius;
                StrokeCircleSegments = data.StrokeCircleSegments;
                UIRingSegments = data.UIRingSegments;
            }
        }
        catch { /* fallback to defaults */ }
    }

    public static void Save()
    {
        try
        {
            var data = new SettingsData 
            { 
                Colors = Colors,
                RadialMenuLongPressThreshold = RadialMenuLongPressThreshold,
                RadialMenuOpenThreshold = RadialMenuOpenThreshold,
                RadialMenuEscapeTime = RadialMenuEscapeTime,
                RadialMenuEscapeDistance = RadialMenuEscapeDistance,
                CameraZoomSpeed = CameraZoomSpeed,
                CameraMinZoom = CameraMinZoom,
                CameraMaxZoom = CameraMaxZoom,
                CameraPanSpeed = CameraPanSpeed,
                DefaultBrushWidth = DefaultBrushWidth,
                DefaultEraserSize = DefaultEraserSize,
                MinBrushWidth = MinBrushWidth,
                MaxBrushWidth = MaxBrushWidth,
                RadialMenuOuterRadius = RadialMenuOuterRadius,
                RadialMenuInnerRadius = RadialMenuInnerRadius,
                RadialMenuCenterRadius = RadialMenuCenterRadius,
                StrokeCircleSegments = StrokeCircleSegments,
                UIRingSegments = UIRingSegments
            };
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(Path, json);
        }
        catch { }
    }

    private class SettingsData
    {
        public List<Vector4> Colors { get; set; } = [];
        
        // Настройки радиального меню
        public float RadialMenuLongPressThreshold { get; set; } = 1.0f;
        public float RadialMenuOpenThreshold { get; set; } = 0.12f;
        public float RadialMenuEscapeTime { get; set; } = 0.12f;
        public float RadialMenuEscapeDistance { get; set; } = 8f;
        
        // Настройки камеры
        public float CameraZoomSpeed { get; set; } = 0.15f;
        public float CameraMinZoom { get; set; } = 0.1f;
        public float CameraMaxZoom { get; set; } = 30f;
        public float CameraPanSpeed { get; set; } = 35f;
        
        // Настройки рисования
        public float DefaultBrushWidth { get; set; } = 22f;
        public float DefaultEraserSize { get; set; } = 8f;
        public float MinBrushWidth { get; set; } = 4f;
        public float MaxBrushWidth { get; set; } = 90f;
        
        // Настройки интерфейса
        public float RadialMenuOuterRadius { get; set; } = 145f;
        public float RadialMenuInnerRadius { get; set; } = 56f;
        public float RadialMenuCenterRadius { get; set; } = 46f;
        
        // Настройки качества
        public int StrokeCircleSegments { get; set; } = 14;
        public int UIRingSegments { get; set; } = 128;
    }
    
    public static void ResetToDefaults()
    {
        Colors = [
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.3f, 0.8f, 1.0f),
            new Vector4(0.8f, 0.1f, 0.1f, 1.0f),
            new Vector4(0.1f, 0.7f, 0.1f, 1.0f)
        ];
    
        RadialMenuLongPressThreshold = 1.0f;
        RadialMenuOpenThreshold = 0.6f;
        RadialMenuEscapeTime = 0.6f;
        RadialMenuEscapeDistance = 8f;
        CameraZoomSpeed = 0.15f;
        CameraMinZoom = 0.1f;
        CameraMaxZoom = 30f;
        CameraPanSpeed = 35f;
        DefaultBrushWidth = 22f;
        DefaultEraserSize = 8f;
        MinBrushWidth = 4f;
        MaxBrushWidth = 90f;
        RadialMenuOuterRadius = 145f;
        RadialMenuInnerRadius = 56f;
        RadialMenuCenterRadius = 46f;
        StrokeCircleSegments = 14;
        UIRingSegments = 128;
    
        Save();
    }
}