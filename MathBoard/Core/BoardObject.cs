using System.Numerics;

namespace MathBoard.Core;

public enum ShapeKind
{
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Triangle,
    Point,
    Ray,
    InfiniteLine,
    Circle,
    Angle,
    Plot,
    Image,
    Box,
    Prism,
    Pyramid,
    Cylinder,
    Cone,
    Sphere,
    Fill
}

/// <summary>
/// An editable shape on the board, defined by control points in world space so any transform stays exact:
/// Line/Arrow/Ray/InfiniteLine: two points; Rectangle: four corners of any quadrilateral; Triangle: three vertices;
/// Ellipse: center and the ends of both semi-axes; Point: one point; Circle: center and a point on it;
/// Angle: vertex and a point on each arm; Plot and Image: origin (top-left) corner, end of the x side, end of the y side;
/// Box and Prism: front bottom-left corner, then the ends of the width, height and depth edges; Pyramid: the same
/// with the apex in place of the height; Cylinder and Cone: base center, end of the width radius, top center or apex,
/// end of the depth radius; Sphere: center, end of the radius, end of the equator's depth radius;
/// Fill: the region's contours, separated by NaN points.
/// Objects are immutable: edits replace them, so undo snapshots can share them.
/// </summary>
public sealed record BoardObject(ShapeKind Kind, Vector2[] Points, Vector4 Color, float Width)
{
    /// <summary>
    /// Plot: functions of x separated by ';'. Image: file name in <see cref="ImageStore"/>. Fill: its style.
    /// Lines and outlines: <see cref="DashedStyle"/> when dashed (older versions read the file and draw them solid).
    /// </summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// The same object on every board of a shared lesson; an edit (a <c>with</c> copy) keeps it, a new object or a copy
    /// next to the original gets a new one. Not saved in files.
    /// </summary>
    public long Id { get; init; } = NewId();

    public static long NewId() => Random.Shared.NextInt64(1, long.MaxValue);

    public const string DashedStyle = "dashed";
    public bool Dashed => Text == DashedStyle;

    /// <summary>Plot only: the x range shown; y uses the same units and is centered on zero.</summary>
    public float XMin { get; init; } = -5f;
    public float XMax { get; init; } = 5f;

    public BoardObject Transformed(Matrix3x2 m) => this with { Points = Points.Select(p => Vector2.Transform(p, m)).ToArray() };
}
