using System.Numerics;

namespace MathBoard.Rendering;

/// <summary>Collects screen-space primitives for one UI frame, in draw order.</summary>
public sealed class UiBatch(TextAtlas atlas)
{
    public enum Align { Left, Center, Right }

    public readonly List<Prim> Prims = [];
    private readonly Stack<Vector4> _clipStack = new();
    private Vector4 _clip = Prim.NoClip;

    public TextAtlas Atlas => atlas;

    public void Clear()
    {
        Prims.Clear();
        _clipStack.Clear();
        _clip = Prim.NoClip;
    }

    public void PushClip(Vector2 pos, Vector2 size)
    {
        _clipStack.Push(_clip);
        _clip = new Vector4(
            MathF.Max(_clip.X, pos.X), MathF.Max(_clip.Y, pos.Y),
            MathF.Min(_clip.Z, pos.X + size.X), MathF.Min(_clip.W, pos.Y + size.Y));
    }

    public void PopClip() => _clip = _clipStack.Pop();

    public void Rect(Vector2 pos, Vector2 size, Vector4 color, float radius = 0f) =>
        Add(new Vector4(pos, pos.X + size.X, pos.Y + size.Y), new Vector4(radius, 0, 0, (float)PrimType.RoundRect), color);

    public void Border(Vector2 pos, Vector2 size, Vector4 color, float thickness, float radius = 0f) =>
        Add(new Vector4(pos, pos.X + size.X, pos.Y + size.Y), new Vector4(radius, thickness, 0, (float)PrimType.RoundRect), color);

    public void Circle(Vector2 center, float radius, Vector4 color) =>
        Rect(center - new Vector2(radius), new Vector2(radius * 2f), color, radius);

    public void Ring(Vector2 center, float radius, float thickness, Vector4 color) =>
        Border(center - new Vector2(radius), new Vector2(radius * 2f), color, thickness, radius);

    public void Line(Vector2 a, Vector2 b, Vector4 color, float thickness) =>
        Add(new Vector4(a, b.X, b.Y), new Vector4(thickness * 0.5f, 0, 0, (float)PrimType.Capsule), color);

    public void Arc(Vector2 center, float innerRadius, float outerRadius, float startAngle, float endAngle, Vector4 color) =>
        Add(new Vector4(center, innerRadius, outerRadius), new Vector4(startAngle, endAngle, 0, (float)PrimType.Arc), color);

    /// <param name="mode">0 = hue, 1 = saturation, 2 = value; the value grows clockwise from the top.</param>
    public void ColorRing(Vector2 center, float innerRadius, float outerRadius, int mode, float hue) =>
        Add(new Vector4(center, innerRadius, outerRadius), new Vector4(mode, hue, 0, (float)PrimType.ColorRing), Vector4.One);

    private void Add(Vector4 a, Vector4 b, Vector4 color) =>
        Prims.Add(new Prim { A = a, B = b, Color = color, Clip = _clip });

    public float Measure(string text, TextStyle style) => atlas.MeasureWidth(text, style);

    /// <summary>Draws text whose line box starts at <paramref name="topLeft"/>. Returns the advance width.</summary>
    public float Text(string text, Vector2 topLeft, Vector4 color, TextStyle style)
    {
        var metrics = atlas.GetMetrics(style);
        DrawEntry(text, new Vector2(MathF.Round(topLeft.X), MathF.Round(topLeft.Y + metrics.Ascent)), color, style);
        return Measure(text, style);
    }

    /// <summary>Draws a single line vertically centered in the rect, shortened with an ellipsis if it does not fit.</summary>
    public void TextInRect(string text, Vector2 pos, Vector2 size, Vector4 color, TextStyle style, Align align = Align.Left)
    {
        text = Fit(text, size.X, style);
        float width = Measure(text, style);
        float x = align switch
        {
            Align.Center => pos.X + (size.X - width) * 0.5f,
            Align.Right => pos.X + size.X - width,
            _ => pos.X
        };
        var metrics = atlas.GetMetrics(style);
        float baseline = pos.Y + (size.Y + metrics.CapHeight) * 0.5f;
        DrawEntry(text, new Vector2(MathF.Round(x), MathF.Round(baseline)), color, style);
    }

    public void Icon(string glyph, Vector2 center, float size, Vector4 color)
    {
        var style = new TextStyle(FontFace.Icons, size);
        if (!atlas.TryGet(glyph, style, out var e)) return;
        AddGlyph(new Vector2(MathF.Round(center.X - e.Size.X * 0.5f), MathF.Round(center.Y - e.Size.Y * 0.5f)), e, color);
    }

