using System.Numerics;
using MathBoard.Core;
using MathBoard.Graphics;

namespace MathBoard.Rendering;

/// <summary>
/// Solids as bodies in space (<see cref="Solid3D"/>), drawn in parallel projection. A solid's surface is a set of convex
/// faces: flat ones, and strips or triangles of a curved side (64 around), which share a smooth group. An edge between
/// different faces is drawn, solid when one of its faces turns to the viewer and dashed otherwise; on a curved side only
/// the outline is (where its strips turn away). A sphere is exact: its outline is a circle, its equator is drawn. Any
/// other line on or in the solid (the section, the lines drawn on it) is seen where it lies on a face turned to the
/// viewer and dashed where it goes behind or through the inside. A solid can be cut by its section's plane: then only one
/// part is left, closed by the section as a face.
/// </summary>
public static partial class ObjectGeometry
{
    private const int RoundSegments = 64;
    // A board unit per unit of the solid when its arrow (see BoardObject.Solid) is this long.
    private const float SolidArrow = 100f;

    /// <summary>The surface of a solid centred on the origin: vertices and convex faces with their outward planes.</summary>
    public sealed class SolidMesh
    {
        public readonly Vector3[] Vertices;
        public readonly int[][] Faces;
        // 0 for a flat face; faces of one curved surface share a number above 0.
        public readonly int[] Groups;
        public readonly Vector3[] Normals;
        public readonly float[] Offsets;
        // Each edge once, with its two faces.
        public readonly (int A, int B, int F1, int F2)[] Edges;

        /// <param name="inside">A point inside the solid; the origin unless the solid was cut.</param>
        public SolidMesh(Vector3[] vertices, int[][] faces, int[] groups, Vector3 inside = default)
        {
            Vertices = vertices;
            Faces = faces;
            Groups = groups;
            Normals = new Vector3[faces.Length];
            Offsets = new float[faces.Length];
            for (int i = 0; i < faces.Length; i++)
            {
                var f = faces[i];
                // Newell's normal, which a cut face with points close together does not spoil.
                var n = Vector3.Zero;
                for (int k = 0; k < f.Length; k++)
                {
                    var a = vertices[f[k]];
                    var b = vertices[f[(k + 1) % f.Length]];
                    n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
                }

                n = Vector3.Normalize(n);
                var center = f.Aggregate(Vector3.Zero, (a, k) => a + vertices[k]) / f.Length;
                // The solid is convex, so the outward normal points away from a point inside it.
                if (Vector3.Dot(n, center - inside) < 0) n = -n;
                Normals[i] = n;
                Offsets[i] = Vector3.Dot(n, center);
            }

            var edges = new Dictionary<(int, int), (int, int)>();
            for (int i = 0; i < faces.Length; i++)
            for (int k = 0; k < faces[i].Length; k++)
            {
                int a = faces[i][k], b = faces[i][(k + 1) % faces[i].Length];
                var key = (Math.Min(a, b), Math.Max(a, b));
                edges[key] = edges.TryGetValue(key, out var e) ? (e.Item1, i) : (i, -1);
            }

            Edges = edges.Select(e => (e.Key.Item1, e.Key.Item2, e.Value.Item1, e.Value.Item2)).ToArray();
        }

        /// <summary>Whether the edge is a real one (not between two strips of one curved surface).</summary>
        public bool IsSharp(int f1, int f2) => f2 < 0 || Groups[f1] == 0 || Groups[f1] != Groups[f2];
    }

    private static readonly Dictionary<(ShapeKind, Vector3), SolidMesh> Meshes = new();

