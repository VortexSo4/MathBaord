using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>
/// The fill tool, and fills following the shapes that bound them. A fill remembers which shapes it lies in and which ones
/// cut it out when it was made (<see cref="FillTie"/>); when shapes are moved, reshaped or removed it is made again from
/// those, so the crescent of an Euler diagram stays a crescent, while a shape moved onto a filled one cuts nothing: both
/// fills show where they overlap, the upper shape's on top.
/// </summary>
public sealed partial class StrokeRenderer
{
    public string FillStyle { get; set; } = ObjectGeometry.FillHatch;

    /// <summary>The hatch spacing used last; a new fill starts with it.</summary>
    public float FillSpacing { get; set; } = ObjectGeometry.DefaultHatchSpacing;

    // A fill being placed: the layers it replaces (put back if cancelled) and where the drag that sets the spacing started.
    private List<(int Index, BoardObject Fill)> _fillReplaced = [];
    private int _fillPlaced = -1;
    private float _fillStartX, _fillStartSpacing;
    private bool _fillDragged;

    /// <summary>
    /// What a fill is made of: the shapes it lies in (its owner, the smallest, first, then shapes that only partly overlap
    /// that one, as the other circle of an Euler lens) and the shapes that cut it out. Frames around the owner are neither.
    /// <see cref="Fill"/> is the fill as it is in the document now.
    /// </summary>
    private sealed class FillTie(BoardObject fill, BoardObject[] inside, BoardObject[] cutters)
    {
        public readonly BoardObject Original = fill;
        public BoardObject Fill = fill;
        public readonly BoardObject[] Inside = inside;
        public readonly BoardObject[] Cutters = cutters;
    }

    // The fills an edit of shapes changes, noted when it starts: those made again at most once per frame while it goes on,
    // and those that only go along with the selection, as everything they are tied to is selected.
    private List<FillTie> _fillTies = [];
    private List<FillTie> _carriedTies = [];
    private bool _refillPending;
    // Shapes as the edit has made them so far, by the shape they were when it started.
    private readonly Dictionary<BoardObject, BoardObject> _edited = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Starts filling the region under the point with the current color and style; until <see cref="EndFill"/> moving
    /// left or right changes the hatch spacing. A region holds layers: a solid fill below, then hatching in each
    /// direction, the newest on top. A new layer replaces the one of the same direction, so "/" and "\" of different
    /// colors lie over each other; cross-hatching puts down both directions, and "clear" removes every layer.
    /// </summary>
    public void BeginFill(Vector2 screenPos)
    {
        var world = ScreenToWorld(screenPos);
        var objects = _document.Objects;
        bool InRegion(BoardObject o) => o.Kind == ShapeKind.Fill && ObjectGeometry.Distance(o, world) == 0f;

        // Cross-hatching from older versions is one object; as two layers, one direction can be replaced alone.
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            if (objects[i].Text != ObjectGeometry.FillCross || !InRegion(objects[i])) continue;
            objects[i] = objects[i] with { Text = ObjectGeometry.FillHatch };
            objects.Insert(i + 1, objects[i] with { Text = ObjectGeometry.FillBackHatch });
        }

        bool clear = FillStyle == ObjectGeometry.FillClear;
        string[] styles = LayerStyles(FillStyle);
        var replaced = Enumerable.Range(0, objects.Count)
            .Where(i => InRegion(objects[i]) && (clear || styles.Contains(objects[i].Text)))
            .Select(i => (i, objects[i])).ToList();
        float spacing = replaced.Select(r => r.Item2.Width).FirstOrDefault(w => w > 0, FillSpacing);
        var created = clear ? null : ObjectGeometry.FillAt(objects, world, CurrentColor, FillStyle, spacing);
        if (replaced.Count == 0 && created == null) return;

        _document.SaveState();
        for (int k = replaced.Count - 1; k >= 0; k--) objects.RemoveAt(replaced[k].Item1);
        _fillReplaced = replaced;
        (_fillStartX, _fillStartSpacing, _fillDragged) = (screenPos.X, spacing, false);
        if (created == null)
        {
            MarkStrokesDirty();
            return;
        }

