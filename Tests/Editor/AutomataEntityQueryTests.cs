using System;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using Caelix.Simulation;
using Caelix.Tick;
using Caelix.Utils;

namespace Caelix.Tests
{
    public unsafe class AutomataEntityQueryTests
    {
        private static readonly Guid128 Source = new(1, 2, 3, 4);
        private static readonly Guid128 AlienA = new(5, 6, 7, 8);
        private static readonly Guid128 AlienB = new(9, 10, 11, 12);
        private static readonly Guid128 Distant = new(13, 14, 15, 16);

        private sealed class CompareHook : ITickHook<CaelixWorld.AutomataStageInputs>, IDisposable
        {
            public NativeArray<int> Results = new(4, Allocator.Persistent);
            public bool Execute(CaelixWorld.AutomataStageInputs inputs, JobHandle start, JobHandle chained, out JobHandle handle)
            {
                handle = new CompareJob
                {
                    Context = inputs.ReadContext,
                    Bricks = inputs.BricksRequiredUpdate.AsDeferredJobArray(),
                    Results = Results
                }.Schedule(chained);
                return true;
            }
            public void Dispose() => Results.Dispose();
        }

        [BurstCompile]
        private struct CompareJob : IJob
        {
            [ReadOnly] public AutomataReadContext Context;
            [ReadOnly] public NativeArray<RequiredBrick> Bricks;
            public NativeArray<int> Results;
            public void Execute()
            {
                for (int i = 0; i < Results.Length; i++) Results[i] = 0;
                AutomataReadContext reference = Context;
                reference.AlienQuery.CandidateIndices = default; // original ordered all-entity query
                foreach (RequiredBrick brick in Bricks)
                {
                    AutomataBrick access = Context.OpenBrick(brick);
                    AutomataReader actual = Context.CreateReader(brick, access);
                    AutomataReader expected = reference.CreateReader(brick, access);
                    Results[2] += brick.AlienCandidateRange.y;
                    for (int z = -1; z <= 8; z++)
                    for (int y = -1; y <= 8; y++)
                    for (int x = -1; x <= 8; x++)
                    {
                        int3 p = access.Origin + new int3(x, y, z);
                        Block a = actual.GetBlock(p), b = expected.GetBlock(p);
                        ushort ma = actual.GetSlot<ushort>(SectorSlotId.Reserved1, p, out float3 pa);
                        ushort mb = expected.GetSlot<ushort>(SectorSlotId.Reserved1, p, out float3 pb);
                        if (a != b || ma != mb || math.any(math.abs(pa - pb) > 0.0001f)) Results[0]++;
                        if (!a.isEmpty) Results[1]++;
                    }
                    Results[3]++;
                }
            }
        }

        private static void Add(CaelixWorld world, Guid128 id, RigidTransform pose, ushort value)
        {
            world.CreateEntity(id, pose, isStatic: true);
            world.AddBody(id);
            VoxelEntityData data = world.GetEntity(id);
            int3[] positions = id == Source
                ? new[] { new int3(4, 4, 4) }
                : new[] { new int3(-1, 4, 4), new int3(8, 4, 4), new int3(4, 4, 4), new int3(6, 6, 6) };
            foreach (int3 p in positions)
            {
                data.SetBlock(p, new Block(value), BrickUpdateFlags.None);
                data.SetSlot(SectorSlotId.Reserved1, p, value, BrickUpdateFlags.None);
            }
        }

