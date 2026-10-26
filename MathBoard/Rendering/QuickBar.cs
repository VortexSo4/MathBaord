using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>
/// Floating buttons for actions that otherwise need the keyboard. Long-press a button (or use the settings entry)
/// to rearrange them: drag to move, drop on the tray to hide, drag from the tray to show. Color buttons are
/// independent: the tray always offers one more.
/// </summary>
public class QuickBar
{
    private const float Size = 44f;
    private const float TrayButtonGap = 8f;
    private const float DragThreshold = 8f;
    private const double LongPressSeconds = 0.6;
    private const double DoubleTapSeconds = 0.4;
    private const float SnapDistance = 14f;
    // Distance between neighbouring button centers in an evenly spaced row or column.
    private const float Stride = Size + TrayButtonGap;

    // Offered for a new color button, the first one not on the board yet.
    private static readonly Vector4[] NewColors =
    [
        Theme.Rgb(0xD3, 0xD3, 0xD3), Theme.Rgb(0xFF, 0x83, 0x83), Theme.Rgb(0x3A, 0x99, 0x4C), Theme.Rgb(0x5B, 0x9B, 0xFF),
        Theme.Rgb(0xFF, 0xD5, 0x4F), Theme.Rgb(0xC0, 0x84, 0xFC), Theme.Rgb(0xFB, 0x92, 0x3C), Theme.Rgb(0x2D, 0xD4, 0xBF),
        Theme.Rgb(0xF4, 0x72, 0xB6), Vector4.One
    ];

    private readonly StrokeRenderer _renderer;
    private readonly Action<QuickButtonConfig> _execute;
    private readonly Func<QuickButtonConfig, bool> _isActive;
    private readonly Func<Vector2, bool> _isCovered;
    private readonly Action<QuickButtonConfig, Vector2> _editColor;
    private QuickButtonConfig? _lastTap;
    private DateTime _lastTapTime;

    private List<QuickButtonConfig> _working = [];
    private QuickButtonConfig? _newColorButton;
    private Vector2 _screen = new(1280, 720);
    private Vector2 _mouse = new(-1000);

    private QuickButtonConfig? _pressed;
    private bool _pressedFromTray;
    private Vector2 _pressPos;
    private DateTime _pressTime;
    private bool _dragging;
    private Vector2 _grabOffset;

    public bool IsEditing { get; private set; }
    public bool IsPressed => _pressed != null;

    /// <summary>Marks the library button, which leads to the settings where an update can be installed.</summary>
    public bool LibraryBadge { get; set; }

    /// <param name="isCovered">True where other UI (the library panel) lies on top of the buttons.</param>
    /// <param name="editColor">Opens the color editor for a color button; invoked on double tap.</param>
    public QuickBar(StrokeRenderer renderer, Action<QuickButtonConfig> execute, Func<QuickButtonConfig, bool> isActive,
        Func<Vector2, bool> isCovered, Action<QuickButtonConfig, Vector2> editColor)
    {
        _editColor = editColor;
        _renderer = renderer;
        _execute = execute;
        _isActive = isActive;
        _isCovered = isCovered;
    }

    // While editing every button is drawn on top, so nothing hides it.
    private bool IsHidden(QuickButtonConfig b) => !IsEditing && _isCovered(CenterOf(b));

    private IReadOnlyList<QuickButtonConfig> Buttons => IsEditing ? _working : Settings.QuickButtons.Value;

    private Vector2 CenterOf(QuickButtonConfig b)
    {
        var c = new Vector2(b.AnchorX * _screen.X + b.OffsetX, b.AnchorY * _screen.Y + b.OffsetY);
        return Vector2.Clamp(c, new Vector2(Size * 0.5f), Vector2.Max(_screen - new Vector2(Size * 0.5f), new Vector2(Size * 0.5f)));
    }

