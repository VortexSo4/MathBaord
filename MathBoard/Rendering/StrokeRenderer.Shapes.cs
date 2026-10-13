using System.Globalization;
using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

public sealed partial class StrokeRenderer
{
    private enum ShapeMode { None, Creating, AnglePending, SecondArm }

    // Snap the direction of lines to multiples of 45° and angles to multiples of 15° when this close (degrees).
    private const float DirectionSnapDegrees = 4f;
    private const float AngleSnapDegrees = 2.5f;
    // Boxes snap to squares when width and height differ by less than this share.
    private const float SquareSnap = 0.06f;

    public ShapeKind CurrentShape { get; set; } = ShapeKind.Line;
    public bool IsAnglePending => _shapeMode == ShapeMode.AnglePending;

    private ShapeMode _shapeMode;
    private Vector2 _shapeStart;

    // The object being created or edited is kept out of the document and drawn after everything else,
    // so only its own primitives change while the pointer moves.
    private BoardObject? _liveObject;
    private int _liveIndex = -1;
    private int _liveStart;
    private bool _liveDirty;
    private readonly List<ObjectLabel> _liveLabels = new();

    // A stroke recognised as a shape keeps following the pointer until it is released.
    private BoardObject? _recognized;
    private Vector2 _recognizedAnchor;
    private float _recognizedStartDistance;

    // Snapping guide in screen space, shown while a shape snaps.
    private (Vector2 A, Vector2 B)? _guide;

    private readonly UiBatch _background;
    private int _backgroundVersion;
    private (Vector2 Camera, float Zoom, float Scale, Vector2 Screen, string Grid, int Objects, int Resets, Vector4 Clear) _backgroundKey;
    private int _objectsVersion;
    private List<BoardObject> _objectsVersionKey = [];
    private readonly List<ObjectLabel> _objectLabels = new();

    private void BeginLive(BoardObject o, int index)
    {
        _liveObject = o;
        _liveIndex = index;
        if (!_strokesDirty) _liveStart = _strokePrimCount;
        _liveDirty = true;
    }

    private void UpdateLive(BoardObject o)
    {
        _liveObject = o;
        _liveDirty = true;
    }

    private void CommitLiveObject()
    {
        if (_liveObject == null) return;
        if (_liveIndex >= 0 && _liveIndex <= _document.Objects.Count) _document.Objects.Insert(_liveIndex, _liveObject);
        else _document.Objects.Add(_liveObject);
        _document.IsDirty = true;
        _liveObject = null;
        _liveIndex = -1;
        _liveLabels.Clear();
        _guide = null;
        MarkStrokesDirty();
    }

    /// <summary>Drops a shape that is being created, including a half-made angle.</summary>
    public void CancelShape()
    {
        if (_shapeMode == ShapeMode.None) return;
        _shapeMode = ShapeMode.None;
        _liveObject = null;
        _liveLabels.Clear();
        _guide = null;
        _document.DiscardLastState();
        MarkStrokesDirty();
    }

    // ---------------------------------------------------------------- shape tool

    public void BeginShape(Vector2 screenPos)
    {
        var world = ScreenToWorld(screenPos);
        if (_shapeMode == ShapeMode.AnglePending && _liveObject != null)
        {
            _shapeMode = ShapeMode.SecondArm;
            UpdateShape(screenPos);
            return;
        }

        _document.SaveState();
        _shapeStart = world;
        _shapeMode = ShapeMode.Creating;
        BeginLive(ObjectGeometry.FromDrag(CurrentShape, world, world, CurrentColor, CurrentBrushWidth), -1);
    }

