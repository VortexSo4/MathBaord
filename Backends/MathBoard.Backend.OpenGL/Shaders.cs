namespace MathBoard.Backend.OpenGL;

/// <summary>GLSL 1.20 port of the Vulkan backend's prim.vert/prim.frag; keep the two in sync.</summary>
internal static class Shaders
{
    public const string Vertex = """
        #version 120
        attribute vec2 aCorner;
        attribute vec4 aA;
        attribute vec4 aB;
        attribute vec4 aColor;
        attribute vec4 aExtra;
        attribute vec4 aClip;

        uniform mat4 uTransform;
        uniform float uPixel;
        // Moves capsule ends (a selection being dragged): p' = mat2(uMove) * p + uMoveOffset; their width stays.
        uniform vec4 uMove;
        uniform vec2 uMoveOffset;

        varying vec2 vLocal;
        varying vec2 vUV;
        varying vec4 vColor;
        varying vec4 vA;
        varying vec4 vB;
        varying vec4 vClip;
        varying vec4 vExtra;

        void main() {
            vec2 c = aCorner;
            float type = floor(aB.w + 0.5);
            float m = uPixel;
            vec2 p;
            vUV = vec2(0.0);
            vec4 A = aA;

            if (type == 1.0) {
                mat2 move = mat2(uMove.xy, uMove.zw);
                A = vec4(move * aA.xy + uMoveOffset, move * aA.zw + uMoveOffset);
                vec2 a = A.xy;
                vec2 d = A.zw - a;
                float len = length(d);
                vec2 dir = len > 1e-6 ? d / len : vec2(1.0, 0.0);
                vec2 n = vec2(-dir.y, dir.x);
                float r = aB.x + m;
                p = a + dir * ((len + 2.0 * r) * c.x - r) + n * r * (c.y * 2.0 - 1.0);
            } else if (type == 2.0) {
                p = mix(aA.xy, aA.zw, c);
                vUV = mix(aExtra.xy, aExtra.zw, c);
            } else if (type == 6.0) {
                p = aA.xy + aA.zw * c.x + aExtra.xy * c.y;
                vUV = c;
            } else if (type == 3.0 || type == 4.0) {
                p = aA.xy + (c * 2.0 - 1.0) * (aA.w + m);
            } else {
                p = mix(aA.xy - m, aA.zw + m, c);
            }

            vLocal = p;
            vColor = aColor;
            vA = A;
            vB = aB;
            vClip = aClip;
            vExtra = aExtra;
            gl_Position = uTransform * vec4(p, 0.0, 1.0);
        }
        """;

    public const string Fragment = """
        #version 120
        uniform sampler2D uAtlas;

        varying vec2 vLocal;
        varying vec2 vUV;
        varying vec4 vColor;
        varying vec4 vA;
        varying vec4 vB;
        varying vec4 vClip;
        varying vec4 vExtra;

        const float PI = 3.14159265359;

        // Distance to the capsule from a to b, or with a dash pattern (dash.y > 0) to the nearest of its dashes: dash.x long
        // (without the round ends) every dash.y, the pattern shifted by dash.z at a, so the dashes of a polyline run on.
        float capsule(vec2 p, vec2 a, vec2 b, float r, vec4 dash) {
            vec2 ba = b - a;
            float len = length(ba);
            vec2 dir = len > 1e-6 ? ba / len : vec2(1.0, 0.0);
            vec2 pa = p - a;
            float s = dot(pa, dir);
            float side = dot(pa, vec2(-dir.y, dir.x));
            if (dash.y <= 0.0) return length(vec2(s - clamp(s, 0.0, len), side)) - r;
            // The dash under the point or one of its neighbours, cut to the segment.
            float k = floor((clamp(s, 0.0, len) + dash.z) / dash.y);
            float best = 1e9;
            for (int i = -1; i <= 1; i++) {
                float from = max((k + float(i)) * dash.y - dash.z, 0.0);
                float to = min((k + float(i)) * dash.y - dash.z + dash.x, len);
                if (to >= from) best = min(best, length(vec2(s - clamp(s, from, to), side)));
            }
            return best - r;
        }

        float sdRoundBox(vec2 p, vec2 b, float r) {
            vec2 q = abs(p) - b + r;
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }

        vec3 hsv2rgb(vec3 c) {
            vec3 p = abs(fract(c.xxx + vec3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
            return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
        }

        vec3 pick(float mode, float hue, float t) {
            if (mode < 0.5) return hsv2rgb(vec3(t, 1.0, 1.0));
            if (mode < 1.5) return hsv2rgb(vec3(hue, t, 1.0));
            return hsv2rgb(vec3(hue, 1.0, t));
        }

        void main() {
            if (vLocal.x < vClip.x || vLocal.y < vClip.y || vLocal.x > vClip.z || vLocal.y > vClip.w) discard;

            float type = floor(vB.w + 0.5);
            vec4 color = vColor;

            if (type == 2.0) {
                gl_FragColor = vec4(color.rgb, color.a * texture2D(uAtlas, vUV).a);
                return;
            }

            if (type == 6.0) {
                vec4 texel = texture2D(uAtlas, vUV);
                gl_FragColor = vec4(texel.rgb * color.rgb, texel.a * color.a);
                return;
            }

            float px = max(fwidth(vLocal.x), 1e-6);
            float d;

            if (type == 1.0) {
                d = capsule(vLocal, vA.xy, vA.zw, vB.x, vExtra);
            } else if (type == 3.0 || type == 4.0) {
                vec2 q = vLocal - vA.xy;
                float r = length(q);
                d = abs(r - (vA.z + vA.w) * 0.5) - (vA.w - vA.z) * 0.5;
                float angle = atan(q.y, q.x);
                if (type == 3.0) {
                    float mid = (vB.x + vB.y) * 0.5;
                    float span = (vB.y - vB.x) * 0.5;
                    float da = abs(mod(angle - mid + PI, 2.0 * PI) - PI) - span;
                    d = max(d, da * r);
                } else {
                    color.rgb = pick(vB.x, vB.y, fract((angle + PI * 0.5) / (2.0 * PI)));
                }
            } else if (type == 5.0) {
                vec2 hs = (vA.zw - vA.xy) * 0.5;
                d = sdRoundBox(vLocal - (vA.xy + hs), hs, min(vB.z, min(hs.x, hs.y)));
                color.rgb = pick(vB.x, vB.y, clamp((vA.w - vLocal.y) / max(vA.w - vA.y, 1e-6), 0.0, 1.0));
            } else {
                vec2 hs = (vA.zw - vA.xy) * 0.5;
                float r = min(vB.x, min(hs.x, hs.y));
                d = sdRoundBox(vLocal - (vA.xy + hs), hs, r);
                if (vB.y > 0.0) d = abs(d + vB.y * 0.5) - vB.y * 0.5;
            }

            float alpha = clamp(0.5 - d / px, 0.0, 1.0);
            if (alpha <= 0.0) discard;
            gl_FragColor = vec4(color.rgb, color.a * alpha);
        }
        """;
}
