#ifndef CAELIX_INSTANCE_RECORD_INCLUDED
#define CAELIX_INSTANCE_RECORD_INCLUDED

// The per-RTAS-instance record. An inline ray query has no shader table, so this is how
// per-instance data reaches the trace: the kernel reads the record of every procedural candidate.

// One record per RTAS instance, indexed by InstanceID(). Mirrors Caelix.Rendering.RayQuery.CaelixRayQueryInstance.
struct CaelixRayQueryInstance
{
    // Rows of the previous frame's object-to-world matrix (Matrix4x4.GetRow), stored as rows so
    // the layout does not depend on HLSL matrix packing rules.
    float4 prevRow0;
    float4 prevRow1;
    float4 prevRow2;
    float4 prevRow3;
    // Rows 0..2 of the CURRENT object-to-world matrix; row 3 is (0, 0, 0, 1).
    float4 curRow0;
    float4 curRow1;
    float4 curRow2;
    // Word offset of this group's first brick inside its page.
    uint brickBase;
    uint hashSeed;
    // Brick pool page holding this group's bricks.
    uint page;
    // Non-zero while the slot is allocated, fresh on every allocation. GI samples store it so a
    // sample whose slot was freed and reused by another group is rejected instead of moved.
    uint lifetimeToken;
};

StructuredBuffer<CaelixRayQueryInstance> g_Instances;

inline float4x4 CaelixInstancePrevObjectToWorld(CaelixRayQueryInstance inst)
{
    return float4x4(inst.prevRow0, inst.prevRow1, inst.prevRow2, inst.prevRow3);
}

inline float4x4 CaelixInstanceObjectToWorld(CaelixRayQueryInstance inst)
{
    return float4x4(inst.curRow0, inst.curRow1, inst.curRow2, float4(0.0f, 0.0f, 0.0f, 1.0f));
}

// Rigid inverse of CaelixInstanceObjectToWorld: entity scale is 1 by contract, so the inverse is
// the transposed rotation and the translation -R^T t.
inline float4x4 CaelixInstanceWorldToObject(CaelixRayQueryInstance inst)
{
    float3x3 r = float3x3(inst.curRow0.xyz, inst.curRow1.xyz, inst.curRow2.xyz);
    float3x3 rt = transpose(r);
    float3 t = float3(inst.curRow0.w, inst.curRow1.w, inst.curRow2.w);
    float3 it = -mul(rt, t);
    return float4x4(
        float4(rt[0], it.x),
        float4(rt[1], it.y),
        float4(rt[2], it.z),
        float4(0.0f, 0.0f, 0.0f, 1.0f));
}

#endif
