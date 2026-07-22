﻿using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

public class RadialMenu
{
    public bool IsOpen { get; private set; }
    public Vector2 Position { get; private set; }

    private readonly StrokeRenderer _renderer;

    private DateTime _pressStartTime;

    private int _selectedIndex = -1;
    private bool _isConfirmingClear;
    private bool _isAdjustingThickness;
    private float _previewThickness = 22f;
    private float _thicknessBaseWidth = 22f;

    private bool _isPickingBackground;
    private Vector4 _tempBackgroundColor;

    private bool _isColorMenuOpen = false;

    private bool _isPickingColor;
    private int _colorEditIndex = -1;
    private float _pickerHue;
    private float _pickerSaturation = 1f;
    private float _pickerValue = 1f;
    private int _activePickerRing = -1;

    private const float PickerOuterRadius = 130f;
    private const float PickerRingWidth = 28f;
    private const float PickerCenterRadius = 18f;

    private const int SectorCount = 6;
    private int CurrentSectorCount => _isColorMenuOpen ? 4 : SectorCount;
    private static float OuterRadius => Settings.RadialMenuOuterRadius;
    private static float InnerRadius => Settings.RadialMenuInnerRadius;
    private static float CenterRadius => Settings.RadialMenuCenterRadius;
    private float IconRadius => (OuterRadius + InnerRadius) * 0.55f;
    private TextAtlas.Entry _eraserIcon, _brushIcon, _thicknessIcon, _clearIcon, _selectIcon, _colorpicker;

    private const float RenderAngleOffset = MathF.PI;
    private bool _isMouseDown;

    public RadialMenu(StrokeRenderer renderer)
    {
        _renderer = renderer;
        Settings.Load();
        _previewThickness = Settings.DefaultBrushWidth;
        _thicknessBaseWidth = Settings.DefaultBrushWidth;
    }
    
    public void RequestIcons(TextAtlas atlas)
    {
        _eraserIcon    = atlas.RequestImage("resources/textures/eraser.png");
        _brushIcon     = atlas.RequestImage("resources/textures/brush.png");
        _thicknessIcon = atlas.RequestImage("resources/textures/thickness.png");
        _clearIcon     = atlas.RequestImage("resources/textures/clear.png");
        _brushIcon     = atlas.RequestImage("resources/textures/brush.png");
        _selectIcon    = atlas.RequestImage("resources/textures/select.png");
        _colorpicker   = atlas.RequestImage("resources/textures/colorpicker.png");
    }

    public void OpenAt(Vector2 screenPos)
    {
        Position = screenPos;
        IsOpen = true;
        _pressStartTime = DateTime.Now;
        _isConfirmingClear = false;
        _isAdjustingThickness = false;
        _isPickingColor = false;
        _isColorMenuOpen = false;
        _activePickerRing = -1;
        _isMouseDown = false;
        _selectedIndex = -1;
        _previewThickness = _renderer.CurrentBrushWidth;
        _thicknessBaseWidth = _renderer.CurrentBrushWidth;
        _renderer.SetDirty();
        _isPickingBackground = false;
    }

    public void Close()
    {
        IsOpen = false;
        _isConfirmingClear = false;
        _isAdjustingThickness = false;
        _isPickingColor = false;
        _isPickingBackground = false;
        _isColorMenuOpen = false;
        _activePickerRing = -1;
        _isMouseDown = false;
        _selectedIndex = -1;
        _renderer.SetDirty();
    }

