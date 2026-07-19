#version 460

layout (location = 0) in vec3 vPOSITION;
layout (location = 1) in vec4 vCOLOR;
layout (location = 2) in vec2 vTEXCOORD;

out vec4 vtxColor;
out vec2 vtxUv;

#include "common/ViewConstants.slang"
#include "common/instancing.slang"

void main(void) {
    vtxColor = vCOLOR;
    vtxUv = vTEXCOORD;

    vec4 worldPos = CalculateObjectToWorldMatrix() * vec4(vPOSITION, 1.0);
    gl_Position = g_matWorldToProjection * worldPos;
}
