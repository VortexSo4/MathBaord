using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>
/// Working with a selected solid in space: turning it by dragging over it, cutting it by a plane through three points
/// tapped on it, and drawing on it - two taps make a segment (also through the inside, such as a cube's diagonal), a
/// drag along a face draws on that face. A section's three points can be dragged over the solid afterwards. Points stick to vertices, the middles of edges and faces, edges and faces.
/// Everything is kept in the solid's own coordinates, so it turns with the solid.
/// </summary>
public sealed partial class StrokeRenderer
{
    public enum SolidMode { None, Turn, Section, Lines }

    private const float SolidSnapPx = 16f;
    private const float TurnPerPixel = 0.01f;

    private SolidMode _solidMode;
    // Points tapped so far: up to three for a section, one for a segment.
    private readonly List<Vector3> _solidPicks = new();
    // A line being drawn along a face, and where the gesture started.
    private List<Vector3>? _solidStroke;
    private Vector2 _solidPress;
    private bool _solidMoved;
    private Solid3D? _turnStart;
    // Which of the section's points is being dragged, or -1.
    private int _sectionHandle = -1;

    // Where the eraser button was pressed on a solid with both a section and lines: a menu there asks what to erase.
    private Vector2? _clearMenu;

    private BoardObject? SelectedSolid => SingleObject is { } o && ObjectGeometry.IsSolid(o.Kind) ? o : null;

    private void ResetSolidMode()
    {
        _solidMode = SolidMode.None;
        _solidPicks.Clear();
        _solidStroke = null;
    }

    /// <summary>Starts or ends a way of working with the selected solid; one in the older projection becomes a body in space first.</summary>
    private void ToggleSolidMode(SolidMode mode)
    {
        if (_solidMode == mode)
        {
            ResetSolidMode();
            // The turning tool goes back to turning.
            if (Tool == Tool.Turn) _solidMode = SolidMode.Turn;
            _uiDirty = true;
            return;
        }

        ResetSolidMode();
        if (SelectedSolid is { Solid: null } old) ReplaceObject(old, ObjectGeometry.AsSolid3D(old));
        _solidMode = mode;
        _uiDirty = true;
    }

    private void ChangeSolid(Func<Solid3D, Solid3D> change)
    {
        if (SelectedSolid is { Solid: { } solid } o) ReplaceObject(o, o with { Solid = change(solid) });
    }

    /// <summary>Whether a press at the screen point works on the selected solid in the current mode.</summary>
    /// <summary>The point of the selected solid's section under the screen point, while sections are being made.</summary>
    private int SectionHandleAt(Vector2 screenPos)
    {
        if (_solidMode != SolidMode.Section || _solidPicks.Count > 0 || SelectedSolid is not { Solid.Section: { } s } o) return -1;
        var view = ObjectGeometry.ViewOf(o);
        Vector3[] points = [s.A, s.B, s.C];
        for (int i = 0; i < 3; i++)
            if (Vector2.Distance(WorldToScreen(view.Project(points[i])), screenPos) <= 14f) return i;
        return -1;
    }

    /// <summary>Cuts the solid by its section, leaving the part its middle is in, or makes it whole again.</summary>
    private void ToggleCut() => ChangeSolid(s => s.Section is not { } section || ObjectGeometry.PlaneOf(section) is not { } plane
        ? s
        : s with { Section = section with { Cut = section.Cut != 0 ? 0 : plane.Offset >= 0 ? 1 : -1 } });

    /// <summary>Erases the section or the lines of the solid; when it has both, asks which with a menu at the press.</summary>
    private void EraseOnSolid(Vector2 at)
    {
        if (SelectedSolid?.Solid is not { } solid) return;
        bool section = solid.Section != null, lines = solid.Marks.Length > 0;
        if (section && lines)
        {
            _clearMenu = at;
            _uiDirty = true;
        }
        else ChangeSolid(s => s with { Section = section ? null : s.Section, Marks = lines ? [] : s.Marks });
    }

    private static (string Label, Func<Solid3D, Solid3D> Change)[] ClearChoices() =>
    [
        (Localization.Get("solid_erase_lines", "Lines only"), s => s with { Marks = [] }),
        (Localization.Get("solid_erase_section", "Section only"), s => s with { Section = null }),
        (Localization.Get("solid_erase_all", "Both"), s => s with { Section = null, Marks = [] })
    ];

    private List<(Vector2 Pos, Vector2 Size)> ClearMenuRects()
    {
        var size = new Vector2(ClearChoices().Max(c => _textAtlas.MeasureWidth(c.Label, Theme.Label)) + 40f, 34f);
        var at = _clearMenu ?? Vector2.Zero;
        var start = Vector2.Clamp(new Vector2(at.X - size.X * 0.5f, at.Y + 16f), new Vector2(10f),
            Vector2.Max(ScreenSize - new Vector2(size.X + 10f, 3 * (size.Y + 4f) + 10f), new Vector2(10f)));
        return Enumerable.Range(0, 3).Select(i => (start + new Vector2(0, i * (size.Y + 4f)), size)).ToList();
    }

