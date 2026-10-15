using System.Numerics;
using MathBoard.Core;
using MathBoard.Graphics;
using SkiaSharp;

namespace MathBoard.Rendering;

/// <summary>
/// Fills of regions bounded by closed shapes. A fill keeps its region as polygons (contours separated by NaN points),
/// its style in <see cref="BoardObject.Text"/> and its hatch spacing in <see cref="BoardObject.Width"/>; it is drawn
/// with lines, so every backend can show it.
/// </summary>
public static partial class ObjectGeometry
{
    public const string FillSolid = "solid", FillHatch = "hatch", FillBackHatch = "backhatch", FillCross = "cross", FillClear = "clear";
    /// <summary>The styles in the picker; "clear" takes fills away instead of making one.</summary>
    public static readonly string[] FillStyles = [FillSolid, FillHatch, FillBackHatch, FillCross, FillClear];

    public const float DefaultHatchSpacing = 12f;
    public const float MinHatchSpacing = 4f, MaxHatchSpacing = 80f;
    private const float SolidSpacing = 3f;
    private static readonly Vector2 Separator = new(float.NaN);

    /// <summary>Pictures and fills lie below everything; they are picked only when nothing else is near.</summary>
    public static bool IsBackdrop(ShapeKind kind) => kind is ShapeKind.Image or ShapeKind.Fill;

    public static bool IsClosed(ShapeKind kind) => kind is ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.Circle or ShapeKind.Triangle;

    private static Vector2[] Outline(BoardObject o) => o.Kind switch
    {
        ShapeKind.Circle => EllipsePoints(o.Points[0], new Vector2(Vector2.Distance(o.Points[0], o.Points[1]), 0),
            new Vector2(0, Vector2.Distance(o.Points[0], o.Points[1])), 128),
        ShapeKind.Ellipse => EllipsePoints(o.Points[0], o.Points[1] - o.Points[0], o.Points[2] - o.Points[0], 128),
        _ => o.Points
    };

    /// <summary>
    /// The region around <paramref name="point"/>: inside every closed shape that contains it and outside all the others,
    /// so each part of overlapping figures (Euler circles) is a region of its own. Null outside every shape.
    /// </summary>
    public static BoardObject? FillAt(IReadOnlyList<BoardObject> objects, Vector2 point, Vector4 color, string style, float spacing)
    {
        var boxes = ClosedBounds(objects);
        var inside = ShapesAround(point, objects, boxes);
        if (inside.Count == 0) return null;
        var area = Intersection(inside.Select(i => boxes[i]));
        var cutters = Enumerable.Range(0, objects.Count).Where(i => !inside.Contains(i) && IsClosed(objects[i].Kind) && Overlap(boxes[i], area));
        return FillOf(inside.Select(i => objects[i]), cutters.Select(i => objects[i]), color, style, spacing);
    }

    public static (Vector2 Min, Vector2 Max) Intersection(IEnumerable<(Vector2 Min, Vector2 Max)> boxes) =>
        boxes.Aggregate((a, b) => (Vector2.Max(a.Min, b.Min), Vector2.Min(a.Max, b.Max)));

    /// <summary>
    /// Bounding boxes of the closed shapes (empty boxes for everything else): shapes whose box cannot matter are skipped
    /// without building their outlines, which keeps fills fast on a board with thousands of shapes.
    /// </summary>
    public static (Vector2 Min, Vector2 Max)[] ClosedBounds(IReadOnlyList<BoardObject> objects)
    {
        var boxes = new (Vector2 Min, Vector2 Max)[objects.Count];
        for (int i = 0; i < objects.Count; i++)
            boxes[i] = IsClosed(objects[i].Kind) ? Bounds(objects[i]) : (new Vector2(float.MaxValue), new Vector2(float.MinValue));
        return boxes;
    }

    public static bool Overlap((Vector2 Min, Vector2 Max) a, (Vector2 Min, Vector2 Max) b) =>
        a.Max.X >= b.Min.X && a.Min.X <= b.Max.X && a.Max.Y >= b.Min.Y && a.Min.Y <= b.Max.Y;

    public static bool Holds((Vector2 Min, Vector2 Max) outer, (Vector2 Min, Vector2 Max) inner) =>
        outer.Min.X <= inner.Min.X && outer.Min.Y <= inner.Min.Y && outer.Max.X >= inner.Max.X && outer.Max.Y >= inner.Max.Y;

    /// <summary>Indices of the closed shapes that contain the point.</summary>
    private static List<int> ShapesAround(Vector2 point, IReadOnlyList<BoardObject> objects, (Vector2 Min, Vector2 Max)[] boxes)
    {
        var inside = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            if (!IsClosed(objects[i].Kind) || !Holds(boxes[i], (point, point))) continue;
            using var path = PathOf(objects[i]);
            if (path.Contains(point.X, point.Y)) inside.Add(i);
        }

