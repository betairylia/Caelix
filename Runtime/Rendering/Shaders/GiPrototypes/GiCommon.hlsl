#ifndef CAELIX_GI_COMMON_INCLUDED
#define CAELIX_GI_COMMON_INCLUDED

#include "UnityRayQuery.cginc"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Assets/Caelix/VoxelMaterials.hlsl"
#include "../RayQuery/CaelixMaterialTable.hlsl"
#include "../RayPayload.hlsl"
#include "../Utils/Utils.hlsl"

#define CAELIX_INLINE_RAY_QUERY 1
static uint2 _CaelixLaunchIndex;
uint2 g_GiResolution;
#define CAELIX_LAUNCH_INDEX _CaelixLaunchIndex
#define CAELIX_LAUNCH_DIM g_GiResolution

RaytracingAccelerationStructure g_AccelStruct;
#include "../RayQuery/CaelixRayQueryTrace.hlsl"
#include "GiBsdf.hlsl"

// GPU surface record: four 16-byte rows. Runtime caches address geometry by
// instance lifetime and exact group-local voxel face, independent of pool slots.
struct GiSurface
{
    float3 position;
    uint materialId;
    float3 normal;
    uint flags;
    float3 previousPosition;
    float depth;
    uint2 faceKey;
    uint2 reserved;
};

StructuredBuffer<GiSurface> g_GiSurfaces;
RWStructuredBuffer<GiSurface> g_GiSurfacesOut;
RWTexture2D<float4> g_GiOutputColor;
RWTexture2D<float> g_GiOutputDepth;

uint g_GiSamplesPerPixel;
uint g_GiMaxBounces;
uint g_GiFrameIndex;
float3 g_GiCameraPosition;
float4x4 g_GiCameraToWorld;
float4x4 g_GiWorldToCamera;
float g_GiZoom;
float g_GiAspect;
float2 g_GiJitter;
float g_GiSkyIntensity;
TextureCube<float4> g_Sky;
SamplerState sampler_g_Sky;

bool GiHasHit(GiSurface surface)
{
    return (surface.flags & 1u) != 0u;
}

uint GiSeed(uint2 pixel, uint sampleIndex)
{
    return GiHash(pixel.x + pixel.y * g_GiResolution.x ^ GiHash(g_GiFrameIndex + 1u) ^ GiHash(sampleIndex + 0x9e3779b9u));
}

float3 GiSanitize(float3 radiance)
{
    return any(isnan(radiance)) || any(isinf(radiance)) ? 0.0 : max(0.0, radiance);
}

float3 GiSky(float3 direction)
{
    return g_Sky.SampleLevel(sampler_g_Sky, direction, 0).rgb * g_GiSkyIntensity;
}

RayDesc GiCameraRay(uint2 pixel, float2 jitter)
{
    float2 ndc = ((float2(pixel) + 0.5 + jitter) / float2(g_GiResolution)) * 2.0 - 1.0;
    float3 viewDirection = normalize(float3(ndc * float2(g_GiAspect, 1.0) * g_GiZoom, -1.0));
    RayDesc ray;
    ray.Origin = g_GiCameraPosition;
    ray.Direction = normalize(mul((float3x3)g_GiCameraToWorld, viewDirection));
    ray.TMin = 0.0;
    ray.TMax = K_T_MAX;
    return ray;
}

RayDesc GiRayFromSurface(GiSurface surface, float3 direction)
{
    RayDesc ray;
    ray.Origin = surface.position + surface.normal * (dot(direction, surface.normal) >= 0.0 ? K_RAY_ORIGIN_PUSH_OFF : -K_RAY_ORIGIN_PUSH_OFF);
    ray.Direction = normalize(direction);
    ray.TMin = 0.0;
    ray.TMax = K_T_MAX;
    return ray;
}

