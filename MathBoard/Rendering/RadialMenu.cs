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
    private const float ThicknessScale = 5f;

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

    // Sectors come from the settings; the special ones (tools, thickness, clear, colors) are handled here,
    // any other action is run by the input manager.
    private static UiAction[] Items => Settings.RadialItems.Value.Length > 0 ? Settings.RadialItems.Value : Settings.DefaultRadialItems;

    /// <summary>Runs an action picked in a sector that the menu does not handle itself.</summary>
    public Action<UiAction>? Execute { get; set; }

    /// <summary>Whether an action is in effect (its tool is chosen), shown with a dot.</summary>
    public Func<UiAction, bool>? IsActive { get; set; }
    // The color sectors: the color buttons' colors (at most seven), then the background.
    private static int PaletteCount => Math.Min(Settings.Palette.Count, 7);
    private int CurrentSectorCount => _isColorMenuOpen ? PaletteCount + 1 : Items.Length;
    private static float OuterRadius => Settings.RadialMenuOuterRadius;
    private static float InnerRadius => Settings.RadialMenuInnerRadius;
    private static float CenterRadius => Settings.RadialMenuCenterRadius;
    private static float IconRadius => (OuterRadius + InnerRadius) * 0.5f;

    // Sector 0 is at the bottom, so any count is symmetric; the others follow clockwise.
    public const float FirstSectorAngle = MathF.PI * 0.5f;
    // The gaps between the sectors: rays of the same width all along.
    private const float SectorGap = 4f;

    /// <summary>The direction's angle clockwise from the middle of sector 0, in [0, 2π).</summary>
    public static float AngleFromFirst(Vector2 dir) =>
        ((MathF.Atan2(dir.Y, dir.X) - FirstSectorAngle) % MathF.Tau + MathF.Tau) % MathF.Tau;

    public static int SectorIndex(Vector2 dir, int count)
    {
        float step = MathF.Tau / count;
        return (int)((AngleFromFirst(dir) + step * 0.5f) / step) % count;
    }

    public static void DrawSectorGaps(UiBatch ui, Vector2 center, float inner, float outer, int count)
    {
        if (count < 2) return;
        for (int i = 0; i < count; i++)
        {
            float angle = FirstSectorAngle + (i + 0.5f) * MathF.Tau / count;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            ui.Line(center + dir * inner, center + dir * outer, Theme.PanelBg, SectorGap);
        }
    }
    private bool _isMouseDown;

    public RadialMenu(StrokeRenderer renderer)
    {
        _renderer = renderer;
        _previewThickness = Settings.DefaultBrushWidth;
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

    /// <summary>
    /// Whether a new press closes the menu and goes on to what is under it, so drawing starts right away: a press outside
    /// the ring, unless the thickness preview or a color picker is up (there the whole screen belongs to the menu).
    /// </summary>
    public bool IsOutside(Vector2 screenPos) => IsOpen && !_isAdjustingThickness && !_isPickingColor && !_isPickingBackground &&
                                                Vector2.Distance(screenPos, Position) > OuterRadius + 10f;

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
            // The farther from the center, the thicker: 5 px of distance per unit of width, so small widths are easy to hit.
            float radius = Vector2.Distance(screenPos, Position);
            _previewThickness = Math.Clamp(radius / ThicknessScale, Settings.MinBrushWidth, Settings.MaxBrushWidth);
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

        _selectedIndex = SectorIndex(dir, CurrentSectorCount);
        // An empty sector only takes its place in the ring.
        if (!_isColorMenuOpen && Items[_selectedIndex] == UiAction.Spacer) _selectedIndex = -1;
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

        switch (Items[_selectedIndex])
        {
            case UiAction.Brush:
                SelectTool(Tool.Brush);
                break;
            case UiAction.Eraser:
                SelectTool(Tool.Eraser);
                break;
            case UiAction.Thickness:
                _isAdjustingThickness = true;
                _previewThickness = _renderer.CurrentBrushWidth;
                _renderer.SetDirty();
                break;
            case UiAction.ClearBoard:
                _isConfirmingClear = true;
                _renderer.SetDirty();
                break;
            case UiAction.Select:
                SelectTool(_renderer.Tool == Tool.Select ? Tool.Brush : Tool.Select);
                break;
            case UiAction.Color:
                _isColorMenuOpen = true;
                _renderer.SetDirty();
                break;
            case var action:
                Close();
                Execute?.Invoke(action);
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

        ui.Circle(Position, OuterRadius + 3f, Theme.WithAlpha(Theme.Border, 0.9f));
        ui.Circle(Position, OuterRadius + 2f, Theme.PanelBg);

        for (int i = 0; i < sectorCount; i++)
        {
            bool isSelected = i == _selectedIndex;
            float centerAngle = i * sectorAngle + FirstSectorAngle;
            float start = centerAngle - sectorAngle * 0.5f;
            float end = centerAngle + sectorAngle * 0.5f;

            Vector4 fill;
            if (_isColorMenuOpen)
            {
                fill = i == PaletteCount ? Settings.Background : palette[i];
                if (isSelected) fill = Vector4.Lerp(fill, Vector4.One, 0.25f);
            }
            else
            {
                fill = Items[i] == UiAction.Spacer ? Theme.PanelBg : isSelected ? Theme.Accent : Theme.Surface;
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
                DrawSectorIcon(ui, _renderer, Items[i], iconCenter, isSelected || IsSectorActive(i) ? Theme.Text : Theme.TextSecondary);
                if (IsSectorActive(i) && !isSelected)
                    ui.Circle(Position + new Vector2(MathF.Cos(centerAngle), MathF.Sin(centerAngle)) * (OuterRadius - 10f),
                        3f, Theme.Accent);
            }
        }

        DrawSectorGaps(ui, Position, InnerRadius, OuterRadius, sectorCount);

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
                : ItemName(Items[_selectedIndex]);
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

    private bool IsSectorActive(int index) => Items[index] != UiAction.Color && IsActive?.Invoke(Items[index]) == true;

    /// <summary>A sector's name: the action's, except the colors sector, which opens the palette.</summary>
    public static string ItemName(UiAction action) => action == UiAction.Color
        ? Localization.Get("tooltip_colorpicker", "Color Picker")
        : Localization.Get("action_" + action, action.ToString());

    public static void DrawSectorIcon(UiBatch ui, StrokeRenderer renderer, UiAction action, Vector2 center, Vector4 color)
    {
        if (action == UiAction.Color) ui.Icon(Icons.Palette, center, 20f, color);
        else if (action != UiAction.Spacer) QuickBar.DrawActionIcon(ui, renderer, action, center, color);
    }

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
