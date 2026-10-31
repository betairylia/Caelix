#ifndef CAELIX_PATH_TRACE_COMMON_INCLUDED
#define CAELIX_PATH_TRACE_COMMON_INCLUDED

// The whole path tracer. This file has no #include and no #pragma
// of its own: the entry file must already have included URP Core.hlsl, Packing.hlsl,
// Assets/Caelix/VoxelMaterials.hlsl, RayPayload.hlsl, Utils/Utils.hlsl, Utils/BlueNoise.hlsl,
// def/CaelixRayPayloadUtils.hlsl and def/CaelixUtils.hlsl, and must have declared g_AccelStruct.
//
// The including entry file defines:
//   CAELIX_TRACE_RAY(ray, payload)  -- trace `ray` (RayDesc) and fill `payload` (RayPayload); a miss leaves the payload cleared
//   CAELIX_LAUNCH_INDEX             -- uint2, this pixel's launch index (the kernel's SV_DispatchThreadID.xy)
//   CAELIX_LAUNCH_DIM               -- uint2, the launch size
#ifndef CAELIX_TRACE_RAY
#error "CaelixPathTraceCommon.hlsl: define CAELIX_TRACE_RAY before including"
#endif

#ifndef CAELIX_LAUNCH_INDEX
#error "CaelixPathTraceCommon.hlsl: define CAELIX_LAUNCH_INDEX before including"
#endif

#ifndef CAELIX_LAUNCH_DIM
#error "CaelixPathTraceCommon.hlsl: define CAELIX_LAUNCH_DIM before including"
#endif

#define CAELIXRT_BOUNCE 1

/////////////////////////////////////////////////////////////////////////////////////////////////
// GLOBALS
/////////////////////////////////////////////////////////////////////////////////////////////////

// View mat etc.
float g_Zoom;
float g_AspectRatio;
// Sub-pixel offset of EVERY primary ray this frame, in pixels, in launch space. One global value
// rather than a per-pixel draw: the denoise guides have to rebuild the exact ray a pixel was traced
// with, and they can only do that from a value the whole image shares.
float2 g_Jitter;
float3 g_CameraWorldPosition;
float4x4 g_CurrentWorldToCamera;
float4x4 g_CurrentCameraToWorld;

// Temporal accumulation
uint g_ConvergenceStep;
float4x4 g_PrevWorldToCamera;

// Path tracing
uint g_BounceCountOpaque;
uint g_BounceCountTransparent;
uint g_spp;

// Sunlight
uint g_EnableSkySun;
float g_SkySunDiskRadius;
float g_SkySunFlareRadius;
float4 g_mainLightColor;

/////////////////////////////////////////////////////////////////////////////////////////////////
// BUFFERS
/////////////////////////////////////////////////////////////////////////////////////////////////

// All radiance that is not denoised (primary emission, primary shadow ray, sky on miss).
RWTexture2D<float4> DeterministicRadianceTarget;
// Stochastic path radiance split by the lobe sampled at the primary hit, NRD-style:
// .rgb = radiance (albedo-demodulated at composite time), .a = first-segment hit distance.
RWTexture2D<float4> DiffuseRadianceTarget;
RWTexture2D<float4> SpecularRadianceTarget;
RWTexture2D<float4> AlbedoTarget;
RWTexture2D<float4> NormalTarget;
RWTexture2D<float> DepthTarget;
RWTexture2D<float4> MotionVectorTarget;
RWTexture2D<float> g_CurrentDepthHistory;
RWTexture2D<float4> g_CurrentNormalHistory;
TextureCube<float4> g_Sky;
SamplerState sampler_g_Sky;

#if defined(CAELIX_GI_MODE)
// GI resampling inputs (Documentation~/internals/rendering/GI_RESAMPLING.md).
// .xyz = real world position of the surface the delta chain ended on, .w = virtual distance along
// the camera ray (path.T); (0, 0, 0, K_T_MAX) on a miss.
RWTexture2D<float4> GiSurfaceTarget;
// .x = pathHash16 | instanceId << 16, .y = instance lifetime token,
// .z = flags (bit 0 hit, bit 1 wants a diffuse GI estimate, bit 2 opaque), .w = block ID. 0 on a miss.
RWTexture2D<uint4> GiKeyTarget;
#endif

struct CaelixPathState
{
    float3 throughput;
    float T;
    uint bounceIndex;
    uint rngState;
    int previousTransparentMaterial;
    // Hash of every delta interface the primary chain crossed (material, face, reflect/refract).
    // 0 = no interface. The GI resampler compares it at a sample's landing pixel.
    uint pathHash;
};