// This adapter retains query identity outside RayPayload. Existing renderers keep
// their compact payload and the common brick-intersection implementation.
bool GiTrace(RayDesc ray, out GiSurface surface)
{
    surface = (GiSurface)0;
    UnityRayQuery<RAY_FLAG_FORCE_OPAQUE> query;
    query.TraceRayInline(g_AccelStruct, RAY_FLAG_NONE, 0xFF, ray);
    uint committedAttribute = 0u;
    uint committedInstance = 0u;
    while (query.Proceed())
    {
        if (query.CandidateType() != CANDIDATE_PROCEDURAL_PRIMITIVE) continue;
        uint instanceId = query.CandidateInstanceID();
        CaelixRayQueryInstance instance = g_Instances[instanceId];
        _CaelixBrickPage = instance.page;
        uint brickBase = instance.brickBase + CaelixBrickBase(query.CandidatePrimitiveIndex());
        AttributeData attribute;
        float distance = CaelixTraceBrickPrimitiveCore(brickBase, query.CandidateObjectRayOrigin(), query.CandidateObjectRayDirection(), query.CommittedRayT(), attribute);
        if (attribute.matID_faceNormal != 0u && distance >= ray.TMin && distance < query.CommittedRayT())
        {
            query.CommitProceduralPrimitiveHit(distance);
            committedAttribute = attribute.matID_faceNormal;
            committedInstance = instanceId;
        }
    }
    if (query.CommittedStatus() != COMMITTED_PROCEDURAL_PRIMITIVE_HIT) return false;

    float distance = query.CommittedRayT();
    float3 localNormal = UnpackObjectNormal(committedAttribute);
    float3 localPosition = query.CommittedObjectRayOrigin() + query.CommittedObjectRayDirection() * distance;
    uint3 voxel = uint3(max(0.0, floor(localPosition - localNormal * 0.001)));
    uint face = firstbitlow(committedAttribute & 63u);
    CaelixRayQueryInstance instance = g_Instances[committedInstance];
    surface.position = ray.Origin + ray.Direction * distance;
    surface.materialId = committedAttribute >> 16;
    surface.normal = normalize(mul((float3x3)query.CommittedObjectToWorld3x4(), localNormal));
    surface.flags = 1u;
    surface.previousPosition = mul(CaelixInstancePrevObjectToWorld(instance), float4(localPosition, 1.0)).xyz;
    surface.depth = -mul(g_GiWorldToCamera, float4(surface.position, 1.0)).z;
    surface.faceKey = uint2(instance.pad1, voxel.x | (voxel.y << 9u) | (voxel.z << 19u) | (face << 28u));
    return true;
}

GiBsdf GiMakeBsdf(GiSurface surface, uint currentMedium)
{
    bool transparent = !IsOpaque(int(surface.materialId));
    uint destination = transparent ? uint(GetTransparentMaterialId(int(surface.materialId))) : currentMedium;
    // Exit interfaces name air; the roughness then belongs to the material exited.
    uint shadingId = transparent && destination == 0u ? currentMedium : surface.materialId;
    VoxelMaterial material = GET_MATERIAL(int(shadingId));
    VoxelMaterial source = GET_MATERIAL(int(currentMedium));
    GiBsdf bsdf;
    bsdf.baseColor = saturate(material.albedo);
    bsdf.emission = max(0.0, material.emission);
    bsdf.roughness = saturate(1.0 - material.smoothness);
    bsdf.metallic = saturate(material.metallic);
    bsdf.etaI = currentMedium != 0u ? max(1.0, source.IOR) : 1.0;
    bsdf.etaT = transparent && destination == 0u ? 1.0 : max(1.0, material.IOR);
    bsdf.transparent = transparent ? 1u : 0u;
    bsdf.destinationMedium = destination;
    return bsdf;
}

float3 GiMediumTransmittance(uint medium, float distance)
{
    if (medium == 0u) return 1.0;
    VoxelMaterial material = GET_MATERIAL(int(medium));
    return exp(-max(0.0, 1.0 - material.albedo) * max(0.0, material.extinction) * distance);
}

float3 GiTracePath(RayDesc ray, uint currentMedium, inout uint rng, uint maxBounces)
{
    float3 radiance = 0.0;
    float3 throughput = 1.0;
    float etaScale = 1.0;
    for (uint bounce = 0u; bounce <= maxBounces; ++bounce)
    {
        GiSurface surface;
        if (!GiTrace(ray, surface))
        {
            radiance += throughput * GiMediumTransmittance(currentMedium, K_T_MAX) * GiSky(ray.Direction);
            break;
        }
        throughput *= GiMediumTransmittance(currentMedium, length(surface.position - ray.Origin));
        GiBsdf bsdf = GiMakeBsdf(surface, currentMedium);
        radiance += throughput * bsdf.emission;
        if (bounce == maxBounces) break;
        GiBsdfSample sample = GiSample(bsdf, surface.normal, -ray.Direction, rng);
        if (sample.pdf <= 0.0 || !any(sample.weight > 0.0)) break;
        throughput *= sample.weight;
        if (sample.transmission != 0u) etaScale *= (bsdf.etaT * bsdf.etaT) / (bsdf.etaI * bsdf.etaI);
        currentMedium = GiNextMedium(bsdf, sample, currentMedium);
        if (bounce >= 3u)
        {
            float survival = min(0.95, max(throughput.x, max(throughput.y, throughput.z)) * etaScale);
            if (GiRandom(rng) >= survival || survival <= 0.0) break;
            throughput /= survival;
        }
        ray = GiRayFromSurface(surface, sample.direction);
    }
    return GiSanitize(radiance);
}

float3 GiTraceFromSurface(GiSurface surface, float3 wo, uint currentMedium, inout uint rng, uint maxBounces)
{
    GiBsdf bsdf = GiMakeBsdf(surface, currentMedium);
    if (maxBounces == 0u) return bsdf.emission;
    GiBsdfSample sample = GiSample(bsdf, surface.normal, wo, rng);
    if (sample.pdf <= 0.0) return bsdf.emission;
    RayDesc ray = GiRayFromSurface(surface, sample.direction);
    return GiSanitize(bsdf.emission + sample.weight * GiTracePath(ray, GiNextMedium(bsdf, sample, currentMedium), rng, maxBounces - 1u));
}

#endif