    public void UpdateShape(Vector2 screenPos)
    {
        var world = ScreenToWorld(screenPos);
        _guide = null;

        if (_shapeMode == ShapeMode.SecondArm && _liveObject != null)
        {
            var p = _liveObject.Points;
            UpdateLive(_liveObject with { Points = [p[0], p[1], SnapAngleArm(p[0], p[1], world)] });
            return;
        }

        if (_shapeMode != ShapeMode.Creating) return;
        var end = CurrentShape switch
        {
            ShapeKind.Line or ShapeKind.Arrow or ShapeKind.Ray or ShapeKind.InfiniteLine or ShapeKind.Angle => SnapDirection(_shapeStart, world),
            ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.Triangle or ShapeKind.Plot => SnapSquare(_shapeStart, world),
            var kind when ObjectGeometry.IsSolid(kind) => SnapSquare(_shapeStart, world),
            _ => world
        };
        UpdateLive(ObjectGeometry.FromDrag(CurrentShape, _shapeStart, end, CurrentColor, CurrentBrushWidth));
    }

    public void EndShape(Vector2 screenPos)
    {
        if (_shapeMode == ShapeMode.None || _liveObject == null) return;
        UpdateShape(screenPos);

        if (_shapeMode == ShapeMode.Creating && CurrentShape == ShapeKind.Angle)
        {
            // The second arm is placed with the next press.
            _shapeMode = ShapeMode.AnglePending;
            _guide = null;
            return;
        }

        bool tooSmall = _shapeMode == ShapeMode.Creating && CurrentShape != ShapeKind.Point &&
                        Vector2.Distance(_shapeStart, ScreenToWorld(screenPos)) * _camera.Zoom < 4f;
        _shapeMode = ShapeMode.None;
        if (tooSmall)
        {
            _liveObject = null;
            _document.DiscardLastState();
            _guide = null;
            MarkStrokesDirty();
            return;
        }

        var created = _liveObject;
        CommitLiveObject();
        // Selected right away, so it can be adjusted without switching tools.
        SelectedStrokes.Clear();
        SelectedObjects.Clear();
        SelectedObjects.Add(created);
        if (created.Kind == ShapeKind.Plot) EditPlotRequested?.Invoke(created);
    }

    /// <summary>A tap with the shape tool: places a point, or finishes an angle.</summary>
    public void TapShape(Vector2 screenPos)
    {
        if (_shapeMode == ShapeMode.AnglePending)
        {
            BeginShape(screenPos);
            EndShape(screenPos);
            return;
        }

        if (CurrentShape != ShapeKind.Point) return;
        BeginShape(screenPos);
        EndShape(screenPos);
    }

    private Vector2 SnapDirection(Vector2 start, Vector2 end)
    {
        var d = end - start;
        float length = d.Length();
        if (length < 1e-3f) return end;
        float angle = MathF.Atan2(d.Y, d.X);
        float step = MathF.PI / 4f;
        float snapped = MathF.Round(angle / step) * step;
        if (MathF.Abs(angle - snapped) * 180f / MathF.PI > DirectionSnapDegrees) return end;

        var dir = new Vector2(MathF.Cos(snapped), MathF.Sin(snapped));
        SetGuide(start, dir);
        return start + dir * length;
    }

    private Vector2 SnapAngleArm(Vector2 vertex, Vector2 first, Vector2 end)
    {
        var a = first - vertex;
        var b = end - vertex;
        float length = b.Length();
        if (length < 1e-3f) return end;
        float a0 = MathF.Atan2(a.Y, a.X);
        float delta = MathF.Atan2(b.Y, b.X) - a0;
        float step = MathF.PI / 12f;
        float snapped = MathF.Round(delta / step) * step;
        if (MathF.Abs(delta - snapped) * 180f / MathF.PI > AngleSnapDegrees) return end;

        var dir = new Vector2(MathF.Cos(a0 + snapped), MathF.Sin(a0 + snapped));
        SetGuide(vertex, dir);
        return vertex + dir * length;
    }

    private Vector2 SnapSquare(Vector2 start, Vector2 end)
    {
        var d = end - start;
        float w = MathF.Abs(d.X), h = MathF.Abs(d.Y);
        if (MathF.Max(w, h) < 1e-3f || MathF.Abs(w - h) > MathF.Max(w, h) * SquareSnap) return end;

        float side = (w + h) * 0.5f;
        var squared = start + new Vector2(MathF.CopySign(side, d.X), MathF.CopySign(side, d.Y));
        SetGuide(start, Vector2.Normalize(squared - start));
        return squared;
    }

