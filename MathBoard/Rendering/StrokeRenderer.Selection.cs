using System.Numerics;
using MathBoard.Core;
using MathBoard.Graphics;

namespace MathBoard.Rendering;

public sealed partial class StrokeRenderer
{
    public enum SelectionState
    {
        None,
        DrawingBox,
        Moving,
        Scaling,
        Rotating,
        DraggingPoint
    }

    private enum SelectionAction { Edit, Dash, Duplicate, Delete }

    private const float ToolbarButtonSize = 36f;
    private const float PointHandleRadius = 16f;
    // Scale and rotate handles sit this far outside the selection, so they do not cover the shape's own points.
    private const float HandleOffset = 18f;

    public bool IsSelectMode => Tool == Tool.Select;
    public List<Stroke> SelectedStrokes { get; } = new();
    public List<BoardObject> SelectedObjects { get; } = new();
    public SelectionState CurrentSelectionState { get; set; } = SelectionState.None;

    /// <summary>Raised when the user asks to edit a plot's functions.</summary>
    public event Action<BoardObject>? EditPlotRequested;

    /// <summary>Raised when a measure's label of the selected shape is tapped, with where the label is on screen.</summary>
    public event Action<Dimension, Vector2>? DimensionEditRequested;

    private bool HasSelection => SelectedStrokes.Count > 0 || SelectedObjects.Count > 0;
    private BoardObject? SingleObject => SelectedStrokes.Count == 0 && SelectedObjects.Count == 1 ? SelectedObjects[0] : null;

    private Vector2 _selectionStartScreen;
    private Vector2 _selectionEndScreen;
    private int _activeHandle = -1;
    private Dictionary<Stroke, List<Vector2>> _originalPoints = new();
    private List<(int Index, BoardObject Original)> _originalObjects = new();
    private (Vector2 Min, Vector2 Max) _originalBounds;
    private Vector2 _startMouseWorld;
    // The shape whose point is dragged, as it was.
    private BoardObject? _dragged;

    // While the selection is dragged (not scaled or rotated) it stays in the document as it was and is drawn from
    // primitives built once, which the backend moves (FrameData.Moving); it is put in place when the drag ends. Pictures
    // are not primitives, and fills lie below every shape while the moved layer is drawn on top, so both are moved in the
    // document.
    private readonly HashSet<object> _moving = new(ReferenceEqualityComparer.Instance);
    private Prim[] _movingPrims = [];
    private readonly List<ObjectLabel> _movingLabels = new();
    private Matrix3x2 _movingTransform = Matrix3x2.Identity;
    private int _movingVersion;

    public void ClearSelection()
    {
        FinishEdit();
        SelectedStrokes.Clear();
        SelectedObjects.Clear();
        CurrentSelectionState = SelectionState.None;
        _uiDirty = true;
    }

    public void DeleteSelection()
    {
        if (!HasSelection) return;
        _document.SaveState();
        var strokes = SelectedStrokes.ToHashSet();
        _document.Strokes.RemoveAll(strokes.Contains);
        RemoveObjects([..SelectedObjects]);
        ClearSelection();
        MarkStrokesDirty();
    }

    /// <summary>Copies the selection slightly offset and selects the copy.</summary>
    public void DuplicateSelection()
    {
        if (!HasSelection) return;
        _document.SaveState();
        var offset = Matrix3x2.CreateTranslation(new Vector2(24f) / _camera.Zoom);

        var strokes = SelectedStrokes.Select(s => new Stroke
        {
            Width = s.Width,
            Color = s.Color,
            Points = s.Points.Select(p => Vector2.Transform(p, offset)).ToList()
        }).ToList();
        var objects = SelectedObjects.Select(o => o.Transformed(offset) with { Id = BoardObject.NewId() }).ToList();
        // The fills tied only to the copied shapes are copied with them.
        var selected = SelectedObjects.ToHashSet<object>(ReferenceEqualityComparer.Instance);
        var fills = TiesTouching(SelectedObjects).Where(t => AllIn(t, selected)).Select(t => t.Fill.Transformed(offset) with { Id = BoardObject.NewId() }).ToList();
        _document.Strokes.AddRange(strokes);
        _document.Objects.AddRange(objects);
        // The copies' fills go with the other fills, below the shapes.
        _document.Objects.InsertRange(_document.Objects.TakeWhile(o => o.Kind == ShapeKind.Fill).Count(), fills);

        SelectedStrokes.Clear();
        SelectedStrokes.AddRange(strokes);
        SelectedObjects.Clear();
        SelectedObjects.AddRange(objects);
        MarkStrokesDirty();
    }