        private static void Check(CaelixWorld world, CompareHook hook, bool isolated = false)
        {
            world.GetEntity(Source).MarkRequired(int3.zero, BrickUpdateFlags.Automata0);
            world.Tick(0.01f);
            Assert.That(hook.Results[0], Is.Zero, "candidate and all-entity block/metadata reads differ");
            Assert.That(hook.Results[1], Is.GreaterThan(0));
            Assert.That(hook.Results[3], Is.GreaterThan(0));
            if (isolated) Assert.That(hook.Results[2], Is.Zero, "distant bodies must be rejected by the BVH");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CandidatesMatchFullScanAcrossFirstTickMotionStaticnessAndReplacement(bool rotated)
        {
            using var world = new CaelixWorld(CaelixWorldConfig.Default());
            using var hook = new CompareHook();
            world.AutomataStage.RegisterHook(hook);
            var pose = new RigidTransform(rotated ? quaternion.EulerXYZ(0.2f, 0.7f, -0.3f) : quaternion.identity,
                new float3(-137f, 20f, 9f));
            Add(world, Source, pose, 0x8010);
            Add(world, AlienA, pose, 0x8020);
            Add(world, AlienB, new RigidTransform(pose.rot, pose.pos + new float3(0.1f)), 0x8030);
            Add(world, Distant, new RigidTransform(quaternion.identity, new float3(10000f)), 0x8040);
            Check(world, hook); // no physics step or collision world existed before this tick

            foreach (Guid128 id in new[] { AlienA, AlienB })
            {
                var entity = world.GetEntity(id);
                entity.transform.pos += new float3(1000f);
                entity.isStatic = false;
                world.SetEntity(id, entity);
            }
            Check(world, hook, isolated: true);
            world.RemoveEntity(AlienA);
            Add(world, AlienA, pose, 0x8050);
            Check(world, hook);
        }

        [Test]
        public void UnallocatedRequiredBrickStillReadsAliens()
        {
            using var world = new CaelixWorld(CaelixWorldConfig.Default());
            using var hook = new CompareHook();
            world.AutomataStage.RegisterHook(hook);
            Add(world, Source, RigidTransform.identity, 0x8010);
            Add(world, AlienA, new RigidTransform(quaternion.identity, new float3(8, 0, 0)), 0x8020);
            // Dirty propagation can schedule a neighbouring brick that has no Block allocation.
            var source = world.GetEntity(Source);
            source.SetBlock(new int3(7, 4, 4), new Block(0x8010), BrickUpdateFlags.Automata0);
            source.PropagateDirtyFlags(BrickUpdateFlags.Automata0, true).Complete();
            using (var required = new NativeList<RequiredBrick>(Allocator.TempJob))
            {
                source.CollectRequiredBricks(Source, BrickUpdateFlags.Automata0, true, required);
                bool hasEmpty = false;
                foreach (RequiredBrick brick in required) hasEmpty |= !brick.IsAllocated;
                Assert.That(hasEmpty, Is.True, "fixture must schedule an unallocated brick");
            }
            Assert.That(source.TryBindBrick<Block>(SectorSlotId.Block, new int3(1, 0, 0), out _), Is.False);
            world.Tick(0.01f);
            Assert.That(hook.Results[0], Is.Zero);
            Assert.That(hook.Results[1], Is.GreaterThan(0));
            Assert.That(hook.Results[2], Is.GreaterThan(0));
            Assert.That(hook.Results[3], Is.GreaterThan(1));
        }

        [Test]
        public void StaticBoundsGrowthAfterSpatialPreparationRefreshesBvh()
        {
            using var world = new CaelixWorld(CaelixWorldConfig.Default());
            Add(world, Source, RigidTransform.identity, 0x8010);
            world.Tick(0.01f); // establish membership and properties
            world.Physics.PrepareSpatialQueries(ref world.Data);

            var source = world.GetEntity(Source);
            source.BeginAutomataWrites();
            source.SetBlock(new int3(128, 4, 4), new Block(0x8010));
            source.EndAutomataWrites();
            source.PropagateDirtyFlags(BrickUpdateFlags.All, true).Complete();
            source.RefreshNonEmptyMask();
            var body = world.Data.VoxelBodies[Source];
            body.ComputePhysicsProperties(source);
            world.Data.VoxelBodies[Source] = body;
            world.Physics.SimulateStep(0.01f, ref world.Data);

            using var hits = new NativeList<int>(Allocator.Temp);
            var queryHits = hits;
            world.Physics.PhysicsWorld.CollisionWorld.OverlapAabb(new OverlapAabbInput
            {
                Aabb = new Aabb { Min = new float3(129, 4, 4), Max = new float3(130, 5, 5) },
                Filter = CollisionFilter.Default
            }, ref queryHits);
            Assert.That(hits.Length, Is.EqualTo(1), "the static tree must include the newly attached region");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PostPhysicsQueriesUseResultingPoses(bool synchronizeDuringSimulation)
        {
            var config = CaelixWorldConfig.Default();
            config.doAlienPropagation = true;
            config.alienIncludeMovingBricks = true;
            config.physics.gravity = float3.zero;
            config.physics.linearAirFriction = 0;
            config.physics.synchronizeCollisionWorld = synchronizeDuringSimulation;
            using var world = new CaelixWorld(config);
            using var hook = new CompareHook();
            world.AutomataStage.RegisterHook(hook);
            Add(world, Source, RigidTransform.identity, 0x8010);
            Add(world, AlienA, new RigidTransform(quaternion.identity, new float3(200, 0, 0)), 0x8020);
            var source = world.GetEntity(Source);
            source.isStatic = false;
            world.SetEntity(Source, source);
            var body = world.Data.VoxelBodies[Source];
            body.motionVelocity.LinearVelocity = new float3(200, 0, 0);
            world.Data.VoxelBodies[Source] = body;
            source.MarkRequired(int3.zero, BrickUpdateFlags.Automata0);
            world.Tick(1f);

            Assert.That(hook.Results[2], Is.Zero, "pre-voxel candidates must use the starting pose");
            float3 resultingPosition = world.GetEntity(Source).transform.pos;
            Assert.That(resultingPosition.x, Is.GreaterThan(100));
            float3 collisionPosition = world.Physics.PhysicsWorld.Bodies[0].WorldFromBody.pos;
            Assert.That(math.distance(collisionPosition, resultingPosition), Is.LessThan(0.001f));
            Assert.That(world.Physics.BrickOverlapGraph.IsCreated, Is.True);
        }
    }
}
