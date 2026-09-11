using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Caelix.Client;
using Caelix.Net;
using Caelix.Rendering.RayQuery;
using Caelix.Simulation;
using Caelix.Utils;

namespace Caelix.Tests
{
    /// <summary>
    /// The ray query scene renderer visits only the groups that have work: a world where nothing
    /// changes costs nothing per frame, and a group whose pool range a compaction moved is visited
    /// even though it had no change of its own.
    /// </summary>
    public class RayQueryIdleGroupTests
    {
        /// <summary>Server, client, view and renderer, wired the way a host wires them.</summary>
        private sealed class Rig : System.IDisposable
        {
            public readonly CaelixServer Server;
            public readonly CaelixClient Client;
            public readonly CaelixWorld World;
            public readonly CaelixRayQueryRenderer Renderer;
            public readonly Guid128 Guid = new Guid128(7u, 8u, 9u, 10u);

            private readonly Material material;
            private readonly GameObject go;

            public Rig(string name)
            {
                Server = new CaelixServer();
                World = Server.CreateWorld(CaelixWorldConfig.Default());
                LocalChannel.CreatePair(out var serverEnd, out var clientEnd);
                Client = new CaelixClient(clientEnd, Server.Types);
                Server.AddConnection(serverEnd);
                World.CreateEntity(Guid, RigidTransform.identity, isStatic: true);

                material = new Material(Shader.Find("Caelix/AabbInstance"));
                go = new GameObject(name);
                // No Awake in edit mode: the renderer creates its pool, instance table and
                // acceleration structure lazily in Tick.
                Renderer = go.AddComponent<CaelixRayQueryRenderer>();
                Renderer.brickMat = material;
            }

            /// <summary>One server step and one client frame, ending with a built structure.</summary>
            public void StepAndRender()
            {
                Server.Step();
                Client.Receive();
                if (Renderer.Source == null)
                {
                    Renderer.SetSource(Client.World);
                }

                Client.PrepareRender();
                Renderer.Tick();
                Renderer.voxelScene.Build();
                Client.EndFrame();
            }

            /// <summary>A client frame with nothing to receive: what an idle world costs.</summary>
            public void RenderIdleFrame()
            {
                Client.Receive();
                Client.PrepareRender();
                Renderer.Tick();
                Renderer.voxelScene.Build();
                Client.EndFrame();
            }

            public int InstanceCount => (int)Renderer.voxelScene.GetInstanceCount();

            public void Dispose()
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(material);
                Client.Dispose();
                Server.Dispose();
            }
        }

        /// <summary>Two groups upload once, then a settled world visits nothing at all.</summary>
        [Test]
        public void IdleStaticWorld_VisitsNoGroup()
        {
            if (!SystemInfo.supportsInlineRayTracing) Assert.Ignore("Inline ray tracing is required.");

            using var rig = new Rig("ray-query-idle-test");

            // Two bricks 200 blocks apart: one group each with the default 128-block grouping.
            rig.World.SetBlock(rig.Guid, new int3(1, 1, 1), new Block(0x8001));
            rig.World.SetBlock(rig.Guid, new int3(200, 1, 1), new Block(0x8001));

            rig.StepAndRender();
            Assert.That(rig.Renderer.groupsEmittedThisTick, Is.EqualTo(2), "both groups get a job");
            Assert.That(rig.Renderer.groupsVisitedThisTick, Is.EqualTo(2), "both groups are visited");
            Assert.That(rig.InstanceCount, Is.EqualTo(2));

            rig.RenderIdleFrame();
            Assert.That(rig.Renderer.groupsEmittedThisTick, Is.Zero, "nothing changed, so nothing is emitted");
            Assert.That(rig.Renderer.groupsVisitedThisTick, Is.Zero, "an idle group is not visited at all");
            Assert.That(rig.Renderer.PendingGroupCount, Is.Zero, "no group carries work across the tick");
            Assert.That(rig.InstanceCount, Is.EqualTo(2), "the instances stay in the structure");
            Assert.That(rig.Renderer.groupCount, Is.EqualTo(2), "both groups are still live");
        }

