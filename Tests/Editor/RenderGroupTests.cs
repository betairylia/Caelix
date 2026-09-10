using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Caelix;
using Caelix.Rendering;
using Caelix.Rendering.RayQuery;

namespace Caelix.Tests
{
    /// <summary>
    /// The renderer's brick grouping and the job that buckets a change list by group.
    /// </summary>
    public class RenderGroupTests
    {
        private static readonly RenderGroupSize[] AllPresets =
        {
            RenderGroupSize.Bricks16,
            RenderGroupSize.Bricks8,
            RenderGroupSize.Bricks32,
            RenderGroupSize.Bricks16x128x16,
        };

        /// <summary>
        /// The group and the storage region share their layout BY CONSTRUCTION in milestone 1: both
        /// are 16 bricks per axis, x-fastest. Storage no longer says so, and the group shape is a
        /// renderer setting since milestone 4, so the DEFAULT preset's numbers are pinned as
        /// literals; changing them has to be a deliberate edit here.
        /// </summary>
        [Test]
        public void LocalBrickIndexIsXFastestOverSixteenBricksPerAxis()
        {
            RenderGroup grouping = RenderGroup.Default;
            for (int z = 0; z < grouping.BricksPerAxis.z; z++)
            for (int y = 0; y < grouping.BricksPerAxis.y; y++)
            for (int x = 0; x < grouping.BricksPerAxis.x; x++)
            {
                Assert.That(
                    grouping.LocalBrickIdx(new int3(x, y, z)),
                    Is.EqualTo(x + y * 16 + z * 16 * 16));
            }
        }

        [Test]
        public void DefaultGroupIsSixteenBricksPerAxis()
        {
            Assert.That(RenderGroup.Default.BricksPerAxis, Is.EqualTo(new int3(16)));
            Assert.That(RenderGroup.Default.BricksInGroup, Is.EqualTo(4096));
            Assert.That(RenderGroup.Default.Mask, Is.EqualTo(new int3(15)));
        }

        [TestCase(0, 0, 0)]
        [TestCase(15, 15, 15)]
        [TestCase(16, 0, 0)]
        [TestCase(-1, -1, -1)]
        [TestCase(-16, -17, 33)]
        [TestCase(-129, 130, -131)]
        public void GroupKeysRoundTripBrickKeys(int x, int y, int z)
        {
            var key = new int3(x, y, z);

            foreach (RenderGroupSize size in AllPresets)
            {
                RenderGroup grouping = RenderGroupPresets.Of(size);

                int3 group = grouping.Of(key);
                int3 local = grouping.LocalBrick(key);

                Assert.That(
                    math.all(local >= 0) && math.all(local < grouping.BricksPerAxis), Is.True, size.ToString());
                Assert.That(grouping.FirstKey(group) + local, Is.EqualTo(key), size.ToString());
                Assert.That(math.all(key >= grouping.FirstKey(group)), Is.True, size.ToString());
                Assert.That(math.all(key <= grouping.LastKey(group)), Is.True, size.ToString());
                Assert.That(
                    grouping.LastKey(group) - grouping.FirstKey(group),
                    Is.EqualTo(grouping.Mask), size.ToString());
                Assert.That(
                    grouping.LocalBrickPos(grouping.LocalBrickIdx(local)),
                    Is.EqualTo(local), size.ToString());
            }
        }

        [Test]
        public void BlockOriginIsTheGroupsFirstBlock()
        {
            RenderGroup grouping = RenderGroup.Default;
            Assert.That(grouping.BlockOrigin(new int3(0, 0, 0)), Is.EqualTo(new int3(0, 0, 0)));
            Assert.That(grouping.BlockOrigin(new int3(-1, 0, 0)), Is.EqualTo(new int3(-128, 0, 0)));
            Assert.That(grouping.BlockOrigin(new int3(1, 2, 3)), Is.EqualTo(new int3(128, 256, 384)));
        }

