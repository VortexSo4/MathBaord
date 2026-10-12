#version 450

// Every primitive is one instanced quad; its shape is resolved per pixel in prim.frag.
layout(location = 0) in vec4 inA;
layout(location = 1) in vec4 inB;
layout(location = 2) in vec4 inColor;
layout(location = 3) in vec4 inExtra;
layout(location = 4) in vec4 inClip;

layout(push_constant) uniform PushConstants {
    mat4 transform;
    float pixel; // size of one screen pixel in primitive units, used as anti-aliasing margin
} pc;

layout(location = 0) out vec2 vLocal;
layout(location = 1) out vec2 vUV;
layout(location = 2) out vec4 vColor;
layout(location = 3) flat out vec4 vA;
layout(location = 4) flat out vec4 vB;
layout(location = 5) flat out vec4 vClip;

const vec2 corners[6] = vec2[](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(1, 1), vec2(0, 1));

void main() {
    vec2 c = corners[gl_VertexIndex];
    uint type = uint(inB.w + 0.5);
    float m = pc.pixel;
    vec2 p;
    vUV = vec2(0);

    if (type == 1u) {
        vec2 a = inA.xy;
        vec2 d = inA.zw - a;
        float len = length(d);
        vec2 dir = len > 1e-6 ? d / len : vec2(1, 0);
        vec2 n = vec2(-dir.y, dir.x);
        float r = inB.x + m;
        p = a + dir * ((len + 2.0 * r) * c.x - r) + n * r * (c.y * 2.0 - 1.0);
    } else if (type == 2u) {
        p = mix(inA.xy, inA.zw, c);
        vUV = mix(inExtra.xy, inExtra.zw, c);
    } else if (type == 6u) {
        p = inA.xy + inA.zw * c.x + inExtra.xy * c.y;
        vUV = c;
    } else if (type == 3u || type == 4u) {
        p = inA.xy + (c * 2.0 - 1.0) * (inA.w + m);
    } else {
        p = mix(inA.xy - m, inA.zw + m, c);
    }

    vLocal = p;
    vColor = inColor;
    vA = inA;
    vB = inB;
    vClip = inClip;
    gl_Position = pc.transform * vec4(p, 0.0, 1.0);
}
