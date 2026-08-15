#version 460

#ifndef VRF_MSAA_INCLUDE
#define VRF_MSAA_INCLUDE

// How a resolve shader names the multisample source it reads.
//
// OpenGL is happy to give you a TEXTURE_2D_MULTISAMPLE with a single sample, and this renderer allocates
// one deliberately: the post-process chain always resolves, so making the no-MSAA case a one-sample
// multisample target means there is one code path instead of two. Vulkan has no such thing. A
// VK_SAMPLE_COUNT_1_BIT image is an ordinary image, and a shader that declares it as OpTypeImage MS 1 is
// reading a descriptor whose type does not match -- VUID-RuntimeSpirv-samples-08726 -- which makes every
// dispatch that touches it undefined rather than merely wrong.
//
// So on Vulkan, and only when there really is one sample, the source is declared as an ordinary sampler
// and fetched at level zero. texelFetch takes an int as its third argument either way, which is what lets
// the sample loop stay exactly as it is: at one sample the only index is 0, and level 0 is the only level
// these targets have.
//
// The OpenGL path is untouched by construction. VRF_VULKAN is 0 there, so it keeps the multisample
// declaration it has always had whatever the sample count is, and the oracle cannot move.
#if (VRF_VULKAN == 1) && (D_MSAA_SAMPLES == 1)
    #define VrfMsaaSampler sampler2D
    #define VrfMsaaFetch(tex, coord, sampleIndex) texelFetch(tex, coord, 0)
    #define VrfMsaaSize(tex) textureSize(tex, 0)
#else
    #define VrfMsaaSampler sampler2DMS
    #define VrfMsaaFetch(tex, coord, sampleIndex) texelFetch(tex, coord, sampleIndex)
    #define VrfMsaaSize(tex) textureSize(tex)
#endif

#endif