        /// <summary>
        /// A group need not be a cube: the tall preset is the one that separates a per-axis shift
        /// from a single one.
        /// </summary>
        [Test]
        public void TallGroupPlacesKeysByAxis()
        {
            RenderGroup grouping = RenderGroupPresets.Of(RenderGroupSize.Bricks16x128x16);

            Assert.That(grouping.BricksPerAxis, Is.EqualTo(new int3(16, 128, 16)));
            Assert.That(grouping.BricksInGroup, Is.EqualTo(32768));

            Assert.That(grouping.Of(new int3(0, 100, 0)), Is.EqualTo(new int3(0, 0, 0)));
            Assert.That(grouping.Of(new int3(0, 128, 0)), Is.EqualTo(new int3(0, 1, 0)));
            Assert.That(grouping.Of(new int3(16, 0, 0)), Is.EqualTo(new int3(1, 0, 0)));
            Assert.That(grouping.Of(new int3(-1, -1, -1)), Is.EqualTo(new int3(-1, -1, -1)));

            Assert.That(grouping.LocalBrickIdx(new int3(0, 100, 0)), Is.EqualTo(100 << 4));

            foreach (int idx in new[] { 0, 32767, 0x1234 })
            {
                Assert.That(grouping.LocalBrickIdx(grouping.LocalBrickPos(idx)), Is.EqualTo(idx));
            }
        }

        [Test]
        public void PresetsStayWithinTheIndexBudget()
        {
            foreach (RenderGroupSize size in AllPresets)
            {
                RenderGroup grouping = RenderGroupPresets.Of(size);
                Assert.That(grouping.IndexBits, Is.LessThanOrEqualTo(RenderGroup.MaxIndexBits), size.ToString());
                Assert.That(grouping.BricksInGroup, Is.LessThanOrEqualTo(32768), size.ToString());
            }

            Assert.Throws<System.ArgumentOutOfRangeException>(() => new RenderGroup(new int3(6, 6, 6)));
        }

        /// <summary>
        /// The brick info word gives the group-local index bits 0..15 and the coarse occupancy
        /// bits 16..23, so the widest preset's index survives the pack untouched.
        /// </summary>
        [Test]
        public void PackBrickInfoKeepsAFifteenBitIndexApartFromTheOccupancy()
        {
            int word = BrickRecordLayout.PackBrickInfo(32767, 0xFF);

            Assert.That(word & 0xFFFF, Is.EqualTo(32767));
            Assert.That((word >> 16) & 0xFF, Is.EqualTo(0xFF));
        }

        [Test]
        public void KeywordsNameEveryNonDefaultPreset()
        {
            Assert.That(RenderGroupPresets.Keyword(RenderGroupSize.Bricks16), Is.EqualTo(string.Empty));

            var seen = new HashSet<string>();
            foreach (RenderGroupSize size in new[]
                     {
                         RenderGroupSize.Bricks8, RenderGroupSize.Bricks32, RenderGroupSize.Bricks16x128x16
                     })
            {
                string keyword = RenderGroupPresets.Keyword(size);
                Assert.That(keyword, Is.Not.Empty, size.ToString());
                Assert.That(seen.Add(keyword), Is.True, $"{size} reuses keyword {keyword}");
                Assert.That(RenderGroupPresets.Keywords, Does.Contain(keyword));
            }

            Assert.That(RenderGroupPresets.Keywords.Length, Is.EqualTo(seen.Count));
        }