        // Fills lie below the shapes that bound them; a solid fill is the background of the region's hatching. The fill
        // goes into the document right away, so even while its spacing is set it is drawn in its place, under the shape.
        int top = objects.TakeWhile(o => o.Kind == ShapeKind.Fill).Count();
        int bottom = objects.FindIndex(InRegion);
        _fillPlaced = created.Text == ObjectGeometry.FillSolid && bottom >= 0 ? Math.Min(bottom, top) : top;
        objects.Insert(_fillPlaced, created);
        MarkStrokesDirty();
    }

    /// <summary>The layers a fill style puts down: cross-hatching is both directions.</summary>
    private static string[] LayerStyles(string style) =>
        style == ObjectGeometry.FillCross ? [ObjectGeometry.FillHatch, ObjectGeometry.FillBackHatch] : [style];

    public void UpdateFill(Vector2 screenPos)
    {
        if (_fillPlaced < 0) return;
        float dx = screenPos.X - _fillStartX;
        if (MathF.Abs(dx) < 6f && !_fillDragged) return;
        _fillDragged = true;
        float spacing = Math.Clamp(_fillStartSpacing * MathF.Exp(dx / 150f), ObjectGeometry.MinHatchSpacing, ObjectGeometry.MaxHatchSpacing);
        _document.Objects[_fillPlaced] = _document.Objects[_fillPlaced] with { Width = spacing };
        MarkStrokesDirty();
    }

    public void EndFill()
    {
        if (_fillPlaced >= 0)
        {
            var objects = _document.Objects;
            var placed = objects[_fillPlaced];
            var styles = LayerStyles(placed.Text);
            // A plain tap on layers of the same color and style takes them away.
            if (!_fillDragged && styles.All(s => _fillReplaced.Any(r => r.Fill.Text == s && r.Fill.Color == placed.Color)))
            {
                objects.RemoveAt(_fillPlaced);
            }
            else
            {
                FillSpacing = placed.Width;
                // Cross-hatching is kept as two layers, so each direction can later be replaced on its own.
                if (styles.Length == 2)
                {
                    objects[_fillPlaced] = placed with { Text = styles[0] };
                    objects.Insert(_fillPlaced + 1, placed with { Text = styles[1] });
                }
            }

            SortFills();
            _document.IsDirty = true;
            MarkStrokesDirty();
        }

        _fillPlaced = -1;
        _fillReplaced = [];
    }

    /// <summary>Undoes a fill that is being placed, for example when the press turns into the radial menu.</summary>
    public void CancelFill()
    {
        if (_fillPlaced >= 0 || _fillReplaced.Count > 0)
        {
            if (_fillPlaced >= 0) _document.Objects.RemoveAt(_fillPlaced);
            foreach (var (index, fill) in _fillReplaced) _document.Objects.Insert(index, fill);
            _document.DiscardLastState();
            MarkStrokesDirty();
        }

        _fillPlaced = -1;
        _fillReplaced = [];
    }

    private static float Area(BoardObject o)
    {
        var (min, max) = ObjectGeometry.Bounds(o);
        return (max.X - min.X) * (max.Y - min.Y);
    }

    /// <summary>
    /// The closed shapes all of the fill lies in: those of a lens are both circles, while a copy of a filled circle laid
    /// over it holds the middle of the fill but not all of it.
    /// </summary>
    private static List<int> Holding(BoardObject fill, IReadOnlyList<BoardObject> objects, (Vector2 Min, Vector2 Max)[] boxes) =>
        ObjectGeometry.ShapesAround(fill, objects, boxes).Where(i => ObjectGeometry.IsWithin(fill, objects[i])).ToList();

    /// <summary>
    /// The shape a fill belongs to: the smallest closed shape holding it, so the fill of a circle inside a frame is the
    /// circle's, not the frame's. Null for a fill that no shape bounds any more. Pass the boxes when asking for many fills.
    /// </summary>
    private BoardObject? OwnerOf(BoardObject fill, (Vector2 Min, Vector2 Max)[]? boxes = null)
    {
        var objects = _document.Objects;
        var holding = Holding(fill, objects, boxes ?? ObjectGeometry.ClosedBounds(objects));
        return holding.Count == 0 ? null : objects[holding.MinBy(i => Area(objects[i]))];
    }

    private static FillTie? TieOf(BoardObject fill, IReadOnlyList<BoardObject> objects, (Vector2 Min, Vector2 Max)[] boxes)
    {
        var holding = Holding(fill, objects, boxes);
        if (holding.Count == 0) return null;
        int owner = holding.MinBy(i => Area(objects[i]));
        // A frame around the owner holds the fill too, but does not bound it.
        var inside = holding.Where(i => i == owner || !ObjectGeometry.Encloses(objects[i], objects[owner])).OrderBy(i => i != owner).ToList();
        var insideShapes = inside.Select(i => objects[i]).ToArray();
        var area = ObjectGeometry.Intersection(inside.Select(i => boxes[i]));
        var cutters = Enumerable.Range(0, objects.Count)
            .Where(i => !holding.Contains(i) && ObjectGeometry.IsClosed(objects[i].Kind) && ObjectGeometry.Overlap(boxes[i], area) &&
                        ObjectGeometry.Cuts(objects[i], insideShapes, fill))
            .Select(i => objects[i]).ToArray();
        return new FillTie(fill, insideShapes, cutters);
    }

    /// <summary>The ties of the fills that lie in the given shapes or are cut by them; no other fill can change with them.</summary>
    private List<FillTie> TiesTouching(IEnumerable<BoardObject> shapes)
    {
        var objects = _document.Objects;
        var closedShapes = shapes.Where(o => ObjectGeometry.IsClosed(o.Kind)).ToList();
        if (closedShapes.Count == 0 || !objects.Any(o => o.Kind == ShapeKind.Fill)) return [];
        var closed = closedShapes.ToHashSet<object>(ReferenceEqualityComparer.Instance);
        var reach = closedShapes.Select(ObjectGeometry.Bounds).ToList();
        var boxes = ObjectGeometry.ClosedBounds(objects);
        var ties = new List<FillTie>();
        foreach (var fill in objects)
        {
            if (fill.Kind != ShapeKind.Fill || !reach.Any(r => ObjectGeometry.Overlap(ObjectGeometry.Bounds(fill), r))) continue;
            if (TieOf(fill, objects, boxes) is { } tie && (tie.Inside.Any(closed.Contains) || tie.Cutters.Any(closed.Contains)))
                ties.Add(tie);
        }

        return ties;
    }

    private static bool AllIn(FillTie tie, HashSet<object> shapes) => tie.Inside.All(shapes.Contains) && tie.Cutters.All(shapes.Contains);

    /// <summary>Notes the fills tied to the shapes about to be edited; those tied only to them are carried along when <paramref name="rigid"/>.</summary>
    private void NoteTies(IReadOnlyCollection<BoardObject> shapes, bool rigid)
    {
        var ties = TiesTouching(shapes);
        var edited = shapes.ToHashSet<object>(ReferenceEqualityComparer.Instance);
        _carriedTies = rigid ? ties.Where(t => AllIn(t, edited)).ToList() : [];
        _fillTies = ties.Except(_carriedTies).ToList();
        _edited.Clear();
    }

    /// <summary>A shape as the edit going on has made it.</summary>
    private BoardObject Edited(BoardObject o) =>
        _edited.TryGetValue(o, out var edited) ? edited : _moving.Contains(o) ? o.Transformed(_movingTransform) : o;

    /// <summary>
    /// Makes the tied fills again where their shapes are now; <paramref name="current"/> gives a shape as it is now, or null
    /// if it was removed. A fill whose owner was removed goes with it. One left without a region (the lens of two circles
    /// moved apart) is not drawn while the edit goes on and removed when it is <paramref name="final"/>.
    /// </summary>
    private void Refill(List<FillTie> ties, Func<BoardObject, BoardObject?> current, bool final)
    {
        _refillPending = false;
        var objects = _document.Objects;
        foreach (var tie in ties)
        {
            int at = objects.FindIndex(o => ReferenceEquals(o, tie.Fill));
            if (at < 0) continue;
            var fill = tie.Fill;
            var made = current(tie.Inside[0]) == null
                ? null
                : ObjectGeometry.FillOf(tie.Inside.Select(current).OfType<BoardObject>(), tie.Cutters.Select(current).OfType<BoardObject>(),
                    fill.Color, fill.Text, fill.Width);
            if (made == null && (final || current(tie.Inside[0]) == null))
            {
                objects.RemoveAt(at);
                continue;
            }

            tie.Fill = made ?? fill with { Points = [] };
            objects[at] = tie.Fill;
        }

        MarkStrokesDirty();
    }

    /// <summary>
    /// Orders the fills by the height of their shapes, so where fills of different shapes overlap the upper shape's shows on
    /// top; the layers of one shape keep their order.
    /// </summary>
    private void SortFills()
    {
        var objects = _document.Objects;
        int count = objects.TakeWhile(o => o.Kind == ShapeKind.Fill).Count();
        if (count < 2) return;
        var boxes = ObjectGeometry.ClosedBounds(objects);
        var sorted = objects.Take(count)
            .Select(fill => (fill, owner: OwnerOf(fill, boxes) is { } o ? objects.FindIndex(x => ReferenceEquals(x, o)) : -1))
            .OrderBy(t => t.owner).Select(t => t.fill).ToList();
        for (int i = 0; i < count; i++) objects[i] = sorted[i];
    }

    /// <summary>
    /// Removes shapes and what goes with them: their fills go too, and the fills they cut out take the space back (an
    /// Euler circle's crescent becomes whole when the other circle is erased).
    /// </summary>
    private void RemoveObjects(IReadOnlyCollection<BoardObject> removed)
    {
        var ties = TiesTouching(removed);
        var gone = removed.ToHashSet(ReferenceEqualityComparer.Instance);
        _document.Objects.RemoveAll(gone.Contains);
        if (ties.Count > 0) Refill(ties, o => gone.Contains(o) ? null : o, final: true);
        MarkStrokesDirty();
    }
}
