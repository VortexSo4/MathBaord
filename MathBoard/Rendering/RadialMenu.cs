using System.Numerics;
using MathBoard.Core;
using System;

namespace MathBoard.Rendering;

public class RadialMenu
{
    public bool IsOpen { get; private set; }
    public Vector2 Position { get; private set; }

    private readonly StrokeRenderer _renderer;

    private DateTime _pressStartTime;
    private const float ColorLongPressThreshold = 1.0f;

    private int _selectedIndex = -1;
    private bool _isConfirmingClear = false;
    private bool _isAdjustingThickness = false;
    private float _previewThickness = 22f;
    private float _thicknessBaseWidth = 22f;

    private const int SectorCount = 8;
    private const float OuterRadius = 145f;
    private const float InnerRadius = 56f;   // внутренний край кольца (чуть больше CenterRadius)
    private const float CenterRadius = 46f;
    private const float IconRadius = 100f;   // середина кольца (≈ (56+145)/2)

    // Угловой сдвиг: рендер повёрнут на PI, чтобы совпасть с формулой выбора мышью
    private const float RenderAngleOffset = MathF.PI;

    public RadialMenu(StrokeRenderer renderer)
    {
        _renderer = renderer;
        Settings.Load();
    }

    public void OpenAt(Vector2 screenPos)
    {
        Position = screenPos;
        IsOpen = true;
        _pressStartTime = DateTime.Now;
        _isConfirmingClear = false;
        _isAdjustingThickness = false;
        _selectedIndex = -1;
        _previewThickness = _renderer.CurrentBrushWidth;
        _thicknessBaseWidth = _renderer.CurrentBrushWidth;
        _renderer.SetDirty();
    }

    public void Close()
    {
        IsOpen = false;
        _isConfirmingClear = false;
        _isAdjustingThickness = false;
        _selectedIndex = -1;
        _renderer.SetDirty();
    }

    public void OnMouseMove(Vector2 screenPos)
    {
        if (!IsOpen) return;

        if (_isAdjustingThickness)
        {
            float deltaY = Position.Y - screenPos.Y;
            _previewThickness = Math.Clamp(_thicknessBaseWidth + deltaY * 0.6f, 4f, 90f);
            _renderer.SetDirty();
            return;
        }

        var dir = screenPos - Position;
        float dist = dir.Length();

        if (dist < CenterRadius || dist > OuterRadius * 1.35f)
        {
            _selectedIndex = -1;
            return;
        }

        // atan2 + PI → диапазон (0, 2PI], совпадает с тем как нарисованы секторы
        float angle = MathF.Atan2(dir.Y, dir.X) + MathF.PI;
        _selectedIndex = (int)(angle / (MathF.PI * 2f / SectorCount)) % SectorCount;
    }

    public void OnMouseUp(Vector2 screenPos)
    {
        if (!IsOpen) return;

        var timeHeld = (DateTime.Now - _pressStartTime).TotalSeconds;

        if (_isAdjustingThickness)
        {
            _renderer.CurrentBrushWidth = _previewThickness;
            Close();
            return;
        }

        if (_selectedIndex == -1)
        {
            if (_isConfirmingClear)
                _renderer.ClearAll();
            Close();
            return;
        }

        if (_selectedIndex <= 3)
            HandleToolSelection(_selectedIndex);
        else
        {
            int colorIdx = _selectedIndex - 4;
            if (colorIdx >= 0 && colorIdx < Settings.Colors.Count)
            {
                if (timeHeld > ColorLongPressThreshold)
                {
                    var rnd = new Random();
                    var newColor = new Vector4(
                        rnd.NextSingle() * 0.85f + 0.05f,
                        rnd.NextSingle() * 0.85f + 0.05f,
                        rnd.NextSingle() * 0.85f + 0.05f, 1f);
                    Settings.Colors[colorIdx] = newColor;
                    Settings.Save();
                    _renderer.SetColor(newColor);
                }
                else
                {
                    _renderer.SetColor(Settings.Colors[colorIdx]);
                }
            }
            Close();
        }
    }

