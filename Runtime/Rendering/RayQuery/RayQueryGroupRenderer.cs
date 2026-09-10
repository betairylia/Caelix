using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Caelix.Client;
using Caelix.Utils;

namespace Caelix.Rendering.RayQuery
{
    /// <summary>
    /// Prepares one render group's data and keeps its acceleration-structure instance up to date.
    /// </summary>
    /// <remarks>
    /// Work arrives as a slice of the entity's per-cycle change list, keyed by brick key. Brick
    /// records go into the shared <see cref="CaelixBrickPool"/>, because a ray query kernel has no
    /// shader table and therefore no per-instance binding; the previous transform and the group
    /// hash seed travel through <see cref="CaelixRayQueryInstanceTable"/>, addressed by the RTAS
    /// instance ID.
    /// <para>
    /// The AABB buffer is thrown away and reallocated whenever a tight box moves: Unity builds
    /// static AABB geometry once per (buffer, aabbCount) and ignores later writes, so that is the
    /// only way a moved box reaches the BLAS. See
    /// <c>Documentation~/internals/rendering/UNITY_RTAS_AABB_ISSUES.md</c>.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Bookkeeping of the acceleration-structure instance handles the scene renderer owns.
    /// </summary>
    /// <remarks>
    /// Unity recycles a freed handle on the very next <c>AddInstance</c> (LIFO), and
    /// <c>RemoveInstance</c> with a handle the caller no longer owns silently deletes whoever holds
    /// it now. A group that removes with a stale handle therefore makes two groups share one
    /// instance, and each rebuild of either hides the other. This ledger names the colliding call
    /// the moment it happens, with both owners, instead of leaving a silently missing group.
    /// </remarks>
    public sealed class InstanceHandleLedger
    {
        private readonly Dictionary<int, RayQueryGroupRenderer> owners = new();

        /// <summary>Records that <paramref name="owner"/> now holds <paramref name="handle"/>.</summary>
        public void Claim(int handle, RayQueryGroupRenderer owner)
        {
            if (handle == 0)
            {
                Debug.LogError($"RayQueryGroupRenderer: AddInstance failed for group {owner.GroupKey}.");
                return;
            }

            if (owners.TryGetValue(handle, out RayQueryGroupRenderer existing) && !ReferenceEquals(existing, owner))
            {
                Debug.LogError(
                    $"RayQueryGroupRenderer: handle {handle} returned for group {owner.GroupKey} is still " +
                    $"held by group {existing.GroupKey}; a stale RemoveInstance freed it.");
            }

            owners[handle] = owner;
        }

        /// <summary>Records that <paramref name="owner"/> is about to remove <paramref name="handle"/>.</summary>
        public void Release(int handle, RayQueryGroupRenderer owner)
        {
            if (!owners.TryGetValue(handle, out RayQueryGroupRenderer existing))
            {
                Debug.LogError(
                    $"RayQueryGroupRenderer: group {owner.GroupKey} removes handle {handle}, which no group holds.");
                return;
            }

            if (!ReferenceEquals(existing, owner))
            {
                Debug.LogError(
                    $"RayQueryGroupRenderer: group {owner.GroupKey} removes handle {handle}, which belongs to " +
                    $"group {existing.GroupKey}.");
                return;
            }

            owners.Remove(handle);
        }

        /// <summary>Number of handles currently claimed.</summary>
        public int Count => owners.Count;
    }

    public class RayQueryGroupRenderer : IDisposable
    {
        private readonly Material groupMaterial;
        private readonly int groupHashSeed;
        private readonly InstanceHandleLedger ledger;

        /// <summary>The shape of this group: how many bricks it covers per axis, and how they index.</summary>
        private readonly RenderGroup grouping;

        private NativeList<BrickRecordLayout.BrickAABB> hostAABBBuffer;
        private SparseBrickIdTable rendererBrickMap;

