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
    Image
}

/// <summary>
/// An editable shape on the board, defined by control points in world space so any transform stays exact:
/// Line/Arrow/Ray/InfiniteLine: two points; Rectangle: four corners; Triangle: three vertices;
/// Ellipse: center and the ends of both semi-axes; Point: one point; Circle: center and a point on it;
/// Angle: vertex and a point on each arm; Plot and Image: origin (top-left) corner, end of the x side, end of the y side.
/// Objects are immutable: edits replace them, so undo snapshots can share them.
/// </summary>
public sealed record BoardObject(ShapeKind Kind, Vector2[] Points, Vector4 Color, float Width)
{
    /// <summary>Plot: functions of x separated by ';'. Image: file name in <see cref="ImageStore"/>.</summary>
    public string Text { get; init; } = "";

    /// <summary>Plot only: the x range shown; y uses the same units and is centered on zero.</summary>
    public float XMin { get; init; } = -5f;
    public float XMax { get; init; } = 5f;

    public BoardObject Transformed(Matrix3x2 m) => this with { Points = Points.Select(p => Vector2.Transform(p, m)).ToArray() };
}
