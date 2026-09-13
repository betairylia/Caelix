using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Caelix.Simulation;
using Caelix.Tick;
using Caelix.Utils;

namespace Caelix.Tests
{
    /// <summary>
    /// The automata stage hands every hook one list of the bricks with simulation-channel work:
    /// geometry-only edits produce no records, channel bits and allocation status ride on each
    /// record for the hook to select by, and the count in the inputs lets a later hook skip an empty
    /// tick without reading the list after an earlier hook scheduled a job over it.
    /// </summary>
    public class AutomataHookSelectionTests
    {
        private static readonly Guid128 EntityA = new(0xA1u, 0xA2u, 0xA3u, 0xA4u);

        private struct TouchBricksJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<RequiredBrick> bricks;

            public void Execute(int index)
            {
                RequiredBrick _ = bricks[index];
            }
        }

        /// <summary>Records the count and a copy of the records each tick, then schedules a job over the list.</summary>
        private sealed class RecordingHook : ITickHook<CaelixWorld.AutomataStageInputs>
        {
            public readonly System.Collections.Generic.List<int> Counts = new();
            public readonly System.Collections.Generic.List<RequiredBrick[]> Records = new();

            public bool Execute(CaelixWorld.AutomataStageInputs inputs, JobHandle stageStart, JobHandle chained,
                out JobHandle handle)
            {
                Counts.Add(inputs.BricksRequiredUpdateCount);
                if (inputs.BricksRequiredUpdateCount == 0)
                {
                    Records.Add(new RequiredBrick[0]);
                    handle = chained;
                    return true;
                }

                // Only legal because no earlier hook scheduled over the list; the second hook
                // below relies on the count instead.
                Records.Add(inputs.BricksRequiredUpdate.AsArray().ToArray());
                handle = new TouchBricksJob { bricks = inputs.BricksRequiredUpdate.AsDeferredJobArray() }
                    .Schedule(inputs.BricksRequiredUpdate, 8, chained);
                return true;
            }
        }

        /// <summary>A second hook that only reads the count: it must never touch the list on the main thread.</summary>
        private sealed class CountingHook : ITickHook<CaelixWorld.AutomataStageInputs>
        {
            public readonly System.Collections.Generic.List<int> Counts = new();

            public bool Execute(CaelixWorld.AutomataStageInputs inputs, JobHandle stageStart, JobHandle chained,
                out JobHandle handle)
            {
                Counts.Add(inputs.BricksRequiredUpdateCount);
                handle = inputs.BricksRequiredUpdateCount == 0
                    ? chained
                    : new TouchBricksJob { bricks = inputs.BricksRequiredUpdate.AsDeferredJobArray() }
                        .Schedule(inputs.BricksRequiredUpdate, 8, chained);
                return true;
            }
        }

        private static CaelixServer NewServer(out CaelixWorld world)
        {
            var server = new CaelixServer { TickRate = 100f };
            world = server.CreateWorld(CaelixWorldConfig.Default(0, "hooks"));
            world.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            return server;
        }

        [Test]
        public void GeometryOnlyEdits_ProduceNoAutomataRecords()
        {
            using var server = NewServer(out CaelixWorld world);
            var hook = new RecordingHook();
            world.AutomataStage.RegisterHook(hook);

            world.GetEntity(EntityA).SetBlock(new int3(5, 5, 5), new Block(0x8001), BrickUpdateFlags.None);
            server.Step();
            server.Step();

            Assert.That(hook.Counts, Is.EqualTo(new[] { 0, 0 }));
        }

        [Test]
        public void RecordsCarryChannelAndAllocation_AndALaterHookSeesTheSameCount()
        {
            using var server = NewServer(out CaelixWorld world);
            var first = new RecordingHook();
            var second = new CountingHook();
            world.AutomataStage.RegisterHook(first);
            world.AutomataStage.RegisterHook(second);

            // A voxel on the +X face of brick (0,0,0) on channel 1: the written brick is allocated,
            // the flagged neighbour (1,0,0) is not.
            world.GetEntity(EntityA).SetBlock(new int3(7, 5, 5), new Block(0x8001), BrickUpdateFlags.Automata1);
            server.Step(); // propagates the write into next-tick work
            server.Step(); // delivers it

            Assert.That(first.Counts[0], Is.EqualTo(0));
            Assert.That(first.Counts[1], Is.GreaterThan(1));
            Assert.That(second.Counts, Is.EqualTo(first.Counts), "the count is the same for every hook in the tick");

            bool sawAllocated = false, sawEmpty = false;
            foreach (RequiredBrick record in first.Records[1])
            {
                Assert.That(record.Flags & BrickUpdateFlags.Automata1, Is.EqualTo(BrickUpdateFlags.Automata1));
                Assert.That(record.Flags & BrickUpdateFlags.Automata0, Is.EqualTo(BrickUpdateFlags.None));
                if (record.Key.Equals(new int3(0, 0, 0))) { Assert.That(record.IsAllocated, Is.True); sawAllocated = true; }
                if (record.Key.Equals(new int3(1, 0, 0))) { Assert.That(record.IsAllocated, Is.False); sawEmpty = true; }
            }

            Assert.That(sawAllocated, Is.True);
            Assert.That(sawEmpty, Is.True);
        }
    }
}
