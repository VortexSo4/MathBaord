using System.Numerics;

namespace MathBoard.Core;

public class Stroke
{
    public List<Vector2> Points { get; } = [];

    public float Width { get; set; } = 4f;
}