    /// <summary>The surface of a solid of this kind and size (null for a sphere, which is drawn exactly).</summary>
    public static SolidMesh? MeshOf(ShapeKind kind, Vector3 size)
    {
        if (kind == ShapeKind.Sphere) return null;
        if (Meshes.TryGetValue((kind, size), out var mesh)) return mesh;
        if (Meshes.Count > 256) Meshes.Clear();
        var h = size / 2f;
        mesh = kind switch
        {
            // A rectangular face towards the viewer, the third edge at the back.
            ShapeKind.Prism => new SolidMesh(
                [new(-h.X, -h.Y, h.Z), new(h.X, -h.Y, h.Z), new(0, -h.Y, -h.Z), new(-h.X, h.Y, h.Z), new(h.X, h.Y, h.Z), new(0, h.Y, -h.Z)],
                [[0, 1, 2], [3, 4, 5], [0, 1, 4, 3], [1, 2, 5, 4], [2, 0, 3, 5]], new int[5]),
            ShapeKind.Pyramid => new SolidMesh(
                [new(-h.X, -h.Y, -h.Z), new(h.X, -h.Y, -h.Z), new(h.X, -h.Y, h.Z), new(-h.X, -h.Y, h.Z), new(0, h.Y, 0)],
                [[0, 1, 2, 3], [0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4]], new int[5]),
            ShapeKind.Cylinder => Round(h, cone: false),
            ShapeKind.Cone => Round(h, cone: true),
            _ => new SolidMesh(
                [new(-h.X, -h.Y, -h.Z), new(h.X, -h.Y, -h.Z), new(h.X, h.Y, -h.Z), new(-h.X, h.Y, -h.Z),
                 new(-h.X, -h.Y, h.Z), new(h.X, -h.Y, h.Z), new(h.X, h.Y, h.Z), new(-h.X, h.Y, h.Z)],
                [[0, 1, 2, 3], [4, 5, 6, 7], [0, 1, 5, 4], [3, 2, 6, 7], [0, 3, 7, 4], [1, 2, 6, 5]], new int[6])
        };
        Meshes[(kind, size)] = mesh;
        return mesh;
    }

    /// <summary>A cylinder or cone around the vertical axis: two caps (or a base and an apex) and the side in strips.</summary>
    private static SolidMesh Round(Vector3 h, bool cone)
    {
        int n = RoundSegments;
        var vertices = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            float t = i * MathF.Tau / n;
            vertices.Add(new Vector3(h.X * MathF.Cos(t), -h.Y, h.Z * MathF.Sin(t)));
        }

        var faces = new List<int[]> { Enumerable.Range(0, n).ToArray() };
        var groups = new List<int> { 0 };
        if (cone)
        {
            vertices.Add(new Vector3(0, h.Y, 0));
            for (int i = 0; i < n; i++)
            {
                faces.Add([i, (i + 1) % n, n]);
                groups.Add(1);
            }
        }
        else
        {
            for (int i = 0; i < n; i++) vertices.Add(vertices[i] with { Y = h.Y });
            faces.Add(Enumerable.Range(n, n).ToArray());
            groups.Add(0);
            for (int i = 0; i < n; i++)
            {
                faces.Add([i, (i + 1) % n, n + (i + 1) % n, n + i]);
                groups.Add(1);
            }
        }

