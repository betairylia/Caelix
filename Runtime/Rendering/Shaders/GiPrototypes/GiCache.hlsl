#ifndef CAELIX_GI_CACHE_INCLUDED
#define CAELIX_GI_CACHE_INCLUDED

#define GI_CACHE_INVALID 0xffffffffu
#define GI_CACHE_PROBES 16u
#define GI_CACHE_FRAME_SAMPLES 1024u
#define GI_CACHE_FIXED_SCALE 256.0f
#define GI_CACHE_SAMPLE_LIMIT 16383.0f
#define GI_CACHE_PI 3.14159265358979323846f

struct GiCacheKey
{
    uint state;
    uint2 faceKey;
};

struct GiCacheHistory
{
    float4 irradiance;
    float4 guide0;
    float4 guide1;
    uint4 age;
};

struct GiCacheAccumulation
{
    uint3 irradiance;
    uint irradianceCount;
    uint guide[8];
    uint guideCount;
};

globallycoherent RWStructuredBuffer<GiCacheKey> g_GiCacheKeys;
StructuredBuffer<GiCacheHistory> g_GiCacheHistory;
RWStructuredBuffer<GiCacheHistory> g_GiCacheNextHistory;
RWStructuredBuffer<GiCacheAccumulation> g_GiCacheAccum;
uint g_GiCacheCapacity;
uint g_GiCacheFrame;
uint g_GiCacheMaxAge;
uint g_GiCacheHistoryLimit;
uint g_GiCacheMinSamples;

uint GiCacheHash(uint2 key)
{
    uint h = key.x * 0x9e3779b9u + key.y;
    h = (h ^ (h >> 16u)) * 0x7feb352du;
    h = (h ^ (h >> 15u)) * 0x846ca68bu;
    return h ^ (h >> 16u);
}

uint GiCacheFind(uint2 key, bool allocate)
{
    if (g_GiCacheCapacity == 0u || key.x == 0u) return GI_CACHE_INVALID;
    uint start = GiCacheHash(key) % g_GiCacheCapacity;
    uint emptySlot = GI_CACHE_INVALID;
    [loop] for (uint probe = 0u; probe < min(GI_CACHE_PROBES, g_GiCacheCapacity); ++probe)
    {
        uint slot = (start + probe) % g_GiCacheCapacity;
        uint state;
        InterlockedAdd(g_GiCacheKeys[slot].state, 0u, state);
        // Never spin on another lane's claim: SIMT lanes may share a wave.
        if (state == 1u) return GI_CACHE_INVALID;
        if (state == 2u && all(g_GiCacheKeys[slot].faceKey == key)) return slot;
        if (state == 0u && emptySlot == GI_CACHE_INVALID) emptySlot = slot;
    }
    if (!allocate || emptySlot == GI_CACHE_INVALID) return GI_CACHE_INVALID;
    uint original;
    InterlockedCompareExchange(g_GiCacheKeys[emptySlot].state, 0u, 1u, original);
    if (original != 0u) return GI_CACHE_INVALID;
    g_GiCacheKeys[emptySlot].faceKey = key;
    DeviceMemoryBarrier();
    InterlockedExchange(g_GiCacheKeys[emptySlot].state, 2u, original);
    return emptySlot;
}

bool GiCacheQuery(uint slot, out float3 irradianceOverPi)
{
    irradianceOverPi = 0.0f;
    if (slot == GI_CACHE_INVALID) return false;
    GiCacheHistory h = g_GiCacheHistory[slot];
    if (h.irradiance.w < max(1u, g_GiCacheMinSamples)
        || g_GiCacheFrame - h.age.x > g_GiCacheMaxAge) return false;
    irradianceOverPi = h.irradiance.xyz;
    return true;
}

uint GiCacheEncode(float sample)
{
    return uint(round(clamp(sample, 0.0f, GI_CACHE_SAMPLE_LIMIT) * GI_CACHE_FIXED_SCALE));
}

