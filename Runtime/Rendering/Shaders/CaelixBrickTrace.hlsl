#ifndef CAELIX_BRICK_TRACE_INCLUDED
#define CAELIX_BRICK_TRACE_INCLUDED

#ifndef SHIFT_SIZE_IN_BLOCKS
#define SHIFT_SIZE_IN_BLOCKS 3
#define SHIFT_SIZE_IN_BLOCKS_U 3u
#endif

#ifndef SIZE_IN_BLOCKS
#define SIZE_IN_BLOCKS (1 << SHIFT_SIZE_IN_BLOCKS)
#define SIZE_IN_BLOCKS_U (1u << SHIFT_SIZE_IN_BLOCKS_U)
#endif

// Render group shape: bits of the group-local brick index per axis. Must match
// RenderGroupPresets.Keyword on the C# side. Default (no keyword) is 16x16x16 bricks.
#if defined(CAELIX_GROUP_3_3_3)
#define CAELIX_GROUP_SHIFT_X 3u
#define CAELIX_GROUP_SHIFT_Y 3u
#define CAELIX_GROUP_SHIFT_Z 3u
#elif defined(CAELIX_GROUP_5_5_5)
#define CAELIX_GROUP_SHIFT_X 5u
#define CAELIX_GROUP_SHIFT_Y 5u
#define CAELIX_GROUP_SHIFT_Z 5u
#elif defined(CAELIX_GROUP_4_7_4)
#define CAELIX_GROUP_SHIFT_X 4u
#define CAELIX_GROUP_SHIFT_Y 7u
#define CAELIX_GROUP_SHIFT_Z 4u
#else
#define CAELIX_GROUP_SHIFT_X 4u
#define CAELIX_GROUP_SHIFT_Y 4u
#define CAELIX_GROUP_SHIFT_Z 4u
#endif

#define CAELIX_GROUP_MASK_X ((1u << CAELIX_GROUP_SHIFT_X) - 1u)
#define CAELIX_GROUP_MASK_Y ((1u << CAELIX_GROUP_SHIFT_Y) - 1u)

#ifndef BRICK_RAY_MAX_STEPS
#define BRICK_RAY_MAX_STEPS 22
#endif

#ifndef BRICK_COARSE_RAY_MAX_STEPS
#define BRICK_COARSE_RAY_MAX_STEPS 4
#endif

#ifndef BRICK_MICRO_RAY_MAX_STEPS
#define BRICK_MICRO_RAY_MAX_STEPS 10
#endif

// Word 0: absolute brick index + coarse occupancy. Word 1: packed tight occupied
// bounds (also keeps uint64_t occupancy loads 8-byte aligned).
// Word 1 layout (matches BrickRecordLayout.PackBrickTightBounds, both bounds inclusive):
// [minX:0-2][minY:3-5][minZ:6-8][maxX:9-11][maxY:12-14][maxZ:15-17]
#define BRICK_INFO_WORDS 2
#define BRICK_TIGHT_AXIS_MASK 7u
#define BRICK_TIGHT_MAX_SHIFT 9u
#define BRICK_OCCUPANCY_WORDS 16
#define BRICK_BLOCK_DATA_OFFSET 18
#define BRICK_DATA_LENGTH 274
#define BRICK_INFO_ABSOLUTE_INDEX_MASK 0xFFFFu
#define BRICK_INFO_COARSE_OCCUPANCY_SHIFT 16u
#define BRICK_INFO_COARSE_OCCUPANCY_MASK 0xFFu
#define BRICK_COARSE_SIZE_IN_BLOCKS 4
#define BRICK_MICRO_SHIFT 2
#define BRICK_MICRO_SIZE (1 << BRICK_MICRO_SHIFT)
#define BRICK_MICRO_MASK (BRICK_MICRO_SIZE - 1)

#define VOXEL_FACE_HASH_MASK 0x3FFu

