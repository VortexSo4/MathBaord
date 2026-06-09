using System.Numerics;

namespace MathBoard.Core;

public class Stroke
{
    public List<Vector2> Points { get; } = [];
    public float Width { get; set; } = 22f;
    public Vector4 Color { get; set; } = new Vector4(0f, 0f, 0f, 1f);
}