    public void OnMouseDown(Vector2 screenPos)
    {
        if (!IsOpen) return;
        _isMouseDown = true;

        if (_isPickingColor || _isPickingBackground)
        {
            var dir = screenPos - Position;
            float dist = dir.Length();

            if (dist <= PickerCenterRadius)
            {
                _activePickerRing = -1;
                return;
            }

            float r1 = PickerCenterRadius + 6f;
            float r2 = r1 + PickerRingWidth;
            float gap = 6f;
            float r3 = r2 + gap + PickerRingWidth;
            float r4 = r3 + gap + PickerRingWidth;

            if (dist >= r1 && dist <= r2) _activePickerRing = 0;
            else if (dist >= r2 + gap && dist <= r3) _activePickerRing = 1;
            else if (dist >= r3 + gap && dist <= r4) _activePickerRing = 2;
            else _activePickerRing = -1;

            if (_activePickerRing >= 0)
                HandlePickerMove(screenPos);
        }
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

        if (_isPickingColor || _isPickingBackground)
        {
            if (_isMouseDown && _activePickerRing >= 0)
                HandlePickerMove(screenPos);
            return;
        }

        var dir = screenPos - Position;
        float dist = dir.Length();

        if (dist < CenterRadius || dist > OuterRadius * 1.35f)
        {
            _selectedIndex = -1;
            return;
        }

        float angle = MathF.Atan2(dir.Y, dir.X) + MathF.PI;
        float sectorAngle = MathF.PI * 2f / CurrentSectorCount;
        float shiftedAngle = angle + sectorAngle * 0.5f;
        _selectedIndex = (int)(shiftedAngle / sectorAngle) % CurrentSectorCount;
    }

    public void OnMouseUp(Vector2 screenPos)
    {
        if (!IsOpen) return;
        _isMouseDown = false;

        if (_isPickingColor)
        {
            var dir = screenPos - Position;
            if (dir.Length() <= PickerCenterRadius)
                ApplyPickerColor();
            _activePickerRing = -1;
            return;
        }

        if (_isPickingBackground)
        {
            var dir = screenPos - Position;
            if (dir.Length() <= PickerCenterRadius)
            {
                ApplyBackgroundPicker();
            }
            else
            {
                _activePickerRing = -1;
            }
            return;
        }

        if (_isAdjustingThickness)
        {
            _renderer.CurrentBrushWidth = _previewThickness;
            _isAdjustingThickness = false;
            if (Settings.RadialMenuCloseOnToolSelect)
                Close();
            else
                _renderer.SetDirty();
            return;
        }

        var dirCenter = screenPos - Position;
        bool isCenterClick = dirCenter.Length() <= CenterRadius + 10f;

        if (_isConfirmingClear)
        {
            if (isCenterClick)
            {
                _renderer.ClearAll();
                Close();
            }
            else
            {
                _isConfirmingClear = false;
                _renderer.SetDirty();
            }
            return;
        }

        if (isCenterClick)
        {
            Close();
            return;
        }

        if (_selectedIndex == -1)
            return;

        if (_isColorMenuOpen)
        {
            if (_selectedIndex == 0) HandleColorPress(0);
            else if (_selectedIndex == 1) HandleColorPress(1);
            else if (_selectedIndex == 2) HandleColorPress(2);
            else if (_selectedIndex == 3) OpenBackgroundPicker();
            return;
        }

        if (_selectedIndex <= 3)
            HandleToolSelection(_selectedIndex);
        else if (_selectedIndex == 4)
        {
            _renderer.IsSelectMode = !_renderer.IsSelectMode;
            if (_renderer.IsSelectMode) _renderer.ClearSelection();
            if (Settings.RadialMenuCloseOnToolSelect) Close();
            else _renderer.SetDirty();
        }
        else if (_selectedIndex == 5)
        {
            _isColorMenuOpen = true;
            _renderer.SetDirty();
        }
    }

    private void HandleColorPress(int colorIdx)
    {
        if ((DateTime.Now - _pressStartTime).TotalSeconds > Settings.RadialMenuLongPressThreshold)
            OpenColorPicker(colorIdx);
        else
        {
            _renderer.SetColor(Settings.Colors[colorIdx]);
            if (Settings.RadialMenuCloseOnToolSelect) Close();
            else _isColorMenuOpen = false;
            _renderer.SetDirty();
        }
    }