        /// <summary>
        /// A pool page that grows re-packs its live ranges, so a group with no change of its own
        /// still has to republish the record naming its range.
        /// </summary>
        /// <remarks>
        /// The page starts at 4096 bricks. Group A reserves 1 and group B reserves 2, so the third
        /// group's full 4096-brick range no longer fits past the bump pointer and the page grows.
        /// Compaction packs the largest range first, which puts B at offset 0 and A behind it: both
        /// moved without either being touched by this tick's changes.
        /// </remarks>
        [Test]
        public void PoolCompaction_RepublishesTheMovedGroupWithoutAChange()
        {
            if (!SystemInfo.supportsInlineRayTracing) Assert.Ignore("Inline ray tracing is required.");

            using var rig = new Rig("ray-query-compaction-test");

            // Group (0,0,0): one brick.
            rig.World.SetBlock(rig.Guid, new int3(1, 1, 1), new Block(0x8001));
            rig.StepAndRender();
            Assert.That(rig.InstanceCount, Is.EqualTo(1));

            // Group (1,0,0): two bricks, so the two live ranges have different sizes and the
            // compaction below reorders them.
            rig.World.SetBlock(rig.Guid, new int3(201, 1, 1), new Block(0x8001));
            rig.World.SetBlock(rig.Guid, new int3(209, 1, 1), new Block(0x8001));
            rig.StepAndRender();
            Assert.That(rig.InstanceCount, Is.EqualTo(2));
            Assert.That(rig.Renderer.groupsVisitedThisTick, Is.EqualTo(1), "only the changed group is visited");

            // Group (2,0,0): every brick of the region at block origin (256,0,0), 4096 of them.
            for (int bx = 0; bx < 16; bx++)
            {
                for (int by = 0; by < 16; by++)
                {
                    for (int bz = 0; bz < 16; bz++)
                    {
                        rig.World.SetBlock(
                            rig.Guid,
                            new int3(256 + bx * 8 + 1, by * 8 + 1, bz * 8 + 1),
                            new Block(0x8001));
                    }
                }
            }

            rig.StepAndRender();
            Assert.That(rig.Renderer.groupsEmittedThisTick, Is.EqualTo(1), "only the new group has a job");
            Assert.That(
                rig.Renderer.groupsVisitedThisTick, Is.EqualTo(3),
                "the new group for its job, and the two moved ranges for their records");
            Assert.That(rig.InstanceCount, Is.EqualTo(3));
            AssertEveryGroupPublishesItsRange(rig);

            // And a settled world after the compaction is idle again.
            rig.RenderIdleFrame();
            Assert.That(rig.Renderer.groupsVisitedThisTick, Is.Zero);
            Assert.That(rig.Renderer.PendingGroupCount, Is.Zero);
            Assert.That(rig.InstanceCount, Is.EqualTo(3));
            AssertEveryGroupPublishesItsRange(rig);
        }

        private static void AssertEveryGroupPublishesItsRange(Rig rig)
        {
            int groups = 0;
            foreach (RayQueryGroupRenderer group in rig.Renderer.AllGroups())
            {
                groups++;
                Assert.That(
                    group.PublishedRecordMatchesPool, Is.True,
                    $"group {group.GroupKey} publishes a range the pool no longer holds");
                Assert.That(
                    group.HasRenderable, Is.True,
                    $"group {group.GroupKey} has no acceleration-structure instance");
            }

            Assert.That(groups, Is.EqualTo(rig.Renderer.groupCount));
        }

