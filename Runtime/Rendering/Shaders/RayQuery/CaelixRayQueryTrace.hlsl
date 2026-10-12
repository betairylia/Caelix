#ifndef CAELIX_RAY_QUERY_TRACE_INCLUDED
#define CAELIX_RAY_QUERY_TRACE_INCLUDED

// Traces one ray against the voxel RTAS with an inline ray query, running the brick DDA on every
// procedural candidate. The AABB instances still carry a material (Unity requires one), but it is
// the CaelixAabbInstance stub and its hit group never runs. The including compute kernel must
// define CAELIX_INLINE_RAY_QUERY, CAELIX_LAUNCH_INDEX, and declare
// `RaytracingAccelerationStructure g_AccelStruct;` before including this file.

#include "../Utils/Utils.hlsl"

#include "../CaelixBrickPages.hlsl"

#include "../CaelixBrickTrace.hlsl"

// The per-instance record table, g_Instances: page, brick base and previous transform per RTAS instance.
#include "../CaelixInstanceRecord.hlsl"

// On a hit the payload carries T, the packed (blockID << 16 | faceNormalFlags) word, the packed
// world normal and the packed previous-frame world offset; on a miss the payload stays cleared,
// which is exactly what CaelixApplyVoxelMiss would leave behind.
void CaelixRayQueryTrace(RayDesc ray, out RayPayload payload)
{
    CaelixClearRayPayload(payload);

    UnityRayQuery<RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(g_AccelStruct, RAY_FLAG_NONE, 0xFF, ray);

    uint committedAttrib = 0u;
    uint committedInstance = 0u;

    while (q.Proceed())
    {
        if (q.CandidateType() != CANDIDATE_PROCEDURAL_PRIMITIVE)
        {
            continue;
        }

        uint instanceId = q.CandidateInstanceID();
        // One fetch: the record carries both the page every brick load below switches on and the
        // sector's word offset inside that page.
        CaelixRayQueryInstance inst = g_Instances[instanceId];
        _CaelixBrickPage = inst.page;
        uint brickBase = inst.brickBase + CaelixBrickBase(q.CandidatePrimitiveIndex());

        AttributeData attrib;
        float t = CaelixTraceBrickPrimitiveCore(
            brickBase, q.CandidateObjectRayOrigin(), q.CandidateObjectRayDirection(), q.CommittedRayT(), attrib);

        // Nothing rejects a t outside [TMin, TCurrent] for us, so the test is done here.
        if (attrib.matID_faceNormal != 0u && t >= ray.TMin && t < q.CommittedRayT())
        {
            q.CommitProceduralPrimitiveHit(t);
            committedAttrib = attrib.matID_faceNormal;
            committedInstance = instanceId;
        }
    }

    if (q.CommittedStatus() != COMMITTED_PROCEDURAL_PRIMITIVE_HIT)
    {
        return;
    }

    float T = q.CommittedRayT();
    float3x4 objectToWorld = q.CommittedObjectToWorld3x4();

    payload.T = T;
    payload.materialID_voxelFaceHash = committedAttrib;
    payload.instanceId = committedInstance;
    payload.packedWorldNormal = CaelixPackWorldNormal(mul((float3x3)objectToWorld, UnpackObjectNormal(committedAttrib)));

    CaelixRayQueryInstance inst = g_Instances[committedInstance];
    float4x4 prevObjectToWorld = CaelixInstancePrevObjectToWorld(inst);
    float3 objectHitPosition = q.CommittedObjectRayOrigin() + q.CommittedObjectRayDirection() * T;
    float3 worldHitPosition = ray.Origin + ray.Direction * T;
    payload.packedPrevWorldOffset = CaelixPackFloat3ToHalf4(
        mul(prevObjectToWorld, float4(objectHitPosition, 1.0f)).xyz - worldHitPosition);
}

#endif