#define BOUNCEIDX_IS_SPECULAR 0x80000000
// Set when the first transparent interface actually split into a reflect/refract checkerboard.
// Published to MotionVectorTarget.a so the final cross resolve knows which pixels to average.
#define BOUNCEIDX_IS_PSR 0x40000000
#define BOUNCEIDX_DETERM_MASK 0xF000
#define BOUNCEIDX_DETERM_SHIFT 12
#define BOUNCEIDX_STOCHASTIC_MASK 0x000F

void CaelixInitPathState(out CaelixPathState path, uint rngState)
{
    path.throughput = float3(1, 1, 1);
    path.T = 0;
    path.bounceIndex = 0u;
    path.rngState = rngState;
    path.previousTransparentMaterial = 0;
    path.pathHash = 0u;
}

int PathBounceIndexDeterministic(CaelixPathState path)
{
    return (path.bounceIndex & BOUNCEIDX_DETERM_MASK) >> BOUNCEIDX_DETERM_SHIFT;
}

int PathBounceIndexStochastic(CaelixPathState path)
{
    return (path.bounceIndex & BOUNCEIDX_STOCHASTIC_MASK);
}

bool PathBounceIndexIsSpecular(CaelixPathState path)
{
    return (path.bounceIndex & BOUNCEIDX_IS_SPECULAR) > 0;
}

bool PathIsPSR(CaelixPathState path)
{
    return (path.bounceIndex & BOUNCEIDX_IS_PSR) > 0;
}

bool AdvanceDeltaRay(RayPayload payload, inout RayDesc ray, inout CaelixPathState path)
{
    float currentRayT = payload.T;

    // Accumulate transparency extinction along the path
    bool hasPreviousTransparentMaterial = path.previousTransparentMaterial != 0;
    VoxelMaterial previousTransparentMaterial = GET_MATERIAL(path.previousTransparentMaterial);
    float3 ext = hasPreviousTransparentMaterial
        ? exp(-(1.0f - previousTransparentMaterial.albedo.rgb) * currentRayT * previousTransparentMaterial.extinction)
        : float3(1.0f, 1.0f, 1.0f);

    path.throughput *= ext;
    path.T += currentRayT;
    path.bounceIndex += (1 << BOUNCEIDX_DETERM_SHIFT);                                      // Advance deterministic bounce count

    // Terminate on miss
    if(!CaelixRayPayloadHasHit(payload)) { return false; }

    // Get material info
    int materialID = int((payload.materialID_voxelFaceHash >> 16) & 0xFFFFu);
    VoxelMaterial material = GET_MATERIAL(materialID);

    // Terminate on rough surfaces / opaque non-metals
    if(material.smoothness < 0.99f || (IsOpaque(materialID) && material.metallic < 0.5f)) { return false; }

    // Compute world hit info
    float3 worldHitPosition = ray.Origin + ray.Direction * currentRayT;
    float3 worldNormal = CaelixUnpackWorldNormal(payload.packedWorldNormal);

    // Advance delta
    if(IsOpaque(materialID))
    {
        // Opaque: reflection only
        // TODO: Not implemented yet
        return false;
    }
    else
    {
        // Transparency
        int sourceMaterialID = path.previousTransparentMaterial;
        int destinationMaterialID = GetTransparentMaterialId(materialID);
        bool hasDestinationTransparentMaterial = destinationMaterialID != 0;

        float3 interfaceNormal = SafeNormalize(worldNormal);
        if (dot(ray.Direction, interfaceNormal) > 0.0f)
        {
            interfaceNormal = -interfaceNormal;
        }

        float sourceIOR = hasPreviousTransparentMaterial ? previousTransparentMaterial.IOR : 1.0f;
        float destinationIOR = hasDestinationTransparentMaterial ? material.IOR : 1.0f;
        float eta = sourceIOR / destinationIOR;

        float3 reflectionRayDir = reflect(ray.Direction, interfaceNormal);
        float3 refractionRayDir = refract(ray.Direction, interfaceNormal, eta);
        bool canRefract = dot(refractionRayDir, refractionRayDir) > 0.000001f;

        float fresnelFactor = canRefract
            ? FresnelReflectAmountTransparent(sourceIOR, destinationIOR, ray.Direction, interfaceNormal)
            : 1.0f;

        // fresnelFactor = clamp(2.0f * (fresnelFactor - 0.1f) + 0.5f, 0.05f, 0.95f);

        float doRefraction = (canRefract && RandomFloat01(path.rngState) >= fresnelFactor) ? 1.0f : 0.0f;
        if(path.bounceIndex == (1 << BOUNCEIDX_DETERM_SHIFT))
        {
            // First interface: split the two delta branches spatially instead of sampling them,
            // so both are present every frame and the cross resolve averages them back together.
            // Total internal reflection has no refracted branch, so it stays unsplit.
            doRefraction = (canRefract && ((CAELIX_LAUNCH_INDEX.x + CAELIX_LAUNCH_INDEX.y) % 2) == 0) ? 1.0f : 0.0f;

            // Sampling by RandomFloat01 >= fresnelFactor let *probability* carry the Fresnel
            // weight, with k = 1. A fixed 50/50 checkerboard destroys that, so the surviving
            // branch has to carry the weight explicitly, or glass reads as a 50% mirror at
            // every angle. The x2 cancels the 0.5 the cross resolve puts on each parity.
            path.throughput *= canRefract
                ? 2.0f * (doRefraction == 1.0f ? (1.0f - fresnelFactor) : fresnelFactor)
                : 1.0f;

            path.bounceIndex |= ((doRefraction ? 0 : 1) * BOUNCEIDX_IS_SPECULAR);
            path.bounceIndex |= (canRefract ? BOUNCEIDX_IS_PSR : 0);
        }

        // CaelixHashAvalanche is the same lowbias32 hash as CaelixHashUint (CaelixDenoiseCommon.hlsl),
        // which is not part of this compile unit.
        path.pathHash = CaelixHashAvalanche(path.pathHash
            ^ (uint(materialID) & 0xFFFFu)
            ^ ((payload.materialID_voxelFaceHash & 63u) << 16)
            ^ (doRefraction == 1.0f ? 0x800000u : 0u));
        // doRefraction = 0.0f;
        float3 bounceRayDir = SafeNormalize(lerp(reflectionRayDir, refractionRayDir, doRefraction));
        float pushOff = doRefraction == 1.0f ? -K_RAY_ORIGIN_PUSH_OFF : K_RAY_ORIGIN_PUSH_OFF;

        path.previousTransparentMaterial = doRefraction == 1.0f ? destinationMaterialID : sourceMaterialID;

        ray.Origin = worldHitPosition + pushOff * interfaceNormal;
        ray.Direction = bounceRayDir;
    }

    return true;
}

