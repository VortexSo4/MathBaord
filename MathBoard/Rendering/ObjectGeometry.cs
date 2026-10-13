using System.Globalization;
using System.Numerics;
using MathBoard.Core;
using MathBoard.Graphics;

namespace MathBoard.Rendering;

/// <summary>Text attached to a world position, drawn in screen space at a fixed size.</summary>
public readonly record struct ObjectLabel(Vector2 World, string Text, Vector4 Color);

/// <summary>Turns <see cref="BoardObject"/>s into capsule primitives and implements their editing rules.</summary>
public static partial class ObjectGeometry
{
    /// <summary>World units per math unit on grids and new plots.</summary>
    public const float GridUnit = 50f;

    // Rays and lines are drawn this far; that is beyond any reachable zoom.
    private const float Far = 1_000_000f;

    private static readonly Vector4[] CurveColors =
    [
        Theme.Accent, Theme.Danger, Theme.Rgb(0x3F, 0xB9, 0x50), Theme.Rgb(0xF5, 0x9E, 0x0B), Theme.Rgb(0xA8, 0x55, 0xF7)
    ];

    public static void Build(BoardObject o, List<Prim> prims, List<ObjectLabel>? labels)
    {
        if (IsSolid(o.Kind))
        {
            BuildSolid(o, prims);
            return;
        }

        var p = o.Points;
        float r = o.Width * 0.5f;
        var c = o.Color;
        switch (o.Kind)
        {
            case ShapeKind.Line:
                prims.Add(Prim.Capsule(p[0], p[1], r, c));
                break;
            case ShapeKind.Arrow:
                prims.Add(Prim.Capsule(p[0], p[1], r, c));
                AddArrowHead(prims, p[0], p[1], MathF.Max(o.Width * 3.5f, 14f), r, c);
                break;
            case ShapeKind.Rectangle or ShapeKind.Triangle:
                Polyline(prims, p, true, r, c);
                break;
            case ShapeKind.Ellipse:
                Polyline(prims, EllipsePoints(p[0], p[1] - p[0], p[2] - p[0], 96), true, r, c);
                break;
            case ShapeKind.Point:
                prims.Add(Prim.Capsule(p[0], p[0], MathF.Max(o.Width * 0.9f, 4f), c));
                break;
            case ShapeKind.Ray:
            {
                var dir = Direction(p[0], p[1]);
                prims.Add(Prim.Capsule(p[0], p[0] + dir * Far, r, c));
                prims.Add(Prim.Capsule(p[0], p[0], MathF.Max(o.Width * 0.9f, 4f), c));
                break;
            }
            case ShapeKind.InfiniteLine:
            {
                var dir = Direction(p[0], p[1]);
                prims.Add(Prim.Capsule(p[0] - dir * Far, p[0] + dir * Far, r, c));
                break;
            }
            case ShapeKind.Circle:
            {
                float radius = Vector2.Distance(p[0], p[1]);
                Polyline(prims, EllipsePoints(p[0], new Vector2(radius, 0), new Vector2(0, radius), 128), true, r, c);
                prims.Add(Prim.Capsule(p[0], p[0], MathF.Max(o.Width * 0.7f, 2.5f), c));
                break;
            }
            case ShapeKind.Angle:
                BuildAngle(o, prims, labels);
                break;
            case ShapeKind.Plot:
                BuildPlot(o, prims, labels);
                break;
            case ShapeKind.Image:
            {
                // Only drawn when the picture file is missing; the picture itself is not a primitive.
                var corner = p[1] + p[2] - p[0];
                var gray = Theme.WithAlpha(Theme.TextMuted, 0.8f);
                Polyline(prims, [p[0], p[1], corner, p[2]], true, 1.5f, gray);
                prims.Add(Prim.Capsule(p[0], corner, 1.5f, gray));
                prims.Add(Prim.Capsule(p[1], p[2], 1.5f, gray));
                break;
            }
        }
    }

    private static Vector2 Direction(Vector2 a, Vector2 b) =>
        Vector2.DistanceSquared(a, b) > 1e-8f ? Vector2.Normalize(b - a) : Vector2.UnitX;

