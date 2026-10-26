using System.Numerics;
using System.Runtime.CompilerServices;
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
    Shape,
    Fill,
    // A tap takes the color and width of the ink or shape under it for drawing.
    FormatPainter,
    // A drag over a solid turns it in space.
    Turn
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
    // The previous rebuild's primitives: comparing with them tells the backend where the changes really start.
    private Prim[] _previousPrims = new Prim[4096];
    private Vector4 _fillBackground;
    private int _strokePrimCount;
    private int _strokesDirtyFrom;
    private bool _framePending;
    private readonly List<Prim> _scratch = new();
    private readonly List<ObjectLabel> _labelScratch = new();
    // Primitives and labels of the objects as last built: objects never change, so a rebuild builds only new ones. The
    // ones used by this rebuild go to the second dictionary, which then takes the place of the first.
    private Dictionary<BoardObject, (Prim[] Prims, ObjectLabel[] Labels)> _built = new(ReferenceEqualityComparer.Instance),
        _builtNow = new(ReferenceEqualityComparer.Instance);

    private LibraryPanel? _libraryPanel;
    private readonly List<IOverlay> _overlays = new();
    private RadialMenu? _radialMenu;
    private QuickBar? _quickBar;

    private Vector2 _screenSize = new(1280, 720);

    private bool _uiDirty = true;
    private bool _strokesDirty = true;
    private Stroke? _activeStroke;
    // A stroke that ended before its last points reached the primitives (a tap ends before any frame is drawn).
    private Stroke? _finishedStroke;
    private int _activeStart = -1;
    private int _activeDirtyFrom = -1;
    private readonly List<Vector2> _rawTail = new(4);

    private float _eraserSize;
    private bool _eraseStateSaved;
    private Vector2? _lastErase;
    private readonly Dictionary<Stroke, (Vector2 Min, Vector2 Max)> _eraseBounds = new();

    public Tool Tool { get; private set; } = Tool.Brush;
    public Vector4 CurrentColor { get; private set; } = Settings.Palette.FirstOrDefault(Vector4.One);
    public float CurrentBrushWidth { get; set; }
    public bool IsQuickShapeApplied { get; set; }
    /// <summary>Where the pointer is over the board; in a solid's building modes the point it would stick to follows it.</summary>
    public Vector2? HoverPosition
    {
        get => _hover;
        set
        {
            _hover = value;
            if (_solidMode is SolidMode.Section or SolidMode.Lines || _clearMenu != null) _uiDirty = true;
            int button = ToolbarButtonAt(value);
            if (button != _hoveredButton)
            {
                _hoveredButton = button;
                _uiDirty = true;
            }
        }
    }

    private Vector2? _hover;
    private int _hoveredButton = -1;

    public StrokeRenderer(Document document, Camera camera)
    {
        _document = document;
        _camera = camera;
        _ui = new UiBatch(_textAtlas);
        _background = new UiBatch(_textAtlas);
        Screenshot = new ScreenshotOverlay(this);

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

    // The tool the format painter goes back to once it took a color and width.
    private Tool _toolBeforePainter = Tool.Brush;

    /// <summary>Switches the format painter on, or off again back to the tool it was started from.</summary>
    public void ToggleFormatPainter()
    {
        if (Tool == Tool.FormatPainter)
        {
            SetTool(_toolBeforePainter);
            return;
        }

        _toolBeforePainter = Tool == Tool.Shape ? Tool.Shape : Tool.Brush;
        SetTool(Tool.FormatPainter);
    }

    /// <summary>
    /// Takes the color and width of the ink or shape at the point (only the color of a fill) and goes back to drawing;
    /// nothing happens on an empty spot or a picture.
    /// </summary>
    public void PaintFormatFrom(Vector2 screenPos)
    {
        switch (PickAt(ScreenToWorld(screenPos), withBackdrop: true))
        {
            case Stroke s:
                CurrentBrushWidth = s.Width;
                SetColor(s.Color);
                break;
            case BoardObject { Kind: ShapeKind.Fill } fill:
                SetColor(fill.Color with { W = 1f });
                break;
            case BoardObject { Kind: not ShapeKind.Image } o:
                CurrentBrushWidth = o.Width;
                SetColor(o.Color);
                break;
            default:
                return;
        }

        SetTool(_toolBeforePainter);
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

    /// <summary>World bounds of everything drawn (rays and lines count only by their points); null for an empty board.</summary>
    public static (Vector2 Min, Vector2 Max)? ContentBounds(Document document)
    {
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var s in document.Strokes)
        {
            var b = GetBoundingBox(s.Points);
            float r = s.Width * 0.5f;
            min = Vector2.Min(min, b.Min - new Vector2(r));
            max = Vector2.Max(max, b.Max + new Vector2(r));
        }

        foreach (var o in document.Objects)
        {
            var b = o.Kind is ShapeKind.Ray or ShapeKind.InfiniteLine ? GetBoundingBox([..o.Points]) : ObjectGeometry.Bounds(o);
            min = Vector2.Min(min, b.Min);
            max = Vector2.Max(max, b.Max);
        }

        return min.X > max.X ? null : (min, max);
    }

    public (Vector2 Min, Vector2 Max)? ContentBounds() => ContentBounds(_document);

    /// <summary>The board as last built: shapes, fills and ink as capsules in world space.</summary>
    public ReadOnlySpan<Prim> WorldPrims => _strokePrims.AsSpan(0, _strokePrimCount);

    public ReadOnlySpan<ImageDraw> FrameImages => CollectionsMarshal.AsSpan(_images);

    public void FitToContent()
    {
        var screen = ScreenSize;
        float left = _libraryPanel?.IsOpen == true ? _libraryPanel.Width : 0f;
        var viewCenter = new Vector2((left + screen.X) * 0.5f, screen.Y * 0.5f);

        if (ContentBounds(_document) is not var (min, max))
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

        if (_activeDirtyFrom >= 0) _finishedStroke = _activeStroke;
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
    public void BeginErase()
    {
        _eraseStateSaved = false;
        _lastErase = null;
        // Only erasing changes the board during the gesture, so stroke bounds are computed once to skip far strokes quickly.
        _eraseBounds.Clear();
        foreach (var s in _document.Strokes) _eraseBounds[s] = GetBoundingBox(s.Points);
    }

    /// <param name="screenRadius">Eraser radius in screen pixels; the eraser size setting if null.</param>
    public void EraseAt(Vector2 screenPos, float? screenRadius = null)
    {
        var to = ScreenToWorld(screenPos);
        var from = _lastErase ?? to;
        _lastErase = to;
        float size = screenRadius / _camera.Zoom ?? _eraserSize;
        // The eraser covers the whole path since the previous event, so fast moves leave no gaps.
        var pathMin = Vector2.Min(from, to) - new Vector2(size);
        var pathMax = Vector2.Max(from, to) + new Vector2(size);

        for (int i = _document.Strokes.Count - 1; i >= 0; i--)
        {
            var stroke = _document.Strokes[i];
            var b = _eraseBounds.TryGetValue(stroke, out var known) ? known : GetBoundingBox(stroke.Points);
            if (!Overlaps(b, pathMin, pathMax, stroke.Width * 0.5f) || !IsStrokeHitByEraser(stroke, from, to, size)) continue;
            SaveEraseState();
            _document.Strokes.RemoveAt(i);
            MarkStrokesDirty();
        }

        int steps = Math.Clamp((int)MathF.Ceiling(Vector2.Distance(from, to) / MathF.Max(size * 0.5f, 1e-3f)), 1, 64);
        var erased = new List<BoardObject>();
        foreach (var o in _document.Objects)
        {
            // Pictures and fills are only removed through the selection, so ink on them can be erased.
            if (ObjectGeometry.IsBackdrop(o.Kind)) continue;
            if (o.Kind is not (ShapeKind.Ray or ShapeKind.InfiniteLine) && !Overlaps(ObjectGeometry.Bounds(o), pathMin, pathMax, o.Width)) continue;
            bool hit = false;
            for (int step = 0; step <= steps && !hit; step++)
            {
                var point = Vector2.Lerp(from, to, step / (float)steps);
                // A plot counts as hit only near its drawing, not anywhere inside its frame.
                hit = (o.Kind == ShapeKind.Plot ? PlotEdgeDistance(o, point) : ObjectGeometry.Distance(o, point)) <= size;
            }

            if (hit) erased.Add(o);
        }

        if (erased.Count == 0) return;
        SaveEraseState();
        RemoveObjects(erased);
    }

    private static bool Overlaps((Vector2 Min, Vector2 Max) b, Vector2 min, Vector2 max, float margin) =>
        b.Max.X + margin >= min.X && b.Min.X - margin <= max.X && b.Max.Y + margin >= min.Y && b.Min.Y - margin <= max.Y;

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

    /// <summary>Whether the eraser moving from one point to another passes within reach of the stroke.</summary>
    private static bool IsStrokeHitByEraser(Stroke stroke, Vector2 from, Vector2 to, float eraserRadius)
    {
        float threshold = eraserRadius + stroke.Width * 0.5f;
        float thresholdSq = threshold * threshold;
        var pts = stroke.Points;
        if (pts.Count == 0) return false;
        if (pts.Count == 1) return PointToSegmentDistanceSq(pts[0], from, to) < thresholdSq;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (SegmentDistanceSq(from, to, pts[i], pts[i + 1]) < thresholdSq)
                return true;
        }

        return false;
    }

    private static float SegmentDistanceSq(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static float Side(Vector2 o, Vector2 p, Vector2 q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        if (Side(c, d, a) > 0 != Side(c, d, b) > 0 && Side(a, b, c) > 0 != Side(a, b, d) > 0) return 0f;
        return MathF.Min(MathF.Min(PointToSegmentDistanceSq(a, c, d), PointToSegmentDistanceSq(b, c, d)),
            MathF.Min(PointToSegmentDistanceSq(c, a, b), PointToSegmentDistanceSq(d, a, b)));
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

    /// <summary>The stroke being drawn, if any.</summary>
    public Stroke? ActiveStroke => _activeStroke;

    /// <summary>
    /// True when no edit is under way that keeps places in the document (a stroke, a shape or a fill being made, a
    /// selection being dragged): changes from other computers are taken in only then.
    /// </summary>
    public bool IsIdleForSync => _activeStroke == null && _recognized == null && _liveObject == null && _fillPlaced < 0 &&
                                 _fillReplaced.Count == 0 && CurrentSelectionState is SelectionState.None or SelectionState.DrawingBox;

    /// <summary>Call after changes from another computer: the selection follows its items by id, removed ones drop out.</summary>
    public void OnRemoteChange()
    {
        var strokes = _document.Strokes.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        var objects = _document.Objects.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        var selectedStrokes = SelectedStrokes.Select(s => strokes.GetValueOrDefault(s.Id)).OfType<Stroke>().ToList();
        var selectedObjects = SelectedObjects.Select(o => objects.GetValueOrDefault(o.Id)).OfType<BoardObject>().ToList();
        SelectedStrokes.Clear();
        SelectedStrokes.AddRange(selectedStrokes);
        SelectedObjects.Clear();
        SelectedObjects.AddRange(selectedObjects);
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
        ResetSolidMode();
        _fillTies = [];
        _carriedTies = [];
        _edited.Clear();
        EndMoving();
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

    private int AppendPrims(ReadOnlySpan<Prim> prims, int index)
    {
        ReserveStrokePrims(index + prims.Length);
        prims.CopyTo(_strokePrims.AsSpan(index));
        return index + prims.Length;
    }

    private (Prim[] Prims, ObjectLabel[] Labels) Built(BoardObject o)
    {
        if (!_built.TryGetValue(o, out var built) && !_builtNow.TryGetValue(o, out built))
        {
            _scratch.Clear();
            _labelScratch.Clear();
            ObjectGeometry.Build(o, _scratch, _labelScratch);
            built = ([.._scratch], [.._labelScratch]);
        }

        _builtNow[o] = built;
        return built;
    }

    /// <summary>Rebuilds changed geometry and the UI before a frame is handed to the backend.</summary>
    public void PrepareFrame()
    {
        if (_refillPending) Refill(_fillTies, Edited, final: false);

        // Solid fills are tinted towards the background (and missing pictures drawn in the theme's gray), so everything
        // is built again when it changes.
        if (Settings.Background != _fillBackground)
        {
            _fillBackground = Settings.Background;
            _built.Clear();
            _strokesDirty = true;
        }

        if (_strokesDirty)
        {
            (_strokePrims, _previousPrims) = (_previousPrims, _strokePrims);
            int previousCount = _strokePrimCount;
            int count = 0;
            _objectLabels.Clear();
            _images.Clear();
            // A selection being dragged is drawn from its own primitives.
            foreach (var o in _document.Objects)
            {
                if (_moving.Contains(o) || o.Kind == ShapeKind.Image && AddImage(o)) continue;
                var (prims, labels) = Built(o);
                count = AppendPrims(prims, count);
                _objectLabels.AddRange(labels);
            }

            (_built, _builtNow) = (_builtNow, _built);
            _builtNow.Clear();
            UpdateImagesVersion();

            int strokeTotal = 0;
            foreach (var s in _document.Strokes)
                if (!_moving.Contains(s)) strokeTotal += SegmentCount(s);
            ReserveStrokePrims(count + strokeTotal);
            foreach (var s in _document.Strokes)
                if (!_moving.Contains(s)) count = WriteStroke(s, 0, _strokePrims, count);

            _activeStart = _activeStroke != null && _document.Strokes.Count > 0 && _document.Strokes[^1] == _activeStroke
                ? count - SegmentCount(_activeStroke)
                : -1;
            _liveStart = count;
            _strokePrimCount = count;
            // Erasing one stroke leaves everything before it as it was, so backends can keep that part.
            int unchanged = MemoryMarshal.AsBytes(_previousPrims.AsSpan(0, previousCount))
                .CommonPrefixLength(MemoryMarshal.AsBytes(_strokePrims.AsSpan(0, count))) / Unsafe.SizeOf<Prim>();
            _strokesDirtyFrom = Math.Min(_strokesDirtyFrom, unchanged);
            _strokesDirty = false;
            _activeDirtyFrom = -1;
            _finishedStroke = null;
            _liveDirty = _liveObject != null;
            if (!_labelsKey.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(_objectLabels)))
            {
                _labelsKey = [.._objectLabels];
                _labelsVersion++;
            }
        }
        else if (_activeDirtyFrom >= 0)
        {
            if ((_activeStroke ?? _finishedStroke) is { } stroke && _activeStart >= 0)
            {
                int segments = SegmentCount(stroke);
                int from = stroke.Points.Count == 1 ? 0 : Math.Min(_activeDirtyFrom, segments - 1);
                ReserveStrokePrims(_activeStart + segments);
                WriteStroke(stroke, from, _strokePrims, _activeStart + from);
                _strokePrimCount = _activeStart + segments;
                _strokesDirtyFrom = Math.Min(_strokesDirtyFrom, _activeStart + from);
            }

            _activeDirtyFrom = -1;
            _finishedStroke = null;
        }

        if (_liveDirty)
        {
            // The shape being drawn is appended after everything else and rewritten every frame.
            _liveLabels.Clear();
            _scratch.Clear();
            if (_liveObject != null) ObjectGeometry.Build(_liveObject, _scratch, _liveLabels);
            _strokePrimCount = AppendPrims(CollectionsMarshal.AsSpan(_scratch), _liveStart);
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

        if (Screenshot.IsOpen)
        {
            // Covers the whole window, so nothing else is drawn.
            Screenshot.Render(_ui, screen);
            if (_textAtlas.NeedsRedraw) _uiDirty = true;
            return;
        }

        DrawLabels(_ui, _movingLabels, _movingTransform);
        DrawLabels(_ui, _liveLabels);
        RenderShapeGuides(_ui);
        if (Tool is Tool.Select or Tool.Shape) RenderSelectionUI(_ui);
        if (Tool == Tool.Eraser && HoverPosition is { } hover && _radialMenu?.IsOpen != true)
            _ui.Ring(hover, MathF.Max(_eraserSize * _camera.Zoom, 3f), 1.5f, Theme.WithAlpha(ColorMath.ContrastOn(Settings.Background), 0.6f));

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
        ClearColor = Settings.Background,
        CameraPosition = _camera.Position,
        CameraZoom = _camera.Zoom,
        UiScale = UiScale,
        Strokes = _strokePrims.AsSpan(0, _strokePrimCount),
        StrokesDirtyFrom = Math.Min(_strokesDirtyFrom, _strokePrimCount),
        StrokesStable = _activeStroke != null && _activeStart >= 0 ? _activeStart
            : _liveObject != null ? _liveStart
            : _strokePrimCount,
        Moving = _movingPrims,
        MovingTransform = _movingTransform,
        MovingVersion = _movingVersion,
        Background = CollectionsMarshal.AsSpan(_background.Prims),
        BackgroundVersion = _backgroundVersion,
        Images = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_images),
        ImagesVersion = _imagesVersion,
        OverlayImages = OverlayImages(),
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
