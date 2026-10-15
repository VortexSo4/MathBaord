using System.Numerics;
using System.Runtime.InteropServices;
using MathBoard.Graphics;
using Silk.NET.Windowing;
using SkiaSharp;

namespace MathBoard.Backend.Software;

/// <summary>
/// CPU rendering with Skia, presented through GDI. Works without any GPU driver. Finished strokes are cached in a
/// layer for the current camera; panning shifts the layer and only redraws the uncovered strips. A selection being
/// dragged is cached in a layer of its own, which is shifted as it moves.
/// </summary>
public sealed partial class SoftwareBackend : IRenderBackend
{
    private IWindow? _window;
    private nint _hwnd;

    private SKBitmap? _frame;
    private SKCanvas? _frameCanvas;
    private SKBitmap? _layer, _spareLayer;
    private SKBitmap? _movingLayer;
    private SKBitmap? _atlas;
    // Drawing an SKBitmap copies it into a temporary image on every call, which for the 1 MB atlas made each
    // glyph cost a full copy. Images that wrap the pixels are drawn without copying.
    private SKImage? _atlasImage;
    private readonly Dictionary<ImageData, SKImage> _pictures = new();
    private readonly SKSamplingOptions _pictureSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    // State the stroke layer was rendered with.
    private bool _layerValid;
    private Vector2 _layerCamera;
    private float _layerZoom;
    private Vector4 _layerClear;
    private int _layerBackground;
    private int _layerImages;

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _glyph = new();
    private readonly SKPaint _image = new();
    private Vector4 _canvasClip;

    public string Name => "Software";
    public int Priority => 100;

    public WindowOptions ConfigureWindow(WindowOptions options) => options with { API = GraphicsAPI.None };

    public void Initialize(IWindow window)
    {
        if (!OperatingSystem.IsWindows() || window.Native?.Win32 is not { } win32)
            throw new NotSupportedException("The software backend presents through GDI and needs a Win32 window.");
        _window = window;
        _hwnd = win32.Hwnd;
    }

    public bool Render(in FrameData frame)
    {
        var size = _window!.FramebufferSize;
        if (size.X == 0 || size.Y == 0) return false;

        if (_frame == null || _frame.Width != size.X || _frame.Height != size.Y)
        {
            DisposeSurfaces();
            var info = new SKImageInfo(size.X, size.Y, SKColorType.Bgra8888, SKAlphaType.Premul);
            _frame = new SKBitmap(info);
            _frameCanvas = new SKCanvas(_frame);
            _layer = new SKBitmap(info);
            _spareLayer = new SKBitmap(info);
            _movingLayer = new SKBitmap(info);
            _layerValid = false;
            _movingValid = false;
        }

        UpdateAtlas(frame);
        UpdatePictures(frame);
        UpdateLayer(frame);

        var canvas = _frameCanvas!;
        canvas.ResetMatrix();
        DrawWrapped(canvas, _layer!, 0, 0);
        if (frame.Moving.Length > 0) DrawMoving(canvas, frame);

        float scale = frame.UiScale;
        canvas.SetMatrix(CameraMatrix(frame.CameraPosition, frame.CameraZoom, scale));
        DrawStrokes(canvas, frame.Strokes, frame.StrokesStable, frame.Strokes.Length);

        canvas.SetMatrix(SKMatrix.CreateScale(scale, scale));
        DrawPictures(canvas, frame.OverlayImages);
        _canvasClip = Prim.NoClip;
        foreach (var prim in frame.Ui) DrawPrim(canvas, prim);
        SetClip(canvas, Prim.NoClip);

        Present();
        return true;
    }

    private static void DrawWrapped(SKCanvas canvas, SKBitmap bitmap, float x, float y)
    {
        using var image = SKImage.FromPixels(bitmap.PeekPixels());
        canvas.DrawImage(image, x, y);
    }

    private void UpdatePictures(in FrameData frame)
    {
        var used = new HashSet<ImageData>();
        foreach (var draw in frame.Images) used.Add(draw.Image);
        foreach (var draw in frame.OverlayImages) used.Add(draw.Image);

        foreach (var image in _pictures.Keys.Where(i => !used.Contains(i)).ToList())
        {
            _pictures[image].Dispose();
            _pictures.Remove(image);
        }

        foreach (var image in used.Where(i => !_pictures.ContainsKey(i)))
        {
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            _pictures[image] = SKImage.FromPixelCopy(info, image.Pixels);
        }
    }

