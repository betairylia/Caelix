#ifndef CAELIX_GI_SKIN_INCLUDED
#define CAELIX_GI_SKIN_INCLUDED

// Brick skin irradiance caching.
//
// A diffuse surface point estimates its incident light with short random walks that stay inside
// the brick record of the voxel it belongs to. The walk uses the brick DDA on the record that is
// already in hand; interior hits add emission and scatter diffusely. When a walk leaves the brick,
// the irradiance stored on the brick's skin at the exit texel (6 faces x 8 x 8 voxel-aligned
// texels) supplies the radiance entering the brick there, as E / pi. Nothing inside a brick is
// cached, so the only bias lives on the skin: it is a six-direction irradiance representation at
// voxel resolution, trained by cosine rays shot outward from each touched texel.
//
// Includes: GiCommon.hlsl and GiEmission.hlsl must come first. The group descriptor table that the
// emission prototype builds (sorted by lifetime token) supplies each group's page, brick base and
// object-to-world transform; the skin uses it to find brick records and to move between object
// and world space without a neighbor table.

#define GI_SKIN_INVALID 0xffffffffu
#define GI_SKIN_PROBES 16u
#define GI_SKIN_FACE_TEXELS 64u
#define GI_SKIN_TEXELS 384u
#define GI_SKIN_MARK_WORDS 12u
#define GI_SKIN_CONTROL_TOUCHED 0u
#define GI_SKIN_CONTROL_TRAINING 1u
#define GI_SKIN_CONTROL_ARGS 4u
#define GI_SKIN_IRRADIANCE_LIMIT 60000.0f

struct GiSkinBrick
{
    uint state;        // 0 free, 1 being claimed, 2 live
    uint2 key;         // instance lifetime token, group-local brick index
    uint touchedFrame; // last frame a walk read or marked one of its texels
};

globallycoherent RWStructuredBuffer<GiSkinBrick> g_GiSkinBricks;
RWStructuredBuffer<uint2> g_GiSkinTexels;   // capacity * 384: half3 irradiance, uint16 sample count
RWStructuredBuffer<uint> g_GiSkinMarks;     // capacity * 12: one bit per texel requested this frame
RWStructuredBuffer<uint> g_GiSkinTouched;   // training budget entries: slot * 384 + texel
RWStructuredBuffer<uint> g_GiSkinControl;   // [0] touched count, [1] training count, [4..6] dispatch args
uint g_GiSkinCapacity;
uint g_GiSkinTrainingBudget;
uint g_GiSkinWalks;
uint g_GiSkinWalkBounces;
uint g_GiSkinTrainingRays;
uint g_GiSkinEmitterSampling;
uint g_GiSkinFrame;
uint g_GiSkinMaxAge;
uint g_GiSkinHistoryLimit;
uint g_GiSkinMinSamples;

// ---------------------------------------------------------------------------------------------
// Texel storage

uint2 GiSkinPack(float3 irradiance, uint count)
{
    irradiance = clamp(irradiance, 0.0f, GI_SKIN_IRRADIANCE_LIMIT);
    return uint2(f32tof16(irradiance.x) | (f32tof16(irradiance.y) << 16u),
        f32tof16(irradiance.z) | (min(count, 65535u) << 16u));
}

void GiSkinUnpack(uint2 packed, out float3 irradiance, out uint count)
{
    irradiance = float3(f16tof32(packed.x & 0xffffu), f16tof32(packed.x >> 16u), f16tof32(packed.y & 0xffffu));
    count = packed.y >> 16u;
}

bool GiSkinRead(uint slot, uint texel, out float3 irradiance)
{
    uint count;
    GiSkinUnpack(g_GiSkinTexels[slot * GI_SKIN_TEXELS + texel], irradiance, count);
    return count >= max(1u, g_GiSkinMinSamples);
}

// ---------------------------------------------------------------------------------------------
// Brick table: the same claim protocol as the face cache, keyed by brick instead of face.

uint GiSkinHash(uint2 key)
{
    uint h = key.x * 0x9e3779b9u + key.y * 0x85ebca6bu;
    h = (h ^ (h >> 16u)) * 0x7feb352du;
    h = (h ^ (h >> 15u)) * 0x846ca68bu;
    return h ^ (h >> 16u);
}

