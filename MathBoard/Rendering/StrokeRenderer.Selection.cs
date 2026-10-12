using System.Numerics;
using MathBoard.Core;

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

    private enum SelectionAction { Edit, Duplicate, Delete }

    private const float ToolbarButtonSize = 36f;
    private const float PointHandleRadius = 16f;

    public bool IsSelectMode => Tool == Tool.Select;
    public List<Stroke> SelectedStrokes { get; } = new();
    public List<BoardObject> SelectedObjects { get; } = new();
    public SelectionState CurrentSelectionState { get; set; } = SelectionState.None;

    /// <summary>Raised when the user asks to edit a plot's functions.</summary>
    public event Action<BoardObject>? EditPlotRequested;

    private bool HasSelection => SelectedStrokes.Count > 0 || SelectedObjects.Count > 0;
    private BoardObject? SingleObject => SelectedStrokes.Count == 0 && SelectedObjects.Count == 1 ? SelectedObjects[0] : null;

    private Vector2 _selectionStartScreen;
    private Vector2 _selectionEndScreen;
    private int _activeHandle = -1;
    private Dictionary<Stroke, List<Vector2>> _originalPoints = new();
    private List<(int Index, BoardObject Original)> _originalObjects = new();
    private (Vector2 Min, Vector2 Max) _originalBounds;
    private Vector2 _startMouseWorld;

    public void ClearSelection()
    {
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
        var objects = SelectedObjects.ToHashSet(ReferenceEqualityComparer.Instance);
        _document.Strokes.RemoveAll(strokes.Contains);
        _document.Objects.RemoveAll(objects.Contains);
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
        var objects = SelectedObjects.Select(o => o.Transformed(offset)).ToList();
        _document.Strokes.AddRange(strokes);
        _document.Objects.AddRange(objects);

        SelectedStrokes.Clear();
        SelectedStrokes.AddRange(strokes);
        SelectedObjects.Clear();
        SelectedObjects.AddRange(objects);
        MarkStrokesDirty();
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

    private List<SelectionAction> ToolbarActions()
    {
        var actions = new List<SelectionAction>();
        if (SingleObject?.Kind == ShapeKind.Plot) actions.Add(SelectionAction.Edit);
        actions.Add(SelectionAction.Duplicate);
        actions.Add(SelectionAction.Delete);
        return actions;
    }

    /// <summary>Buttons in a row above the selection's top-right corner.</summary>
    private Vector2 ToolbarButtonPos(int index, int count)
    {
        var bbox = GetSelectedBounds();
        var minS = WorldToScreen(bbox.Min) - new Vector2(4f);
        var maxS = WorldToScreen(bbox.Max) + new Vector2(4f);
        float width = count * ToolbarButtonSize + (count - 1) * 6f;
        var start = new Vector2(maxS.X + 10f - width + ToolbarButtonSize, minS.Y - 10f - ToolbarButtonSize);
        start = Vector2.Clamp(start, new Vector2(4f), Vector2.Max(ScreenSize - new Vector2(width + 4f, ToolbarButtonSize + 4f), new Vector2(4f)));
        return start + new Vector2(index * (ToolbarButtonSize + 6f), 0);
    }

    private int HitTestControlPoint(Vector2 screenPos)
    {
        if (SingleObject is not { } o) return -1;
        for (int i = 0; i < o.Points.Length; i++)
            if (Vector2.Distance(WorldToScreen(o.Points[i]), screenPos) < PointHandleRadius)
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
                    _document.Objects.RemoveAt(index);
                    BeginLive(o, index);
                    _activeHandle = point;
                    CurrentSelectionState = SelectionState.DraggingPoint;
                    MarkStrokesDirty();
                    return;
                }
            }

            int handle = HitTestHandles(screenPos);
            var bbox = GetSelectedBounds();
            var minS = WorldToScreen(bbox.Min);
            var maxS = WorldToScreen(bbox.Max);
            bool insideBox = screenPos.X >= minS.X && screenPos.X <= maxS.X &&
                             screenPos.Y >= minS.Y && screenPos.Y <= maxS.Y;

            if (handle != -1 || insideBox)
            {
                _document.SaveState();
                CurrentSelectionState = handle == -1 ? SelectionState.Moving
                    : handle == 8 ? SelectionState.Rotating : SelectionState.Scaling;
                _activeHandle = handle;
                _originalPoints = SelectedStrokes.ToDictionary(s => s, s => s.Points);
                _originalObjects = SelectedObjects
                    .Select(o => (_document.Objects.FindIndex(x => ReferenceEquals(x, o)), o))
                    .Where(t => t.Item1 >= 0).ToList();
                _originalBounds = bbox;
                _startMouseWorld = ScreenToWorld(screenPos);
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
                return;
            case SelectionState.None or SelectionState.DraggingPoint:
                return;
        }

        var mouseWorld = ScreenToWorld(screenPos);
        Matrix3x2 m = Matrix3x2.Identity;

        if (CurrentSelectionState == SelectionState.Moving)
        {
            m = Matrix3x2.CreateTranslation(mouseWorld - _startMouseWorld);
        }
        else if (CurrentSelectionState == SelectionState.Scaling)
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
            _document.Objects[index] = original.Transformed(m);
            SelectedObjects.Add(_document.Objects[index]);
        }

        MarkStrokesDirty();
    }

    public void HandleSelectionUp(Vector2 screenPos)
    {
        if (CurrentSelectionState == SelectionState.DraggingPoint)
        {
            CommitLiveObject();
        }
        else if (CurrentSelectionState == SelectionState.DrawingBox)
        {
            var minS = Vector2.Min(_selectionStartScreen, screenPos);
            var maxS = Vector2.Max(_selectionStartScreen, screenPos);
            if ((maxS - minS).LengthSquared() < 25f) SelectClosest(ScreenToWorld(screenPos));
            else SelectInBox(ScreenToWorld(minS), ScreenToWorld(maxS));
        }

        CurrentSelectionState = SelectionState.None;
        _uiDirty = true;
    }

    private void SelectClosest(Vector2 world)
    {
        float tolerance = 10f / _camera.Zoom;
        Stroke? closestStroke = null;
        BoardObject? closestObject = null;
        float best = tolerance;

        foreach (var s in _document.Strokes)
        {
            for (int i = 0; i < Math.Max(1, s.Points.Count - 1) && s.Points.Count > 0; i++)
            {
                var b = s.Points[Math.Min(i + 1, s.Points.Count - 1)];
                float d = PointToSegmentDistance(world, s.Points[i], b) - s.Width * 0.5f;
                if (d < best)
                {
                    best = d;
                    closestStroke = s;
                }
            }
        }

        // Ink lies on top of objects, so an object only wins if it is clearly closer.
        foreach (var o in _document.Objects)
        {
            float d = ObjectGeometry.Distance(o, world);
            if (d < best - 1e-3f)
            {
                best = d;
                closestObject = o;
                closestStroke = null;
            }
        }

        if (closestStroke != null) SelectedStrokes.Add(closestStroke);
        else if (closestObject != null) SelectedObjects.Add(closestObject);
    }

    private void SelectInBox(Vector2 minW, Vector2 maxW)
    {
        static bool Overlaps((Vector2 Min, Vector2 Max) b, Vector2 min, Vector2 max) =>
            b.Max.X >= min.X && b.Min.X <= max.X && b.Max.Y >= min.Y && b.Min.Y <= max.Y;

        foreach (var s in _document.Strokes)
            if (Overlaps(GetBoundingBox(s.Points), minW, maxW)) SelectedStrokes.Add(s);
        foreach (var o in _document.Objects)
            if (Overlaps(ObjectGeometry.Bounds(o), minW, maxW)) SelectedObjects.Add(o);
    }

    private int HitTestHandles(Vector2 screenPos)
    {
        var bbox = GetSelectedBounds();
        var handles = GetHandlePositions(WorldToScreen(bbox.Min), WorldToScreen(bbox.Max));
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

        var handles = GetHandlePositions(minS, maxS);
        ui.Line(handles[1], handles[8], accent, 1.5f);
        foreach (var h in handles)
        {
            ui.Circle(h, 7f, Theme.Text);
            ui.Ring(h, 7f, 2f, accent);
        }

        if (SingleObject is { } single)
        {
            // A single shape is edited through its own points; the box handles still scale and rotate it.
            foreach (var p in single.Points)
            {
                var s = WorldToScreen(p);
                ui.Rect(s - new Vector2(8f), new Vector2(16f), Theme.Accent, 4f);
                ui.Border(s - new Vector2(8f), new Vector2(16f), Theme.Text, 2f, 4f);
            }
        }

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
            string icon = actions[i] switch { SelectionAction.Edit => Icons.Edit, SelectionAction.Duplicate => Icons.Copy, _ => Icons.Delete };
            ui.Icon(icon, pos + size * 0.5f, 16f, danger && !hover ? Theme.Danger : Theme.Text);
        }
    }
}