    /// <summary>Draws pictures in the canvas's current space.</summary>
    private void DrawPictures(SKCanvas canvas, ReadOnlySpan<ImageDraw> images)
    {
        foreach (var draw in images)
        {
            var picture = _pictures[draw.Image];
            float w = picture.Width, h = picture.Height;
            var local = new SKMatrix(draw.U.X / w, draw.V.X / h, draw.Origin.X, draw.U.Y / w, draw.V.Y / h, draw.Origin.Y, 0, 0, 1);
            canvas.Save();
            canvas.Concat(in local);
            canvas.DrawImage(picture, 0, 0, _pictureSampling);
            canvas.Restore();
        }
    }

    private static SKMatrix CameraMatrix(Vector2 camera, float zoom, float scale) =>
        new(zoom * scale, 0, camera.X * scale, 0, zoom * scale, camera.Y * scale, 0, 0, 1);

    private void UpdateLayer(in FrameData frame)
    {
        int stable = Math.Min(frame.StrokesStable, frame.Strokes.Length);
        if (frame.StrokesDirtyFrom < _pathsBuilt) DropPathsFrom(frame.StrokesDirtyFrom);
        BuildPaths(frame.Strokes, stable);
        var removed = DisposeDroppedPaths();

        bool sameView = _layerValid && _layerZoom == frame.CameraZoom * frame.UiScale && _layerClear == frame.ClearColor &&
                        _layerBackground == frame.BackgroundVersion && _layerImages == frame.ImagesVersion;
        var shift = (frame.CameraPosition - _layerCamera) * frame.UiScale;
        var whole = new Vector2(MathF.Round(shift.X), MathF.Round(shift.Y));
        var matrix = CameraMatrix(frame.CameraPosition, frame.CameraZoom, frame.UiScale);
        int w = _layer!.Width, h = _layer.Height;

        if (sameView && shift == Vector2.Zero)
        {
            UpdateLayerInPlace(frame, matrix, removed);
            return;
        }

        using (var canvas = new SKCanvas(_spareLayer))
        {
            canvas.Clear(ToColor(frame.ClearColor));
            var regions = new List<SKRect> { new(0, 0, w, h) };
            bool canShift = sameView && Vector2.DistanceSquared(shift, whole) < 1e-4f &&
                            MathF.Abs(whole.X) < w && MathF.Abs(whole.Y) < h && removed.IsEmpty &&
                            _paths.All(path => path.InLayer);
            if (canShift)
            {
                // Only the strips uncovered by the shift need the strokes again.
                DrawWrapped(canvas, _layer, whole.X, whole.Y);
                regions.Clear();
                if (whole.X > 0) regions.Add(new SKRect(0, 0, whole.X, h));
                if (whole.X < 0) regions.Add(new SKRect(w + whole.X, 0, w, h));
                if (whole.Y > 0) regions.Add(new SKRect(0, 0, w, whole.Y));
                if (whole.Y < 0) regions.Add(new SKRect(0, h + whole.Y, w, h));
            }

            foreach (var region in regions) RedrawRegion(canvas, frame, matrix, region);
        }

        (_layer, _spareLayer) = (_spareLayer, _layer);
        _layerValid = true;
        _layerCamera = frame.CameraPosition;
        _layerZoom = frame.CameraZoom * frame.UiScale;
        _layerClear = frame.ClearColor;
        _layerBackground = frame.BackgroundVersion;
        _layerImages = frame.ImagesVersion;
        foreach (var path in _paths) path.InLayer = true;
    }