        /// <summary>
        /// The brick records the last render job rewrote, plus the slot each of them belongs in.
        /// Created per job, consumed and disposed by <see cref="UploadBricks"/>.
        /// </summary>
        /// <remarks>
        /// There is no host copy of the group's records: only the bricks a job actually touched
        /// ever exist on the CPU, and only until they have been scattered into the pool.
        /// </remarks>
        private NativeList<int> stagingWords;
        private NativeList<int> stagingSlots;

        private GraphicsBuffer aabbBuffer;

        /// <summary>
        /// The AABB buffer the live instance still references after <see cref="aabbBuffer"/> was
        /// replaced. Released only at the end of <see cref="RenderModifyAS"/>, never before.
        /// </summary>
        /// <remarks>
        /// Disposing a GraphicsBuffer that a live acceleration-structure instance references purges
        /// that instance at once and pushes its handle onto the LIFO free list, with no call on our
        /// side. Both scene renderers replace buffers in one pass and remove-and-add instances in a
        /// later pass, so an eager dispose let the first group to re-add pop the LAST purged group's
        /// handle, and that group's own RemoveInstance then deleted it: two groups sharing one
        /// instance, each rebuild hiding the other. Verified 2026-09-10 on 6000.5.6f1 / D3D12; see
        /// <c>RtasAabbBufferLifetimeTests</c>.
        /// </remarks>
        private GraphicsBuffer staleAabbBuffer;

        /// <summary>This group's slice of the brick pool. Null until the first allocation.</summary>
        /// <remarks>
        /// A class the pool owns and mutates: compacting a page rewrites the offset in place. Read
        /// its fields fresh every use rather than caching them.
        /// </remarks>
        private CaelixBrickPool.Handle poolHandle;

        /// <summary>
        /// Set when the group's records are gone and every brick has to be generated again: the
        /// pool had no room, so the range — and with it everything the pool would have carried over
        /// — was dropped.
        /// </summary>
        private bool needsFullRebuild;

        /// <summary>Slot in the instance table, and therefore this group's RTAS instance ID.</summary>
        private int instanceSlot = -1;

        /// <summary>Word offset and page of the range the instance record currently names; -1 = never published.</summary>
        private int publishedBrickBase = -1;
        private int publishedPage = -1;

        private int groupASHandle;
        private bool hasRenderable;
        private bool isDirty;
        private bool shouldRemove;

        private RayTracingAABBsInstanceConfig AABBconfig;
        private Matrix4x4 previousObjectToWorld;
        private bool hasPreviousObjectToWorld;

        // Temp variables used in the render process. RenderEmitJob and ApplyCompletedRenderJob must
        // run in the same frame / tick.
        private JobHandle jobHandle;
        private GenerateGroupRenderDataJob rendererJob;
        private bool jobScheduled;

        /// <summary>
        /// Number of brick slots the group currently occupies.
        /// </summary>
        /// <remarks>
        /// Always taken from the sparse brick map. <c>CAELIX_RENDER_DISABLE_CULLING</c> is not
        /// honoured here: the pool addresses bricks by the renderer's own compacted ids, which
        /// only exist with culling on.
        /// </remarks>
        public int BrickBufferSize => rendererBrickMap.IsCreated ? rendererBrickMap.Capacity : 0;

        /// <summary>
        /// Number of bricks the group actually renders. Zero means the scene renderer can drop it:
        /// nothing points at its pool range or its instance any more.
        /// </summary>
        public int RendererBrickCount => rendererBrickMap.IsCreated ? rendererBrickMap.Count : 0;

        /// <summary>
        /// True while the group's records are gone and every brick has to be generated again. The
        /// scene renderer emits for such a group even when this cycle's changes name none of it.
        /// </summary>
        public bool NeedsFullRebuild => needsFullRebuild;