    private void HandleToolSelection(int index)
    {
        switch (index)
        {
            case 0: _renderer.ToggleEraser(false); Close(); break;
            case 1: _renderer.ToggleEraser(true);  Close(); break;
            case 2:
                _isAdjustingThickness = true;
                _previewThickness = _renderer.CurrentBrushWidth;
                _thicknessBaseWidth = _renderer.CurrentBrushWidth;
                _renderer.SetDirty();
                break;
            case 3:
                _isConfirmingClear = true;
                _renderer.SetDirty();
                break;
        }
    }

    // ====================== РЕНДЕР ======================

    public void RenderUI(List<Vertex> vertices)
    {
        if (!IsOpen) return;

        var bgColor      = new Vector4(0.12f, 0.12f, 0.16f, 0.97f);
        var selectColor  = new Vector4(0.4f, 0.78f, 1.0f, 0.95f);
        var outlineColor = new Vector4(0.85f, 0.85f, 0.90f, 0.5f);
        var centerColor  = _isConfirmingClear
            ? new Vector4(0.85f, 0.15f, 0.15f, 0.97f)
            : bgColor;

        if (_isAdjustingThickness)
        {
            // Режим слайдера: только превью толщины, без секторов
            DrawThicknessPreview(vertices);
            DrawCenterButton(vertices, centerColor, outlineColor);
            return;
        }

        // Кольцевые секторы (рисуем ДО центра, чтобы центр перекрыл)
        float sectorAngle = MathF.PI * 2f / SectorCount;
        float gap = 0.04f; // угловой зазор между секторами (радиан)

        for (int i = 0; i < SectorCount; i++)
        {
            bool isSelected = i == _selectedIndex;

            // Сдвиг: сектор i отображается там, где его выбирает мышь
            float centerAngle = i * sectorAngle + RenderAngleOffset;
            float start = centerAngle - sectorAngle * 0.5f + gap;
            float end   = centerAngle + sectorAngle * 0.5f - gap;

            Vector4 fillColor;
            if (i >= 4)
            {
                int idx = i - 4;
                fillColor = idx < Settings.Colors.Count ? Settings.Colors[idx] : bgColor;
                if (isSelected)
                    fillColor = Vector4.Lerp(fillColor, Vector4.One, 0.35f);
            }
            else
            {
                fillColor = isSelected ? selectColor : bgColor;
            }

            // Чуть более широкий сектор как обводка
            DrawAnnularSector(vertices, Position, InnerRadius - 3, OuterRadius + 4, start, end, outlineColor, 32);
            DrawAnnularSector(vertices, Position, InnerRadius,     OuterRadius,     start, end, fillColor,   32);

            // Иконка
            DrawIcon(vertices, i, isSelected);
        }

        // Центральная кнопка поверх секторов
        DrawCenterButton(vertices, centerColor, outlineColor);
    }

    private void DrawCenterButton(List<Vertex> vertices, Vector4 fill, Vector4 outline)
    {
        DrawCircle(vertices, Position, CenterRadius + 5, outline, 48);
        DrawCircle(vertices, Position, CenterRadius,     fill,    48);

        // Иконка закрытия: крестик
        var cross = new Vector4(0.92f, 0.92f, 0.95f, 0.9f);
        DrawLine(vertices, Position + new Vector2(-11, -11), Position + new Vector2(11, 11), cross, 5f);
        DrawLine(vertices, Position + new Vector2( 11, -11), Position + new Vector2(-11, 11), cross, 5f);
    }

    private void DrawThicknessPreview(List<Vertex> vertices)
    {
        float r = _previewThickness * 0.5f;

        // Заполненный круг = размер будущего штриха
        DrawCircle(vertices, Position, r + 4f, new Vector4(0.15f, 0.15f, 0.18f, 0.9f), 56);
        // Вырезаем середину фоновым цветом → получается кольцо
        DrawCircle(vertices, Position, Math.Max(r - 4f, 1f), new Vector4(0.97f, 0.97f, 0.98f, 1.0f), 56);
    }

