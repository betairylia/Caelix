#ifndef CAELIX_RAY_PAYLOAD_INCLUDED
#define CAELIX_RAY_PAYLOAD_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

// TODO: PAQ. I cannot do them ...
struct RayPayload
{
    float T;
    uint materialID_voxelFaceHash;
    uint packedWorldNormal;
    uint2 packedPrevWorldOffset;
    // RTAS instance ID (= instance table slot) of the committed hit; 0 on a miss. The GI mode
    // G-buffer uses it to store the primary surface in the object space of its instance.
    uint instanceId;
};

inline void CaelixClearRayPayload(out RayPayload payload)
{
    payload.T = 0.0f;
    payload.materialID_voxelFaceHash = 0u;
    payload.packedWorldNormal = 0u;
    payload.packedPrevWorldOffset = uint2(0u, 0u);
    payload.instanceId = 0u;
}

inline bool CaelixRayPayloadHasHit(RayPayload payload)
{
    return payload.materialID_voxelFaceHash != 0u;
}

inline uint CaelixPackHalf2(float2 value)
{
    return (f32tof16(value.x) & 0xFFFFu) | ((f32tof16(value.y) & 0xFFFFu) << 16);
}

inline float2 CaelixUnpackHalf2(uint value)
{
    return float2(
        f16tof32(value & 0xFFFFu),
        f16tof32((value >> 16) & 0xFFFFu));
}

inline uint CaelixPackWorldNormal(float3 normal)
{
    return CaelixPackHalf2(PackNormalOctQuadEncode(normalize(normal)));
}

inline float3 CaelixUnpackWorldNormal(uint packedNormal)
{
    return normalize(UnpackNormalOctQuadEncode(CaelixUnpackHalf2(packedNormal)));
}

inline uint2 CaelixPackFloat3ToHalf4(float3 value)
{
    return uint2(
        CaelixPackHalf2(value.xy),
        CaelixPackHalf2(float2(value.z, 0.0f)));
}

// I kinda forgot, why unpack float3 from half4?
inline float3 CaelixUnpackFloat3FromHalf4(uint2 value)
{
    return float3(CaelixUnpackHalf2(value.x), CaelixUnpackHalf2(value.y).x);
}

#endif
