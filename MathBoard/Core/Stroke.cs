using System.Numerics;

namespace MathBoard.Core;

public class Stroke
{
    public List<Vector2> Points { get; set; } = [];
    public float Width { get; set; } = 22f;
    public Vector4 Color { get; set; } = Settings.Colors[0];
}