using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Caelix.Rendering;
using Caelix.Rendering.RayQuery;
using Caelix.Tests.TestSupport;

namespace Caelix.Tests
{
    /// <summary>
    /// A full rebuild of a render group must start from nothing: bricks whose storage went away
    /// while the group waited for pool room are absent from the enumeration and their removal
    /// records were consumed by earlier cycles, so the previous renderer state cannot be trusted.
    /// </summary>
    public unsafe class RenderGroupRebuildTests
    {
        private sealed class JobScope : System.IDisposable
        {
            public readonly RenderGroup Grouping;
            public SparseBrickIdTable Map;
            public NativeList<BrickRecordLayout.BrickAABB> Aabbs = new(0, Allocator.Persistent);

            public JobScope() : this(RenderGroup.Default)
            {
            }

            public JobScope(RenderGroup grouping)
            {
                Grouping = grouping;
                Map = SparseBrickIdTable.New(grouping.BricksInGroup, Allocator.Persistent);
            }

            public GenerateGroupRenderDataJob Run(
                VoxelEntityData data, NativeArray<BrickChange> changes, int count, bool fullUpload)
            {
                var job = new GenerateGroupRenderDataJob
                {
                    data = data,
                    changes = changes,
                    start = 0,
                    count = count,
                    groupKey = int3.zero,
                    grouping = Grouping,
                    forceFullUpload = fullUpload,
                    rendererBrickMap = Map,
                    aabbBuffer = Aabbs,
                    stagingWords = new NativeList<int>(BrickRecordLayout.BRICK_DATA_LENGTH * 4, Allocator.TempJob),
                    stagingSlots = new NativeList<int>(4, Allocator.TempJob),
                    syncRecord = new NativeArray<int>(1, Allocator.TempJob)
                };
                job.Run();
                return job;
            }

            public void Dispose()
            {
                if (Map.IsCreated) Map.Dispose();
                if (Aabbs.IsCreated) Aabbs.Dispose();
            }
        }

        private static void DisposeJob(GenerateGroupRenderDataJob job)
        {
            job.stagingWords.Dispose();
            job.stagingSlots.Dispose();
            job.syncRecord.Dispose();
        }

        private static NativeArray<BrickChange> CopyChanges(VoxelEntityData data)
        {
            NativeArray<BrickChange>.ReadOnly view = data.Changes;
            var copy = new NativeArray<BrickChange>(math.max(1, view.Length), Allocator.TempJob);
            for (int i = 0; i < view.Length; i++) copy[i] = view[i];
            return copy;
        }

        [Test]
        public void FullRebuild_AfterRegionRemoval_RetiresEveryStaleRendererBrick()
        {
            using var scope = new EntityDataTestScope();
            using var jobs = new JobScope();

            // Two bricks in group (0,0,0): brick (0,0,0) and brick (2,0,0).
            scope.Data.EnsureRegion(int3.zero);
            scope.Data.SetBlock(new int3(1, 1, 1), new Block(5));
            scope.Data.SetBlock(new int3(17, 1, 1), new Block(5));
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();
            scope.Data.BuildChangeList();

            NativeArray<BrickChange> changes = CopyChanges(scope.Data);
            GenerateGroupRenderDataJob first = jobs.Run(scope.Data, changes, scope.Data.ChangeCount, fullUpload: false);
            Assert.That(jobs.Map.Count, Is.EqualTo(2), "both bricks render before the removal");
            DisposeJob(first);
            changes.Dispose();

            // The storage goes away while (in the real renderer) the group waits for pool room, so
            // its Removed entries are cleared with that cycle and never reach a job.
            scope.Data.ClearChanges();
            scope.Data.ClearDirtyFlags();
            scope.Data.ClearRequireUpdates();
            scope.Data.RemoveRegion(int3.zero);
            scope.Data.ClearChanges();

            var empty = new NativeArray<BrickChange>(1, Allocator.TempJob);
            GenerateGroupRenderDataJob rebuild = jobs.Run(scope.Data, empty, 0, fullUpload: true);

            Assert.That(jobs.Map.Count, Is.EqualTo(0), "a full rebuild must not keep bricks whose storage is gone");
            Assert.That(rebuild.syncRecord[0], Is.EqualTo(1), "retired boxes change the AABB set");
            Assert.That(rebuild.stagingSlots.Length, Is.EqualTo(2), "one zeroed record per retired slot");
            for (int i = 0; i < 2; i++)
            {
                Assert.That(float.IsNaN(jobs.Aabbs[i].min.x), Is.True, $"slot {i} must be an inactive primitive");
            }

            DisposeJob(rebuild);
            empty.Dispose();
        }

