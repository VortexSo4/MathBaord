using System.Numerics;

namespace MathBoard.Graphics;

/// <summary>
/// Pixels of a picture: RGBA, 8 bits per channel, straight alpha, rows from top to bottom. Backends keep one texture per
/// instance, so the same picture must be passed as the same instance every frame and never modified.
/// </summary>
public sealed class ImageData(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
}

/// <summary>A picture mapped onto the parallelogram Origin, Origin + U, Origin + V (the top-left, top-right and bottom-left corners).</summary>
public readonly record struct ImageDraw(ImageData Image, Vector2 Origin, Vector2 U, Vector2 V)
{
    public Prim ToPrim() => new()
    {
        A = new Vector4(Origin, U.X, U.Y),
        B = new Vector4(0, 0, 0, (float)PrimType.Image),
        Color = Vector4.One,
        Extra = new Vector4(V, 0, 0),
        Clip = Prim.NoClip
    };
}