        /// <summary>First uploads survive EndFrame without retaining its native change arrays.</summary>
        [TestCase(false)]
        [TestCase(true)]
        public void InitialUpload_BrickBudgetDrainsAcrossFrames(bool bindBeforeJoin)
        {
            if (!SystemInfo.supportsInlineRayTracing) Assert.Ignore("Inline ray tracing is required.");
            using var rig = new Rig("ray-query-streamed-join-test");
            rig.Renderer.InitialUploadBricksPerFrame = 3;
            if (bindBeforeJoin) rig.StepAndRender();

            for (int group = 0; group < 3; group++)
            {
                rig.World.SetBlock(rig.Guid, new int3(group * 128 + 17, 17, 17), new Block(0x8001));
                rig.World.SetBlock(rig.Guid, new int3(group * 128 + 25, 17, 17), new Block(0x8001));
            }

            rig.StepAndRender();
            for (int frame = 0; frame < 3; frame++)
            {
                if (frame > 0) rig.RenderIdleFrame();
                Assert.That(rig.InstanceCount, Is.EqualTo(frame + 1));
                Assert.That(rig.Renderer.initialUploadGroupsPending, Is.EqualTo(2 - frame));
                Assert.That(rig.Renderer.initialUploadGroupsThisTick, Is.EqualTo(1));
                Assert.That(rig.Renderer.initialUploadBricksThisTick, Is.EqualTo(2));
                Assert.That(rig.Renderer.bricksStagedThisTick, Is.EqualTo(2));
                AssertEveryGroupPublishesItsRange(rig);
                foreach (RayQueryGroupRenderer group in rig.Renderer.AllGroups())
                {
                    Assert.That(group.HasScheduledJob, Is.False, "no job or TempJob data crosses frames");
                }
            }

            rig.RenderIdleFrame();
            Assert.That(rig.Renderer.groupsVisitedThisTick, Is.Zero);
            Assert.That(rig.Renderer.initialUploadBricksThisTick, Is.Zero);
        }

        [Test]
        public void InitialUpload_GroupBudgetLimitsSparseInstances()
        {
            if (!SystemInfo.supportsInlineRayTracing) Assert.Ignore("Inline ray tracing is required.");
            using var rig = new Rig("ray-query-group-budget-test");
            rig.Renderer.InitialUploadGroupsPerFrame = 1;
            AddSparseGroups(rig, 3);

            rig.StepAndRender();
            Assert.That(rig.InstanceCount, Is.EqualTo(1));
            Assert.That(rig.Renderer.initialUploadGroupsPending, Is.EqualTo(2));
            rig.RenderIdleFrame();
            Assert.That(rig.InstanceCount, Is.EqualTo(2));
            Assert.That(rig.Renderer.instanceRebuildsThisTick, Is.EqualTo(1));
            Assert.That(rig.Renderer.initialUploadGroupsPending, Is.EqualTo(1));
        }

        [Test]
        public void InitialUpload_GroupLargerThanBrickBudgetMakesProgressAlone()
        {
            if (!SystemInfo.supportsInlineRayTracing) Assert.Ignore("Inline ray tracing is required.");
            using var rig = new Rig("ray-query-oversized-upload-test");
            rig.Renderer.InitialUploadBricksPerFrame = 1;
            for (int group = 0; group < 2; group++)
            {
                rig.World.SetBlock(rig.Guid, new int3(group * 128 + 17, 17, 17), new Block(0x8001));
                rig.World.SetBlock(rig.Guid, new int3(group * 128 + 25, 17, 17), new Block(0x8001));
            }

            rig.StepAndRender();
            Assert.That(rig.Renderer.initialUploadGroupsThisTick, Is.EqualTo(1));
            Assert.That(rig.Renderer.initialUploadBricksThisTick, Is.EqualTo(2));
            Assert.That(rig.Renderer.initialUploadGroupsPending, Is.EqualTo(1));
            rig.RenderIdleFrame();
            Assert.That(rig.InstanceCount, Is.EqualTo(2));
            Assert.That(rig.Renderer.initialUploadGroupsPending, Is.Zero);
        }

        [Test]
        public void InitialUpload_DeferredGroupReadsReplacementStorage()
        {
            if (!SystemInfo.supportsInlineRayTracing) Assert.Ignore("Inline ray tracing is required.");
            using var rig = new Rig("ray-query-deferred-change-test");
            rig.Renderer.InitialUploadGroupsPerFrame = 1;
            AddSparseGroups(rig, 3);
            rig.StepAndRender();

            EntityView view = rig.Renderer.Source.Views[0];
            int3 deferred = FindDeferredGroup(rig, view, 3);
            rig.World.GetEntity(rig.Guid).RemoveRegion(deferred);
            int3 origin = rig.Renderer.Grouping.BlockOrigin(deferred);
            rig.World.SetBlock(rig.Guid, origin + new int3(41, 17, 17), new Block(0x8001));
            rig.World.SetBlock(rig.Guid, origin + new int3(49, 17, 17), new Block(0x8001));
            rig.StepAndRender();
            rig.RenderIdleFrame();

            Assert.That(rig.InstanceCount, Is.EqualTo(3));
            Assert.That(rig.Renderer.initialUploadGroupsPending, Is.Zero, "updates do not enqueue a group twice");
            Assert.That(rig.Renderer.TryGetGroup(view, deferred, out RayQueryGroupRenderer group), Is.True);
            Assert.That(group.RendererBrickCount, Is.EqualTo(2), "the original brick was removed while queued");
            AssertEveryGroupPublishesItsRange(rig);
        }