    private void RenderClearMenu(UiBatch ui)
    {
        var rects = ClearMenuRects();
        var choices = ClearChoices();
        var panel = rects[0].Pos - new Vector2(6f);
        var panelSize = rects[^1].Pos + rects[^1].Size - rects[0].Pos + new Vector2(12f);
        ui.Rect(panel, panelSize, Theme.WithAlpha(Theme.Surface, 0.97f), 10f);
        ui.Border(panel, panelSize, Theme.Border, 1f, 10f);
        for (int i = 0; i < rects.Count; i++)
        {
            bool hover = HoverPosition is { } p && UiBatch.Contains(rects[i].Pos, rects[i].Size, p);
            ui.TextButton(choices[i].Label, rects[i].Pos, rects[i].Size, hover, i == 2 ? UiBatch.ButtonKind.Danger : UiBatch.ButtonKind.Ghost);
        }
    }

    private bool OnSolid(Vector2 screenPos)
    {
        if (_solidMode == SolidMode.None || SelectedSolid is not { Solid: not null } o) return false;
        var (min, max) = ObjectGeometry.Bounds(o);
        var margin = new Vector2(24f);
        return UiBatch.Contains(WorldToScreen(min) - margin, (max - min) * _camera.Zoom + margin * 2f, screenPos);
    }

    /// <summary>
    /// For the turning tool: selects the topmost solid under the press, so the press goes on to turn it (one in the older
    /// projection becomes a body in space first). False if there is none.
    /// </summary>
    public bool SelectSolidToTurn(Vector2 screenPos)
    {
        var world = ScreenToWorld(screenPos);
        var o = _document.Objects.LastOrDefault(x => ObjectGeometry.IsSolid(x.Kind) &&
            (x.Solid != null ? ObjectGeometry.OnBody(x, world) : ObjectGeometry.Distance(x, world) <= Settings.SelectionRadius / _camera.Zoom));
        if (o == null) return false;
        ClearSelection();
        if (o.Solid == null)
        {
            var body = ObjectGeometry.AsSolid3D(o);
            ReplaceObject(o, body);
            o = body;
        }

        SelectedObjects.Add(o);
        _solidMode = SolidMode.Turn;
        _uiDirty = true;
        return true;
    }

    private void BeginSolidGesture(Vector2 screenPos)
    {
        _solidPress = screenPos;
        _solidMoved = false;
        _solidStroke = null;
        _sectionHandle = SectionHandleAt(screenPos);
        if (_solidMode != SolidMode.Turn && _sectionHandle < 0)
        {
            CurrentSelectionState = SelectionState.SolidGesture;
            return;
        }

        BeginTurn(screenPos);
    }

    /// <summary>Starts turning the selected solid (or moving its section's point) as the live object.</summary>
    private void BeginTurn(Vector2 screenPos)
    {
        var o = SelectedSolid!;
        _solidPress = screenPos;
        int index = _document.Objects.FindIndex(x => ReferenceEquals(x, o));
        if (index < 0) return;
        // Turned as the live object, so only it is drawn again while dragging.
        _document.SaveState();
        _document.Objects.RemoveAt(index);
        BeginLive(o, index);
        _turnStart = o.Solid;
        CurrentSelectionState = SelectionState.ChangingSolid;
        MarkStrokesDirty();
    }

    private void MoveSolidGesture(Vector2 screenPos)
    {
        var d = screenPos - _solidPress;
        if (CurrentSelectionState == SelectionState.ChangingSolid && _sectionHandle >= 0 && _liveObject != null && _turnStart?.Section is { } section)
        {
            // Over the whole solid: a point may go onto the part that is cut away.
            var whole = _liveObject with { Solid = _turnStart with { Section = section with { Cut = 0 } } };
            if (ObjectGeometry.PickOnSolid(whole, ScreenToWorld(screenPos), SolidSnapPx / _camera.Zoom, Settings.SolidSnapMiddles.Value) is not { } to) return;
            var moved = _sectionHandle switch { 0 => section with { A = to }, 1 => section with { B = to }, _ => section with { C = to } };
            // Kept where it was while the three points would fall on one line.
            if (ObjectGeometry.SectionPolygon(_liveObject, moved) == null) return;
            UpdateLive(_liveObject with { Solid = _turnStart with { Section = moved } });
            SelectedObjects[0] = _liveObject!;
            return;
        }

        if (CurrentSelectionState == SelectionState.ChangingSolid && _liveObject != null && _turnStart != null)
        {
            // A turntable: across turns it about its vertical axis, up and down tilts it, never over the top.
            var turned = _turnStart with
            {
                Yaw = _turnStart.Yaw + d.X * TurnPerPixel,
                Pitch = Math.Clamp(_turnStart.Pitch + d.Y * TurnPerPixel, -MathF.PI / 2f, MathF.PI / 2f)
            };
            UpdateLive(_liveObject with { Solid = turned });
            SelectedObjects[0] = _liveObject!;
            return;
        }

        if (!_solidMoved && d.Length() < 8f) return;
        _solidMoved = true;
        if (_solidMode != SolidMode.Lines || SelectedSolid is not { Solid: not null } o) return;
        var view = ObjectGeometry.ViewOf(o);
        if (ObjectGeometry.FaceAt(o, view, ObjectGeometry.BodyOf(o), ScreenToWorld(screenPos)) is not { } p) return;
        _solidStroke ??= [];
        if (_solidStroke.Count == 0 || Vector2.Distance(view.Project(p), view.Project(_solidStroke[^1])) * _camera.Zoom >= 3f) _solidStroke.Add(p);
        _uiDirty = true;
    }

