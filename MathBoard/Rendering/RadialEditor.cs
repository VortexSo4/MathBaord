using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>
/// Edits the radial menu, opened from the settings: drag actions from the tray into the ring (onto the place where they
/// should be), drag a sector onto another to swap them or out of the ring to remove it, tap a tray action to add it at
/// the end. Three handles set the menu's sizes: the center button, the inner and the outer edge of the ring.
/// </summary>
public sealed class RadialEditor(StrokeRenderer renderer) : IPopup
{
    private const float HandleRadius = 9f;
    private const float MaxOuter = 260f;
    private const float Cell = 44f, Gap = 8f;
    private const float DragThreshold = 8f;
    private const float PanelPad = 12f;
    private static readonly Vector2 ButtonSize = new(130f, 36f);

    private List<UiAction> _items = [];
    // Center button, inner and outer edge, edited by the handles.
    private float[] _radii = new float[3];
    private int _handle = -1;
    private int _pressedSector = -1;
    private UiAction? _pressedTray;
    private bool _dragging;
    private Vector2 _pressPos, _mouse;

    public bool IsOpen { get; private set; }

    public void Open()
    {
        _items = Settings.RadialItems.Value.ToList();
        _radii = [Settings.RadialMenuCenterRadius, Settings.RadialMenuInnerRadius, Settings.RadialMenuOuterRadius];
        IsOpen = true;
        renderer.SetDirty();
    }

    /// <summary>Leaves without saving (Esc).</summary>
    public void Close()
    {
        IsOpen = false;
        _pressedSector = -1;
        _pressedTray = null;
        _dragging = false;
        _handle = -1;
        renderer.SetDirty();
    }

    private float Inner => _radii[1];
    private float Outer => _radii[2];
    // Each handle on its own diagonal, so they never sit on top of each other.
    private static readonly Vector2[] HandleDirections =
        [Vector2.Normalize(new Vector2(1f, -1f)), Vector2.Normalize(new Vector2(1f, 1f)), Vector2.Normalize(new Vector2(-1f, 1f))];
    private Vector2 HandlePos(int i) => Center + HandleDirections[i] * _radii[i];

    private int HandleAt(Vector2 pos)
    {
        for (int i = _radii.Length - 1; i >= 0; i--)
            if (Vector2.Distance(pos, HandlePos(i)) < HandleRadius + 8f) return i;
        return -1;
    }

    /// <summary>Sets a radius from the pointer, keeping center &lt; inner &lt; outer with room for the icons.</summary>
    private void DragHandle(Vector2 pos)
    {
        float r = Vector2.Dot(pos - Center, HandleDirections[_handle]);
        _radii[_handle] = _handle switch
        {
            0 => Math.Clamp(r, 20f, _radii[1] - 8f),
            1 => Math.Clamp(r, _radii[0] + 8f, _radii[2] - 40f),
            _ => Math.Clamp(r, _radii[1] + 40f, MaxOuter)
        };
    }

    private Vector2 Screen => renderer.ScreenSize;
    // Placed for the largest ring, so it stays put while the outer edge is dragged.
    private Vector2 Center => new(Screen.X * 0.5f, MathF.Max(MaxOuter * 0.6f + 70f, Screen.Y * 0.4f));

    // The panel under the ring: the hint and the buttons on top, the actions that are not in the menu below them.
    private float PanelTop => Center.Y + Outer + 40f;

    private (Vector2 Pos, Vector2 Size) PanelRect()
    {
        int count = TrayItems().Count;
        float cellsW = TrayColumns(count) * (Cell + Gap) - Gap;
        float w = MathF.Min(Screen.X - 32f, MathF.Max(cellsW + PanelPad * 2, 620f));
        int rows = count == 0 ? 0 : (count + TrayColumns(count) - 1) / TrayColumns(count);
        float h = PanelPad * 2 + ButtonSize.Y + (rows > 0 ? rows * (Cell + Gap) - Gap + PanelPad : 0);
        return (new Vector2((Screen.X - w) * 0.5f, PanelTop), new Vector2(w, h));
    }

    private Vector2 DonePos
    {
        get
        {
            var (pos, size) = PanelRect();
            return new Vector2(pos.X + size.X - PanelPad - ButtonSize.X, pos.Y + PanelPad);
        }
    }

    private Vector2 ResetPos => DonePos - new Vector2(ButtonSize.X + 8f, 0);

    // The empty sector can be added any number of times.
    private List<UiAction> TrayItems() => Enum.GetValues<UiAction>().Where(a => a == UiAction.Spacer || !_items.Contains(a)).ToList();

    private int TrayColumns(int count) => Math.Clamp((int)((Screen.X - 64f + Gap) / (Cell + Gap)), 1, Math.Max(count, 1));

