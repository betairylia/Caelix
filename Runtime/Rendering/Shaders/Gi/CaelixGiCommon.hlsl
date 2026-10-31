#ifndef CAELIX_GI_COMMON_INCLUDED
#define CAELIX_GI_COMMON_INCLUDED

// Data layouts and helpers of the GI resampler (Documentation~/internals/rendering/GI_RESAMPLING.md).
// Every struct here is mirrored by a stride constant in CaelixGiResources.cs; change both together.
// Needs Packing.hlsl (PackNormalOctQuadEncode) included first.

#define GI_BUCKET_SIZE 8u
#define GI_REFINED_PER_BUCKET 32u
#define GI_COMPACT_PER_BUCKET 8u
#define GI_HISTORY_SLOTS 128u
// DispatchIndirect X limit is 65535 groups; the sample count rounded to a power of two is split
// over X and Y with this many groups per row.
#define GI_MAX_GROUPS_X 32768u

// primaryNormal flag (bit 8 of GiLitSample.primaryNormal) and the matching bit 3 of GiBucketSample.normals.
#define GI_SAMPLE_SKY_FLAG 0x100u
#define GI_BUCKET_SKY_FLAG 0x8u

// Key-target flags (GiKeyTarget.z).
#define GI_KEY_FLAG_HIT 1u
#define GI_KEY_FLAG_DIFFUSE 2u
#define GI_KEY_FLAG_OPAQUE 4u

struct GiLitSample            // 56 bytes, ring of litRingFrames * pixels
{
    uint   key;               // pathHash16 | instanceSlot << 16
    uint   token;             // instance lifetime token at sample time
    float3 objectPosition;    // primary surface, object space of the instance
    float3 virtualOffset;     // virtual point minus real point, world space (0 without delta chain)
    uint   primaryNormal;     // axis index 0..5 | flags << 8 (bit 8 = sky sample)
    float3 secondaryPosition; // reconnection point, world space (unit direction for sky samples)
    uint   secondaryNormal;   // oct-encoded, 2 x 16 bit (unused for sky)
    uint   radiance;          // RGB9E5, incident radiance along the sample direction
};

struct GiUnlitSample          // 36 bytes, ring of unlitRingFrames * pixels
{
    uint   key;
    uint   token;
    float3 objectPosition;
    float3 virtualOffset;
    uint   primaryNormal;
};

struct GiBucketSample         // 32 bytes; refined: buckets * 32, compact: buckets * 8
{
    float3 primaryPosition;   // current world position of the sample's primary surface
    uint   normals;           // primary axis (bits 0-2) | sky flag (bit 3) | secondary oct x 12 bits << 8 | oct y 12 bits << 20
    float3 secondaryPosition; // world (unit direction for sky)
    uint   radiance;          // RGB9E5
};

struct GiBucketInfo           // 32 bytes, buckets
{
    uint  litCount;           // atomic, this frame
    uint  unlitCount;         // atomic, this frame (each stored unlit sample counts unlitKeepDivisor)
    uint  distRange;          // f16 min | f16 max of the bucket's pixels' virtual distance
    uint  packed;             // storedCount (4) | normalMask6 << 4 | totalComp3 << 10
    float litRatio;
    float maxLuminance;       // of the stored lit samples
    uint  reserved0;
    uint  reserved1;
};

struct GiPixelInfo            // 8 bytes, pixels
{
    uint distRange;           // f16 min | f16 max of the 3x3 virtual distances
    uint hashes;              // centre hash16 | bloom16 << 16
};

// Counts buffer word indices.
#define GI_COUNT_LIT_HEAD 0u
#define GI_COUNT_UNLIT_HEAD 1u
#define GI_COUNT_LIT_VALID 2u
#define GI_COUNT_UNLIT_VALID 3u
#define GI_COUNT_LIT_START 4u
#define GI_COUNT_UNLIT_START 5u
#define GI_COUNT_LIT_COPRIME 6u
#define GI_COUNT_UNLIT_COPRIME 7u
#define GI_COUNT_FRAME_BASE 8u      // + 2 * slot (lit), + 2 * slot + 1 (unlit)

// --- Axis normals ---------------------------------------------------------------------------

// 0..5 as +X, -X, +Y, -Y, +Z, -Z (the objectNormals order in CaelixBrickTrace.hlsl). The dominant
// component wins, so a world normal of a rotated entity still maps to one axis.
uint GiAxisIndex(float3 n)
{
    float3 a = abs(n);
    if (a.x >= a.y && a.x >= a.z)
    {
        return n.x >= 0.0f ? 0u : 1u;
    }

    if (a.y >= a.z)
    {
        return n.y >= 0.0f ? 2u : 3u;
    }

    return n.z >= 0.0f ? 4u : 5u;
}

// --- Radiance: RGB9E5 -----------------------------------------------------------------------

#define GI_RGB9E5_EXP_BIAS 15
#define GI_RGB9E5_MANTISSA_BITS 9
#define GI_RGB9E5_MAX 65408.0f      // (2^9 - 1) / 2^9 * 2^16

uint GiPackRadiance(float3 rgb)
{
    // NaN fails every comparison, so it lands on 0 here rather than in the exponent math.
    float3 c = float3(
        rgb.r > 0.0f ? min(rgb.r, GI_RGB9E5_MAX) : 0.0f,
        rgb.g > 0.0f ? min(rgb.g, GI_RGB9E5_MAX) : 0.0f,
        rgb.b > 0.0f ? min(rgb.b, GI_RGB9E5_MAX) : 0.0f);
    float maxc = max(c.r, max(c.g, c.b));
    if (maxc < 1.0e-9f)
    {
        return 0u;
    }

    int expShared = max(-GI_RGB9E5_EXP_BIAS - 1, int(floor(log2(maxc)))) + 1 + GI_RGB9E5_EXP_BIAS;
    float denom = exp2(float(expShared - GI_RGB9E5_EXP_BIAS - GI_RGB9E5_MANTISSA_BITS));
    if (uint(floor(maxc / denom + 0.5f)) >= 512u)
    {
        denom *= 2.0f;
        expShared += 1;
    }

    expShared = clamp(expShared, 0, 31);
    uint3 m = min(uint3(floor(c / denom + 0.5f)), uint3(511u, 511u, 511u));
    return m.r | (m.g << 9) | (m.b << 18) | (uint(expShared) << 27);
}