#ifndef BRICK_RAY_GRID_EPSILON
#define BRICK_RAY_GRID_EPSILON 0.0001f
#endif

#ifndef BRICK_RAY_STEP_EPSILON
#define BRICK_RAY_STEP_EPSILON 0.0001f
#endif

#ifndef BRICK_RAY_MIN_DIR
#define BRICK_RAY_MIN_DIR 1e-20f
#endif

#include "RayPayload.hlsl"
#include "def/CaelixUtils.hlsl"
#include "Utils/BlueNoise.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

/*
float t
6bit normal
16bit matID
normalFlags: 0b000000 -> <0/low> X+ X- Y+ Y- Z+ Z- <5/high>, only 1 bit set; 0 means miss
*/
struct CaelixBrickHit
{
    float t;
    uint materialID_faceNormal;
};

struct AttributeData
{
    // (31) [matID:16] [reserved] [faceNormalFlags:6] (0)
    uint matID_faceNormal;
};

static half3 objectNormals[6] = {
    half3( 1, 0, 0),
    half3(-1, 0, 0),
    half3(0,  1, 0),
    half3(0, -1, 0),
    half3(0, 0,  1),
    half3(0, 0, -1),
};

inline half3 UnpackObjectNormal(uint normalFlag)
{
    return objectNormals[firstbitlow(normalFlag)];
}

// Brick record loads. Every brick lives in the shared brick pool, so the three macros route
// through the page switch in CaelixBrickPages.hlsl; the including file defines them before it
// includes this one (CaelixRayQueryTrace.hlsl is where that order is fixed).

inline CaelixBrickHit CaelixMakeBrickMiss()
{
    CaelixBrickHit hit;
    hit.t = 0.0f;
    hit.materialID_faceNormal = 0u;
    return hit;
}

inline uint CaelixHashAvalanche(uint value)
{
    value ^= value >> 16;
    value *= 0x7FEB352Du;
    value ^= value >> 15;
    value *= 0x846CA68Bu;
    value ^= value >> 16;
    return value;
}

inline uint CaelixFaceID(int3 normal)
{
    // TODO: FIXME: bruh
    if (normal.x > 0) return 0u;
    if (normal.x < 0) return 1u;
    if (normal.y > 0) return 2u;
    if (normal.y < 0) return 3u;
    if (normal.z > 0) return 4u;
    return 5u;
}

// No face hash is produced: NormalTarget.b carries the 6-bit face-direction flags instead. The
// per-instance seed the old body hashed with lives in the instance record (hashSeed) if this is
// ever revived.
inline uint CaelixMakeVoxelFaceHash(int3 localVoxelPos, int3 normal)
{
    return 0u;
}

inline int3 CaelixBrickRayIntMask(bool3 mask)
{
    return int3(mask.x ? 1 : 0, mask.y ? 1 : 0, mask.z ? 1 : 0);
}

inline int CaelixBrickRayStepAxis(half direction)
{
    return direction > 0.0h ? 1 : -1;
}

inline float CaelixBrickRaySafeInvAxis(float direction)
{
    if (abs(direction) >= BRICK_RAY_MIN_DIR)
    {
        return 1.0f / direction;
    }

    return direction < 0.0f ? -1.0f / BRICK_RAY_MIN_DIR : 1.0f / BRICK_RAY_MIN_DIR;
}

inline float3 CaelixBrickRaySafeInvDir(float3 rayDir)
{
    return float3(
        CaelixBrickRaySafeInvAxis(rayDir.x),
        CaelixBrickRaySafeInvAxis(rayDir.y),
        CaelixBrickRaySafeInvAxis(rayDir.z));
}

inline float3 CaelixBrickRayBoundaryOffset(float3 rayDir)
{
    return float3(
        rayDir.x >= 0.0f ? 1.0f : 0.0f,
        rayDir.y >= 0.0f ? 1.0f : 0.0f,
        rayDir.z >= 0.0f ? 1.0f : 0.0f);
}

