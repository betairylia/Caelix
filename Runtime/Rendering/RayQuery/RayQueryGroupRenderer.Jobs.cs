using Unity.Burst;
using Unity.Collections;
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
    /// and opens a 27-brick window around it for the face culling.
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

        public void Execute()
        {
            syncRecord[0] = 0;

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
                    ProcessBrick(key, true, ref removedStagingBase);
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

                    bool isAdded = (change.RequiredFlags & DirtyFlags.BlockBrickAdded) != 0;
                    bool needRebuilt = (change.RequiredFlags & DirtyFlags.GeometryWithLocalNeighbor) != 0;
                    if (!isAdded && !needRebuilt)
                    {
                        continue;
                    }

                    ProcessBrick(change.Key, isAdded, ref removedStagingBase);
                }
            }

            if (removedStagingBase.IsCreated)
            {
                removedStagingBase.Dispose();
            }
        }

        /// <summary>
        /// Reserves the staging record a brick's words are written into: a zeroed block appended
        /// to <see cref="stagingWords"/>, with <paramref name="rendererBrickId"/> appended to
        /// <see cref="stagingSlots"/>.
        /// </summary>
        /// <remarks>
        /// A brick removed earlier in this run has already staged a zeroed record for its slot,
        /// and <see cref="SparseBrickIdTable"/> hands a freed id straight back out, so a brick
        /// added later in the same run can claim that very slot. Two staged records for one slot
        /// would race inside the scatter kernel — its threads run in no order — so the removal's
        /// record is taken over instead of a second one being appended. It is already zeroed,
        /// which is exactly what a fresh record needs.
        /// </remarks>
        private int TakeStagingRecord(int rendererBrickId, ref NativeArray<int> removedStagingBase)
        {
            if (removedStagingBase.IsCreated && removedStagingBase[rendererBrickId] >= 0)
            {
                int reused = removedStagingBase[rendererBrickId];
                removedStagingBase[rendererBrickId] = -1;
                return reused;
            }

            int stagingBase = stagingWords.Length;
            stagingWords.Resize(stagingBase + BrickRecordLayout.BRICK_DATA_LENGTH, NativeArrayOptions.ClearMemory);
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

        private unsafe void ProcessBrick(int3 key, bool isAdded, ref NativeArray<int> removedStagingBase)
        {
            int3 local = grouping.LocalBrick(key);

            if (!data.TryBindBrick(SectorSlotId.Block, key, out Block* brick))
            {
                // The brick's storage went away (or never existed). Retire whatever the renderer
                // still holds for that position.
                RemoveRendererBrick(local, ref removedStagingBase);
                return;
            }

            VoxelNeighborhood neighborhood = data.OpenNeighborhood(key);

            int localIdx = grouping.LocalBrickIdx(local);

            // Group-local block coordinates: the frame the instance's translation is built in.
            int3 brickBlockPos = BrickKey.ToBlockOrigin(local);

            // Neighbour reads are ENTITY-local, the frame the neighbourhood window was opened in.
            int3 brickOriginBlock = BrickKey.ToBlockOrigin(key);

            int rendererBrickId = -1;
            int rendererBrickBase = -1;

            uint coarseOccupancy = 0;
            int3 occupiedMin = new int3(BrickKey.BlocksPerAxis);
            int3 occupiedMax = new int3(-1);

            // Brick data
            for (int bz = 0; bz < BrickKey.BlocksPerAxis; bz++)
            {
                for (int by = 0; by < BrickKey.BlocksPerAxis; by++)
                {
                    // Assume X-First
                    for (int bx = 0; bx < BrickKey.BlocksPerAxis; bx += 2)
                    {
                        Block block0 = brick[BrickKey.ToBlockIdx(bx, by, bz)];
                        Block block1 = brick[BrickKey.ToBlockIdx(bx + 1, by, bz)];

                        int rendererBlockIdx = BrickKey.ToBlockIdx(bx, by, bz) / 2;

                        uint block0Data = BrickRecordLayout.GetRendererBlockData(
                            block0, brickOriginBlock + new int3(bx, by, bz), ref neighborhood);
                        uint block1Data = BrickRecordLayout.GetRendererBlockData(
                            block1, brickOriginBlock + new int3(bx + 1, by, bz), ref neighborhood);

                        // Do nothing if blocks are empty. The staged record starts zeroed, so an
                        // empty pair simply stays zero.
                        if (Block.IsRendererDataEmpty(block0Data) && Block.IsRendererDataEmpty(block1Data))
                        {
                            continue;
                        }

                        // Brick not allocated yet
                        if (rendererBrickId == -1)
                        {
                            // Claim this brick's renderer id
                            isAdded = rendererBrickMap.AddBrickAt(localIdx, out rendererBrickId, out bool requireExtension);
                            if (requireExtension)
                            {
                                aabbBuffer.Resize(rendererBrickMap.Capacity, NativeArrayOptions.UninitializedMemory);
                            }

                            // The record is staged zeroed, so the block words before this one,
                            // the occupancy words and the info words need no explicit reset.
                            rendererBrickBase = TakeStagingRecord(rendererBrickId, ref removedStagingBase);
                        }

                        stagingWords[rendererBrickBase + BrickRecordLayout.BRICK_BLOCK_DATA_OFFSET + rendererBlockIdx] =
                            unchecked((int)((block0Data << 16) | block1Data));

                        if (!Block.IsRendererDataEmpty(block0Data))
                        {
                            AccumulateOccupancy(ref coarseOccupancy, rendererBrickBase, bx, by, bz);
                            occupiedMin = math.min(occupiedMin, new int3(bx, by, bz));
                            occupiedMax = math.max(occupiedMax, new int3(bx, by, bz));
                        }

                        if (!Block.IsRendererDataEmpty(block1Data))
                        {
                            AccumulateOccupancy(ref coarseOccupancy, rendererBrickBase, bx + 1, by, bz);
                            occupiedMin = math.min(occupiedMin, new int3(bx + 1, by, bz));
                            occupiedMax = math.max(occupiedMax, new int3(bx + 1, by, bz));
                        }
                    }
                }
            }

            // Handle removal if brick empty
            // TODO: Compaction?
            if (coarseOccupancy == 0)
            {
                RemoveRendererBrick(local, ref removedStagingBase);
                return;
            }

            stagingWords[rendererBrickBase] = BrickRecordLayout.PackBrickInfo(localIdx, coarseOccupancy);
            stagingWords[rendererBrickBase + 1] = BrickRecordLayout.PackBrickTightBounds(occupiedMin, occupiedMax);

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

        private void AccumulateOccupancy(ref uint coarseOccupancy, int bp, int bx, int by, int bz)
        {
            int coarseBit = BrickRecordLayout.ToCoarseOccupancyBit(bx, by, bz);
            int microBit = BrickRecordLayout.ToMicroOccupancyBit(bx, by, bz);
            int wordOffset = BrickRecordLayout.ToOccupancyWordOffset(coarseBit, microBit);
            uint wordBit = 1u << (microBit & 31);

            coarseOccupancy |= 1u << coarseBit;
            stagingWords[bp + wordOffset] = unchecked((int)(uint)stagingWords[bp + wordOffset] | (int)wordBit);
        }
    }
}