    public string Fit(string text, float maxWidth, TextStyle style)
    {
        if (Measure(text, style) <= maxWidth) return text;

        const string ellipsis = "…";
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Measure(string.Concat(text.AsSpan(0, mid), ellipsis), style) <= maxWidth) lo = mid;
            else hi = mid - 1;
        }

        return string.Concat(text.AsSpan(0, lo).TrimEnd(), ellipsis);
    }

    private void DrawEntry(string text, Vector2 baselineOrigin, Vector4 color, TextStyle style)
    {
        if (string.IsNullOrEmpty(text) || !atlas.TryGet(text, style, out var e)) return;
        AddGlyph(baselineOrigin + e.Offset, e, color);
    }

    private void AddGlyph(Vector2 pos, TextAtlas.Entry e, Vector4 color) =>
        Prims.Add(new Prim
        {
            A = new Vector4(pos, pos.X + e.Size.X, pos.Y + e.Size.Y),
            B = new Vector4(0, 0, 0, (float)PrimType.Glyph),
            Color = color,
            Extra = e.UV,
            Clip = _clip
        });

    public enum ButtonKind { Primary, Secondary, Danger, Ghost }

    public void ButtonBackground(Vector2 pos, Vector2 size, bool hovered, ButtonKind kind, float radius = Theme.ButtonRadius)
    {
        var color = kind switch
        {
            ButtonKind.Primary => hovered ? Theme.AccentHover : Theme.Accent,
            ButtonKind.Danger => hovered ? Theme.DangerHover : Theme.Danger,
            ButtonKind.Ghost => hovered ? Theme.Surface : Vector4.Zero,
            _ => hovered ? Theme.SurfaceHover : Theme.Surface
        };
        if (color.W > 0) Rect(pos, size, color, radius);
    }

    public void IconButton(string icon, Vector2 pos, Vector2 size, bool hovered, ButtonKind kind = ButtonKind.Ghost,
        float iconSize = 16f, Vector4? iconColor = null)
    {
        ButtonBackground(pos, size, hovered, kind);
        var color = iconColor ?? (kind == ButtonKind.Ghost && !hovered ? Theme.TextSecondary : Theme.Text);
        Icon(icon, pos + size * 0.5f, iconSize, color);
    }

    public void TextButton(string label, Vector2 pos, Vector2 size, bool hovered, ButtonKind kind)
    {
        ButtonBackground(pos, size, hovered, kind);
        TextInRect(label, pos + new Vector2(8f, 0), size - new Vector2(16f, 0), Theme.Text, Theme.Label, Align.Center);
    }

    public void TextField(TextBoxState box, Vector2 pos, Vector2 size, bool focused, TextStyle style, Vector4 textColor)
    {
        Rect(pos, size, focused ? Theme.Field : Theme.WithAlpha(Theme.Field, 0.7f), 6f);
        Border(pos, size, focused ? Theme.Accent : Theme.Border, focused ? 1.5f : 1f, 6f);

        const float pad = 10f;
        string text = box.ToString();
        float innerWidth = size.X - pad * 2;
        float caretX = Measure(text[..box.CursorPos], style);
        float scroll = MathF.Max(0f, caretX - innerWidth + 2f);
        float x = pos.X + pad - scroll;

        PushClip(pos + new Vector2(pad - 1f, 0), new Vector2(innerWidth + 2f, size.Y));
        if (focused && box.HasSelection)
        {
            float selX = Measure(text[..box.SelStart], style);
            float selW = Measure(text[box.SelStart..box.SelEnd], style);
            Rect(new Vector2(x + selX, pos.Y + 6f), new Vector2(selW, size.Y - 12f), Theme.WithAlpha(Theme.Accent, 0.45f), 2f);
        }

        TextInRect(text, new Vector2(x, pos.Y), new Vector2(float.MaxValue, size.Y), textColor, style);

        bool caretVisible = (DateTime.Now - box.LastEditTime).TotalMilliseconds % 1000 < 500;
        if (focused && caretVisible)
        {
            float h = MathF.Min(size.Y - 12f, atlas.GetMetrics(style).LineHeight);
            Rect(new Vector2(MathF.Round(x + caretX), pos.Y + (size.Y - h) * 0.5f), new Vector2(1.5f, h), Theme.Text);
        }

        PopClip();
    }

    public static bool Contains(Vector2 pos, Vector2 size, Vector2 point) =>
        point.X >= pos.X && point.X < pos.X + size.X && point.Y >= pos.Y && point.Y < pos.Y + size.Y;

    public Vector2 TooltipSize(string text) => new(Measure(text, Theme.Small) + 16f, 28f);

    public void TooltipBox(string text, Vector2 pos)
    {
        var size = TooltipSize(text);
        Rect(pos, size, Theme.WithAlpha(Theme.Field, 0.96f), 6f);
        Border(pos, size, Theme.Border, 1f, 6f);
        TextInRect(text, pos, size, Theme.Text, Theme.Small, Align.Center);
    }

    /// <summary>Shows a tooltip above and to the right of the pointer, kept inside the screen.</summary>
    public void Tooltip(string text, Vector2 anchor, Vector2 screenSize)
    {
        var size = TooltipSize(text);
        var pos = new Vector2(anchor.X + 14f, anchor.Y - size.Y - 10f);
        pos.X = Math.Clamp(pos.X, 4f, MathF.Max(4f, screenSize.X - size.X - 4f));
        if (pos.Y < 4f) pos.Y = anchor.Y + 22f;
        TooltipBox(text, pos);
    }
}
