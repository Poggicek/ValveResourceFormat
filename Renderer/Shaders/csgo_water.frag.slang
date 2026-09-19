#version 460

// Reconstructed from every variant of csgo_water.vfx, the plain (non fancy) water of CS2.
// Refraction reads the scene behind the surface, reflection comes from the environment maps, and the
// water fog color can optionally be lit by the scene lighting.

#include "common/utils.slang"
#include "common/features.slang"
#include "common/ViewConstants.slang"
#include "common/LightingConstants.slang"

in vec3 vFragPosition;
in vec2 vTexCoordOut;
in vec3 vNormalOut;
in vec3 vTangentOut;
in vec3 vBitangentOut;

#include "common/lighting_common.slang"
#include "common/texturing.slang"
#include "common/fog.slang"
#include "common/depth.slang"
#include "common/lighting.slang"

out vec4 outputColor;

#define F_FRESNEL 0 // csgo_water
#define F_DISABLE_REFRACTION 0 // csgo_water
#define F_FLOW_NORMALS 0 // csgo_water
#define F_FLOW_COLOR 0 // csgo_water, requires F_FLOW_NORMALS
#define F_LIGHTMAP_WATER_FOG 0 // csgo_water
#define F_ANIMATED_NORMALS 0 // csgo_water

// The game picks this from the shader quality setting. 0 reflects a scene-wide fallback cube map at a
// fixed roughness, 1 runs the per-pixel environment map loop.
#define S_SHADER_QUALITY 1
// The tools visualization modes are the generic Source 2 ones and are not reproduced here.
#define S_MODE_TOOLS_VIS 0

#define S_FRESNEL F_FRESNEL
#define S_DISABLE_REFRACTION F_DISABLE_REFRACTION
#define S_FLOW_NORMALS F_FLOW_NORMALS
#define S_FLOW_COLOR F_FLOW_COLOR
#define S_LIGHTMAP_WATER_FOG F_LIGHTMAP_WATER_FOG
#define S_ANIMATED_NORMALS F_ANIMATED_NORMALS

#if (S_FLOW_NORMALS == 0)
    in vec2 vNormalTexCoord;
    in vec4 vExtraTexCoords;
#endif

#if (S_ANIMATED_NORMALS == 1)
    uniform sampler2DArray g_tNormal;
    uniform float g_flNormalMapAnimationTimePerFrame = 0.1;
    uniform float g_flNormalMapAnimationTimeOffset = 0.0;
#else
    uniform sampler2D g_tNormal;
#endif

uniform float g_flBumpStrength = 1.0;

#if (S_FLOW_NORMALS == 1)
    uniform sampler2D g_tFlow;
    uniform sampler2D g_tNoise;

    uniform float g_flFlowTimeScale = 1.0;
    uniform float g_flWorldPositionScale = 1.0;
    uniform float g_flWorldUvScale = 0.1;
    uniform float g_flNormalUvScale = 1.0;
    uniform float g_flNormalFlowLerpExp = 1.0;
    uniform float g_flNormalFlowTimeIntervalInSeconds = 0.4;
    uniform float g_flNormalFlowUvScrollDistance = 0.25;
    uniform float g_flNoiseUvScale = 1.0;
    uniform float g_flNoiseStrength = 1.0;

    #if (S_FLOW_COLOR == 1)
        uniform sampler2D g_tColor; // SrgbRead(true)

        uniform float g_flColorFlowLerpExp = 1.0;
        uniform float g_flColorFlowUvScale = 1.0;
        uniform float g_flColorFlowTimeIntervalInSeconds = 0.4;
        uniform float g_flColorFlowUvScrollDistance = 0.25;
    #endif
#endif

#if (S_DISABLE_REFRACTION == 0)
    uniform sampler2D g_tSceneColor; // reserved

    uniform vec3 g_vRefractionTint = vec3(1.0); // SrgbRead(true)
    uniform float g_flRefractionAmount = 1.0;
    uniform float g_flWaterDepth = 35.0;
    uniform float g_flWaterStart = 0.0;
    uniform float g_flRefractionClipPlaneAdjust = 0.0;
#endif

uniform vec3 g_vWaterFogColor = vec3(0.5, 0.5, 0.5); // SrgbRead(true)

uniform float g_flGlossiness = 1.0;
uniform float g_flReflectance = 0.2;

uniform vec3 g_vReflectionDir = vec3(0.0);
uniform vec3 g_vReflectionColor = vec3(1.0); // SrgbRead(true)
uniform float g_flReflectionPower = 1.0;

// Each flow layer restarts from a fresh spot of the normal map every interval, offset by a fraction of a
// tile per cycle so consecutive cycles do not sample the same texels.
const float FlowCycleTexCoordOffset = 0.311;

