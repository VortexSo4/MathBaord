using System.Numerics;
using System.Runtime.InteropServices;
using MathBoard.Core;
using MathBoard.Graphics;

namespace MathBoard.Rendering;

public enum Tool
{
    Brush,
    Eraser,
    Select,
    Pan
}

public sealed class StrokeRenderer : IDisposable
{
    // New input points closer than this (in screen pixels) to the previous one are dropped.
    private const float MinPointSpacingPx = 1.5f;
    // Maximum distance (in screen pixels) between the smoothed curve and its polyline approximation.
    private const float SmoothingTolerancePx = 0.2f;

    private readonly Document _document;
    private readonly Camera _camera;

    private readonly TextAtlas _textAtlas = new();
    private readonly UiBatch _ui;

    // Stroke primitives in document order; the backend uploads the range changed since the last presented frame.
    private Prim[] _strokePrims = new Prim[4096];
    private int _strokePrimCount;
    private int _strokesDirtyFrom;
    private bool _framePending;

    private LibraryPanel? _libraryPanel;
    private readonly List<IOverlay> _overlays = new();
    private RadialMenu? _radialMenu;
    private QuickBar? _quickBar;

    private Vector2 _screenSize = new(1280, 720);

    private bool _uiDirty = true;
    private bool _strokesDirty = true;
    private Stroke? _activeStroke;
    private int _activeStart = -1;
    private int _activeDirtyFrom = -1;
    private readonly List<Vector2> _rawTail = new(4);

    private float _eraserSize;
    private bool _eraseStateSaved;

    public Tool Tool { get; private set; } = Tool.Brush;
    public Vector4 CurrentColor { get; private set; } = Settings.Colors[0];
    public float CurrentBrushWidth { get; set; }
    public bool IsQuickShapeApplied { get; set; }
    public Vector2? HoverPosition { get; set; }

    public bool IsSelectMode => Tool == Tool.Select;
    public List<Stroke> SelectedStrokes { get; } = new();

    public enum SelectionState
    {
        None,
        DrawingBox,
        Moving,
        Scaling,
        Rotating
    }

    public SelectionState CurrentSelectionState { get; set; } = SelectionState.None;

    private Vector2 _selectionStartScreen;
    private Vector2 _selectionEndScreen;
    private int _activeHandle = -1;
    private Dictionary<Stroke, List<Vector2>> _originalPoints = new();
    private (Vector2 Min, Vector2 Max) _originalBounds;
    private Vector2 _startMouseWorld;

    public StrokeRenderer(Document document, Camera camera)
    {
        _document = document;
        _camera = camera;
        _ui = new UiBatch(_textAtlas);

        CurrentBrushWidth = Settings.DefaultBrushWidth;
        _eraserSize = Settings.DefaultEraserSize;
    }

    public void SetLibraryPanel(LibraryPanel panel) => _libraryPanel = panel;
    public void SetRadialMenu(RadialMenu menu) => _radialMenu = menu;
    public void SetQuickBar(QuickBar bar) => _quickBar = bar;

    /// <summary>Adds UI drawn above everything else, in the order added.</summary>
    public void AddOverlay(IOverlay overlay) => _overlays.Add(overlay);

    public Camera Camera => _camera;
    public TextAtlas TextAtlas => _textAtlas;

    /// <summary>Screen size in logical pixels.</summary>
    public Vector2 ScreenSize => _screenSize;

    /// <summary>Framebuffer pixels per logical pixel.</summary>
    public float UiScale { get; private set; } = 1f;

    public void UpdateViewport(Vector2 framebufferSize, float scale)
    {
        var logical = framebufferSize / scale;
        if (logical == _screenSize && scale == UiScale) return;
        _screenSize = logical;
        UiScale = scale;
        _uiDirty = true;
    }

    public Vector2 ScreenToWorld(Vector2 screen) => (screen - _camera.Position) / _camera.Zoom;
    public Vector2 WorldToScreen(Vector2 world) => world * _camera.Zoom + _camera.Position;

    /// <summary>Requests a redraw; the UI is rebuilt, stroke geometry is kept.</summary>
    public void SetDirty() => _uiDirty = true;

    public void MarkStrokesDirty()
    {
        _strokesDirty = true;
        _uiDirty = true;
    }

    public bool NeedsRedraw => _uiDirty || _strokesDirty || _activeDirtyFrom >= 0 || _framePending;