        /// <summary>
        /// True while the group carries unfinished state from an earlier tick and therefore has to
        /// be visited again even when nothing changed.
        /// </summary>
        /// <remarks>
        /// Three states: <see cref="needsFullRebuild"/> (the pool had no room, so every brick has to
        /// be generated again), <see cref="isDirty"/> (an instance rebuild still waiting for pool
        /// room) and a stale AABB buffer that no <see cref="RenderModifyAS"/> has released yet. A
        /// group that reports false is idle, and the scene renderer does not visit it at all.
        /// </remarks>
        public bool HasPendingWork => needsFullRebuild || isDirty || staleAabbBuffer != null;

        /// <summary>True between <see cref="RenderEmitJob"/> and <see cref="ApplyCompletedRenderJob"/>.</summary>
        public bool HasScheduledJob => jobScheduled;

        /// <summary>The view this group belongs to. Fixed for the renderer's life.</summary>
        public EntityView View { get; }

        /// <summary>True while the group holds an acceleration-structure instance.</summary>
        internal bool HasRenderable => hasRenderable;

        /// <summary>
        /// True when the published instance record names the range the pool currently holds for this
        /// group; a group with no range has nothing to publish and counts as matching.
        /// </summary>
        /// <remarks>
        /// The invariant the idle-group skipping rests on: a group whose range moved must be visited
        /// before the next trace, or it names bricks that now belong to somebody else.
        /// </remarks>
        internal bool PublishedRecordMatchesPool
        {
            get
            {
                if (poolHandle == null || !poolHandle.IsValid)
                {
                    return true;
                }

                return publishedBrickBase == poolHandle.OffsetBricks * BrickRecordLayout.BRICK_DATA_LENGTH
                    && publishedPage == poolHandle.Page;
            }
        }

        /// <summary>
        /// Gets the estimated host memory usage in bytes for this renderer's buffers: the AABB list
        /// only, because brick records are never mirrored on the host.
        /// </summary>
        public ulong MemoryUsage =>
            (ulong)(hostAABBBuffer.IsCreated ? hostAABBBuffer.Capacity * sizeof(float) * 6 : 0);

        /// <summary>Gets the estimated VRAM usage in bytes: the AABB buffer plus this group's pool range.</summary>
        public ulong VRAMUsage =>
            (ulong)((aabbBuffer?.count ?? 0) * 24 +
                    (poolHandle?.CapacityBricks ?? 0) * BrickRecordLayout.BRICK_DATA_LENGTH * 4);

        /// <summary>Bytes the group's AABB buffer occupies, or 0 while it has none.</summary>
        public long AabbVramBytes => (long)(aabbBuffer?.count ?? 0) * 24;

        private bool HostBufferInitialized => hostAABBBuffer.IsCreated;

        /// <summary>
        /// Constructs a renderer for one (view, render group) pair.
        /// </summary>
        /// <param name="entity">The view the group belongs to; part of the hash seed.</param>
        /// <param name="groupKey">The group's position in group coordinates.</param>
        /// <param name="material">
        /// Material handed to <see cref="RayTracingAABBsInstanceConfig"/>. The ray query path never
        /// runs its hit group, but the config requires one.
        /// </param>
        /// <param name="ledger">Shared bookkeeping of the scene renderer's instance handles.</param>
        /// <param name="grouping">
        /// The group shape. Fixed for the renderer's life: the scene renderer drops every group and
        /// re-uploads when its setting changes.
        /// </param>
        public RayQueryGroupRenderer(
            EntityView entity, int3 groupKey, Material material, InstanceHandleLedger ledger,
            RenderGroup grouping)
        {
            GroupKey = groupKey;
            View = entity;
            groupMaterial = material;
            this.ledger = ledger;
            this.grouping = grouping;
            groupHashSeed = unchecked((int)ComputeGroupHashSeed(entity, groupKey));
        }

        /// <summary>The group this renderer draws, in group coordinates.</summary>
        public int3 GroupKey { get; }

        private static uint HashUInt(uint value)
        {
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value;
        }