/////////////////////////////////////////////////////////////////////////////////////////////////
// STOCHASTIC SHADING
/////////////////////////////////////////////////////////////////////////////////////////////////

struct CaelixShadeResult
{
    half3 albedo;
    half3 emission;
    float3 bounceRayOrigin;
    float3 bounceRayDirection;
    half3 worldNormal;
    // Which lobe the bounce ray sampled: 1 = specular (incl. transparent reflection/refraction,
    // both delta), 0 = diffuse. Consumed at the primary hit to route the stochastic signal into
    // DiffuseRadianceTarget vs SpecularRadianceTarget; ignored on later bounces.
    half specularLobe;
    float k;
};

// Attenuation of one straight segment of length segmentT travelling inside mediumMaterialID
// (0 = vacuum). AdvanceDeltaRay folds this into path.throughput for delta segments; the bounce
// loop folds it into its own throughput. Shading never applies it, so it is counted exactly once.
float3 CaelixMediumExtinction(int mediumMaterialID, float segmentT)
{
    if (mediumMaterialID == 0) { return float3(1.0f, 1.0f, 1.0f); }

    VoxelMaterial medium = GET_MATERIAL(mediumMaterialID);
    return exp(-(1.0f - medium.albedo.rgb) * segmentT * medium.extinction);
}