    /// <summary>
    /// Brings the layer up to date without moving it: erased strokes are cleared by redrawing only the area they covered,
    /// new strokes at the end are simply drawn on top.
    /// </summary>
    private void UpdateLayerInPlace(in FrameData frame, SKMatrix matrix, SKRect removed)
    {
        // A new path below one already in the layer would end up on top of it, so its area is redrawn too.
        var dirty = removed;
        int lastInLayer = _paths.FindLastIndex(path => path.InLayer);
        for (int i = 0; i < lastInLayer; i++)
            if (!_paths[i].InLayer) dirty = Union(dirty, _paths[i].Bounds);

        if (dirty.IsEmpty && lastInLayer == _paths.Count - 1) return;

        using var canvas = new SKCanvas(_layer);
        if (dirty.IsEmpty)
        {
            canvas.SetMatrix(matrix);
            foreach (var path in _paths.Where(path => !path.InLayer)) DrawPath(canvas, path);
        }
        else
        {
            // The redrawn area takes in every new path: drawn on top as well, a path would be drawn twice where it
            // crosses the area, its anti-aliased edges brighter there.
            foreach (var path in _paths.Where(path => !path.InLayer)) dirty = Union(dirty, path.Bounds);
            var region = matrix.MapRect(dirty);
            region = SKRect.Intersect(new SKRect(MathF.Floor(region.Left), MathF.Floor(region.Top), MathF.Ceiling(region.Right),
                MathF.Ceiling(region.Bottom)), new SKRect(0, 0, _layer!.Width, _layer.Height));
            if (!region.IsEmpty) RedrawRegion(canvas, frame, matrix, region);
        }

        foreach (var path in _paths) path.InLayer = true;
    }

    private void RedrawRegion(SKCanvas canvas, in FrameData frame, SKMatrix matrix, SKRect region)
    {
        canvas.Save();
        canvas.ClipRect(region);
        canvas.Clear(ToColor(frame.ClearColor));
        canvas.SetMatrix(SKMatrix.CreateScale(frame.UiScale, frame.UiScale));
        _canvasClip = Prim.NoClip;
        foreach (var prim in frame.Background) DrawPrim(canvas, prim);
        SetClip(canvas, Prim.NoClip);
        canvas.SetMatrix(matrix);
        DrawPictures(canvas, frame.Images);
        DrawPaths(canvas, matrix, [region], 0);
        canvas.Restore();
    }

    private static SKRect Union(SKRect a, SKRect b) => a.IsEmpty ? b : SKRect.Union(a, b);

    /// <summary>
    /// A run of connected capsules with the same color and width, primitives [Start, End), kept as one polyline in world
    /// space. <see cref="InLayer"/> tells whether the cached layer already shows it.
    /// </summary>
    private sealed class StrokePath(SKPath path, SKRect bounds, SKColor color, float width) : IDisposable
    {
        public SKPath Path { get; } = path;
        public SKRect Bounds { get; } = bounds;
        public SKColor Color { get; } = color;
        public float Width { get; } = width;
        /// <summary>The points of a dashed run, cut into dashes as it is drawn, and its pattern (the first capsule's Extra).</summary>
        public Vector2[]? Dashed;
        public Vector4 Pattern;
        public int Start, End;
        public bool InLayer;
        /// <summary>First and last primitive, color and width, dash pattern and length: what identifies the same run elsewhere.</summary>
        public (Vector4, Vector4, Vector4, Vector4, int) Key;

        public void Dispose() => Path.Dispose();
    }

    private readonly List<StrokePath> _paths = new();
    private int _pathsBuilt;

    // Paths dropped because primitives before them changed, by their first and last primitive. When the same run comes
    // back at another index (strokes after an erased one shift down), the path and its place in the layer are reused.
    private readonly Dictionary<(Vector4, Vector4, Vector4, Vector4, int), StrokePath> _dropped = new();

    /// <summary>Takes the paths that include primitives from <paramref name="prim"/> on out of the list, for reuse.</summary>
    private void DropPathsFrom(int prim)
    {
        while (_paths.Count > 0 && _paths[^1].End > prim)
        {
            var path = _paths[^1];
            _paths.RemoveAt(_paths.Count - 1);
            if (!_dropped.TryAdd(path.Key, path))
            {
                Erase(path);
                path.Dispose();
            }
        }

        _pathsBuilt = _paths.Count > 0 ? _paths[^1].End : 0;
    }

    private SKRect _erased;

    private void Erase(StrokePath path)
    {
        if (path.InLayer) _erased = Union(_erased, path.Bounds);
    }