        private static uint ComputeGroupHashSeed(EntityView entity, int3 groupKey)
        {
            uint seed = (uint)entity.Guid.GetHashCode();
            seed ^= (uint)groupKey.x * 0x9E3779B9u;
            seed ^= (uint)groupKey.y * 0x85EBCA6Bu;
            seed ^= (uint)groupKey.z * 0xC2B2AE35u;
            return HashUInt(seed);
        }

        /// <summary>
        /// Schedules the Burst job that turns this cycle's changes into render data.
        /// Must be followed by <see cref="ApplyCompletedRenderJob"/> in the same frame.
        /// </summary>
        /// <param name="data">The entity's storage. Bricks are bound by key inside the job.</param>
        /// <param name="sortedChanges">
        /// The cycle's changes, reordered so that one group's entries are contiguous. Several group
        /// jobs read it at once, which is why the job takes it read-only.
        /// </param>
        /// <param name="start">Index of this group's first entry in <paramref name="sortedChanges"/>.</param>
        /// <param name="count">Number of entries that belong to this group.</param>
        public void RenderEmitJob(
            VoxelEntityData data, NativeArray<BrickChange> sortedChanges, int start, int count)
        {
            if (shouldRemove || (count <= 0 && !needsFullRebuild))
            {
                return;
            }

            PreallocateBuffers();

            rendererJob = new GenerateGroupRenderDataJob()
            {
                data = data,
                changes = sortedChanges,
                start = start,
                count = count,
                groupKey = GroupKey,
                grouping = grouping,
                forceFullUpload = needsFullRebuild,
                rendererBrickMap = rendererBrickMap,
                aabbBuffer = hostAABBBuffer,
                stagingWords = new NativeList<int>(
                    BrickRecordLayout.BRICK_DATA_LENGTH * 16, Allocator.TempJob),
                stagingSlots = new NativeList<int>(16, Allocator.TempJob),
                syncRecord = new NativeArray<int>(1, Allocator.TempJob)
            };

            stagingWords = rendererJob.stagingWords;
            stagingSlots = rendererJob.stagingSlots;
            jobHandle = rendererJob.Schedule();

            jobScheduled = true;
        }

        /// <summary>Creates the host buffers on the first emit.</summary>
        /// <remarks>
        /// There is no brick count to size them from: the job discovers the group's bricks as it
        /// walks the changes, and grows the AABB list whenever the brick map extends.
        /// </remarks>
        private void PreallocateBuffers()
        {
            if (HostBufferInitialized)
            {
                return;
            }

            hostAABBBuffer = new NativeList<BrickRecordLayout.BrickAABB>(0, Allocator.Persistent);
            rendererBrickMap = SparseBrickIdTable.New(grouping.BricksInGroup, Allocator.Persistent);
        }

        internal bool TryGetScheduledJobHandle(out JobHandle handle)
        {
            handle = jobHandle;
            return jobScheduled;
        }

