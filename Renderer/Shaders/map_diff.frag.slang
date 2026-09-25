#version 460

#include "common/utils.slang"

// Composites two builds of a map drawn from the same camera: tonemapped color and resolved opaque depth of each.
layout (binding = 0) uniform sampler2D g_tNewColor;
layout (binding = 1) uniform sampler2D g_tOldColor;
layout (binding = 2) uniform sampler2D g_tNewDepth;
layout (binding = 3) uniform sampler2D g_tOldDepth;

#define MODE_DIFF 0
#define MODE_NEW 1
#define MODE_OLD 2
#define MODE_SPLIT 3
#define MODE_HEATMAP 4

uniform int g_nMode = 0;
uniform float g_flSplitX = 0.0;
uniform float g_flDepthTolerance = 1.0;
uniform float g_flColorThreshold = 0.1;
uniform float g_flUnchangedDim = 0.6;
uniform vec4 g_vInvProjRow3;
uniform vec2 g_vViewportZRange;

const vec3 AddedColor = vec3(0.1, 1.0, 0.2);
const vec3 RemovedColor = vec3(1.0, 0.1, 0.1);
const vec3 ChangedColor = vec3(1.0, 0.85, 0.1);

out vec4 outputColor;

bool IsSurface(float depth)
{
    // The 2D and 3D sky sit in front of the clear depth but behind the scene's own range
    return depth > g_vViewportZRange.x;
}

float DistanceFromDepth(float depth)
{
    float normalized = RemapValClamped(depth, g_vViewportZRange.x, g_vViewportZRange.y, 0.0, 1.0);
    float invDistance = fma(normalized, g_vInvProjRow3.z, g_vInvProjRow3.w);
    return 1.0 / max(invDistance, 1e-20);
}

float Luminance(vec3 color)
{
    return dot(color, vec3(0.2126, 0.7152, 0.0722));
}

void main()
{
    ivec2 pixel = ivec2(gl_FragCoord.xy);

    vec3 newColor = texelFetch(g_tNewColor, pixel, 0).rgb;
    vec3 oldColor = texelFetch(g_tOldColor, pixel, 0).rgb;

    if (g_nMode == MODE_NEW)
    {
        outputColor = vec4(newColor, 1.0);
        return;
    }

    if (g_nMode == MODE_OLD)
    {
        outputColor = vec4(oldColor, 1.0);
        return;
    }

    if (g_nMode == MODE_SPLIT)
    {
        float edge = abs(gl_FragCoord.x - g_flSplitX);
        vec3 color = gl_FragCoord.x < g_flSplitX ? oldColor : newColor;
        outputColor = vec4(edge < 1.0 ? vec3(1.0) : color, 1.0);
        return;
    }

    float newDepth = texelFetch(g_tNewDepth, pixel, 0).r;
    float oldDepth = texelFetch(g_tOldDepth, pixel, 0).r;

    bool newSurface = IsSurface(newDepth);
    bool oldSurface = IsSurface(oldDepth);

    float newDistance = newSurface ? DistanceFromDepth(newDepth) : 1e30;
    float oldDistance = oldSurface ? DistanceFromDepth(oldDepth) : 1e30;

    // Depth precision falls off with distance, so the tolerance grows with it
    float tolerance = g_flDepthTolerance + min(newDistance, oldDistance) * 0.001;

    vec3 colorDelta = abs(newColor - oldColor);
    bool colorChanged = max(colorDelta.r, max(colorDelta.g, colorDelta.b)) > g_flColorThreshold;

    bool added = newSurface && newDistance + tolerance < oldDistance;
    bool removed = oldSurface && oldDistance + tolerance < newDistance;

    if (g_nMode == MODE_HEATMAP)
    {
        vec3 heat = added ? AddedColor : removed ? RemovedColor : colorChanged ? ChangedColor : vec3(Luminance(newColor) * 0.15);
        outputColor = vec4(heat, 1.0);
        return;
    }

    vec3 color;

    if (added)
    {
        color = mix(newColor, AddedColor, 0.55);
    }
    else if (removed)
    {
        // Show what used to be there
        color = mix(oldColor, RemovedColor, 0.55);
    }
    else if (colorChanged)
    {
        color = mix(newColor, ChangedColor, 0.45);
    }
    else
    {
        vec3 grey = vec3(Luminance(newColor));
        color = mix(newColor, grey, g_flUnchangedDim) * (1.0 - 0.5 * g_flUnchangedDim);
    }

    outputColor = vec4(color, 1.0);
}
