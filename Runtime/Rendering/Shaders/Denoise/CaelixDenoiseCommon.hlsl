#ifndef CAELIX_DENOISE_COMMON_INCLUDED
#define CAELIX_DENOISE_COMMON_INCLUDED

// Screen-space helpers and the G-buffer guides every Caelix screen-space filter shares.
//
// Both denoise chains (the path tracer's indirect radiance and the budget mode's AO/shadow)
// run the same kernels over the same geometry guides; only the signal being filtered differs.
// That signal is named by a macro so a shader can point a kernel at its own target without the
// kernel knowing anything about it -- see CaelixATrous.hlsl / CaelixTemporal.hlsl.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

// --- Shared G-buffer guides ---------------------------------------------------------------
// Declared here rather than per-shader because every filter below reads them under these exact
// names, and the C# side publishes them as globals once (CaelixShaderIDs).
TEXTURE2D(_NormalTex);
TEXTURE2D(_MotionVectorTex);
TEXTURE2D(_CurrentDepthHistoryTex);
TEXTURE2D(_CurrentNormalHistoryTex);

float4 _CaelixFrameSize;

// --- Shared camera state -------------------------------------------------------------------
// Everything a filter needs to rebuild the ray a given pixel was traced with, this frame or last.
// Pushed once per pass by CaelixDenoiseUniforms (C#).
float4   _CaelixJitter;          // xy = this frame's jitter in pixels, zw = previous frame's
float4   _CaelixProjection;      // x = zoom (tan(fov/2)), y = aspect
float4x4 _CaelixWorldToCamera;   // this frame's view matrix
float4x4 _CaelixPrevWorldToCamera;

uint2 CaelixPixelCoord(float2 uv)
{
    uint2 size = max(uint2(_CaelixFrameSize.xy), uint2(1, 1));
    return min((uint2)(uv * size), size - uint2(1, 1));
}

uint2 CaelixClampCoord(int2 coord)
{
    uint2 size = max(uint2(_CaelixFrameSize.xy), uint2(1, 1));
    int2 maxCoord = int2(size) - int2(1, 1);
    return (uint2)clamp(coord, int2(0, 0), maxCoord);
}

// The raygen maps a pixel to UV as (index + 0.5 + jitter) / size, so every consumer of a
// reprojected UV has to use the same denominator or history lands half a pixel off.
float2 CaelixHistoryScale()
{
    return max(_CaelixFrameSize.xy, float2(1.0f, 1.0f));
}

// View-space ray through a launch-space position (pixel index + 0.5 + jitter). Unnormalised,
// z = -1, so the point t*ray has view-forward depth t, the quantity stored in the depth targets.
// This must match the raygen mapping exactly -- same launch space, same jitter added, same
// zoom/aspect -- because the filters compare its predictions against depths the raygen wrote.
float3 CaelixViewRay(float2 launchPos)
{
    float2 ndc = (launchPos / _CaelixFrameSize.xy) * 2.0f - 1.0f;
    ndc *= _CaelixProjection.x;
    return float3(ndc.x * _CaelixProjection.y, ndc.y, -1.0f);
}

float3 CaelixUnpackNormal(float2 packedNormal)
{
    return UnpackNormalOctQuadEncode(packedNormal * 2.0f - 1.0f);
}

float CaelixLuminance(float3 color)
{
    return dot(color, float3(0.2126f, 0.7152f, 0.0722f));
}

// lowbias32 integer hash.
uint CaelixHashUint(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352dU;
    x ^= x >> 15;
    x *= 0x846ca68bU;
    x ^= x >> 16;
    return x;
}

#endif