    /// <summary>Frees the dropped paths that did not come back and returns the area they covered in the layer.</summary>
    private SKRect DisposeDroppedPaths()
    {
        foreach (var path in _dropped.Values)
        {
            Erase(path);
            path.Dispose();
        }

        _dropped.Clear();
        var erased = _erased;
        _erased = SKRect.Empty;
        return erased;
    }

    private void ClearPaths()
    {
        foreach (var path in _paths) path.Dispose();
        _paths.Clear();
        _pathsBuilt = 0;
        _layerValid = false;
    }

    /// <summary>Turns stroke primitives [_pathsBuilt, to) into cached polylines, which are much cheaper to draw.</summary>
    private void BuildPaths(ReadOnlySpan<Prim> prims, int to)
    {
        int i = _pathsBuilt;
        while (i < to)
        {
            int j = RunEnd(prims, i, to);
            var first = prims[i];
            var key = (first.A, prims[j - 1].A, new Vector4(first.Color.X, first.Color.Y, first.Color.Z, first.B.X), first.Extra, j - i);
            if (_dropped.Remove(key, out var reused))
            {
                reused.Start = i;
                reused.End = j;
                _paths.Add(reused);
                i = j;
                continue;
            }

            var path = MakePath(prims, i, j);
            path.Start = i;
            path.End = j;
            path.Key = key;
            _paths.Add(path);
            i = j;
        }

        _pathsBuilt = Math.Max(_pathsBuilt, to);
    }

    /// <summary>The end of the run of connected capsules from <paramref name="i"/> with the same color, width and dash pattern.</summary>
    private static int RunEnd(ReadOnlySpan<Prim> prims, int i, int to)
    {
        var first = prims[i];
        var end = new Vector2(first.A.Z, first.A.W);
        int j = i + 1;
        while (j < to)
        {
            var next = prims[j];
            if (next.B.X != first.B.X || next.Color != first.Color || next.Extra.X != first.Extra.X || next.Extra.Y != first.Extra.Y ||
                next.A.X != end.X || next.A.Y != end.Y) break;
            end = new Vector2(next.A.Z, next.A.W);
            j++;
        }

        return j;
    }

    /// <summary>Capsules [i, j) as one polyline; a dashed one starts its pattern at the first capsule's phase.</summary>
    private static StrokePath MakePath(ReadOnlySpan<Prim> prims, int i, int j)
    {
        var first = prims[i];
        float radius = first.B.X;
        var path = new SKPath();
        path.MoveTo(first.A.X, first.A.Y);
        for (int k = i; k < j; k++) path.LineTo(prims[k].A.Z, prims[k].A.W);
        var bounds = path.Bounds;
        bounds.Inflate(radius, radius);
        var made = new StrokePath(path, bounds, ToColor(first.Color), radius * 2f);
        if (first.IsDashed)
        {
            made.Dashed = new Vector2[j - i + 1];
            made.Dashed[0] = new Vector2(first.A.X, first.A.Y);
            for (int k = i; k < j; k++) made.Dashed[k - i + 1] = new Vector2(prims[k].A.Z, prims[k].A.W);
            made.Pattern = first.Extra;
        }

        return made;
    }

    private void DrawPath(SKCanvas canvas, StrokePath p)
    {
        var color = p.Color;
        float radius = Thin(canvas, ref color, p.Width * 0.5f);
        _stroke.StrokeWidth = radius * 2f;
        _stroke.Color = color;
        _stroke.Shader = null;
        if (p.Dashed != null) DrawDashes(canvas, p.Dashed, p.Pattern, radius);
        else canvas.DrawPath(p.Path, _stroke);
    }

    private readonly List<(Vector2 A, Vector2 B)> _dashes = new();

    /// <summary>
    /// Draws a dashed polyline with the stroke paint: the dashes in view, or a fainter solid line where the gaps would be
    /// too fine to see, as the GPU backends blur them.
    /// </summary>
    private void DrawDashes(SKCanvas canvas, ReadOnlySpan<Vector2> points, Vector4 pattern, float radius)
    {
        using var path = new SKPath();
        if (pattern.Y * canvas.TotalMatrix.ScaleX < 4f)
        {
            var color = _stroke.Color;
            _stroke.Color = color.WithAlpha((byte)(color.Alpha * MathF.Min((pattern.X + radius * 2f) / pattern.Y, 1f)));
            path.MoveTo(points[0].X, points[0].Y);
            foreach (var p in points[1..]) path.LineTo(p.X, p.Y);
        }
        else
        {
            var clip = canvas.LocalClipBounds;
            clip.Inflate(radius, radius);
            _dashes.Clear();
            Dashing.Visible(points, pattern, new Vector2(clip.Left, clip.Top), new Vector2(clip.Right, clip.Bottom), _dashes);
            foreach (var (a, b) in _dashes)
            {
                path.MoveTo(a.X, a.Y);
                path.LineTo(b.X, b.Y);
            }
        }

        canvas.DrawPath(path, _stroke);
    }