    /// <summary>Gives the selected ink and shapes another color as one undo step; false if nothing is selected.</summary>
    public bool RecolorSelection(Vector4 color)
    {
        if (!HasSelection || CurrentSelectionState != SelectionState.None) return false;
        _document.SaveState();
        foreach (var stroke in SelectedStrokes) stroke.Color = color;
        for (int i = 0; i < SelectedObjects.Count; i++)
        {
            var o = SelectedObjects[i];
            if (o.Kind == ShapeKind.Image) continue;
            int index = _document.Objects.FindIndex(x => ReferenceEquals(x, o));
            SelectedObjects[i] = o with { Color = color };
            if (index >= 0) _document.Objects[index] = SelectedObjects[i];
        }

        MarkStrokesDirty();
        return true;
    }

    /// <summary>Makes the selected lines and outlines dashed, or solid again when all of them already are, as one undo step.</summary>
    public void ToggleDashedSelection()
    {
        var dashable = SelectedObjects.Where(o => ObjectGeometry.CanDash(o.Kind)).ToList();
        if (dashable.Count == 0) return;
        string text = dashable.All(o => o.Dashed) ? "" : BoardObject.DashedStyle;
        _document.SaveState();
        for (int i = 0; i < SelectedObjects.Count; i++)
        {
            var o = SelectedObjects[i];
            if (!ObjectGeometry.CanDash(o.Kind)) continue;
            int index = _document.Objects.FindIndex(x => ReferenceEquals(x, o));
            SelectedObjects[i] = o with { Text = text };
            if (index >= 0) _document.Objects[index] = SelectedObjects[i];
        }

        MarkStrokesDirty();
    }

    /// <summary>Sets a measure of the selected shape (world units or degrees) as one undo step; its fills follow.</summary>
    public void ApplyDimension(Dimension dimension, float value)
    {
        if (SingleObject is not { } o || CurrentSelectionState != SelectionState.None) return;
        int index = _document.Objects.FindIndex(x => ReferenceEquals(x, o));
        if (index < 0) return;
        var edited = ObjectGeometry.WithDimension(o, dimension, value);
        _document.SaveState();
        NoteTies([o], rigid: false);
        _edited[o] = edited;
        _document.Objects[index] = edited;
        SelectedObjects[0] = edited;
        if (_fillTies.Count > 0) Refill(_fillTies, Edited, final: true);
        _fillTies = [];
        _edited.Clear();
        _document.IsDirty = true;
        MarkStrokesDirty();
    }

    /// <summary>
    /// The labels of the selected shape's measures on screen: sides and segments, radii, angles. A label that would
    /// cover one already placed (on a small shape) is left out.
    /// </summary>
    private List<(Dimension Dimension, string Text, Vector2 Pos, Vector2 Size)> DimensionLabels()
    {
        var labels = new List<(Dimension, string, Vector2, Vector2)>();
        if (SingleObject is not { } o || CurrentSelectionState == SelectionState.DrawingBox) return labels;
        foreach (var d in ObjectGeometry.Dimensions(o))
        {
            string text = DimensionEditor.Format(d);
            var size = new Vector2(_ui.Measure(text, Theme.Small) + 12f, 22f);
            var pos = WorldToScreen(Vector2.Transform(d.At, _movingTransform)) + d.Away * d.Offset - size * 0.5f;
            if (labels.Any(l => pos.X < l.Item3.X + l.Item4.X && l.Item3.X < pos.X + size.X && pos.Y < l.Item3.Y + l.Item4.Y && l.Item3.Y < pos.Y + size.Y))
                continue;
            labels.Add((d, text, pos, size));
        }

        return labels;
    }

    private (Dimension Dimension, Vector2 Pos, Vector2 Size)? DimensionAt(Vector2 screenPos)
    {
        foreach (var (d, _, pos, size) in DimensionLabels())
            if (UiBatch.Contains(pos - new Vector2(4f), size + new Vector2(8f), screenPos)) return (d, pos, size);
        return null;
    }

