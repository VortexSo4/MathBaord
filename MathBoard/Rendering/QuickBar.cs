using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>
/// Floating buttons for actions that otherwise need the keyboard. Long-press a button (or use the settings entry)
/// to rearrange them: drag to move, drop on the tray to hide, drag from the tray to show.
/// </summary>
public class QuickBar
{
    private const float Size = 44f;
    private const float TrayButtonGap = 8f;
    private const float DragThreshold = 8f;
    private const double LongPressSeconds = 0.6;

    private readonly StrokeRenderer _renderer;
    private readonly Action<UiAction> _execute;
    private readonly Func<UiAction, bool> _isActive;
    private readonly Func<Vector2, bool> _isCovered;

    private List<QuickButtonConfig> _working = [];
    private Vector2 _screen = new(1280, 720);
    private Vector2 _mouse = new(-1000);

    private UiAction? _pressed;
    private bool _pressedFromTray;
    private Vector2 _pressPos;
    private DateTime _pressTime;
    private bool _dragging;
    private Vector2 _grabOffset;

    public bool IsEditing { get; private set; }
    public bool IsPressed => _pressed != null;

    /// <param name="isCovered">True where other UI (the library panel) lies on top of the buttons.</param>
    public QuickBar(StrokeRenderer renderer, Action<UiAction> execute, Func<UiAction, bool> isActive, Func<Vector2, bool> isCovered)
    {
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

    private UiAction? ButtonAt(Vector2 pos)
    {
        var buttons = Buttons;
        for (int i = buttons.Count - 1; i >= 0; i--)
        {
            if (IsHidden(buttons[i])) continue;
            var c = CenterOf(buttons[i]);
            if (MathF.Abs(pos.X - c.X) <= Size * 0.5f && MathF.Abs(pos.Y - c.Y) <= Size * 0.5f)
                return buttons[i].Action;
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

    private List<UiAction> HiddenActions() =>
        Enum.GetValues<UiAction>().Where(a => _working.All(b => b.Action != a)).ToList();

    private (Vector2 Pos, Vector2 Size, Vector2 DonePos, Vector2 DoneSize, Vector2 FirstButton) TrayLayout(int hiddenCount)
    {
        const float pad = 14f;
        const float doneW = 110f;
        float buttonsW = Math.Max(hiddenCount, 1) * (Size + TrayButtonGap) - TrayButtonGap;
        float w = Math.Min(_screen.X - 32f, Math.Max(buttonsW, 260f) + doneW + pad * 3);
        float h = Size + pad * 2 + 24f;
        var pos = new Vector2((_screen.X - w) * 0.5f, 16f);
        var donePos = new Vector2(pos.X + w - pad - doneW, pos.Y + 24f + pad + (Size - 36f) * 0.5f);
        return (pos, new Vector2(w, h), donePos, new Vector2(doneW, 36f), new Vector2(pos.X + pad + Size * 0.5f, pos.Y + 24f + pad + Size * 0.5f));
    }

    private UiAction? TrayButtonAt(Vector2 pos)
    {
        var hidden = HiddenActions();
        var layout = TrayLayout(hidden.Count);
        for (int i = 0; i < hidden.Count; i++)
        {
            var c = layout.FirstButton + new Vector2(i * (Size + TrayButtonGap), 0);
            if (MathF.Abs(pos.X - c.X) <= Size * 0.5f && MathF.Abs(pos.Y - c.Y) <= Size * 0.5f)
                return hidden[i];
        }

        return null;
    }

    private bool IsOverTray(Vector2 pos)
    {
        var layout = TrayLayout(HiddenActions().Count);
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
            var layout = TrayLayout(HiddenActions().Count);
            if (UiBatch.Contains(layout.DonePos, layout.DoneSize, pos))
            {
                FinishEditing();
                return;
            }

            _pressed = ButtonAt(pos);
            _pressedFromTray = false;
            if (_pressed == null)
            {
                _pressed = TrayButtonAt(pos);
                _pressedFromTray = _pressed != null;
            }

            if (_pressed is { } action)
            {
                var center = _pressedFromTray
                    ? pos
                    : CenterOf(_working.First(b => b.Action == action));
                _grabOffset = center - pos;
            }
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
        if (_pressed is not { } action) return;

        if (IsEditing)
        {
            if (_dragging) Drop(action, pos);
        }
        else if (ButtonAt(pos) == action)
        {
            _execute(action);
        }

        CancelPress();
        _renderer.SetDirty();
    }

    public void UpdateHover(Vector2 pos)
    {
        if (Vector2.DistanceSquared(pos, _mouse) < 0.01f) return;
        bool wasOver = ButtonAt(_mouse) != null;
        _mouse = pos;
        if (wasOver || ButtonAt(pos) != null) _renderer.SetDirty();
    }

    /// <summary>Returns true while a long press is being timed.</summary>
    public bool Update()
    {
        if (IsEditing || _pressed is not { } action) return false;
        if ((DateTime.Now - _pressTime).TotalSeconds < LongPressSeconds) return true;

        // Keep the press so the user can drag the button right away without lifting the pen.
        BeginEditing();
        _pressedFromTray = false;
        _grabOffset = CenterOf(_working.First(b => b.Action == action)) - _pressPos;
        return false;
    }

    private void CancelPress()
    {
        _pressed = null;
        _dragging = false;
    }

    private void Drop(UiAction action, Vector2 pos)
    {
        int index = _working.FindIndex(b => b.Action == action);
        if (IsOverTray(pos))
        {
            if (index >= 0) _working.RemoveAt(index);
            return;
        }

        var center = Vector2.Clamp(pos + _grabOffset, new Vector2(Size * 0.5f), _screen - new Vector2(Size * 0.5f));
        // Anchor to the nearest screen third so the button keeps its place relative to that edge on resize.
        float ax = center.X < _screen.X / 3f ? 0f : center.X > _screen.X * 2f / 3f ? 1f : 0.5f;
        float ay = center.Y < _screen.Y / 3f ? 0f : center.Y > _screen.Y * 2f / 3f ? 1f : 0.5f;
        center = new Vector2(MathF.Round(center.X / 4f) * 4f, MathF.Round(center.Y / 4f) * 4f);
        var config = new QuickButtonConfig(action, ax, ay, center.X - ax * _screen.X, center.Y - ay * _screen.Y);

        if (index >= 0) _working[index] = config;
        else _working.Add(config);
    }

    // ------------------------------------------------------------- rendering

    public void Render(UiBatch ui, Vector2 screen)
    {
        _screen = screen;
        if (IsEditing) return;

        foreach (var b in Buttons)
            if (!IsHidden(b)) DrawButton(ui, b.Action, CenterOf(b));
    }

    public void RenderOverlay(UiBatch ui, Vector2 screen)
    {
        if (IsEditing)
        {
            ui.Rect(Vector2.Zero, screen, new Vector4(0, 0, 0, 0.35f));
            foreach (var b in Buttons)
                if (!(_dragging && _pressed == b.Action)) DrawButton(ui, b.Action, CenterOf(b));
            RenderTray(ui);
            if (_dragging && _pressed is { } dragged)
                DrawButton(ui, dragged, _mouse + _grabOffset, lifted: true);
            return;
        }

        if (_pressed == null && ButtonAt(_mouse) is { } hovered)
            ui.Tooltip(Localization.Get("action_" + hovered, hovered.ToString()), _mouse, screen);
    }

    private void RenderTray(UiBatch ui)
    {
        var hidden = HiddenActions();
        var layout = TrayLayout(hidden.Count);
        bool dropping = _dragging && IsOverTray(_mouse) && !_pressedFromTray;

        ui.Rect(layout.Pos, layout.Size, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(layout.Pos, layout.Size, dropping ? Theme.Accent : Theme.Border, dropping ? 2f : 1f, Theme.PopupRadius);
        ui.TextInRect(Localization.Get("quickbar_edit_hint", "Drag buttons to move them, drop here to hide"),
            layout.Pos + new Vector2(14f, 8f), new Vector2(layout.Size.X - 28f, 22f), Theme.TextSecondary, Theme.Small);

        for (int i = 0; i < hidden.Count; i++)
        {
            if (_dragging && _pressedFromTray && _pressed == hidden[i]) continue;
            DrawButton(ui, hidden[i], layout.FirstButton + new Vector2(i * (Size + TrayButtonGap), 0));
        }

        bool doneHover = UiBatch.Contains(layout.DonePos, layout.DoneSize, _mouse) && !_dragging;
        ui.TextButton(Localization.Get("quickbar_done", "Done"), layout.DonePos, layout.DoneSize, doneHover,
            UiBatch.ButtonKind.Primary);
    }

    private void DrawButton(UiBatch ui, UiAction action, Vector2 center, bool lifted = false)
    {
        var pos = center - new Vector2(Size * 0.5f);
        var size = new Vector2(Size);
        bool hovered = !IsEditing && UiBatch.Contains(pos, size, _mouse);
        bool pressed = !IsEditing && _pressed == action && hovered;
        bool active = _isActive(action);
        bool isColor = action is UiAction.Color1 or UiAction.Color2 or UiAction.Color3;

        var bg = pressed ? Theme.Accent
            : active && !isColor ? Theme.WithAlpha(Theme.Accent, 0.9f)
            : hovered ? Theme.SurfaceHover : Theme.WithAlpha(Theme.Surface, 0.94f);
        ui.Rect(pos, size, bg, 10f);
        ui.Border(pos, size, lifted || IsEditing ? Theme.Accent : Theme.Border, lifted ? 2f : 1f, 10f);

        if (isColor)
        {
            var color = Settings.Colors[action - UiAction.Color1];
            ui.Circle(center, 12f, color);
            if (active) ui.Ring(center, 17f, 2f, Theme.Text);
        }
        else
        {
            ui.Icon(IconFor(action), center, 18f, Theme.Text);
        }
    }

    public static string IconFor(UiAction action) => action switch
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
        UiAction.Pan => Icons.Pan,
        UiAction.ZoomIn => Icons.ZoomIn,
        UiAction.ZoomOut => Icons.ZoomOut,
        UiAction.FitView => Icons.FitView,
        _ => Icons.Palette
    };
}
