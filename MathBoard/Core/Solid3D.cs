using System.Numerics;

namespace MathBoard.Core;

/// <summary>
/// A solid as a body in space, which can be turned: its size along its own x, y (up) and z, centred on its middle, and
/// how it is turned towards the viewer (<see cref="Yaw"/> about the vertical, then <see cref="Pitch"/> towards the
/// viewer). The section and the lines drawn on or in it are kept in its own coordinates, so they turn with it.
/// </summary>
public sealed record Solid3D(Vector3 Size, float Yaw, float Pitch)
{
    /// <summary>The usual textbook view: turned a little to the side and seen a little from above.</summary>
    public const float DefaultYaw = -0.5f, DefaultPitch = 0.35f;

    public SolidSection? Section { get; init; }
    public SolidMark[] Marks { get; init; } = [];

    public void Write(BinaryWriter w)
    {
        WriteVector(w, Size);
        w.Write(Yaw);
        w.Write(Pitch);
        w.Write(Section != null);
        if (Section is { } s)
        {
            WriteVector(w, s.A);
            WriteVector(w, s.B);
            WriteVector(w, s.C);
            WriteColor(w, s.Color);
            w.Write(s.Style);
            w.Write(s.Spacing);
            w.Write((sbyte)s.Cut);
        }

        w.Write(Marks.Length);
        foreach (var mark in Marks)
        {
            WriteColor(w, mark.Color);
            w.Write(mark.Points.Length);
            foreach (var p in mark.Points) WriteVector(w, p);
        }
    }

    public static Solid3D Read(BinaryReader r)
    {
        var solid = new Solid3D(ReadVector(r), r.ReadSingle(), r.ReadSingle());
        if (r.ReadBoolean())
            solid = solid with { Section = new SolidSection(ReadVector(r), ReadVector(r), ReadVector(r), ReadColor(r)) { Style = r.ReadString(), Spacing = r.ReadSingle(), Cut = r.ReadSByte() } };
        var marks = new SolidMark[r.ReadInt32()];
        for (int i = 0; i < marks.Length; i++)
        {
            var color = ReadColor(r);
            var points = new Vector3[r.ReadInt32()];
            for (int k = 0; k < points.Length; k++) points[k] = ReadVector(r);
            marks[i] = new SolidMark(points, color);
        }

        return solid with { Marks = marks };
    }

    private static void WriteVector(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3 ReadVector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    private static void WriteColor(BinaryWriter w, Vector4 c)
    {
        w.Write(c.X);
        w.Write(c.Y);
        w.Write(c.Z);
        w.Write(c.W);
    }

    private static Vector4 ReadColor(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}

/// <summary>
/// The plane of a section, through three points of the solid (its own coordinates), and how it is filled: a style of the
/// fill tool (empty for none) in its colour, and the hatch spacing. A solid can be cut by it: <see cref="Cut"/> is 1 or -1
/// for the side of the plane that is left (see ObjectGeometry.CutPlane), 0 for a whole solid.
/// </summary>
public sealed record SolidSection(Vector3 A, Vector3 B, Vector3 C, Vector4 Color)
{
    public string Style { get; init; } = "hatch";
    public float Spacing { get; init; } = 8f;
    public int Cut { get; init; }
}

/// <summary>A line on the solid's faces or through it, in its own coordinates.</summary>
public sealed record SolidMark(Vector3[] Points, Vector4 Color);