    /// <summary>
    /// The radius to draw a line or dot with: one thinner than a pixel is drawn a pixel thick, from half bright for a
    /// hairline to full from half a pixel up. That is how the GPU backends show it: their anti-aliasing widens a thin line
    /// by a pixel, and the short pieces of handwriting overlap there; Skia alone would draw it about half as bright.
    /// </summary>
    private static float Thin(SKCanvas canvas, ref SKColor color, float radius)
    {
        float scale = canvas.TotalMatrix.ScaleX;
        float r = radius * scale;
        if (r >= 0.5f || scale <= 0f) return radius;
        color = color.WithAlpha((byte)(color.Alpha * MathF.Min(0.5f + r * 2f, 1f) + 0.5f));
        return 0.5f / scale;
    }

    /// <summary>Draws cached paths from index <paramref name="from"/> that touch one of the device-space regions.</summary>
    private void DrawPaths(SKCanvas canvas, SKMatrix matrix, SKRect[] regions, int from)
    {
        matrix.TryInvert(out var inverse);
        var world = regions.Select(r => inverse.MapRect(r)).ToArray();
        for (int i = from; i < _paths.Count; i++)
        {
            var p = _paths[i];
            if (world.Any(r => r.IntersectsWith(p.Bounds))) DrawPath(canvas, p);
        }
    }

    // The dragged selection as polylines, the layer they were last drawn into and the matrix they were drawn with.
    private readonly List<StrokePath> _movingPaths = new();
    private SKRect _movingBounds;
    private int _movingVersion = -1;
    private bool _movingValid;
    private SKMatrix _movingDrawn;

    /// <summary>
    /// Draws the dragged selection. Its layer is drawn again only when the selection changed, when a part of it that the
    /// layer does not hold comes into view, or when the move is more than a shift (scaling or rotating keeps the lines'
    /// width, which a stretched picture would not); otherwise the layer is shifted.
    /// </summary>
    private void DrawMoving(SKCanvas canvas, in FrameData frame)
    {
        if (frame.MovingVersion != _movingVersion)
        {
            foreach (var path in _movingPaths) path.Dispose();
            _movingPaths.Clear();
            _movingBounds = SKRect.Empty;
            var prims = frame.Moving;
            for (int i = 0; i < prims.Length;)
            {
                int j = RunEnd(prims, i, prims.Length);
                var path = MakePath(prims, i, j);
                _movingPaths.Add(path);
                _movingBounds = Union(_movingBounds, path.Bounds);
                i = j;
            }

            _movingVersion = frame.MovingVersion;
            _movingValid = false;
        }

        var m = frame.MovingTransform;
        var matrix = SKMatrix.Concat(CameraMatrix(frame.CameraPosition, frame.CameraZoom, frame.UiScale),
            new SKMatrix(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1));
        var screen = new SKRect(0, 0, _movingLayer!.Width, _movingLayer.Height);
        var shift = new SKPoint(matrix.TransX - _movingDrawn.TransX, matrix.TransY - _movingDrawn.TransY);
        bool onlyShifted = matrix.ScaleX == _movingDrawn.ScaleX && matrix.ScaleY == _movingDrawn.ScaleY &&
                           matrix.SkewX == _movingDrawn.SkewX && matrix.SkewY == _movingDrawn.SkewY;
        var visible = SKRect.Intersect(matrix.MapRect(_movingBounds), screen);
        var held = screen;
        held.Offset(shift);
        if (!_movingValid || !onlyShifted || !held.Contains(visible))
        {
            using var layer = new SKCanvas(_movingLayer);
            layer.Clear(SKColors.Transparent);
            layer.SetMatrix(matrix);
            foreach (var path in _movingPaths) DrawPath(layer, path);
            _movingDrawn = matrix;
            _movingValid = true;
            shift = SKPoint.Empty;
        }

        canvas.ResetMatrix();
        using var image = SKImage.FromPixels(_movingLayer.PeekPixels());
        canvas.DrawImage(image, shift.X, shift.Y, new SKSamplingOptions(SKFilterMode.Linear), _image);
    }

