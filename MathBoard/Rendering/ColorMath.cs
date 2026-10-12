using System.Numerics;

namespace MathBoard.Rendering;

public static class ColorMath
{
    public static Vector4 HsvToRgb(float h, float s, float v)
    {
        h = Math.Clamp(h, 0f, 1f);
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        int hi = (int)(h * 6f) % 6;
        float f = h * 6f - (int)(h * 6f);
        float p = v * (1f - s);
        float q = v * (1f - f * s);
        float t = v * (1f - (1f - f) * s);

        return hi switch
        {
            0 => new Vector4(v, t, p, 1f),
            1 => new Vector4(q, v, p, 1f),
            2 => new Vector4(p, v, t, 1f),
            3 => new Vector4(p, q, v, 1f),
            4 => new Vector4(t, p, v, 1f),
            _ => new Vector4(v, p, q, 1f),
        };
    }

    public static (float H, float S, float V) RgbToHsv(Vector4 rgb)
    {
        float max = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z));
        float min = Math.Min(rgb.X, Math.Min(rgb.Y, rgb.Z));
        float delta = max - min;
        float s = max > 0f ? delta / max : 0f;
        if (delta < 0.00001f) return (0f, s, max);

        float h;
        if (Math.Abs(max - rgb.X) < 0.00001f) h = (rgb.Y - rgb.Z) / delta + (rgb.Y < rgb.Z ? 6f : 0f);
        else if (Math.Abs(max - rgb.Y) < 0.00001f) h = (rgb.Z - rgb.X) / delta + 2f;
        else h = (rgb.X - rgb.Y) / delta + 4f;
        return (h / 6f, s, max);
    }

    /// <summary>Dark or white, whichever reads better on top of <paramref name="c"/>.</summary>
    public static Vector4 ContrastOn(Vector4 c) =>
        c.X * 0.299f + c.Y * 0.587f + c.Z * 0.114f > 0.6f ? new Vector4(0.05f, 0.05f, 0.08f, 1f) : Vector4.One;
}
