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
    Pan,
    Shape
}

/// <summary>
/// The board: drawing, erasing, shapes and selection, plus turning the document and the interface into
/// primitives for the render backend. Selection and shapes live in the other partial files.
/// </summary>
public sealed partial class StrokeRenderer : IDisposable
{
    // New input points closer than this (in screen pixels) to the previous one are dropped.
    private const float MinPointSpacingPx = 1.5f;
    // Maximum distance (in screen pixels) between the smoothed curve and its polyline approximation.
    private const float SmoothingTolerancePx = 0.2f;

    private readonly Document _document;
    private readonly Camera _camera;

    private readonly TextAtlas _textAtlas = new();
    private readonly UiBatch _ui;

    // World primitives: objects first (below the ink), then strokes, then the stroke or shape being drawn.
    // The backend uploads the range changed since the last presented frame.
    private Prim[] _strokePrims = new Prim[4096];
    private int _strokePrimCount;
    private int _strokesDirtyFrom;
    private bool _framePending;
    private readonly List<Prim> _scratch = new();

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

    public StrokeRenderer(Document document, Camera camera)
    {
        _document = document;
        _camera = camera;
        _ui = new UiBatch(_textAtlas);
        _background = new UiBatch(_textAtlas);

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

    public bool NeedsRedraw => _uiDirty || _strokesDirty || _activeDirtyFrom >= 0 || _liveDirty || _framePending;

    public void SetTool(Tool tool)
    {
        if (Tool == tool) return;
        CancelShape();
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

        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var s in _document.Strokes)
        {
            var b = GetBoundingBox(s.Points);
            float r = s.Width * 0.5f;
            min = Vector2.Min(min, b.Min - new Vector2(r));
            max = Vector2.Max(max, b.Max + new Vector2(r));
        }

        foreach (var o in _document.Objects)
        {
            if (o.Kind is ShapeKind.Ray or ShapeKind.InfiniteLine) continue;
            var b = ObjectGeometry.Bounds(o);
            min = Vector2.Min(min, b.Min);
            max = Vector2.Max(max, b.Max);
        }

        if (min.X > max.X)
        {
            _camera.Zoom = 1f;
            _camera.Position = viewCenter;
        }
        else
        {
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

        if (_recognized != null)
        {
            ResizeRecognizedShape(ScreenToWorld(screenPos));
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
        if (_recognized != null)
        {
            CommitLiveObject();
            _recognized = null;
        }

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

    /// <summary>Removes the stroke being drawn (or the shape it became) as if it was never started.</summary>
    public void CancelStroke()
    {
        if (_recognized != null)
        {
            _recognized = null;
            _liveObject = null;
            _document.DiscardLastState();
            MarkStrokesDirty();
            return;
        }

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
            SaveEraseState();
            _document.Strokes.RemoveAt(i);
            MarkStrokesDirty();
        }

        for (int i = _document.Objects.Count - 1; i >= 0; i--)
        {
            var o = _document.Objects[i];
            // A plot counts as hit only near its drawing, not anywhere inside its frame.
            float distance = o.Kind == ShapeKind.Plot ? PlotEdgeDistance(o, worldPos) : ObjectGeometry.Distance(o, worldPos);
            if (distance > _eraserSize) continue;
            SaveEraseState();
            _document.Objects.RemoveAt(i);
            MarkStrokesDirty();
        }
    }

    private static float PlotEdgeDistance(BoardObject plot, Vector2 world)
    {
        var p = plot.Points;
        var corner = p[1] + p[2] - p[0];
        return MathF.Min(MathF.Min(PointToSegmentDistance(world, p[0], p[1]), PointToSegmentDistance(world, p[0], p[2])),
            MathF.Min(PointToSegmentDistance(world, p[1], corner), PointToSegmentDistance(world, p[2], corner)));
    }

    private void SaveEraseState()
    {
        if (!_eraseStateSaved)
        {
            _document.SaveState();
            _eraseStateSaved = true;
        }

        _document.IsDirty = true;
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
        CancelShape();
        _document.SaveState();
        _document.Strokes.Clear();
        _document.Objects.Clear();
        ClearSelection();
        MarkStrokesDirty();
    }

    public void Undo()
    {
        EndStroke();
        CancelShape();
        if (_document.Undo()) OnDocumentReplaced();
    }

    public void Redo()
    {
        EndStroke();
        CancelShape();
        if (_document.Redo()) OnDocumentReplaced();
    }

    /// <summary>Call after the document's contents were replaced wholesale (undo, load, new file).</summary>
    public void OnDocumentReplaced()
    {
        _activeStroke = null;
        _rawTail.Clear();
        _recognized = null;
        _liveObject = null;
        _shapeMode = ShapeMode.None;
        SelectedStrokes.Clear();
        SelectedObjects.Clear();
        CurrentSelectionState = SelectionState.None;
        MarkStrokesDirty();
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

    // ---------------------------------------------------------------- frame building

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

    private int AppendPrims(List<Prim> prims, int index)
    {
        ReserveStrokePrims(index + prims.Count);
        prims.CopyTo(_strokePrims, index);
        return index + prims.Count;
    }

    /// <summary>Rebuilds changed geometry and the UI before a frame is handed to the backend.</summary>
    public void PrepareFrame()
    {
        if (_strokesDirty)
        {
            int count = 0;
            _objectLabels.Clear();
            foreach (var o in _document.Objects)
            {
                _scratch.Clear();
                ObjectGeometry.Build(o, _scratch, _objectLabels);
                count = AppendPrims(_scratch, count);
            }

            int strokeTotal = 0;
            foreach (var s in _document.Strokes) strokeTotal += SegmentCount(s);
            ReserveStrokePrims(count + strokeTotal);
            foreach (var s in _document.Strokes) count = WriteStroke(s, 0, _strokePrims, count);

            _activeStart = _activeStroke != null && _document.Strokes.Count > 0 && _document.Strokes[^1] == _activeStroke
                ? count - SegmentCount(_activeStroke)
                : -1;
            _liveStart = count;
            _strokePrimCount = count;
            _strokesDirtyFrom = 0;
            _strokesDirty = false;
            _activeDirtyFrom = -1;
            _liveDirty = _liveObject != null;
            if (!_objectsVersionKey.SequenceEqual(_document.Objects))
            {
                _objectsVersionKey = [.._document.Objects];
                _objectsVersion++;
            }
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

        if (_liveDirty)
        {
            // The shape being drawn is appended after everything else and rewritten every frame.
            _liveLabels.Clear();
            _scratch.Clear();
            if (_liveObject != null) ObjectGeometry.Build(_liveObject, _scratch, _liveLabels);
            _strokePrimCount = AppendPrims(_scratch, _liveStart);
            _strokesDirtyFrom = Math.Min(_strokesDirtyFrom, _liveStart);
            _liveDirty = false;
            _uiDirty = true;
        }

        if (_uiDirty)
        {
            _uiDirty = false;
            BuildUI();
        }

        BuildBackgroundIfNeeded();
        _framePending = true;
    }

    private void BuildUI()
    {
        _textAtlas.BeginFrame(UiScale);
        _ui.Clear(UiScale);
        var screen = ScreenSize;

        DrawLabels(_ui, _liveLabels);
        RenderShapeGuides(_ui);
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
        StrokesStable = _activeStroke != null && _activeStart >= 0 ? _activeStart
            : _liveObject != null ? _liveStart
            : _strokePrimCount,
        Background = CollectionsMarshal.AsSpan(_background.Prims),
        BackgroundVersion = _backgroundVersion,
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

    public void Dispose() => _textAtlas.Dispose();
}
