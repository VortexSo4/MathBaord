using System.Numerics;
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
    private float _previewThickness;
    private float _thicknessBaseWidth;

    private bool _isPickingBackground;
    private bool _isColorMenuOpen;

    private bool _isPickingColor;
    private int _colorEditIndex = -1;
    private float _pickerHue;
    private float _pickerSaturation = 1f;
    private float _pickerValue = 1f;
    private int _activePickerRing = -1;

    private Vector2 _mousePos;

    private const float PickerOuterRadius = 130f;
    private const float PickerRingWidth = 28f;
    private const float PickerCenterRadius = 18f;
    private const float PickerRingGap = 6f;

    private const int SectorCount = 6;
    // The color sectors: the color buttons' colors (at most seven), then the background.
    private static int PaletteCount => Math.Min(Settings.Palette.Count, 7);
    private int CurrentSectorCount => _isColorMenuOpen ? PaletteCount + 1 : SectorCount;
    private static float OuterRadius => Settings.RadialMenuOuterRadius;
    private static float InnerRadius => Settings.RadialMenuInnerRadius;
    private static float CenterRadius => Settings.RadialMenuCenterRadius;
    private static float IconRadius => (OuterRadius + InnerRadius) * 0.5f;

    private const float RenderAngleOffset = MathF.PI;
    private bool _isMouseDown;

    public RadialMenu(StrokeRenderer renderer)
    {
        _renderer = renderer;
        _previewThickness = Settings.DefaultBrushWidth;
        _thicknessBaseWidth = Settings.DefaultBrushWidth;
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
        _isPickingBackground = false;
        _renderer.SetDirty();
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

    private (float R1, float R2, float R3, float R4) PickerRings()
    {
        float r1 = PickerCenterRadius + 6f;
        float r2 = r1 + PickerRingWidth;
        float r3 = r2 + PickerRingGap + PickerRingWidth;
        float r4 = r3 + PickerRingGap + PickerRingWidth;
        return (r1, r2, r3, r4);
    }

    public void OnMouseDown(Vector2 screenPos)
    {
        if (!IsOpen) return;
        _isMouseDown = true;
        _pressStartTime = DateTime.Now;
        _mousePos = screenPos;

        if (_isPickingColor || _isPickingBackground)
        {
            float dist = (screenPos - Position).Length();
            var (r1, r2, r3, r4) = PickerRings();

            if (dist >= r1 && dist <= r2 + PickerRingGap * 0.5f) _activePickerRing = 0;
            else if (dist > r2 + PickerRingGap * 0.5f && dist <= r3 + PickerRingGap * 0.5f) _activePickerRing = 1;
            else if (dist > r3 + PickerRingGap * 0.5f && dist <= r4 + 8f) _activePickerRing = 2;
            else _activePickerRing = -1;

            if (_activePickerRing >= 0)
                HandlePickerMove(screenPos);
            return;
        }

        UpdateSelection(screenPos);
    }

    public void OnMouseMove(Vector2 screenPos)
    {
        if (!IsOpen) return;
        _mousePos = screenPos;

        if (_isAdjustingThickness)
        {
            float deltaY = Position.Y - screenPos.Y;
            _previewThickness = Math.Clamp(_thicknessBaseWidth + deltaY * 0.6f, Settings.MinBrushWidth, Settings.MaxBrushWidth);
            _renderer.SetDirty();
            return;
        }

        if (_isPickingColor || _isPickingBackground)
        {
            if (_isMouseDown && _activePickerRing >= 0)
                HandlePickerMove(screenPos);
            return;
        }

        UpdateSelection(screenPos);
    }

    private void UpdateSelection(Vector2 screenPos)
    {
        var dir = screenPos - Position;
        float dist = dir.Length();

        if (dist < CenterRadius || dist > OuterRadius * 1.35f)
        {
            _selectedIndex = -1;
            return;
        }

        float angle = MathF.Atan2(dir.Y, dir.X) + MathF.PI;
        float sectorAngle = MathF.PI * 2f / CurrentSectorCount;
        _selectedIndex = (int)((angle + sectorAngle * 0.5f) / sectorAngle) % CurrentSectorCount;
    }

    public void OnMouseUp(Vector2 screenPos)
    {
        if (!IsOpen) return;
        _isMouseDown = false;

        if (_isPickingColor)
        {
            if ((screenPos - Position).Length() <= PickerCenterRadius + 4f)
                ApplyPickerColor();
            _activePickerRing = -1;
            return;
        }

        if (_isPickingBackground)
        {
            if ((screenPos - Position).Length() <= PickerCenterRadius + 4f)
            {
                Settings.Save();
                Close();
            }

            _activePickerRing = -1;
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

        bool isCenterClick = (screenPos - Position).Length() <= CenterRadius + 10f;

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
            if (_selectedIndex < PaletteCount) HandleColorPress(_selectedIndex);
            else OpenBackgroundPicker();
            return;
        }

        switch (_selectedIndex)
        {
            case 0:
                SelectTool(Tool.Brush);
                break;
            case 1:
                SelectTool(Tool.Eraser);
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
            case 4:
                SelectTool(_renderer.Tool == Tool.Select ? Tool.Brush : Tool.Select);
                break;
            case 5:
                _isColorMenuOpen = true;
                _renderer.SetDirty();
                break;
        }
    }

    private void SelectTool(Tool tool)
    {
        _renderer.SetTool(tool);
        if (Settings.RadialMenuCloseOnToolSelect) Close();
        else _renderer.SetDirty();
    }

    private void HandleColorPress(int colorIdx)
    {
        if ((DateTime.Now - _pressStartTime).TotalSeconds > Settings.RadialMenuLongPressThreshold)
        {
            OpenColorPicker(colorIdx);
            return;
        }

        _renderer.RecolorSelection(Settings.Palette[colorIdx]);
        _renderer.SetColor(Settings.Palette[colorIdx]);
        if (_renderer.Tool is Tool.Eraser or Tool.Pan) _renderer.SetTool(Tool.Brush);
        if (Settings.RadialMenuCloseOnToolSelect) Close();
        else _isColorMenuOpen = false;
        _renderer.SetDirty();
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

        var newColor = ColorMath.HsvToRgb(_pickerHue, _pickerSaturation, _pickerValue);
        if (_isPickingBackground)
            Settings.Background = newColor;
        else
            _renderer.SetColor(newColor);
        _renderer.SetDirty();
    }

    private void OpenColorPicker(int colorIndex)
    {
        _isPickingColor = true;
        _colorEditIndex = colorIndex;
        (_pickerHue, _pickerSaturation, _pickerValue) = ColorMath.RgbToHsv(Settings.Palette[colorIndex]);
        _activePickerRing = -1;
        _selectedIndex = -1;
        _renderer.SetDirty();
    }

    private void ApplyPickerColor()
    {
        var newColor = ColorMath.HsvToRgb(_pickerHue, _pickerSaturation, _pickerValue);
        Settings.SetPaletteColor(_colorEditIndex, newColor);
        Settings.Save();
        _renderer.SetColor(newColor);
        Close();
    }

    private void OpenBackgroundPicker()
    {
        _isPickingBackground = true;
        _isPickingColor = false;
        _isAdjustingThickness = false;
        _isConfirmingClear = false;
        (_pickerHue, _pickerSaturation, _pickerValue) = ColorMath.RgbToHsv(Settings.Background);
        _activePickerRing = -1;
        _selectedIndex = -1;
        _renderer.SetDirty();
    }

    // ------------------------------------------------------------- rendering

    public void Render(UiBatch ui)
    {
        if (!IsOpen) return;

        if (_isPickingColor || _isPickingBackground)
        {
            RenderColorPicker(ui);
            return;
        }

        if (_isAdjustingThickness)
        {
            RenderThicknessPreview(ui);
            return;
        }

        int sectorCount = CurrentSectorCount;
        var palette = Settings.Palette;
        float sectorAngle = MathF.PI * 2f / sectorCount;
        const float gap = 0.03f;

        ui.Circle(Position, OuterRadius + 3f, Theme.WithAlpha(Theme.Border, 0.9f));
        ui.Circle(Position, OuterRadius + 2f, Theme.PanelBg);

        for (int i = 0; i < sectorCount; i++)
        {
            bool isSelected = i == _selectedIndex;
            float centerAngle = i * sectorAngle + RenderAngleOffset;
            float start = centerAngle - sectorAngle * 0.5f + gap;
            float end = centerAngle + sectorAngle * 0.5f - gap;

            Vector4 fill;
            if (_isColorMenuOpen)
            {
                fill = i == PaletteCount ? Settings.Background : palette[i];
                if (isSelected) fill = Vector4.Lerp(fill, Vector4.One, 0.25f);
            }
            else
            {
                fill = isSelected ? Theme.Accent : Theme.Surface;
            }

            ui.Arc(Position, InnerRadius, OuterRadius, start, end, fill);

            var iconCenter = Position + new Vector2(MathF.Cos(centerAngle), MathF.Sin(centerAngle)) * IconRadius;
            if (_isColorMenuOpen)
            {
                if (i == PaletteCount) ui.Icon(Icons.Palette, iconCenter, 20f, ColorMath.ContrastOn(fill));
                else if (Vector4.DistanceSquared(palette[i], _renderer.CurrentColor) < 1e-4f)
                    ui.Icon(Icons.CheckMark, iconCenter, 18f, ColorMath.ContrastOn(fill));
            }
            else
            {
                ui.Icon(SectorIcon(i), iconCenter, 22f, isSelected || IsSectorActive(i) ? Theme.Text : Theme.TextSecondary);
                if (IsSectorActive(i) && !isSelected)
                    ui.Circle(Position + new Vector2(MathF.Cos(centerAngle), MathF.Sin(centerAngle)) * (OuterRadius - 10f),
                        3f, Theme.Accent);
            }
        }

        var centerFill = _isConfirmingClear ? Theme.Danger : Theme.Surface;
        ui.Circle(Position, CenterRadius + 3f, Theme.Border);
        ui.Circle(Position, CenterRadius, centerFill);
        ui.Icon(_isConfirmingClear ? Icons.Delete : Icons.Close, Position, 20f, Theme.Text);

        string? tooltip = null;
        if (_isConfirmingClear)
            tooltip = Localization.Get("tooltip_clear_confirm", "Tap the center to clear everything");
        else if (_selectedIndex >= 0)
        {
            tooltip = _isColorMenuOpen
                ? _selectedIndex < PaletteCount
                    ? Localization.Get("action_Color", "Color") + " " + (_selectedIndex + 1)
                    : Localization.Get("tooltip_background_color", "Background Color")
                : _selectedIndex switch
                {
                    0 => Localization.Get("tooltip_brush", "Brush"),
                    1 => Localization.Get("tooltip_eraser", "Eraser"),
                    2 => Localization.Get("tooltip_thickness", "Thickness"),
                    3 => Localization.Get("tooltip_clear", "Clear All"),
                    4 => Localization.Get("tooltip_select", "Select"),
                    _ => Localization.Get("tooltip_colorpicker", "Color Picker")
                };
        }

        if (tooltip != null)
        {
            // Show the label just outside the ring so it never covers the sector icons.
            var dir = _mousePos - Position;
            dir = dir.LengthSquared() > 1f ? Vector2.Normalize(dir) : new Vector2(0, -1);
            var size = ui.TooltipSize(tooltip);
            float reach = OuterRadius + 10f + MathF.Abs(dir.X) * size.X * 0.5f + MathF.Abs(dir.Y) * size.Y * 0.5f;
            ui.TooltipBox(tooltip, Position + dir * reach - size * 0.5f);
        }
    }

    private bool IsSectorActive(int index) => index switch
    {
        0 => _renderer.Tool == Tool.Brush,
        1 => _renderer.Tool == Tool.Eraser,
        4 => _renderer.Tool == Tool.Select,
        _ => false
    };

    private static string SectorIcon(int index) => index switch
    {
        0 => Icons.Pen,
        1 => Icons.Eraser,
        2 => Icons.Thickness,
        3 => Icons.Delete,
        4 => Icons.Select,
        _ => Icons.Palette
    };

    private void RenderColorPicker(UiBatch ui)
    {
        var (r1, r2, r3, r4) = PickerRings();
        var preview = ColorMath.HsvToRgb(_pickerHue, _pickerSaturation, _pickerValue);

        ui.Circle(Position, PickerOuterRadius + 9f, Theme.Border);
        ui.Circle(Position, PickerOuterRadius + 8f, Theme.PanelBg);

        ui.ColorRing(Position, r1, r2, 0, _pickerHue);
        ui.ColorRing(Position, r2 + PickerRingGap, r3, 1, _pickerHue);
        ui.ColorRing(Position, r3 + PickerRingGap, r4, 2, _pickerHue);

        var rings = new[] { (r1, r2, _pickerHue), (r2 + PickerRingGap, r3, _pickerSaturation), (r3 + PickerRingGap, r4, _pickerValue) };
        for (int i = 0; i < rings.Length; i++)
        {
            var (inner, outer, value) = rings[i];
            if (_activePickerRing == i)
            {
                ui.Ring(Position, outer + 1.5f, 3f, new Vector4(1, 1, 1, 0.85f));
                ui.Ring(Position, inner + 1.5f, 3f, new Vector4(1, 1, 1, 0.85f));
            }

            float angle = value * MathF.PI * 2f - MathF.PI * 0.5f;
            var knob = Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * ((inner + outer) * 0.5f);
            ui.Circle(knob, 8f, Vector4.One);
            ui.Circle(knob, 5f, new Vector4(0.05f, 0.05f, 0.08f, 0.9f));
        }

        ui.Circle(Position, PickerCenterRadius + 3f, Theme.Border);
        ui.Circle(Position, PickerCenterRadius, preview);
        ui.Icon(Icons.CheckMark, Position, 16f, ColorMath.ContrastOn(preview));
    }

    private void RenderThicknessPreview(UiBatch ui)
    {
        float screenRadius = MathF.Max(_previewThickness * 0.5f * _renderer.Camera.Zoom, 1f);
        ui.Circle(Position, screenRadius + 4f, Theme.WithAlpha(Theme.PanelBg, 0.9f));
        ui.Circle(Position, screenRadius, _renderer.CurrentColor);

        string label = $"{_previewThickness:0}";
        float w = ui.Measure(label, Theme.Label) + 20f;
        var pos = Position + new Vector2(-w * 0.5f, screenRadius + 12f);
        ui.Rect(pos, new Vector2(w, 28f), Theme.PopupBg, 8f);
        ui.TextInRect(label, pos, new Vector2(w, 28f), Theme.Text, Theme.Label, UiBatch.Align.Center);
    }
}
