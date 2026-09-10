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
    }
}
