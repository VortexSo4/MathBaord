using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>Grid of shape kinds opened from the "Shapes" quick button; picking one switches to the shape tool.</summary>
public sealed class ShapePicker(StrokeRenderer renderer) : IPopup
{
    private const float Cell = 56f;
    private const float Gap = 6f;
    private const int Columns = 4;
    private const float Padding = 12f;
    private const float TitleHeight = 34f;

    private static readonly ShapeKind[] Kinds = Enum.GetValues<ShapeKind>();
    private Vector2 _pos;
    private Vector2 _mouse;

    public bool IsOpen { get; private set; }

    private static Vector2 Size
    {
        get
        {
            int rows = (Kinds.Length + Columns - 1) / Columns;
            return new Vector2(Padding * 2 + Columns * Cell + (Columns - 1) * Gap,
                TitleHeight + Padding + rows * Cell + (rows - 1) * Gap);
        }
    }

    public void Open(Vector2 anchor)
    {
        var screen = renderer.ScreenSize;
        var pos = anchor - new Vector2(Size.X + 34f, Size.Y * 0.5f);
        if (pos.X < 8f) pos.X = anchor.X + 34f;
        _pos = Vector2.Clamp(pos, new Vector2(8f), Vector2.Max(screen - Size - new Vector2(8f), new Vector2(8f)));
        IsOpen = true;
        renderer.SetDirty();
    }

    public void Close()
    {
        IsOpen = false;
        renderer.SetDirty();
    }

    private Vector2 CellPos(int i) =>
        _pos + new Vector2(Padding + i % Columns * (Cell + Gap), TitleHeight + i / Columns * (Cell + Gap));

    public void OnMouseDown(Vector2 pos)
    {
        _mouse = pos;
        for (int i = 0; i < Kinds.Length; i++)
        {
            if (!UiBatch.Contains(CellPos(i), new Vector2(Cell), pos)) continue;
            renderer.CurrentShape = Kinds[i];
            renderer.SetTool(Tool.Shape);
            break;
        }

        Close();
    }

    public void OnMouseMove(Vector2 pos)
    {
        _mouse = pos;
        renderer.SetDirty();
    }

    public void OnMouseUp(Vector2 pos) { }

    public void Render(UiBatch ui, Vector2 screenSize)
    {
        if (!IsOpen) return;
        ui.Rect(_pos + new Vector2(0, 4f), Size, new Vector4(0, 0, 0, 0.35f), Theme.PopupRadius);
        ui.Rect(_pos, Size, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(_pos, Size, Theme.Border, 1f, Theme.PopupRadius);
        ui.TextInRect(Localization.Get("action_Shapes", "Shapes"), _pos + new Vector2(Padding, 4f),
            new Vector2(Size.X - Padding * 2, TitleHeight - 4f), Theme.Text, Theme.Title);

        ShapeKind? hovered = null;
        for (int i = 0; i < Kinds.Length; i++)
        {
            var pos = CellPos(i);
            var size = new Vector2(Cell);
            bool active = renderer.Tool == Tool.Shape && renderer.CurrentShape == Kinds[i];
            bool hover = UiBatch.Contains(pos, size, _mouse);
            if (hover) hovered = Kinds[i];
            ui.Rect(pos, size, active ? Theme.AccentSoft : hover ? Theme.SurfaceHover : Theme.Surface, 10f);
            if (active) ui.Border(pos, size, Theme.Accent, 1.5f, 10f);
            DrawPreview(ui, Kinds[i], pos + size * 0.5f, 26f, Theme.Text);
        }

        if (hovered is { } kind) ui.Tooltip(Localization.Get("shape_" + kind, kind.ToString()), _mouse, screenSize);
    }

    /// <summary>Small line drawing of a shape kind, used for the picker and the quick button.</summary>
    public static void DrawPreview(UiBatch ui, ShapeKind kind, Vector2 c, float size, Vector4 color)
    {
        float h = size * 0.5f;
        const float w = 2f;
        switch (kind)
        {
            case ShapeKind.Line:
                ui.Line(c + new Vector2(-h, h * 0.6f), c + new Vector2(h, -h * 0.6f), color, w);
                break;
            case ShapeKind.Arrow:
                ui.Line(c + new Vector2(-h, h * 0.6f), c + new Vector2(h, -h * 0.6f), color, w);
                ui.Line(c + new Vector2(h, -h * 0.6f), c + new Vector2(h * 0.25f, -h * 0.55f), color, w);
                ui.Line(c + new Vector2(h, -h * 0.6f), c + new Vector2(h * 0.6f, h * 0.05f), color, w);
                break;
            case ShapeKind.Rectangle:
                ui.Border(c - new Vector2(h, h * 0.7f), new Vector2(size, size * 0.7f), color, w);
                break;
            case ShapeKind.Ellipse:
                ui.Border(c - new Vector2(h, h * 0.62f), new Vector2(size, size * 0.62f), color, w, size * 0.31f);
                break;
            case ShapeKind.Triangle:
                ui.Line(c + new Vector2(-h, h * 0.7f), c + new Vector2(h, h * 0.7f), color, w);
                ui.Line(c + new Vector2(h, h * 0.7f), c + new Vector2(0, -h * 0.8f), color, w);
                ui.Line(c + new Vector2(0, -h * 0.8f), c + new Vector2(-h, h * 0.7f), color, w);
                break;
            case ShapeKind.Point:
                ui.Circle(c, 4f, color);
                break;
            case ShapeKind.Ray:
                ui.Circle(c + new Vector2(-h, h * 0.5f), 3f, color);
                ui.Line(c + new Vector2(-h, h * 0.5f), c + new Vector2(h * 1.2f, -h * 0.6f), color, w);
                break;
            case ShapeKind.InfiniteLine:
                ui.Line(c + new Vector2(-h * 1.2f, h * 0.55f), c + new Vector2(h * 1.2f, -h * 0.55f), color, w);
                ui.Circle(c + new Vector2(-h * 0.4f, h * 0.18f), 2.5f, color);
                ui.Circle(c + new Vector2(h * 0.4f, -h * 0.18f), 2.5f, color);
                break;
            case ShapeKind.Circle:
                ui.Ring(c, h, w, color);
                ui.Circle(c, 2f, color);
                break;
            case ShapeKind.Angle:
            {
                var v = c + new Vector2(-h * 0.8f, h * 0.6f);
                ui.Line(v, c + new Vector2(h, h * 0.6f), color, w);
                ui.Line(v, c + new Vector2(h * 0.3f, -h * 0.8f), color, w);
                ui.Arc(v, h * 0.5f, h * 0.5f + w, -MathF.Atan2(1.4f, 1.1f), 0f, color);
                break;
            }
            case ShapeKind.Plot:
                ui.Line(c + new Vector2(-h, h * 0.3f), c + new Vector2(h, h * 0.3f), Theme.WithAlpha(color, 0.6f), 1.5f);
                ui.Line(c + new Vector2(-h * 0.1f, h), c + new Vector2(-h * 0.1f, -h), Theme.WithAlpha(color, 0.6f), 1.5f);
                var previous = c + new Vector2(-h, -h);
                for (int i = 1; i <= 8; i++)
                {
                    float x = -1f + i / 4f;
                    var next = c + new Vector2(x * h, (x * x * 1.3f - 1f) * h);
                    ui.Line(previous, next, Theme.Accent, w);
                    previous = next;
                }

                break;
        }
    }
}