uint GiSkinFind(uint2 key, bool allocate)
{
    if (g_GiSkinCapacity == 0u || key.x == 0u) return GI_SKIN_INVALID;
    uint start = GiSkinHash(key) % g_GiSkinCapacity;
    uint emptySlot = GI_SKIN_INVALID;
    [loop] for (uint probe = 0u; probe < min(GI_SKIN_PROBES, g_GiSkinCapacity); ++probe)
    {
        uint slot = (start + probe) % g_GiSkinCapacity;
        uint state;
        InterlockedAdd(g_GiSkinBricks[slot].state, 0u, state);
        // Never spin on another lane's claim: SIMT lanes may share a wave.
        if (state == 1u) return GI_SKIN_INVALID;
        if (state == 2u && all(g_GiSkinBricks[slot].key == key)) return slot;
        if (state == 0u && emptySlot == GI_SKIN_INVALID) emptySlot = slot;
    }
    if (!allocate || emptySlot == GI_SKIN_INVALID) return GI_SKIN_INVALID;
    uint original;
    InterlockedCompareExchange(g_GiSkinBricks[emptySlot].state, 0u, 1u, original);
    if (original != 0u) return GI_SKIN_INVALID;
    g_GiSkinBricks[emptySlot].key = key;
    g_GiSkinBricks[emptySlot].touchedFrame = g_GiSkinFrame;
    DeviceMemoryBarrier();
    InterlockedExchange(g_GiSkinBricks[emptySlot].state, 2u, original);
    return emptySlot;
}

void GiSkinTouch(uint slot)
{
    if (slot == GI_SKIN_INVALID) return;
    InterlockedMax(g_GiSkinBricks[slot].touchedFrame, g_GiSkinFrame);
}

// Requests training for one texel. The first request of a frame appends it to the training list;
// an append that does not fit the budget releases the mark so a later frame can request it again.
void GiSkinMark(uint slot, uint texel)
{
    if (slot == GI_SKIN_INVALID) return;
    uint word = slot * GI_SKIN_MARK_WORDS + (texel >> 5u);
    uint bit = 1u << (texel & 31u);
    uint previous;
    InterlockedOr(g_GiSkinMarks[word], bit, previous);
    if ((previous & bit) != 0u) return;
    uint index;
    InterlockedAdd(g_GiSkinControl[GI_SKIN_CONTROL_TOUCHED], 1u, index);
    if (index < g_GiSkinTrainingBudget) g_GiSkinTouched[index] = slot * GI_SKIN_TEXELS + texel;
    else InterlockedAnd(g_GiSkinMarks[word], ~bit);
}

void GiSkinUnmark(uint slot, uint texel)
{
    InterlockedAnd(g_GiSkinMarks[slot * GI_SKIN_MARK_WORDS + (texel >> 5u)], ~(1u << (texel & 31u)));
}

// ---------------------------------------------------------------------------------------------
// Group transforms. Entity scale is exactly 1, so the inverse of a row-major object-to-world
// 3x4 is its transposed rotation applied after removing the translation.

bool GiSkinFindGroup(uint token, out GiEmissionGroup group)
{
    group = (GiEmissionGroup)0;
    uint lo = 0u;
    uint hi = g_GiEmissionGroupCount;
    while (lo < hi)
    {
        uint mid = (lo + hi) >> 1u;
        if (g_GiEmissionGroups[mid].groupToken < token) lo = mid + 1u;
        else hi = mid;
    }
    if (lo >= g_GiEmissionGroupCount) return false;
    group = g_GiEmissionGroups[lo];
    return group.groupToken == token;
}

float3 GiSkinToWorld(GiEmissionGroup group, float3 objectPosition)
{
    float4 p = float4(objectPosition, 1.0f);
    return float3(dot(group.row0, p), dot(group.row1, p), dot(group.row2, p));
}

float3 GiSkinDirectionToWorld(GiEmissionGroup group, float3 objectDirection)
{
    return float3(dot(group.row0.xyz, objectDirection), dot(group.row1.xyz, objectDirection), dot(group.row2.xyz, objectDirection));
}