    private void EndSolidGesture(Vector2 screenPos)
    {
        if (CurrentSelectionState == SelectionState.ChangingSolid) return;
        if (SelectedSolid is not { Solid: not null } o) return;
        if (_solidMoved)
        {
            if (_solidStroke is { Count: >= 2 } stroke)
                ChangeSolid(s => s with { Marks = [..s.Marks, new SolidMark([..stroke], CurrentColor)] });
            _solidStroke = null;
            return;
        }

        if (ObjectGeometry.PickOnSolid(o, ScreenToWorld(screenPos), SolidSnapPx / _camera.Zoom, Settings.SolidSnapMiddles.Value) is not { } p) return;
        _solidPicks.Add(p);
        if (_solidMode == SolidMode.Lines && _solidPicks.Count == 2)
        {
            var (a, b) = (_solidPicks[0], _solidPicks[1]);
            _solidPicks.Clear();
            if (Vector3.Distance(a, b) > 1e-3f) ChangeSolid(s => s with { Marks = [..s.Marks, new SolidMark([a, b], CurrentColor)] });
        }
        else if (_solidMode == SolidMode.Section && _solidPicks.Count == 3)
        {
            var section = new SolidSection(_solidPicks[0], _solidPicks[1], _solidPicks[2], CurrentColor);
            _solidPicks.Clear();
            // Three points on one line give no plane; then they are simply tapped again.
            if (ObjectGeometry.SectionPolygon(o, section) != null) ChangeSolid(s => s with { Section = section });
        }

        _uiDirty = true;
    }

    /// <summary>The points tapped so far, where the next one would stick, and a hint above the solid.</summary>
    private void RenderSolidMode(UiBatch ui)
    {
        if (_solidMode == SolidMode.None || SelectedSolid is not { Solid: not null } o) return;
        var view = ObjectGeometry.ViewOf(o);
        var picks = _solidPicks.Select(p => WorldToScreen(view.Project(p))).ToList();
        if (_solidMode == SolidMode.Section && picks.Count == 0 && o.Solid.Section is { } section)
        {
            foreach (var p in new[] { section.A, section.B, section.C })
            {
                var at = WorldToScreen(view.Project(p));
                ui.Circle(at, 7f, Theme.Surface);
                ui.Ring(at, 7f, 2f, Theme.Accent);
            }
        }

        if (_solidMode != SolidMode.Turn && CurrentSelectionState == SelectionState.None && HoverPosition is { } hover && OnSolid(hover) &&
            SectionHandleAt(hover) < 0 &&
            ObjectGeometry.PickOnSolid(o, ScreenToWorld(hover), SolidSnapPx / _camera.Zoom, Settings.SolidSnapMiddles.Value) is { } next)
        {
            var at = WorldToScreen(view.Project(next));
            if (_solidMode == SolidMode.Lines && picks.Count == 1) ui.Line(picks[0], at, Theme.WithAlpha(CurrentColor, 0.7f), 2f);
            ui.Ring(at, 7f, 2f, Theme.Accent);
        }

        if (_solidStroke is { Count: >= 2 } stroke)
            for (int i = 0; i + 1 < stroke.Count; i++)
                ui.Line(WorldToScreen(view.Project(stroke[i])), WorldToScreen(view.Project(stroke[i + 1])), CurrentColor, 2f);

        foreach (var p in picks)
        {
            ui.Circle(p, 6f, Theme.Accent);
            ui.Ring(p, 6f, 2f, Theme.Text);
        }

        string hint = _solidMode switch
        {
            SolidMode.Turn => Localization.Get("solid_hint_turn", "Drag over the solid to turn it"),
            SolidMode.Section => string.Format(Localization.Get("solid_hint_section", "Tap three points of the section, or drag one of its points: {0}/3"), picks.Count),
            _ => Localization.Get("solid_hint_lines", "Two taps make a segment, a drag along a face draws on it")
        };
        var (minS, _) = HandleFrame();
        ui.TooltipBox(hint, new Vector2(minS.X, minS.Y - 72f));
    }
}