float3 GiUnpackRadiance(uint packed)
{
    float scale = exp2(float(int(packed >> 27) - GI_RGB9E5_EXP_BIAS - GI_RGB9E5_MANTISSA_BITS));
    return float3(packed & 511u, (packed >> 9) & 511u, (packed >> 18) & 511u) * scale;
}

float GiLuminance(float3 c)
{
    return dot(c, float3(0.2126f, 0.7152f, 0.0722f));
}

// --- Secondary normal: octahedral -----------------------------------------------------------

// 16 bits per component, as stored in GiLitSample.secondaryNormal.
uint GiPackOct16(float3 n)
{
    float2 o = saturate(PackNormalOctQuadEncode(normalize(n)) * 0.5f + 0.5f);
    uint2 q = uint2(o * 65535.0f + 0.5f);
    return q.x | (q.y << 16);
}

// 16-bit pair to the 12-bit pair GiBucketSample.normals carries in bits 8..31.
uint GiOct16To12(uint oct16)
{
    return (((oct16 & 0xFFFFu) >> 4) << 8) | (((oct16 >> 16) >> 4) << 20);
}

float3 GiUnpackOct12(uint normals)
{
    float2 o = float2((normals >> 8) & 0xFFFu, (normals >> 20) & 0xFFFu) / 4095.0f;
    return normalize(UnpackNormalOctQuadEncode(o * 2.0f - 1.0f));
}

// The G-buffer normal (NormalTarget.xy, Unity oct quad encoding in [0, 1]).
float3 GiDecodeGBufferNormal(float2 packedNormal)
{
    return normalize(UnpackNormalOctQuadEncode(packedNormal * 2.0f - 1.0f));
}

// --- f16 pairs ------------------------------------------------------------------------------

uint GiPackF16Pair(float lo, float hi)
{
    return (f32tof16(lo) & 0xFFFFu) | ((f32tof16(hi) & 0xFFFFu) << 16);
}

float2 GiUnpackF16Pair(uint v)
{
    return float2(f16tof32(v & 0xFFFFu), f16tof32(v >> 16));
}

// --- Random numbers -------------------------------------------------------------------------

// PCG hash (Jarzynski and Olano 2020).
uint GiPcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

uint GiRandSeed(uint2 pixel, uint frame, uint kernelId)
{
    return GiPcg(pixel.x + GiPcg(pixel.y + GiPcg(frame ^ (kernelId * 0x9E3779B9u))));
}

// Uniform in [0, 1).
float GiRand(inout uint state)
{
    state = GiPcg(state);
    return float(state >> 8) * (1.0f / 16777216.0f);
}

float2 GiRand2(inout uint state)
{
    float x = GiRand(state);
    float y = GiRand(state);
    return float2(x, y);
}

uint GiNextPow2(uint x)
{
    return x <= 1u ? 1u : (1u << (firstbithigh(x - 1u) + 1u));
}

// --- Camera ---------------------------------------------------------------------------------
// Pushed by CaelixGiPass from CaelixCameraHistory, the same values the tracer used this frame.

uint2 g_GiFrameSize;
float2 g_GiJitter;            // pixels, launch space
float2 g_GiProjection;        // x = zoom (tan(fov / 2)), y = aspect
float4x4 g_GiWorldToCamera;
float4x4 g_GiCameraToWorld;
float3 g_GiCameraPosition;

// World direction of the primary ray through texel `pixel`: the tracer's mapping exactly
// (pixel centre + global jitter, divided by the size; RT_GBUFFER_LAYOUT.md).
float3 GiCameraRayDirection(uint2 pixel)
{
    float2 ndc = (float2(pixel) + 0.5f + g_GiJitter) / float2(g_GiFrameSize);
    ndc = (ndc * 2.0f - 1.0f) * g_GiProjection.x;
    float3 viewDirection = normalize(float3(ndc.x * g_GiProjection.y, ndc.y, -1.0f));
    return mul((float3x3)g_GiCameraToWorld, viewDirection);
}

// Projects a world point to the texel whose primary ray passes closest to it this frame.
bool GiProjectToTexel(float3 worldPosition, out int2 texel)
{
    texel = int2(0, 0);
    float3 view = mul(g_GiWorldToCamera, float4(worldPosition, 1.0f)).xyz;
    float depth = -view.z;
    if (depth <= 0.0f)
    {
        return false;
    }

    float2 ndc = view.xy / (depth * float2(g_GiProjection.x * g_GiProjection.y, g_GiProjection.x));
    float2 launch = (ndc * 0.5f + 0.5f) * float2(g_GiFrameSize) - 0.5f - g_GiJitter;
    texel = int2(floor(launch + 0.5f));
    return all(texel >= 0) && all(texel < int2(g_GiFrameSize));
}

uint GiBucketsX()
{
    return (g_GiFrameSize.x + GI_BUCKET_SIZE - 1u) / GI_BUCKET_SIZE;
}

uint GiBucketIndex(uint2 texel)
{
    return (texel.x / GI_BUCKET_SIZE) + (texel.y / GI_BUCKET_SIZE) * GiBucketsX();
}

#endif