// Shades one hit and samples the next bounce ray. Unlike AdvanceDeltaRay this accepts any
// material: it is what runs once the delta chain has terminated.
void CaelixShadeVoxelHit(
    RayPayload payload,
    float3 worldHitPosition,
    float3 worldRayDirection,
    inout CaelixPathState path,
    out CaelixShadeResult shade)
{
    int materialID = int((payload.materialID_voxelFaceHash >> 16) & 0xFFFFu);
    VoxelMaterial material = GET_MATERIAL(materialID);

    float3 worldNormal = CaelixUnpackWorldNormal(payload.packedWorldNormal);

    path.bounceIndex += 1;                                                                      // Advance stochastic bounce count

    if (IsOpaque(materialID))
    {
        shade.albedo = material.albedo;
        shade.emission = material.emission;

        float fresnelFactor = FresnelReflectAmountOpaque(1.0f, material.IOR, worldRayDirection, worldNormal);
        float specularChance = lerp(material.metallic, 1.0f, fresnelFactor * material.smoothness);
        float doSpecular = (RandomFloat01(path.rngState) < specularChance) ? 1.0f : 0.0f;

        float3 diffuseRayDir = SafeNormalize(worldNormal + RandomUnitVector(path.rngState));
        diffuseRayDir = (dot(diffuseRayDir, diffuseRayDir) > 0.0f) ? diffuseRayDir : worldNormal;
        float3 specularRayDir = reflect(worldRayDirection, worldNormal);
        specularRayDir = SafeNormalize(lerp(diffuseRayDir, specularRayDir, material.smoothness));
        float3 reflectedRayDir = lerp(diffuseRayDir, specularRayDir, doSpecular);

        // shade.k = (doSpecular == 1.0f) ? specularChance : 1.0f - specularChance;
        shade.k = 1;
        shade.bounceRayOrigin = worldHitPosition + K_RAY_ORIGIN_PUSH_OFF * worldNormal;
        shade.bounceRayDirection = reflectedRayDir;
        shade.worldNormal = worldNormal;
        shade.specularLobe = half(doSpecular);
    }
    else
    {
        int sourceMaterialID = path.previousTransparentMaterial;
        int destinationMaterialID = GetTransparentMaterialId(materialID);
        bool hasPreviousTransparentMaterial = sourceMaterialID != 0;
        bool hasDestinationTransparentMaterial = destinationMaterialID != 0;
        VoxelMaterial previousTransparentMaterial = GET_MATERIAL(sourceMaterialID);

        float3 interfaceNormal = SafeNormalize(worldNormal);
        if (dot(worldRayDirection, interfaceNormal) > 0.0f)
        {
            interfaceNormal = -interfaceNormal;
        }

        float sourceIOR = hasPreviousTransparentMaterial ? previousTransparentMaterial.IOR : 1.0f;
        float destinationIOR = hasDestinationTransparentMaterial ? material.IOR : 1.0f;
        float eta = sourceIOR / destinationIOR;

        float3 reflectionRayDir = reflect(worldRayDirection, interfaceNormal);
        float3 refractionRayDir = refract(worldRayDirection, interfaceNormal, eta);
        bool canRefract = dot(refractionRayDir, refractionRayDir) > 0.000001f;

        float fresnelFactor = canRefract
            ? FresnelReflectAmountTransparent(sourceIOR, destinationIOR, worldRayDirection, interfaceNormal)
            : 1.0f;

        float doRefraction = (canRefract && RandomFloat01(path.rngState) >= fresnelFactor) ? 1.0f : 0.0f;
        // doRefraction = 0.0f;
        float3 bounceRayDir = SafeNormalize(lerp(reflectionRayDir, refractionRayDir, doRefraction));
        float pushOff = doRefraction == 1.0f ? -K_RAY_ORIGIN_PUSH_OFF : K_RAY_ORIGIN_PUSH_OFF;

        // shade.k = doRefraction == 1.0f ? 1.0f - fresnelFactor : fresnelFactor;
        shade.k = 1;
        shade.albedo = half3(1.0f, 1.0f, 1.0f);                                                 // extinction is a per-segment term
        shade.emission = material.emission;
        shade.bounceRayOrigin = worldHitPosition + pushOff * interfaceNormal;
        shade.bounceRayDirection = bounceRayDir;
        shade.worldNormal = interfaceNormal;
        // Both transparent lobes are delta (pure reflect/refract): treat them as specular with
        // roughness 0. Refraction does NOT fit the diffuse-denoiser lobe model; the proper
        // long-term handling is primary surface replacement, i.e. the delta chain above.
        shade.specularLobe = half(1.0f);
        path.previousTransparentMaterial = doRefraction == 1.0f ? destinationMaterialID : sourceMaterialID;
    }
}

float3 CaelixSampleSkyRadiance(float3 direction)
{
    return g_Sky.SampleLevel(sampler_g_Sky, direction, 0).xyz * 2.0f;
}

// Last line of defence before a radiance write. The denoise chain has no NaN/Inf guard of its
// own and cannot have a cheap one: a NaN tap poisons an a-trous sum whatever its weight, the
// temporal lerp keeps it for as long as the pixel reprojects, and the colour resolve's bilinear
// history taps grow it by a few pixels per frame. One bad pixel from here therefore turns into a
// ~125 px blob that never clears, so a bad sample is dropped to black instead. Any channel bad
// drops the whole colour: a partially-NaN float3 is not a usable sample either.
float3 CaelixSanitizeRadiance(float3 radiance)
{
    return any(isnan(radiance) | isinf(radiance)) ? float3(0.0f, 0.0f, 0.0f) : radiance;
}