        [Test]
        public void FullRebuild_ReaddsExactlyTheBricksThatExistNow()
        {
            using var scope = new EntityDataTestScope();
            using var jobs = new JobScope();

            scope.Data.EnsureRegion(int3.zero);
            scope.Data.SetBlock(new int3(1, 1, 1), new Block(5));
            scope.Data.SetBlock(new int3(17, 1, 1), new Block(5));
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();
            scope.Data.BuildChangeList();

            NativeArray<BrickChange> changes = CopyChanges(scope.Data);
            DisposeJob(jobs.Run(scope.Data, changes, scope.Data.ChangeCount, fullUpload: false));
            changes.Dispose();
            Assert.That(jobs.Map.Count, Is.EqualTo(2));

            // Replace the storage with a region holding one brick at a new position.
            scope.Data.ClearChanges();
            scope.Data.ClearDirtyFlags();
            scope.Data.ClearRequireUpdates();
            scope.Data.RemoveRegion(int3.zero);
            scope.Data.ClearChanges();
            scope.Data.EnsureRegion(int3.zero);
            scope.Data.SetBlock(new int3(33, 1, 1), new Block(5)); // brick (4,0,0)

            var empty = new NativeArray<BrickChange>(1, Allocator.TempJob);
            GenerateGroupRenderDataJob rebuild = jobs.Run(scope.Data, empty, 0, fullUpload: true);

            RenderGroup grouping = RenderGroup.Default;
            Assert.That(jobs.Map.Count, Is.EqualTo(1));
            Assert.That(jobs.Map.indices[grouping.LocalBrickIdx(new int3(4, 0, 0))], Is.Not.EqualTo(SparseBrickIdTable.EMPTY));
            Assert.That(jobs.Map.indices[grouping.LocalBrickIdx(new int3(0, 0, 0))], Is.EqualTo(SparseBrickIdTable.EMPTY));
            Assert.That(jobs.Map.indices[grouping.LocalBrickIdx(new int3(2, 0, 0))], Is.EqualTo(SparseBrickIdTable.EMPTY));

            DisposeJob(rebuild);
            empty.Dispose();
        }

        /// <summary>
        /// A group 128 bricks tall reaches a brick the default 16-brick group never could, and its
        /// slot index runs through the y shift instead of a cube's.
        /// </summary>
        [Test]
        public void FullUpload_TallGroup_PlacesABrickAtY800()
        {
            using var scope = new EntityDataTestScope();
            using var jobs = new JobScope(RenderGroupPresets.Of(RenderGroupSize.Bricks16x128x16));

            // Regions are 128 blocks per axis, so y 800 lies in region y index 6.
            scope.Data.EnsureRegion(new int3(0, 6, 0));
            scope.Data.SetBlock(new int3(1, 801, 1), new Block(5));
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();
            scope.Data.BuildChangeList();

            var empty = new NativeArray<BrickChange>(1, Allocator.TempJob);
            GenerateGroupRenderDataJob job = jobs.Run(scope.Data, empty, 0, fullUpload: true);

            Assert.That(jobs.Map.Count, Is.EqualTo(1));

            int slot = 100 << 4;
            Assert.That(slot, Is.EqualTo(jobs.Grouping.LocalBrickIdx(new int3(0, 100, 0))));

            short id = jobs.Map.indices[slot];
            Assert.That(id, Is.Not.EqualTo(SparseBrickIdTable.EMPTY));

            // Group-local block coordinates, so the brick origin is (0, 800, 0) and not the
            // entity-local one. Block 5 is a transparent medium, so the six air cells that border
            // it carry interface faces and count as occupied as well: the tight box is the 3x3x3
            // shell around block (1, 1, 1), which is (0, 0, 0)..(2, 2, 2) inclusive.
            BrickRecordLayout.BrickAABB box = jobs.Aabbs[id];
            Assert.That(box.min, Is.EqualTo(new Vector3(0, 800, 0)));
            Assert.That(box.max, Is.EqualTo(new Vector3(3, 803, 3)));

            DisposeJob(job);
            empty.Dispose();
        }

        [TestCase(0)]
        [TestCase(4095)]
        [TestCase(0x123)]
        public void LocalBrickPos_RoundTripsLocalBrickIdx(int idx)
        {
            RenderGroup grouping = RenderGroup.Default;
            Assert.That(grouping.LocalBrickIdx(grouping.LocalBrickPos(idx)), Is.EqualTo(idx));
        }
    }
}