// Alpha and green carry the normal.
vec2 SampleNormalMap(vec2 texCoord)
{
    #if (S_ANIMATED_NORMALS == 1)
        float frame = (g_flTime + g_flNormalMapAnimationTimeOffset) / g_flNormalMapAnimationTimePerFrame;
        float frameCount = float(textureSize(g_tNormal, 0).z);

        frame = frame - frameCount * trunc(frame / frameCount);
        float frameA = floor(frame);
        float frameB = ceil(frame);
        frameB = frameB - frameCount * trunc(frameB / frameCount);

        return mix(texture(g_tNormal, vec3(texCoord, frameA)), texture(g_tNormal, vec3(texCoord, frameB)), frame - frameA).wy;
    #else
        return texture(g_tNormal, texCoord).wy;
    #endif
}

#if (S_FLOW_NORMALS == 1)
    // The flow map moves the normal map with the current, two layers half a cycle apart crossfade so the
    // scroll can restart without a visible jump.
    vec2 SampleFlowingNormal(vec2 texCoord, vec2 flowScroll, float phase)
    {
        float phaseA = phase;
        float phaseB = phase + 0.5;
        float fractionA = fract(phaseA);
        float fractionB = fract(phaseB);

        vec2 texCoordA = texCoord + vec2(floor(phaseA) * FlowCycleTexCoordOffset) + flowScroll * fractionA;
        vec2 texCoordB = texCoord + vec2(floor(phaseB) * FlowCycleTexCoordOffset + 0.5) + flowScroll * fractionB;

        float blend = pow(abs(2.0 * fractionA - 1.0), g_flNormalFlowLerpExp);

        return mix(SampleNormalMap(texCoordA), SampleNormalMap(texCoordB), vec2(blend));
    }

    #if (S_FLOW_COLOR == 1)
        vec4 SampleFlowingColor(vec2 texCoord, vec2 flowScroll, float phase)
        {
            float phaseA = phase;
            float phaseB = phase + 0.5;
            float fractionA = fract(phaseA);
            float fractionB = fract(phaseB);

            // The color scroll is centered on its cycle, and the layers weigh each other's fraction.
            vec2 texCoordA = (texCoord + vec2(floor(phaseA) * FlowCycleTexCoordOffset) + flowScroll * (fractionA - 0.5)) * 0.235;
            vec2 texCoordB = (texCoord + vec2(floor(phaseB) * FlowCycleTexCoordOffset + 0.5) + flowScroll * (fractionB - 0.5)) * 0.235;

            return texture(g_tColor, texCoordA) * pow(abs(2.0 * fractionB - 1.0), g_flColorFlowLerpExp)
                 + texture(g_tColor, texCoordB) * pow(abs(2.0 * fractionA - 1.0), g_flColorFlowLerpExp);
        }
    #endif
#endif