    private void HandlePickerMove(Vector2 screenPos)
    {
        var dir = screenPos - Position;
        float angle = MathF.Atan2(dir.Y, dir.X);
        float raw = (angle + MathF.PI * 0.5f) / (MathF.PI * 2f);
        float value = raw < 0f ? raw + 1f : raw;

        switch (_activePickerRing)
        {
            case 0: _pickerHue = value; break;
            case 1: _pickerSaturation = value; break;
            case 2: _pickerValue = value; break;
        }

        var newColor = HsvToRgb(_pickerHue, _pickerSaturation, _pickerValue);

        if (_isPickingBackground)
        {
            Settings.BackgroundColor.Value = newColor;
            _renderer.SetDirty();
            return;
        }

        _renderer.SetColor(newColor);
        _renderer.SetDirty();
    }

    private void OpenColorPicker(int colorIndex)
    {
        _isPickingColor = true;
        _colorEditIndex = colorIndex;
        var currentColor = Settings.Colors[colorIndex];
        RgbToHsv(currentColor, out _pickerHue, out _pickerSaturation, out _pickerValue);
        _activePickerRing = -1;
        _selectedIndex = -1;
        _renderer.SetDirty();
    }

    private void ApplyPickerColor()
    {
        var newColor = HsvToRgb(_pickerHue, _pickerSaturation, _pickerValue);
        Settings.Colors[_colorEditIndex] = newColor;
        Settings.Save();
        _renderer.SetColor(newColor);
        Close();
    }