        return new SolidMesh(vertices.ToArray(), faces.ToArray(), groups.ToArray());
    }

    private static readonly Dictionary<(ShapeKind, Vector3, Vector3, float), SolidMesh> CutMeshes = new();

    /// <summary>The plane of a section as a unit normal and offset; null for three points on a line.</summary>
    public static (Vector3 Normal, float Offset)? PlaneOf(SolidSection section)
    {
        var normal = Vector3.Cross(section.B - section.A, section.C - section.A);
        if (normal.LengthSquared() < 1e-8f) return null;
        normal = Vector3.Normalize(normal);
        return (normal, Vector3.Dot(normal, section.A));
    }

    /// <summary>The plane a cut solid is cut by, turned so the part left is where n·p ≤ offset; null for a whole solid.</summary>
    private static (Vector3 Normal, float Offset)? CutPlane(BoardObject o) =>
        o.Solid?.Section is { Cut: not 0 } s && PlaneOf(s) is { } p ? (p.Normal * s.Cut, p.Offset * s.Cut) : null;

    /// <summary>The surface of the solid as it is drawn: what is left of it when it is cut; null for a sphere.</summary>
    public static SolidMesh? BodyOf(BoardObject o)
    {
        var solid = o.Solid!;
        var mesh = MeshOf(o.Kind, solid.Size);
        if (mesh == null || CutPlane(o) is not { } plane) return mesh;
        var key = (o.Kind, solid.Size, plane.Normal, plane.Offset);
        if (CutMeshes.TryGetValue(key, out var cut)) return cut;
        if (CutMeshes.Count > 256) CutMeshes.Clear();
        return CutMeshes[key] = Clip(mesh, plane.Normal, plane.Offset, SurfaceTolerance(solid.Size) * 0.25f);
    }

    /// <summary>The part of a convex surface where n·p ≤ offset, closed by a face in the plane.</summary>
    private static SolidMesh Clip(SolidMesh mesh, Vector3 n, float offset, float eps)
    {
        var d = mesh.Vertices.Select(v => Vector3.Dot(n, v) - offset).ToArray();
        var vertices = new List<Vector3>();
        // A vertex left as it was is (a, a); a point where the edge a-b crosses the plane is (a, b).
        var index = new Dictionary<(int, int), int>();
        int At(int a, int b)
        {
            var key = (Math.Min(a, b), Math.Max(a, b));
            if (index.TryGetValue(key, out int i)) return i;
            index[key] = vertices.Count;
            vertices.Add(a == b ? mesh.Vertices[a] : Vector3.Lerp(mesh.Vertices[a], mesh.Vertices[b], d[a] / (d[a] - d[b])));
            return vertices.Count - 1;
        }

        var faces = new List<int[]>();
        var groups = new List<int>();
        var cap = new HashSet<int>();
        for (int f = 0; f < mesh.Faces.Length; f++)
        {
            var face = mesh.Faces[f];
            // A face in the plane itself: the new face takes its place.
            if (face.All(k => MathF.Abs(d[k]) <= eps)) continue;
            var left = new List<int>();
            for (int i = 0; i < face.Length; i++)
            {
                int a = face[i], b = face[(i + 1) % face.Length];
                if (d[a] <= eps)
                {
                    left.Add(At(a, a));
                    if (d[a] >= -eps) cap.Add(left[^1]);
                }

                if ((d[a] < -eps && d[b] > eps) || (d[a] > eps && d[b] < -eps))
                {
                    left.Add(At(a, b));
                    cap.Add(left[^1]);
                }
            }

            if (left.Count < 3) continue;
            faces.Add(left.ToArray());
            groups.Add(mesh.Groups[f]);
        }

        if (cap.Count >= 3)
        {
            var middle = cap.Aggregate(Vector3.Zero, (s, k) => s + vertices[k]) / cap.Count;
            var (u, w) = PlaneAxes(n);
            faces.Add(cap.OrderBy(k => MathF.Atan2(Vector3.Dot(vertices[k] - middle, w), Vector3.Dot(vertices[k] - middle, u))).ToArray());
            groups.Add(0);
        }

        var inside = vertices.Aggregate(Vector3.Zero, (s, v) => s + v) / vertices.Count;
        return new SolidMesh(vertices.ToArray(), faces.ToArray(), groups.ToArray(), inside);
    }

    /// <summary>How a solid lies on the board: its turn in space, then the parallel projection onto the board.</summary>
    public readonly record struct SolidView(Matrix4x4 Rotation, Vector2 Origin, Vector2 AxisX)
    {
        // Up on the board for the solid's y: the arrow turned a quarter back (the board's y points down).
        private Vector2 AxisY => new(AxisX.Y, -AxisX.X);

        public Vector3 Turn(Vector3 p) => Vector3.Transform(p, Rotation);

        /// <summary>Whether a direction of the solid (a face's normal) points towards the viewer.</summary>
        public bool Faces(Vector3 normal) => Turn(normal).Z > 0;

        public Vector2 Project(Vector3 p)
        {
            var q = Turn(p);
            return Origin + AxisX * q.X + AxisY * q.Y;
        }

        /// <summary>A board point as the turned solid's x and y (the depth is not known).</summary>
        public Vector2 Unproject(Vector2 board)
        {
            var d = board - Origin;
            float s2 = AxisX.LengthSquared();
            return new Vector2(Vector2.Dot(d, AxisX), Vector2.Dot(d, AxisY)) / s2;
        }

        /// <summary>The solid's own point that is at turned x, y, z.</summary>
        public Vector3 Untwist(Vector3 q) => Vector3.Transform(q, Matrix4x4.Transpose(Rotation));

        public float Scale => AxisX.Length();
    }

    public static SolidView ViewOf(BoardObject o)
    {
        var s = o.Solid!;
        return new SolidView(Matrix4x4.CreateRotationY(s.Yaw) * Matrix4x4.CreateRotationX(s.Pitch), o.Points[0],
            (o.Points[1] - o.Points[0]) / SolidArrow);
    }

    // ------------------------------------------------------------- where points are seen

    private static float SurfaceTolerance(Vector3 size) => MathF.Max(size.X, MathF.Max(size.Y, size.Z)) * 0.004f;

    /// <summary>
    /// Whether a point of the solid is seen: it lies on a face turned to the viewer (or, on an edge, on one of two). A
    /// point inside or on the far side is hidden.
    /// </summary>
    public static bool Seen(BoardObject o, SolidView view, SolidMesh? mesh, Vector3 p)
    {
        var size = o.Solid!.Size;
        float eps = SurfaceTolerance(size);
        if (mesh == null)
        {
            float radius = size.X / 2f;
            // On the flat face of a cut sphere the point is seen when that face turns to the viewer.
            if (CutPlane(o) is { } cut && MathF.Abs(Vector3.Dot(cut.Normal, p) - cut.Offset) <= eps && view.Faces(cut.Normal)) return true;
            return p.Length() > radius - eps * 3f && view.Turn(p).Z > -eps;
        }

        for (int i = 0; i < mesh.Normals.Length; i++)
            if (Vector3.Dot(mesh.Normals[i], p) - mesh.Offsets[i] > -eps && view.Faces(mesh.Normals[i]))
                return true;
        return false;
    }

    /// <summary>A line of the solid's own points, solid where it is seen and dashed where it is hidden; none on a part cut away.</summary>
    private static void SolidLine(List<Prim> prims, BoardObject o, SolidView view, SolidMesh? mesh, IReadOnlyList<Vector3> points,
        bool closed, float r, Vector4 c)
    {
        var size = o.Solid!.Size;
        float step = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / 60f;
        var run = new List<Vector2>();
        bool runSeen = false;
        var cut = CutPlane(o);
        float eps = SurfaceTolerance(size);
        int count = closed ? points.Count : points.Count - 1;
        for (int i = 0; i < count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            int pieces = Math.Clamp((int)MathF.Ceiling(Vector3.Distance(a, b) / step), 1, 200);
            for (int k = 0; k < pieces; k++)
            {
                var from = Vector3.Lerp(a, b, k / (float)pieces);
                var to = Vector3.Lerp(a, b, (k + 1) / (float)pieces);
                if (cut is { } plane && Vector3.Dot(plane.Normal, (from + to) * 0.5f) - plane.Offset > eps)
                {
                    if (run.Count > 1) Line(prims, run, runSeen, r, c);
                    run = [];
                    continue;
                }

                bool seen = Seen(o, view, mesh, (from + to) * 0.5f);
                if (run.Count == 0)
                {
                    run.Add(view.Project(from));
                    runSeen = seen;
                }
                else if (seen != runSeen)
                {
                    Line(prims, run, runSeen, r, c);
                    run = [run[^1]];
                    runSeen = seen;
                }

                run.Add(view.Project(to));
            }
        }

        if (run.Count > 1) Line(prims, run, runSeen, r, c);
    }

    // ------------------------------------------------------------- drawing

    private static void BuildSolid3D(BoardObject o, List<Prim> prims)
    {
        var solid = o.Solid!;
        var view = ViewOf(o);
        var mesh = BodyOf(o);
        float r = o.Width * 0.5f;
        // The section's fill goes first, so the edges behind and in front of it are all drawn over it.
        var section = solid.Section;
        var polygon = section != null ? SectionPolygon(o, section) : null;
        if (section != null && polygon is { Count: >= 3 })
            Pattern(prims, [polygon.Select(view.Project).ToArray()], section.Style, section.Spacing, section.Color);
        if (mesh == null) SphereBody(prims, o, view, r);
        else
        {
            var lines = new List<(int A, int B, bool Seen)>();
            foreach (var (a, b, f1, f2) in mesh.Edges)
            {
                bool front1 = view.Faces(mesh.Normals[f1]), front2 = f2 >= 0 && view.Faces(mesh.Normals[f2]);
                if (mesh.IsSharp(f1, f2)) lines.Add((a, b, front1 || front2));
                // On a curved side only the outline, where the strips turn away from the viewer.
                else if (front1 != front2) lines.Add((a, b, true));
            }

            foreach (var (chain, seen) in Chains(lines))
                Line(prims, chain.Select(i => view.Project(mesh.Vertices[i])).ToList(), seen, r, o.Color);
        }

        // The outline is seen on the faces turned to the viewer.
        if (section != null && polygon is { Count: >= 3 }) SolidLine(prims, o, view, mesh, polygon, true, r, section.Color);

        foreach (var mark in solid.Marks)
            if (mark.Points.Length > 1)
                SolidLine(prims, o, view, mesh, mark.Points, false, r, mark.Color);
    }

    /// <summary>
    /// Edges joined end to end into lines of one kind (seen or hidden), so the dashes of a rim made of many short edges
    /// run on along it instead of starting again on each.
    /// </summary>
    private static List<(List<int> Chain, bool Seen)> Chains(List<(int A, int B, bool Seen)> edges)
    {
        var result = new List<(List<int>, bool)>();
        var used = new bool[edges.Count];
        var at = new Dictionary<(int, bool), List<int>>();
        for (int i = 0; i < edges.Count; i++)
        {
            foreach (int v in new[] { edges[i].A, edges[i].B })
            {
                if (!at.TryGetValue((v, edges[i].Seen), out var list)) at[(v, edges[i].Seen)] = list = [];
                list.Add(i);
            }
        }

        for (int i = 0; i < edges.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var chain = new LinkedList<int>([edges[i].A, edges[i].B]);
            bool seen = edges[i].Seen;
            // Grows at both ends while the end vertex goes on in exactly one more edge of the same kind.
            foreach (bool atEnd in new[] { true, false })
            {
                while (true)
                {
                    int v = atEnd ? chain.Last!.Value : chain.First!.Value;
                    var next = at[(v, seen)].Where(k => !used[k]).ToList();
                    if (next.Count != 1 || at[(v, seen)].Count != 2) break;
                    used[next[0]] = true;
                    int w = edges[next[0]].A == v ? edges[next[0]].B : edges[next[0]].A;
                    if (atEnd) chain.AddLast(w);
                    else chain.AddFirst(w);
                }
            }

            result.Add((chain.ToList(), seen));
        }

        return result;
    }

    private static void SphereBody(List<Prim> prims, BoardObject o, SolidView view, float r)
    {
        float radius = o.Solid!.Size.X / 2f;
        var center = view.Project(Vector3.Zero);
        float projected = radius * view.Scale;
        if (CutPlane(o) == null) Polyline(prims, EllipsePoints(center, new Vector2(projected, 0), new Vector2(0, projected), 128), true, r, o.Color);
        else
        {
            // What is left of the outline: the circle of the sphere seen edge-on.
            var outline = Enumerable.Range(0, 128).Select(i => view.Untwist(new Vector3(MathF.Cos(i * MathF.Tau / 128), MathF.Sin(i * MathF.Tau / 128), 0) * radius)).ToList();
            SolidLine(prims, o, view, null, outline, true, r, o.Color);
        }

        var equator = Enumerable.Range(0, 128).Select(i => new Vector3(radius * MathF.Cos(i * MathF.Tau / 128), 0, radius * MathF.Sin(i * MathF.Tau / 128))).ToList();
        SolidLine(prims, o, view, null, equator, true, r, o.Color);
    }

    /// <summary>Whether the board point lies on the solid's section as it is seen.</summary>
    public static bool OnSection(BoardObject o, Vector2 point)
    {
        if (o.Solid?.Section is not { } section || SectionPolygon(o, section) is not { Count: >= 3 } polygon) return false;
        var view = ViewOf(o);
        var outline = polygon.Select(view.Project).ToArray();
        bool inside = false;
        for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
        {
            var (a, b) = (outline[i], outline[j]);
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y)) inside = !inside;
        }

        return inside;
    }

    /// <summary>The section of the whole solid by the plane of its three points, as a convex polygon of its own points.</summary>
    public static List<Vector3>? SectionPolygon(BoardObject o, SolidSection section)
    {
        if (PlaneOf(section) is not { } plane) return null;
        var (normal, offset) = plane;
        var size = o.Solid!.Size;
        var points = new List<Vector3>();
        if (MeshOf(o.Kind, size) is not { } mesh)
        {
            // A sphere is cut in a circle around the foot of the perpendicular from its centre.
            float radius = size.X / 2f;
            if (MathF.Abs(offset) >= radius) return null;
            var foot = normal * offset;
            float rho = MathF.Sqrt(radius * radius - offset * offset);
            var (u, w) = PlaneAxes(normal);
            for (int i = 0; i < 96; i++)
            {
                float t = i * MathF.Tau / 96;
                points.Add(foot + (u * MathF.Cos(t) + w * MathF.Sin(t)) * rho);
            }

            return points;
        }

        float eps = SurfaceTolerance(size) * 0.25f;
        foreach (var (a, b, _, _) in mesh.Edges)
        {
            float da = Vector3.Dot(normal, mesh.Vertices[a]) - offset, db = Vector3.Dot(normal, mesh.Vertices[b]) - offset;
            if (MathF.Abs(da) < eps) points.Add(mesh.Vertices[a]);
            if (MathF.Abs(db) < eps) points.Add(mesh.Vertices[b]);
            if (da * db < 0 && MathF.Abs(da) >= eps && MathF.Abs(db) >= eps)
                points.Add(Vector3.Lerp(mesh.Vertices[a], mesh.Vertices[b], da / (da - db)));
        }

        if (points.Count < 3) return null;
        // The points go round their middle in the plane.
        var middle = points.Aggregate(Vector3.Zero, (s, p) => s + p) / points.Count;
        var (pu, pw) = PlaneAxes(normal);
        var sorted = points.OrderBy(p => MathF.Atan2(Vector3.Dot(p - middle, pw), Vector3.Dot(p - middle, pu))).ToList();
        var distinct = new List<Vector3>();
        foreach (var p in sorted)
            if (distinct.Count == 0 || Vector3.Distance(p, distinct[^1]) > eps * 4f)
                distinct.Add(p);
        if (distinct.Count > 1 && Vector3.Distance(distinct[0], distinct[^1]) <= eps * 4f) distinct.RemoveAt(distinct.Count - 1);
        return distinct.Count >= 3 ? distinct : null;
    }

    private static (Vector3 U, Vector3 W) PlaneAxes(Vector3 normal)
    {
        var u = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        return (u, Vector3.Cross(normal, u));
    }

    private static Vector2[] Solid3DOutlinePoints(BoardObject o)
    {
        var view = ViewOf(o);
        if (BodyOf(o) is { } mesh) return mesh.Vertices.Select(view.Project).ToArray();
        float projected = o.Solid!.Size.X / 2f * view.Scale;
        var center = view.Project(Vector3.Zero);
        return [center - new Vector2(projected), center + new Vector2(projected)];
    }

    // ------------------------------------------------------------- picking points on a solid

    /// <summary>
    /// The solid's point at a board point, to build on: a vertex, the middle of an edge, the centre of a face or of the
    /// solid within <paramref name="snap"/> board units, else a point of an edge within it, else the point of the face
    /// turned to the viewer under it. Hidden vertices and edges count too, as they do in textbook problems.
    /// </summary>
    public static Vector3? PickOnSolid(BoardObject o, Vector2 board, float snap, bool middles)
    {
        var view = ViewOf(o);
        var size = o.Solid!.Size;
        var mesh = BodyOf(o);
        var cut = CutPlane(o);
        var special = new List<Vector3>();
        if (middles && cut is not { Offset: < 0 }) special.Add(Vector3.Zero);
        if (mesh != null)
        {
            special.AddRange(mesh.Vertices.Where((_, i) => mesh.Edges.Any(e => (e.A == i || e.B == i) && mesh.IsSharp(e.F1, e.F2))));
            if (middles)
                special.AddRange(mesh.Edges.Where(e => mesh.IsSharp(e.F1, e.F2) && mesh.Groups[e.F1] == 0 && (e.F2 < 0 || mesh.Groups[e.F2] == 0))
                .Select(e => (mesh.Vertices[e.A] + mesh.Vertices[e.B]) / 2f));
            if (middles)
                special.AddRange(mesh.Faces.Where((_, i) => mesh.Groups[i] == 0)
                .Select(f => f.Aggregate(Vector3.Zero, (s, k) => s + mesh.Vertices[k]) / f.Length));
        }

        // The nearest on the board; of two about as near, the one in front.
        var best = special.Select(p => (Point: p, Distance: Vector2.Distance(view.Project(p), board), Depth: view.Turn(p).Z))
            .Where(t => t.Distance <= snap).OrderBy(t => MathF.Round(t.Distance / 3f)).ThenByDescending(t => t.Depth).FirstOrDefault();
        if (best.Distance <= snap && best != default) return best.Point;

        if (mesh != null)
        {
            Vector3? onEdge = null;
            float nearest = snap;
            foreach (var (a, b, f1, f2) in mesh.Edges)
            {
                if (!mesh.IsSharp(f1, f2)) continue;
                var pa = view.Project(mesh.Vertices[a]);
                var pb = view.Project(mesh.Vertices[b]);
                var ab = pb - pa;
                float t = ab.LengthSquared() < 1e-9f ? 0f : Math.Clamp(Vector2.Dot(board - pa, ab) / ab.LengthSquared(), 0f, 1f);
                float d = Vector2.Distance(board, pa + ab * t);
                if (d >= nearest) continue;
                nearest = d;
                onEdge = Vector3.Lerp(mesh.Vertices[a], mesh.Vertices[b], t);
            }

            if (onEdge != null) return onEdge;
        }
        else
        {
            // The sphere's equator works as an edge, and so does the rim of a cut sphere.
            float radius = size.X / 2f;
            var rim = cut != null ? SectionPolygon(o, o.Solid.Section!) ?? [] : [];
            float eps = SurfaceTolerance(size);
            var along = Enumerable.Range(0, 360).Select(i => new Vector3(radius * MathF.Cos(i * MathF.PI / 180), 0, radius * MathF.Sin(i * MathF.PI / 180)))
                .Where(p => cut is not { } c || Vector3.Dot(c.Normal, p) - c.Offset <= eps).Concat(rim);
            foreach (var p in along)
                if (Vector2.Distance(view.Project(p), board) <= snap * 0.6f) return p;
        }

        return FaceAt(o, view, mesh, board);
    }

    /// <summary>Whether the board point is on a solid in space as it is seen (on any of its faces, not only its lines).</summary>
    public static bool OnBody(BoardObject o, Vector2 board) => o.Solid != null && FaceAt(o, ViewOf(o), BodyOf(o), board) != null;

    /// <summary>The point of the solid's surface turned to the viewer under the board point; null if there is none.</summary>
    public static Vector3? FaceAt(BoardObject o, SolidView view, SolidMesh? mesh, Vector2 board)
    {
        var q = view.Unproject(board);
        if (mesh == null)
        {
            float radius = o.Solid!.Size.X / 2f;
            float z2 = radius * radius - q.LengthSquared();
            Vector3? ahead = z2 < 0 ? null : view.Untwist(new Vector3(q, MathF.Sqrt(z2)));
            if (CutPlane(o) is not { } cut) return ahead;
            float eps = SurfaceTolerance(o.Solid.Size);
            if (ahead is { } f && Vector3.Dot(cut.Normal, f) - cut.Offset <= eps) return f;
            // Where the sphere in front is cut away, the flat face is in front, if it turns to the viewer.
            var n = view.Turn(cut.Normal);
            if (n.Z <= 1e-4f) return null;
            var onCap = view.Untwist(new Vector3(q, (cut.Offset - n.X * q.X - n.Y * q.Y) / n.Z));
            return onCap.Length() <= radius + eps ? onCap : null;
        }

        Vector3? found = null;
        float front = float.MinValue;
        for (int i = 0; i < mesh.Faces.Length; i++)
        {
            var n = view.Turn(mesh.Normals[i]);
            if (n.Z <= 1e-4f) continue;
            float z = (mesh.Offsets[i] - n.X * q.X - n.Y * q.Y) / n.Z;
            var p = view.Untwist(new Vector3(q, z));
            // On the face if it is on the inner side of every other face's plane.
            bool inside = true;
            float eps = SurfaceTolerance(o.Solid!.Size);
            for (int k = 0; k < mesh.Faces.Length && inside; k++)
                if (Vector3.Dot(mesh.Normals[k], p) - mesh.Offsets[k] > eps) inside = false;
            if (!inside || z <= front) continue;
            front = z;
            found = p;
        }

        return found;
    }

    // ------------------------------------------------------------- sizing

    private const float MinSolidSide = 10f;

    /// <summary>The solid's own axis x, y or z, pointing to the face of it turned to the viewer.</summary>
    private static Vector3 AxisEnd(SolidView view, int axis)
    {
        var e = axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
        return view.Turn(e).Z >= 0 ? e : -e;
    }

    /// <summary>
    /// A solid's handles: its middle, which moves it, then the middles of its faces towards the viewer along its x, y and
    /// z, which size it with the opposite face kept in place; a sphere has one for its radius.
    /// </summary>
    private static Vector2[] Solid3DHandles(BoardObject o)
    {
        var view = ViewOf(o);
        var h = o.Solid!.Size / 2f;
        if (o.Kind == ShapeKind.Sphere) return [o.Points[0], o.Points[0] + view.AxisX * h.X];
        return [o.Points[0], ..Enumerable.Range(0, 3).Select(k => view.Project(AxisEnd(view, k) * h))];
    }

    /// <summary>Sizes the solid by one of its handles; the section and the lines on it stretch with it.</summary>
    private static BoardObject ResizeSolid3D(BoardObject o, int index, Vector2 target)
    {
        var solid = o.Solid!;
        var view = ViewOf(o);
        var size = solid.Size;
        Vector3 newSize;
        var shift = Vector2.Zero;
        if (o.Kind == ShapeKind.Sphere) newSize = new Vector3(MathF.Max(Vector2.Distance(target, o.Points[0]) / view.Scale * 2f, MinSolidSide));
        else
        {
            int k = index - 1;
            var d = view.Project(AxisEnd(view, k)) - view.Project(Vector3.Zero);
            if (d.LengthSquared() < 1e-4f) return o;
            float old = k switch { 0 => size.X, 1 => size.Y, _ => size.Z };
            var opposite = o.Points[0] - d * old / 2f;
            float length = MathF.Max(Vector2.Dot(target - opposite, d) / d.LengthSquared(), MinSolidSide);
            newSize = k switch { 0 => size with { X = length }, 1 => size with { Y = length }, _ => size with { Z = length } };
            shift = d * (length - old) / 2f;
        }

        var stretch = newSize / size;
        var section = solid.Section is { } s ? s with { A = s.A * stretch, B = s.B * stretch, C = s.C * stretch } : null;
        var marks = solid.Marks.Select(m => m with { Points = m.Points.Select(p => p * stretch).ToArray() }).ToArray();
        return o with { Points = [o.Points[0] + shift, o.Points[1] + shift], Solid = solid with { Size = newSize, Section = section, Marks = marks } };
    }

    // ------------------------------------------------------------- making solids

    /// <summary>A new solid filling the dragged box in the textbook view.</summary>
    private static BoardObject Solid3DFromDrag(ShapeKind kind, Vector2 min, Vector2 max, Vector4 color, float width)
    {
        float w = MathF.Max(max.X - min.X, 1f), h = MathF.Max(max.Y - min.Y, 1f), m = MathF.Min(w, h);
        var size = kind switch
        {
            ShapeKind.Sphere => new Vector3(m),
            ShapeKind.Cylinder or ShapeKind.Cone => new Vector3(w, h, w),
            ShapeKind.Prism => new Vector3(w, h, m * 0.8f),
            _ => new Vector3(w, h, m)
        };
        var center = (min + max) * 0.5f;
        // A prism turned less, or its third edge would fall onto a side one.
        var o = new BoardObject(kind, [center, center + new Vector2(SolidArrow, 0)], color, width)
            { Solid = new Solid3D(size, kind == ShapeKind.Prism ? Solid3D.DefaultYaw / 2f : Solid3D.DefaultYaw, Solid3D.DefaultPitch) };
        var (lo, hi) = Bounds(o);
        var extent = Vector2.Max(hi - lo, new Vector2(1e-3f));
        float fit = MathF.Min(w / extent.X, h / extent.Y);
        o = o with { Solid = o.Solid! with { Size = size * fit } };
        // Centred on the box: the projection of the middle is not always the middle of the drawing.
        (lo, hi) = Bounds(o);
        var shift = center - (lo + hi) * 0.5f;
        return o with { Points = [o.Points[0] + shift, o.Points[1] + shift] };
    }

    /// <summary>
    /// The solid as a body in space, for turning and building on it: one in the older textbook projection becomes one
    /// of the same size where it stood, seen in the usual view.
    /// </summary>
    public static BoardObject AsSolid3D(BoardObject o)
    {
        if (o.Solid != null || !IsSolid(o.Kind)) return o;
        var (min, max) = Bounds(o);
        return Solid3DFromDrag(o.Kind, min, max, o.Color, o.Width) with { Id = o.Id };
    }
}
