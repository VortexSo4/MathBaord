using System.Numerics;
using MathBoard.Graphics;
using SkiaSharp;

namespace MathBoard.Rendering;

/// <summary>
/// Lazily rasterizes whole strings into a single-channel coverage atlas at the current UI scale. Backends
/// upload the rows reported as dirty. When the atlas fills up it is cleared on the next frame and refilled
/// with whatever is drawn then.
/// </summary>
public sealed unsafe class TextAtlas : IDisposable
{
    public const int Size = 1024;
    private const int Spacing = 1;

    public readonly struct Entry(Vector2 offset, Vector2 size, Vector4 uv)
    {
        /// <summary>Bitmap top-left relative to the pen position on the baseline, in framebuffer pixels.</summary>
        public readonly Vector2 Offset = offset;
        public readonly Vector2 Size = size;
        public readonly Vector4 UV = uv;
    }

    /// <summary>Font metrics in logical pixels.</summary>
    public readonly record struct Metrics(float Ascent, float Descent, float CapHeight)
    {
        public float LineHeight => Ascent + Descent;
    }

    private readonly Dictionary<(string, TextStyle), Entry> _entries = new();
    private readonly Dictionary<(string, TextStyle), float> _widths = new();
    private readonly Dictionary<TextStyle, SKFont> _fonts = new();
    private readonly SKTypeface[] _typefaces = new SKTypeface[3];

    private readonly SKBitmap _bitmap = new(Size, Size, SKColorType.Alpha8, SKAlphaType.Premul);
    private readonly SKCanvas _canvas;
    private readonly SKPaint _paint = new() { Color = SKColors.White, IsAntialias = true };

    private int _penX, _penY, _rowHeight;
    private bool _resetPending;
    private float _scale = 1f;

    public int DirtyTop { get; private set; }
    public int DirtyBottom { get; private set; } = Size;
    public ReadOnlySpan<byte> Pixels => new((byte*)_bitmap.GetPixels(), Size * Size);

    /// <summary>Set when a string did not fit this frame; the caller should draw the UI again next frame.</summary>
    public bool NeedsRedraw { get; private set; }

    public TextAtlas()
    {
        _canvas = new SKCanvas(_bitmap);
        _canvas.Clear(SKColors.Transparent);

        var semibold = new SKFontStyle(SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        _typefaces[(int)FontFace.Regular] = LoadTypeface(SKFontStyle.Normal, "Segoe UI", "Arial");
        _typefaces[(int)FontFace.Semibold] = LoadTypeface(semibold, "Segoe UI", "Arial");
        _typefaces[(int)FontFace.Icons] = LoadTypeface(SKFontStyle.Normal, "Segoe Fluent Icons", "Segoe MDL2 Assets");
    }

    private static SKTypeface LoadTypeface(SKFontStyle style, params string[] families)
    {
        foreach (var family in families)
        {
            var typeface = SKFontManager.Default.MatchFamily(family, style);
            if (typeface != null && typeface.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
                return typeface;
            typeface?.Dispose();
        }

        Console.WriteLine($"TextAtlas: none of [{string.Join(", ", families)}] found, using default font");
        return SKTypeface.Default;
    }

    private SKFont Font(TextStyle style)
    {
        if (!_fonts.TryGetValue(style, out var font))
        {
            font = new SKFont(_typefaces[(int)style.Face], style.Size * _scale)
            {
                Edging = SKFontEdging.Antialias,
                Subpixel = true,
                Hinting = SKFontHinting.Slight
            };
            _fonts[style] = font;
        }

        return font;
    }

    public Metrics GetMetrics(TextStyle style)
    {
        var m = Font(style).Metrics;
        float capHeight = m.CapHeight > 0 ? m.CapHeight : -m.Ascent * 0.7f;
        return new Metrics(-m.Ascent / _scale, m.Descent / _scale, capHeight / _scale);
    }

    public float MeasureWidth(string text, TextStyle style)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (_widths.TryGetValue((text, style), out float w)) return w;
        if (_widths.Count > 4096) _widths.Clear();
        w = Font(style).MeasureText(text) / _scale;
        _widths[(text, style)] = w;
        return w;
    }

    /// <summary>Call once before building a frame's UI.</summary>
    public void BeginFrame(float scale)
    {
        NeedsRedraw = false;
        if (scale != _scale)
        {
            _scale = scale;
            foreach (var font in _fonts.Values) font.Dispose();
            _fonts.Clear();
            _widths.Clear();
            _resetPending = true;
        }

        if (!_resetPending) return;
        _resetPending = false;
        _entries.Clear();
        _canvas.Clear(SKColors.Transparent);
        _penX = _penY = _rowHeight = 0;
        MarkDirty(0, Size);
    }

    public bool TryGet(string text, TextStyle style, out Entry entry)
    {
        if (_entries.TryGetValue((text, style), out entry)) return entry.Size.X > 0;

        var font = Font(style);
        font.MeasureText(text, out var bounds, _paint);
        if (bounds.IsEmpty)
        {
            _entries[(text, style)] = entry = default;
            return false;
        }

        int left = (int)MathF.Floor(bounds.Left) - 1;
        int top = (int)MathF.Floor(bounds.Top) - 1;
        int width = (int)MathF.Ceiling(bounds.Right) + 1 - left;
        int height = (int)MathF.Ceiling(bounds.Bottom) + 1 - top;

        // Bigger than the whole atlas: clearing would not help, so it is simply not drawn.
        if (width > Size || height > Size)
        {
            _entries[(text, style)] = entry = default;
            return false;
        }

        if (_penX + width > Size)
        {
            _penX = 0;
            _penY += _rowHeight + Spacing;
            _rowHeight = 0;
        }

        if (_penY + height > Size)
        {
            _resetPending = true;
            NeedsRedraw = true;
            entry = default;
            return false;
        }

        int x = _penX, y = _penY;
        _canvas.DrawText(text, x - left, y - top, font, _paint);
        _penX += width + Spacing;
        _rowHeight = Math.Max(_rowHeight, height);
        MarkDirty(y, y + height);

        entry = new Entry(new Vector2(left, top), new Vector2(width, height),
            new Vector4(x, y, x + width, y + height) / Size);
        _entries[(text, style)] = entry;
        return true;
    }

    private void MarkDirty(int top, int bottom)
    {
        DirtyTop = DirtyBottom > DirtyTop ? Math.Min(DirtyTop, top) : top;
        DirtyBottom = Math.Max(DirtyBottom, bottom);
    }

    /// <summary>Call after a backend has presented the rows reported so far.</summary>
    public void ClearDirty() => DirtyTop = DirtyBottom = 0;

    public void Dispose()
    {
        foreach (var font in _fonts.Values) font.Dispose();
        _paint.Dispose();
        _canvas.Dispose();
        _bitmap.Dispose();
    }
}