    private QuickButtonConfig? ButtonAt(Vector2 pos)
    {
        var buttons = Buttons;
        for (int i = buttons.Count - 1; i >= 0; i--)
        {
            if (IsHidden(buttons[i])) continue;
            var c = CenterOf(buttons[i]);
            if (MathF.Abs(pos.X - c.X) <= Size * 0.5f && MathF.Abs(pos.Y - c.Y) <= Size * 0.5f)
                return buttons[i];
        }

        return null;
    }

    public bool Contains(Vector2 pos) => IsEditing || ButtonAt(pos) != null;

    public void BeginEditing()
    {
        IsEditing = true;
        _working = Settings.QuickButtons.Value.ToList();
        _renderer.SetDirty();
    }

    public void FinishEditing()
    {
        if (!IsEditing) return;
        IsEditing = false;
        Settings.QuickButtons.Value = _working.ToArray();
        Settings.Save();
        CancelPress();
        _renderer.SetDirty();
    }

    // ------------------------------------------------------------- tray layout (edit mode)

    private List<QuickButtonConfig> TrayButtons()
    {
        var palette = _working.Where(b => b.Color != null).Select(b => b.Color!.Value).ToList();
        _newColorButton ??= new QuickButtonConfig(UiAction.Color, 0, 0, 0, 0,
            NewColors.FirstOrDefault(c => palette.All(p => Vector4.DistanceSquared(p, c) > 1e-4f), Vector4.One));
        return Enum.GetValues<UiAction>()
            .Where(a => a is not (UiAction.Color or UiAction.Spacer) && _working.All(b => b.Action != a))
            .Select(a => new QuickButtonConfig(a, 0, 0, 0, 0))
            .Append(_newColorButton)
            .ToList();
    }

    private (Vector2 Pos, Vector2 Size, Vector2 DonePos, Vector2 ResetPos, Vector2 ButtonSize, Vector2 FirstButton) TrayLayout(int hiddenCount)
    {
        // As in the radial menu editor: the hint and the buttons in the top row, the hidden buttons centered below.
        const float pad = 12f;
        var buttonSize = new Vector2(130f, 36f);
        float buttonsW = Math.Max(hiddenCount, 1) * (Size + TrayButtonGap) - TrayButtonGap;
        float w = Math.Min(_screen.X - 32f, Math.Max(buttonsW + pad * 2, 720f));
        float h = pad * 3 + buttonSize.Y + Size;
        var pos = new Vector2((_screen.X - w) * 0.5f, 16f);
        var donePos = new Vector2(pos.X + w - pad - buttonSize.X, pos.Y + pad);
        return (pos, new Vector2(w, h), donePos, donePos - new Vector2(buttonSize.X + 8f, 0), buttonSize,
            new Vector2(pos.X + (w - buttonsW + Size) * 0.5f, pos.Y + pad * 2 + buttonSize.Y + Size * 0.5f));
    }

    private QuickButtonConfig? TrayButtonAt(Vector2 pos)
    {
        var tray = TrayButtons();
        var layout = TrayLayout(tray.Count);
        for (int i = 0; i < tray.Count; i++)
        {
            var c = layout.FirstButton + new Vector2(i * (Size + TrayButtonGap), 0);
            if (MathF.Abs(pos.X - c.X) <= Size * 0.5f && MathF.Abs(pos.Y - c.Y) <= Size * 0.5f)
                return tray[i];
        }

        return null;
    }

    private bool IsOverTray(Vector2 pos)
    {
        var layout = TrayLayout(TrayButtons().Count);
        return UiBatch.Contains(layout.Pos, layout.Size, pos);
    }

    // ------------------------------------------------------------- input

