#ifndef CAELIX_PACKED_SCENE_MATERIAL
#define CAELIX_PACKED_SCENE_MATERIAL
#include "CaelixSharedGlass.hlsl"

// Include after the application defines VoxelMaterial. RGB is stored in the same
// numeric color space as the legacy color path; emission shares that RGB value.
static VoxelMaterial GetMaterial_Color(int ID)
{
    uint code = uint(ID) & 65535u;
    if ((code & 0xC000u) == 0u)
        return GetSharedGlass(code & 255u); // Faces never participate in medium identity.

    VoxelMaterial m;
    m.smoothness = 0; m.metallic = 0; m.IOR = 1.5; m.extinction = 0;
    if ((code & 0x8000u) != 0u)
    {
        m.albedo = float3((code >> 10) & 31u, (code >> 5) & 31u, code & 31u) / 31.0;
        m.emission = 0;
    }
    else
    {
        m.albedo = float3((code >> 10) & 15u, (code >> 6) & 15u, (code >> 2) & 15u) / 15.0;
        m.emission = m.albedo * exp2(3.0 * (code & 3u)) * CAELIX_PACKED_EMISSION_SCALE;
    }
    return m;
}
#endif
