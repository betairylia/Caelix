#ifndef CAELIX_GI_EMISSION_INCLUDED
#define CAELIX_GI_EMISSION_INCLUDED

struct GiEmissionGroup
{
    float4 row0;
    float4 row1;
    float4 row2;
    uint page;
    uint brickBase;
    uint brickCount;
    uint firstBrick;
    uint groupToken;
    uint3 padding;
};

struct GiEmissionVoxelWeights
{
    float4 low;
    float4 high;
};

StructuredBuffer<GiEmissionGroup> g_GiEmissionGroups;
RWStructuredBuffer<GiEmissionVoxelWeights> g_GiEmissionVoxelWeights;
RWStructuredBuffer<float> g_GiEmissionTree;
uint g_GiEmissionGroupCount;
uint g_GiEmissionBrickCount;
uint g_GiEmissionLeafCount;
uint g_GiEmissionBuildOffset;
uint g_GiEmissionBuildCount;
uint g_GiEmissionLevelOffset;
uint g_GiEmissionLevelCount;

uint GiEmissionFindGroup(uint brick)
{
    uint lo = 0u;
    uint hi = g_GiEmissionGroupCount;
    while (lo + 1u < hi)
    {
        uint mid = (lo + hi) >> 1u;
        if (g_GiEmissionGroups[mid].firstBrick <= brick) lo = mid;
        else hi = mid;
    }
    return lo;
}

bool GiEmissionContainsGroup(uint token)
{
    // Descriptors are sorted by lifetime token; firstBrick follows that order.
    uint lo = 0u;
    uint hi = g_GiEmissionGroupCount;
    while (lo < hi)
    {
        uint mid = (lo + hi) >> 1u;
        uint candidate = g_GiEmissionGroups[mid].groupToken;
        if (candidate < token) lo = mid + 1u;
        else hi = mid;
    }
    if (lo >= g_GiEmissionGroupCount) return false;
    return g_GiEmissionGroups[lo].groupToken == token && g_GiEmissionGroups[lo].brickCount > 0u;
}

uint GiEmissionMaterial(uint brickBase, uint voxelIndex)
{
    uint word = CAELIX_BRICKS_LOAD((brickBase + BRICK_BLOCK_DATA_OFFSET + (voxelIndex >> 1u)) << 2u);
    return (word >> (((voxelIndex & 1u) ^ 1u) * 16u)) & 0xffffu;
}

float GiEmissionVoxelPower(uint materialId)
{
    if (materialId == 0u || !IsOpaque(int(materialId))) return 0.0f;
    float3 emission = GET_MATERIAL(int(materialId)).emission;
    return all(isfinite(emission)) ? max(0.0f, dot(emission, float3(0.2126f, 0.7152f, 0.0722f))) * 6.0f : 0.0f;
}

struct GiEmissionSample
{
    float3 position;
    float3 normal;
    float3 direction;
    float3 radiance;
    float distance;
    float pdf;
    uint2 faceKey;
};

float GiEmissionPdf(float3 from, float3 lightPosition, float3 lightNormal, float3 emission, uint2 faceKey)
{
    // A ray can hit a group whose published range was unavailable to the builder.
    // Such a surface has no support in this proposal and must retain full BSDF weight.
    if (g_GiEmissionBrickCount == 0u || !GiEmissionContainsGroup(faceKey.x)) return 0.0f;
    float total = g_GiEmissionTree[1];
    float3 difference = lightPosition - from;
    float distanceSquared = dot(difference, difference);
    if (total <= 0.0f || distanceSquared <= 1e-12f) return 0.0f;
    float cosine = dot(lightNormal, -difference * rsqrt(distanceSquared));
    if (cosine <= 1e-7f) return 0.0f;
    // Unit voxel faces, with identical power on all six faces. Occluded faces
    // remain in the proposal; the visibility ray rejects them without changing PDF.
    return max(0.0f, dot(emission, float3(0.2126f, 0.7152f, 0.0722f))) * distanceSquared / (total * cosine);
}