    public void SetTool(Tool tool)
    {
        if (Tool == tool) return;
        Tool = tool;
        ClearSelection();
    }

    public void SetColor(Vector4 color)
    {
        CurrentColor = color;
        _uiDirty = true;
    }

    public void ZoomAt(Vector2 screenPos, float factor)
    {
        var worldBefore = ScreenToWorld(screenPos);
        _camera.Zoom = Math.Clamp(_camera.Zoom * factor, Settings.CameraMinZoom, Settings.CameraMaxZoom);
        _camera.Position = screenPos - worldBefore * _camera.Zoom;
        _uiDirty = true;
    }

    public void FitToContent()
    {
        var screen = ScreenSize;
        float left = _libraryPanel?.IsOpen == true ? _libraryPanel.Width : 0f;
        var viewCenter = new Vector2((left + screen.X) * 0.5f, screen.Y * 0.5f);

        if (_document.Strokes.Count == 0)
        {
            _camera.Zoom = 1f;
            _camera.Position = viewCenter;
        }
        else
        {
            Vector2 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var s in _document.Strokes)
            {
                var b = GetBoundingBox(s.Points);
                float r = s.Width * 0.5f;
                min = Vector2.Min(min, b.Min - new Vector2(r));
                max = Vector2.Max(max, b.Max + new Vector2(r));
            }

            var size = Vector2.Max(max - min, new Vector2(1f));
            var available = new Vector2(screen.X - left, screen.Y) * 0.85f;
            _camera.Zoom = Math.Clamp(MathF.Min(available.X / size.X, available.Y / size.Y),
                Settings.CameraMinZoom, Settings.CameraMaxZoom);
            _camera.Position = viewCenter - (min + max) * 0.5f * _camera.Zoom;
        }