    private static void Polyline(List<Prim> prims, IReadOnlyList<Vector2> pts, bool closed, float r, Vector4 c)
    {
        for (int i = 0; i < pts.Count - 1; i++) prims.Add(Prim.Capsule(pts[i], pts[i + 1], r, c));
        if (closed && pts.Count > 2) prims.Add(Prim.Capsule(pts[^1], pts[0], r, c));
    }

    private static Vector2[] EllipsePoints(Vector2 center, Vector2 u, Vector2 v, int segments)
    {
        var pts = new Vector2[segments];
        for (int i = 0; i < segments; i++)
        {
            float t = i * MathF.PI * 2f / segments;
            pts[i] = center + u * MathF.Cos(t) + v * MathF.Sin(t);
        }

        return pts;
    }

    private static void AddArrowHead(List<Prim> prims, Vector2 from, Vector2 tip, float length, float r, Vector4 c)
    {
        var dir = Direction(from, tip);
        var side = new Vector2(-dir.Y, dir.X) * length * 0.5f;
        prims.Add(Prim.Capsule(tip, tip - dir * length + side, r, c));
        prims.Add(Prim.Capsule(tip, tip - dir * length - side, r, c));
    }

    private static void BuildAngle(BoardObject o, List<Prim> prims, List<ObjectLabel>? labels)
    {
        var p = o.Points;
        float r = o.Width * 0.5f;
        prims.Add(Prim.Capsule(p[0], p[1], r, o.Color));
        prims.Add(Prim.Capsule(p[0], p[2], r, o.Color));

        var ua = Direction(p[0], p[1]);
        var ub = Direction(p[0], p[2]);
        float a0 = MathF.Atan2(ua.Y, ua.X);
        float delta = MathF.Atan2(ub.Y, ub.X) - a0;
        if (delta > MathF.PI) delta -= MathF.PI * 2f;
        if (delta < -MathF.PI) delta += MathF.PI * 2f;
        float degrees = MathF.Abs(delta) * 180f / MathF.PI;

        float radius = Math.Clamp(MathF.Min(Vector2.Distance(p[0], p[1]), Vector2.Distance(p[0], p[2])) * 0.3f, 12f, 80f);
        float arcWidth = MathF.Max(r * 0.6f, 1f);
        if (MathF.Abs(degrees - 90f) < 0.5f)
        {
            // A right angle is marked with a square, as in textbooks.
            float s = radius * 0.7f;
            prims.Add(Prim.Capsule(p[0] + ua * s, p[0] + (ua + ub) * s, arcWidth, o.Color));
            prims.Add(Prim.Capsule(p[0] + ub * s, p[0] + (ua + ub) * s, arcWidth, o.Color));
        }
        else
        {
            const int segments = 32;
            var previous = p[0] + ua * radius;
            for (int i = 1; i <= segments; i++)
            {
                float t = a0 + delta * i / segments;
                var next = p[0] + new Vector2(MathF.Cos(t), MathF.Sin(t)) * radius;
                prims.Add(Prim.Capsule(previous, next, arcWidth, o.Color));
                previous = next;
            }
        }

        var bisector = Direction(Vector2.Zero, ua + ub);
        if ((ua + ub).LengthSquared() < 1e-6f) bisector = new Vector2(-ua.Y, ua.X);
        labels?.Add(new ObjectLabel(p[0] + bisector * (radius + 16f), Number(degrees, 1) + "°", o.Color));
    }

    /// <summary>Plot coordinates of the frame: the x range is set by the user, y keeps the same units around zero.</summary>
    private readonly record struct PlotFrame(Vector2 Origin, Vector2 U, Vector2 V, float XMin, float XMax, float YMin, float YMax)
    {
        public Vector2 Map(double x, double y) =>
            Origin + U * (float)((x - XMin) / (XMax - XMin)) + V * (float)((y - YMin) / (YMax - YMin));
    }