    /// <summary>Replaces an object (for example after editing a plot's functions) as one undo step.</summary>
    public void ReplaceObject(BoardObject original, BoardObject replacement)
    {
        int index = _document.Objects.FindIndex(o => ReferenceEquals(o, original));
        if (index < 0) return;
        _document.SaveState();
        _document.Objects[index] = replacement;
        int selected = SelectedObjects.FindIndex(o => ReferenceEquals(o, original));
        if (selected >= 0) SelectedObjects[selected] = replacement;
        MarkStrokesDirty();
    }

    /// <summary>The measures of a single selected shape, as labels to tap; radii and semi-axes are drawn as lines.</summary>
    private void RenderDimensions(UiBatch ui)
    {
        if (SingleObject is not { } o) return;
        foreach (var d in ObjectGeometry.Dimensions(o))
            if (d.HasGuide)
                ui.Line(WorldToScreen(Vector2.Transform(d.GuideFrom, _movingTransform)), WorldToScreen(Vector2.Transform(d.GuideTo, _movingTransform)),
                    Theme.WithAlpha(Theme.Accent, 0.7f), 1.5f);

        foreach (var (_, text, pos, size) in DimensionLabels())
        {
            bool hover = HoverPosition is { } p && UiBatch.Contains(pos, size, p);
            ui.Rect(pos, size, hover ? Theme.SurfaceHover : Theme.WithAlpha(Theme.Surface, 0.92f), 11f);
            ui.Border(pos, size, Theme.Accent, 1f, 11f);
            ui.TextInRect(text, pos, size, Theme.Text, Theme.Small, UiBatch.Align.Center);
        }
    }

    private List<SelectionAction> ToolbarActions()
    {
        var actions = new List<SelectionAction>();
        if (SingleObject?.Kind == ShapeKind.Plot) actions.Add(SelectionAction.Edit);
        if (SelectedObjects.Any(o => ObjectGeometry.CanDash(o.Kind))) actions.Add(SelectionAction.Dash);
        actions.Add(SelectionAction.Duplicate);
        actions.Add(SelectionAction.Delete);
        return actions;
    }

    /// <summary>Buttons in a row below the selection's bottom-right handle (the rotate handle is above).</summary>
    private Vector2 ToolbarButtonPos(int index, int count)
    {
        var (_, maxS) = HandleFrame();
        float width = count * ToolbarButtonSize + (count - 1) * 6f;
        var start = new Vector2(maxS.X - width + ToolbarButtonSize * 0.5f, maxS.Y + 14f);
        start = Vector2.Clamp(start, new Vector2(4f), Vector2.Max(ScreenSize - new Vector2(width + 4f, ToolbarButtonSize + 4f), new Vector2(4f)));
        return start + new Vector2(index * (ToolbarButtonSize + 6f), 0);
    }

    private int HitTestControlPoint(Vector2 screenPos)
    {
        if (SingleObject is not { } o || ObjectGeometry.IsBackdrop(o.Kind)) return -1;
        var handles = ObjectGeometry.ControlHandles(o);
        for (int i = 0; i < handles.Length; i++)
            if (Vector2.Distance(WorldToScreen(handles[i]), screenPos) < PointHandleRadius)
                return i;
        return -1;
    }