    private void DrawIcon(List<Vertex> vertices, int sectorIndex, bool isSelected)
    {
        float angle = sectorIndex * MathF.PI * 2f / SectorCount + RenderAngleOffset;
        var iconCenter = Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * IconRadius;
        var iconColor  = isSelected
            ? new Vector4(0.02f, 0.02f, 0.05f, 1f)
            : new Vector4(1f, 1f, 1f, 0.92f);

        switch (sectorIndex)
        {
            case 0: // Кисть
                DrawLine(vertices, iconCenter + new Vector2(-13, -11), iconCenter + new Vector2(13, 11), iconColor, 8f);
                break;
            case 1: // Ластик
                DrawRect(vertices, iconCenter, 26, 18, iconColor);
                break;
            case 2: // Толщина — два концентрических кружка
                DrawCircle(vertices, iconCenter, 13, iconColor, 22);
                DrawCircle(vertices, iconCenter,  6,
                    isSelected ? new Vector4(0.4f, 0.78f, 1f, 1f) : new Vector4(0.12f, 0.12f, 0.16f, 1f), 18);
                break;
            case 3: // Очистить — крест
                DrawLine(vertices, iconCenter + new Vector2(-11, -11), iconCenter + new Vector2(11, 11), iconColor, 7f);
                DrawLine(vertices, iconCenter + new Vector2( 11, -11), iconCenter + new Vector2(-11, 11), iconColor, 7f);
                break;
            default: // Цвет — белый кружок-точка
                DrawCircle(vertices, iconCenter, 9, new Vector4(1, 1, 1, 0.85f), 18);
                break;
        }
    }

    // ====================== ПРИМИТИВЫ ======================

    private void DrawAnnularSector(
        List<Vertex> v, Vector2 center,
        float r1, float r2,
        float start, float end,
        Vector4 color, int segments)
    {
        float step = (end - start) / segments;
        for (int i = 0; i < segments; i++)
        {
            float a1 = start + i * step;
            float a2 = a1 + step;
            var inner1 = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r1;
            var outer1 = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r2;
            var inner2 = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * r1;
            var outer2 = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * r2;

            v.Add(new Vertex { Position = inner1, Color = color });
            v.Add(new Vertex { Position = outer1, Color = color });
            v.Add(new Vertex { Position = inner2, Color = color });

            v.Add(new Vertex { Position = outer1, Color = color });
            v.Add(new Vertex { Position = outer2, Color = color });
            v.Add(new Vertex { Position = inner2, Color = color });
        }
    }

    private void DrawCircle(List<Vertex> v, Vector2 center, float r, Vector4 color, int segments)
    {
        float step = MathF.PI * 2f / segments;
        for (int i = 0; i < segments; i++)
        {
            float a1 = i * step;
            float a2 = (i + 1) * step;
            v.Add(new Vertex { Position = center, Color = color });
            v.Add(new Vertex { Position = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r, Color = color });
            v.Add(new Vertex { Position = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * r, Color = color });
        }
    }

    private void DrawLine(List<Vertex> v, Vector2 p1, Vector2 p2, Vector4 color, float thickness)
    {
        var dir  = Vector2.Normalize(p2 - p1);
        var perp = new Vector2(-dir.Y, dir.X) * (thickness * 0.5f);

        v.Add(new Vertex { Position = p1 + perp, Color = color });
        v.Add(new Vertex { Position = p1 - perp, Color = color });
        v.Add(new Vertex { Position = p2 + perp, Color = color });

        v.Add(new Vertex { Position = p2 + perp, Color = color });
        v.Add(new Vertex { Position = p1 - perp, Color = color });
        v.Add(new Vertex { Position = p2 - perp, Color = color });
    }

    private void DrawRect(List<Vertex> v, Vector2 center, float w, float h, Vector4 color)
    {
        var half = new Vector2(w * 0.5f, h * 0.5f);
        var p1 = center + new Vector2(-half.X, -half.Y);
        var p2 = center + new Vector2( half.X, -half.Y);
        var p3 = center + new Vector2( half.X,  half.Y);
        var p4 = center + new Vector2(-half.X,  half.Y);

        v.Add(new Vertex { Position = p1, Color = color }); v.Add(new Vertex { Position = p2, Color = color }); v.Add(new Vertex { Position = p3, Color = color });
        v.Add(new Vertex { Position = p1, Color = color }); v.Add(new Vertex { Position = p3, Color = color }); v.Add(new Vertex { Position = p4, Color = color });
    }
}