using System.Numerics;
using System.Runtime.InteropServices;

namespace MathBoard.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct TextVertex
{
    public Vector2 Position;
    public Vector2 UV;
    public Vector4 Color;
}