    public void HandleSelectionDown(Vector2 screenPos)
    {
        if (HasSelection)
        {
            var actions = ToolbarActions();
            for (int i = 0; i < actions.Count; i++)
            {
                if (!UiBatch.Contains(ToolbarButtonPos(i, actions.Count), new Vector2(ToolbarButtonSize), screenPos)) continue;
                switch (actions[i])
                {
                    case SelectionAction.Edit: EditPlotRequested?.Invoke(SingleObject!); break;
                    case SelectionAction.Dash: ToggleDashedSelection(); break;
                    case SelectionAction.Duplicate: DuplicateSelection(); break;
                    default: DeleteSelection(); break;
                }

                return;
            }

            int point = HitTestControlPoint(screenPos);
            if (point >= 0)
            {
                // The object is edited as the live object, so only it is redrawn while dragging.
                var o = SingleObject!;
                int index = _document.Objects.FindIndex(x => ReferenceEquals(x, o));
                if (index >= 0)
                {
                    _document.SaveState();
                    NoteTies([o], rigid: false);
                    _dragged = o;
                    _document.Objects.RemoveAt(index);
                    BeginLive(o, index);
                    _activeHandle = point;
                    CurrentSelectionState = SelectionState.DraggingPoint;
                    MarkStrokesDirty();
                    return;
                }
            }

            if (DimensionAt(screenPos) is var (dimension, labelPos, labelSize))
            {
                DimensionEditRequested?.Invoke(dimension, labelPos + labelSize * 0.5f);
                return;
            }

            // The selection is dragged by its lines or by the fill of a selected shape. A press on the empty inside of an
            // outline, or on something else, starts a new selection: the circle inside a selected frame is picked.
            int handle = HitTestHandles(screenPos);
            if (handle != -1 || GrabsSelection(ScreenToWorld(screenPos)))
            {
                _document.SaveState();
                _originalBounds = GetSelectedBounds();
                CurrentSelectionState = handle == -1 ? SelectionState.Moving
                    : handle == 8 ? SelectionState.Rotating : SelectionState.Scaling;
                _activeHandle = handle;
                _originalPoints = SelectedStrokes.ToDictionary(s => s, s => s.Points);
                _originalObjects = SelectedObjects
                    .Select(o => (_document.Objects.FindIndex(x => ReferenceEquals(x, o)), o))
                    .Where(t => t.Item1 >= 0).ToList();
                NoteTies(SelectedObjects, rigid: true);
                _startMouseWorld = ScreenToWorld(screenPos);
                if (CurrentSelectionState == SelectionState.Moving) BeginMoving();
                return;
            }
        }

        ClearSelection();
        CurrentSelectionState = SelectionState.DrawingBox;
        _selectionStartScreen = screenPos;
        _selectionEndScreen = screenPos;
    }

    public void HandleSelectionMove(Vector2 screenPos)
    {
        switch (CurrentSelectionState)
        {
            case SelectionState.DrawingBox:
                _selectionEndScreen = screenPos;
                _uiDirty = true;
                return;
            case SelectionState.DraggingPoint when _liveObject != null:
                UpdateLive(ObjectGeometry.MoveControlPoint(_liveObject, _activeHandle, ScreenToWorld(screenPos)));
                SelectedObjects[0] = _liveObject!;
                _edited[_dragged!] = _liveObject!;
                if (_fillTies.Count > 0) _refillPending = true;
                return;
            case SelectionState.None or SelectionState.DraggingPoint:
                return;
        }

        var mouseWorld = ScreenToWorld(screenPos);
        Matrix3x2 m = Matrix3x2.Identity;

        if (CurrentSelectionState == SelectionState.Moving)
        {
            _movingTransform = Matrix3x2.CreateTranslation(mouseWorld - _startMouseWorld);
            foreach (var (index, original) in _originalObjects)
            {
                if (original.Kind != ShapeKind.Image) continue;
                _document.Objects[index] = original.Transformed(_movingTransform);
                MarkStrokesDirty();
            }

            if (_carriedTies.Count > 0)
            {
                CarryFills(_movingTransform);
                MarkStrokesDirty();
            }

            if (_fillTies.Count > 0) _refillPending = true;
            _uiDirty = true;
            return;
        }

        if (CurrentSelectionState == SelectionState.Scaling)
        {
            Vector2 minB = _originalBounds.Min;
            Vector2 maxB = _originalBounds.Max;
            float midX = (minB.X + maxB.X) / 2f;
            float midY = (minB.Y + maxB.Y) / 2f;

            Vector2 fixedCorner = _activeHandle switch
            {
                0 => maxB,
                1 => new Vector2(midX, maxB.Y),
                2 => new Vector2(minB.X, maxB.Y),
                3 => new Vector2(maxB.X, midY),
                4 => new Vector2(minB.X, midY),
                5 => new Vector2(maxB.X, minB.Y),
                6 => new Vector2(midX, minB.Y),
                _ => minB
            };

            Vector2 newMin = minB;
            Vector2 newMax = maxB;
            if (_activeHandle is 0 or 3 or 5) newMin.X = mouseWorld.X;
            if (_activeHandle is 2 or 4 or 7) newMax.X = mouseWorld.X;
            if (_activeHandle is 0 or 1 or 2) newMin.Y = mouseWorld.Y;
            if (_activeHandle is 5 or 6 or 7) newMax.Y = mouseWorld.Y;

            Vector2 origSize = maxB - minB;
            if (MathF.Abs(origSize.X) < 0.001f) origSize.X = 0.001f;
            if (MathF.Abs(origSize.Y) < 0.001f) origSize.Y = 0.001f;
            Vector2 scale = (newMax - newMin) / origSize;
            if (!float.IsFinite(scale.X)) scale.X = 1;
            if (!float.IsFinite(scale.Y)) scale.Y = 1;
            if (SingleObject?.Kind == ShapeKind.Image && _activeHandle is 0 or 2 or 5 or 7)
            {
                // Corners keep a picture's proportions.
                float uniform = MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Y));
                scale = new Vector2(MathF.CopySign(uniform, scale.X), MathF.CopySign(uniform, scale.Y));
            }