    private void SetGuide(Vector2 worldOrigin, Vector2 dir)
    {
        var origin = WorldToScreen(worldOrigin);
        float reach = ScreenSize.Length();
        _guide = (origin - dir * reach, origin + dir * reach);
    }

    private void RenderShapeGuides(UiBatch ui)
    {
        if (_guide is { } g) ui.Line(g.A, g.B, Theme.WithAlpha(Theme.Accent, 0.55f), 1f);
    }

    // ---------------------------------------------------------------- recognised shapes

    /// <summary>
    /// Turns the stroke being drawn into a shape object. The shape then keeps following the pointer (it is
    /// resized, not continued) until the pen is lifted.
    /// </summary>
    public void ApplyQuickShape()
    {
        var stroke = _activeStroke;
        if (stroke == null || stroke.Points.Count < 3) return;

        var bbox = GetBoundingBox(stroke.Points);
        float diag = (bbox.Max - bbox.Min).Length();
        if (diag < 15f) return;

        var simplified = RDP(stroke.Points, diag * 0.05f);
        var distinct = new List<Vector2>();
        foreach (var p in simplified)
            if (distinct.Count == 0 || Vector2.Distance(distinct[^1], p) > 1f) distinct.Add(p);
        if (distinct.Count < 2) return;

        bool isClosed = distinct.Count > 2 && Vector2.Distance(distinct[0], distinct[^1]) < diag * 0.25f;
        BoardObject shape;
        if (isClosed)
        {
            distinct[0] = (distinct[0] + distinct[^1]) * 0.5f;
            distinct.RemoveAt(distinct.Count - 1);
            var corners = FilterCorners(distinct);
            if (corners.Count == 3)
                shape = new BoardObject(ShapeKind.Triangle, corners.ToArray(), stroke.Color, stroke.Width);
            else if (corners.Count == 4)
                shape = ObjectGeometry.FromDrag(ShapeKind.Rectangle, bbox.Min, bbox.Max, stroke.Color, stroke.Width);
            else
                shape = RecognizedEllipse(bbox.Min, bbox.Max, stroke);
        }
        else
        {
            shape = new BoardObject(ShapeKind.Line, [stroke.Points[0], stroke.Points[^1]], stroke.Color, stroke.Width);
        }

        // The stroke's undo snapshot (taken when it started) now belongs to the shape.
        var pointer = _rawTail[^1];
        _document.Strokes.Remove(stroke);
        _activeStroke = null;
        _rawTail.Clear();

        _recognized = shape;
        _recognizedAnchor = shape.Kind == ShapeKind.Line ? shape.Points[0] : (bbox.Min + bbox.Max) * 0.5f;
        _recognizedStartDistance = MathF.Max(Vector2.Distance(pointer, _recognizedAnchor), 1f);
        BeginLive(shape, -1);
        _document.IsDirty = true;
        MarkStrokesDirty();
    }

    private static BoardObject RecognizedEllipse(Vector2 min, Vector2 max, Stroke stroke)
    {
        var center = (min + max) * 0.5f;
        var half = (max - min) * 0.5f;
        // Nearly round outlines become exact circles.
        if (MathF.Abs(half.X - half.Y) < MathF.Max(half.X, half.Y) * 0.15f)
            half = new Vector2((half.X + half.Y) * 0.5f);
        return new BoardObject(ShapeKind.Ellipse, [center, center + new Vector2(half.X, 0), center + new Vector2(0, half.Y)],
            stroke.Color, stroke.Width);
    }