float3 GiSkinToObject(GiEmissionGroup group, float3 worldPosition)
{
    float3 q = worldPosition - float3(group.row0.w, group.row1.w, group.row2.w);
    return q.x * group.row0.xyz + q.y * group.row1.xyz + q.z * group.row2.xyz;
}

// ---------------------------------------------------------------------------------------------
// Brick geometry

float3 GiSkinBrickOrigin(uint groupLocalIndex)
{
    return float3(groupLocalIndex & CAELIX_GROUP_MASK_X,
        (groupLocalIndex >> CAELIX_GROUP_SHIFT_X) & CAELIX_GROUP_MASK_Y,
        groupLocalIndex >> (CAELIX_GROUP_SHIFT_X + CAELIX_GROUP_SHIFT_Y)) * float(SIZE_IN_BLOCKS);
}

struct GiSkinBrickContext
{
    GiEmissionGroup group;
    uint slot;        // skin table slot, or GI_SKIN_INVALID when the table is full
    uint recordBase;  // word offset of the brick record inside its page
    uint coarse;      // coarse occupancy bits of the record
    float3 origin;    // object-space position of the brick's minimum corner
};

// Resolves the brick record a surface belongs to. The committed primitive index of the hit (kept
// in GiSurface.reserved.y) is the renderer brick slot inside the group's published range.
bool GiSkinResolveBrick(GiSurface surface, bool allocate, out GiSkinBrickContext context)
{
    context = (GiSkinBrickContext)0;
    context.slot = GI_SKIN_INVALID;
    if (!GiSkinFindGroup(surface.faceKey.x, context.group)) return false;
    uint primitive = surface.reserved.y;
    if (primitive >= context.group.brickCount) return false;
    context.recordBase = context.group.brickBase + primitive * BRICK_DATA_LENGTH;
    _CaelixBrickPage = context.group.page;
    uint info = CAELIX_BRICKS_LOAD(context.recordBase << 2u);
    uint index = info & BRICK_INFO_ABSOLUTE_INDEX_MASK;
    context.coarse = CaelixGetCoarseOccupancy(info);
    context.origin = GiSkinBrickOrigin(index);
    context.slot = GiSkinFind(uint2(surface.faceKey.x, index), allocate);
    return true;
}

// Face numbering follows objectNormals: 0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z.
float2 GiSkinFaceCoordinates(uint face, float3 brickPosition)
{
    uint axis = face >> 1u;
    return axis == 0u ? brickPosition.yz : (axis == 1u ? brickPosition.xz : brickPosition.xy);
}

// Brick-local center of a skin texel, pushed off the face along its outward normal.
float3 GiSkinTexelCenter(uint texel, out float3 outwardNormal)
{
    uint face = texel / GI_SKIN_FACE_TEXELS;
    uint inFace = texel - face * GI_SKIN_FACE_TEXELS;
    float u = float(inFace & 7u) + 0.5f;
    float v = float(inFace >> 3u) + 0.5f;
    outwardNormal = float3(objectNormals[face]);
    float along = (face & 1u) == 0u ? float(SIZE_IN_BLOCKS) : 0.0f;
    uint axis = face >> 1u;
    float3 center = axis == 0u ? float3(along, u, v) : (axis == 1u ? float3(u, along, v) : float3(u, v, along));
    return center + outwardNormal * K_RAY_ORIGIN_PUSH_OFF;
}

// Where a brick-local ray leaves the brick. A start position already past a face along its
// direction exits at once (t = 0). Returns the texel index and the exit face.
uint GiSkinExit(float3 position, float3 direction, out float3 exitPosition, out uint exitFace, out float exitT)
{
    float3 limit = float3(direction.x > 0.0f ? float(SIZE_IN_BLOCKS) : 0.0f,
        direction.y > 0.0f ? float(SIZE_IN_BLOCKS) : 0.0f,
        direction.z > 0.0f ? float(SIZE_IN_BLOCKS) : 0.0f);
    float3 t;
    t.x = abs(direction.x) < 1e-7f ? 1e30f : max(0.0f, (limit.x - position.x) / direction.x);
    t.y = abs(direction.y) < 1e-7f ? 1e30f : max(0.0f, (limit.y - position.y) / direction.y);
    t.z = abs(direction.z) < 1e-7f ? 1e30f : max(0.0f, (limit.z - position.z) / direction.z);
    exitT = min(t.x, min(t.y, t.z));
    uint axis = exitT == t.x ? 0u : (exitT == t.y ? 1u : 2u);
    exitPosition = position + direction * exitT;
    exitFace = axis * 2u + (direction[axis] > 0.0f ? 0u : 1u);
    int2 cell = clamp(int2(floor(GiSkinFaceCoordinates(exitFace, exitPosition))), int2(0, 0), int2(7, 7));
    return exitFace * GI_SKIN_FACE_TEXELS + uint(cell.y) * 8u + uint(cell.x);
}