    private static PlotFrame FrameOf(BoardObject o)
    {
        var p = o.Points;
        var u = p[1] - p[0];
        var v = p[2] - p[0];
        float xRange = MathF.Max(o.XMax - o.XMin, 1e-3f);
        float yRange = xRange * v.Length() / MathF.Max(u.Length(), 1e-3f);
        return new PlotFrame(p[0], u, v, o.XMin, o.XMin + xRange, -yRange * 0.5f, yRange * 0.5f);
    }

    /// <summary>The smallest value of the form 1, 2 or 5 times a power of ten that is at least <paramref name="minimum"/>.</summary>
    public static float NiceCeil(float minimum)
    {
        float magnitude = MathF.Pow(10, MathF.Floor(MathF.Log10(minimum)));
        float n = minimum / magnitude;
        return (n <= 1f ? 1f : n <= 2f ? 2f : n <= 5f ? 5f : 10f) * magnitude;
    }

    private static string Number(float value, int decimals) =>
        MathF.Round(value, decimals).ToString(decimals == 0 ? "0" : "0.##", CultureInfo.InvariantCulture).Replace("-", "−");

    private static void BuildPlot(BoardObject o, List<Prim> prims, List<ObjectLabel>? labels)
    {
        var f = FrameOf(o);
        float r = o.Width * 0.5f;
        float thin = MathF.Max(r * 0.25f, 0.5f);
        var gridColor = Theme.WithAlpha(o.Color, 0.18f);
        var axisColor = Theme.WithAlpha(o.Color, 0.85f);
        // Divisions at least 36 world units apart keep the numbers readable.
        float step = NiceCeil(36f * (f.XMax - f.XMin) / MathF.Max(f.U.Length(), 1f));
        float tick = MathF.Max(f.U.Length(), f.V.Length()) * 0.01f;
        var uDir = Direction(Vector2.Zero, f.U);
        var vDir = Direction(Vector2.Zero, f.V);

        for (float x = MathF.Ceiling(f.XMin / step) * step; x <= f.XMax + step * 1e-3f; x += step)
            prims.Add(Prim.Capsule(f.Map(x, f.YMin), f.Map(x, f.YMax), thin, gridColor));
        for (float y = MathF.Ceiling(f.YMin / step) * step; y <= f.YMax + step * 1e-3f; y += step)
            prims.Add(Prim.Capsule(f.Map(f.XMin, y), f.Map(f.XMax, y), thin, gridColor));

        // Axes go through zero when it is visible and along the frame edge otherwise.
        float axisY = Math.Clamp(0f, f.YMin, f.YMax);
        float axisX = Math.Clamp(0f, f.XMin, f.XMax);
        float axisWidth = MathF.Max(r * 0.5f, 1f);
        prims.Add(Prim.Capsule(f.Map(f.XMin, axisY), f.Map(f.XMax, axisY), axisWidth, axisColor));
        prims.Add(Prim.Capsule(f.Map(axisX, f.YMin), f.Map(axisX, f.YMax), axisWidth, axisColor));
        AddArrowHead(prims, f.Map(f.XMin, axisY), f.Map(f.XMax, axisY), tick * 2.5f, axisWidth, axisColor);
        AddArrowHead(prims, f.Map(axisX, f.YMin), f.Map(axisX, f.YMax), tick * 2.5f, axisWidth, axisColor);

        int decimals = step >= 1 ? 0 : step >= 0.1f ? 1 : 2;
        for (float x = MathF.Ceiling(f.XMin / step) * step; x < f.XMax - step * 0.5f; x += step)
        {
            var at = f.Map(x, axisY);
            prims.Add(Prim.Capsule(at - vDir * tick, at + vDir * tick, axisWidth, axisColor));
            if (MathF.Abs(x) > step * 1e-3f) labels?.Add(new ObjectLabel(at - vDir * tick * 3f, Number(x, decimals), axisColor));
        }

        for (float y = MathF.Ceiling(f.YMin / step) * step; y < f.YMax - step * 0.5f; y += step)
        {
            var at = f.Map(axisX, y);
            prims.Add(Prim.Capsule(at - uDir * tick, at + uDir * tick, axisWidth, axisColor));
            if (MathF.Abs(y) > step * 1e-3f) labels?.Add(new ObjectLabel(at - uDir * tick * 3f, Number(y, decimals), axisColor));
        }

        if (labels != null)
        {
            labels.Add(new ObjectLabel(f.Map(axisX, axisY) - (uDir + vDir) * tick * 2.5f, "0", axisColor));
            labels.Add(new ObjectLabel(f.Map(f.XMax, axisY) - vDir * tick * 3.5f, "x", axisColor));
            labels.Add(new ObjectLabel(f.Map(axisX, f.YMax) + uDir * tick * 3f, "y", axisColor));
        }

        var (functions, _) = Expression.ParseList(o.Text);
        for (int i = 0; i < functions.Count; i++)
            BuildCurve(functions[i], f, r, CurveColors[i % CurveColors.Length], prims);
    }

