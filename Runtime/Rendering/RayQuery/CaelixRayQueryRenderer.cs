using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Caelix.Client;

namespace Caelix.Rendering.RayQuery
{
    /// <summary>
    /// Scene renderer for the inline ray query backend: owns the acceleration structure, the shared
    /// brick pool and the instance table that <c>CaelixPathTraceRQ.compute</c> reads.
    /// </summary>
    /// <remarks>
    /// Per-instance data is published through GPU buffers rather than a shader table, because a
    /// ray query has no hit group to carry it.
    /// <para>
    /// Work comes from the client store's per-cycle change list, and an instance is a
    /// <see cref="RenderGroup"/> rather than a sector; nothing here knows how storage groups
    /// bricks.
    /// </para>
    /// <para>
    /// Enable exactly ONE scene renderer per client world.
    /// <see cref="EntityView.ShouldResetMotionVectors"/> is a flag its consumer clears, so two
    /// renderers reading the same client world would steal each other's reset.
    /// </para>
    /// <para>
    /// A renderer bound after its world already exists still draws it: <see cref="SetSource"/>
    /// raises a full-upload flag, and the next <see cref="Tick"/> builds its work from every
    /// allocated brick instead of from the cycle's changes. That is what removed the old
    /// "a renderer enabled mid-Play draws nothing" limitation.
    /// </para>
    /// </remarks>
    public class CaelixRayQueryRenderer : MonoBehaviour
    {
        /// <summary>The host whose client world this renderer draws. Found in the scene when empty.</summary>
        [SerializeField] private CaelixHost host;

        /// <summary>
        /// Material handed to every group's <see cref="RayTracingAABBsInstanceConfig"/>. The ray
        /// query path never runs its hit group, but the config requires a material.
        /// </summary>
        public Material brickMat;

        /// <summary>When enabled, automatically calls <see cref="Tick"/> every frame.</summary>
        [SerializeField] private bool autoTick = false;

        [Header("Brick Pool")]
        [SerializeField, Tooltip("Upper size of one brick pool page, in bricks (1096 bytes each). Clamped to the platform's maximum buffer size. Lower it only to test paging.")]
        private int pageCapacityLimitBricks = CaelixBrickPool.DefaultPageCapacityLimitBricks;

        [Header("Render Groups")]
        [SerializeField, Tooltip("Bricks per acceleration-structure instance. Changing it while running re-uploads the world.")]
        private RenderGroupSize groupSize = RenderGroupSize.Bricks16;

        /// <summary>Debug field showing the current number of instances in the acceleration structure.</summary>
        [Header("Debug Utils")] public int instanceCount;

        /// <summary>Debug field showing how many pages the pool has open.</summary>
        public int poolPages;

        /// <summary>Debug field showing how many bricks are reserved by a live group range.</summary>
        public int poolLiveBricks;

        /// <summary>Debug field showing how many bricks the pool's page buffers can hold together.</summary>
        public int poolCapacityBricks;

        /// <summary>Debug field: live group renderers over every view.</summary>
        public int groupCount;

        /// <summary>Debug field: group render jobs this tick scheduled.</summary>
        public int groupsEmittedThisTick;

        /// <summary>Debug field: brick records this tick staged into the pool.</summary>
        public int bricksStagedThisTick;

        /// <summary>Debug field: groups whose AABB buffer this tick replaced.</summary>
        public int aabbReallocsThisTick;

        /// <summary>Debug field: acceleration-structure instances this tick removed and re-added.</summary>
        public int instanceRebuildsThisTick;

        /// <summary>Debug field: bytes the brick pool's page buffers occupy.</summary>
        public long poolVramBytes;

        /// <summary>Debug field: bytes every group's AABB buffer occupies together.</summary>
        public long aabbVramBytes;

        private ClientWorld source;
        private bool warnedTooManyPages;

        /// <summary>The grouping every live group renderer was built with. Follows <see cref="groupSize"/>.</summary>
        private RenderGroup grouping = RenderGroup.Default;

        /// <summary>
        /// Set while the renderer still has to upload a world it did not watch being built: after
        /// binding a source, and after its resources were released. The next <see cref="Tick"/>
        /// builds every view's work from <c>EnumerateBricks</c> rather than from the cycle's
        /// changes, then clears it.
        /// </summary>
        private bool needsInitialUpload;

        /// <summary>Maps view → render group → renderer, so render state stays separate from entity data.</summary>
        private readonly Dictionary<EntityView, Dictionary<int3, RayQueryGroupRenderer>> groups = new();

