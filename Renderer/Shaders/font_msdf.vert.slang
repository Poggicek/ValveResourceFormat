#version 460

in vec2 vPOSITION;
in vec2 vTEXCOORD;
in vec4 vCOLOR;
in float vDEPTH;

out vec2 vTexCoord;
out vec4 vFragColor;
out float vSceneDepth;

// Prefixed so it packs into the globals block. Unprefixed it was a loose uniform, which Vulkan
// forbids outright, and it was named 'transform', which collides with the per-draw push constant
// block's mat3x4 of the same name -- the collision reads as a type error on the multiply rather
// than as a name clash, which is why it took a while to place.
uniform mat4 g_matTextTransform;

void main(void) {
    gl_Position = g_matTextTransform * vec4(vPOSITION, 0, 1);
    vTexCoord = vTEXCOORD;
    vFragColor = vCOLOR;
    vSceneDepth = vDEPTH;
}