    public void OnMouseDown(Vector2 pos)
    {
        _mouse = pos;
        _pressPos = pos;
        _pressTime = DateTime.Now;
        _dragging = false;

        if (IsEditing)
        {
            var layout = TrayLayout(TrayButtons().Count);
            if (UiBatch.Contains(layout.DonePos, layout.ButtonSize, pos))
            {
                FinishEditing();
                return;
            }

            if (UiBatch.Contains(layout.ResetPos, layout.ButtonSize, pos))
            {
                _working = Settings.QuickButtons.Default.ToList();
                _renderer.SetDirty();
                return;
            }

            _pressed = ButtonAt(pos);
            _pressedFromTray = false;
            if (_pressed == null)
            {
                _pressed = TrayButtonAt(pos);
                _pressedFromTray = _pressed != null;
            }

            if (_pressed != null) _grabOffset = (_pressedFromTray ? pos : CenterOf(_pressed)) - pos;
        }
        else
        {
            _pressed = ButtonAt(pos);
        }

        _renderer.SetDirty();
    }

    public void OnMouseMove(Vector2 pos)
    {
        _mouse = pos;
        if (_pressed != null && !_dragging && Vector2.Distance(pos, _pressPos) > DragThreshold)
        {
            if (IsEditing) _dragging = true;
            else _pressed = null;
        }

        _renderer.SetDirty();
    }

    public void OnMouseUp(Vector2 pos)
    {
        _mouse = pos;
        if (_pressed is not { } button) return;

        if (IsEditing)
        {
            if (_dragging) Drop(button, pos);
        }
        else if (ReferenceEquals(ButtonAt(pos), button))
        {
            _execute(button);
            bool doubleTap = ReferenceEquals(_lastTap, button) && (DateTime.Now - _lastTapTime).TotalSeconds < DoubleTapSeconds;
            _lastTap = doubleTap ? null : button;
            _lastTapTime = DateTime.Now;
            if (doubleTap && button.Action == UiAction.Color) _editColor(button, CenterOf(button));
        }

        CancelPress();
        _renderer.SetDirty();
    }

    public void UpdateHover(Vector2 pos)
    {
        if (Vector2.DistanceSquared(pos, _mouse) < 0.01f) return;
        bool wasOver = ButtonAt(_mouse) != null;
        _mouse = pos;
        // While editing, names are shown for the tray buttons too, so every move may change the label.
        if (IsEditing || wasOver || ButtonAt(pos) != null) _renderer.SetDirty();
    }

    /// <summary>Returns true while a long press is being timed.</summary>
    public bool Update()
    {
        if (IsEditing || _pressed is not { } button) return false;
        if ((DateTime.Now - _pressTime).TotalSeconds < LongPressSeconds) return true;

        // Keep the press so the user can drag the button right away without lifting the pen.
        BeginEditing();
        _pressedFromTray = false;
        _grabOffset = CenterOf(button) - _pressPos;
        return false;
    }

    private void CancelPress()
    {
        _pressed = null;
        _dragging = false;
    }

    /// <summary>
    /// Where a dragged button lands: snapped to the row or column of nearby buttons, and to even spacing
    /// inside that row or column. The ghost preview and the drop both use this, so they always agree.
    /// </summary>
    private (Vector2 Center, bool InRow, bool InColumn) SnapTarget(QuickButtonConfig button, Vector2 raw)
    {
        var half = new Vector2(Size * 0.5f);
        var c = Vector2.Clamp(raw, half, Vector2.Max(_screen - half, half));
        var others = _working.Where(b => !ReferenceEquals(b, button) && !IsOverTray(CenterOf(b))).Select(CenterOf).ToList();

        static float? Nearest(IEnumerable<float> values, float target)
        {
            float? best = null;
            foreach (float v in values)
                if (MathF.Abs(v - target) < SnapDistance && (best == null || MathF.Abs(v - target) < MathF.Abs(best.Value - target)))
                    best = v;
            return best;
        }

        bool inRow = false, inColumn = false;
        if (Nearest(others.Select(o => o.Y), c.Y) is { } rowY)
        {
            c.Y = rowY;
            inRow = true;
            var row = others.Where(o => MathF.Abs(o.Y - rowY) < 0.5f).ToList();
            if (Nearest(row.SelectMany(o => new[] { o.X - Stride, o.X + Stride }), c.X) is { } spaced) c.X = spaced;
            else if (Nearest(others.Select(o => o.X), c.X) is { } columnX)
            {
                c.X = columnX;
                inColumn = true;
            }
        }
        else if (Nearest(others.Select(o => o.X), c.X) is { } columnX)
        {
            c.X = columnX;
            inColumn = true;
            var column = others.Where(o => MathF.Abs(o.X - columnX) < 0.5f).ToList();
            if (Nearest(column.SelectMany(o => new[] { o.Y - Stride, o.Y + Stride }), c.Y) is { } spaced) c.Y = spaced;
        }

        return (new Vector2(MathF.Round(c.X), MathF.Round(c.Y)), inRow, inColumn);
    }