        _uiDirty = true;
    }

    // ---------------------------------------------------------------- strokes

    public void BeginStroke(Vector2 screenPos)
    {
        if (Tool == Tool.Eraser)
        {
            BeginErase();
            EraseAt(screenPos);
            return;
        }

        _document.SaveState();
        var worldPos = ScreenToWorld(screenPos);
        _activeStroke = new Stroke { Width = CurrentBrushWidth, Color = CurrentColor, Points = [worldPos] };
        _document.Strokes.Add(_activeStroke);
        _rawTail.Clear();
        _rawTail.Add(worldPos);
        _activeStart = _strokesDirty ? -1 : _strokePrimCount;
        MarkActiveDirty(0);
    }

    public void AddPoint(Vector2 screenPos)
    {
        if (Tool == Tool.Eraser)
        {
            EraseAt(screenPos);
            return;
        }

        if (_activeStroke == null) return;
        var world = ScreenToWorld(screenPos);
        if (Vector2.Distance(world, _rawTail[^1]) * _camera.Zoom < MinPointSpacingPx) return;

        _rawTail.Add(world);
        if (_rawTail.Count > 4) _rawTail.RemoveAt(0);

        var pts = _activeStroke.Points;
        int n = _rawTail.Count;
        if (n >= 3)
        {
            // The segment ending at the previous point now has both neighbours known, so it can be smoothed.
            pts.RemoveAt(pts.Count - 1);
            int firstChanged = pts.Count - 1;
            var before = n >= 4 ? _rawTail[n - 4] : _rawTail[n - 3];
            AppendSmoothSegment(pts, before, _rawTail[n - 3], _rawTail[n - 2], _rawTail[n - 1]);
            pts.Add(world);
            MarkActiveDirty(firstChanged);
        }
        else
        {
            pts.Add(world);
            MarkActiveDirty(pts.Count - 2);
        }

        _document.IsDirty = true;
    }

    public void EndStroke()
    {
        if (_activeStroke != null && _rawTail.Count >= 2)
        {
            var pts = _activeStroke.Points;
            int n = _rawTail.Count;
            pts.RemoveAt(pts.Count - 1);
            int firstChanged = Math.Max(0, pts.Count - 1);
            var before = n >= 3 ? _rawTail[n - 3] : _rawTail[n - 2];
            AppendSmoothSegment(pts, before, _rawTail[n - 2], _rawTail[n - 1], _rawTail[n - 1]);
            MarkActiveDirty(firstChanged);
        }

        _activeStroke = null;
        _rawTail.Clear();
        IsQuickShapeApplied = false;
    }

    /// <summary>Removes the stroke being drawn as if it was never started.</summary>
    public void CancelStroke()
    {
        if (_activeStroke == null) return;
        _document.Strokes.Remove(_activeStroke);
        _document.DiscardLastState();
        _activeStroke = null;
        _rawTail.Clear();
        MarkStrokesDirty();
    }

    private void MarkActiveDirty(int firstChangedPoint)
    {
        int segment = Math.Max(0, firstChangedPoint);
        _activeDirtyFrom = _activeDirtyFrom < 0 ? segment : Math.Min(_activeDirtyFrom, segment);
    }

    /// <summary>Appends a centripetal Catmull-Rom curve from p1 to p2 (p1 itself is assumed to be present already).</summary>
    private void AppendSmoothSegment(List<Vector2> pts, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        float tolerance = SmoothingTolerancePx / _camera.Zoom;
        float deviation = PointToSegmentDistance(CatmullRom(p0, p1, p2, p3, 0.5f), p1, p2);
        int steps = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(deviation / tolerance)), 1, 16);
        for (int i = 1; i < steps; i++)
            pts.Add(CatmullRom(p0, p1, p2, p3, i / (float)steps));
        pts.Add(p2);
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float u)
    {
        static float Knot(Vector2 a, Vector2 b) => MathF.Max(MathF.Sqrt(Vector2.Distance(a, b)), 1e-4f);

        float t1 = Knot(p0, p1);
        float t2 = t1 + Knot(p1, p2);
        float t3 = t2 + Knot(p2, p3);
        float t = t1 + (t2 - t1) * u;

        var a1 = ((t1 - t) * p0 + t * p1) / t1;
        var a2 = ((t2 - t) * p1 + (t - t1) * p2) / (t2 - t1);
        var a3 = ((t3 - t) * p2 + (t - t2) * p3) / (t3 - t2);
        var b1 = ((t2 - t) * a1 + t * a2) / t2;
        var b2 = ((t3 - t) * a2 + (t - t1) * a3) / (t3 - t1);
        return ((t2 - t) * b1 + (t - t1) * b2) / (t2 - t1);
    }

    /// <summary>Starts an erase gesture; the whole gesture becomes a single undo step.</summary>
    public void BeginErase() => _eraseStateSaved = false;

    public void EraseAt(Vector2 screenPos)
    {
        var worldPos = ScreenToWorld(screenPos);
        for (int i = _document.Strokes.Count - 1; i >= 0; i--)
        {
            if (!IsStrokeHitByEraser(_document.Strokes[i], worldPos, _eraserSize)) continue;
            if (!_eraseStateSaved)
            {
                _document.SaveState();
                _eraseStateSaved = true;
            }

            _document.Strokes.RemoveAt(i);
            _document.IsDirty = true;
            MarkStrokesDirty();
        }
    }

    private static bool IsStrokeHitByEraser(Stroke stroke, Vector2 eraserPos, float eraserRadius)
    {
        float threshold = eraserRadius + stroke.Width * 0.5f;
        float thresholdSq = threshold * threshold;
        var pts = stroke.Points;
        if (pts.Count == 0) return false;
        if (pts.Count == 1) return Vector2.DistanceSquared(pts[0], eraserPos) < thresholdSq;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (PointToSegmentDistanceSq(eraserPos, pts[i], pts[i + 1]) < thresholdSq)
                return true;
        }

        return false;
    }

    private static float PointToSegmentDistanceSq(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-6f) return Vector2.DistanceSquared(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    private static float PointToSegmentDistance(Vector2 p, Vector2 a, Vector2 b) =>
        MathF.Sqrt(PointToSegmentDistanceSq(p, a, b));

    public void ClearAll()
    {
        EndStroke();
        _document.SaveState();
        _document.Strokes.Clear();
        SelectedStrokes.Clear();
        MarkStrokesDirty();
    }

    public void Undo()
    {
        EndStroke();
        if (_document.Undo()) OnDocumentReplaced();
    }

    public void Redo()
    {
        EndStroke();
        if (_document.Redo()) OnDocumentReplaced();
    }

    /// <summary>Call after the document's strokes were replaced wholesale (undo, load, new file).</summary>
    public void OnDocumentReplaced()
    {
        _activeStroke = null;
        _rawTail.Clear();
        SelectedStrokes.Clear();
        CurrentSelectionState = SelectionState.None;
        MarkStrokesDirty();
    }

    // ---------------------------------------------------------------- selection

    public void ClearSelection()
    {
        SelectedStrokes.Clear();
        CurrentSelectionState = SelectionState.None;
        _uiDirty = true;
    }

    public void DeleteSelection()
    {
        if (SelectedStrokes.Count == 0) return;
        _document.SaveState();
        var selected = SelectedStrokes.ToHashSet();
        _document.Strokes.RemoveAll(selected.Contains);
        ClearSelection();
        MarkStrokesDirty();
    }

    private const float DeleteButtonSize = 36f;

    /// <summary>Delete button shown next to the selection's top-right corner.</summary>
    private Vector2 DeleteButtonPos()
    {
        var bbox = GetSelectedBounds();
        var minS = WorldToScreen(bbox.Min) - new Vector2(4f);
        var maxS = WorldToScreen(bbox.Max) + new Vector2(4f);
        var pos = new Vector2(maxS.X + 10f, minS.Y - 10f - DeleteButtonSize);
        return Vector2.Clamp(pos, new Vector2(4f), Vector2.Max(ScreenSize - new Vector2(DeleteButtonSize + 4f), new Vector2(4f)));
    }

    public void HandleSelectionDown(Vector2 screenPos)
    {
        if (SelectedStrokes.Count > 0 && UiBatch.Contains(DeleteButtonPos(), new Vector2(DeleteButtonSize), screenPos))
        {
            DeleteSelection();
            return;
        }

        if (SelectedStrokes.Count > 0)
        {
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
                _originalBounds = bbox;
                _startMouseWorld = ScreenToWorld(screenPos);
                return;
            }
        }

        SelectedStrokes.Clear();
        CurrentSelectionState = SelectionState.DrawingBox;
        _selectionStartScreen = screenPos;
        _selectionEndScreen = screenPos;
        _uiDirty = true;
    }

    public void HandleSelectionMove(Vector2 screenPos)
    {
        if (CurrentSelectionState == SelectionState.DrawingBox)
        {
            _selectionEndScreen = screenPos;
            _uiDirty = true;
            return;
        }

        if (CurrentSelectionState == SelectionState.None) return;

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

        MarkStrokesDirty();
    }

    public void HandleSelectionUp(Vector2 screenPos)
    {
        if (CurrentSelectionState == SelectionState.DrawingBox)
        {
            var minS = Vector2.Min(_selectionStartScreen, screenPos);
            var maxS = Vector2.Max(_selectionStartScreen, screenPos);

            if ((maxS - minS).LengthSquared() < 25f)
            {
                var clickWorld = ScreenToWorld(screenPos);
                Stroke? closest = null;
                float minDist = float.MaxValue;
                foreach (var s in _document.Strokes)
                {
                    if (s.Points.Count == 0) continue;
                    for (int i = 0; i < Math.Max(1, s.Points.Count - 1); i++)
                    {
                        var b = s.Points[Math.Min(i + 1, s.Points.Count - 1)];
                        float d = PointToSegmentDistance(clickWorld, s.Points[i], b) - s.Width * 0.5f;
                        if (d < minDist)
                        {
                            minDist = d;
                            closest = s;
                        }
                    }
                }

                if (closest != null && minDist < 10f / _camera.Zoom)
                    SelectedStrokes.Add(closest);
            }
            else
            {
                var minW = ScreenToWorld(minS);
                var maxW = ScreenToWorld(maxS);
                foreach (var s in _document.Strokes)
                {
                    var bbox = GetBoundingBox(s.Points);
                    if (bbox.Max.X >= minW.X && bbox.Min.X <= maxW.X &&
                        bbox.Max.Y >= minW.Y && bbox.Min.Y <= maxW.Y)
                    {
                        SelectedStrokes.Add(s);
                    }
                }
            }
        }

        CurrentSelectionState = SelectionState.None;
        _uiDirty = true;
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
        if (SelectedStrokes.Count == 0) return (Vector2.Zero, Vector2.Zero);
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var s in SelectedStrokes)
        {
            var b = GetBoundingBox(s.Points);
            min = Vector2.Min(min, b.Min);
            max = Vector2.Max(max, b.Max);
        }

        return (min, max);
    }

    public static (Vector2 Min, Vector2 Max) GetBoundingBox(List<Vector2> points)
    {
        if (points.Count == 0) return (Vector2.Zero, Vector2.Zero);
        Vector2 min = points[0], max = points[0];
        foreach (var p in points)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        return (min, max);
    }

    // ---------------------------------------------------------------- quick shapes

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
        {
            if (distinct.Count == 0 || Vector2.Distance(distinct[^1], p) > 1f) distinct.Add(p);
        }

        if (distinct.Count < 2) return;

        bool isClosed = distinct.Count > 2 &&
                        Vector2.Distance(distinct[0], distinct[^1]) < diag * 0.25f;

        if (isClosed)
        {
            var merged = (distinct[0] + distinct[^1]) * 0.5f;
            distinct[0] = merged;
            distinct.RemoveAt(distinct.Count - 1);

            var corners = FilterCorners(distinct);

            if (corners.Count == 3)
                stroke.Points = [corners[0], corners[1], corners[2], corners[0]];
            else if (corners.Count == 4)
                stroke.Points = GenerateRectangle(bbox.Min, bbox.Max);
            else
                stroke.Points = GenerateCircle(bbox.Min, bbox.Max);
        }
        else
        {
            stroke.Points = [stroke.Points[0], stroke.Points[^1]];
        }

        _rawTail.Clear();
        _rawTail.Add(stroke.Points[^1]);
        _document.IsDirty = true;
        MarkStrokesDirty();
    }

    private static List<Vector2> FilterCorners(List<Vector2> pts)
    {
        if (pts.Count <= 3) return pts.ToList();

        var result = new List<Vector2>();
        int n = pts.Count;
        const float angleThreshold = MathF.PI / 7f;

        for (int i = 0; i < n; i++)
        {
            var prev = pts[(i - 1 + n) % n];
            var curr = pts[i];
            var next = pts[(i + 1) % n];

            var v1 = curr - prev;
            var v2 = next - curr;
            float len1 = v1.Length();
            float len2 = v2.Length();
            if (len1 < 0.001f || len2 < 0.001f) continue;

            float cos = Math.Clamp(Vector2.Dot(v1, v2) / (len1 * len2), -1f, 1f);
            if (MathF.Acos(cos) > angleThreshold)
                result.Add(curr);
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

        List<Vector2> result = new();
        if (maxDist > epsilon * epsilon)
        {
            var left = RDP(points.GetRange(0, index + 1), epsilon);
            var right = RDP(points.GetRange(index, points.Count - index), epsilon);
            result.AddRange(left.Take(left.Count - 1));
            result.AddRange(right);
        }
        else
        {
            result.Add(points[0]);
            result.Add(points[^1]);
        }

        return result;
    }

    private static List<Vector2> GenerateRectangle(Vector2 min, Vector2 max) =>
        [new(min.X, min.Y), new(max.X, min.Y), new(max.X, max.Y), new(min.X, max.Y), new(min.X, min.Y)];

    private static List<Vector2> GenerateCircle(Vector2 min, Vector2 max)
    {
        List<Vector2> pts = new();
        Vector2 center = (min + max) * 0.5f;
        float rx = (max.X - min.X) * 0.5f;
        float ry = (max.Y - min.Y) * 0.5f;
        const int segments = 96;
        for (int i = 0; i < segments; i++)
        {
            float a = i * MathF.PI * 2f / segments;
            pts.Add(center + new Vector2(MathF.Cos(a) * rx, MathF.Sin(a) * ry));
        }

        pts.Add(pts[0]);
        return pts;
    }

    // ---------------------------------------------------------------- GPU

    private static int SegmentCount(Stroke s) => s.Points.Count == 0 ? 0 : Math.Max(1, s.Points.Count - 1);

    private static int WriteStroke(Stroke s, int fromSegment, Prim[] dst, int index)
    {
        var pts = s.Points;
        float r = s.Width * 0.5f;
        if (pts.Count == 1)
        {
            dst[index++] = Prim.Capsule(pts[0], pts[0], r, s.Color);
            return index;
        }

        for (int k = fromSegment; k < pts.Count - 1; k++)
            dst[index++] = Prim.Capsule(pts[k], pts[k + 1], r, s.Color);
        return index;
    }

    private void ReserveStrokePrims(int count)
    {
        if (count > _strokePrims.Length)
            Array.Resize(ref _strokePrims, Math.Max(count, _strokePrims.Length * 2));
    }

    /// <summary>Rebuilds changed geometry and the UI before a frame is handed to the backend.</summary>
    public void PrepareFrame()
    {
        if (_strokesDirty)
        {
            int total = 0;
            foreach (var s in _document.Strokes) total += SegmentCount(s);
            ReserveStrokePrims(total);

            int count = 0;
            foreach (var s in _document.Strokes) count = WriteStroke(s, 0, _strokePrims, count);
            _strokePrimCount = count;
            _strokesDirtyFrom = 0;

            _activeStart = _activeStroke != null && _document.Strokes.Count > 0 && _document.Strokes[^1] == _activeStroke
                ? count - SegmentCount(_activeStroke)
                : -1;
            _strokesDirty = false;
            _activeDirtyFrom = -1;
        }
        else if (_activeDirtyFrom >= 0)
        {
            if (_activeStroke != null && _activeStart >= 0)
            {
                int segments = SegmentCount(_activeStroke);
                int from = _activeStroke.Points.Count == 1 ? 0 : Math.Min(_activeDirtyFrom, segments - 1);
                ReserveStrokePrims(_activeStart + segments);
                WriteStroke(_activeStroke, from, _strokePrims, _activeStart + from);
                _strokePrimCount = _activeStart + segments;
                _strokesDirtyFrom = Math.Min(_strokesDirtyFrom, _activeStart + from);
            }

            _activeDirtyFrom = -1;
        }

        if (_uiDirty)
        {
            _uiDirty = false;
            BuildUI();
        }

        _framePending = true;
    }

    private void BuildUI()
    {
        _textAtlas.BeginFrame(UiScale);
        _ui.Clear(UiScale);
        var screen = ScreenSize;

        if (Tool == Tool.Select) RenderSelectionUI(_ui);
        if (Tool == Tool.Eraser && HoverPosition is { } hover && _radialMenu?.IsOpen != true)
            _ui.Ring(hover, MathF.Max(_eraserSize * _camera.Zoom, 3f), 1.5f, new Vector4(1f, 1f, 1f, 0.6f));

        _quickBar?.Render(_ui, screen);
        _libraryPanel?.Render(_ui, screen);
        _radialMenu?.Render(_ui);
        if (_libraryPanel?.IsModalOpen != true) _quickBar?.RenderOverlay(_ui, screen);
        _libraryPanel?.RenderOverlay(_ui, screen);
        foreach (var overlay in _overlays) overlay.Render(_ui, screen);

        if (_textAtlas.NeedsRedraw) _uiDirty = true;
    }

    public FrameData GetFrame() => new()
    {
        ClearColor = Settings.BackgroundColor.Value,
        CameraPosition = _camera.Position,
        CameraZoom = _camera.Zoom,
        UiScale = UiScale,
        Strokes = _strokePrims.AsSpan(0, _strokePrimCount),
        StrokesDirtyFrom = Math.Min(_strokesDirtyFrom, _strokePrimCount),
        StrokesStable = _activeStroke != null && _activeStart >= 0 ? _activeStart : _strokePrimCount,
        Ui = CollectionsMarshal.AsSpan(_ui.Prims),
        AtlasPixels = _textAtlas.Pixels,
        AtlasSize = TextAtlas.Size,
        AtlasDirtyTop = _textAtlas.DirtyTop,
        AtlasDirtyBottom = _textAtlas.DirtyBottom
    };

    /// <summary>Call after the backend presented the frame from <see cref="GetFrame"/>.</summary>
    public void OnPresented()
    {
        _strokesDirtyFrom = int.MaxValue;
        _textAtlas.ClearDirty();
        _framePending = false;
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

        if (SelectedStrokes.Count == 0) return;

        var bbox = GetSelectedBounds();
        var minS = WorldToScreen(bbox.Min) - new Vector2(4f);
        var maxS = WorldToScreen(bbox.Max) + new Vector2(4f);
        ui.Border(minS, maxS - minS, accent, 1.5f, 4f);

        var handles = GetHandlePositions(minS, maxS);
        ui.Line(handles[1], handles[8], accent, 1.5f);
        foreach (var h in handles)
        {
            ui.Circle(h, 7f, Theme.Text);
            ui.Ring(h, 7f, 2f, accent);
        }

        if (CurrentSelectionState == SelectionState.None)
        {
            var pos = DeleteButtonPos();
            var size = new Vector2(DeleteButtonSize);
            bool hover = HoverPosition is { } p && UiBatch.Contains(pos, size, p);
            ui.Rect(pos, size, hover ? Theme.Danger : Theme.WithAlpha(Theme.Surface, 0.95f), 10f);
            ui.Border(pos, size, Theme.Border, 1f, 10f);
            ui.Icon(Icons.Delete, pos + size * 0.5f, 16f, hover ? Theme.Text : Theme.Danger);
        }
    }

    public void Dispose() => _textAtlas.Dispose();
}