bool GiSampleEmission(float3 from, float4 u, out GiEmissionSample sample)
{
    sample = (GiEmissionSample)0;
    if (g_GiEmissionBrickCount == 0u || g_GiEmissionGroupCount == 0u) return false;
    float total = g_GiEmissionTree[1];
    if (total <= 0.0f || !isfinite(total)) return false;
    float target = min(u.x, 0.99999994f) * total;
    uint node = 1u;
    while (node < g_GiEmissionLeafCount)
    {
        node <<= 1u;
        float left = g_GiEmissionTree[node];
        if (target >= left)
        {
            target -= left;
            ++node;
        }
    }
    uint brick = node - g_GiEmissionLeafCount;
    if (brick >= g_GiEmissionBrickCount) return false;
    GiEmissionVoxelWeights weights = g_GiEmissionVoxelWeights[brick];
    uint segment = 7u;
    [unroll] for (uint i = 0u; i < 8u; ++i)
    {
        float weight = i < 4u ? weights.low[i] : weights.high[i - 4u];
        if (target < weight || i == 7u) { segment = i; break; }
        target -= weight;
    }
    GiEmissionGroup group = g_GiEmissionGroups[GiEmissionFindGroup(brick)];
    uint brickBase = group.brickBase + (brick - group.firstBrick) * BRICK_DATA_LENGTH;
    _CaelixBrickPage = group.page;
    uint selectedVoxel = 0xffffffffu;
    uint selectedMaterial = 0u;
    [loop] for (uint v = segment * 64u; v < (segment + 1u) * 64u; ++v)
    {
        uint materialId = GiEmissionMaterial(brickBase, v);
        float power = GiEmissionVoxelPower(materialId);
        if (power > 0.0f && target < power)
        {
            selectedVoxel = v;
            selectedMaterial = materialId;
            break;
        }
        target -= power;
    }
    if (selectedVoxel == 0xffffffffu) return false;
    uint brickIndex = CAELIX_BRICKS_LOAD(brickBase << 2u) & BRICK_INFO_ABSOLUTE_INDEX_MASK;
    uint3 brickPosition = uint3(brickIndex & CAELIX_GROUP_MASK_X,
        (brickIndex >> CAELIX_GROUP_SHIFT_X) & CAELIX_GROUP_MASK_Y,
        brickIndex >> (CAELIX_GROUP_SHIFT_X + CAELIX_GROUP_SHIFT_Y)) * 8u;
    uint3 voxel = brickPosition + uint3(selectedVoxel & 7u, (selectedVoxel >> 3u) & 7u, selectedVoxel >> 6u);
    uint face = min(5u, uint(u.y * 6.0f));
    float3 n = float3(objectNormals[face]);
    float3 tangent = abs(n.x) > 0.5f ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 bitangent = cross(n, tangent);
    float3 localPosition = float3(voxel) + 0.5f + 0.5f * n + (u.z - 0.5f) * tangent + (u.w - 0.5f) * bitangent;
    sample.position = float3(dot(group.row0, float4(localPosition, 1)), dot(group.row1, float4(localPosition, 1)), dot(group.row2, float4(localPosition, 1)));
    sample.normal = normalize(float3(dot(group.row0.xyz, n), dot(group.row1.xyz, n), dot(group.row2.xyz, n)));
    float3 difference = sample.position - from;
    sample.distance = length(difference);
    sample.direction = difference / max(1e-7f, sample.distance);
    sample.radiance = max(0.0f, GET_MATERIAL(int(selectedMaterial)).emission);
    sample.faceKey = uint2(group.groupToken, voxel.x | (voxel.y << 9u) | (voxel.z << 19u) | (face << 28u));
    sample.pdf = GiEmissionPdf(from, sample.position, sample.normal, sample.radiance, sample.faceKey);
    return sample.pdf > 0.0f;
}

#endif