        /// <summary>
        /// Consumes the completed render job: replaces the AABB buffer when a tight box moved and
        /// resizes this group's pool range.
        /// </summary>
        /// <remarks>
        /// The staged brick records are deliberately written later, in <see cref="UploadBricks"/>:
        /// resizing a range can grow a pool page, which replaces its <see cref="GraphicsBuffer"/>
        /// and re-packs every other range on it. Every group must therefore learn its final range
        /// before any of them writes.
        /// </remarks>
        /// <returns>True when the AABB buffer was replaced this tick.</returns>
        internal bool ApplyCompletedRenderJob(CaelixBrickPool pool)
        {
            if (!jobScheduled)
            {
                return false;
            }

            jobScheduled = false;

            bool aabbChanged = rendererJob.syncRecord[0] > 0;
            rendererJob.syncRecord.Dispose();

            // Unity builds static AABB geometry once per (buffer, aabbCount) and ignores later
            // writes, so a moved tight box only reaches the BLAS through a brand-new buffer.
            // See Documentation~/internals/rendering/UNITY_RTAS_AABB_ISSUES.md for the full reasoning.
            bool aabbRealloc = aabbChanged || aabbBuffer == null;
            if (aabbRealloc)
            {
                // The instance still references the buffer it was added with. Keep that one until
                // RenderModifyAS has re-added the instance; an intermediate buffer no instance ever
                // saw can go at once.
                if (staleAabbBuffer == null)
                {
                    staleAabbBuffer = aabbBuffer;
                }
                else
                {
                    aabbBuffer?.Dispose();
                }

                // Sized to the bricks the group actually holds, not to its full extent. Safe because
                // every growth of the id table raises syncRecord (a new id sets isAdded, which sets
                // boundsChanged), so a bigger table always arrives together with a reallocation.
                aabbBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, math.max(1, BrickBufferSize), 24);

                // Invalidate the AABBconfig so RenderModifyAS recreates it against the new buffer.
                AABBconfig.aabbCount = 0;

                // Always the whole host list, and only here: the buffer was just replaced, so a
                // partial write would leave every untouched box uninitialised — and while the boxes
                // did not change there is nothing to write at all.
                if (hostAABBBuffer.IsCreated && hostAABBBuffer.Length > 0)
                {
                    aabbBuffer.SetData(hostAABBBuffer.AsArray());
                }
            }

            int requestedCapacity = BrickRecordLayout.GetCapacity(BrickBufferSize);
            if (requestedCapacity != (poolHandle?.CapacityBricks ?? 0))
            {
                // Reallocate carries the records the range already holds over to the new one, so a
                // resize costs a GPU copy rather than a full regeneration.
                poolHandle = pool.Reallocate(poolHandle, requestedCapacity);

                // The pool hands a moved handle back to the scene renderer, which finds the group
                // that has to republish its record through this back reference.
                if (poolHandle != null && poolHandle.IsValid)
                {
                    poolHandle.Owner = this;
                }
            }

            if (poolHandle == null || !poolHandle.IsValid)
            {
                // Every page is full, and the old range went with the failed reallocation, so every
                // brick has to be generated again once there is room. The capacity test above
                // retries on every tick because an invalid handle reports capacity 0. Until one
                // succeeds RenderModifyAS skips this group, so it is simply not drawn.
                needsFullRebuild = true;
                return aabbRealloc;
            }

            needsFullRebuild = false;

            if (aabbRealloc)
            {
                AABBconfig = default;
                isDirty = true;
            }

            return aabbRealloc;
        }

        /// <summary>
        /// Adds this group's staged brick records to the frame's scatter batch. Runs after every
        /// group has settled its range, so a pool growth caused by one group is seen by all of
        /// them.
        /// </summary>
        /// <remarks>
        /// Staged, not dispatched: the renderer flushes the whole frame's batch once, after every
        /// group has staged. See <see cref="CaelixBrickGpuOps"/> for why writing and dispatching
        /// per group hung the device.
        /// </remarks>
        /// <returns>Number of brick records staged.</returns>
        internal int UploadBricks(CaelixBrickPool pool)
        {
            if (!stagingSlots.IsCreated)
            {
                return 0;
            }

            int staged = stagingSlots.Length;

            if (stagingSlots.Length > 0 && poolHandle != null && poolHandle.IsValid)
            {
                Debug.Assert(
                    BrickBufferSize <= poolHandle.CapacityBricks,
                    "RayQueryGroupRenderer: the group has more bricks than the pool range reserved for it.");

                pool.Ops.StageScatter(
                    pool.GetPageBuffer(poolHandle.Page),
                    poolHandle.OffsetBricks,
                    stagingWords.AsArray(),
                    stagingSlots.AsArray(),
                    stagingSlots.Length);
            }

            // StageScatter has copied the records into the frame staging buffer, so the lists go
            // back immediately rather than waiting for the flush.
            DisposeStaging();
            return staged;
        }