    private void ResizeRecognizedShape(Vector2 pointer)
    {
        var shape = _recognized!;
        if (shape.Kind == ShapeKind.Line)
        {
            UpdateLive(shape with { Points = [shape.Points[0], SnapDirection(shape.Points[0], pointer)] });
            return;
        }

        float factor = MathF.Max(Vector2.Distance(pointer, _recognizedAnchor) / _recognizedStartDistance, 0.05f);
        UpdateLive(shape.Transformed(Matrix3x2.CreateScale(factor, _recognizedAnchor)));
    }

    private static List<Vector2> FilterCorners(List<Vector2> pts)
    {
        if (pts.Count <= 3) return pts.ToList();

        var result = new List<Vector2>();
        int n = pts.Count;
        const float angleThreshold = MathF.PI / 7f;

        for (int i = 0; i < n; i++)
        {
            var v1 = pts[i] - pts[(i - 1 + n) % n];
            var v2 = pts[(i + 1) % n] - pts[i];
            float len1 = v1.Length();
            float len2 = v2.Length();
            if (len1 < 0.001f || len2 < 0.001f) continue;

            float cos = Math.Clamp(Vector2.Dot(v1, v2) / (len1 * len2), -1f, 1f);
            if (MathF.Acos(cos) > angleThreshold)
                result.Add(pts[i]);
        }

        return result;
    }

    private static List<Vector2> RDP(List<Vector2> points, float epsilon)
    {
        if (points.Count < 3) return points.ToList();
        float maxDist = 0;
        int index = 0;
        for (int i = 1; i < points.Count - 1; i++)
        {
            float d = PointToSegmentDistanceSq(points[i], points[0], points[^1]);
            if (d > maxDist)
            {
                maxDist = d;
                index = i;
            }
        }

        if (maxDist <= epsilon * epsilon) return [points[0], points[^1]];
        var left = RDP(points.GetRange(0, index + 1), epsilon);
        var right = RDP(points.GetRange(index, points.Count - index), epsilon);
        return [..left.Take(left.Count - 1), ..right];
    }

    // ---------------------------------------------------------------- background: grid and object labels

    private void BuildBackgroundIfNeeded()
    {
        (Vector2 Camera, float Zoom, float Scale, Vector2 Screen, string Grid, int Objects, int Resets, Vector4 Clear) key =
            (_camera.Position, _camera.Zoom, UiScale, ScreenSize, Settings.GridMode.Value, _objectsVersion,
                _textAtlas.ResetCount, Settings.Background);
        if (key == _backgroundKey) return;

        // Camera moves keep the background attached to the world, so caches that follow the camera stay valid;
        // anything else is a new background version.
        var previous = _backgroundKey;
        if (key with { Camera = previous.Camera, Zoom = previous.Zoom } != previous) _backgroundVersion++;
        _backgroundKey = key;

        _background.Clear(UiScale);
        BuildGrid(_background);
        DrawLabels(_background, _objectLabels);
        if (_textAtlas.NeedsRedraw) _uiDirty = true;
    }

    private void DrawLabels(UiBatch ui, List<ObjectLabel> labels)
    {
        foreach (var label in labels)
        {
            var center = WorldToScreen(label.World);
            if (center.X < -50 || center.Y < -50 || center.X > ScreenSize.X + 50 || center.Y > ScreenSize.Y + 50) continue;
            var size = new Vector2(ui.Measure(label.Text, Theme.Small) + 4f, 18f);
            ui.TextInRect(label.Text, center - size * 0.5f, size, label.Color, Theme.Small, UiBatch.Align.Center);
        }
    }