        /// <summary>Groups the current pass has emitted a bucketed job for. Cleared per view.</summary>
        private readonly HashSet<int3> bucketedGroups = new();

        /// <summary>Handle bookkeeping shared by every group renderer of this scene renderer.</summary>
        private readonly InstanceHandleLedger ledger = new();

        /// <summary>Frame of the last <see cref="Tick"/>, to flag a renderer ticked twice in one frame.</summary>
        private int lastTickFrame = -1;

        /// <summary>Groups this tick decided to drop, collected while their dictionary is being read.</summary>
        private readonly List<(EntityView view, int3 groupKey)> groupRemovalScratch = new();

        /// <summary>This tick's bucketed change lists, one per view that had work. Disposed after pass 2a.</summary>
        private readonly List<(EntityView view, ChangeBuckets buckets)> frameBuckets = new();

        private RayTracingAccelerationStructure _voxelScene;

        /// <summary>
        /// The acceleration structure containing all voxel geometry. Created on first access.
        /// </summary>
        public RayTracingAccelerationStructure voxelScene
        {
            get
            {
                if (_voxelScene == null) ReloadAS();
                return _voxelScene;
            }
        }

        /// <summary>The shared brick records, in up to four pages bound as <c>g_bricks0..3</c>.</summary>
        public CaelixBrickPool Pool { get; private set; }

        /// <summary>The per-instance record buffer bound as <c>g_Instances</c>.</summary>
        public CaelixRayQueryInstanceTable Instances { get; private set; }

        /// <summary>Entries in <see cref="MaterialTable"/>: one per 16-bit block ID.</summary>
        public const int MaterialTableEntries = 65536;

        /// <summary>Byte size of the HLSL <c>VoxelMaterial</c> struct: albedo, emission, smoothness, metallic, IOR, extinction.</summary>
        public const int MaterialStride = 40;

        /// <summary>
        /// One <c>VoxelMaterial</c> per 16-bit block ID, bound as <c>g_Materials</c>. The trace
        /// kernel cannot carry the static HLSL material tables (D3D12 refuses the pipeline state),
        /// so the G-buffer stage bakes them into this buffer once, then reads from it.
        /// </summary>
        public GraphicsBuffer MaterialTable { get; private set; }

        /// <summary>False until the G-buffer stage has recorded the bake into <see cref="MaterialTable"/>.</summary>
        public bool MaterialsBaked { get; set; }

        /// <summary>Current frame ID for rendering. Incremented each <see cref="Tick"/>.</summary>
        public uint frameId { get; private set; }

        /// <summary>The client world this renderer draws, once resolved.</summary>
        public ClientWorld Source => source;

        /// <summary>The grouping in force. Only changes at the top of a <see cref="Tick"/>.</summary>
        public RenderGroup Grouping => grouping;

        /// <summary>
        /// The group shape preset. A new value is applied on the next <see cref="Tick"/>, which
        /// drops every group and uploads the world again.
        /// </summary>
        public RenderGroupSize GroupSize { get => groupSize; set => groupSize = value; }

        /// <summary>
        /// True while every GPU resource the trace needs exists: between Awake/Tick and OnDisable. False
        /// in edit mode (no Awake) and while disabled, which is what keeps the G-buffer stage from
        /// dispatching against null buffers and logging "Property ... is not set" every frame.
        /// </summary>
        public bool HasResources => isActiveAndEnabled && Pool != null && Instances != null
            && MaterialTable != null && _voxelScene != null;

        private void Awake()
        {
            if (!SystemInfo.supportsInlineRayTracing)
            {
                Debug.LogWarning(
                    "Caelix: this device reports no inline ray tracing support. " +
                    "The Caelix trace kernels cannot run on it.", this);
            }

            Pool ??= new CaelixBrickPool(4096, pageCapacityLimitBricks);
            Instances ??= new CaelixRayQueryInstanceTable();
            EnsureMaterialTable();
            ReloadAS();
        }

        /// <summary>Creates the acceleration structure for voxel rendering.</summary>
        private void CreateRayTracingAccelerationStructure()
        {
            if (_voxelScene == null)
            {
                RayTracingAccelerationStructure.Settings settings = new RayTracingAccelerationStructure.Settings();
                settings.rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything;
                settings.managementMode = RayTracingAccelerationStructure.ManagementMode.Manual;
                settings.layerMask = -1;
                _voxelScene = new RayTracingAccelerationStructure(settings);
                Debug.Log($"voxAS (ray query): {_voxelScene}");
            }
        }