        [TestCase(RenderGroupSize.Bricks16)]
        [TestCase(RenderGroupSize.Bricks8)]
        [TestCase(RenderGroupSize.Bricks32)]
        [TestCase(RenderGroupSize.Bricks16x128x16)]
        public void BucketChangesGroupsEntriesAndKeepsTheirRelativeOrder(RenderGroupSize size)
        {
            RenderGroup grouping = RenderGroupPresets.Of(size);

            // Seven changes across three groups of the default preset, deliberately interleaved.
            var keys = new[]
            {
                new int3(0, 0, 0),     // default group (0,0,0)
                new int3(16, 0, 0),    // default group (1,0,0)
                new int3(1, 0, 0),     // default group (0,0,0)
                new int3(-1, 0, 0),    // default group (-1,0,0)
                new int3(17, 0, 0),    // default group (1,0,0)
                new int3(2, 0, 0),     // default group (0,0,0)
                new int3(-16, 0, 0),   // default group (-1,0,0)
            };

            using var source = BuildChanges(keys);
            using var sorted = new NativeList<BrickChange>(keys.Length, Allocator.Persistent);
            using var groupKeys = new NativeList<int3>(keys.Length, Allocator.Persistent);
            using var groupStarts = new NativeList<int>(keys.Length, Allocator.Persistent);
            using var groupCounts = new NativeList<int>(keys.Length, Allocator.Persistent);

            new BucketChangesJob
            {
                changes = source,
                changeCount = keys.Length,
                sorted = sorted,
                groupKeys = groupKeys,
                groupStarts = groupStarts,
                groupCounts = groupCounts,
                grouping = grouping
            }.Run();

            // Expectations come from the grouping under test, in first-seen order.
            var expected = new Dictionary<int3, List<int3>>();
            foreach (int3 key in keys)
            {
                int3 group = grouping.Of(key);
                if (!expected.TryGetValue(group, out List<int3> members))
                {
                    members = new List<int3>();
                    expected[group] = members;
                }

                members.Add(key);
            }

            if (size == RenderGroupSize.Bricks16)
            {
                // The default preset's group keys stay pinned as literals.
                Assert.That(expected.Keys, Is.EquivalentTo(new[]
                {
                    new int3(0, 0, 0), new int3(1, 0, 0), new int3(-1, 0, 0)
                }));
            }

            Assert.That(sorted.Length, Is.EqualTo(keys.Length));
            Assert.That(groupKeys.Length, Is.EqualTo(expected.Count));
            Assert.That(groupStarts.Length, Is.EqualTo(expected.Count));
            Assert.That(groupCounts.Length, Is.EqualTo(expected.Count));

            int total = 0;
            int previousEnd = 0;
            for (int g = 0; g < groupKeys.Length; g++)
            {
                int3 group = groupKeys[g];
                Assert.That(expected.ContainsKey(group), Is.True, $"unexpected group {group}");
                Assert.That(groupStarts[g], Is.EqualTo(previousEnd), "slices must be contiguous and in order");

                List<int3> want = expected[group];
                Assert.That(groupCounts[g], Is.EqualTo(want.Count));

                for (int i = 0; i < want.Count; i++)
                {
                    BrickChange change = sorted[groupStarts[g] + i];
                    Assert.That(change.Key, Is.EqualTo(want[i]));
                    Assert.That(grouping.Of(change.Key), Is.EqualTo(group));
                }

                previousEnd = groupStarts[g] + groupCounts[g];
                total += groupCounts[g];
                expected.Remove(group);
            }

            Assert.That(expected, Is.Empty);
            Assert.That(total, Is.EqualTo(keys.Length));
        }

        [Test]
        public void BucketChangesProducesNothingForAnEmptyChangeList()
        {
            using var source = new NativeArray<BrickChange>(1, Allocator.Persistent);
            using var sorted = new NativeList<BrickChange>(1, Allocator.Persistent);
            using var groupKeys = new NativeList<int3>(1, Allocator.Persistent);
            using var groupStarts = new NativeList<int>(1, Allocator.Persistent);
            using var groupCounts = new NativeList<int>(1, Allocator.Persistent);

            new BucketChangesJob
            {
                changes = source,
                changeCount = 0,
                sorted = sorted,
                groupKeys = groupKeys,
                groupStarts = groupStarts,
                groupCounts = groupCounts,
                grouping = RenderGroup.Default
            }.Run();

            Assert.That(sorted.Length, Is.EqualTo(0));
            Assert.That(groupKeys.Length, Is.EqualTo(0));
            Assert.That(sorted.AsArray().IsCreated, Is.True, "an empty sorted list must still be job-schedulable");
        }