        /// <summary>Releases the per-job staging lists.</summary>
        private void DisposeStaging()
        {
            if (stagingWords.IsCreated) stagingWords.Dispose();
            if (stagingSlots.IsCreated) stagingSlots.Dispose();
        }

        /// <summary>
        /// Updates the acceleration structure and the instance record with this group's state.
        /// </summary>
        /// <remarks>
        /// Three outcomes: rebuild the instance when geometry changed, retrack it while the entity
        /// moves (or for the one frame its motion vectors must settle), otherwise do nothing.
        /// </remarks>
        /// <returns>True when the instance was rebuilt (removed and re-added) this tick.</returns>
        public bool RenderModifyAS(
            ref RayTracingAccelerationStructure AS,
            EntityView entity,
            int3 groupKey,
            CaelixRayQueryInstanceTable table)
        {
            Matrix4x4 objectToWorld =
                entity.LocalToWorld *
                Matrix4x4.Translate(grouping.BlockOrigin(groupKey).ToVector3Int());

            // Set for the frame an entity flips to static (including the initial flip on a
            // born-static body). Collapsing prev onto the current transform zeroes the motion
            // vectors once, so the denoiser stops reprojecting a body that will never move again.
            // CaelixRayQueryRenderer clears the flag after every group of the entity consumed it.
            bool resetsMotionVectors = entity.ShouldResetMotionVectors;
            Matrix4x4 prevObjectToWorld = (hasPreviousObjectToWorld && !resetsMotionVectors)
                ? previousObjectToWorld
                : objectToWorld;

            // A dirty group with no renderable bricks is legitimate (freshly loaded, or fully
            // culled) and RayTracingAABBsInstanceConfig throws on aabbCount == 0, so skip it; the
            // group re-dirties and retries once real geometry appears. A group the pool could not
            // find room for is skipped the same way.
            bool hasPool = poolHandle != null && poolHandle.IsValid;

            // Fall back to the last published range while the pool has no room for this group, so
            // a retrack cannot overwrite a live instance's record with a null range.
            int brickBaseWords = hasPool
                ? poolHandle.OffsetBricks * BrickRecordLayout.BRICK_DATA_LENGTH
                : publishedBrickBase;
            int brickPage = hasPool ? poolHandle.Page : publishedPage;

            bool rebuildsInstance = isDirty && BrickBufferSize > 0 && hasPool;
            bool retracksInstance = hasRenderable && (!entity.IsStatic || resetsMotionVectors);
            // A range that moved — this group resized, or another one grew and compacted the page —
            // changes only what the record names, so it is published without touching the
            // acceleration structure.
            bool rangeMoved = hasPool && (brickBaseWords != publishedBrickBase || brickPage != publishedPage);
            bool republishesRecord = rangeMoved && hasRenderable;

            if (rebuildsInstance || retracksInstance || republishesRecord)
            {
                if (brickBaseWords >= 0 && brickPage >= 0)
                {
                    // The shader reads the previous transform, the brick pool page and the hash seed
                    // from the instance table, keyed by the RTAS instance ID. The slot is claimed
                    // once and kept for the renderer's whole life, because the ID has to survive a
                    // remove + add.
                    if (instanceSlot < 0)
                    {
                        instanceSlot = table.Allocate();
                    }

                    table.Set(instanceSlot, prevObjectToWorld, brickBaseWords, brickPage, (uint)groupHashSeed);
                    publishedBrickBase = brickBaseWords;
                    publishedPage = brickPage;
                }
            }

            if (rebuildsInstance)
            {
                EnsureAABBConfig();

                if (hasRenderable)
                {
                    ledger?.Release(groupASHandle, this);
                    AS.RemoveInstance(groupASHandle);
                }

                groupASHandle = AS.AddInstance(AABBconfig, objectToWorld, (uint)instanceSlot);
                ledger?.Claim(groupASHandle, this);
                hasRenderable = true;
            }
            else if (retracksInstance)
            {
                AS.UpdateInstanceTransform(groupASHandle, objectToWorld);
            }

            // Recorded unconditionally: this is simply where the entity was on the last renderer
            // tick, which is what the next tick needs as its previous transform.
            previousObjectToWorld = objectToWorld;
            hasPreviousObjectToWorld = true;
            isDirty = false;

            ReleaseStaleAabbBuffer(ref AS, rebuildsInstance);
            return rebuildsInstance;
        }

