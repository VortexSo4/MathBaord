using System.Numerics;

namespace MathBoard.Graphics;

/// <summary>Where the dashes of dashed capsules are (see <see cref="Prim.DashedCapsule"/>), for drawing them without a shader.</summary>
public static class Dashing
{
    /// <summary>
    /// Adds the dashes of the polyline that lie in the rectangle, as segments to draw with round ends of the line's radius:
    /// a ray is a million units long, so only the part in view can be cut into dashes. <paramref name="pattern"/> is the
    /// first capsule's Extra: dash, period and where the pattern stands at the first point.
    /// </summary>
    public static void Visible(ReadOnlySpan<Vector2> points, Vector4 pattern, Vector2 min, Vector2 max, List<(Vector2 A, Vector2 B)> dashes)
    {
        float dash = pattern.X, period = pattern.Y, walked = pattern.Z;
        for (int i = 0; i + 1 < points.Length; i++)
        {
            var a = points[i];
            float length = Vector2.Distance(a, points[i + 1]);
            if (length > 1e-6f && Clip(a, points[i + 1], min, max, out float t0, out float t1))
            {
                var dir = (points[i + 1] - a) / length;
                float from = t0 * length, to = t1 * length;
                for (float k = MathF.Floor((from + walked) / period); k * period - walked <= to; k++)
                {
                    float start = MathF.Max(k * period - walked, from), end = MathF.Min(k * period - walked + dash, to);
                    if (end >= start) dashes.Add((a + dir * start, a + dir * end));
                }
            }

            walked = (walked + length) % period;
        }
    }

    /// <summary>The part [t0, t1] of the segment from a to b inside the rectangle; false if there is none.</summary>
    public static bool Clip(Vector2 a, Vector2 b, Vector2 min, Vector2 max, out float t0, out float t1)
    {
        t0 = 0f;
        t1 = 1f;
        var d = b - a;
        return Edge(-d.X, a.X - min.X, ref t0, ref t1) && Edge(d.X, max.X - a.X, ref t0, ref t1) &&
               Edge(-d.Y, a.Y - min.Y, ref t0, ref t1) && Edge(d.Y, max.Y - a.Y, ref t0, ref t1);
    }

    private static bool Edge(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0f) return q >= 0f;
        float t = q / p;
        if (p < 0f)
        {
            if (t > t1) return false;
            t0 = MathF.Max(t0, t);
        }
        else
        {
            if (t < t0) return false;
            t1 = MathF.Min(t1, t);
        }

        return true;
    }
}