    private Vector2 TrayOrigin(int count) =>
        new((Screen.X - (TrayColumns(count) * (Cell + Gap) - Gap)) * 0.5f, PanelTop + PanelPad * 2 + ButtonSize.Y);

    private Vector2 TrayCell(int index, int count)
    {
        int columns = TrayColumns(count);
        return TrayOrigin(count) + new Vector2(index % columns * (Cell + Gap), index / columns * (Cell + Gap));
    }

    private static float Step(int count) => MathF.Tau / Math.Max(count, 1);

    private int SectorAt(Vector2 pos)
    {
        float dist = Vector2.Distance(pos, Center);
        if (dist < Inner || dist > Outer || _items.Count == 0) return -1;
        return RadialMenu.SectorIndex(pos - Center, _items.Count);
    }

    private bool NearRing(Vector2 pos)
    {
        float dist = Vector2.Distance(pos, Center);
        return dist > Inner * 0.5f && dist < Outer * 1.3f;
    }

    /// <summary>
    /// Where a dropped action goes among <paramref name="count"/> other sectors: the sector of the new ring under the pointer,
    /// so it lands where it is dropped (the leftmost too) and the others keep their order around it.
    /// </summary>
    private int InsertIndex(Vector2 pos, int count) =>
        (int)MathF.Round(RadialMenu.AngleFromFirst(pos - Center) / Step(count + 1)) % (count + 1);

    private UiAction? TrayAt(Vector2 pos)
    {
        var tray = TrayItems();
        for (int i = 0; i < tray.Count; i++)
            if (UiBatch.Contains(TrayCell(i, tray.Count), new Vector2(Cell), pos)) return tray[i];
        return null;
    }

    public void OnMouseDown(Vector2 pos)
    {
        _mouse = _pressPos = pos;
        _dragging = false;
        if (UiBatch.Contains(DonePos, ButtonSize, pos))
        {
            Settings.RadialItems.Value = _items.ToArray();
            Settings.RadialMenuCenterRadius.Value = _radii[0];
            Settings.RadialMenuInnerRadius.Value = _radii[1];
            Settings.RadialMenuOuterRadius.Value = _radii[2];
            Settings.Save();
            Close();
            return;
        }

        if (UiBatch.Contains(ResetPos, ButtonSize, pos))
        {
            _items = Settings.DefaultRadialItems.ToList();
            _radii = [Settings.RadialMenuCenterRadius.Default, Settings.RadialMenuInnerRadius.Default, Settings.RadialMenuOuterRadius.Default];
            renderer.SetDirty();
            return;
        }

        _handle = HandleAt(pos);
        if (_handle >= 0) return;

        _pressedSector = SectorAt(pos);
        _pressedTray = _pressedSector < 0 ? TrayAt(pos) : null;
        renderer.SetDirty();
    }

    public void OnMouseMove(Vector2 pos)
    {
        _mouse = pos;
        if (_handle >= 0)
        {
            DragHandle(pos);
            renderer.SetDirty();
            return;
        }

        if ((_pressedSector >= 0 || _pressedTray != null) && Vector2.Distance(pos, _pressPos) > DragThreshold) _dragging = true;
        renderer.SetDirty();
    }

    public void OnMouseUp(Vector2 pos)
    {
        _mouse = pos;
        if (_handle >= 0)
        {
            _handle = -1;
            renderer.SetDirty();
            return;
        }

        if (_dragging && _pressedSector >= 0)
        {
            // A sector dropped away from the ring is removed, but the menu keeps at least one.
            if (NearRing(pos)) Swap(_items, _pressedSector, RadialMenu.SectorIndex(pos - Center, _items.Count));
            else if (_items.Count > 1) _items.RemoveAt(_pressedSector);
        }
        else if (_dragging && _pressedTray is { } dropped)
        {
            if (NearRing(pos)) _items.Insert(InsertIndex(pos, _items.Count), dropped);
        }
        else if (!_dragging && _pressedTray is { } added)
        {
            _items.Add(added);
        }

        _pressedSector = -1;
        _pressedTray = null;
        _dragging = false;
        renderer.SetDirty();
    }

    private UiAction? DraggedAction() => _pressedSector >= 0 ? _items[_pressedSector] : _pressedTray;

    private static void Swap(List<UiAction> items, int a, int b) => (items[a], items[b]) = (items[b], items[a]);