        [Test]
        public void InitialUpload_RemovedRegionIsNotPublished()
        {
            if (!SystemInfo.supportsInlineRayTracing) Assert.Ignore("Inline ray tracing is required.");
            using var rig = new Rig("ray-query-deferred-removal-test");
            rig.Renderer.InitialUploadGroupsPerFrame = 1;
            AddSparseGroups(rig, 3);
            rig.StepAndRender();
            EntityView view = rig.Renderer.Source.Views[0];
            int3 deferred = FindDeferredGroup(rig, view, 3);
            rig.World.GetEntity(rig.Guid).RemoveRegion(deferred);
            rig.StepAndRender();
            rig.RenderIdleFrame();

            Assert.That(rig.InstanceCount, Is.EqualTo(2));
            Assert.That(rig.Renderer.initialUploadGroupsPending, Is.Zero);
            Assert.That(rig.Renderer.TryGetGroup(view, deferred, out _), Is.False);
        }

        [Test]
        public void InitialUpload_EntityReplacementCancelsOldKeys()
        {
            if (!SystemInfo.supportsInlineRayTracing) Assert.Ignore("Inline ray tracing is required.");
            using var rig = new Rig("ray-query-deferred-entity-test");
            rig.Renderer.InitialUploadGroupsPerFrame = 1;
            AddSparseGroups(rig, 3);
            rig.StepAndRender();

            rig.World.RemoveEntity(rig.Guid);
            rig.World.CreateEntity(rig.Guid, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(rig.Guid, new int3(657, 17, 17), new Block(0x8001));
            rig.StepAndRender();

            Assert.That(rig.Renderer.initialUploadGroupsPending, Is.Zero);
            Assert.That(rig.InstanceCount, Is.EqualTo(1));
            EntityView view = rig.Renderer.Source.Views[0];
            Assert.That(rig.Renderer.TryGetGroup(view, new int3(5, 0, 0), out _), Is.True);
        }

        [Test]
        public void InitialUpload_WorldRemovalCancelsQueueAndRebindDiscoversAgain()
        {
            if (!SystemInfo.supportsInlineRayTracing) Assert.Ignore("Inline ray tracing is required.");
            using var rig = new Rig("ray-query-deferred-world-test");
            rig.Renderer.InitialUploadGroupsPerFrame = 1;
            AddSparseGroups(rig, 3);
            rig.StepAndRender();
            Assert.That(rig.Renderer.initialUploadGroupsPending, Is.EqualTo(2));

            rig.Server.RemoveWorld(rig.World);
            rig.Server.Step();
            rig.Client.Receive();
            Assert.That(rig.Renderer.Source, Is.Null);
            Assert.That(rig.Renderer.initialUploadGroupsPending, Is.Zero);
            Assert.That(rig.InstanceCount, Is.Zero);

            CaelixWorld replacement = rig.Server.CreateWorld(CaelixWorldConfig.Default());
            replacement.CreateEntity(rig.Guid, RigidTransform.identity, isStatic: true);
            replacement.SetBlock(rig.Guid, new int3(17), new Block(0x8001));
            rig.StepAndRender();
            Assert.That(rig.InstanceCount, Is.EqualTo(1));
            Assert.That(rig.Renderer.initialUploadGroupsPending, Is.Zero);
        }

        private static void AddSparseGroups(Rig rig, int count)
        {
            for (int group = 0; group < count; group++)
            {
                rig.World.SetBlock(rig.Guid, new int3(group * 128 + 17, 17, 17), new Block(0x8001));
            }
        }

        private static int3 FindDeferredGroup(Rig rig, EntityView view, int count)
        {
            for (int group = 0; group < count; group++)
            {
                var key = new int3(group, 0, 0);
                if (!rig.Renderer.TryGetGroup(view, key, out _)) return key;
            }

            Assert.Fail("Expected a group waiting for its first upload.");
            return default;
        }
    }
}
