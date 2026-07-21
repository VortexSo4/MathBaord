#version 460

layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec2 inUV;
layout(location = 2) in vec4 inColor;

layout(location = 0) out vec2 fragUV;
layout(location = 1) out vec4 fragColor;

layout(push_constant) uniform PushConstants {
    mat4 transform;
} pc;

void main() {
    gl_Position = pc.transform * vec4(inPosition, 0.0, 1.0);
    fragUV = inUV;
    fragColor = inColor;
}
