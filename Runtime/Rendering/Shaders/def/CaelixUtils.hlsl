#ifndef CAELIX_UTILS
#define CAELIX_UTILS
#include "CaelixMaterialConfig.hlsl"

// #define half min16float
// #define half2 min16float2
// #define half3 min16float3
// #define half4 min16float4
// #define half3x3 min16float3x3
// #define half3x4 min16float3x4

#define half float
#define half2 float2
#define half3 float3
#define half4 float4
#define half3x3 float3x3
#define half3x4 float3x4

// Classification must match Core's BlockEncoding, including emissive surfaces.
inline bool IsOpaque(int blk)
{
#if defined(CAELIX_PACKED_SCENE_COLOR)
    return (blk & 0xC000) != 0;
#else
    return (blk & 0x8000) != 0;
#endif
}

inline int GetFaceBits(int blk)
{
#if defined(CAELIX_PACKED_SCENE_COLOR)
    return (blk >> 8) & 63;
#else
    return (blk >> 9) & 63;
#endif
}

inline int GetTransparentMaterialId(int blk)
{
#if defined(CAELIX_PACKED_SCENE_COLOR)
    return blk & 255;
#else
    return blk & 511;
#endif
}

#endif