    private static void BuildCurve(Func<double, double> fn, PlotFrame f, float r, Vector4 color, List<Prim> prims)
    {
        const int samples = 600;
        float yRange = f.YMax - f.YMin;
        double prevX = f.XMin, prevY = Safe(fn, prevX);
        for (int i = 1; i <= samples; i++)
        {
            double x = f.XMin + (f.XMax - f.XMin) * i / samples;
            double y = Safe(fn, x);
            bool valid = double.IsFinite(prevY) && double.IsFinite(y) && Math.Abs(y - prevY) < yRange;
            if (valid && !(prevY > f.YMax && y > f.YMax) && !(prevY < f.YMin && y < f.YMin))
            {
                // Clip the segment to the frame's y range.
                double x0 = prevX, y0 = prevY, x1 = x, y1 = y;
                ClipY(ref x0, ref y0, x1, y1, f.YMin, f.YMax);
                ClipY(ref x1, ref y1, x0, y0, f.YMin, f.YMax);
                prims.Add(Prim.Capsule(f.Map(x0, y0), f.Map(x1, y1), r, color));
            }

            prevX = x;
            prevY = y;
        }
    }

    private static void ClipY(ref double x, ref double y, double otherX, double otherY, double yMin, double yMax)
    {
        double limit = y > yMax ? yMax : y < yMin ? yMin : double.NaN;
        if (double.IsNaN(limit) || Math.Abs(otherY - y) < 1e-12) return;
        double t = (limit - y) / (otherY - y);
        x += (otherX - x) * t;
        y = limit;
    }

    private static double Safe(Func<double, double> fn, double x)
    {
        try
        {
            return fn(x);
        }
        catch (ArithmeticException)
        {
            return double.NaN;
        }
    }

    public static (Vector2 Min, Vector2 Max) Bounds(BoardObject o)
    {
        var pts = o.Kind switch
        {
            ShapeKind.Plot or ShapeKind.Image => [..o.Points, o.Points[1] + o.Points[2] - o.Points[0]],
            ShapeKind.Circle => CircleBox(o.Points[0], Vector2.Distance(o.Points[0], o.Points[1])),
            ShapeKind.Ellipse => EllipsePoints(o.Points[0], o.Points[1] - o.Points[0], o.Points[2] - o.Points[0], 32),
            var kind when IsSolid(kind) => SolidOutlinePoints(o),
            _ => o.Points
        };
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var pt in pts)
        {
            min = Vector2.Min(min, pt);
            max = Vector2.Max(max, pt);
        }

