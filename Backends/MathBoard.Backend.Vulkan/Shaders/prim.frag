#version 450

// The glyph atlas, or the picture of an image primitive.
layout(binding = 0) uniform sampler2D atlas;

layout(location = 0) in vec2 vLocal;
layout(location = 1) in vec2 vUV;
layout(location = 2) in vec4 vColor;
layout(location = 3) flat in vec4 vA;
layout(location = 4) flat in vec4 vB;
layout(location = 5) flat in vec4 vClip;
layout(location = 6) flat in vec4 vExtra;

layout(location = 0) out vec4 outColor;

const float PI = 3.14159265359;

float sdRoundBox(vec2 p, vec2 b, float r) {
    vec2 q = abs(p) - b + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

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

vec3 hsv2rgb(vec3 c) {
    vec3 p = abs(fract(c.xxx + vec3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
    return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

void main() {
    // The clip rect is in the primitive's own space, which for interface primitives is the screen.
    if (vLocal.x < vClip.x || vLocal.y < vClip.y || vLocal.x > vClip.z || vLocal.y > vClip.w) discard;

    uint type = uint(vB.w + 0.5);
    vec4 color = vColor;

    if (type == 2u) {
        outColor = vec4(color.rgb, color.a * texture(atlas, vUV).r);
        return;
    }

    if (type == 6u) {
        vec4 texel = texture(atlas, vUV);
        outColor = vec4(texel.rgb * color.rgb, texel.a * color.a);
        return;
    }

    float px = max(fwidth(vLocal.x), 1e-6);
    float d;

    if (type == 1u) {
        d = capsule(vLocal, vA.xy, vA.zw, vB.x, vExtra);
    } else if (type == 3u || type == 4u) {
        vec2 q = vLocal - vA.xy;
        float r = length(q);
        d = abs(r - (vA.z + vA.w) * 0.5) - (vA.w - vA.z) * 0.5;
        float angle = atan(q.y, q.x);
        if (type == 3u) {
            float mid = (vB.x + vB.y) * 0.5;
            float span = (vB.y - vB.x) * 0.5;
            float da = abs(mod(angle - mid + PI, 2.0 * PI) - PI) - span;
            d = max(d, da * r);
        } else {
            // Value grows clockwise from 12 o'clock, matching the picker's input mapping.
            float t = fract((angle + PI * 0.5) / (2.0 * PI));
            vec3 hsv = vB.x < 0.5 ? vec3(t, 1.0, 1.0) : (vB.x < 1.5 ? vec3(vB.y, t, 1.0) : vec3(vB.y, 1.0, t));
            color.rgb = hsv2rgb(hsv);
        }
    } else if (type == 5u) {
        vec2 hs = (vA.zw - vA.xy) * 0.5;
        d = sdRoundBox(vLocal - (vA.xy + hs), hs, min(vB.z, min(hs.x, hs.y)));
        float t = clamp((vA.w - vLocal.y) / max(vA.w - vA.y, 1e-6), 0.0, 1.0);
        color.rgb = hsv2rgb(vB.x < 0.5 ? vec3(t, 1.0, 1.0) : (vB.x < 1.5 ? vec3(vB.y, t, 1.0) : vec3(vB.y, 1.0, t)));
    } else {
        vec2 hs = (vA.zw - vA.xy) * 0.5;
        float r = min(vB.x, min(hs.x, hs.y));
        d = sdRoundBox(vLocal - (vA.xy + hs), hs, r);
        if (vB.y > 0.0) d = abs(d + vB.y * 0.5) - vB.y * 0.5;
    }

    float alpha = clamp(0.5 - d / px, 0.0, 1.0);
    if (alpha <= 0.0) discard;
    outColor = vec4(color.rgb, color.a * alpha);
}
