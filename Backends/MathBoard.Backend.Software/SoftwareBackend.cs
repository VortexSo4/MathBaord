using System.Numerics;
using System.Runtime.InteropServices;
using MathBoard.Graphics;
using Silk.NET.Windowing;
using SkiaSharp;

namespace MathBoard.Backend.Software;

/// <summary>
/// CPU rendering with Skia, presented through GDI. Works without any GPU driver. Finished strokes are cached in a
/// layer for the current camera; panning shifts the layer and only redraws the uncovered strips.
/// </summary>
public sealed partial class SoftwareBackend : IRenderBackend
{
    private IWindow? _window;
    private nint _hwnd;

    private SKBitmap? _frame;
    private SKCanvas? _frameCanvas;
    private SKBitmap? _layer, _spareLayer;
    private SKBitmap? _atlas;
    // Drawing an SKBitmap copies it into a temporary image on every call, which for the 1 MB atlas made each
    // glyph cost a full copy. Images that wrap the pixels are drawn without copying.
    private SKImage? _atlasImage;

    // State the stroke layer was rendered with.
    private bool _layerValid;
    private Vector2 _layerCamera;
    private float _layerZoom;
    private Vector4 _layerClear;
    private int _layerBackground;
    private int _baked;

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _glyph = new();
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
            _layerValid = false;
        }

        UpdateAtlas(frame);
        UpdateLayer(frame);

        var canvas = _frameCanvas!;
        canvas.ResetMatrix();
        DrawWrapped(canvas, _layer!, 0, 0);

        float scale = frame.UiScale;
        canvas.SetMatrix(CameraMatrix(frame.CameraPosition, frame.CameraZoom, scale));
        DrawStrokes(canvas, frame.Strokes, frame.StrokesStable, frame.Strokes.Length);

        canvas.SetMatrix(SKMatrix.CreateScale(scale, scale));
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

    private static SKMatrix CameraMatrix(Vector2 camera, float zoom, float scale) =>
        new(zoom * scale, 0, camera.X * scale, 0, zoom * scale, camera.Y * scale, 0, 0, 1);

    private void UpdateLayer(in FrameData frame)
    {
        int stable = Math.Min(frame.StrokesStable, frame.Strokes.Length);
        if (frame.StrokesDirtyFrom < _pathsBuilt) ClearPaths();
        BuildPaths(frame.Strokes, stable);

        bool sameView = _layerValid && _layerZoom == frame.CameraZoom * frame.UiScale && _layerClear == frame.ClearColor &&
                        _layerBackground == frame.BackgroundVersion && frame.StrokesDirtyFrom >= _baked;
        var shift = (frame.CameraPosition - _layerCamera) * frame.UiScale;
        var whole = new Vector2(MathF.Round(shift.X), MathF.Round(shift.Y));
        var matrix = CameraMatrix(frame.CameraPosition, frame.CameraZoom, frame.UiScale);
        int w = _layer!.Width, h = _layer.Height;

        if (sameView && shift == Vector2.Zero)
        {
            if (stable > _baked)
            {
                using var canvas = new SKCanvas(_layer);
                canvas.SetMatrix(matrix);
                DrawPaths(canvas, matrix, [new SKRect(0, 0, w, h)], _bakedPaths);
                _baked = stable;
                _bakedPaths = _paths.Count;
            }

            return;
        }

        using (var canvas = new SKCanvas(_spareLayer))
        {
            canvas.Clear(ToColor(frame.ClearColor));
            var regions = new List<SKRect> { new(0, 0, w, h) };
            bool canShift = sameView && Vector2.DistanceSquared(shift, whole) < 1e-4f &&
                            MathF.Abs(whole.X) < w && MathF.Abs(whole.Y) < h;
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

            foreach (var region in regions)
            {
                canvas.Save();
                canvas.ClipRect(region);
                canvas.SetMatrix(SKMatrix.CreateScale(frame.UiScale, frame.UiScale));
                _canvasClip = Prim.NoClip;
                foreach (var prim in frame.Background) DrawPrim(canvas, prim);
                SetClip(canvas, Prim.NoClip);
                canvas.SetMatrix(matrix);
                DrawPaths(canvas, matrix, [region], 0);
                canvas.Restore();
            }
        }

        (_layer, _spareLayer) = (_spareLayer, _layer);
        _layerValid = true;
        _layerCamera = frame.CameraPosition;
        _layerZoom = frame.CameraZoom * frame.UiScale;
        _layerClear = frame.ClearColor;
        _layerBackground = frame.BackgroundVersion;
        _baked = stable;
        _bakedPaths = _paths.Count;
    }

    /// <summary>A run of connected capsules with the same color and width, kept as one polyline in world space.</summary>
    private sealed record StrokePath(SKPath Path, SKRect Bounds, SKColor Color, float Width) : IDisposable
    {
        public void Dispose() => Path.Dispose();
    }

    private readonly List<StrokePath> _paths = new();
    private int _pathsBuilt;
    private int _bakedPaths;

    private void ClearPaths()
    {
        foreach (var path in _paths) path.Dispose();
        _paths.Clear();
        _pathsBuilt = 0;
        _bakedPaths = 0;
        _layerValid = false;
    }

    /// <summary>Turns stroke primitives [_pathsBuilt, to) into cached polylines, which are much cheaper to draw.</summary>
    private void BuildPaths(ReadOnlySpan<Prim> prims, int to)
    {
        int i = _pathsBuilt;
        while (i < to)
        {
            var first = prims[i];
            float radius = first.B.X;
            var end = new Vector2(first.A.Z, first.A.W);
            var path = new SKPath();
            path.MoveTo(first.A.X, first.A.Y);
            path.LineTo(end.X, end.Y);
            int j = i + 1;
            while (j < to)
            {
                var next = prims[j];
                if (next.B.X != radius || next.Color != first.Color || next.A.X != end.X || next.A.Y != end.Y) break;
                end = new Vector2(next.A.Z, next.A.W);
                path.LineTo(end.X, end.Y);
                j++;
            }

            var bounds = path.Bounds;
            bounds.Inflate(radius, radius);
            _paths.Add(new StrokePath(path, bounds, ToColor(first.Color), radius * 2f));
            i = j;
        }

        _pathsBuilt = Math.Max(_pathsBuilt, to);
    }

    /// <summary>Draws cached paths from index <paramref name="from"/> that touch one of the device-space regions.</summary>
    private void DrawPaths(SKCanvas canvas, SKMatrix matrix, SKRect[] regions, int from)
    {
        matrix.TryInvert(out var inverse);
        var world = regions.Select(r => inverse.MapRect(r)).ToArray();
        for (int i = from; i < _paths.Count; i++)
        {
            var p = _paths[i];
            if (!world.Any(r => r.IntersectsWith(p.Bounds))) continue;
            _stroke.Color = p.Color;
            _stroke.StrokeWidth = p.Width;
            _stroke.Shader = null;
            canvas.DrawPath(p.Path, _stroke);
        }
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
                _stroke.Color = color;
                _stroke.StrokeWidth = p.B.X * 2f;
                _stroke.Shader = null;
                if (p.A.X == p.A.Z && p.A.Y == p.A.W)
                {
                    _fill.Color = color;
                    _fill.Shader = null;
                    canvas.DrawCircle(p.A.X, p.A.Y, p.B.X, _fill);
                }
                else canvas.DrawLine(p.A.X, p.A.Y, p.A.Z, p.A.W, _stroke);
                break;
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

    [LibraryImport("gdi32.dll")]
    private static partial int SetDIBitsToDevice(nint dc, int x, int y, uint w, uint h, int srcX, int srcY,
        uint startScan, uint scanLines, nint bits, ref BitmapInfoHeader info, uint usage);

    private void DisposeSurfaces()
    {
        _frameCanvas?.Dispose();
        _frame?.Dispose();
        _layer?.Dispose();
        _spareLayer?.Dispose();
    }

    public void Dispose()
    {
        ClearPaths();
        DisposeSurfaces();
        _atlasImage?.Dispose();
        _atlas?.Dispose();
        _fill.Dispose();
        _stroke.Dispose();
        _glyph.Dispose();
    }
}
