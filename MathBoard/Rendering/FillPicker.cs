using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>Fill styles to pick from, opened by the "Fill" quick button; picking one switches to the fill tool.</summary>
public sealed class FillPicker(StrokeRenderer renderer) : IPopup
{
    private const float Cell = 56f;
    private const float Padding = 12f;
    private const float TitleHeight = 34f;

    private Vector2 _pos;
    private Vector2 _mouse;

    public bool IsOpen { get; private set; }

    private static Vector2 Size => new(Padding * 2 + ObjectGeometry.FillStyles.Length * Cell, TitleHeight + Cell + Padding);

    public void Open(Vector2 anchor)
    {
        var screen = renderer.ScreenSize;
        _pos = Vector2.Clamp(anchor - new Vector2(Size.X + 34f, Size.Y * 0.5f), new Vector2(8f),
            Vector2.Max(screen - Size - new Vector2(8f), new Vector2(8f)));
        IsOpen = true;
        renderer.SetDirty();
    }

    public void Close()
    {
        IsOpen = false;
        renderer.SetDirty();
    }

    private Vector2 CellPos(int i) => _pos + new Vector2(Padding + i * Cell, TitleHeight);

    public void OnMouseDown(Vector2 pos) => _mouse = pos;

    public void OnMouseMove(Vector2 pos)
    {
        _mouse = pos;
        renderer.SetDirty();
    }

    public void OnMouseUp(Vector2 pos)
    {
        for (int i = 0; i < ObjectGeometry.FillStyles.Length; i++)
        {
            if (!UiBatch.Contains(CellPos(i), new Vector2(Cell), pos)) continue;
            renderer.FillStyle = ObjectGeometry.FillStyles[i];
            renderer.SetTool(Tool.Fill);
            break;
        }

        Close();
    }

    public void Render(UiBatch ui, Vector2 screenSize)
    {
        if (!IsOpen) return;
        ui.Rect(_pos + new Vector2(0, 4f), Size, new Vector4(0, 0, 0, 0.35f), Theme.PopupRadius);
        ui.Rect(_pos, Size, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(_pos, Size, Theme.Border, 1f, Theme.PopupRadius);
        ui.TextInRect(Localization.Get("action_Fill", "Fill"), _pos + new Vector2(Padding, 0),
            new Vector2(Size.X - Padding * 2, TitleHeight), Theme.Text, Theme.Title);

        string? hovered = null;
        for (int i = 0; i < ObjectGeometry.FillStyles.Length; i++)
        {
            string style = ObjectGeometry.FillStyles[i];
            var cell = CellPos(i);
            bool active = renderer.Tool == Tool.Fill && renderer.FillStyle == style;
            bool hover = UiBatch.Contains(cell, new Vector2(Cell), _mouse);
            if (hover) hovered = style;
            ui.Rect(cell + new Vector2(3f), new Vector2(Cell - 6f), active ? Theme.AccentSoft : hover ? Theme.SurfaceHover : Theme.Surface, 10f);
            DrawPreview(ui, style, cell + new Vector2(Cell * 0.5f), 26f, renderer.CurrentColor);
        }

        if (hovered != null) ui.Tooltip(Localization.Get("fill_" + hovered, hovered), _mouse, screenSize);
    }

    /// <summary>A small square filled in the given style.</summary>
    public static void DrawPreview(UiBatch ui, string style, Vector2 center, float size, Vector4 color)
    {
        float h = size * 0.5f;
        if (style == ObjectGeometry.FillSolid) ui.Rect(center - new Vector2(h), new Vector2(size), Theme.WithAlpha(color, 0.6f), 4f);
        if (style == ObjectGeometry.FillClear) ui.Line(center + new Vector2(-h, h), center + new Vector2(h, -h), Theme.Danger, 2f);
        if (style is ObjectGeometry.FillHatch or ObjectGeometry.FillCross) Hatch(ui, center, h, -1f, color);
        if (style is ObjectGeometry.FillBackHatch or ObjectGeometry.FillCross) Hatch(ui, center, h, 1f, color);
        ui.Border(center - new Vector2(h), new Vector2(size), color, 1.5f, 4f);
    }

    // Lines y = slope * x + t inside the square [-h, h]².
    private static void Hatch(UiBatch ui, Vector2 center, float h, float slope, Vector4 color)
    {
        for (float t = -2f * h + 5f; t < 2f * h; t += 6f)
        {
            float x0 = MathF.Max(-h, slope > 0 ? -h - t : t - h), x1 = MathF.Min(h, slope > 0 ? h - t : t + h);
            if (x1 <= x0) continue;
            ui.Line(center + new Vector2(x0, slope * x0 + t), center + new Vector2(x1, slope * x1 + t), color, 1.2f);
        }
    }
}