            m = Matrix3x2.CreateScale(scale, fixedCorner);
        }
        else if (CurrentSelectionState == SelectionState.Rotating)
        {
            Vector2 center = (_originalBounds.Min + _originalBounds.Max) * 0.5f;
            float startAngle = MathF.Atan2(_startMouseWorld.Y - center.Y, _startMouseWorld.X - center.X);
            float currentAngle = MathF.Atan2(mouseWorld.Y - center.Y, mouseWorld.X - center.X);
            m = Matrix3x2.CreateRotation(currentAngle - startAngle, center);
        }

        foreach (var stroke in SelectedStrokes)
            stroke.Points = _originalPoints[stroke].Select(p => Vector2.Transform(p, m)).ToList();

        SelectedObjects.Clear();
        foreach (var (index, original) in _originalObjects)
        {
            _document.Objects[index] = _edited[original] = original.Transformed(m);
            SelectedObjects.Add(_document.Objects[index]);
        }

        // Fills follow the shapes that bound them: those tied only to the selection turn with it, the others are made again.
        CarryFills(m);
        if (_fillTies.Count > 0) _refillPending = true;
        MarkStrokesDirty();
    }

    /// <summary>Moves the fills tied only to the selection with it, as it was moved since the edit started.</summary>
    private void CarryFills(Matrix3x2 m)
    {
        foreach (var tie in _carriedTies)
        {
            int at = _document.Objects.FindIndex(o => ReferenceEquals(o, tie.Fill));
            if (at >= 0) _document.Objects[at] = tie.Fill = tie.Original.Transformed(m);
        }
    }

    /// <summary>Starts dragging the selection: its shapes and ink are drawn from primitives built once.</summary>
    private void BeginMoving()
    {
        foreach (var s in SelectedStrokes) _moving.Add(s);
        foreach (var o in SelectedObjects)
            if (o.Kind != ShapeKind.Image) _moving.Add(o);

        // In document order, so they lie over each other as before.
        int count = 0;
        foreach (var o in _document.Objects)
            if (_moving.Contains(o)) count += Built(o).Prims.Length;
        foreach (var s in _document.Strokes)
            if (_moving.Contains(s)) count += SegmentCount(s);

        _movingPrims = new Prim[count];
        _movingLabels.Clear();
        int at = 0;
        foreach (var o in _document.Objects)
        {
            if (!_moving.Contains(o)) continue;
            var (prims, labels) = Built(o);
            prims.CopyTo(_movingPrims, at);
            at += prims.Length;
            _movingLabels.AddRange(labels);
        }

        foreach (var s in _document.Strokes)
            if (_moving.Contains(s)) at = WriteStroke(s, 0, _movingPrims, at);

        _movingTransform = Matrix3x2.Identity;
        _movingVersion++;
        MarkStrokesDirty();
    }

    /// <summary>Puts the dragged selection where it was dragged.</summary>
    private void PlaceMoved()
    {
        var m = _movingTransform;
        if (m.IsIdentity) return;
        foreach (var stroke in SelectedStrokes)
            stroke.Points = _originalPoints[stroke].Select(p => Vector2.Transform(p, m)).ToList();

        SelectedObjects.Clear();
        foreach (var (index, original) in _originalObjects)
        {
            _document.Objects[index] = original.Transformed(m);
            SelectedObjects.Add(_document.Objects[index]);
        }

        CarryFills(m);
    }

    private void EndMoving()
    {
        if (_movingPrims.Length == 0 && _moving.Count == 0 && _movingTransform.IsIdentity) return;
        _moving.Clear();
        _movingPrims = [];
        _movingLabels.Clear();
        _movingTransform = Matrix3x2.Identity;
        _movingVersion++;
        MarkStrokesDirty();
    }

    /// <summary>
    /// Ends a drag of the selection or of a shape's point: puts the result in place and makes the fills tied to it again
    /// (fills carried along a scaling too, as a circle stays a circle while its fill would stretch).
    /// </summary>
    private void FinishEdit()
    {
        var state = CurrentSelectionState;
        if (state is SelectionState.None or SelectionState.DrawingBox) return;
        if (state == SelectionState.DraggingPoint) CommitLiveObject();
        if (state == SelectionState.Moving) PlaceMoved();
        if (state == SelectionState.Scaling && _carriedTies.Count > 0) Refill(_carriedTies, Edited, final: true);
        if (_fillTies.Count > 0) Refill(_fillTies, Edited, final: true);
        _fillTies = [];
        _carriedTies = [];
        _edited.Clear();
        _dragged = null;
        EndMoving();
        CurrentSelectionState = SelectionState.None;
        MarkStrokesDirty();
    }

    public void HandleSelectionUp(Vector2 screenPos)
    {
        if (CurrentSelectionState == SelectionState.DrawingBox)
        {
            var minS = Vector2.Min(_selectionStartScreen, screenPos);
            var maxS = Vector2.Max(_selectionStartScreen, screenPos);
            var size = maxS - minS;
            if (MathF.Max(size.X, size.Y) < Settings.SelectionTapSize) SelectClosest(ScreenToWorld((minS + maxS) * 0.5f));
            else SelectInBox(ScreenToWorld(minS), ScreenToWorld(maxS));
        }

        FinishEdit();
        CurrentSelectionState = SelectionState.None;
        _uiDirty = true;
    }

    /// <summary>
    /// A tap selects one thing: the nearest within <see cref="Settings.SelectionRadius"/>, and of two about as near the upper
    /// one (ink lies over shapes, later over earlier). The empty inside of an outline is not part of it. Pictures and
    /// fills lie below everything, so the topmost one under the point is picked only when nothing else is near.
    /// </summary>
    private void SelectClosest(Vector2 world)
    {
        switch (PickAt(world, withBackdrop: true))
        {
            case Stroke stroke: SelectedStrokes.Add(stroke); break;
            // A fill is part of its shape: tapping it selects the shape, and the fill goes along with it.
            case BoardObject { Kind: ShapeKind.Fill } fill: SelectedObjects.Add(OwnerOf(fill) ?? fill); break;
            case BoardObject o: SelectedObjects.Add(o); break;
        }
    }

    /// <summary>Whether a press at the point takes hold of the selection: on its lines (within the tap radius) or on a selected shape's fill.</summary>
    private bool GrabsSelection(Vector2 world) => PickAt(world, withBackdrop: true) switch
    {
        Stroke s => SelectedStrokes.Contains(s),
        BoardObject { Kind: ShapeKind.Fill } fill => (OwnerOf(fill) ?? fill) is var owner && SelectedObjects.Any(o => ReferenceEquals(o, owner)),
        BoardObject o => SelectedObjects.Any(x => ReferenceEquals(x, o)),
        _ => false
    };

    /// <summary>What a tap at the point selects (see <see cref="SelectClosest"/>): a stroke, an object or nothing.</summary>
    private object? PickAt(Vector2 world, bool withBackdrop)
    {
        float best = Settings.SelectionRadius / _camera.Zoom;
        float tie = 1f / _camera.Zoom;
        Stroke? closestStroke = null;
        BoardObject? closestObject = null;

        foreach (var o in _document.Objects)
        {
            if (ObjectGeometry.IsBackdrop(o.Kind)) continue;
            float d = ObjectGeometry.Distance(o, world);
            if (d > best + tie) continue;
            best = MathF.Min(best, d);
            closestObject = o;
        }

        foreach (var s in _document.Strokes)
        {
            float d = float.MaxValue;
            for (int i = 0; i < s.Points.Count; i++)
                d = MathF.Min(d, PointToSegmentDistance(world, s.Points[i], s.Points[Math.Min(i + 1, s.Points.Count - 1)]));
            d = MathF.Max(d - s.Width * 0.5f, 0f);
            if (d > best + tie) continue;
            best = MathF.Min(best, d);
            closestStroke = s;
        }

        if (closestStroke != null) return closestStroke;
        if (closestObject != null || !withBackdrop) return closestObject;
        return _document.Objects.LastOrDefault(o => ObjectGeometry.IsBackdrop(o.Kind) && ObjectGeometry.Distance(o, world) == 0f);
    }

    /// <summary>
    /// Selects what the box touches: ink and lines that cross it or lie in it, not shapes whose empty inside it only
    /// covers. A picture or a fill is selected only when the box encloses it, so ink drawn over it can be selected alone.
    /// </summary>
    private void SelectInBox(Vector2 minW, Vector2 maxW)
    {
        static bool Overlaps((Vector2 Min, Vector2 Max) b, Vector2 min, Vector2 max) =>
            b.Max.X >= min.X && b.Min.X <= max.X && b.Max.Y >= min.Y && b.Min.Y <= max.Y;

        foreach (var s in _document.Strokes)
        {
            if (!Overlaps(GetBoundingBox(s.Points), minW, maxW)) continue;
            var r = new Vector2(s.Width * 0.5f);
            for (int i = 0; i < s.Points.Count; i++)
            {
                if (!ObjectGeometry.SegmentTouchesBox(s.Points[i], s.Points[Math.Min(i + 1, s.Points.Count - 1)], minW - r, maxW + r)) continue;
                SelectedStrokes.Add(s);
                break;
            }
        }

        var boxes = ObjectGeometry.ClosedBounds(_document.Objects);
        foreach (var o in _document.Objects)
        {
            var b = ObjectGeometry.Bounds(o);
            bool hit = ObjectGeometry.IsBackdrop(o.Kind)
                ? b.Min.X >= minW.X && b.Min.Y >= minW.Y && b.Max.X <= maxW.X && b.Max.Y <= maxW.Y
                : Overlaps(b, minW, maxW) && ObjectGeometry.Touches(o, minW, maxW);
            // Fills come along with their shapes.
            if (hit && (o.Kind != ShapeKind.Fill || OwnerOf(o, boxes) == null)) SelectedObjects.Add(o);
        }
    }

    /// <summary>True if the press is on something that edits the selection: a toolbar button, a shape's point or a handle.</summary>
    public bool HitsSelectionControls(Vector2 screenPos)
    {
        if (!HasSelection) return false;
        var actions = ToolbarActions();
        for (int i = 0; i < actions.Count; i++)
            if (UiBatch.Contains(ToolbarButtonPos(i, actions.Count), new Vector2(ToolbarButtonSize), screenPos)) return true;
        return HitTestControlPoint(screenPos) >= 0 || HitTestHandles(screenPos) >= 0 || DimensionAt(screenPos) != null;
    }

    private (Vector2 Min, Vector2 Max) HandleFrame()
    {
        var bbox = GetSelectedBounds();
        return (WorldToScreen(bbox.Min) - new Vector2(HandleOffset), WorldToScreen(bbox.Max) + new Vector2(HandleOffset));
    }

    private int HitTestHandles(Vector2 screenPos)
    {
        var (minS, maxS) = HandleFrame();
        var handles = GetHandlePositions(minS, maxS);
        for (int i = 0; i < handles.Length; i++)
        {
            if (Vector2.DistanceSquared(screenPos, handles[i]) < 14f * 14f)
                return i;
        }

        return -1;
    }

    private static Vector2[] GetHandlePositions(Vector2 minS, Vector2 maxS)
    {
        float midX = (minS.X + maxS.X) / 2, midY = (minS.Y + maxS.Y) / 2;
        return
        [
            new(minS.X, minS.Y), new(midX, minS.Y), new(maxS.X, minS.Y),
            new(minS.X, midY), new(maxS.X, midY),
            new(minS.X, maxS.Y), new(midX, maxS.Y), new(maxS.X, maxS.Y),
            new(midX, minS.Y - 32f)
        ];
    }

    public (Vector2 Min, Vector2 Max) GetSelectedBounds()
    {
        if (!HasSelection) return (Vector2.Zero, Vector2.Zero);
        // The selection stays where it was while it is dragged.
        if (CurrentSelectionState == SelectionState.Moving)
            return (Vector2.Transform(_originalBounds.Min, _movingTransform), Vector2.Transform(_originalBounds.Max, _movingTransform));
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var s in SelectedStrokes)
        {
            var b = GetBoundingBox(s.Points);
            min = Vector2.Min(min, b.Min);
            max = Vector2.Max(max, b.Max);
        }

        foreach (var o in SelectedObjects)
        {
            var b = ObjectGeometry.Bounds(o);
            min = Vector2.Min(min, b.Min);
            max = Vector2.Max(max, b.Max);
        }

        return (min, max);
    }

    private void RenderSelectionUI(UiBatch ui)
    {
        var accent = Theme.Accent;
        if (CurrentSelectionState == SelectionState.DrawingBox)
        {
            var min = Vector2.Min(_selectionStartScreen, _selectionEndScreen);
            var max = Vector2.Max(_selectionStartScreen, _selectionEndScreen);
            ui.Rect(min, max - min, Theme.WithAlpha(accent, 0.08f), 4f);
            ui.Border(min, max - min, accent, 1.5f, 4f);
        }

        if (!HasSelection) return;

        var bbox = GetSelectedBounds();
        var minS = WorldToScreen(bbox.Min) - new Vector2(4f);
        var maxS = WorldToScreen(bbox.Max) + new Vector2(4f);
        ui.Border(minS, maxS - minS, Theme.WithAlpha(accent, 0.8f), 1.5f, 4f);

        var (handleMin, handleMax) = HandleFrame();
        var handles = GetHandlePositions(handleMin, handleMax);
        ui.Line(handles[1], handles[8], accent, 1.5f);
        foreach (var h in handles)
        {
            ui.Circle(h, 7f, Theme.Text);
            ui.Ring(h, 7f, 2f, accent);
        }

        if (SingleObject is { } single && !ObjectGeometry.IsBackdrop(single.Kind))
        {
            // A single shape is edited through its own points (squares) and the handles that move a whole side (circles);
            // the box handles still scale and rotate it.
            var points = ObjectGeometry.ControlHandles(single);
            for (int i = 0; i < points.Length; i++)
            {
                var s = WorldToScreen(Vector2.Transform(points[i], _movingTransform));
                float corner = i < single.Points.Length ? 4f : 8f;
                ui.Rect(s - new Vector2(8f), new Vector2(16f), Theme.Accent, corner);
                ui.Border(s - new Vector2(8f), new Vector2(16f), Theme.Text, 2f, corner);
            }
        }

        RenderDimensions(ui);
        if (CurrentSelectionState != SelectionState.None) return;
        var actions = ToolbarActions();
        for (int i = 0; i < actions.Count; i++)
        {
            var pos = ToolbarButtonPos(i, actions.Count);
            var size = new Vector2(ToolbarButtonSize);
            bool hover = HoverPosition is { } p && UiBatch.Contains(pos, size, p);
            bool danger = actions[i] == SelectionAction.Delete;
            ui.Rect(pos, size, hover ? (danger ? Theme.Danger : Theme.SurfaceHover) : Theme.WithAlpha(Theme.Surface, 0.95f), 10f);
            ui.Border(pos, size, Theme.Border, 1f, 10f);
            var iconColor = danger && !hover ? Theme.Danger : Theme.Text;
            if (actions[i] == SelectionAction.Dash)
            {
                ShapePicker.DrawDashes(ui, pos + size * 0.5f, 18f, iconColor);
                continue;
            }

            string icon = actions[i] switch { SelectionAction.Edit => Icons.Edit, SelectionAction.Duplicate => Icons.Copy, _ => Icons.Delete };
            ui.Icon(icon, pos + size * 0.5f, 16f, iconColor);
        }
    }
}