    private void Drop(QuickButtonConfig button, Vector2 pos)
    {
        int index = _working.FindIndex(b => ReferenceEquals(b, button));
        if (IsOverTray(pos))
        {
            if (index >= 0) _working.RemoveAt(index);
            return;
        }

        var center = SnapTarget(button, pos + _grabOffset).Center;
        // Anchor to the nearest screen third so the button keeps its place relative to that edge on resize.
        float ax = center.X < _screen.X / 3f ? 0f : center.X > _screen.X * 2f / 3f ? 1f : 0.5f;
        float ay = center.Y < _screen.Y / 3f ? 0f : center.Y > _screen.Y * 2f / 3f ? 1f : 0.5f;
        var placed = button with { AnchorX = ax, AnchorY = ay, OffsetX = center.X - ax * _screen.X, OffsetY = center.Y - ay * _screen.Y };

        if (index >= 0) _working[index] = placed;
        else _working.Add(placed);
        if (ReferenceEquals(button, _newColorButton)) _newColorButton = null;
    }

    // ------------------------------------------------------------- rendering

    public void Render(UiBatch ui, Vector2 screen)
    {
        _screen = screen;
        if (IsEditing) return;

        foreach (var b in Buttons)
            if (!IsHidden(b)) DrawButton(ui, b, CenterOf(b));
    }

    public void RenderOverlay(UiBatch ui, Vector2 screen)
    {
        if (IsEditing)
        {
            ui.Rect(Vector2.Zero, screen, new Vector4(0, 0, 0, 0.35f));
            foreach (var b in Buttons)
                if (!(_dragging && ReferenceEquals(_pressed, b))) DrawButton(ui, b, CenterOf(b));
            RenderTray(ui);
            if (_dragging && _pressed is { } dragged)
            {
                if (!IsOverTray(_mouse)) RenderLandingGhost(ui, dragged);
                DrawButton(ui, dragged, _mouse + _grabOffset, lifted: true);
            }

            // Names help with the buttons that are not on the board yet; with a finger they show while pressed.
            if ((_dragging ? _pressed : ButtonAt(_mouse) ?? TrayButtonAt(_mouse)) is { } named)
                ui.Tooltip(Localization.Get("action_" + named.Action, named.Action.ToString()), _mouse, screen);
            return;
        }

        if (_pressed == null && ButtonAt(_mouse) is { } hovered)
            ui.Tooltip(Localization.Get("action_" + hovered.Action, hovered.Action.ToString()), _mouse, screen);
    }

