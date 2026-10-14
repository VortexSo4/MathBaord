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

    private static bool IsClosed(ShapeKind kind) => kind is ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.Circle or ShapeKind.Triangle;

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
    public static BoardObject? FillAt(IEnumerable<BoardObject> objects, Vector2 point, Vector4 color, string style, float spacing)
    {
        SKPath? region = null;
        var others = new List<SKPath>();
        foreach (var o in objects.Where(o => IsClosed(o.Kind)))
        {
            var path = new SKPath();
            path.AddPoly(Outline(o).Select(p => new SKPoint(p.X, p.Y)).ToArray());
            if (!path.Contains(point.X, point.Y))
            {
                others.Add(path);
                continue;
            }

            region = Combine(region, path, SKPathOp.Intersect);
        }

        foreach (var other in others)
        {
            if (region != null) region = Combine(region, other, SKPathOp.Difference);
            else other.Dispose();
        }

        if (region == null) return null;
        var points = new List<Vector2>();
        using (var iterator = region.CreateRawIterator())
        {
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
        }

        region.Dispose();
        return points.Count < 3 ? null : new BoardObject(ShapeKind.Fill, points.ToArray(), color, spacing) { Text = style };
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

    /// <summary>
    /// Whether the fill lies inside <paramref name="shape"/>, so it goes away with it: a point inside the fill (the middle
    /// of its middle scanline) is inside the shape.
    /// </summary>
    public static bool IsFillInside(BoardObject fill, BoardObject shape)
    {
        if (!IsClosed(shape.Kind) || InteriorPoint(fill) is not { } inside) return false;
        using var path = new SKPath();
        path.AddPoly(Outline(shape).Select(p => new SKPoint(p.X, p.Y)).ToArray());
        return path.Contains(inside.X, inside.Y);
    }

    /// <summary>The fill computed again for the shapes as they are now, for example after one bounding it was removed.</summary>
    public static BoardObject? Refill(BoardObject fill, IEnumerable<BoardObject> objects) =>
        InteriorPoint(fill) is { } inside ? FillAt(objects, inside, fill.Color, fill.Text, fill.Width) : null;

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