        return (min, max);
    }

    private static Vector2[] CircleBox(Vector2 c, float radius) => [c - new Vector2(radius), c + new Vector2(radius)];

    private static Vector2[] SolidOutlinePoints(BoardObject o)
    {
        var prims = new List<Prim>();
        BuildSolid(o, prims);
        return prims.SelectMany(p => new[] { new Vector2(p.A.X, p.A.Y), new Vector2(p.A.Z, p.A.W) }).ToArray();
    }

    /// <summary>Distance from a world point to the drawn object, 0 inside a plot or an image.</summary>
    public static float Distance(BoardObject o, Vector2 point)
    {
        if (o.Kind is ShapeKind.Plot or ShapeKind.Image)
        {
            var u = o.Points[1] - o.Points[0];
            var v = o.Points[2] - o.Points[0];
            var local = point - o.Points[0];
            float s = Vector2.Dot(local, u) / u.LengthSquared();
            float t = Vector2.Dot(local, v) / v.LengthSquared();
            if (s is >= 0 and <= 1 && t is >= 0 and <= 1) return 0f;
        }

        var prims = new List<Prim>();
        Build(o, prims, null);
        float best = float.MaxValue;
        foreach (var prim in prims)
        {
            var a = new Vector2(prim.A.X, prim.A.Y);
            var b = new Vector2(prim.A.Z, prim.A.W);
            var ab = b - a;
            float h = ab.LengthSquared() < 1e-12f ? 0f : Math.Clamp(Vector2.Dot(point - a, ab) / ab.LengthSquared(), 0f, 1f);
            best = MathF.Min(best, Vector2.Distance(point, a + ab * h) - prim.B.X);
        }

        return MathF.Max(best, 0f);
    }

    /// <summary>Moves one control point while keeping the object what it is: a rectangle stays a rectangle, etc.</summary>
    public static BoardObject MoveControlPoint(BoardObject o, int index, Vector2 target)
    {
        var p = (Vector2[])o.Points.Clone();
        bool translateAll = index == 0 && (o.Kind is ShapeKind.Ellipse or ShapeKind.Circle or ShapeKind.Angle or ShapeKind.Plot || IsSolid(o.Kind));
        if (translateAll)
        {
            var delta = target - p[0];
            for (int i = 0; i < p.Length; i++) p[i] += delta;
        }
        else if (o.Kind == ShapeKind.Rectangle)
        {
            // The opposite corner stays, the neighbours slide along the rectangle's own sides.
            var opposite = p[(index + 2) % 4];
            foreach (int j in new[] { (index + 1) % 4, (index + 3) % 4 })
            {
                var side = Direction(opposite, p[j]);
                p[j] = opposite + side * Vector2.Dot(target - opposite, side);
            }

            p[index] = target;
        }
        else if (o.Kind == ShapeKind.Plot)
        {
            // Plot sides keep their direction so the axes stay perpendicular.
            var side = Direction(p[0], p[index]);
            p[index] = p[0] + side * MathF.Max(Vector2.Dot(target - p[0], side), 20f);
        }
        else
        {
            p[index] = target;
        }

        return o with { Points = p };
    }

    /// <summary>Creates an object from a drag between two world points.</summary>
    public static BoardObject FromDrag(ShapeKind kind, Vector2 start, Vector2 end, Vector4 color, float width)
    {
        var min = Vector2.Min(start, end);
        var max = Vector2.Max(start, end);
        var center = (min + max) * 0.5f;
        Vector2[] points = kind switch
        {
            ShapeKind.Rectangle => [new(min.X, min.Y), new(max.X, min.Y), new(max.X, max.Y), new(min.X, max.Y)],
            ShapeKind.Ellipse => [center, new(max.X, center.Y), new(center.X, max.Y)],
            ShapeKind.Triangle => [new(min.X, max.Y), new(max.X, max.Y), new(center.X, min.Y)],
            ShapeKind.Point => [end],
            ShapeKind.Angle => [start, end, start + Vector2.Transform(end - start, Matrix3x2.CreateRotation(-MathF.PI / 3f))],
            ShapeKind.Plot => [new(min.X, max.Y), new(max.X, max.Y), new(min.X, min.Y)],
            _ when IsSolid(kind) => SolidFromDrag(kind, min, max),
            _ => [start, end]
        };

        var o = new BoardObject(kind, points, color, width);
        if (kind != ShapeKind.Plot) return o;

        float half = MathF.Max(MathF.Round((max.X - min.X) / GridUnit * 0.5f), 1f);
        return o with { XMin = -half, XMax = half };
    }
}
