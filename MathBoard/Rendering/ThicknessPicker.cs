using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>Brush widths to tap, opened from the "Thickness" quick button.</summary>
public sealed class ThicknessPicker(StrokeRenderer renderer) : IPopup
{
    private const float Cell = 52f;
    private const float Padding = 12f;
    private const float TitleHeight = 34f;
    private static readonly float[] Widths = [3f, 6f, 10f, 16f, 24f, 36f];

    private Vector2 _pos;
    private Vector2 _mouse;

    public bool IsOpen { get; private set; }

    private static Vector2 Size => new(Padding * 2 + Widths.Length * Cell, TitleHeight + Cell + Padding);

    public void Open(Vector2 anchor)
    {
        var screen = renderer.ScreenSize;
        _pos = Vector2.Clamp(anchor - new Vector2(Size.X * 0.5f, Size.Y + 34f), new Vector2(8f),
            Vector2.Max(screen - Size - new Vector2(8f), new Vector2(8f)));
        IsOpen = true;
        renderer.SetDirty();
    }

    public void Close()
    {
        IsOpen = false;
        renderer.SetDirty();
    }

    private static float Width(int i) => Math.Clamp(Widths[i], Settings.MinBrushWidth, Settings.MaxBrushWidth);
    private Vector2 CellPos(int i) => _pos + new Vector2(Padding + i * Cell, TitleHeight);

    public void OnMouseDown(Vector2 pos) => _mouse = pos;

    public void OnMouseMove(Vector2 pos)
    {
        _mouse = pos;
        renderer.SetDirty();
    }

    public void OnMouseUp(Vector2 pos)
    {
        for (int i = 0; i < Widths.Length; i++)
        {
            if (!UiBatch.Contains(CellPos(i), new Vector2(Cell), pos)) continue;
            renderer.CurrentBrushWidth = Width(i);
            if (renderer.Tool is Tool.Eraser or Tool.Pan or Tool.Select) renderer.SetTool(Tool.Brush);
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
        ui.TextInRect(Localization.Get("action_Thickness", "Line width"), _pos + new Vector2(Padding, 0),
            new Vector2(Size.X - Padding * 2, TitleHeight), Theme.Text, Theme.Title);

        for (int i = 0; i < Widths.Length; i++)
        {
            var cell = CellPos(i);
            var center = cell + new Vector2(Cell * 0.5f);
            bool current = MathF.Abs(renderer.CurrentBrushWidth - Width(i)) < 0.5f;
            if (current || UiBatch.Contains(cell, new Vector2(Cell), _mouse))
                ui.Rect(cell + new Vector2(3f), new Vector2(Cell - 6f), current ? Theme.AccentSoft : Theme.SurfaceHover, 10f);
            ui.Circle(center, MathF.Min(Width(i) * 0.5f, Cell * 0.4f), renderer.CurrentColor);
        }
    }
}