inline uint CaelixBrickRayNormalFlags(uint index, int3 cell, float3 tStart, half3 rayDir, half3 invDir, uint entryNormalFlags, out float T)
{
    if (index == 0u)
    {
        T = 0;
        return entryNormalFlags;
    }
    
    // This is brutal. Ideas?
    // Ties can happen on voxel edges/corners. Pick one face deterministically,
    // matching the X/Y/Z priority used by the brick AABB entry normal.
    
    float3 sideDist = tStart + float3(cell) * invDir;
    T = min(min(sideDist.x, sideDist.y), sideDist.z);

    bool3 axisMask = sideDist <= float3(
        T + BRICK_RAY_STEP_EPSILON,
        T + BRICK_RAY_STEP_EPSILON,
        T + BRICK_RAY_STEP_EPSILON);
    
    uint xUse = axisMask.x ? 0xFFFFFFFFu : 0u;
    uint yUse = (axisMask.y ? 0xFFFFFFFFu : 0u) & ~xUse;
    uint zUse = ~(xUse | yUse);

    uint xFlag = rayDir.x > 0.0h ? 0b000010 : 0b000001;
    uint yFlag = rayDir.y > 0.0h ? 0b001000 : 0b000100;
    uint zFlag = rayDir.z > 0.0h ? 0b100000 : 0b010000;

    return (xFlag & xUse) | (yFlag & yUse) | (zFlag & zUse);
}

inline int CaelixReadBrick(uint brickBase, int3 localBlockPos)
{
    uint shift = uint((1 - (localBlockPos.x & 1)) << 4);
    // return int((g_bricks[brickBase + ((localBlockPos.x >> 1) + (localBlockPos.y << 2) + (localBlockPos.z << 5))] >> shift) & 0xFFFFu);
    return int((CAELIX_BRICKS_LOAD((brickBase + ((localBlockPos.x >> 1) + (localBlockPos.y << 2) + (localBlockPos.z << 5))) << 2) >> shift) & 0xFFFFu);
}

inline uint CaelixBrickBase(uint brickID)
{
    return brickID * BRICK_DATA_LENGTH;
}

inline uint CaelixGetCoarseOccupancy(uint brickInfo)
{
    return (brickInfo >> BRICK_INFO_COARSE_OCCUPANCY_SHIFT) & BRICK_INFO_COARSE_OCCUPANCY_MASK;
}

inline uint CaelixMicroOccupancyBit(int3 microCell)
{
    return uint(microCell.x | (microCell.y << 2) | (microCell.z << 4));
}

inline void CaelixLoadMicroOccupancy(uint brickBase, uint coarseBit, out uint64_t occ)
{
    uint occupancyBase = brickBase + BRICK_INFO_WORDS + coarseBit * 2u;
    occ = CAELIX_BRICKS_LOAD64(occupancyBase << 2);
    // occLo = g_bricks[occupancyBase];
    // occHi = g_bricks[occupancyBase + 1u];
}

inline bool CaelixIsMicroOccupied(uint64_t occ, uint microBit)
{
    return (occ & (1ull << (microBit))) != 0;
}

inline bool CaelixShouldTraceMicroOccupancy(uint64_t occ, half3 rayDir)
{
    // Future LUT early rejection can key off the 64-bit occupancy and a binned ray direction here.
    return true;
}

inline bool CaelixShouldTerminateBrickRay(uint index, int blockID, uint normalFlags, float entryT)
{
    bool shouldTerminate = IsOpaque(blockID);
    UNITY_BRANCH if (shouldTerminate) return shouldTerminate;
    
    // Transparency
    if (index == 0 && entryT == 0) return false;
    shouldTerminate = GetFaceBits(blockID) & normalFlags;
    return shouldTerminate;
}

inline int3 VRTJump(int3 cell, float3 rayDir, uint mask)
{
    return lerp(cell | mask, cell & (~mask), rayDir < 0);
}

