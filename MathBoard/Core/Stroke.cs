using System.Numerics;

namespace MathBoard.Core;

public class Stroke
{
    /// <summary>The same stroke on every board of a shared lesson; not saved in files.</summary>
    public long Id { get; set; } = BoardObject.NewId();
    public List<Vector2> Points { get; set; } = [];
    public float Width { get; set; } = 22f;
    public Vector4 Color { get; set; } = Vector4.One;
}