        /// <summary>
        /// The path the scene renderer actually takes: read a live entity's change view, copy it
        /// into a job array and bucket it. Guards the read-only view's use on the main thread.
        /// </summary>
        [TestCase(RenderGroupSize.Bricks16)]
        [TestCase(RenderGroupSize.Bricks8)]
        [TestCase(RenderGroupSize.Bricks32)]
        [TestCase(RenderGroupSize.Bricks16x128x16)]
        public void BucketChangesAcceptsALiveChangeList(RenderGroupSize size)
        {
            RenderGroup grouping = RenderGroupPresets.Of(size);

            var data = new VoxelEntityData(Allocator.Persistent);
            try
            {
                data.SetBlock(new int3(1, 1, 1), new Block(1));          // brick (0,0,0)
                data.SetBlock(new int3(200, 200, 200), new Block(1));    // brick (25,25,25)
                data.PropagateDirtyFlags(DirtyFlags.All).Complete();
                data.BuildChangeList();

                NativeArray<BrickChange>.ReadOnly changes = data.Changes;
                Assert.That(changes.Length, Is.EqualTo(2));

                using var source = CopyOf(changes);
                using var sorted = new NativeList<BrickChange>(changes.Length, Allocator.Persistent);
                using var groupKeys = new NativeList<int3>(changes.Length, Allocator.Persistent);
                using var groupStarts = new NativeList<int>(changes.Length, Allocator.Persistent);
                using var groupCounts = new NativeList<int>(changes.Length, Allocator.Persistent);

                new BucketChangesJob
                {
                    changes = source,
                    changeCount = changes.Length,
                    sorted = sorted,
                    groupKeys = groupKeys,
                    groupStarts = groupStarts,
                    groupCounts = groupCounts,
                    grouping = grouping
                }.Run();

                var wanted = new HashSet<int3>
                {
                    grouping.Of(new int3(0, 0, 0)), grouping.Of(new int3(25, 25, 25))
                };

                if (size == RenderGroupSize.Bricks16)
                {
                    Assert.That(wanted, Is.EquivalentTo(new[] { new int3(0, 0, 0), new int3(1, 1, 1) }));
                }

                Assert.That(groupKeys.Length, Is.EqualTo(wanted.Count));
                int totalCounted = 0;
                for (int g = 0; g < groupKeys.Length; g++)
                {
                    Assert.That(wanted, Does.Contain(groupKeys[g]));
                    totalCounted += groupCounts[g];
                }

                Assert.That(totalCounted, Is.EqualTo(2));
                Assert.That(sorted.Length, Is.EqualTo(2));
            }
            finally
            {
                data.Dispose();
            }
        }

        private static NativeArray<BrickChange> CopyOf(NativeArray<BrickChange>.ReadOnly changes)
        {
            var copy = new NativeArray<BrickChange>(
                math.max(1, changes.Length), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < changes.Length; i++)
            {
                copy[i] = changes[i];
            }

            return copy;
        }

        private static NativeArray<BrickChange> BuildChanges(int3[] keys)
        {
            var changes = new NativeArray<BrickChange>(keys.Length, Allocator.Persistent);
            for (int i = 0; i < keys.Length; i++)
            {
                changes[i] = new BrickChange
                {
                    Key = keys[i],
                    Kind = ChangeKind.Updated,
                    SourceFlags = DirtyFlags.Geometry,
                    RequiredFlags = DirtyFlags.GeometryWithLocalNeighbor
                };
            }

            return changes;
        }
    }
}