    private void RenderLandingGhost(UiBatch ui, QuickButtonConfig button)
    {
        var (center, inRow, inColumn) = SnapTarget(button, _mouse + _grabOffset);
        var guide = Theme.WithAlpha(Theme.Accent, 0.7f);
        var others = _working.Where(b => !ReferenceEquals(b, button)).Select(CenterOf).ToList();
        if (inRow)
        {
            var row = others.Where(o => MathF.Abs(o.Y - center.Y) < 0.5f).Select(o => o.X).Append(center.X).ToList();
            ui.Line(new Vector2(row.Min(), center.Y), new Vector2(row.Max(), center.Y), guide, 1.5f);
        }

        if (inColumn)
        {
            var column = others.Where(o => MathF.Abs(o.X - center.X) < 0.5f).Select(o => o.Y).Append(center.Y).ToList();
            ui.Line(new Vector2(center.X, column.Min()), new Vector2(center.X, column.Max()), guide, 1.5f);
        }

        var pos = center - new Vector2(Size * 0.5f);
        ui.Rect(pos, new Vector2(Size), Theme.AccentSoft, 10f);
        ui.Border(pos, new Vector2(Size), Theme.Accent, 1.5f, 10f);
    }

    private void RenderTray(UiBatch ui)
    {
        var tray = TrayButtons();
        var layout = TrayLayout(tray.Count);
        bool dropping = _dragging && IsOverTray(_mouse) && !_pressedFromTray;

        ui.Rect(layout.Pos, layout.Size, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(layout.Pos, layout.Size, dropping ? Theme.Accent : Theme.Border, dropping ? 2f : 1f, Theme.PopupRadius);
        ui.TextInRect(Localization.Get("quickbar_edit_hint", "Drag buttons to move them, drop here to hide"),
            layout.Pos + new Vector2(14f, 12f), new Vector2(layout.ResetPos.X - layout.Pos.X - 24f, layout.ButtonSize.Y),
            Theme.TextSecondary, Theme.Body);

        for (int i = 0; i < tray.Count; i++)
        {
            if (_dragging && _pressedFromTray && _pressed!.Action == tray[i].Action) continue;
            DrawButton(ui, tray[i], layout.FirstButton + new Vector2(i * (Size + TrayButtonGap), 0));
        }

        ui.TextButton(Localization.Get("radial_reset", "Default"), layout.ResetPos, layout.ButtonSize,
            UiBatch.Contains(layout.ResetPos, layout.ButtonSize, _mouse) && !_dragging, UiBatch.ButtonKind.Secondary);
        ui.TextButton(Localization.Get("quickbar_done", "Done"), layout.DonePos, layout.ButtonSize,
            UiBatch.Contains(layout.DonePos, layout.ButtonSize, _mouse) && !_dragging, UiBatch.ButtonKind.Primary);
    }

    private void DrawButton(UiBatch ui, QuickButtonConfig button, Vector2 center, bool lifted = false)
    {
        var pos = center - new Vector2(Size * 0.5f);
        var size = new Vector2(Size);
        bool hovered = !IsEditing && UiBatch.Contains(pos, size, _mouse);
        bool pressed = !IsEditing && ReferenceEquals(_pressed, button) && hovered;
        bool active = _isActive(button);
        var action = button.Action;

        var bg = pressed ? Theme.Accent
            : active && button.Color == null ? Theme.WithAlpha(Theme.Accent, 0.9f)
            : hovered ? Theme.SurfaceHover : Theme.WithAlpha(Theme.Surface, 0.94f);
        ui.Rect(pos, size, bg, 10f);
        ui.Border(pos, size, lifted || IsEditing ? Theme.Accent : Theme.Border, lifted ? 2f : 1f, 10f);

        if (button.Color is { } color)
        {
            ui.Circle(center, 12f, color);
            if (active) ui.Ring(center, 17f, 2f, Theme.Text);
        }
        else
        {
            DrawActionIcon(ui, _renderer, action, center, pressed || active ? Theme.OnAccent : Theme.Text);
        }

        if (action == UiAction.ToggleLibrary && LibraryBadge) ui.Circle(center + new Vector2(11f, -11f), 4f, Theme.Accent);
    }

    /// <summary>The picture of an action: the current shape or fill style, the background color, or an icon.</summary>
    public static void DrawActionIcon(UiBatch ui, StrokeRenderer renderer, UiAction action, Vector2 center, Vector4 color)
    {
        switch (action)
        {
            case UiAction.Shapes:
                // A hexagon reads as "shapes" better than the shape the tool happens to draw.
                for (int i = 0; i < 6; i++)
                    ui.Line(center + Corner(i), center + Corner(i + 1), color, 1.8f);
                break;
            case UiAction.Pan:
                // Drawn rather than taken from the icon font, which on some Windows versions has no glyph for it.
                foreach (var d in new[] { Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY })
                {
                    var tip = center + d * 9f;
                    var side = new Vector2(-d.Y, d.X) * 3.2f;
                    ui.Line(center, tip, color, 1.6f);
                    ui.Line(tip, tip - d * 3.2f + side, color, 1.6f);
                    ui.Line(tip, tip - d * 3.2f - side, color, 1.6f);
                }

                break;
            case UiAction.Fill:
                FillPicker.DrawPreview(ui, renderer.FillStyle, center, 18f, color);
                break;
            case UiAction.ZoomLevel:
                ui.TextInRect($"{MathF.Round(renderer.Camera.Zoom * 100f):0}%", center - new Vector2(22f, 10f), new Vector2(44f, 20f),
                    color, Theme.Small, UiBatch.Align.Center);
                break;
            case UiAction.Minimap:
                ui.Border(center - new Vector2(10f, 8f), new Vector2(20f, 16f), color, 1.6f, 3f);
                ui.Rect(center + new Vector2(-1f, -4f), new Vector2(8f, 6f), color, 1.5f);
                break;
            case UiAction.FormatPainter:
                // An eyedropper, drawn: the icon fonts of older Windows versions have none.
                ui.Line(center + new Vector2(-7f, 7f), center + new Vector2(3f, -3f), color, 3.4f);
                ui.Line(center + new Vector2(-7f, 7f), center + new Vector2(-9f, 9f), color, 1.6f);
                ui.Line(center + new Vector2(1f, -7f), center + new Vector2(7f, -1f), color, 2.2f);
                ui.Circle(center + new Vector2(6f, -6f), 3.6f, color);
                break;
            case UiAction.BackgroundColor:
                ui.Rect(center - new Vector2(10f), new Vector2(20f), Settings.Background, 5f);
                ui.Border(center - new Vector2(10f), new Vector2(20f), color, 1.5f, 5f);
                break;
            default:
                ui.Icon(IconFor(action), center, 18f, color);
                break;
        }
    }

    private static Vector2 Corner(int i) => new Vector2(MathF.Cos(i * MathF.PI / 3f), MathF.Sin(i * MathF.PI / 3f)) * 9f;

    private static string IconFor(UiAction action) => action switch
    {
        UiAction.ToggleLibrary => Icons.Menu,
        UiAction.Undo => Icons.Undo,
        UiAction.Redo => Icons.Redo,
        UiAction.Save => Icons.Save,
        UiAction.NewFile => Icons.Add,
        UiAction.OpenAutoSave => Icons.History,
        UiAction.Settings => Icons.Settings,
        UiAction.Brush => Icons.Pen,
        UiAction.Eraser => Icons.Eraser,
        UiAction.Select => Icons.Select,
        UiAction.TurnSolids => Icons.Rotate,
        UiAction.ZoomIn => Icons.ZoomIn,
        UiAction.ZoomOut => Icons.ZoomOut,
        UiAction.FitView => Icons.FitView,
        UiAction.Keyboard => Icons.Keyboard,
        UiAction.Grid => Icons.Grid,
        UiAction.Screenshot => Icons.Screenshot,
        UiAction.InsertImage => Icons.Picture,
        UiAction.Thickness => Icons.Thickness,
        UiAction.ClearBoard => Icons.Delete,
        UiAction.Export => Icons.Export,
        _ => Icons.Palette
    };
}
