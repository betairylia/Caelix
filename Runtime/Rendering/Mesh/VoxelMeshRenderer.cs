using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Caelix.Client;

namespace Caelix.Rendering.Meshing
{
    /// <summary>
    /// Main coordinator for mesh-based voxel rendering.
    /// Tracks the <see cref="EntityView"/>s of a <see cref="ClientWorld"/> and manages one
    /// <see cref="GroupMeshRenderer"/> per render group.
    /// </summary>
    /// <remarks>
    /// Work comes from the client store's per-cycle change list, bucketed by render group exactly
    /// as the ray query renderer buckets it. Nothing here knows how storage groups bricks: a group
    /// that holds no allocated brick after its jobs ran is dropped, which is what replaces the old
    /// sector-map sweep and its attachment-identity check.
    /// </remarks>
    public class VoxelMeshRenderer : IDisposable
    {
        // Settings
        private readonly int chunkSize;
        private readonly Material material;

        // View tracking
        private readonly HashSet<EntityView> trackedViews = new HashSet<EntityView>();
        private readonly Dictionary<(EntityView, int3), GroupMeshRenderer> groupRenderers =
            new Dictionary<(EntityView, int3), GroupMeshRenderer>();

        // Cached lists to avoid allocations
        private readonly List<(EntityView, int3)> groupsToRemove = new List<(EntityView, int3)>();
        private readonly List<EntityView> viewsToRemove = new List<EntityView>();
        private readonly HashSet<EntityView> currentViews = new HashSet<EntityView>();

        // Groups whose slice carried a Removed change this frame. Only those can have lost their
        // last brick, so only those are asked whether they are empty; asking every group would
        // walk every region's brick index each frame (milestone 3 review).
        private readonly HashSet<(EntityView, int3)> groupsWithRemovals = new HashSet<(EntityView, int3)>();

        /// <summary>
        /// Views whose work must be synthesised from every allocated brick instead of from this
        /// cycle's changes: a view this renderer did not watch being built, or one whose meshes
        /// were dropped.
        /// </summary>
        private readonly HashSet<EntityView> pendingFullUpload = new HashSet<EntityView>();

        /// <summary>This Update's bucketed change lists, one per view that had work. Disposed at the end.</summary>
        private readonly List<ChangeBuckets> frameBuckets = new List<ChangeBuckets>();

        private bool regenerateAllPending;

        /// <summary>The client world whose views are rendered. Nothing renders until it is set.</summary>
        private ClientWorld source;
        public ClientWorld Source
        {
            get => source;
            set
            {
                if (ReferenceEquals(source, value)) return;
                if (source != null)
                {
                    source.ViewDespawning -= RemoveView;
                    source.Owner.WorldRemoving -= OnWorldRemoving;
                }
                ReleaseRenderers();
                source = value != null && !value.IsDisposed ? value : null;
                if (source != null)
                {
                    // No sector-removal subscription: a group whose bricks are gone is dropped at the
                    // end of Update, after its jobs completed, and every brick pointer is bound and
                    // consumed inside one Update. So no job can touch freed storage.
                    source.ViewDespawning += RemoveView;
                    source.Owner.WorldRemoving += OnWorldRemoving;
                }
            }
        }

        private void OnWorldRemoving(ClientWorld world)
        {
            if (ReferenceEquals(source, world)) Source = null;
        }

        /// <summary>
        /// Gets the number of currently tracked views.
        /// </summary>
        public int TrackedEntityCount => trackedViews.Count;

        /// <summary>
        /// Gets the total number of render group renderers.
        /// </summary>
        public int GroupRendererCount => groupRenderers.Count;

        public VoxelMeshRenderer(int chunkSize, Material material)
        {
            this.chunkSize = chunkSize;
            this.material = material;
        }

