using System.Numerics;
using System.Runtime.InteropServices;

namespace MathBoard.Rendering;

public enum PrimType
{
    RoundRect = 0,
    Capsule = 1,
    Glyph = 2,
    Arc = 3,
    ColorRing = 4
}

/// <summary>
/// One instanced quad for prim.vert/prim.frag. Field meaning depends on the type:
/// RoundRect: A = min.xy max.xy, B = radius, border;
/// Capsule: A = p0.xy p1.xy, B = radius;
/// Glyph: A = min.xy max.xy, Extra = uv rect;
/// Arc: A = center.xy, r0, r1, B = start angle, end angle;
/// ColorRing: A = center.xy, r0, r1, B = mode (0 hue, 1 saturation, 2 value), hue.
/// B.w always holds the type. Clip is a screen-space rect (min.xy max.xy).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Prim
{
    public Vector4 A;
    public Vector4 B;
    public Vector4 Color;
    public Vector4 Extra;
    public Vector4 Clip;

    public static readonly Vector4 NoClip = new(-1e9f, -1e9f, 1e9f, 1e9f);

    public static Prim Capsule(Vector2 p0, Vector2 p1, float radius, Vector4 color) => new()
    {
        A = new Vector4(p0, p1.X, p1.Y),
        B = new Vector4(radius, 0, 0, (float)PrimType.Capsule),
        Color = color,
        Clip = NoClip
    };
}