float3 ComputeRayDirection(float2 ndcCoords)
{
    float3 viewDirection = normalize(float3(ndcCoords.x * g_AspectRatio, ndcCoords.y, -1));     // view space ray
    float3 rayDirection = mul((float3x3)g_CurrentCameraToWorld, viewDirection);                 // view -> world
    return rayDirection;
}

// Main
void CaelixPathTraceMain()
{
    // Ray indices
    uint2 rayDispatchIndex = CAELIX_LAUNCH_INDEX;
    uint2 rayLaunchDim = CAELIX_LAUNCH_DIM;
    uint2 rayLaunchIndex = uint2(rayDispatchIndex.x, rayLaunchDim.y - rayDispatchIndex.y - 1);

    // Initialize RNG. Every pixel starts its draw index at 0 every frame, because SampleBlueNoise
    // shifts the STBN tile by the DRAW index and slices it by the frame: a per-frame seed here would
    // put a pixel on a different sequence position each frame and destroy the temporal property.
    uint rngState = 0u;

    // Pixel-centre mapping: the sample sits at the centre of pixel `rayLaunchIndex`, displaced by
    // this frame's global jitter, and the pixel grid spans the full [0, 1] range -- hence `/ dim`
    // and not `/ (dim - 1)`, which would place the samples on the grid CORNERS and leave the last
    // row and column unreprojectable.
    float2 ndcCoords = (float2(rayLaunchIndex) + 0.5f + g_Jitter) / float2(rayLaunchDim);
    ndcCoords = ndcCoords * 2 - float2(1, 1);
    ndcCoords = ndcCoords * g_Zoom;

    // Raygen
    RayDesc ray;
    ray.Origin = g_CameraWorldPosition;
    ray.Direction = ComputeRayDirection(ndcCoords);
    ray.TMin = 0;
    ray.TMax = K_T_MAX;

    RayPayload payload;
    CaelixPathState path;

    CaelixInitPathState(path, rngState);

    //////////////////////////////////////////////
    // Primary ray
    //////////////////////////////////////////////

    // Real geometry of the surface the delta chain terminates on, and the direction it was hit
    // from. The PSR "virtual" position below is only valid for depth / motion vectors — bounce
    // rays have to leave the actual surface. Captured per iteration because the loop can also
    // exit on the deterministic bounce budget, after AdvanceDeltaRay has already moved `ray` on.
    float3 surfaceWorldPosition = float3(0, 0, 0);
    float3 surfaceRayDirection = ray.Direction;

    // Primary delta
    for(; PathBounceIndexDeterministic(path) < g_BounceCountTransparent;)
    {
        CaelixClearRayPayload(payload);
        ray.TMin = 0;
        ray.TMax = K_T_MAX;
        CAELIX_TRACE_RAY(ray, payload);

        // Handle miss
        if(!CaelixRayPayloadHasHit(payload))
        {
            path.T = K_T_MAX;
            break;
        }

        surfaceWorldPosition = ray.Origin + ray.Direction * payload.T;
        surfaceRayDirection = ray.Direction;

        // Write non-PSR depth
        if(path.bounceIndex == 0)
        {
            // Recompute indices to reduce register pressure as much as possible
            uint2 outputDispatchIndex = CAELIX_LAUNCH_INDEX;
            uint2 outputLaunchIndex = uint2(outputDispatchIndex.x, CAELIX_LAUNCH_DIM.y - outputDispatchIndex.y - 1);

            float3 viewForward = -normalize(g_CurrentCameraToWorld._m02_m12_m22);               // TODO: Compute depth more elegantly?
            float distanceFromCamera = dot(
                (ray.Origin + ray.Direction * payload.T) - g_CameraWorldPosition, viewForward);
            DepthTarget[outputLaunchIndex] = distanceFromCamera;                                // Should be correct?
            // g_CurrentDepthHistory[outputLaunchIndex] = payload.T;
        }

        if(!AdvanceDeltaRay(payload, ray, path))
        {
            break;
        }
    }

    //////////////////////////////////////////////
    // GBuffer write (primary surface)
    //////////////////////////////////////////////

    uint2 outputDispatchIndex = CAELIX_LAUNCH_INDEX;
    uint2 outputLaunchIndex = uint2(outputDispatchIndex.x, CAELIX_LAUNCH_DIM.y - outputDispatchIndex.y - 1);

    // Get material info
    if(CaelixRayPayloadHasHit(payload))
    {
        int materialID = int((payload.materialID_voxelFaceHash >> 16) & 0xFFFFu);
        VoxelMaterial material = GET_MATERIAL(materialID);

        float3 camRayDir = ComputeRayDirection(ndcCoords);                                      // Recompute for reg pressure
        float3 primaryWorldHitPosition_PSR = g_CameraWorldPosition + camRayDir * path.T;

        // Depth
        float3 viewForward = -normalize(g_CurrentCameraToWorld._m02_m12_m22);
        float distanceFromCamera = dot(primaryWorldHitPosition_PSR - g_CameraWorldPosition, viewForward);

        /// GBUFFER SET ////////////////////////////////////////////////////////////
        g_CurrentDepthHistory[outputLaunchIndex] = distanceFromCamera;
        ////////////////////////////////////////////////////////////////////////////

        // MotionVector
        float2 motionVector = float2(0, 0);
        float3 prevWorldHitPosition = primaryWorldHitPosition_PSR                               // TODO: Handle PSR (Set to 0)
            + CaelixUnpackFloat3FromHalf4(payload.packedPrevWorldOffset);
        float3 prevViewPosition = mul(g_PrevWorldToCamera, float4(prevWorldHitPosition, 1.0f)).xyz;
        float previousSurfaceViewDepth = -prevViewPosition.z;

        if (previousSurfaceViewDepth > 0.00001f)
        {
            uint2 outputLaunchDim = CAELIX_LAUNCH_DIM;
            // The same launch-space position the primary ray was built from, jitter included. The
            // subtraction therefore cancels the jitter out of the stored vector: on a static scene
            // a pixel reprojects exactly onto its own texel, which is what a TAA-style history
            // fetch expects. Consumers that need the jittered position add g_Jitter back.
            float2 outputFrameCoord = float2(outputLaunchIndex) + 0.5f + g_Jitter;

            float2 prevNdc = prevViewPosition.xy / previousSurfaceViewDepth;
            prevNdc /= float2(g_AspectRatio * g_Zoom, g_Zoom);
            // previousUV - currentUV: adding this to a pixel's UV lands on where that surface
            // was last frame. Same "previous minus current" direction as .z below.
            motionVector = (prevNdc * 0.5f + 0.5f) - outputFrameCoord / float2(outputLaunchDim);
        }

        // 2.5D screen-space motion, per the NRD doc: .xy = previousUV - currentUV,
        // .z = viewZprev - viewZ. Both components point "previous minus current", so a
        // consumer reprojects with previousUV = currentUV + mv.xy.
        // .a = the delta-checkerboard flag (1 = this pixel traced one parity of a reflect/refract
        // split and needs the final cross resolve, 0 = single unsplit surface or sky).
        /// GBUFFER SET ////////////////////////////////////////////////////////////
        MotionVectorTarget[outputLaunchIndex] = float4(
            motionVector, previousSurfaceViewDepth - distanceFromCamera, PathIsPSR(path) ? 1.0f : 0.0f);
        ////////////////////////////////////////////////////////////////////////////

        // Normal
        float2 packedNormal = saturate(PackNormalOctQuadEncode(
            CaelixUnpackWorldNormal(payload.packedWorldNormal)) * 0.5f + 0.5f);
        // .b = voxel face hash (spatial filters' edge guide), .a = linear roughness (denoiser guide).
        /// GBUFFER SET ////////////////////////////////////////////////////////////
        NormalTarget[outputLaunchIndex] = float4(
            packedNormal, float(payload.materialID_voxelFaceHash & 0xFFFFu), half(1.0f - material.smoothness));
        ////////////////////////////////////////////////////////////////////////////

        // .b carries the view depth this surface had last frame so temporal reprojection
        // can validate history against an exact expectation even while the camera moves.
        /// GBUFFER SET ////////////////////////////////////////////////////////////
        g_CurrentNormalHistory[outputLaunchIndex] = float4(packedNormal, previousSurfaceViewDepth, 1.0f);
        ////////////////////////////////////////////////////////////////////////////

        // Composite does `deterministic + albedo * indirect`, so path.throughput (the tint and
        // attenuation of everything the delta chain refracted through) rides on the albedo and
        // modulates the whole stochastic signal. A transparent terminator carries no albedo of
        // its own — its colour is already in the extinction term.
        /// GBUFFER SET ////////////////////////////////////////////////////////////
        AlbedoTarget[outputLaunchIndex] = float4(                                           // TODO: We still need this?
            path.throughput * (IsOpaque(materialID) ? material.albedo : float3(1, 1, 1)), 1);
        ////////////////////////////////////////////////////////////////////////////
    }
    // Miss (Direct miss or delta miss)
    else
    {
        // Delta (reflected / refracted) miss
        if((path.bounceIndex & BOUNCEIDX_DETERM_MASK) > 0)
        {
            float3 outColor = CaelixSampleSkyRadiance(ray.Direction);
            DeterministicRadianceTarget[outputLaunchIndex] = float4(CaelixSanitizeRadiance(path.throughput * outColor), 1.0f);
            g_CurrentDepthHistory[outputLaunchIndex] = K_T_MAX;
        }
        // Direct miss
        else
        {
            DeterministicRadianceTarget[outputLaunchIndex] = float4(0.0f, 0.0f, 0.0f, 0.0f);
            DepthTarget[outputLaunchIndex] = K_T_MAX;
            g_CurrentDepthHistory[outputLaunchIndex] = K_T_MAX;
        }
        // Set remaining GBuffers for miss
        float2 packedNormal = saturate(PackNormalOctQuadEncode(float3(0, 0, 1)) * 0.5f + 0.5f);
        NormalTarget[outputLaunchIndex] = float4(packedNormal, 0.0f, 0);
        g_CurrentNormalHistory[outputLaunchIndex] = float4(packedNormal, 0, 0.0f);
        AlbedoTarget[outputLaunchIndex] = float4(0, 0, 0, 1);
        // A delta miss still belongs to a checkerboard pair — its parity flew off to the sky while
        // the neighbouring parity hit something — so it has to be resolved like any other PSR pixel.
        MotionVectorTarget[outputLaunchIndex] = float4(0, 0, 0, PathIsPSR(path) ? 1.0f : 0.0f);
    }

    //////////////////////////////////////////////
    // Further Bounces
    //////////////////////////////////////////////

#if CAELIXRT_BOUNCE
    if(CaelixRayPayloadHasHit(payload))
    {
        // Shade the surface the delta chain landed on — the one the G-buffer above describes.
        // Its lobe choice classifies the entire stochastic path.
        CaelixShadeResult primaryShade;
        CaelixShadeVoxelHit(payload, surfaceWorldPosition, surfaceRayDirection, path, primaryShade);

        // Primary emission is deterministic (nothing was sampled to obtain it), so it bypasses
        // the denoiser. path.throughput carries the delta chain's transmission.
        DeterministicRadianceTarget[outputLaunchIndex] = float4(CaelixSanitizeRadiance(path.throughput * primaryShade.emission), 1.0f);

#if defined(CAELIX_GI_MODE)
        // The diffuse lobe of an opaque primary belongs to the GI resampler: no bounce is traced
        // for it here, and the resampler fills DiffuseRadianceTarget later in the frame.
        int giMaterialID = int((payload.materialID_voxelFaceHash >> 16) & 0xFFFFu);
        bool giOpaque = IsOpaque(giMaterialID);
        bool giDiffuseLobe = giOpaque && primaryShade.specularLobe < half(0.5f) && !PathBounceIndexIsSpecular(path);
        uint giHash16 = path.pathHash == 0u
            ? 0u
            : max(1u, (path.pathHash ^ (path.pathHash >> 16)) & 0xFFFFu);
        uint giFlags = 1u | (giDiffuseLobe ? 2u : 0u) | (giOpaque ? 4u : 0u);
        GiSurfaceTarget[outputLaunchIndex] = float4(surfaceWorldPosition, path.T);
        GiKeyTarget[outputLaunchIndex] = uint4(
            giHash16 | (payload.instanceId << 16),
            g_Instances[payload.instanceId].lifetimeToken,
            giFlags,
            uint(giMaterialID));
#endif

        // Keep the primary albedo out of the temporal history. It is applied once during final
        // composition so replaced voxels can reuse lighting history.
        half3 indirectIncidentRadiance = half3(0, 0, 0);
        float3 throughput = 1.0f / max(0.001, primaryShade.k);
        // Hit distance of the first stochastic segment (primary surface -> first bounce hit),
        // in raw world units per NRD conventions; K_T_MAX when the first bounce hits the sky.
        float firstSegmentHitT = K_T_MAX;
        bool firstSegment = true;

        RayDesc bounceRay;
        bounceRay.Origin = primaryShade.bounceRayOrigin;
        bounceRay.Direction = primaryShade.bounceRayDirection;
        bounceRay.TMin = 0;
        bounceRay.TMax = K_T_MAX;

        // NOTE: the stochastic counter lives in the low nibble of path.bounceIndex, so
        // g_BounceCountOpaque must stay below 15 — past that the counter wraps and this loop
        // never terminates.
#if defined(CAELIX_GI_MODE)
        if (!giDiffuseLobe)
#endif
        for(; (uint)PathBounceIndexStochastic(path) <= g_BounceCountOpaque;)
        {
            RayPayload bouncePayload;
            CaelixClearRayPayload(bouncePayload);
            CAELIX_TRACE_RAY(bounceRay, bouncePayload);

            // Attenuate the segment by the medium it just crossed, before shading its hit.
            // On a miss payload.T is 0, so the sky term is unattenuated (same as the old shader).
            throughput *= CaelixMediumExtinction(path.previousTransparentMaterial, bouncePayload.T);

            if (!CaelixRayPayloadHasHit(bouncePayload))
            {
                indirectIncidentRadiance += CaelixSampleSkyRadiance(bounceRay.Direction) * throughput;
                break;
            }

            if (firstSegment)
            {
                firstSegmentHitT = bouncePayload.T;
                firstSegment = false;
            }

            CaelixShadeResult bounceShade;
            CaelixShadeVoxelHit(
                bouncePayload,
                bounceRay.Origin + bounceRay.Direction * bouncePayload.T,
                bounceRay.Direction,
                path,
                bounceShade);

            indirectIncidentRadiance += bounceShade.emission * throughput;
            throughput *= bounceShade.albedo / max(0.001, bounceShade.k);

            half pathStopProbability = 1;

    #define ENABLE_RUSSIAN_ROULETTE 1

    #if ENABLE_RUSSIAN_ROULETTE
            pathStopProbability = max(throughput.r, max(throughput.g, throughput.b));

            // Dark colors have higher chance to terminate the path early. A zero throughput
            // (black albedo, or a medium that absorbed everything) MUST stop here: the random
            // value is an 8-bit blue-noise sample that is exactly 0 in 1 of 256 draws, and
            // `0 < 0` would let the path survive into `0 * (1 / 0)` = NaN below.
            if (pathStopProbability <= 0.0f || pathStopProbability < RandomFloat01(path.rngState))
                break;
    #endif

            throughput *= 1 / pathStopProbability;

            bounceRay.Origin = bounceShade.bounceRayOrigin;
            bounceRay.Direction = bounceShade.bounceRayDirection;
            bounceRay.TMin = 0;
            bounceRay.TMax = K_T_MAX;
        }

        // The path is classified once, by the lobe sampled at the primary hit, and the whole
        // gathered signal goes to that lobe's target — so diffuse + specular always sums to
        // exactly what the old combined buffer held. The unsampled lobe gets 0 radiance and
        // 0 hit distance (no data this frame).
        // bool primaryLobeIsSpecular = primaryShade.specularLobe > half(0.5f);
        float4 stochasticOut = float4(CaelixSanitizeRadiance(indirectIncidentRadiance), firstSegmentHitT);
#if defined(CAELIX_GI_MODE)
        // GI mode: the diffuse target belongs to the resampler alone, so every traced path
        // (including an opaque specular-lobe sample) goes to the specular target.
        DiffuseRadianceTarget[outputLaunchIndex] = giDiffuseLobe ? float4(0, 0, 0, K_T_MAX) : float4(0, 0, 0, 0);
        SpecularRadianceTarget[outputLaunchIndex] = giDiffuseLobe ? float4(0, 0, 0, 0) : stochasticOut;
#else
        bool primaryLobeIsSpecular = PathBounceIndexIsSpecular(path);
        DiffuseRadianceTarget[outputLaunchIndex] = primaryLobeIsSpecular ? float4(0, 0, 0, 0) : stochasticOut;
        SpecularRadianceTarget[outputLaunchIndex] = primaryLobeIsSpecular ? stochasticOut : float4(0, 0, 0, 0);
#endif
    }
    else
    {
#if defined(CAELIX_GI_MODE)
        GiSurfaceTarget[outputLaunchIndex] = float4(0.0f, 0.0f, 0.0f, K_T_MAX);
        GiKeyTarget[outputLaunchIndex] = uint4(0u, 0u, 0u, 0u);
#endif
        // Sky pixels carry no stochastic signal. These are pooled render-graph textures created
        // without a clear, so leaving them unwritten feeds a stale frame into the denoiser.
        DiffuseRadianceTarget[outputLaunchIndex] = float4(0, 0, 0, 0);
        SpecularRadianceTarget[outputLaunchIndex] = float4(0, 0, 0, 0);
    }
#else
    // Flat debug signal: the denoise chain becomes an identity, so the composite shows albedo only.
    if(CaelixRayPayloadHasHit(payload))
    {
        DeterministicRadianceTarget[outputLaunchIndex] = float4(0.0f, 0.0f, 0.0f, 1.0f);
    }
    DiffuseRadianceTarget[outputLaunchIndex] = float4(1.0f, 1.0f, 1.0f, K_T_MAX);
    SpecularRadianceTarget[outputLaunchIndex] = float4(0.0f, 0.0f, 0.0f, 0.0f);
#endif
}

#endif