        /// <summary>
        /// Reinitializes the acceleration structure and resets the frame counter.
        /// Can be called from the context menu in the Unity Editor.
        /// </summary>
        [ContextMenu("Re-init voxAS")]
        public void ReloadAS()
        {
            CreateRayTracingAccelerationStructure();
            frameId = 0;
        }

        /// <summary>Binds to the host's client world. Safe to call every frame.</summary>
        private bool EnsureSource()
        {
            if (source != null && !source.IsDisposed)
            {
                return true;
            }

            if (host == null)
            {
                host = CaelixHost.Current;
            }

            if (host == null)
            {
                return false;
            }

            host.EnsureInitialized();
            SetSource(host.ClientWorld);
            return source != null;
        }

        /// <summary>Binds a replica and releases every group belonging to the previous one.</summary>
        /// <remarks>
        /// The new world is uploaded in full on the next <see cref="Tick"/>: its bricks were
        /// replicated before this renderer was looking, so its change list says nothing about them.
        /// </remarks>
        public void SetSource(ClientWorld world)
        {
            if (ReferenceEquals(source, world))
            {
                return;
            }

            if (source != null)
            {
                source.ViewDespawning -= OnViewDespawning;
                source.Owner.WorldRemoving -= OnWorldRemoving;
            }

            foreach (var viewGroups in groups)
            {
                foreach (var kvp in viewGroups.Value)
                {
                    kvp.Value.MarkRemove();
                    if (_voxelScene != null && Instances != null && Pool != null)
                    {
                        kvp.Value.RemoveMe(ref _voxelScene, Instances, Pool);
                    }
                    else
                    {
                        kvp.Value.Dispose();
                    }
                }

                viewGroups.Value.Clear();
            }

            groups.Clear();

            source = world != null && !world.IsDisposed ? world : null;
            if (source != null)
            {
                source.ViewDespawning += OnViewDespawning;
                source.Owner.WorldRemoving += OnWorldRemoving;
                needsInitialUpload = true;
            }
        }

        private void OnWorldRemoving(ClientWorld world)
        {
            if (ReferenceEquals(source, world))
            {
                SetSource(null);
            }
        }

        private void OnViewDespawning(EntityView view)
        {
            if (!groups.TryGetValue(view, out Dictionary<int3, RayQueryGroupRenderer> viewGroups))
            {
                return;
            }

            foreach (var kvp in viewGroups)
            {
                kvp.Value.MarkRemove();
                kvp.Value.RemoveMe(ref _voxelScene, Instances, Pool);
            }

            viewGroups.Clear();
            groups.Remove(view);
        }

        /// <summary>A re-enabled renderer starts from nothing, so it uploads the world in full.</summary>
        private void OnEnable()
        {
            needsInitialUpload = true;
        }

        private void Update()
        {
            if (autoTick) { Tick(); }
        }

