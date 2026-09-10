using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Caelix.Rendering.RayQuery;

namespace Caelix.Rendering
{
    /// <summary>
    /// Reorders one entity's change list so that the entries of a render group are contiguous, and
    /// reports where each group's slice starts and how long it is.
    /// </summary>
    /// <remarks>
    /// A counting pass, a prefix sum and a scatter pass, so a group's entries keep their original
    /// relative order. That matters: within one group a removal must still be seen before an
    /// addition that reuses its slot. The renderer hands each group renderer its own slice, and
    /// several of those jobs then read this one array at once.
    /// </remarks>
    [BurstCompile]
    internal struct BucketChangesJob : IJob
    {
        /// <summary>The cycle's changes, in publication order.</summary>
        [ReadOnly] public NativeArray<BrickChange> changes;

        /// <summary>How many entries of <see cref="changes"/> are real; the array itself may be padded.</summary>
        public int changeCount;

        /// <summary>Output: every entry, reordered by group.</summary>
        public NativeList<BrickChange> sorted;

        /// <summary>Output: one key per group that has at least one entry, in first-seen order.</summary>
        public NativeList<int3> groupKeys;

        /// <summary>Output: index into <see cref="sorted"/> of each group's first entry.</summary>
        public NativeList<int> groupStarts;

        /// <summary>Output: number of entries of each group.</summary>
        public NativeList<int> groupCounts;

        /// <summary>The group shape that decides which group a brick key falls in.</summary>
        public RenderGroup grouping;

        public void Execute()
        {
            if (changeCount <= 0)
            {
                return;
            }

            var groupIndex = new NativeHashMap<int3, int>(changeCount, Allocator.Temp);

            for (int i = 0; i < changeCount; i++)
            {
                int3 group = grouping.Of(changes[i].Key);
                if (groupIndex.TryGetValue(group, out int slot))
                {
                    groupCounts[slot] = groupCounts[slot] + 1;
                }
                else
                {
                    groupIndex.Add(group, groupKeys.Length);
                    groupKeys.Add(group);
                    groupCounts.Add(1);
                }
            }

            groupStarts.Resize(groupKeys.Length, NativeArrayOptions.UninitializedMemory);
            var cursor = new NativeArray<int>(groupKeys.Length, Allocator.Temp);

            int running = 0;
            for (int g = 0; g < groupKeys.Length; g++)
            {
                groupStarts[g] = running;
                cursor[g] = running;
                running += groupCounts[g];
            }

            sorted.Resize(changeCount, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < changeCount; i++)
            {
                int slot = groupIndex[grouping.Of(changes[i].Key)];
                sorted[cursor[slot]] = changes[i];
                cursor[slot] = cursor[slot] + 1;
            }

            cursor.Dispose();
            groupIndex.Dispose();
        }
    }

    /// <summary>
    /// One view's change list, reordered so that each render group's entries are contiguous.
    /// </summary>
    /// <remarks>
    /// Shared by every renderer that consumes the change list by render group. Every list is
    /// allocated with capacity for at least one element, so that a view whose change list is
    /// empty — a group that only needs a full rebuild still has to be handed a valid array —
    /// produces containers a job can be scheduled against.
    /// </remarks>
    internal struct ChangeBuckets : IDisposable
    {
        /// <summary>Require-update bits that make a brick worth a render job.</summary>
        private const DirtyFlags RenderFlags =
            DirtyFlags.BlockBrickAdded | DirtyFlags.GeometryWithLocalNeighbor;

        public NativeArray<BrickChange> Source;
        public NativeList<BrickChange> Sorted;
        public NativeList<int3> GroupKeys;
        public NativeList<int> GroupStarts;
        public NativeList<int> GroupCounts;

        /// <summary>Buckets a plain array, for the work a full upload synthesises.</summary>
        public static ChangeBuckets Build(NativeArray<BrickChange> changes, RenderGroup grouping)
            => Build(changes.AsReadOnly(), grouping);

        public static ChangeBuckets Build(NativeArray<BrickChange>.ReadOnly changes, RenderGroup grouping)
        {
            int count = changes.Length;
            var buckets = new ChangeBuckets
            {
                Source = new NativeArray<BrickChange>(
                    math.max(1, count), Allocator.TempJob, NativeArrayOptions.UninitializedMemory),
                Sorted = new NativeList<BrickChange>(math.max(1, count), Allocator.TempJob),
                GroupKeys = new NativeList<int3>(math.max(1, count), Allocator.TempJob),
                GroupStarts = new NativeList<int>(math.max(1, count), Allocator.TempJob),
                GroupCounts = new NativeList<int>(math.max(1, count), Allocator.TempJob)
            };

            // Only entries the renderer acts on are bucketed: a removal, or a brick that
            // was added or whose geometry (own or neighbouring) changed. Entries that carry
            // only automata flags would otherwise create a group renderer just to drop it.
            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                BrickChange change = changes[i];
                if (change.Kind == ChangeKind.Removed || (change.RequiredFlags & RenderFlags) != 0)
                {
                    buckets.Source[kept++] = change;
                }
            }

            new BucketChangesJob
            {
                changes = buckets.Source,
                changeCount = kept,
                sorted = buckets.Sorted,
                groupKeys = buckets.GroupKeys,
                groupStarts = buckets.GroupStarts,
                groupCounts = buckets.GroupCounts,
                grouping = grouping
            }.Run();

            return buckets;
        }

        public void Dispose()
        {
            if (Source.IsCreated) Source.Dispose();
            if (Sorted.IsCreated) Sorted.Dispose();
            if (GroupKeys.IsCreated) GroupKeys.Dispose();
            if (GroupStarts.IsCreated) GroupStarts.Dispose();
            if (GroupCounts.IsCreated) GroupCounts.Dispose();
        }
    }

    /// <summary>Change-list helpers shared by the renderers that group bricks by render group.</summary>
    internal static class RenderGroupChanges
    {
        /// <summary>
        /// One <see cref="BrickChange"/> per allocated brick of an entity, as if every one of them
        /// had just been added. The caller owns the array.
        /// </summary>
        /// <remarks>
        /// The two require-update bits are what <see cref="ChangeBuckets"/> keeps and what a group
        /// job acts on: BlockBrickAdded claims a renderer brick id, GeometryWithLocalNeighbor
        /// rebuilds the record. Walked twice rather than grown, because the enumerator allocates
        /// nothing.
        /// </remarks>
        public static NativeArray<BrickChange> BuildFullUploadChanges(VoxelEntityData data)
        {
            int count = 0;
            foreach (int3 unused in data.EnumerateBricks())
            {
                count++;
            }

            var changes = new NativeArray<BrickChange>(
                count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            int i = 0;
            foreach (int3 key in data.EnumerateBricks())
            {
                changes[i++] = new BrickChange
                {
                    Key = key,
                    Kind = ChangeKind.Updated,
                    SourceFlags = DirtyFlags.None,
                    RequiredFlags = DirtyFlags.BlockBrickAdded | DirtyFlags.GeometryWithLocalNeighbor
                };
            }

            return changes;
        }

        /// <summary>True when a group's slice holds at least one <see cref="ChangeKind.Updated"/> entry.</summary>
        public static bool SliceHasUpdate(NativeArray<BrickChange> sorted, int start, int count)
        {
            for (int i = start; i < start + count; i++)
            {
                if (sorted[i].Kind == ChangeKind.Updated) return true;
            }

            return false;
        }
    }
}
