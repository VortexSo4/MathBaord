using System.Numerics;
using MathBoard.Core;
using MathBoard.Graphics;

namespace MathBoard.Rendering;

/// <summary>
/// An overview of the whole board with the visible part framed, opened next to its quick button; pressing or dragging on
/// it moves the view there. The board is not drawn again: when it opens, its lines are laid on a grid of small cells, and
/// each row's runs of same-colored cells are drawn as rectangles.
/// </summary>
public sealed class Minimap(StrokeRenderer renderer) : IPopup
{
    private const float Padding = 10f;
    private const float Cell = 2f;
    private static readonly Vector2 MapSize = new(320f, 200f);

    private Vector2 _pos;
    private bool _dragging;
    // The world part shown, its scale to map pixels, and the offset that centers it in the map.
    private Vector2 _worldMin;
    private float _scale;
    private Vector2 _offset;
    private readonly List<(Vector2 Pos, Vector2 Size, Vector4 Color)> _runs = new();

    public bool IsOpen { get; private set; }

    private static Vector2 Size => MapSize + new Vector2(Padding * 2);
    private Vector2 MapPos => _pos + new Vector2(Padding);

    public void Open(Vector2 anchor)
    {
        var screen = renderer.ScreenSize;
        _pos = Vector2.Clamp(anchor - new Vector2(Size.X * 0.5f, Size.Y + 34f), new Vector2(8f),
            Vector2.Max(screen - Size - new Vector2(8f), new Vector2(8f)));
        Build();
        IsOpen = true;
        renderer.SetDirty();
    }

    public void Close()
    {
        IsOpen = false;
        _dragging = false;
        _runs.Clear();
        renderer.SetDirty();
    }

    private (Vector2 Min, Vector2 Max) View => (renderer.ScreenToWorld(Vector2.Zero), renderer.ScreenToWorld(renderer.ScreenSize));

    /// <summary>Fits the board and the view into the map and lays the board's lines on the cells.</summary>
    private void Build()
    {
        var view = View;
        var (min, max) = renderer.ContentBounds() is var (cMin, cMax) ? (Vector2.Min(cMin, view.Min), Vector2.Max(cMax, view.Max)) : view;
        var size = Vector2.Max(max - min, new Vector2(1f));
        min -= size * 0.05f;
        size *= 1.1f;
        _scale = MathF.Min(MapSize.X / size.X, MapSize.Y / size.Y);
        _worldMin = min;
        _offset = (MapSize - size * _scale) * 0.5f;

        int w = (int)(MapSize.X / Cell), h = (int)(MapSize.Y / Cell);
        var cells = new Vector4[w * h];
        var cellMax = new Vector2(w - 1, h - 1);
        Vector2 ToCell(Vector2 world) => ((world - _worldMin) * _scale + _offset) / Cell;

        foreach (var image in renderer.FrameImages)
        {
            // Pictures as gray rectangles.
            var a = Vector2.Clamp(ToCell(image.Origin), Vector2.Zero, cellMax);
            var b = Vector2.Clamp(ToCell(image.Origin + image.U + image.V), Vector2.Zero, cellMax);
            for (int y = (int)MathF.Min(a.Y, b.Y); y <= (int)MathF.Max(a.Y, b.Y); y++)
                for (int x = (int)MathF.Min(a.X, b.X); x <= (int)MathF.Max(a.X, b.X); x++)
                    cells[y * w + x] = Theme.WithAlpha(Theme.TextMuted, 1f);
        }

        // Every world primitive is a capsule; it is walked a cell at a time, later ones over earlier ones.
        foreach (var p in renderer.WorldPrims)
        {
            var a = ToCell(new Vector2(p.A.X, p.A.Y));
            var b = ToCell(new Vector2(p.A.Z, p.A.W));
            if (!Dashing.Clip(a, b, Vector2.Zero, cellMax, out float t0, out float t1)) continue;
            int r = (int)(p.B.X * _scale / Cell);
            int steps = (int)(Vector2.Distance(a, b) * (t1 - t0)) + 1;
            for (int i = 0; i <= steps; i++)
            {
                var c = Vector2.Lerp(a, b, t0 + (t1 - t0) * i / steps);
                for (int y = Math.Max((int)c.Y - r, 0); y <= Math.Min((int)c.Y + r, h - 1); y++)
                    for (int x = Math.Max((int)c.X - r, 0); x <= Math.Min((int)c.X + r, w - 1); x++)
                        cells[y * w + x] = p.Color with { W = 1f };
            }
        }

        _runs.Clear();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w;)
            {
                var color = cells[y * w + x];
                int end = x + 1;
                while (end < w && cells[y * w + end] == color) end++;
                if (color.W > 0) _runs.Add((new Vector2(x, y) * Cell, new Vector2(end - x, 1) * Cell, color));
                x = end;
            }
        }
    }

    private Vector2 ToMap(Vector2 world) => MapPos + (world - _worldMin) * _scale + _offset;

    /// <summary>Puts the middle of the view on the board point under the map position.</summary>
    private void CenterOn(Vector2 mapPoint)
    {
        var world = (Vector2.Clamp(mapPoint, MapPos, MapPos + MapSize) - MapPos - _offset) / _scale + _worldMin;
        var view = View;
        renderer.Camera.Position -= (world - (view.Min + view.Max) * 0.5f) * renderer.Camera.Zoom;
        renderer.SetDirty();
    }

    public void OnMouseDown(Vector2 pos)
    {
        if (!UiBatch.Contains(_pos, Size, pos))
        {
            Close();
            return;
        }

        _dragging = true;
        CenterOn(pos);
    }

    public void OnMouseMove(Vector2 pos)
    {
        if (_dragging) CenterOn(pos);
    }

    public void OnMouseUp(Vector2 pos) => _dragging = false;

    public void Render(UiBatch ui, Vector2 screenSize)
    {
        if (!IsOpen) return;
        ui.Rect(_pos + new Vector2(0, 4f), Size, new Vector4(0, 0, 0, 0.35f), Theme.PopupRadius);
        ui.Rect(_pos, Size, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(_pos, Size, Theme.Border, 1f, Theme.PopupRadius);
        ui.Rect(MapPos, MapSize, Settings.Background, 6f);
        foreach (var (pos, size, color) in _runs) ui.Rect(MapPos + pos, size, color);

        // The visible part of the board, cut to the map.
        var view = View;
        var min = Vector2.Clamp(ToMap(view.Min), MapPos, MapPos + MapSize);
        var max = Vector2.Clamp(ToMap(view.Max), MapPos, MapPos + MapSize);
        ui.Rect(min, max - min, Theme.WithAlpha(Theme.Accent, 0.12f), 3f);
        ui.Border(min, max - min, Theme.Accent, 1.5f, 3f);
        ui.Border(MapPos, MapSize, Theme.Border, 1f, 6f);
    }
}