        /// <summary>
        /// Disposes the buffer the previous instance referenced, once the instance no longer does.
        /// </summary>
        /// <param name="AS">The acceleration structure holding the instance.</param>
        /// <param name="rebuiltThisTick">True when the instance was removed and re-added on the new buffer.</param>
        private void ReleaseStaleAabbBuffer(ref RayTracingAccelerationStructure AS, bool rebuiltThisTick)
        {
            if (staleAabbBuffer == null)
            {
                return;
            }

            if (hasRenderable && !rebuiltThisTick)
            {
                // No pool room this tick, so the instance was not re-added and still references the
                // stale buffer. Disposing it would purge the instance silently and leave a handle
                // that a later RemoveInstance would use against whoever inherits it. Remove it
                // explicitly instead; isDirty keeps the re-add pending for when there is room.
                ledger?.Release(groupASHandle, this);
                AS.RemoveInstance(groupASHandle);
                hasRenderable = false;
                isDirty = true;
            }

            staleAabbBuffer.Dispose();
            staleAabbBuffer = null;
        }

        /// <summary>Builds the AABB instance config if it was invalidated (or never built).</summary>
        private void EnsureAABBConfig()
        {
            if (AABBconfig.aabbCount != 0)
            {
                return;
            }

            AABBconfig = new RayTracingAABBsInstanceConfig(
                aabbBuffer, BrickBufferSize, false, groupMaterial)
            {
                dynamicGeometry = false,
                accelerationStructureBuildFlagsOverride = true,
                accelerationStructureBuildFlags = RayTracingAccelerationStructureBuildFlags.PreferFastTrace,
            };
        }

        /// <summary>Marks this group for removal from the acceleration structure.</summary>
        public void MarkRemove()
        {
            shouldRemove = true;
        }

        /// <summary>Releases the RTAS instance, the instance slot and the pool range, then disposes.</summary>
        public void RemoveMe(
            ref RayTracingAccelerationStructure AS, CaelixRayQueryInstanceTable table, CaelixBrickPool pool)
        {
            if (!shouldRemove)
            {
                return;
            }

            if (hasRenderable)
            {
                ledger?.Release(groupASHandle, this);
                AS.RemoveInstance(groupASHandle);
                hasRenderable = false;
            }

            if (instanceSlot >= 0)
            {
                table.Free(instanceSlot);
                instanceSlot = -1;
            }

            if (poolHandle != null)
            {
                pool.Free(poolHandle);
                poolHandle = null;
            }

            isDirty = false;
            Dispose();
        }

        /// <summary>Disposes host buffers, the brick map and the AABB buffer.</summary>
        public void Dispose()
        {
            // A job may still be in flight when a view despawns mid-frame; its syncRecord and its
            // staging lists are TempJob allocations this renderer owns.
            if (jobScheduled)
            {
                jobHandle.Complete();
                if (rendererJob.syncRecord.IsCreated)
                {
                    rendererJob.syncRecord.Dispose();
                }

                jobScheduled = false;
            }

            DisposeStaging();

            if (hostAABBBuffer.IsCreated) hostAABBBuffer.Dispose();
            if (rendererBrickMap.IsCreated) rendererBrickMap.Dispose();

            aabbBuffer?.Dispose();
            aabbBuffer = null;
            staleAabbBuffer?.Dispose();
            staleAabbBuffer = null;
        }
    }
}