        /// <summary>
        /// Updates all mesh renderers. Called once per frame.
        /// Implements two-phase update: schedule jobs, then complete and apply.
        /// </summary>
        public void Update()
        {
            if (Source == null)
            {
                return;
            }

            // Discover and track new views
            DiscoverViews();

            if (regenerateAllPending)
            {
                regenerateAllPending = false;
                foreach (EntityView view in trackedViews)
                {
                    pendingFullUpload.Add(view);
                }
            }

            // Phase 1: bucket each view's changes by render group and schedule one job per dirty chunk
            JobHandle meshJobs = default;
            bool hasMeshJobs = false;
            frameBuckets.Clear();
            groupsWithRemovals.Clear();

            foreach (EntityView view in trackedViews)
            {
                bool fullUpload = pendingFullUpload.Contains(view);
                ChangeBuckets buckets;
                if (fullUpload)
                {
                    NativeArray<BrickChange> initial = RenderGroupChanges.BuildFullUploadChanges(view.Data);
                    buckets = ChangeBuckets.Build(initial);
                    initial.Dispose();
                }
                else
                {
                    NativeArray<BrickChange>.ReadOnly changes = view.Data.Changes;
                    if (changes.Length == 0)
                    {
                        continue;
                    }

                    buckets = ChangeBuckets.Build(changes);
                }

                frameBuckets.Add(buckets);
                NativeArray<BrickChange> sorted = buckets.Sorted.AsArray();

                for (int g = 0; g < buckets.GroupKeys.Length; g++)
                {
                    int3 groupKey = buckets.GroupKeys[g];
                    int start = buckets.GroupStarts[g];
                    int count = buckets.GroupCounts[g];
                    var key = (view, groupKey);
                    if (SliceHasRemoval(sorted, start, count))
                    {
                        groupsWithRemovals.Add(key);
                    }

                    if (!groupRenderers.TryGetValue(key, out GroupMeshRenderer renderer))
                    {
                        // A group nobody meshes yet has nothing to retire, so a slice of removals
                        // alone does not warrant a renderer.
                        if (!RenderGroupChanges.SliceHasUpdate(sorted, start, count))
                        {
                            continue;
                        }

                        renderer = new GroupMeshRenderer(groupKey, chunkSize, material, view.Transform);
                        groupRenderers[key] = renderer;
                    }

                    renderer.ScheduleJobs(view.Data, sorted, start, count);
                    if (renderer.TryGetScheduledJobHandle(out JobHandle groupJobs))
                    {
                        meshJobs = JobHandle.CombineDependencies(meshJobs, groupJobs);
                        hasMeshJobs = true;
                    }
                }
            }

            pendingFullUpload.Clear();

            if (hasMeshJobs)
            {
                meshJobs.Complete();
            }

            // Phase 2: Apply meshes after the combined barrier
            foreach (var kvp in groupRenderers)
            {
                kvp.Value.ApplyCompletedJobs();
            }

            // A group that holds no brick any more draws nothing; dropping it here replaces the old
            // sector-map sweep. A region freed and recreated at the same coordinate inside one frame
            // arrives as Removed plus Updated entries, so its group is re-meshed rather than leaked.
            // TODO (low priority, mesh fallback): removing a group's last brick then calling
            // RegenerateAll() before Update() leaves its old mesh alive. Full upload emits only
            // allocated bricks, so groupsWithRemovals misses the emptied group. Preserve removals
            // during full upload or check existing groups once on regeneration.
            groupsToRemove.Clear();
            foreach (var kvp in groupRenderers)
            {
                EntityView view = kvp.Key.Item1;
                if (!trackedViews.Contains(view) ||
                    (groupsWithRemovals.Contains(kvp.Key) && kvp.Value.IsEmpty(view.Data)))
                {
                    groupsToRemove.Add(kvp.Key);
                }
            }

            for (int i = 0; i < groupsToRemove.Count; i++)
            {
                RemoveGroupRenderer(groupsToRemove[i].Item1, groupsToRemove[i].Item2);
            }

            for (int i = 0; i < frameBuckets.Count; i++)
            {
                frameBuckets[i].Dispose();
            }

            frameBuckets.Clear();

            // requireUpdate cleanup is owned by the client frame after consumers have read it.
        }

        /// <summary>
        /// Tracks the views of the source world and drops the ones that despawned.
        /// </summary>
        private static bool SliceHasRemoval(NativeArray<BrickChange> sorted, int start, int count)
        {
            for (int i = start; i < start + count; i++)
            {
                if (sorted[i].Kind == ChangeKind.Removed) return true;
            }

            return false;
        }

        private void DiscoverViews()
        {
            currentViews.Clear();
            IReadOnlyList<EntityView> views = Source.Views;
            for (int i = 0; i < views.Count; i++)
            {
                EntityView view = views[i];
                if (view.Transform == null)
                {
                    continue; // no scene object to parent meshes under yet
                }

                currentViews.Add(view);
                if (!trackedViews.Contains(view))
                {
                    AddView(view);
                }
            }

            viewsToRemove.Clear();
            foreach (EntityView view in trackedViews)
            {
                if (!currentViews.Contains(view))
                {
                    viewsToRemove.Add(view);
                }
            }

            for (int i = 0; i < viewsToRemove.Count; i++)
            {
                RemoveView(viewsToRemove[i]);
            }
        }

        private void AddView(EntityView view)
        {
            trackedViews.Add(view);

            // Its bricks arrived before this renderer was looking, so the change list says nothing
            // about them; the first Update synthesises the work from storage instead.
            pendingFullUpload.Add(view);
        }

        private void RemoveView(EntityView view)
        {
            trackedViews.Remove(view);
            pendingFullUpload.Remove(view);

            // Remove all group renderers for this view
            groupsToRemove.Clear();
            foreach (var kvp in groupRenderers)
            {
                if (kvp.Key.Item1 == view)
                {
                    groupsToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in groupsToRemove)
            {
                RemoveGroupRenderer(key.Item1, key.Item2);
            }

            groupsToRemove.Clear();
        }

        private void RemoveGroupRenderer(EntityView view, int3 groupKey)
        {
            var key = (view, groupKey);
            if (groupRenderers.TryGetValue(key, out var renderer))
            {
                renderer.Dispose();
                groupRenderers.Remove(key);
            }
        }

        /// <summary>
        /// Forces regeneration of all meshes.
        /// </summary>
        public void RegenerateAll()
        {
            regenerateAllPending = true;
            foreach (var kvp in groupRenderers)
            {
                kvp.Value.MarkAllDirty();
            }
        }

        /// <summary>
        /// Cleanup all resources.
        /// </summary>
        public void Dispose()
        {
            Source = null;
            ReleaseRenderers();
        }

        private void ReleaseRenderers()
        {
            for (int i = 0; i < frameBuckets.Count; i++)
            {
                frameBuckets[i].Dispose();
            }

            frameBuckets.Clear();

            foreach (var kvp in groupRenderers)
            {
                kvp.Value.Dispose();
            }

            groupRenderers.Clear();
            trackedViews.Clear();
            pendingFullUpload.Clear();
        }
    }
}
