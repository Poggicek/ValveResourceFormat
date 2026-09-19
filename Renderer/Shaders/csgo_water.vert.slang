#version 460

// Reconstructed from every variant of csgo_water.vfx. The pixel program is csgo_water.frag.slang.

#include "common/utils.slang"
#include "common/instancing.slang"
#include "common/features.slang"

in vec3 vPOSITION;
in vec2 vTEXCOORD;
#include "common/compression.slang"

#define F_FLOW_NORMALS 0 // csgo_water
#define S_MODE_TOOLS_VIS 0

#define S_FLOW_NORMALS F_FLOW_NORMALS

#if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
    in vec2 vLightmapUV;
    out vec3 vLightmapUVScaled;
#elif (D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1)
    in vec4 vPerVertexLighting;
    out vec3 vPerVertexLightingOut;
#endif

out vec3 vFragPosition;
out vec2 vTexCoordOut;
out vec3 vNormalOut;
out vec3 vTangentOut;
out vec3 vBitangentOut;
centroid out vec4 vVertexColorOut;

// Flowing water builds its normal map coordinates from the world position in the pixel program,
// so the scrolling normal map coordinates only exist without it.
#if (S_FLOW_NORMALS == 0)
    out vec2 vNormalTexCoord;
    out vec4 vExtraTexCoords;
#endif

#include "common/ViewConstants.slang"
#include "common/LightingConstants.slang"

uniform vec3 g_vColorTint = vec3(1.0); // SrgbRead(true)
uniform float g_flModelTintAmount = 1.0;

uniform vec2 g_vTexCoordCenter = vec2(0.5);
uniform vec2 g_vTexCoordScale = vec2(1.0);
uniform float g_flTexCoordRotation = 0.0;
uniform vec2 g_vTexCoordOffset = vec2(0.0);
uniform vec2 g_vTexCoordScrollSpeed = vec2(0.0);

uniform float g_flNormalTexCoordRotation = 0.0;
uniform vec2 g_vNormalTexCoordScale = vec2(1.0);
uniform vec2 g_vNormalTexCoordOffset = vec2(0.0);
uniform vec4 g_vExtraTexCoordScroll = vec4(0.0);

#if (S_MODE_TOOLS_VIS == 1)
    // 0 = None, 1 = model.x, 2 = model.y, 3 = model.z
    uniform int g_nScaleTexCoordUByModelScaleAxis = 0;
    uniform int g_nScaleTexCoordVByModelScaleAxis = 0;

    // The game only applies this scale in the tools visualization mode, the shipped vertex program ignores it.
    vec2 ScaleTexCoordByModel(vec2 texCoord, mat3x4 objectToWorld)
    {
        if (g_nScaleTexCoordUByModelScaleAxis == 0 && g_nScaleTexCoordVByModelScaleAxis == 0)
        {
            return texCoord;
        }

        texCoord.y = 1.0 - texCoord.y;

        if (texCoord.x >= 0.0 || texCoord.y >= 0.0)
        {
            vec4 modelScale = vec4(
                length(vec3(objectToWorld[0].x, objectToWorld[1].x, objectToWorld[2].x)),
                length(vec3(objectToWorld[0].y, objectToWorld[1].y, objectToWorld[2].y)),
                length(vec3(objectToWorld[0].z, objectToWorld[1].z, objectToWorld[2].z)),
                1.0);

            texCoord.x *= modelScale[(g_nScaleTexCoordUByModelScaleAxis + 3) % 4];
            texCoord.y *= modelScale[(g_nScaleTexCoordVByModelScaleAxis + 3) % 4];
        }

        texCoord.y = 1.0 - texCoord.y;
        return texCoord;
    }
#endif

// The material system bakes the rotation into the offset only, the coordinates themselves are not rotated.
vec2 GetNormalTexCoordOffset()
{
    float rotation = radians(g_flNormalTexCoordRotation);
    float sinRot = sin(rotation);
    float cosRot = cos(rotation);

    return (-0.5 * g_vNormalTexCoordScale) * vec2(cosRot - sinRot, sinRot + cosRot) + g_vNormalTexCoordOffset + 0.5;
}

void main()
{
    mat3x4 objectToWorld = CalculateObjectToWorldMatrix();
    ObjectData_t object = GetObjectData();

    vec3 normal;
    vec4 tangent;
    GetOptionallyCompressedNormalTangent(normal, tangent);

    vFragPosition = vec4(vPOSITION, 1.0) * objectToWorld;
    gl_Position = g_matWorldToProjection * vec4(vFragPosition, 1.0);

    vNormalOut = TransformDirectionToWorld(objectToWorld, normal);
    #if (S_MODE_TOOLS_VIS == 1)
        vTangentOut = normalize(tangent.xyz * inverse(mat3(objectToWorld)));
    #else
        vTangentOut = TransformDirectionToWorld(objectToWorld, tangent.xyz);
    #endif
    vBitangentOut = tangent.w * cross(vNormalOut, vTangentOut);

    vec2 texCoord = vTEXCOORD;
    #if (S_MODE_TOOLS_VIS == 1)
        texCoord = ScaleTexCoordByModel(texCoord, objectToWorld);
    #endif

    texCoord = RotateVector2D(g_vTexCoordScale * texCoord - g_vTexCoordCenter, g_flTexCoordRotation) + g_vTexCoordCenter + g_vTexCoordOffset;
    vTexCoordOut = texCoord + g_vTexCoordScrollSpeed * g_flTime;

    #if (S_FLOW_NORMALS == 0)
        vNormalTexCoord = vTEXCOORD * g_vNormalTexCoordScale + GetNormalTexCoordOffset();

        // Two extra normal map layers: a diagonal pair at a tenth of the scale and a swapped axis pair at 0.45, each with its own scroll.
        vExtraTexCoords = vec4(
            (vTEXCOORD.x + vTEXCOORD.y) * 0.1,
            (vTEXCOORD.y - vTEXCOORD.x) * 0.1,
            vTEXCOORD.y * 0.45,
            vTEXCOORD.x * 0.45) + g_vExtraTexCoordScroll * g_flTime;
    #endif

    vVertexColorOut = vec4(mix(vec3(1.0), SrgbGammaToLinear(object.vTint.rgb), g_flModelTintAmount) * g_vColorTint, object.vTint.a);

    #if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
        vLightmapUVScaled = vec3(vLightmapUV * g_vLightmapUvScale.xy, 0.0);
    #elif (D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1)
        vec3 light = vPerVertexLighting.rgb * 6.0 * vPerVertexLighting.a;
        vPerVertexLightingOut = pow2(light);
    #endif
}