    private void HandleToolSelection(int index)
    {
        switch (index)
        {
            case 0:
                _renderer.ToggleEraser(false);
                _renderer.IsSelectMode = false;
                if (Settings.RadialMenuCloseOnToolSelect) Close();
                break;
            case 1:
                _renderer.ToggleEraser(true);
                _renderer.IsSelectMode = false;
                if (Settings.RadialMenuCloseOnToolSelect) Close();
                break;
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

        var bgColor = new Vector4(0.12f, 0.12f, 0.16f, 0.97f);
        var selectColor = new Vector4(0.4f, 0.78f, 1.0f, 0.95f);
        var outlineColor = new Vector4(0.85f, 0.85f, 0.90f, 0.5f);
        var centerColor = _isConfirmingClear
            ? new Vector4(0.85f, 0.15f, 0.15f, 0.97f)
            : bgColor;

        if (_isPickingColor || _isPickingBackground)
        {
            RenderColorPicker(vertices);
            return;
        }

        if (_isAdjustingThickness)
        {
            DrawThicknessPreview(vertices);
            return;
        }

        int sectorCount = CurrentSectorCount;
        float sectorAngle = MathF.PI * 2f / sectorCount;
        float gap = 0.04f;

        for (int i = 0; i < sectorCount; i++)
        {
            bool isSelected = i == _selectedIndex;
            float centerAngle = i * sectorAngle + RenderAngleOffset;
            float start = centerAngle - sectorAngle * 0.5f + gap;
            float end = centerAngle + sectorAngle * 0.5f - gap;

            Vector4 fillColor;
            if (_isColorMenuOpen)
            {
                if (i == 3) fillColor = Settings.BackgroundColor.Value;
                else fillColor = Settings.Colors[i];
                if (isSelected) fillColor = Vector4.Lerp(fillColor, Vector4.One, 0.35f);
            }
            else
            {
                fillColor = isSelected ? selectColor : bgColor;
            }

            DrawAnnularSector(vertices, Position, InnerRadius - 3, OuterRadius + 4, start, end, outlineColor, 32);
            DrawAnnularSector(vertices, Position, InnerRadius, OuterRadius, start, end, fillColor, 32);
            
            if (!_isColorMenuOpen) DrawIcon(vertices, i, isSelected);
        }

        DrawCenterButton(vertices, centerColor, outlineColor);
    }

    private void RenderColorPicker(List<Vertex> vertices)
    {
        var bgColor = new Vector4(0.10f, 0.10f, 0.13f, 0.97f);
        var outlineColor = new Vector4(0.85f, 0.85f, 0.90f, 0.4f);
        var previewColor = HsvToRgb(_pickerHue, _pickerSaturation, _pickerValue);

        DrawCircle(vertices, Position, PickerOuterRadius + 8f, bgColor, 64);

        float r1 = PickerCenterRadius + 6f;
        float r2 = r1 + PickerRingWidth;
        float gap = 6f;
        float r3 = r2 + gap + PickerRingWidth;
        float r4 = r3 + gap + PickerRingWidth;

        DrawHueRing(vertices, Position, r1, r2, 128);
        if (_activePickerRing == 0) DrawRingBorder(vertices, Position, r1, r2, new Vector4(1, 1, 1, 0.8f), 3f);
        DrawRingIndicator(vertices, Position, (r1 + r2) * 0.5f, _pickerHue);

        DrawSatValRing(vertices, Position, r2 + gap, r3, _pickerHue, true, 128);
        if (_activePickerRing == 1) DrawRingBorder(vertices, Position, r2 + gap, r3, new Vector4(1, 1, 1, 0.8f), 3f);
        DrawRingIndicator(vertices, Position, (r2 + gap + r3) * 0.5f, _pickerSaturation);

        DrawSatValRing(vertices, Position, r3 + gap, r4, _pickerHue, false, 128);
        if (_activePickerRing == 2) DrawRingBorder(vertices, Position, r3 + gap, r4, new Vector4(1, 1, 1, 0.8f), 3f);
        DrawRingIndicator(vertices, Position, (r3 + gap + r4) * 0.5f, _pickerValue);

        DrawCircle(vertices, Position, PickerCenterRadius + 3f, outlineColor, 48);
        DrawCircle(vertices, Position, PickerCenterRadius, previewColor, 48);
        DrawCheckmark(vertices, Position);
    }

    private static void DrawHueRing(List<Vertex> vertices, Vector2 center, float r1, float r2, int segments)
    {
        float step = MathF.PI * 2f / segments;
        for (int i = 0; i < segments; i++)
        {
            float a1 = i * step; float a2 = (i + 1) * step;
            float hue1 = i / (float)segments; float hue2 = (i + 1) / (float)segments;
            var c1 = HsvToRgb(hue1, 1f, 1f); var c2 = HsvToRgb(hue2, 1f, 1f);

            var inner1 = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r1;
            var outer1 = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r2;
            var inner2 = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * r1;
            var outer2 = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * r2;

            vertices.Add(new Vertex { Position = inner1, Color = c1 });
            vertices.Add(new Vertex { Position = outer1, Color = c1 });
            vertices.Add(new Vertex { Position = inner2, Color = c2 });

            vertices.Add(new Vertex { Position = outer1, Color = c1 });
            vertices.Add(new Vertex { Position = outer2, Color = c2 });
            vertices.Add(new Vertex { Position = inner2, Color = c2 });
        }
    }

    private static void DrawSatValRing(List<Vertex> vertices, Vector2 center, float r1, float r2, float hue, bool isSaturation, int segments)
    {
        float step = MathF.PI * 2f / segments;
        float startAngle = -MathF.PI * 0.5f;
        for (int i = 0; i < segments; i++)
        {
            float a1 = startAngle + i * step; float a2 = startAngle + (i + 1) * step;
            float t1 = i / (float)segments; float t2 = (i + 1) / (float)segments;

            Vector4 c1, c2;
            if (isSaturation) { c1 = HsvToRgb(hue, t1, 1f); c2 = HsvToRgb(hue, t2, 1f); }
            else { c1 = HsvToRgb(hue, 1f, t1); c2 = HsvToRgb(hue, 1f, t2); }

            var inner1 = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r1;
            var outer1 = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r2;
            var inner2 = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * r1;
            var outer2 = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * r2;

            vertices.Add(new Vertex { Position = inner1, Color = c1 });
            vertices.Add(new Vertex { Position = outer1, Color = c1 });
            vertices.Add(new Vertex { Position = inner2, Color = c2 });

            vertices.Add(new Vertex { Position = outer1, Color = c1 });
            vertices.Add(new Vertex { Position = outer2, Color = c2 });
            vertices.Add(new Vertex { Position = inner2, Color = c2 });
        }
    }

    private void DrawRingBorder(List<Vertex> vertices, Vector2 center, float r1, float r2, Vector4 color, float thickness)
    {
        int segs = 128; float step = MathF.PI * 2f / segs;
        for (int i = 0; i < segs; i++)
        {
            float a1 = i * step; float a2 = (i + 1) * step;
            DrawLineSegment(vertices, center, r2 - thickness * 0.5f, r2 + thickness * 0.5f, a1, a2, color);
            DrawLineSegment(vertices, center, r1 - thickness * 0.5f, r1 + thickness * 0.5f, a1, a2, color);
        }
    }

    private static void DrawLineSegment(List<Vertex> v, Vector2 center, float r1, float r2, float a1, float a2, Vector4 color)
    {
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

    private void DrawRingIndicator(List<Vertex> vertices, Vector2 center, float radius, float normalizedValue)
    {
        float angle = normalizedValue * MathF.PI * 2f - MathF.PI * 0.5f;
        var pos = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        DrawCircle(vertices, pos, 7f, new Vector4(1, 1, 1, 0.95f), 20);
        DrawCircle(vertices, pos, 4f, new Vector4(0.05f, 0.05f, 0.08f, 0.9f), 16);
    }

    private void DrawCheckmark(List<Vertex> vertices, Vector2 center)
    {
        var color = new Vector4(0.1f, 0.1f, 0.1f, 0.8f);
        var p1 = center + new Vector2(-6, 0);
        var p2 = center + new Vector2(-2, 5);
        var p3 = center + new Vector2(7, -5);
        DrawLine(vertices, p1, p2, color, 3f);
        DrawLine(vertices, p2, p3, color, 3f);
    }

    private void DrawCenterButton(List<Vertex> vertices, Vector4 fill, Vector4 outline)
    {
        DrawCircle(vertices, Position, CenterRadius + 5, outline, 48);
        DrawCircle(vertices, Position, CenterRadius, fill, 48);
        var cross = new Vector4(0.92f, 0.92f, 0.95f, 0.9f);
        DrawLine(vertices, Position + new Vector2(-11, -11), Position + new Vector2(11, 11), cross, 5f);
        DrawLine(vertices, Position + new Vector2(11, -11), Position + new Vector2(-11, 11), cross, 5f);
    }

    private void OpenBackgroundPicker()
    {
        _isPickingBackground = true;
        _isPickingColor = false;
        _isAdjustingThickness = false;
        _isConfirmingClear = false;
        _tempBackgroundColor = Settings.BackgroundColor.Value;
        RgbToHsv(Settings.BackgroundColor.Value, out _pickerHue, out _pickerSaturation, out _pickerValue);
        _activePickerRing = -1;
        _selectedIndex = -1;
        _renderer.SetDirty();
    }

    private void ApplyBackgroundPicker()
    {
        Settings.Save();
        _isPickingBackground = false;
        Close();
    }

    private void DrawThicknessPreview(List<Vertex> vertices)
    {
        var zoom = _renderer.Camera.Zoom;
        float screenRadius = _previewThickness * zoom;
        DrawCircle(vertices, Position, screenRadius + 4f, new Vector4(0.15f, 0.15f, 0.18f, 0.9f), 56);
        DrawCircle(vertices, Position, Math.Max(screenRadius - 4f, 1f), new Vector4(0.97f, 0.97f, 0.98f, 1.0f), 56);
    }

    private void DrawIcon(List<Vertex> vertices, int sectorIndex, bool isSelected)
    {
        float angle = sectorIndex * MathF.PI * 2f / SectorCount + RenderAngleOffset;
        var iconCenter = Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * IconRadius;
        var iconColor = isSelected
            ? new Vector4(0.02f, 0.02f, 0.05f, 1f)
            : new Vector4(1f, 1f, 1f, 0.92f);

        float iconSize = 28f;
        var entry = sectorIndex switch
        {
            0 => _brushIcon,
            1 => _eraserIcon,
            2 => _thicknessIcon,
            3 => _clearIcon,
            4 => _selectIcon,
            5 => _colorpicker,
            _ => default
        };

        if (entry.Width > 0)
        {
            _renderer.TextAtlas.EmitImage(entry,
                iconCenter - new Vector2(iconSize * 0.5f, iconSize * 0.5f),
                new Vector2(iconSize, iconSize), iconColor);
        }
    }

    // ====================== ПРИМИТИВЫ ======================

    private static void DrawAnnularSector(List<Vertex> v, Vector2 center, float r1, float r2, float start, float end, Vector4 color, int segments)
    {
        float step = (end - start) / segments;
        for (int i = 0; i < segments; i++)
        {
            float a1 = start + i * step; float a2 = a1 + step;
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

    private static void DrawCircle(List<Vertex> v, Vector2 center, float r, Vector4 color, int segments)
    {
        float step = MathF.PI * 2f / segments;
        for (int i = 0; i < segments; i++)
        {
            float a1 = i * step; float a2 = (i + 1) * step;
            v.Add(new Vertex { Position = center, Color = color });
            v.Add(new Vertex { Position = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r, Color = color });
            v.Add(new Vertex { Position = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * r, Color = color });
        }
    }

    private static void DrawLine(List<Vertex> v, Vector2 p1, Vector2 p2, Vector4 color, float thickness)
    {
        var dir = Vector2.Normalize(p2 - p1);
        var perp = new Vector2(-dir.Y, dir.X) * (thickness * 0.5f);
        v.Add(new Vertex { Position = p1 + perp, Color = color });
        v.Add(new Vertex { Position = p1 - perp, Color = color });
        v.Add(new Vertex { Position = p2 + perp, Color = color });
        v.Add(new Vertex { Position = p2 + perp, Color = color });
        v.Add(new Vertex { Position = p1 - perp, Color = color });
        v.Add(new Vertex { Position = p2 - perp, Color = color });
    }

    // ====================== ЦВЕТОВЫЕ УТИЛИТЫ ======================

    private static Vector4 HsvToRgb(float h, float s, float v)
    {
        h = Math.Clamp(h, 0f, 1f); s = Math.Clamp(s, 0f, 1f); v = Math.Clamp(v, 0f, 1f);
        int hi = (int)(h * 6f) % 6;
        float f = h * 6f - (int)(h * 6f);
        float p = v * (1f - s); float q = v * (1f - f * s); float t = v * (1f - (1f - f) * s);

        return hi switch
        {
            0 => new Vector4(v, t, p, 1f),
            1 => new Vector4(q, v, p, 1f),
            2 => new Vector4(p, v, t, 1f),
            3 => new Vector4(p, q, v, 1f),
            4 => new Vector4(t, p, v, 1f),
            _ => new Vector4(v, p, q, 1f),
        };
    }

    private static void RgbToHsv(Vector4 rgb, out float h, out float s, out float v)
    {
        float max = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z));
        float min = Math.Min(rgb.X, Math.Min(rgb.Y, rgb.Z));
        float delta = max - min;

        v = max;
        s = max > 0f ? delta / max : 0f;

        if (delta < 0.00001f) { h = 0f; return; }

        if (Math.Abs(max - rgb.X) < 0.00001f) h = (rgb.Y - rgb.Z) / delta + (rgb.Y < rgb.Z ? 6f : 0f);
        else if (Math.Abs(max - rgb.Y) < 0.00001f) h = (rgb.Z - rgb.X) / delta + 2f;
        else h = (rgb.X - rgb.Y) / delta + 4f;

        h /= 6f;
    }
}