        return inside;
    }

    /// <summary>Indices of the closed shapes the fill lies inside: its region is their intersection minus the other shapes.</summary>
    public static List<int> ShapesAround(BoardObject fill, IReadOnlyList<BoardObject> objects, (Vector2 Min, Vector2 Max)[] boxes) =>
        InteriorPoint(fill) is { } inside ? ShapesAround(inside, objects, boxes) : [];

    /// <summary>
    /// Whether the fill leaves out where <paramref name="shape"/> overlaps the shapes the fill lies in: the shape cut it when
    /// it was made, as the other circle cuts the crescent of an Euler diagram.
    /// </summary>
    public static bool Cuts(BoardObject shape, IEnumerable<BoardObject> inside, BoardObject fill)
    {
        var overlap = PathOf(shape);
        foreach (var o in inside) overlap = Combine(overlap, PathOf(o), SKPathOp.Intersect);
        using (overlap)
        {
            float area = Area(overlap);
            if (area < 1f) return false;
            using var region = FillPath(fill);
            using var covered = overlap.Op(region, SKPathOp.Intersect);
            return covered == null || Area(covered) < area * 0.01f;
        }
    }

    /// <summary>Whether all of the fill lies inside <paramref name="shape"/>, but for slivers along its edge.</summary>
    public static bool IsWithin(BoardObject fill, BoardObject shape)
    {
        using var region = FillPath(fill);
        using var path = PathOf(shape);
        using var outside = region.Op(path, SKPathOp.Difference);
        return outside == null || Area(outside) < Area(region) * 0.01f;
    }

    /// <summary>The area of a path of polygons; a hole adds its area instead of taking it away, which is fine for telling slivers.</summary>
    private static float Area(SKPath path)
    {
        float area = 0f;
        foreach (var contour in Contours(Polygons(path)))
        {
            float twice = 0f;
            for (int i = 0; i < contour.Length; i++)
            {
                var a = contour[i];
                var b = contour[(i + 1) % contour.Length];
                twice += a.X * b.Y - a.Y * b.X;
            }

            area += MathF.Abs(twice) * 0.5f;
        }

        return area;
    }

    private static SKPath FillPath(BoardObject fill)
    {
        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        foreach (var contour in Contours(fill.Points)) path.AddPoly(contour.Select(p => new SKPoint(p.X, p.Y)).ToArray());
        return path;
    }

    /// <summary>Whether <paramref name="outer"/> holds all of <paramref name="inner"/>, as a frame holds a circle drawn in it.</summary>
    public static bool Encloses(BoardObject outer, BoardObject inner)
    {
        if (ReferenceEquals(outer, inner) || !IsClosed(outer.Kind) || !IsClosed(inner.Kind) || !Holds(Bounds(outer), Bounds(inner))) return false;
        using var path = PathOf(outer);
        return Outline(inner).All(p => path.Contains(p.X, p.Y));
    }

    private static SKPath PathOf(BoardObject o)
    {
        var path = new SKPath();
        path.AddPoly(Outline(o).Select(p => new SKPoint(p.X, p.Y)).ToArray());
        return path;
    }

    /// <summary>A fill of the region inside every shape in <paramref name="inside"/> and outside every one in <paramref name="cutters"/>.</summary>
    public static BoardObject? FillOf(IEnumerable<BoardObject> inside, IEnumerable<BoardObject> cutters, Vector4 color, string style, float spacing)
    {
        SKPath? region = null;
        foreach (var o in inside) region = Combine(region, PathOf(o), SKPathOp.Intersect);
        if (region == null) return null;
        foreach (var o in cutters) region = Combine(region, PathOf(o), SKPathOp.Difference);
        var points = Polygons(region);
        region.Dispose();
        return points.Length < 3 ? null : new BoardObject(ShapeKind.Fill, points, color, spacing) { Text = style };
    }

    /// <summary>The polygons of a path made of lines, as contours separated by NaN points.</summary>
    private static Vector2[] Polygons(SKPath path)
    {
        var points = new List<Vector2>();
        using var iterator = path.CreateRawIterator();
        var verb = new SKPoint[4];
        SKPathVerb kind;
        while ((kind = iterator.Next(verb)) != SKPathVerb.Done)
        {
            if (kind == SKPathVerb.Move)
            {
                if (points.Count > 0) points.Add(Separator);
                points.Add(new Vector2(verb[0].X, verb[0].Y));
            }
            else if (kind == SKPathVerb.Line)
            {
                points.Add(new Vector2(verb[1].X, verb[1].Y));
            }
        }

        return points.ToArray();
    }

    private static SKPath Combine(SKPath? region, SKPath path, SKPathOp op)
    {
        if (region == null) return path;
        var result = region.Op(path, op) ?? new SKPath();
        region.Dispose();
        path.Dispose();
        return result;
    }

    private static List<Vector2[]> Contours(Vector2[] points)
    {
        var contours = new List<Vector2[]>();
        int start = 0;
        for (int i = 0; i <= points.Length; i++)
        {
            if (i < points.Length && !float.IsNaN(points[i].X)) continue;
            if (i - start >= 3) contours.Add(points[start..i]);
            start = i + 1;
        }

        return contours;
    }

    private static void BuildFill(BoardObject o, List<Prim> prims)
    {
        var contours = Contours(o.Points);
        if (o.Text == FillSolid)
        {
            // An opaque tint, so the overlapping lines that make up the area do not show.
            var tint = Vector4.Lerp(Settings.Background, o.Color, 0.35f) with { W = 1f };
            Scan(prims, contours, 0f, SolidSpacing, SolidSpacing * 0.7f, tint);
            // The ends of the scanlines make a staircase along the edge, which shows between the dashes of a dashed
            // outline; a line along the edge smooths it.
            foreach (var contour in contours) Polyline(prims, contour, true, SolidSpacing * 0.7f, tint);
            return;
        }

        float spacing = Math.Clamp(o.Width > 0 ? o.Width : DefaultHatchSpacing, MinHatchSpacing, MaxHatchSpacing);
        float r = Math.Clamp(spacing * 0.08f, 0.75f, 2f);
        // Screen y points down, so -45° reads as "/".
        if (o.Text is FillHatch or FillCross) Scan(prims, contours, -MathF.PI / 4f, spacing, r, o.Color);
        if (o.Text is FillBackHatch or FillCross) Scan(prims, contours, MathF.PI / 4f, spacing, r, o.Color);
    }

    /// <summary>
    /// Parallel lines across the region (even-odd, so holes stay empty). They sit on multiples of the spacing in world
    /// space, so hatching lines up across neighbouring fills.
    /// </summary>
    private static void Scan(List<Prim> prims, List<Vector2[]> contours, float angle, float spacing, float r, Vector4 c)
    {
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var normal = new Vector2(-dir.Y, dir.X);
        float min = float.MaxValue, max = float.MinValue;
        foreach (var p in contours.SelectMany(contour => contour))
        {
            min = MathF.Min(min, Vector2.Dot(p, normal));
            max = MathF.Max(max, Vector2.Dot(p, normal));
        }

        var crossings = new List<float>();
        for (float v = MathF.Ceiling(min / spacing) * spacing; v <= max; v += spacing)
        {
            crossings.Clear();
            foreach (var contour in contours)
            {
                for (int i = 0; i < contour.Length; i++)
                {
                    var a = contour[i];
                    var b = contour[(i + 1) % contour.Length];
                    float va = Vector2.Dot(a, normal), vb = Vector2.Dot(b, normal);
                    if (va <= v == vb <= v) continue;
                    crossings.Add(Vector2.Dot(Vector2.Lerp(a, b, (v - va) / (vb - va)), dir));
                }
            }

            crossings.Sort();
            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                // Pulled in by the radius, so the round ends stay inside the region.
                float from = crossings[i] + r, to = crossings[i + 1] - r;
                if (to < from) continue;
                prims.Add(Prim.Capsule(dir * from + normal * v, dir * to + normal * v, r, c));
            }
        }
    }

    /// <summary>A point inside the fill: the middle of the first span of its middle scanline.</summary>
    private static Vector2? InteriorPoint(BoardObject fill)
    {
        var contours = Contours(fill.Points);
        if (contours.Count == 0) return null;
        float y = (contours.SelectMany(c => c).Min(p => p.Y) + contours.SelectMany(c => c).Max(p => p.Y)) * 0.5f;
        var crossings = new List<float>();
        foreach (var contour in contours)
            for (int i = 0; i < contour.Length; i++)
            {
                var a = contour[i];
                var b = contour[(i + 1) % contour.Length];
                if (a.Y <= y != b.Y <= y) crossings.Add(a.X + (b.X - a.X) * (y - a.Y) / (b.Y - a.Y));
            }

        if (crossings.Count < 2) return null;
        crossings.Sort();
        return new Vector2((crossings[0] + crossings[1]) * 0.5f, y);
    }

    private static bool InsideFill(BoardObject o, Vector2 point)
    {
        bool inside = false;
        foreach (var contour in Contours(o.Points))
        {
            for (int i = 0, j = contour.Length - 1; i < contour.Length; j = i++)
            {
                var a = contour[i];
                var b = contour[j];
                if (a.Y > point.Y != b.Y > point.Y && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
        }

        return inside;
    }
}