    private void BuildGrid(UiBatch ui)
    {
        string mode = Settings.GridMode.Value;
        if (mode is not ("Squares" or "Axes")) return;

        var ink = ColorMath.ContrastOn(Settings.Background);
        float zoom = _camera.Zoom;

        // Lines at least 24 px apart; numbers and stronger lines at least 56 px apart, both at round values.
        float units = ObjectGeometry.NiceCeil(24f / zoom / ObjectGeometry.GridUnit);
        float step = units * ObjectGeometry.GridUnit;
        int every = Math.Max(1, (int)MathF.Round(ObjectGeometry.NiceCeil(56f / zoom / ObjectGeometry.GridUnit) / units));

        var minW = ScreenToWorld(Vector2.Zero);
        var maxW = ScreenToWorld(ScreenSize);
        var screen = ScreenSize;
        int kx0 = (int)MathF.Ceiling(minW.X / step), kx1 = (int)MathF.Floor(maxW.X / step);
        int ky0 = (int)MathF.Ceiling(minW.Y / step), ky1 = (int)MathF.Floor(maxW.Y / step);

        for (int k = kx0; k <= kx1; k++)
        {
            float x = WorldToScreen(new Vector2(k * step, 0)).X;
            ui.Rect(new Vector2(x - 0.5f, 0), new Vector2(1f, screen.Y), Theme.WithAlpha(ink, k % every == 0 ? 0.13f : 0.06f));
        }

        for (int k = ky0; k <= ky1; k++)
        {
            float y = WorldToScreen(new Vector2(0, k * step)).Y;
            ui.Rect(new Vector2(0, y - 0.5f), new Vector2(screen.X, 1f), Theme.WithAlpha(ink, k % every == 0 ? 0.13f : 0.06f));
        }

        if (mode != "Axes") return;

        var origin = WorldToScreen(Vector2.Zero);
        var axisColor = Theme.WithAlpha(ink, 0.55f);
        var labelColor = Theme.WithAlpha(ink, 0.6f);
        // Axes that are off screen keep their labels along the nearest edge.
        float axisY = Math.Clamp(origin.Y, 0f, screen.Y);
        float axisX = Math.Clamp(origin.X, 0f, screen.X);
        ui.Line(new Vector2(0, axisY), new Vector2(screen.X - 2f, axisY), axisColor, 1.5f);
        ui.Line(new Vector2(axisX, screen.Y), new Vector2(axisX, 2f), axisColor, 1.5f);
        ui.Line(new Vector2(screen.X - 2f, axisY), new Vector2(screen.X - 12f, axisY - 5f), axisColor, 1.5f);
        ui.Line(new Vector2(screen.X - 2f, axisY), new Vector2(screen.X - 12f, axisY + 5f), axisColor, 1.5f);
        ui.Line(new Vector2(axisX, 2f), new Vector2(axisX - 5f, 12f), axisColor, 1.5f);
        ui.Line(new Vector2(axisX, 2f), new Vector2(axisX + 5f, 12f), axisColor, 1.5f);

        float labelY = Math.Clamp(axisY + 4f, 2f, screen.Y - 20f);
        for (int k = kx0; k <= kx1; k++)
        {
            if (k == 0 || k % every != 0) continue;
            float x = WorldToScreen(new Vector2(k * step, 0)).X;
            string text = FormatUnits(k * units);
            float w = ui.Measure(text, Theme.Small) + 4f;
            ui.TextInRect(text, new Vector2(x - w * 0.5f, labelY), new Vector2(w, 16f), labelColor, Theme.Small, UiBatch.Align.Center);
        }

        for (int k = ky0; k <= ky1; k++)
        {
            if (k == 0 || k % every != 0) continue;
            float y = WorldToScreen(new Vector2(0, k * step)).Y;
            // Screen y grows downwards, mathematical y upwards.
            string text = FormatUnits(-k * units);
            float w = ui.Measure(text, Theme.Small) + 4f;
            float x = Math.Clamp(axisX - 6f - w, 2f, screen.X - w - 2f);
            ui.TextInRect(text, new Vector2(x, y - 8f), new Vector2(w, 16f), labelColor, Theme.Small, UiBatch.Align.Right);
        }

        ui.TextInRect("0", new Vector2(axisX - 14f, labelY), new Vector2(10f, 16f), labelColor, Theme.Small, UiBatch.Align.Right);
    }

    private static string FormatUnits(float value) =>
        MathF.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture).Replace("-", "−");
}