// ---------------------------------------------------------------------------------------------
// The estimator

// Average radiance arriving at a diffuse interior point along cosine-distributed directions
// (irradiance over pi). Each walk bounces diffusely inside the brick until it leaves, then reads
// the skin texel it crossed. A cold texel costs one real path from the exit point instead.
float3 GiSkinWalks(GiSkinBrickContext context, float3 localPosition, uint face, inout uint rng, bool allowMark)
{
    float3 total = 0.0f;
    uint walks = max(1u, g_GiSkinWalks);
    float3 startNormal = float3(objectNormals[face]);
    float3 start = localPosition + startNormal * K_RAY_ORIGIN_PUSH_OFF;
    [loop] for (uint walk = 0u; walk < walks; ++walk)
    {
        float3 position = start;
        float3 direction = GiCosineHemisphere(startNormal, rng);
        uint faceFlags = 1u << face;
        float3 throughput = 1.0f;
        [loop] for (uint bounce = 0u; bounce <= g_GiSkinWalkBounces; ++bounce)
        {
            // The DDA clamps its start into the brick, so a start already outside (a surface that
            // lies on a brick face) must exit here instead of hitting its own voxel at t = 0.
            AttributeData attribute;
            attribute.matID_faceNormal = 0u;
            float t = 0.0f;
            if (all(position >= 0.0f) && all(position < float(SIZE_IN_BLOCKS)))
            {
                _CaelixBrickPage = context.group.page;
                t = CaelixTraceBrickRay(context.recordBase, position, direction, 0.0f,
                    (faceFlags << 26u) | context.coarse, attribute);
                // A hit at zero distance means the start cell itself is solid; stop rather than loop.
                if (attribute.matID_faceNormal != 0u && t <= 0.0f) break;
            }
            if (attribute.matID_faceNormal != 0u)
            {
                uint blockId = attribute.matID_faceNormal >> 16u;
                faceFlags = attribute.matID_faceNormal & 63u;
                VoxelMaterial material = GET_MATERIAL(int(blockId));
                total += throughput * max(0.0f, material.emission);
                // Interior walks are diffuse-only; a transparent interior voxel ends the walk.
                if (!IsOpaque(int(blockId))) break;
                throughput *= saturate(material.albedo) * (1.0f - saturate(material.metallic));
                if (max(throughput.x, max(throughput.y, throughput.z)) <= 1e-3f) break;
                float3 normal = float3(UnpackObjectNormal(faceFlags));
                position = position + direction * t + normal * K_RAY_ORIGIN_PUSH_OFF;
                direction = GiCosineHemisphere(normal, rng);
                continue;
            }

            float3 exitPosition;
            uint exitFace;
            float exitT;
            uint texel = GiSkinExit(position, direction, exitPosition, exitFace, exitT);
            if (context.slot != GI_SKIN_INVALID)
            {
                if (allowMark) GiSkinMark(context.slot, texel);
                float3 irradiance;
                if (GiSkinRead(context.slot, texel, irradiance))
                {
                    total += throughput * irradiance / GI_PI;
                    break;
                }
            }
            // Cold texel. A genuine interior exit starts just inside the brick so a solid voxel
            // across the boundary is entered through its face; an immediate exit already stands
            // in front of its surface.
            float3 origin = exitT > 0.0f ? exitPosition - float3(objectNormals[exitFace]) * K_RAY_ORIGIN_PUSH_OFF : position;
            RayDesc ray;
            ray.Origin = GiSkinToWorld(context.group, context.origin + origin);
            ray.Direction = normalize(GiSkinDirectionToWorld(context.group, direction));
            ray.TMin = 0.0f;
            ray.TMax = K_T_MAX;
            total += throughput * GiTracePath(ray, 0u, rng, g_GiMaxBounces);
            break;
        }
    }
    return total / float(walks);
}