inline int3 VRTStep(int3 cell, float3 tStart, float3 origin, float3 rayDir, half3 invDir)
{
    float3 sideDist = tStart + float3(cell) * invDir;
    float tmin = min(min(sideDist.x, sideDist.y), sideDist.z) + BRICK_RAY_STEP_EPSILON;
    float3 currPos = origin + tmin * rayDir;
    return int3(floor(currPos));
}

inline float CaelixTraceBrickRay(uint brickBase, float3 entryPositionInBrick, float3 rayDir, float entryT, uint entryNormal_coarseOccupancy, out AttributeData attrib)
{
    uint64_t occ = 0ull;

    half3 invDir = CaelixBrickRaySafeInvDir(rayDir);
    float3 tStart = (CaelixBrickRayBoundaryOffset(rayDir) - entryPositionInBrick) * invDir;
    float3 origin = clamp(entryPositionInBrick, 0.0f, float3(SIZE_IN_BLOCKS, SIZE_IN_BLOCKS, SIZE_IN_BLOCKS) - BRICK_RAY_GRID_EPSILON);
    int3 cell = int3(floor(origin));
    int3 prevCell = cell;
    
    // attrib.matID_faceNormal = ((entryNormal_coarseOccupancy & 0xFFFF) << 16) + (entryNormal_coarseOccupancy >> 26);
    // attrib.matID_faceNormal = ((0x8D3F & 0xFFFF) << 16) + (entryNormal_coarseOccupancy >> 26);
    // return entryT;

    [loop] for (uint rayStep = 0; rayStep < BRICK_RAY_MAX_STEPS; rayStep++)
    {
        // Out of bounds
        if (any(uint3(cell) >= SIZE_IN_BLOCKS)) { break; }

        // Coarse jumps
        uint3 coarseCell = cell >> BRICK_MICRO_SHIFT;
        uint coarseBit = uint(coarseCell.x | (coarseCell.y << 1) | (coarseCell.z << 2));
        if ((entryNormal_coarseOccupancy & (1u << coarseBit)) == 0u)
        {
            // Faster than compute the exact distance needed to jump multiple voxels
            cell = VRTJump(cell, rayDir, 3u);
            prevCell = cell;
            cell = VRTStep(cell, tStart, origin, rayDir, invDir);
            continue;
        }
        
        // Let L1 Cache do its job
        // TODO: Cache prev coarsebit or something?
        CaelixLoadMicroOccupancy(brickBase, coarseBit, occ);
        if (!CaelixShouldTraceMicroOccupancy(occ, rayDir))
        {
            prevCell = cell;
            cell = VRTStep(cell, tStart, origin, rayDir, invDir);
            continue;
        }

        int3 microCell = cell & BRICK_MICRO_MASK;
        uint microBit = CaelixMicroOccupancyBit(microCell);
        if (CaelixIsMicroOccupied(occ, microBit))
        {
            int blockID = CaelixReadBrick(brickBase + BRICK_BLOCK_DATA_OFFSET, cell);
            float T;
            uint normalFlags = CaelixBrickRayNormalFlags(rayStep, prevCell, tStart, rayDir, invDir, (entryNormal_coarseOccupancy >> 26), T);
            bool shouldTerminate = CaelixShouldTerminateBrickRay(
                rayStep, blockID, normalFlags, entryT);

            if (shouldTerminate)
            {
                attrib.matID_faceNormal = (blockID << 16) + normalFlags;
                return entryT + T;
            }
        }

        prevCell = cell;
        cell = VRTStep(cell, tStart, origin, rayDir, invDir);
    }

    // no hit
    attrib.matID_faceNormal = 0u;
    return 0;
}