        /// <summary>
        /// Performs one render update tick for all voxel entity views.
        /// </summary>
        /// <remarks>
        /// Pass 1 buckets each view's changes by render group and emits one job per group. Pass 2a
        /// consumes the finished jobs, settles every group's pool range and drops the groups that
        /// render nothing. Pass 2b writes bricks and updates the acceleration structure. 2a and 2b
        /// are separate loops on purpose: 2a can grow a page, which replaces its buffer and moves
        /// every range on it, so no group may write its records before every group has settled its
        /// range.
        /// </remarks>
        public void Tick()
        {
            groupCount = 0;
            groupsEmittedThisTick = 0;
            bricksStagedThisTick = 0;
            aabbReallocsThisTick = 0;
            instanceRebuildsThisTick = 0;
            poolVramBytes = 0;
            aabbVramBytes = 0;

            // The grouping sizes every group renderer's brick map, AABB buffer and pool range, so a
            // new preset is applied by dropping them all. ReleaseResources unbinds the source and
            // raises needsInitialUpload; the lines below rebind it and recreate the pool, the
            // instance table and the acceleration structure.
            RenderGroup wanted = RenderGroupPresets.Of(groupSize);
            if (!wanted.Equals(grouping))
            {
                ReleaseResources();
                grouping = wanted;
            }

            if (!EnsureSource())
            {
                return;
            }

            if (_voxelScene == null)
            {
                ReloadAS();
            }

            Pool ??= new CaelixBrickPool(4096, pageCapacityLimitBricks);
            Instances ??= new CaelixRayQueryInstanceTable();
            EnsureMaterialTable();

            if (lastTickFrame == Time.frameCount)
            {
                Debug.LogWarning(
                    $"CaelixRayQueryRenderer: Tick ran twice in frame {Time.frameCount}; the change list is " +
                    "consumed once per frame, so the second run redoes every group's work.", this);
            }

            lastTickFrame = Time.frameCount;

            frameId += 1;
            instanceCount = (int)voxelScene.GetInstanceCount();
            JobHandle renderJobs = default;
            bool hasRenderJobs = false;

            IReadOnlyList<EntityView> views = source.Views;
            frameBuckets.Clear();

            // Pass 1: bucket this cycle's changes and emit one job per touched render group.
            for (int v = 0; v < views.Count; v++)
            {
                EntityView view = views[v];
                Dictionary<int3, RayQueryGroupRenderer> viewGroups = GetOrCreateViewGroups(view);

                ChangeBuckets buckets;
                if (needsInitialUpload)
                {
                    // Nothing told this renderer about the bricks that arrived before it was
                    // bound, so the work is synthesised from the storage itself.
                    NativeArray<BrickChange> initial = RenderGroupChanges.BuildFullUploadChanges(view.Data);
                    buckets = ChangeBuckets.Build(initial, grouping);
                    initial.Dispose();
                }
                else
                {
                    NativeArray<BrickChange>.ReadOnly changes = view.Data.Changes;
                    if (changes.Length == 0 && !AnyGroupNeedsFullRebuild(viewGroups))
                    {
                        continue;
                    }

                    buckets = ChangeBuckets.Build(changes, grouping);
                }

                frameBuckets.Add((view, buckets));
                NativeArray<BrickChange> sorted = buckets.Sorted.AsArray();

                bucketedGroups.Clear();
                for (int g = 0; g < buckets.GroupKeys.Length; g++)
                {
                    int3 groupKey = buckets.GroupKeys[g];
                    bucketedGroups.Add(groupKey);

                    if (!viewGroups.TryGetValue(groupKey, out RayQueryGroupRenderer renderer))
                    {
                        // A group nobody renders yet has nothing to retire, so a slice of removals
                        // alone does not warrant a renderer.
                        if (!RenderGroupChanges.SliceHasUpdate(sorted, buckets.GroupStarts[g], buckets.GroupCounts[g]))
                        {
                            continue;
                        }

                        renderer = new RayQueryGroupRenderer(view, groupKey, brickMat, ledger, grouping);
                        viewGroups[groupKey] = renderer;
                    }

                    renderer.RenderEmitJob(view.Data, sorted, buckets.GroupStarts[g], buckets.GroupCounts[g]);
                    CombineJob(renderer, ref renderJobs, ref hasRenderJobs);
                }

                // A group whose records the pool dropped has to regenerate everything, whether or
                // not this cycle's changes name it.
                foreach (var kvp in viewGroups)
                {
                    if (!kvp.Value.NeedsFullRebuild || bucketedGroups.Contains(kvp.Key))
                    {
                        continue;
                    }

                    kvp.Value.RenderEmitJob(view.Data, sorted, 0, 0);
                    CombineJob(kvp.Value, ref renderJobs, ref hasRenderJobs);
                }
            }

            needsInitialUpload = false;

            if (hasRenderJobs)
            {
                renderJobs.Complete();
            }

            // Pass 2a: consume the finished jobs. May grow the pool.
            groupRemovalScratch.Clear();
            for (int v = 0; v < views.Count; v++)
            {
                EntityView view = views[v];
                if (!groups.TryGetValue(view, out Dictionary<int3, RayQueryGroupRenderer> viewGroups))
                {
                    continue;
                }

                foreach (var kvp in viewGroups)
                {
                    if (kvp.Value.ApplyCompletedRenderJob(Pool))
                    {
                        aabbReallocsThisTick++;
                    }

                    // A group that renders nothing owns an instance, a pool range and a brick map
                    // for no geometry. Dropping it here is what replaces the old sector-removal
                    // subscription; it is recreated as soon as one of its bricks changes again.
                    if (kvp.Value.RendererBrickCount == 0 && !kvp.Value.NeedsFullRebuild)
                    {
                        groupRemovalScratch.Add((view, kvp.Key));
                    }
                }
            }

            for (int i = 0; i < groupRemovalScratch.Count; i++)
            {
                (EntityView view, int3 groupKey) = groupRemovalScratch[i];
                if (!groups.TryGetValue(view, out Dictionary<int3, RayQueryGroupRenderer> viewGroups)) continue;
                if (!viewGroups.TryGetValue(groupKey, out RayQueryGroupRenderer stale)) continue;

                stale.MarkRemove();
                stale.RemoveMe(ref _voxelScene, Instances, Pool);
                viewGroups.Remove(groupKey);
            }

            // The group jobs read the sorted arrays, and pass 2a is the last point that could still
            // touch a job, so the bucket outputs go back here.
            for (int i = 0; i < frameBuckets.Count; i++)
            {
                frameBuckets[i].buckets.Dispose();
            }

            frameBuckets.Clear();

            // Pass 2b: upload bricks against the final pool, then update the acceleration structure.
            for (int v = 0; v < views.Count; v++)
            {
                EntityView view = views[v];
                if (groups.TryGetValue(view, out Dictionary<int3, RayQueryGroupRenderer> viewGroups))
                {
                    foreach (var kvp in viewGroups)
                    {
                        bricksStagedThisTick += kvp.Value.UploadBricks(Pool);
                        if (kvp.Value.RenderModifyAS(ref _voxelScene, view, kvp.Key, Instances))
                        {
                            instanceRebuildsThisTick++;
                        }

                        groupCount++;
                        aabbVramBytes += kvp.Value.AabbVramBytes;
                    }
                }

                // Every group of this view has consumed the reset; its motion vectors are settled.
                view.ShouldResetMotionVectors = false;
            }

            // One batched write of every record staged above. Per-group dispatches would ask the
            // driver for one staging copy of the buffer each — see CaelixBrickGpuOps.
            Pool.Ops.FlushScatter();

            Instances.Flush();

            poolPages = Pool.PageCount;
            if (Pool.PageCount > CaelixBrickPool.MaxNamedPages && !warnedTooManyPages)
            {
                // The kernel switches over MaxNamedPages named buffers; anything past that is invisible to it.
                warnedTooManyPages = true;
                Debug.LogError(
                    $"CaelixRayQueryRenderer: the brick pool opened {Pool.PageCount} pages but the kernel " +
                    $"addresses only {CaelixBrickPool.MaxNamedPages}. Raise pageCapacityLimitBricks.", this);
            }

            poolLiveBricks = Pool.TotalLiveBricks;
            poolCapacityBricks = Pool.TotalCapacityBricks;
            poolVramBytes = (long)Pool.VRAMUsage;
        }