// The caller supplies a fresh full-path suffix estimate, divided by its actual
// sampling density. Cache lookups must not recursively train themselves.
void GiCacheTrainRadiance(uint slot, float3 irradianceOverPiSample)
{
    if (slot == GI_CACHE_INVALID || any(!isfinite(irradianceOverPiSample))) return;
    uint sampleIndex;
    InterlockedAdd(g_GiCacheAccum[slot].irradianceCount, 1u, sampleIndex);
    if (sampleIndex >= GI_CACHE_FRAME_SAMPLES) return;
    InterlockedAdd(g_GiCacheAccum[slot].irradiance.x, GiCacheEncode(irradianceOverPiSample.x));
    InterlockedAdd(g_GiCacheAccum[slot].irradiance.y, GiCacheEncode(irradianceOverPiSample.y));
    InterlockedAdd(g_GiCacheAccum[slot].irradiance.z, GiCacheEncode(irradianceOverPiSample.z));
}

void GiGuideBasis(float3 n, out float3 t, out float3 b)
{
    t = normalize(cross(abs(n.z) < 0.999f ? float3(0, 0, 1) : float3(0, 1, 0), n));
    b = cross(n, t);
}

uint GiGuideBin(float3 n, float3 wi)
{
    float3 t, b;
    GiGuideBasis(n, t, b);
    float phi = atan2(dot(wi, b), dot(wi, t));
    if (phi < 0.0f) phi += 2.0f * GI_CACHE_PI;
    uint azimuth = min(3u, uint(phi * (2.0f / GI_CACHE_PI)));
    return azimuth + 4u * min(1u, uint(saturate(dot(n, wi)) * 2.0f));
}

void GiCacheTrainGuide(uint slot, float3 n, float3 wi, float incidentLuminanceOverPdf)
{
    if (slot == GI_CACHE_INVALID || dot(n, wi) <= 0.0f || !isfinite(incidentLuminanceOverPdf)) return;
    uint sampleIndex;
    InterlockedAdd(g_GiCacheAccum[slot].guideCount, 1u, sampleIndex);
    if (sampleIndex >= GI_CACHE_FRAME_SAMPLES) return;
    uint bin = GiGuideBin(n, wi);
    InterlockedAdd(g_GiCacheAccum[slot].guide[bin], GiCacheEncode(incidentLuminanceOverPdf));
}

bool GiGuideAvailable(uint slot)
{
    if (slot == GI_CACHE_INVALID) return false;
    GiCacheHistory h = g_GiCacheHistory[slot];
    return h.age.z >= max(1u, g_GiCacheMinSamples) && g_GiCacheFrame - h.age.y <= g_GiCacheMaxAge
        && dot(h.guide0 + h.guide1, float4(1, 1, 1, 1)) > 0.0f;
}

float GiGuideBinWeight(GiCacheHistory h, uint bin)
{
    float total = dot(h.guide0 + h.guide1, float4(1, 1, 1, 1));
    float value = bin < 4u ? h.guide0[bin] : h.guide1[bin - 4u];
    // Every direction retains support even before a bright path is discovered.
    return total > 0.0f ? 0.9f * max(0.0f, value) / total + 0.1f / 8.0f : 1.0f / 8.0f;
}

float GiGuidePdf(uint slot, float3 n, float3 wi)
{
    if (slot == GI_CACHE_INVALID || dot(n, wi) <= 0.0f) return 0.0f;
    return GiGuideBinWeight(g_GiCacheHistory[slot], GiGuideBin(n, wi)) * (4.0f / GI_CACHE_PI);
}

float3 GiGuideSample(uint slot, float3 n, float2 u, out float pdf)
{
    GiCacheHistory h = g_GiCacheHistory[slot];
    float residual = min(u.x, 0.99999994f);
    uint bin = 7u;
    float probability = 1.0f / 8.0f;
    [unroll] for (uint candidate = 0u; candidate < 8u; ++candidate)
    {
        probability = GiGuideBinWeight(h, candidate);
        if (residual < probability || candidate == 7u)
        {
            bin = candidate;
            break;
        }
        residual -= probability;
    }
    float z = (float(bin >> 2u) + saturate(residual / probability)) * 0.5f;
    float phi = (float(bin & 3u) + u.y) * (0.5f * GI_CACHE_PI);
    float r = sqrt(max(0.0f, 1.0f - z * z));
    float3 t, b;
    GiGuideBasis(n, t, b);
    pdf = probability * (4.0f / GI_CACHE_PI);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * z);
}

#endif
