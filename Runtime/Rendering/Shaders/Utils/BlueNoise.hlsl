#ifndef CAELIX_BLUE_NOISE
#define CAELIX_BLUE_NOISE

// Launch index of the current pixel. DXR stages read it from the dispatch intrinsic; the inline
// ray query compute kernel defines this macro to its own thread index before including this file.
#ifndef CAELIX_LAUNCH_INDEX
#define CAELIX_LAUNCH_INDEX (DispatchRaysIndex().xy)
#endif

// https://developer.nvidia.com/blog/rendering-in-real-time-with-spatiotemporal-blue-noise-textures-part-1/
// Texture2D<uint2> stbnTexture;
Texture2D<half2> stbnTexture;
uint g_FrameIndex;

float2 MartinR2(uint index)
{
    return frac(index * float2(0.75487766624669276005, 0.56984029099805326591) + 0.5);
}

// The STBN texture is 64 slices of a 128x128 tile, and its whole point is the TEMPORAL property:
// for one fixed pixel position, the 64 values across the slices are blue-noise distributed. That
// only holds if a given pixel reads the same tile position on every frame, which is why nothing
// here may depend on the frame index except the slice.
float2 SampleBlueNoise(inout uint state)
{
    uint draw = state++;

    // Tile shift depends on the draw index only. It must not depend on the frame: the STBN
    // temporal property holds for a fixed pixel position across the 64 slices, and a per-frame
    // shift would walk each pixel across the tile and hand it 64 unrelated values instead.
    float2 tileShift = MartinR2(draw + 1u);
    int2 pos = (int2(CAELIX_LAUNCH_INDEX) + int2(tileShift * 128)) & 127;
    pos.y += (g_FrameIndex & 63) * 128;

    return stbnTexture[pos];
}

float RandomFloat01(inout uint state)
{
    return SampleBlueNoise(state).x;
}

float3 RandomUnitVector(float2 sample)
{
    float a = sample.x * K_TWO_PI;
    float y = sample.y * 2 - 1;
    float sy = sqrt(1.0 - y * y);
    return float3(sin(a) * sy, y, cos(a)* sy);
}

float3 RandomUnitVector(inout uint state)
{
    return RandomUnitVector(SampleBlueNoise(state));
}

#endif