        private Dictionary<int3, RayQueryGroupRenderer> GetOrCreateViewGroups(EntityView view)
        {
            if (!groups.TryGetValue(view, out Dictionary<int3, RayQueryGroupRenderer> viewGroups))
            {
                viewGroups = new Dictionary<int3, RayQueryGroupRenderer>();
                groups[view] = viewGroups;
            }

            return viewGroups;
        }

        private static bool AnyGroupNeedsFullRebuild(Dictionary<int3, RayQueryGroupRenderer> viewGroups)
        {
            foreach (var kvp in viewGroups)
            {
                if (kvp.Value.NeedsFullRebuild) return true;
            }

            return false;
        }

        private void CombineJob(
            RayQueryGroupRenderer renderer, ref JobHandle renderJobs, ref bool hasRenderJobs)
        {
            if (!renderer.TryGetScheduledJobHandle(out JobHandle groupJob))
            {
                return;
            }

            groupsEmittedThisTick++;
            renderJobs = JobHandle.CombineDependencies(renderJobs, groupJob);
            hasRenderJobs = true;
        }

        private void EnsureMaterialTable()
        {
            if (MaterialTable != null && MaterialTable.IsValid())
            {
                return;
            }

            MaterialTable = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, MaterialTableEntries, MaterialStride);
            // A fresh buffer holds garbage until the G-buffer stage bakes into it.
            MaterialsBaked = false;
        }

        /// <summary>Releases all resources when the renderer is disabled.</summary>
        private void OnDisable()
        {
            ReleaseResources();
        }

        /// <summary>Releases all GPU resources.</summary>
        private void ReleaseResources()
        {
            // Unbinds the events and retires every group while the pool and the table are still
            // alive; whatever the renderer draws next has to be uploaded in full.
            SetSource(null);
            needsInitialUpload = true;

            for (int i = 0; i < frameBuckets.Count; i++)
            {
                frameBuckets[i].buckets.Dispose();
            }

            frameBuckets.Clear();

            foreach (var viewGroups in groups)
            {
                foreach (var kvp in viewGroups.Value)
                {
                    kvp.Value.Dispose();
                }
            }

            groups.Clear();

            _voxelScene?.Dispose();
            _voxelScene = null;

            Instances?.Dispose();
            Instances = null;

            MaterialTable?.Dispose();
            MaterialTable = null;
            MaterialsBaked = false;

            // Nothing may be left staged when the batch's buffers go away.
            Pool?.Ops?.FlushScatter();

            Pool?.Dispose();
            Pool = null;
        }
    }
}