// Shades a surface. Diffuse reflection comes from skin walks; emission is exact; glossy and
// transmissive lobes continue with real rays and are shaded the same way where they land.
// `skipFirstEmission` drops the first vertex's emission when emitter sampling already covers it.
float3 GiSkinShadeChain(GiSurface surface, RayDesc ray, inout uint rng, bool allowMark, bool skipFirstEmission)
{
    float3 radiance = 0.0f;
    float3 throughput = 1.0f;
    uint medium = 0u;
    float etaScale = 1.0f;
    [loop] for (uint vertex = 0u; vertex <= g_GiMaxBounces; ++vertex)
    {
        GiBsdf bsdf = GiMakeBsdf(surface, medium);
        if (!(vertex == 0u && skipFirstEmission)) radiance += throughput * bsdf.emission;
        if (vertex == g_GiMaxBounces) break;
        float3 wo = -ray.Direction;
        float facing = dot(surface.normal, wo);
        float3 normal = facing >= 0.0f ? surface.normal : -surface.normal;
        float3 diffuse = GiDiffuseReflectance(bsdf);
        bool cached = false;
        if (bsdf.transparent == 0u && medium == 0u && facing > 0.0f && any(diffuse > 0.0f))
        {
            GiSkinBrickContext context;
            if (GiSkinResolveBrick(surface, allowMark, context))
            {
                uint face = surface.faceKey.y >> 28u;
                float3 localPosition = GiSkinToObject(context.group, surface.position) - context.origin;
                radiance += throughput * diffuse * GiSkinWalks(context, localPosition, face, rng, allowMark);
                if (allowMark) GiSkinTouch(context.slot);
                cached = true;
            }
        }
        GiBsdfSample sample = GiSample(bsdf, normal, wo, rng);
        if (sample.pdf <= 0.0f) break;
        if (cached && sample.delta == 0u)
        {
            float3 f;
            float pdf;
            GiEvaluate(bsdf, normal, wo, sample.direction, f, pdf);
            sample.weight = max(0.0f, f - diffuse / GI_PI) * (abs(dot(normal, sample.direction)) / sample.pdf);
        }
        if (!any(sample.weight > 0.0f)) break;
        throughput *= sample.weight;
        if (sample.transmission != 0u) etaScale *= (bsdf.etaT * bsdf.etaT) / (bsdf.etaI * bsdf.etaI);
        medium = GiNextMedium(bsdf, sample, medium);
        if (vertex >= 3u)
        {
            float survival = min(0.95f, max(throughput.x, max(throughput.y, throughput.z)) * etaScale);
            if (survival <= 0.0f || GiRandom(rng) >= survival) break;
            throughput /= survival;
        }
        ray = GiRayFromSurface(surface, sample.direction);
        if (!GiTrace(ray, surface))
        {
            radiance += throughput * GiMediumTransmittance(medium, K_T_MAX) * GiSky(ray.Direction);
            break;
        }
        throughput *= GiMediumTransmittance(medium, length(surface.position - ray.Origin));
    }
    return GiSanitize(radiance);
}

// Irradiance a texel receives directly from the emitter proposal, one sample.
float3 GiSkinEmitterIrradiance(float3 origin, float3 normal, inout uint rng)
{
    GiEmissionSample light;
    float4 u = float4(GiRandom(rng), GiRandom(rng), GiRandom(rng), GiRandom(rng));
    if (!GiSampleEmission(origin, u, light)) return 0.0f;
    float cosine = dot(normal, light.direction);
    if (cosine <= 0.0f) return 0.0f;
    RayDesc visibility;
    visibility.Origin = origin;
    visibility.Direction = light.direction;
    visibility.TMin = 0.0f;
    visibility.TMax = light.distance + 0.01f;
    GiSurface endpoint;
    if (!GiTrace(visibility, endpoint) || any(endpoint.faceKey != light.faceKey)
        || length(endpoint.position - light.position) > 0.01f) return 0.0f;
    return GiSanitize(light.radiance * (cosine / light.pdf));
}

#endif