// Ray-vs-brick intersection, with every input passed in rather than read from a DXR intrinsic, so
// the inline ray query kernel can run it on each procedural candidate. `brickBase` is the word
// offset of the brick record inside the brick pool page the candidate's instance record names,
// which is the buffer CAELIX_BRICKS_LOAD reads.
inline float CaelixTraceBrickPrimitiveCore(uint brickBase, float3 objectRayOrigin, float3 objectRayDir, float tCurrent, out AttributeData attrib)
{
    // attrib.matID_faceNormal = ((0x8001 & 0xFFFF) << 16) + (0b010000 >> 26);
    // return 10;

    uint2 brickInfo = CAELIX_BRICKS_LOAD2(brickBase << 2);

    // Empty brick
    // if(CaelixGetCoarseOccupancy(brickInfo.x) == 0)
    // {
    //     return false;
    // }

    // AABB Intersection against the tight occupied-bounds box (word 1).
    // The DDA below still runs in full-brick coordinates; only the entry point moves.
    uint idx = brickInfo.x & BRICK_INFO_ABSOLUTE_INDEX_MASK;
    int bX = int((idx & CAELIX_GROUP_MASK_X) << SHIFT_SIZE_IN_BLOCKS);
    int bY = int(((idx >> CAELIX_GROUP_SHIFT_X) & CAELIX_GROUP_MASK_Y) << SHIFT_SIZE_IN_BLOCKS);
    int bZ = int((idx >> (CAELIX_GROUP_SHIFT_X + CAELIX_GROUP_SHIFT_Y)) << SHIFT_SIZE_IN_BLOCKS);

    uint tightBounds = brickInfo.y;
    float3 tightMin = float3(
        tightBounds & BRICK_TIGHT_AXIS_MASK,
        (tightBounds >> 3u) & BRICK_TIGHT_AXIS_MASK,
        (tightBounds >> 6u) & BRICK_TIGHT_AXIS_MASK);
    float3 tightMax = float3(
        (tightBounds >> BRICK_TIGHT_MAX_SHIFT) & BRICK_TIGHT_AXIS_MASK,
        (tightBounds >> (BRICK_TIGHT_MAX_SHIFT + 3u)) & BRICK_TIGHT_AXIS_MASK,
        (tightBounds >> (BRICK_TIGHT_MAX_SHIFT + 6u)) & BRICK_TIGHT_AXIS_MASK) + 1.0f;

    float3 brickOrigin = float3(bX, bY, bZ);
    float3 aabbMin = brickOrigin + tightMin;
    float3 aabbMax = brickOrigin + tightMax;

    float3 rayDir = objectRayDir;
    half3 invDir = 1.0h / rayDir;
    float3 t0 = (aabbMin - objectRayOrigin) * invDir;
    float3 t1 = (aabbMax - objectRayOrigin) * invDir;

    float3 tmin = min(t0, t1);
    float3 tmax = max(t0, t1);

    float largestTmin = max(max(tmin.x, tmin.y), tmin.z);
    float smallestTmax = min(min(tmax.x, tmax.y), tmax.z);

    if (largestTmin > smallestTmax || smallestTmax < 0 || largestTmin > tCurrent)
    {
        attrib.matID_faceNormal = 0;
        return 0;
    }

    float t = max(0, largestTmin);
    float3 entryPositionInBrick = objectRayOrigin + rayDir * t - brickOrigin;

    // TODO: Do coarse bit (2x2x2) early reject here?
    // CaelixGetCoarseOccupancy(brickInfo.x)

    // TODO: branchless?
    uint normalFlags;
    if (largestTmin == tmin.x)
    {
        normalFlags = rayDir.x > 0 ? 0b000010 : 0b000001;
    }
    else if (largestTmin == tmin.y)
    {
        normalFlags = rayDir.y > 0 ? 0b001000 : 0b000100;
    }
    else
    {
        normalFlags = rayDir.z > 0 ? 0b100000 : 0b010000;
    }

    CaelixBrickHit result = CaelixMakeBrickMiss();
    return CaelixTraceBrickRay(brickBase, entryPositionInBrick, rayDir, t,
        (normalFlags << 26)  | CaelixGetCoarseOccupancy(brickInfo.x),
        attrib);
}

#endif