    public void Render(UiBatch ui, Vector2 screen)
    {
        if (!IsOpen) return;
        ui.Rect(Vector2.Zero, screen, new Vector4(0, 0, 0, 0.6f));

        // While something is dragged over the ring, the ring shows where it would land and how the others move aside.
        var ring = _items.ToList();
        int landing = -1;
        if (_dragging && NearRing(_mouse))
        {
            if (_pressedSector >= 0)
            {
                landing = RadialMenu.SectorIndex(_mouse - Center, ring.Count);
                Swap(ring, _pressedSector, landing);
            }
            else if (_pressedTray is { } landed)
            {
                landing = InsertIndex(_mouse, ring.Count);
                ring.Insert(landing, landed);
            }
        }
        else if (_dragging && _pressedSector >= 0 && ring.Count > 1)
        {
            ring.RemoveAt(_pressedSector);
        }

        float step = Step(ring.Count);
        ui.Circle(Center, Outer + 3f, Theme.Border);
        ui.Circle(Center, Outer + 2f, Theme.PanelBg);
        for (int i = 0; i < ring.Count; i++)
        {
            float angle = i * step + RadialMenu.FirstSectorAngle;
            ui.Arc(Center, Inner, Outer, angle - step * 0.5f, angle + step * 0.5f,
                i == landing ? Theme.Accent : ring[i] == UiAction.Spacer ? Theme.PanelBg : Theme.Surface);
            RadialMenu.DrawSectorIcon(ui, renderer, ring[i], Center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (Inner + Outer) * 0.5f,
                Theme.Text);
        }

        RadialMenu.DrawSectorGaps(ui, Center, Inner, Outer, ring.Count);

        // The center button as the radial menu draws it.
        ui.Circle(Center, _radii[0] + 3f, Theme.Border);
        ui.Circle(Center, _radii[0], Theme.Surface);
        ui.Icon(Icons.Close, Center, 20f, Theme.Text);

        string[] handleNames =
        [
            Localization.Get("settings_radialmenu_center_radius", "Center radius"),
            Localization.Get("settings_radialmenu_inner_radius", "Inner radius"),
            Localization.Get("settings_radialmenu_outer_radius", "Outer radius")
        ];
        int hoveredHandle = _handle >= 0 ? _handle : HandleAt(_mouse);
        for (int i = 0; i < _radii.Length; i++)
        {
            ui.Circle(HandlePos(i), HandleRadius + 2f, Theme.Text);
            ui.Circle(HandlePos(i), HandleRadius, i == hoveredHandle ? Theme.AccentHover : Theme.Accent);
        }

        var tray = TrayItems();
        var (panelPos, panelSize) = PanelRect();
        ui.Rect(panelPos, panelSize, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(panelPos, panelSize, Theme.Border, 1f, Theme.PopupRadius);
        ui.TextInRect(Localization.Get("radial_edit_hint", "Drag actions into the ring or out of it"),
            panelPos + new Vector2(PanelPad + 2f, PanelPad), new Vector2(ResetPos.X - panelPos.X - PanelPad * 2, ButtonSize.Y),
            Theme.TextSecondary, Theme.Body);
        ui.TextButton(Localization.Get("radial_reset", "Default"), ResetPos, ButtonSize, UiBatch.Contains(ResetPos, ButtonSize, _mouse),
            UiBatch.ButtonKind.Secondary);
        ui.TextButton(Localization.Get("quickbar_done", "Done"), DonePos, ButtonSize, UiBatch.Contains(DonePos, ButtonSize, _mouse),
            UiBatch.ButtonKind.Primary);

        for (int i = 0; i < tray.Count; i++)
        {
            if (_dragging && _pressedTray == tray[i]) continue;
            var cell = TrayCell(i, tray.Count);
            bool hover = UiBatch.Contains(cell, new Vector2(Cell), _mouse);
            ui.Rect(cell, new Vector2(Cell), hover ? Theme.SurfaceHover : Theme.Surface, 10f);
            RadialMenu.DrawSectorIcon(ui, renderer, tray[i], cell + new Vector2(Cell * 0.5f), Theme.Text);
        }

        if (_dragging && landing < 0 && DraggedAction() is { } dragged)
        {
            ui.Rect(_mouse - new Vector2(Cell * 0.5f), new Vector2(Cell), Theme.Surface, 10f);
            ui.Border(_mouse - new Vector2(Cell * 0.5f), new Vector2(Cell), Theme.Accent, 2f, 10f);
            RadialMenu.DrawSectorIcon(ui, renderer, dragged, _mouse, Theme.Text);
        }

        // The name of what is under the pointer, or of what is being dragged; a handle shows its size.
        if (hoveredHandle >= 0)
        {
            ui.Tooltip($"{handleNames[hoveredHandle]}: {_radii[hoveredHandle]:0}", _mouse, screen);
            return;
        }

        UiAction? named = _dragging ? DraggedAction() : SectorAt(_mouse) is >= 0 and var s ? _items[s] : TrayAt(_mouse);
        if (named is { } shown) ui.Tooltip(RadialMenu.ItemName(shown), _mouse, screen);
    }
}