    /// <summary>Draws the stroke that is still being drawn; it changes every frame, so it is not cached.</summary>
    private void DrawStrokes(SKCanvas canvas, ReadOnlySpan<Prim> prims, int from, int to)
    {
        for (int i = from; i < to; i++) DrawPrim(canvas, prims[i]);
    }

    private void DrawPrim(SKCanvas canvas, in Prim p)
    {
        SetClip(canvas, p.Clip);
        var color = ToColor(p.Color);
        var rect = new SKRect(p.A.X, p.A.Y, p.A.Z, p.A.W);

        switch ((PrimType)(int)p.B.W)
        {
            case PrimType.RoundRect:
            {
                float radius = MathF.Min(p.B.X, MathF.Min(rect.Width, rect.Height) * 0.5f);
                if (p.B.Y > 0)
                {
                    float t = p.B.Y;
                    rect.Inflate(-t * 0.5f, -t * 0.5f);
                    radius = MathF.Max(radius - t * 0.5f, 0f);
                    _stroke.Color = color;
                    _stroke.StrokeWidth = t;
                    _stroke.Shader = null;
                    canvas.DrawRoundRect(rect, radius, radius, _stroke);
                }
                else
                {
                    _fill.Color = color;
                    _fill.Shader = null;
                    canvas.DrawRoundRect(rect, radius, radius, _fill);
                }

                break;
            }
            case PrimType.Capsule:
            {
                float radius = Thin(canvas, ref color, p.B.X);
                _stroke.Color = color;
                _stroke.StrokeWidth = radius * 2f;
                _stroke.Shader = null;
                if (p.A.X == p.A.Z && p.A.Y == p.A.W)
                {
                    _fill.Color = color;
                    _fill.Shader = null;
                    canvas.DrawCircle(p.A.X, p.A.Y, radius, _fill);
                }
                else if (p.IsDashed)
                {
                    DrawDashes(canvas, [new Vector2(p.A.X, p.A.Y), new Vector2(p.A.Z, p.A.W)], p.Extra, radius);
                }
                else canvas.DrawLine(p.A.X, p.A.Y, p.A.Z, p.A.W, _stroke);
                break;
            }
            case PrimType.Glyph when _atlasImage != null:
            {
                float s = _atlasImage.Width;
                var src = new SKRect(p.Extra.X * s, p.Extra.Y * s, p.Extra.Z * s, p.Extra.W * s);
                _glyph.Color = color;
                canvas.DrawImage(_atlasImage, src, rect, SKSamplingOptions.Default, _glyph);
                break;
            }
            case PrimType.Arc:
            {
                var center = new SKPoint(p.A.X, p.A.Y);
                float r0 = p.A.Z, r1 = p.A.W;
                float start = p.B.X * 180f / MathF.PI, sweep = (p.B.Y - p.B.X) * 180f / MathF.PI;
                using var path = new SKPath();
                path.ArcTo(new SKRect(center.X - r1, center.Y - r1, center.X + r1, center.Y + r1), start, sweep, true);
                path.ArcTo(new SKRect(center.X - r0, center.Y - r0, center.X + r0, center.Y + r0), start + sweep, -sweep, false);
                path.Close();
                _fill.Color = color;
                _fill.Shader = null;
                canvas.DrawPath(path, _fill);
                break;
            }
            case PrimType.ColorRing:
            {
                var center = new SKPoint(p.A.X, p.A.Y);
                float r0 = p.A.Z, r1 = p.A.W;
                // Value grows clockwise from 12 o'clock, so the sweep gradient is rotated by a quarter turn.
                using var shader = SKShader.CreateSweepGradient(center, GradientColors((int)p.B.X, p.B.Y), null,
                    SKMatrix.CreateRotationDegrees(-90f, center.X, center.Y));
                _stroke.Shader = shader;
                _stroke.StrokeWidth = r1 - r0;
                canvas.DrawCircle(center, (r0 + r1) * 0.5f, _stroke);
                _stroke.Shader = null;
                break;
            }
            case PrimType.ColorBar:
            {
                using var shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Bottom),
                    new SKPoint(rect.Left, rect.Top), GradientColors((int)p.B.X, p.B.Y), SKShaderTileMode.Clamp);
                float radius = MathF.Min(p.B.Z, MathF.Min(rect.Width, rect.Height) * 0.5f);
                _fill.Shader = shader;
                canvas.DrawRoundRect(rect, radius, radius, _fill);
                _fill.Shader = null;
                break;
            }
        }
    }

    private static SKColor[] GradientColors(int mode, float hue)
    {
        var full = Hsv(hue, 1f, 1f);
        return mode switch
        {
            0 => Enumerable.Range(0, 7).Select(i => Hsv(i / 6f, 1f, 1f)).ToArray(),
            1 => [SKColors.White, full],
            _ => [SKColors.Black, full]
        };
    }

    private static SKColor Hsv(float h, float s, float v) => SKColor.FromHsv(h * 360f % 360f, s * 100f, v * 100f);

    private void SetClip(SKCanvas canvas, Vector4 clip)
    {
        if (clip == _canvasClip) return;
        if (_canvasClip != Prim.NoClip) canvas.Restore();
        if (clip != Prim.NoClip)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(clip.X, clip.Y, clip.Z, clip.W));
        }

        _canvasClip = clip;
    }

    private unsafe void UpdateAtlas(in FrameData frame)
    {
        int size = frame.AtlasSize;
        int top = frame.AtlasDirtyTop, bottom = frame.AtlasDirtyBottom;
        if (_atlas == null || _atlas.Width != size)
        {
            _atlas?.Dispose();
            _atlas = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Premul));
            top = 0;
            bottom = size;
        }

        if (bottom <= top) return;
        frame.AtlasPixels.Slice(top * size, (bottom - top) * size)
            .CopyTo(new Span<byte>((byte*)_atlas.GetPixels() + top * size, (bottom - top) * size));
        _atlas.NotifyPixelsChanged();
        _atlasImage?.Dispose();
        _atlasImage = SKImage.FromPixels(_atlas.PeekPixels());
    }

    private static SKColor ToColor(Vector4 c) => new(
        (byte)Math.Clamp(c.X * 255f + 0.5f, 0, 255), (byte)Math.Clamp(c.Y * 255f + 0.5f, 0, 255),
        (byte)Math.Clamp(c.Z * 255f + 0.5f, 0, 255), (byte)Math.Clamp(c.W * 255f + 0.5f, 0, 255));

    private unsafe void Present()
    {
        var frame = _frame!;
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = frame.Width,
            Height = -frame.Height, // top-down rows
            Planes = 1,
            BitCount = 32
        };
        nint dc = GetDC(_hwnd);
        SetDIBitsToDevice(dc, 0, 0, (uint)frame.Width, (uint)frame.Height, 0, 0, 0, (uint)frame.Height,
            frame.GetPixels(), ref header, 0);
        ReleaseDC(_hwnd, dc);
        // Waits for the screen's next refresh, as vsync does on the other backends, so animations do not draw hundreds of
        // frames per second that are never shown.
        DwmFlush();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();

    [LibraryImport("gdi32.dll")]
    private static partial int SetDIBitsToDevice(nint dc, int x, int y, uint w, uint h, int srcX, int srcY,
        uint startScan, uint scanLines, nint bits, ref BitmapInfoHeader info, uint usage);

    private void DisposeSurfaces()
    {
        _frameCanvas?.Dispose();
        _frame?.Dispose();
        _layer?.Dispose();
        _spareLayer?.Dispose();
        _movingLayer?.Dispose();
    }

    public void Dispose()
    {
        ClearPaths();
        foreach (var path in _movingPaths) path.Dispose();
        DisposeSurfaces();
        _atlasImage?.Dispose();
        _atlas?.Dispose();
        foreach (var picture in _pictures.Values) picture.Dispose();
        _fill.Dispose();
        _stroke.Dispose();
        _glyph.Dispose();
        _image.Dispose();
    }
}
