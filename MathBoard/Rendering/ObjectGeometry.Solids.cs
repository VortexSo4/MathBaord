using System.Numerics;
using MathBoard.Core;
using MathBoard.Graphics;

namespace MathBoard.Rendering;

/// <summary>
/// Solids drawn the way they are in school: an oblique projection where local x, y and z go along the vectors U, V
/// and W from the origin, z away from the viewer. Edges and curves on the far side are dashed. Visibility comes from
/// the viewing direction, so it stays right however the control points are dragged.
/// </summary>
public static partial class ObjectGeometry
{
    private const int CurveSegments = 96;

    private static readonly Vector3[] BoxVertices =
        [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)];

    private static readonly int[][] BoxFaces =
        [[0, 1, 2, 3], [4, 5, 6, 7], [0, 1, 5, 4], [3, 2, 6, 7], [0, 3, 7, 4], [1, 2, 6, 5]];

    private static readonly Vector3[] PrismVertices =
        [new(0, 0, 0), new(1, 0, 0), new(0.5f, 0, 1), new(0, 1, 0), new(1, 1, 0), new(0.5f, 1, 1)];

    private static readonly int[][] PrismFaces = [[0, 1, 2], [3, 4, 5], [0, 1, 4, 3], [1, 2, 5, 4], [2, 0, 3, 5]];

    private static readonly Vector3[] PyramidVertices =
        [new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1), new(0.5f, 1, 0.5f)];

    private static readonly int[][] PyramidFaces = [[0, 1, 2, 3], [0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4]];

    public static bool IsSolid(ShapeKind kind) =>
        kind is ShapeKind.Box or ShapeKind.Prism or ShapeKind.Pyramid or ShapeKind.Cylinder or ShapeKind.Cone or ShapeKind.Sphere;

    private readonly record struct SolidFrame(Vector2 Origin, Vector2 U, Vector2 V, Vector2 W)
    {
        public Vector2 Project(Vector3 p) => Origin + U * p.X + V * p.Y + W * p.Z;

        /// <summary>The direction of the viewer's rays: it projects to a single point and goes into the depth (z = 1).</summary>
        public Vector3 View()
        {
            float det = U.X * V.Y - U.Y * V.X;
            if (MathF.Abs(det) < 1e-6f) return Vector3.UnitZ;
            return new Vector3((V.X * W.Y - V.Y * W.X) / det, (U.Y * W.X - U.X * W.Y) / det, 1f);
        }
    }

    private static SolidFrame FrameOfSolid(BoardObject o)
    {
        var p = o.Points;
        return o.Kind switch
        {
            // The apex is stored as a point; V runs to it from the middle of the base.
            ShapeKind.Pyramid => new SolidFrame(p[0], p[1] - p[0], p[2] - p[0] - (p[1] - p[0] + p[3] - p[0]) * 0.5f, p[3] - p[0]),
            ShapeKind.Sphere => new SolidFrame(p[0], p[1] - p[0], new Vector2(p[1].Y - p[0].Y, p[0].X - p[1].X), p[2] - p[0]),
            _ => new SolidFrame(p[0], p[1] - p[0], p[2] - p[0], p[3] - p[0])
        };
    }

    private static void BuildSolid(BoardObject o, List<Prim> prims)
    {
        var f = FrameOfSolid(o);
        float r = o.Width * 0.5f;
        switch (o.Kind)
        {
            case ShapeKind.Box:
                Polyhedron(prims, f, BoxVertices, BoxFaces, r, o.Color);
                break;
            case ShapeKind.Prism:
                Polyhedron(prims, f, PrismVertices, PrismFaces, r, o.Color);
                break;
            case ShapeKind.Pyramid:
                Polyhedron(prims, f, PyramidVertices, PyramidFaces, r, o.Color);
                break;
            case ShapeKind.Cylinder:
                Cylinder(prims, f, r, o.Color);
                break;
            case ShapeKind.Cone:
                Cone(prims, f, r, o.Color);
                break;
            case ShapeKind.Sphere:
                Sphere(prims, f, r, o.Color);
                break;
        }
    }

    private static void Polyhedron(List<Prim> prims, SolidFrame f, Vector3[] vertices, int[][] faces, float r, Vector4 c)
    {
        var view = f.View();
        var center = vertices.Aggregate(Vector3.Zero, (a, v) => a + v) / vertices.Length;
        var visible = faces.Select(face =>
        {
            var normal = Vector3.Cross(vertices[face[1]] - vertices[face[0]], vertices[face[2]] - vertices[face[0]]);
            var faceCenter = face.Aggregate(Vector3.Zero, (a, i) => a + vertices[i]) / face.Length;
            if (Vector3.Dot(normal, faceCenter - center) < 0) normal = -normal;
            return Vector3.Dot(normal, view) < 0;
        }).ToArray();

        // An edge is seen when one of its two faces is.
        var edges = new Dictionary<(int, int), bool>();
        for (int i = 0; i < faces.Length; i++)
        {
            var face = faces[i];
            for (int k = 0; k < face.Length; k++)
            {
                int a = face[k], b = face[(k + 1) % face.Length];
                var key = (Math.Min(a, b), Math.Max(a, b));
                edges[key] = edges.GetValueOrDefault(key) || visible[i];
            }
        }

        foreach (var ((a, b), seen) in edges)
            Edge(prims, f.Project(vertices[a]), f.Project(vertices[b]), seen, r, c);
    }

    private static void Cylinder(List<Prim> prims, SolidFrame f, float r, Vector4 c)
    {
        var view = f.View();
        bool topSeen = view.Y < 0, bottomSeen = view.Y > 0;
        Rim(prims, f, 0f, angle => bottomSeen || SideSeen(angle), r, c);
        Rim(prims, f, 1f, angle => topSeen || SideSeen(angle), r, c);

        float silhouette = MathF.Atan2(-view.X, view.Z);
        foreach (float angle in new[] { silhouette, silhouette + MathF.PI })
            prims.Add(Prim.Capsule(f.Project(Circle(angle, 0f)), f.Project(Circle(angle, 1f)), r, c));
        return;

        bool SideSeen(float angle) => view.X * MathF.Cos(angle) + view.Z * MathF.Sin(angle) < 0;
    }

    private static void Cone(List<Prim> prims, SolidFrame f, float r, Vector4 c)
    {
        var view = f.View();
        bool baseSeen = view.Y > 0;
        Rim(prims, f, 0f, angle => baseSeen || view.X * MathF.Cos(angle) + view.Y + view.Z * MathF.Sin(angle) < 0, r, c);

        // Generators where the side turns away: X cos angle + Z sin angle = -Y; none when the apex is seen inside the base.
        float length = MathF.Sqrt(view.X * view.X + view.Z * view.Z);
        if (length < 1e-6f || MathF.Abs(view.Y) > length) return;
        float phase = MathF.Atan2(view.Z, view.X), spread = MathF.Acos(-view.Y / length);
        var apex = f.Project(new Vector3(0, 1, 0));
        foreach (float angle in new[] { phase - spread, phase + spread })
            prims.Add(Prim.Capsule(f.Project(Circle(angle, 0f)), apex, r, c));
    }

    private static void Sphere(List<Prim> prims, SolidFrame f, float r, Vector4 c)
    {
        var view = Vector3.Normalize(f.View());
        // The outline is the great circle across the viewing direction.
        var e1 = Vector3.Normalize(Vector3.Cross(view, MathF.Abs(view.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var e2 = Vector3.Cross(view, e1);
        var outline = new Vector2[CurveSegments];
        for (int i = 0; i < CurveSegments; i++)
        {
            float t = i * MathF.Tau / CurveSegments;
            outline[i] = f.Project(e1 * MathF.Cos(t) + e2 * MathF.Sin(t));
        }

        Polyline(prims, outline, true, r, c);
        Rim(prims, f, 0f, angle => Vector3.Dot(Circle(angle, 0f), view) < 0, r, c);
    }

    private static Vector3 Circle(float angle, float y) => new(MathF.Cos(angle), y, MathF.Sin(angle));

    /// <summary>The circle at height y, solid where <paramref name="seen"/> holds and dashed elsewhere.</summary>
    private static void Rim(List<Prim> prims, SolidFrame f, float y, Func<float, bool> seen, float r, Vector4 c)
    {
        var run = new List<Vector2> { f.Project(Circle(0f, y)) };
        bool runSeen = seen(MathF.PI / CurveSegments);
        for (int i = 0; i < CurveSegments; i++)
        {
            float a = i * MathF.Tau / CurveSegments, b = (i + 1) * MathF.Tau / CurveSegments;
            bool segmentSeen = seen((a + b) * 0.5f);
            if (segmentSeen != runSeen)
            {
                Line(prims, run, runSeen, r, c);
                run = [run[^1]];
                runSeen = segmentSeen;
            }

            run.Add(f.Project(Circle(b, y)));
        }

        Line(prims, run, runSeen, r, c);
    }

    private static void Edge(List<Prim> prims, Vector2 a, Vector2 b, bool seen, float r, Vector4 c) => Line(prims, [a, b], seen, r, c);

    /// <summary>A solid polyline, or a thinner dashed one for what is hidden; dashes follow the length of the line.</summary>
    private static void Line(List<Prim> prims, IReadOnlyList<Vector2> points, bool seen, float r, Vector4 c)
    {
        if (seen)
        {
            Polyline(prims, points, false, r, c);
            return;
        }

        float dash = MathF.Max(r * 5f, 6f);
        Dashes(prims, points, MathF.Max(r * 0.6f, 0.75f), c, dash, dash * 1.8f);
    }

    /// <summary>Control points of a solid dragged out over a box, which it fills.</summary>
    private static Vector2[] SolidFromDrag(ShapeKind kind, Vector2 min, Vector2 max)
    {
        float w = max.X - min.X, h = max.Y - min.Y;
        var corner = new Vector2(min.X, max.Y);
        // A shallower prism keeps its hidden back edge inside the front face, as in textbooks.
        float depth = (kind == ShapeKind.Prism ? 0.2f : 0.3f) * MathF.Min(w, h);
        float ry = MathF.Min(w * 0.25f, h / 3f);
        var baseCenter = new Vector2(min.X + w * 0.5f, max.Y - ry);
        return kind switch
        {
            ShapeKind.Pyramid => [corner, corner + new Vector2(w - depth, 0), new(min.X + w * 0.5f, min.Y), corner + new Vector2(depth, -depth)],
            ShapeKind.Cylinder => [baseCenter, baseCenter + new Vector2(w * 0.5f, 0), new(baseCenter.X, min.Y + ry), baseCenter - new Vector2(0, ry)],
            ShapeKind.Cone => [baseCenter, baseCenter + new Vector2(w * 0.5f, 0), new(baseCenter.X, min.Y), baseCenter - new Vector2(0, ry)],
            ShapeKind.Sphere => SphereFromDrag((min + max) * 0.5f, MathF.Min(w, h) * 0.5f),
            _ => [corner, corner + new Vector2(w - depth, 0), corner - new Vector2(0, h - depth), corner + new Vector2(depth, -depth)]
        };
    }

    private static Vector2[] SphereFromDrag(Vector2 center, float radius) =>
        [center, center + new Vector2(radius, 0), center - new Vector2(0, radius * 0.3f)];
}