void main()
{
    MaterialProperties_t mat;
    InitProperties(mat, vNormalOut, vFragPosition, vTangentOut, vBitangentOut);

    // Normal
    #if (S_FLOW_NORMALS == 1)
        // The flowing normal is used as the world normal directly, the surface is assumed to face up.
        vec2 flowTexCoord = vec2(vFragPosition.x, -vFragPosition.y) * g_flWorldPositionScale;
        vec2 flow = texture(g_tFlow, vTexCoordOut * (1.0 / g_flWorldUvScale)).xy * 2.0 - 1.0;
        float flowNoise = texture(g_tNoise, flowTexCoord * g_flNoiseUvScale).y * g_flNoiseStrength;
        float flowTime = g_flTime * g_flFlowTimeScale;

        float normalPhase = flowTime / (g_flNormalFlowTimeIntervalInSeconds * 2.0) + flowNoise;
        vec2 normalTexCoord = flowTexCoord * (1.0 / g_flNormalUvScale);
        vec2 normalXy = SampleFlowingNormal(normalTexCoord, flow * g_flNormalFlowUvScrollDistance, normalPhase) * 2.0 - 1.0;

        // Faster flow bumps harder, with a floor so still water keeps some detail.
        normalXy *= (dot(flow, flow) + 0.1) * g_flBumpStrength;

        if (dot(normalXy, normalXy) > 1.0)
        {
            normalXy = normalize(normalXy);
        }

        vec3 normal = vec3(normalXy, sqrt(saturate(1.0 - dot(normalXy, normalXy))));

        #if (S_FLOW_COLOR == 1)
            float colorPhase = flowTime / (g_flColorFlowTimeIntervalInSeconds * 2.0) + flowNoise;
            vec2 colorTexCoord = flowTexCoord * (1.0 / g_flColorFlowUvScale);
            vec4 flowColor = SampleFlowingColor(colorTexCoord, flow * g_flColorFlowUvScrollDistance, colorPhase);
        #endif
    #else
        // Three scrolling layers of the same normal map, averaged. The tangent frame is the world X and
        // Y axes rather than the vertex tangents.
        vec2 normalXy = (SampleNormalMap(vNormalTexCoord) + SampleNormalMap(vExtraTexCoords.xy) + SampleNormalMap(vExtraTexCoords.zw)) * 0.33;
        normalXy = normalXy * 2.0 - 1.0;

        vec3 normalTs = vec3(normalXy, sqrt(saturate(1.0 - dot(normalXy, normalXy))));
        normalTs.y = -normalTs.y;
        normalTs.xy *= g_flBumpStrength;
        normalTs = normalize(normalTs);

        vec3 normal = normalize(vec3(1.0, 0.0, 0.0) * normalTs.x + vec3(0.0, 1.0, 0.0) * normalTs.y + mat.GeometricNormal * normalTs.z);
    #endif

    mat.Normal = normal;
    mat.AmbientNormal = normal;

    #if (S_SHADER_QUALITY == 1)
        mat.Roughness = vec2(max(0.01, 1.0 - g_flGlossiness));
    #else
        // The fallback cube map is a single scene-wide texture that is not loaded here, so the
        // environment maps stand in for it at the same fixed roughness.
        mat.Roughness = vec2(0.99);
    #endif

    // Water fog color, optionally lit by the scene as a diffuse white surface facing the geometric normal
    vec3 waterFogColor = g_vWaterFogColor;
    LightingTerms_t lighting = InitLighting();

    #if (S_LIGHTMAP_WATER_FOG == 1)
        mat.Normal = mat.GeometricNormal;
        lighting = CalculateLighting(mat);
        waterFogColor *= lighting.DiffuseDirect + lighting.DiffuseIndirect * mat.AmbientOcclusion;
        mat.Normal = normal;
    #endif

    // Refraction
    #if (S_DISABLE_REFRACTION == 0)
        vec2 screenTexCoord = gl_FragCoord.xy / vec2(textureSize(g_tSceneColor, 0));

        // How much water sits between the surface and what the depth buffer holds behind it
        vec3 scenePosition = GetWorldPositionFromDepth(ivec2(gl_FragCoord.xy), normalize(vFragPosition - g_vCameraPositionWs));
        float waterExtent = saturate((1.0 / (g_flWaterDepth - g_flWaterStart)) * ((vFragPosition.z - g_flRefractionClipPlaneAdjust) - scenePosition.z + g_flWaterStart));

        vec2 refractedTexCoord = screenTexCoord - normal.xy * (g_flRefractionAmount * waterExtent);

        float surfaceDepth = RemapValClamped(gl_FragCoord.z, g_flViewportMinZ, g_flViewportMaxZ, 0.0, 1.0);
        float refractedDepth = RemapValClamped(textureLod(g_tSceneDepth, refractedTexCoord, 0.0).x, g_flViewportMinZ, g_flViewportMaxZ, 0.0, 1.0);
        vec3 refraction = textureLod(g_tSceneColor, refractedDepth > surfaceDepth ? screenTexCoord : refractedTexCoord, 0.0).rgb;

        refraction = mix(refraction, refraction * g_vRefractionTint, saturate(3.5 * waterExtent));
        vec3 color = mix(refraction, waterFogColor, waterExtent);
    #else
        float waterExtent = 1.0;
        vec3 color = waterFogColor;
    #endif

    // Reflection
    mat.IsometricRoughness = dot(mat.Roughness, vec2(0.5));

    vec3 reflection = GetEnvironmentNoBRDF(mat, 1.0) * saturate(2.0 * waterExtent);

    float reflectance = g_flReflectance;
    #if (S_FRESNEL == 1)
        reflectance += (1.0 - g_flReflectance) * pow(1.0 - saturate(dot(mat.ViewDir, normal)), 5.0);
    #endif

    // Reflections fade out at the shore
    float reflectionAmount = reflectance * saturate((waterExtent - 0.05) * 20.0);

    #if (S_FLOW_NORMALS == 1 && S_FLOW_COLOR == 1)
        // Flowing color rides on top of the water and pushes the reflection away where it is dense
        color = mix(color, flowColor.rgb, saturate(flowColor.a * 2.0));
        color = mix(color, reflection, saturate(reflectionAmount * (1.0 - smoothstep(0.5, 0.7, flowColor.a))));
    #else
        color += reflection * reflectionAmount;
    #endif

    // A fixed direction highlight
    color += g_vReflectionColor * pow(saturate(dot(g_vReflectionDir, reflect(mat.ViewDir, normal))), g_flReflectionPower);

    color = saturate(color);

    ApplyFog(color, vFragPosition);

    // The game writes zero alpha here
    outputColor = vec4(color, 1.0);

    if (g_iRenderMode != 0)
    {
        lighting.SpecularIndirect = reflection * reflectionAmount;

        if (!HandleMaterialRenderModes(outputColor, mat)
        && !HandleLightingRenderModes(outputColor, mat, lighting))
        {
            if (g_iRenderMode == renderMode_Cubemaps)
            {
                outputColor.rgb = SrgbLinearToGamma(reflection);
            }
        }
    }
}
