using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Caelix.Rendering.RayQuery
{
    /// <summary>
    /// Burst-compiled job that turns one render group's slice of an entity's change list into
    /// GPU-ready brick records and AABBs.
    /// </summary>
    /// <remarks>
    /// Per brick it writes one record in the layout <see cref="BrickRecordLayout"/> describes, in
    /// group-local brick positions. Work is selected from the change list rather than by sweeping
    /// brick positions: the job walks the entries this cycle already named, binds each brick by key
    /// and binds its six face neighbors for SIMD face culling.
    /// </remarks>
    [BurstCompile]
    internal struct GenerateGroupRenderDataJob : IJob
    {
        /// <summary>The entity's storage. Bricks are bound by key; nothing here knows about sectors.</summary>
        [ReadOnly] public VoxelEntityData data;

        /// <summary>
        /// This cycle's changes, reordered so that one group's entries are contiguous. Several
        /// group jobs read the same array at once, which is what the read-only marking allows.
        /// </summary>
        [ReadOnly] public NativeArray<BrickChange> changes;

        /// <summary>Index of this group's first entry in <see cref="changes"/>.</summary>
        public int start;

        /// <summary>Number of entries that belong to this group.</summary>
        public int count;

        /// <summary>The group being generated, in group coordinates.</summary>
        public int3 groupKey;

        /// <summary>The group shape: how brick keys map to this group and to its local indices.</summary>
        public RenderGroup grouping;

        /// <summary>Generate every allocated brick of the group instead of only the changed ones.</summary>
        public bool forceFullUpload;

        /// <summary>
        /// Group-local brick position to renderer brick id, indexed by the grouping's flat
        /// brick index; one slot per brick of the group.
        /// </summary>
        public SparseBrickIdTable rendererBrickMap;

        /// <summary>
        /// Buffer of all AABB bounding boxes used for RayTracingAccelerationStructure.
        /// </summary>
        public NativeList<BrickRecordLayout.BrickAABB> aabbBuffer;

        /// <summary>
        /// The brick records this run rewrote, back to back,
        /// <see cref="BrickRecordLayout.BRICK_DATA_LENGTH"/> words each.
        /// </summary>
        /// <remarks>
        /// Records exist on the GPU only. This job stages the ones it touched and the renderer
        /// scatters them into the brick pool, so nothing keeps a host copy of a whole group's
        /// bricks (1096 bytes each, several GB on a large world).
        /// </remarks>
        public NativeList<int> stagingWords;

        /// <summary>
        /// Renderer brick id of each record in <see cref="stagingWords"/>, in the same order:
        /// where the record has to land inside the group's pool range.
        /// </summary>
        public NativeList<int> stagingSlots;

        /// <summary>
        /// One element: 1 when any AABB changed, 0 otherwise. A changed AABB forces the renderer
        /// to replace its AABB buffer, because Unity builds static AABB geometry once per
        /// (buffer, count) and ignores later writes.
        /// </summary>
        public NativeArray<int> syncRecord;

        public unsafe void Execute()
        {
            syncRecord[0] = 0;
            // Reuse one scratch record for the whole job, including completely culled bricks.
            int* record = stackalloc int[BrickRecordLayout.BRICK_DATA_LENGTH];

            // Staging index of every slot a removal has already written, or -1. Allocated on the
            // first removal only; see TakeStagingRecord for what it is for.
            NativeArray<int> removedStagingBase = default;

            if (forceFullUpload)
            {
                // The renderer's previous state may name bricks whose storage went away while the
                // group waited for pool room: those keys are absent from the enumeration below and
                // their removal records were consumed by earlier cycles. Start from nothing.
                RetireAllRendererBricks(ref removedStagingBase);

                foreach (int3 key in data.EnumerateBricks(
                             grouping.FirstKey(groupKey), grouping.LastKey(groupKey)))
                {
                    ProcessBrick(key, record, ref removedStagingBase);
                }
            }
            else
            {
                for (int i = start; i < start + count; i++)
                {
                    BrickChange change = changes[i];

                    if (change.Kind == ChangeKind.Removed)
                    {
                        RemoveRendererBrick(grouping.LocalBrick(change.Key), ref removedStagingBase);
                        continue;
                    }

                    bool isAdded = (change.RequireUpdateFlags & BrickUpdateFlags.BlockBrickAdded) != 0;
                    bool needRebuilt = (change.RequireUpdateFlags & BrickUpdateFlags.GeometryWithLocalNeighbor) != 0;
                    if (!isAdded && !needRebuilt)
                    {
                        continue;
                    }

                    ProcessBrick(change.Key, record, ref removedStagingBase);
                }
            }

            if (removedStagingBase.IsCreated)
            {
                removedStagingBase.Dispose();
            }
        }

        /// <summary>
        /// Reserves the staging record a brick's words are written into: a block appended
        /// to <see cref="stagingWords"/>, with <paramref name="rendererBrickId"/> appended to
        /// <see cref="stagingSlots"/>.
        /// </summary>
        /// <remarks>
        /// A brick removed earlier in this run has already staged a zeroed record for its slot,
        /// and <see cref="SparseBrickIdTable"/> hands a freed id straight back out, so a brick
        /// added later in the same run can claim that very slot. Two staged records for one slot
        /// would race inside the scatter kernel — its threads run in no order — so the removal's
        /// record is taken over instead of a second one being appended. Removal records request
        /// zero initialization; live records overwrite every word from their scratch record.
        /// </remarks>
        private int TakeStagingRecord(int rendererBrickId, ref NativeArray<int> removedStagingBase,
            NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            if (removedStagingBase.IsCreated && removedStagingBase[rendererBrickId] >= 0)
            {
                int reused = removedStagingBase[rendererBrickId];
                removedStagingBase[rendererBrickId] = -1;
                return reused;
            }

            int stagingBase = stagingWords.Length;
            stagingWords.Resize(stagingBase + BrickRecordLayout.BRICK_DATA_LENGTH, options);
            stagingSlots.Add(rendererBrickId);
            return stagingBase;
        }

        /// <summary>
        /// Retires every renderer brick of the group, so a full rebuild re-adds exactly the bricks
        /// that exist now. The re-adds reuse the zeroed records staged here (see
        /// <see cref="TakeStagingRecord"/>), so a surviving brick costs no extra record.
        /// </summary>
        private unsafe void RetireAllRendererBricks(ref NativeArray<int> removedStagingBase)
        {
            if (!rendererBrickMap.IsCreated || rendererBrickMap.Count == 0)
            {
                return;
            }

            for (int i = 0; i < rendererBrickMap.SlotCount; i++)
            {
                if (rendererBrickMap.indices[i] == SparseBrickIdTable.EMPTY)
                {
                    continue;
                }

                // The loop index IS the slot, so no position round trip is needed.
                RemoveRendererBrickAt(i, ref removedStagingBase);
            }
        }

        /// <summary>
        /// Retires one group-local brick position: an all-zero record so the slot traces as empty,
        /// an inactive AABB so it drops out of the BLAS, and the renderer id back on the free list.
        /// No-op when the position holds no renderer brick.
        /// </summary>
        private void RemoveRendererBrick(int3 local, ref NativeArray<int> removedStagingBase)
            => RemoveRendererBrickAt(grouping.LocalBrickIdx(local), ref removedStagingBase);

        /// <summary>
        /// Retires one group-local brick slot. Same work as <see cref="RemoveRendererBrick"/>, for
        /// the callers that already hold the flat slot index.
        /// </summary>
        private void RemoveRendererBrickAt(int slot, ref NativeArray<int> removedStagingBase)
        {
            int removed = rendererBrickMap.RemoveBrickAt(slot);
            if (removed == SparseBrickIdTable.EMPTY)
            {
                return;
            }

            if (!removedStagingBase.IsCreated)
            {
                removedStagingBase = new NativeArray<int>(
                    rendererBrickMap.SlotCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < rendererBrickMap.SlotCount; i++)
                {
                    removedStagingBase[i] = -1;
                }
            }

            // An all-zero record: no occupancy, so the slot traces as empty even if the
            // acceleration structure has not been rebuilt yet.
            int removedBase = TakeStagingRecord(removed, ref removedStagingBase);
            stagingWords[removedBase] = BrickRecordLayout.PackBrickInfo(slot, 0);
            removedStagingBase[removed] = removedBase;

            // A NaN min.x marks the AABB as an inactive primitive (DXR spec), so the freed slot
            // drops out of the BLAS at the next build instead of leaving a stale full-brick box
            // over dead data.
            aabbBuffer[removed] = new BrickRecordLayout.BrickAABB()
            {
                min = new Vector3(float.NaN, float.NaN, float.NaN),
                max = new Vector3(float.NaN, float.NaN, float.NaN)
            };
            syncRecord[0] = 1;
        }

        private unsafe void ProcessBrick(int3 key, int* record, ref NativeArray<int> removedStagingBase)
        {
            int3 local = grouping.LocalBrick(key);

            if (!data.TryBindBrick(SectorSlotId.Block, key, out Block* brick))
            {
                // The brick's storage went away (or never existed). Retire whatever the renderer
                // still holds for that position.
                RemoveRendererBrick(local, ref removedStagingBase);
                return;
            }

            int localIdx = grouping.LocalBrickIdx(local);

            // Group-local block coordinates: the frame the instance's translation is built in.
            int3 brickBlockPos = BrickKey.ToBlockOrigin(local);

            uint coarseOccupancy = BrickRecordLayout.GenerateBrickRecord(data, key, brick, record,
                out int3 occupiedMin, out int3 occupiedMax);

            // Retire the renderer slot when face culling leaves no visible voxel data.
            if (coarseOccupancy == 0)
            {
                RemoveRendererBrick(local, ref removedStagingBase);
                return;
            }

            bool isAdded = rendererBrickMap.AddBrickAt(localIdx, out int rendererBrickId, out bool requireExtension);
            if (requireExtension)
            {
                aabbBuffer.Resize(rendererBrickMap.Capacity, NativeArrayOptions.UninitializedMemory);
            }

            record[0] = BrickRecordLayout.PackBrickInfo(localIdx, coarseOccupancy);
            record[1] = BrickRecordLayout.PackBrickTightBounds(occupiedMin, occupiedMax);
            int rendererBrickBase = TakeStagingRecord(rendererBrickId, ref removedStagingBase,
                NativeArrayOptions.UninitializedMemory);
            UnsafeUtility.MemCpy((int*)stagingWords.GetUnsafePtr() + rendererBrickBase, record,
                BrickRecordLayout.BRICK_DATA_LENGTH * sizeof(int));

            // AABB tight to the occupied blocks, in group-local block coordinates.
            // Rewritten on every rebuild since edits can grow or shrink the bounds;
            // syncRecord[0] (=> BLAS rebuild) is raised only when the box actually
            // changed, or for new bricks whose slot may hold garbage/NaN.
            BrickRecordLayout.BrickAABB tightAABB = new BrickRecordLayout.BrickAABB()
            {
                min = new Vector3(brickBlockPos.x + occupiedMin.x,
                                  brickBlockPos.y + occupiedMin.y,
                                  brickBlockPos.z + occupiedMin.z),
                max = new Vector3(brickBlockPos.x + occupiedMax.x + 1,
                                  brickBlockPos.y + occupiedMax.y + 1,
                                  brickBlockPos.z + occupiedMax.z + 1)
            };

            BrickRecordLayout.BrickAABB previousAABB = aabbBuffer[rendererBrickId];
            bool boundsChanged = isAdded
                || previousAABB.min.x != tightAABB.min.x
                || previousAABB.min.y != tightAABB.min.y
                || previousAABB.min.z != tightAABB.min.z
                || previousAABB.max.x != tightAABB.max.x
                || previousAABB.max.y != tightAABB.max.y
                || previousAABB.max.z != tightAABB.max.z;

            if (boundsChanged)
            {
                aabbBuffer[rendererBrickId] = tightAABB;
                syncRecord[0] = 1;
            }
        }
    }
}
