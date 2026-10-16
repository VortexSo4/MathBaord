using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

public enum DimensionKind { Length, Angle }

/// <summary>
/// One measure of a shape that can be typed in: a length in world units (a side, a segment, a radius, a semi-axis) or an
/// angle in degrees. Its label goes at <see cref="At"/>, moved <see cref="Offset"/> screen pixels towards
/// <see cref="Away"/>; a radius or semi-axis is shown as a line from <see cref="GuideFrom"/> to <see cref="GuideTo"/>.
/// </summary>
public readonly record struct Dimension(DimensionKind Kind, int Index, float Value, Vector2 At, Vector2 Away, float Offset,
    Vector2 GuideFrom = default, Vector2 GuideTo = default)
{
    public bool HasGuide => GuideFrom != GuideTo;
}

public static partial class ObjectGeometry
{
    public const float MinLength = 1f;

    /// <summary>The measures of a line, a flat closed shape or an angle; other shapes have none.</summary>
    public static List<Dimension> Dimensions(BoardObject o)
    {
        var p = o.Points;
        var list = new List<Dimension>();
        switch (o.Kind)
        {
            case ShapeKind.Line or ShapeKind.Arrow:
                // On the line itself: the selection's box handles sit on both sides of it.
                list.Add(new Dimension(DimensionKind.Length, 0, Vector2.Distance(p[0], p[1]), (p[0] + p[1]) * 0.5f, Vector2.Zero, 0f));
                break;
            case ShapeKind.Circle:
                list.Add(Radius(0, p[0], p[1]));
                break;
            case ShapeKind.Ellipse:
                list.Add(Radius(0, p[0], p[1]));
                list.Add(Radius(1, p[0], p[2]));
                break;
            case ShapeKind.Angle:
            {
                // Where the angle's own label is, so the typed value replaces what is shown.
                var ua = Direction(p[0], p[1]);
                var ub = Direction(p[0], p[2]);
                float radius = Math.Clamp(MathF.Min(Vector2.Distance(p[0], p[1]), Vector2.Distance(p[0], p[2])) * 0.3f, 12f, 80f);
                var bisector = (ua + ub).LengthSquared() < 1e-6f ? new Vector2(-ua.Y, ua.X) : Direction(Vector2.Zero, ua + ub);
                list.Add(new Dimension(DimensionKind.Angle, 0, AngleAt(p[0], p[1], p[2]), p[0] + bisector * (radius + 16f), Vector2.Zero, 0f));
                break;
            }
            case ShapeKind.Rectangle or ShapeKind.Triangle:
            {
                int n = p.Length;
                var center = p.Aggregate(Vector2.Zero, (a, b) => a + b) / n;
                for (int i = 0; i < n; i++)
                {
                    var a = p[i];
                    var b = p[(i + 1) % n];
                    var mid = (a + b) * 0.5f;
                    var dir = Direction(a, b);
                    var inward = new Vector2(-dir.Y, dir.X);
                    if (Vector2.Dot(center - mid, inward) < 0) inward = -inward;
                    list.Add(new Dimension(DimensionKind.Length, i, Vector2.Distance(a, b), mid, inward, 22f));
                }

                for (int i = 0; i < n; i++)
                {
                    var u = Direction(p[i], p[(i + n - 1) % n]);
                    var v = Direction(p[i], p[(i + 1) % n]);
                    var bisector = (u + v).LengthSquared() < 1e-6f ? new Vector2(-u.Y, u.X) : Direction(Vector2.Zero, u + v);
                    list.Add(new Dimension(DimensionKind.Angle, i, AngleAt(p[i], p[(i + n - 1) % n], p[(i + 1) % n]), p[i], bisector, 32f));
                }

                break;
            }
        }

        return list;
    }

    private static Dimension Radius(int index, Vector2 center, Vector2 end)
    {
        var dir = Direction(center, end);
        return new Dimension(DimensionKind.Length, index, Vector2.Distance(center, end), (center + end) * 0.5f,
            new Vector2(-dir.Y, dir.X), 14f, center, end);
    }

    /// <summary>The angle at <paramref name="vertex"/> between the directions to a and b, in degrees from 0 to 180.</summary>
    private static float AngleAt(Vector2 vertex, Vector2 a, Vector2 b)
    {
        var u = Direction(vertex, a);
        var v = Direction(vertex, b);
        return MathF.Acos(Math.Clamp(Vector2.Dot(u, v), -1f, 1f)) * 180f / MathF.PI;
    }

    private static Vector2 Rotate(Vector2 v, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>
    /// The shape with one measure set to <paramref name="value"/> (world units or degrees). A segment, a radius and a
    /// semi-axis keep their start and direction. A side of a polygon keeps its first vertex and the one before it: the
    /// side grows along itself and the vertices after it move with it, so a rectangle stays a rectangle. An angle keeps
    /// its vertex and the side before it, and turns the side after it with the vertices beyond. A side and the angle at
    /// its first vertex therefore keep each other, so both can be set one after the other.
    /// </summary>
    public static BoardObject WithDimension(BoardObject o, Dimension d, float value)
    {
        var p = (Vector2[])o.Points.Clone();
        if (d.Kind == DimensionKind.Length) value = MathF.Max(value, MinLength);
        else value = Math.Clamp(value, 1f, 179f);

        switch (o.Kind)
        {
            case ShapeKind.Line or ShapeKind.Arrow or ShapeKind.Circle:
                p[1] = p[0] + Direction(p[0], p[1]) * value;
                break;
            case ShapeKind.Ellipse:
                p[d.Index + 1] = p[0] + Direction(p[0], p[d.Index + 1]) * value;
                break;
            case ShapeKind.Angle:
                p[2] = p[0] + TurnedTo(p[0], p[1], p[2], value) * Vector2.Distance(p[0], p[2]);
                break;
            case ShapeKind.Rectangle or ShapeKind.Triangle:
            {
                int n = p.Length, i = d.Index, before = (i + n - 1) % n;
                bool Moves(int j) => j != i && j != before;
                if (d.Kind == DimensionKind.Length)
                {
                    var next = (i + 1) % n;
                    var shift = Direction(p[i], p[next]) * (value - Vector2.Distance(p[i], p[next]));
                    for (int j = 0; j < n; j++)
                        if (Moves(j)) p[j] += shift;
                }
                else
                {
                    var next = p[(i + 1) % n];
                    var turned = TurnedTo(p[i], p[before], next, value);
                    var old = Direction(p[i], next);
                    float angle = MathF.Atan2(old.X * turned.Y - old.Y * turned.X, Vector2.Dot(old, turned));
                    var vertex = p[i];
                    for (int j = 0; j < n; j++)
                        if (Moves(j)) p[j] = vertex + Rotate(p[j] - vertex, angle);
                }

                break;
            }
        }

        return o with { Points = p };
    }

    /// <summary>
    /// The direction from <paramref name="vertex"/> that makes the given angle with the direction to <paramref name="fixedEnd"/>,
    /// on the side where <paramref name="movingEnd"/> is now.
    /// </summary>
    private static Vector2 TurnedTo(Vector2 vertex, Vector2 fixedEnd, Vector2 movingEnd, float degrees)
    {
        var u = Direction(vertex, fixedEnd);
        var v = movingEnd - vertex;
        float side = u.X * v.Y - u.Y * v.X < 0 ? -1f : 1f;
        return Rotate(u, side * degrees * MathF.PI / 180f);
    }
}
