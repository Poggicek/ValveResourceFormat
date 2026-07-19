#version 460

#define UV_SCALE 2.0

uniform sampler2D g_tColor; // SrgbRead(true)

in vec4 vtxColor;
in vec2 vtxUv;

out vec4 outputColor;

#include "common/ViewConstants.slang"

void main(void)
{
    // Scroll the arrow texture along the ribbon so the flow reads from start to end.
    float scroll = mod(g_flTime, 1.0);
    vec2 uv = vtxUv * UV_SCALE + vec2(-scroll, 0.0);

    outputColor = texture(g_tColor, uv) * vtxColor;

    // Discard the transparent gaps between arrows; the arrow glyph itself stays fully opaque (no dithering, no fade).
    if (outputColor.a <= 0.1)
    {
        discard;
    }
}
