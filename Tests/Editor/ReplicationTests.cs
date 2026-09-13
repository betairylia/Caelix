using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Caelix.Client;
using Caelix.Net;
using Caelix.Simulation;
using Caelix.Utils;

namespace Caelix.Tests
{
    /// <summary>
    /// Server world to client replica through the in-process channel: the replica must equal
    /// the server's Block slot after every tick, and commands must reach the server.
    /// </summary>
    public unsafe class ReplicationTests
    {
        private sealed class CountingAutomaton : Caelix.Tick.ITickHook<CaelixWorld.AutomataStageInputs>
        {
            public int Calls;

            public bool Execute(CaelixWorld.AutomataStageInputs inputs,
                Unity.Jobs.JobHandle stageStart, Unity.Jobs.JobHandle chained,
                out Unity.Jobs.JobHandle handle)
            {
                Calls++;
                handle = chained;
                return true;
            }
        }

        [Test]
        public void PackedSceneMode_DisablesAutomataButStillReplicatesEdits()
        {
            using var rig = new Rig();
            var automaton = new CountingAutomaton();
            rig.World.AutomataStage.RegisterHook(automaton);
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            var position = new int3(7, 2, 3);
            rig.World.SetBlock(EntityA, position, new Block(PackedSceneColor.Opaque(4, 12, 20)));
            rig.Server.Step();
            rig.Client.Receive();
            AssertReplicaMatchesServer(rig, rig.Client);

            var changed = new Block(PackedSceneColor.Emissive(15, 8, 1, 2));
            rig.World.SetBlock(EntityA, position, changed);
            rig.Server.Step();
            rig.Client.Receive();
            Assert.That(automaton.Calls, Is.EqualTo(BlockEncoding.PackedSceneColor ? 0 : 2));
            Assert.That(rig.World.GetEntity(EntityA).GetBlock(position), Is.EqualTo(changed));
            AssertReplicaMatchesServer(rig, rig.Client);
        }

        private struct TestEvent
        {
            public int Value;
            public float3 Position;
        }

        [Test]
        public void Receive_MessageLimitAndInvalidPacketPreserveLaterWritesAcrossWorlds()
        {
            LocalChannel.CreatePair(out LocalChannel sender, out LocalChannel receiver);
            using (sender)
            using (var client = new CaelixClient(receiver))
            {
                var writer = new NetMessageWriter();
                for (ushort world = 0; world < 2; world++)
                {
                    NetHeader.Write(writer, NetMessageType.EntitySpawn, world, 0);
                    writer.Write(new EntitySpawnMessage { Guid = EntityA, Transform = RigidTransform.identity, IsStatic = 1 });
                    sender.Send(NetDelivery.Reliable, writer.AsSpan());
                    writer.Reset();
                }
                client.Receive();

                for (int i = 0; i < 1030; i++)
                {
                    // The same guid and coordinates in different worlds must never share work.
                    NetHeader.Write(writer, NetMessageType.BrickBatch, (ushort)(i % 2), (uint)i);
                    writer.Write(new BrickBatchHeader { Guid = EntityA, BrickCount = 1 });
                    writer.Write(int3.zero);
                    writer.Write((byte)BrickOp.Update);
                    writer.Write((byte)1);
                    writer.Write((byte)SectorSlotId.Block);
                    writer.Write((ushort)2);
                    for (int v = 0; v < BrickKey.BlocksInBrick; v++) writer.Write((ushort)(0x8000 + i));
                    sender.Send(NetDelivery.Reliable, writer.AsSpan());
                    writer.Reset();
                    if (i != 1025) continue;
                    NetHeader.Write(writer, NetMessageType.BrickBatch, 0, (uint)i);
                    writer.Write(new BrickBatchHeader { Guid = EntityA, BrickCount = 1 });
                    writer.Write(int3.zero);
                    writer.Write((byte)7); // no such operation
                    sender.Send(NetDelivery.Reliable, writer.AsSpan());
                    writer.Reset();
                }
                UnityEngine.TestTools.LogAssert.Expect(LogType.Exception,
                    new System.Text.RegularExpressions.Regex("Invalid replicated brick batch: InvalidOp"));
                client.Receive();
                for (ushort world = 0; world < 2; world++)
                {
                    Assert.That(client.GetOrCreateWorld(world).TryGetView(EntityA, out EntityView view), Is.True);
                    Assert.That(view.Data.GetBlock(new int3(7)), Is.EqualTo(new Block((ushort)(0x8000 + 1028 + world))));
                    Assert.That(CountBricksInRegion(view.Data, int3.zero), Is.EqualTo(1));
                }
            }
        }

        [Test]
        public void Receive_QueuedSectorDeltasAreVisibleInOrderToEventsAndRemoval()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            var events = new List<int>();
            rig.Client.RegisterEvent<TestEvent>((world, evt) =>
            {
                Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView view), Is.True);
                for (int s = 0; s < 12; s++)
                    Assert.That(view.Data.GetBlock(new int3(s * 128 + 1, 1, 1)),
                        Is.EqualTo(new Block((ushort)(0x8000 + evt.Value))));
                events.Add(evt.Value);
            });
            for (int tick = 1; tick <= 4; tick++)
            {
                for (int s = 0; s < 12; s++)
                    rig.World.SetBlock(EntityA, new int3(s * 128 + 1, 1, 1), new Block((ushort)(0x8000 + tick)));
                // Leave two ticks adjacent without callbacks to exercise ordered sector chains.
                if (tick >= 3) rig.World.EmitEvent(new TestEvent { Value = tick });
                rig.Server.Step();
            }
            // The server drops sector 0 and immediately writes into it again, so the client sees
            // Remove then Update for the same brick key inside one message.
            rig.World.GetEntity(EntityA).RemoveRegion(int3.zero);
            rig.World.SetBlock(EntityA, new int3(2), new Block(0x8010));
            rig.Server.Step();
            rig.Client.Receive();
            Assert.That(events, Is.EqualTo(new[] { 3, 4 }));

            Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView replicaView), Is.True);
            Assert.That(replicaView.Data.HasRegion(int3.zero), Is.True);
            Assert.That(replicaView.Data.IsBrickAllocated(int3.zero), Is.True);
            Assert.That(replicaView.Data.GetBlock(new int3(1)).isEmpty, Is.True);
            Assert.That(replicaView.Data.GetBlock(new int3(2)), Is.EqualTo(new Block(0x8010)));

            rig.Client.PrepareRender();
            AssertRemovedBeforeUpdated(replicaView, int3.zero, int3.zero);
            rig.Client.EndFrame();
            AssertReplicaMatchesServer(rig, rig.Client);
        }

        /// <summary>
        /// Finds a <see cref="ChangeKind.Removed"/> entry for <paramref name="removedKey"/> before an
        /// <see cref="ChangeKind.Updated"/> entry for <paramref name="updatedKey"/> that carries
        /// <see cref="BrickUpdateFlags.BlockBrickAdded"/>. The order is the contract: a consumer applies
        /// change entries in list order.
        /// </summary>
        private static void AssertRemovedBeforeUpdated(EntityView view, int3 removedKey, int3 updatedKey)
        {
            int removed = -1, updated = -1;
            for (int i = 0; i < view.Data.ChangeCount; i++)
            {
                BrickChange change = view.Data.GetChange(i);
                if (removed < 0 && change.Kind == ChangeKind.Removed && math.all(change.Key == removedKey))
                {
                    removed = i;
                }

                if (updated < 0 && change.Kind == ChangeKind.Updated && math.all(change.Key == updatedKey) &&
                    (change.DirtyFlags & BrickUpdateFlags.BlockBrickAdded) != BrickUpdateFlags.None)
                {
                    updated = i;
                }
            }

            Assert.That(removed, Is.GreaterThanOrEqualTo(0), $"no Removed entry for {removedKey}");
            Assert.That(updated, Is.GreaterThanOrEqualTo(0), $"no added Updated entry for {updatedKey}");
            Assert.That(removed, Is.LessThan(updated), "removals must precede the cycle's updates");
        }

        private struct TestBatchCommand
        {
            public int Count;
        }

        private struct TestQuery
        {
            public int Wanted;
        }

        private struct TestReply
        {
            public int Count;
        }

        private sealed class Rig : System.IDisposable
        {
            public readonly CaelixServer Server;
            public readonly CaelixWorld World;
            public readonly CaelixClient Client;
            public LocalChannel FirstClientChannel { get; private set; }

            private readonly List<CaelixClient> clients = new();

            public Rig()
            {
                Server = new CaelixServer { TickRate = 100f };
                World = Server.CreateWorld(CaelixWorldConfig.Default(0, "test"));
                Client = AddClient();
            }

            /// <summary>Every client connected to this rig's server, in connection order.</summary>
            public IReadOnlyList<CaelixClient> Clients => clients;

            /// <summary>Connects one more client through its own in-process channel pair.</summary>
            public CaelixClient AddClient()
            {
                LocalChannel.CreatePair(out LocalChannel serverEnd, out LocalChannel clientEnd);
                if (clients.Count == 0) FirstClientChannel = clientEnd;
                var client = new CaelixClient(clientEnd, Server.Types);
                Server.AddConnection(serverEnd);
                clients.Add(client);
                return client;
            }

            /// <summary>One server tick, then one client frame on the first client.</summary>
            public void Exchange()
            {
                Server.Step();
                Client.Receive();
                Client.PrepareRender();
                Client.EndFrame();
            }

            /// <summary>One server tick, then one client frame on every connected client.</summary>
            public void ExchangeAll()
            {
                Server.Step();
                for (int i = 0; i < clients.Count; i++)
                {
                    clients[i].Receive();
                    clients[i].PrepareRender();
                    clients[i].EndFrame();
                }
            }

            public void Dispose()
            {
                for (int i = 0; i < clients.Count; i++)
                {
                    clients[i].Dispose();
                }

                clients.Clear();
                Server.Dispose();
            }
        }

        private static readonly Guid128 EntityA = new(0x11111111u, 0x22222222u, 0x33333333u, 0x44444444u);
        private static readonly Guid128 EntityB = new(0x55555555u, 0x66666666u, 0x77777777u, 0x88888888u);

        [Test]
        public void InitialSync_ReplicatesEntitiesSectorsAndBlocks()
        {
            using var rig = new Rig();
            var pose = new RigidTransform(quaternion.identity, new float3(10f, 0f, -5f));
            rig.World.CreateEntity(EntityA, pose, isStatic: true, isProtected: true);
            rig.World.SetBlock(EntityA, new int3(1, 2, 3), new Block(0x8001));
            rig.World.SetBlock(EntityA, new int3(130, 2, 3), new Block(0x8002)); // second sector
            rig.World.SetBlock(EntityA, new int3(-1, 0, 0), new Block(0x8003));  // negative sector

            rig.Exchange();

            Assert.That(rig.Client.IsConnected, Is.True);
            Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView view), Is.True);
            Assert.That(view.IsStatic, Is.True);
            Assert.That(view.IsProtected, Is.True);
            Assert.That(view.HasBody, Is.False);
            Assert.That(math.all(view.Data.transform.pos == pose.pos), Is.True);
            Assert.That(view.Component, Is.Not.Null, "a view object is spawned for an entity the scene did not author");
            Assert.That(view.IsClientSpawned, Is.True);

            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
            Assert.That(view.Data.GetBlock(new int3(1, 2, 3)), Is.EqualTo(new Block(0x8001)));
            Assert.That(view.Data.GetBlock(new int3(130, 2, 3)), Is.EqualTo(new Block(0x8002)));
            Assert.That(view.Data.GetBlock(new int3(-1, 0, 0)), Is.EqualTo(new Block(0x8003)));
            Assert.That(view.Data.GetBlock(new int3(0, 0, 0)).isEmpty, Is.True);
        }

        [Test]
        public void Delta_OnlyChangedBricksArriveAndReplicaMatches()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            for (int x = 0; x < 16; x++)
            {
                rig.World.SetBlock(EntityA, new int3(x, 0, 0), new Block(0x8001));
            }

            rig.Exchange();
            rig.Client.World.TryGetView(EntityA, out EntityView view);

            // A quiet tick sends nothing for known sectors.
            rig.Exchange();

            rig.World.SetBlock(EntityA, new int3(3, 0, 0), Block.Empty);
            rig.World.SetBlock(EntityA, new int3(40, 40, 40), new Block(0x8009));
            rig.Exchange();

            Assert.That(view.Data.GetBlock(new int3(3, 0, 0)).isEmpty, Is.True);
            Assert.That(view.Data.GetBlock(new int3(4, 0, 0)), Is.EqualTo(new Block(0x8001)));
            Assert.That(view.Data.GetBlock(new int3(40, 40, 40)), Is.EqualTo(new Block(0x8009)));
            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
        }

        [Test]
        public void ClientFrame_MarksRendererFlagsOnlyForAppliedBricks()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(8, 8, 8), new Block(0x8001));
            rig.Exchange();
            rig.Client.World.TryGetView(EntityA, out EntityView view);

            rig.World.SetBlock(EntityA, new int3(9, 8, 8), new Block(0x8002));
            rig.Server.Step();
            rig.Client.Receive(); // apply messages
            rig.Client.PrepareRender(); // propagate render flags; EndFrame not yet called

            BrickUpdateFlags require = view.Data.GetRequiredFlags(new int3(1, 1, 1));
            Assert.That(require & BrickUpdateFlags.GeometryWithLocalNeighbor, Is.Not.EqualTo(BrickUpdateFlags.None));
            Assert.That(require & BrickUpdateFlags.Automata0, Is.EqualTo(BrickUpdateFlags.None));
            Assert.That(CountRequiredBricks(view.Data, RendererFlags), Is.Not.Zero,
                "the renderer has work to pick up");

            rig.Client.EndFrame();
            Assert.That(AnyBrickDirty(view.Data), Is.False, "the frame's dirty lifetime ended");
        }

        [Test]
        public void Commands_SetBlockAndStaticReachTheServer()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: false);
            rig.World.AddBody(EntityA);
            rig.Exchange();

            rig.Client.SetBlock(EntityA, new int3(2, 2, 2), new Block(0x8004));
            rig.Client.SetEntityStatic(EntityA, true);
            rig.Exchange(); // Step drains the inbox before ticking

            Assert.That(rig.World.GetBlock(EntityA, new int3(2, 2, 2)), Is.EqualTo(new Block(0x8004)));
            Assert.That(rig.World.GetEntity(EntityA).isStatic, Is.True);
            rig.Client.World.TryGetView(EntityA, out EntityView view);
            Assert.That(view.IsStatic, Is.True);
            Assert.That(view.HasBody, Is.True);
            Assert.That(view.Data.GetBlock(new int3(2, 2, 2)), Is.EqualTo(new Block(0x8004)));
        }

        [Test]
        public void PayloadCommand_ReachesHandlerWithTrailingBytes()
        {
            using var rig = new Rig();
            int sum = 0;
            int seenRemaining = -1;
            rig.Server.RegisterCommand<TestBatchCommand>(
                (ServerConnection connection, CaelixWorld world, in TestBatchCommand cmd, ref NetMessageReader payload) =>
                {
                    seenRemaining = payload.Remaining;
                    ReadOnlySpan<int> values =
                        MemoryMarshal.Cast<byte, int>(payload.ReadSpan(cmd.Count * sizeof(int)));
                    for (int i = 0; i < values.Length; i++)
                    {
                        sum += values[i];
                    }
                });

            var records = new[] { 1, 2, 3, 4 };
            rig.Client.SendCommand(
                new TestBatchCommand { Count = records.Length },
                MemoryMarshal.AsBytes(new ReadOnlySpan<int>(records)));
            rig.Server.ProcessIncoming();

            Assert.That(seenRemaining, Is.EqualTo(16), "the payload is exactly the trailing bytes");
            Assert.That(sum, Is.EqualTo(10));
        }

        [Test]
        public void TypedQuery_ReturnsReplyAndPayload()
        {
            using var rig = new Rig();
            rig.Server.RegisterQuery<TestQuery, TestReply>(
                (ServerConnection connection, CaelixWorld world, in TestQuery request,
                    ref NetMessageReader requestPayload, NetMessageWriter replyPayload) =>
                {
                    for (int i = 0; i < request.Wanted; i++)
                    {
                        replyPayload.Write(i);
                    }

                    return new TestReply { Count = request.Wanted };
                });

            int replyCount = -1;
            int[] replyValues = null;
            rig.Client.SendQuery<TestQuery, TestReply>(
                new TestQuery { Wanted = 5 },
                ReadOnlySpan<byte>.Empty,
                (ushort worldId, in TestReply reply, ref NetMessageReader payload) =>
                {
                    replyCount = reply.Count;
                    replyValues = MemoryMarshal.Cast<byte, int>(payload.ReadSpan(reply.Count * sizeof(int))).ToArray();
                });

            rig.Server.ProcessIncoming();
            rig.Client.Receive();
            rig.Client.PrepareRender();
            rig.Client.EndFrame();

            Assert.That(replyCount, Is.EqualTo(5));
            Assert.That(replyValues, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
        }

        [Test]
        public void SpawnEntityCommand_CreatesEntityWithBodyAndAcceptsSameFrameWrites()
        {
            using var rig = new Rig();
            rig.Client.SpawnEntity(EntityB, RigidTransform.identity, isStatic: false, hasBody: true);
            rig.Client.SetBlock(EntityB, new int3(1, 1, 1), new Block(0x8001));
            rig.Exchange();

            Assert.That(rig.World.HasEntity(EntityB), Is.True);
            Assert.That(rig.World.HasBody(EntityB), Is.True);
            Assert.That(rig.World.GetBlock(EntityB, new int3(1, 1, 1)), Is.EqualTo(new Block(0x8001)));

            Assert.That(rig.Client.World.TryGetView(EntityB, out EntityView view), Is.True);
            Assert.That(view.HasBody, Is.True);
            Assert.That(view.IsClientSpawned, Is.True);
            Assert.That(view.Data.GetBlock(new int3(1, 1, 1)), Is.EqualTo(new Block(0x8001)));

            // The guid is taken: a second spawn must not replace the entity that is already there.
            int entityCount = rig.World.EntityCount;
            rig.Client.SpawnEntity(EntityB, RigidTransform.identity, isStatic: true, hasBody: true);
            rig.Exchange();
            Assert.That(rig.World.EntityCount, Is.EqualTo(entityCount));
            Assert.That(rig.World.GetEntity(EntityB).isStatic, Is.False);
        }

        [Test]
        public void ProtectedEntity_RefusesUnfreezeCommand()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true, isProtected: true);
            rig.Exchange();

            rig.Client.SetEntityStatic(EntityA, false);
            rig.Server.ProcessIncoming();
            Assert.That(rig.World.GetEntity(EntityA).isStatic, Is.True);
        }

        [Test]
        public void Despawn_RemovesViewAndDestroysSpawnedObject()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(1, 1, 1), new Block(0x8001));
            rig.Exchange();
            rig.Client.World.TryGetView(EntityA, out EntityView view);
            GameObject go = view.Component.gameObject;

            rig.World.RemoveEntity(EntityA);
            rig.Exchange();

            Assert.That(rig.Client.World.TryGetView(EntityA, out _), Is.False);
            Assert.That(rig.Client.World.Views.Count, Is.EqualTo(0));
            Assert.That(go == null, Is.True, "client-spawned view object is destroyed");
        }

        [Test]
        public void BodyProperties_UpdateOnServerTicksWithoutClientFrames()
        {
            using var rig = new Rig();
            rig.World.Config.physics.gravity = float3.zero;
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: false);
            rig.World.AddBody(EntityA);
            rig.World.SetBlock(EntityA, new int3(8, 8, 8), new Block(0x8001));

            // No view or VoxelBody component exists while the server computes these properties.
            rig.Server.Step();
            Assert.That(rig.Client.World.Views.Count, Is.Zero);
            Assert.That(rig.World.TryGetBody(EntityA, out var initial), Is.True);
            float initialMass = initial.massProperties.mass;
            float3 initialInertia = initial.massProperties.inertiaTensor;
            Assert.That(initialMass, Is.GreaterThan(0));
            Assert.That(initial.massProperties.centerOfMass, Is.EqualTo(new float3(8.5f)));

            rig.Client.SetBlock(EntityA, new int3(10, 8, 8), new Block(0x8001));
            rig.Server.Step();
            Assert.That(rig.World.TryGetBody(EntityA, out var expanded), Is.True);
            Assert.That(expanded.massProperties.mass, Is.EqualTo(initialMass * 2).Within(0.0001f));
            Assert.That(expanded.massProperties.centerOfMass, Is.EqualTo(new float3(9.5f, 8.5f, 8.5f)));
            Assert.That(expanded.massProperties.inertiaTensor.y, Is.GreaterThan(initialInertia.y));

            rig.Client.SetBlock(EntityA, new int3(10, 8, 8), Block.Empty);
            rig.Server.Step();
            Assert.That(rig.World.TryGetBody(EntityA, out var reduced), Is.True);
            Assert.That(reduced.massProperties.mass, Is.EqualTo(initialMass).Within(0.0001f));
            Assert.That(math.distance(reduced.massProperties.inertiaTensor, initialInertia), Is.LessThan(0.0001f));

            rig.Client.Receive();
            rig.Client.PrepareRender();
            Assert.That(rig.Client.World.TryGetView(EntityA, out var view), Is.True);
            Assert.That(view.HasBody, Is.True);
            Assert.That(view.Component.GetComponent<VoxelBody>(), Is.Null,
                "replicated body presence is carried by EntityView, without an authoring component");
            Assert.That(AnyBrickHasSlot(rig.World.GetEntity(EntityA), SectorSlotId.PhysicsInfo), Is.True);
            Assert.That(AnyBrickHasSlot(view.Data, SectorSlotId.PhysicsInfo), Is.False,
                "client presentation must not allocate derived physics data");
            rig.Client.EndFrame();

            rig.World.RemoveBody(EntityA);
            rig.Exchange();
            Assert.That(view.HasBody, Is.False);
            Assert.That(view.Component.GetComponent<VoxelBody>(), Is.Null);
            rig.World.AddBody(EntityA);
            rig.Exchange();
            Assert.That(view.HasBody, Is.True);
            Assert.That(view.Component.GetComponent<VoxelBody>(), Is.Null);
        }

        [Test]
        public void Drag_PullsBodyTowardTargetAndTimesOut()
        {
            using var rig = new Rig();
            rig.World.Config.physics.gravity = float3.zero;
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: false);
            rig.World.AddBody(EntityA);
            rig.World.SetBlock(EntityA, new int3(0, 0, 0), new Block(0x8001));
            rig.Exchange();

            rig.Client.SetDrag(new DragCommand
            {
                Entity = EntityA,
                AnchorLocal = new float3(0.5f, 0.5f, 0.5f),
                TargetWorld = new float3(5.5f, 0.5f, 0.5f),
                Spring = 50f,
                Damping = 10f,
                MaxAcceleration = 100f,
            });

            rig.Server.Step(30); // 0.3 s at 100 TPS, inside the 0.5 s timeout
            Assert.That(rig.World.DragCount, Is.EqualTo(1));
            float3 pulled = rig.World.GetEntity(EntityA).transform.pos;
            Assert.That(pulled.x, Is.GreaterThan(0.05f), "spring pulls toward +X");
            Assert.That(math.abs(pulled.y) + math.abs(pulled.z), Is.LessThan(0.05f), "no off-axis drift");

            rig.Server.Step(60); // no refresh: past the timeout, the drag is dropped
            Assert.That(rig.World.DragCount, Is.EqualTo(0));

            rig.Client.ReleaseDrag(EntityA);
            rig.Server.Step();
            Assert.That(rig.World.DragCount, Is.EqualTo(0));
        }

        [Test]
        public void Drag_AnchorAtCenterOfMass_PullsTowardTarget()
        {
            using var rig = new Rig();
            rig.World.Config.physics.gravity = float3.zero;
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: false);
            rig.World.AddBody(EntityA);
            rig.World.SetBlock(EntityA, new int3(0, 0, 0), new Block(0x8001));
            rig.Exchange();

            rig.Client.SetDrag(new DragCommand
            {
                Entity = EntityA,
                AnchorLocal = float3.zero, // deliberately wrong; the flag must make the server ignore it
                AnchorAtCenterOfMass = 1,
                TargetWorld = new float3(5.5f, 0.5f, 0.5f),
                Spring = 50f,
                Damping = 10f,
                MaxAcceleration = 100f,
            });

            rig.Server.Step(30);
            Assert.That(rig.World.DragCount, Is.EqualTo(1));
            float3 pulled = rig.World.GetEntity(EntityA).transform.pos;
            Assert.That(pulled.x, Is.GreaterThan(0.05f), "spring pulls toward +X");
            Assert.That(math.abs(pulled.y) + math.abs(pulled.z), Is.LessThan(0.05f), "no off-axis drift");
        }

        [Test]
        public void Drag_IgnoresProtectedEntities()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: false, isProtected: true);
            rig.World.AddBody(EntityA);
            rig.Exchange();

            rig.Client.SetDrag(new DragCommand { Entity = EntityA, TargetWorld = new float3(5f, 0f, 0f), Spring = 50f });
            rig.Server.Step();
            Assert.That(rig.World.DragCount, Is.EqualTo(0));
        }

        [Test]
        public void Events_ReachRegisteredClientHandler()
        {
            using var rig = new Rig();
            var received = new List<TestEvent>();
            rig.Client.RegisterEvent<TestEvent>((worldId, evt) => received.Add(evt));

            rig.World.EmitEvent(new TestEvent { Value = 5, Position = new float3(1f, 2f, 3f) });
            rig.Exchange();

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0].Value, Is.EqualTo(5));
            Assert.That(math.all(received[0].Position == new float3(1f, 2f, 3f)), Is.True);
        }

        [Test]
        public void Query_ReturnsNonReplicatedSlot()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(5, 5, 5), new Block(0x8001));
            rig.World.SetSlot(EntityA, (SectorSlotId)2, new int3(5, 5, 5), (short)77);
            rig.Exchange();

            VoxelQueryReply reply = null;
            rig.Client.QueryVoxel(EntityA, new int3(5, 5, 5), 0xFFFF, r => reply = r);
            rig.Server.ProcessIncoming();
            rig.Client.Receive();
            rig.Client.PrepareRender();
            rig.Client.EndFrame();

            Assert.That(reply, Is.Not.Null);
            Assert.That(reply.Found, Is.True);
            Assert.That(reply.TryGetSlot(SectorSlotId.Block, out Block block), Is.True);
            Assert.That(block, Is.EqualTo(new Block(0x8001)));
            Assert.That(reply.TryGetSlot((SectorSlotId)2, out short meta), Is.True);
            Assert.That(meta, Is.EqualTo(77));

            // The replica never received slot 2.
            rig.Client.World.TryGetView(EntityA, out EntityView view);
            Assert.That(AnyBrickHasSlot(view.Data, (SectorSlotId)2), Is.False);
        }

        [Test]
        public void SaveLoad_RoundTripsThroughWorldAndReplicates()
        {
            string path = System.IO.Path.Combine(Application.temporaryCachePath, "replication-test.cxw");
            using (var rig = new Rig())
            {
                rig.World.CreateEntity(EntityA, new RigidTransform(quaternion.identity, new float3(3f, 4f, 5f)), isStatic: false, isProtected: true);
                rig.World.AddBody(EntityA);
                rig.World.SetBlock(EntityA, new int3(7, 7, 7), new Block(0x8005));
                rig.World.Save(path);
            }

            using (var rig = new Rig())
            {
                rig.World.Load(path);
                rig.Exchange();
                Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView view), Is.True);
                Assert.That(view.HasBody, Is.True);
                Assert.That(view.IsProtected, Is.True);
                Assert.That(view.IsStatic, Is.False);
                Assert.That(view.Data.GetBlock(new int3(7, 7, 7)), Is.EqualTo(new Block(0x8005)));
                // The body is dynamic, so the one tick in Exchange applies gravity for 1/100 s.
                Assert.That(math.distance(view.Data.transform.pos, new float3(3f, 4f, 5f)), Is.LessThan(0.01f));
            }

            System.IO.File.Delete(path);
        }

        [Test]
        public void TwoClients_ShareDeltaAndLateJoinerGetsFullState()
        {
            using var rig = new Rig();
            CaelixClient second = rig.AddClient();

            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(1, 1, 1), new Block(0x8001));
            rig.World.SetBlock(EntityA, new int3(130, 1, 1), new Block(0x8002)); // second sector
            rig.ExchangeAll();

            AssertReplicaMatchesServer(rig, rig.Client);
            AssertReplicaMatchesServer(rig, second);

            rig.World.SetBlock(EntityA, new int3(1, 1, 1), new Block(0x8003));   // edit a known brick
            rig.World.SetBlock(EntityA, new int3(1, 1, 260), new Block(0x8004)); // a sector nobody has
            rig.ExchangeAll();

            AssertReplicaMatchesServer(rig, rig.Client);
            AssertReplicaMatchesServer(rig, second);

            // A late joiner is caught up from the same tick's batches, not from a replay.
            CaelixClient third = rig.AddClient();
            rig.ExchangeAll();
            AssertReplicaMatchesServer(rig, third);

            rig.World.SetBlock(EntityA, new int3(2, 1, 1), new Block(0x8005));
            rig.ExchangeAll();

            AssertReplicaMatchesServer(rig, rig.Client);
            AssertReplicaMatchesServer(rig, second);
            AssertReplicaMatchesServer(rig, third);

            Assert.That(third.World.TryGetView(EntityA, out EntityView thirdView), Is.True);
            Assert.That(thirdView.Data.GetBlock(new int3(2, 1, 1)), Is.EqualTo(new Block(0x8005)));
            Assert.That(thirdView.Data.GetBlock(new int3(1, 1, 1)), Is.EqualTo(new Block(0x8003)));
            Assert.That(thirdView.Data.GetBlock(new int3(1, 1, 260)), Is.EqualTo(new Block(0x8004)));
        }

        [Test]
        public void ChunkedReplication_JoinAndDeltaSurviveSmallChunks()
        {
            using var rig = new Rig();
            // One one-brick sector message is roughly 1.1 KB, so this packs about two per chunk.
            rig.Server.ReplicationChunkBytes = 2500;

            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            for (int x = 0; x < 12; x++)
            {
                rig.World.SetBlock(EntityA, new int3(x * 128, 0, 0), new Block(0x8001));
            }

            rig.Exchange();

            Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView view), Is.True);
            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
            Assert.That(rig.Server.FullBatchForTests.LastChunkCount, Is.GreaterThanOrEqualTo(4),
                "12 sectors at ~2 per chunk take several chunks");

            // Now every sector is known, so the same 12 sectors go out as a chunked delta.
            for (int x = 0; x < 12; x++)
            {
                rig.World.SetBlock(EntityA, new int3(x * 128 + 1, 1, 1), new Block(0x8002));
            }

            rig.Exchange();

            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
            Assert.That(rig.Server.DeltaBatchForTests.LastChunkCount, Is.GreaterThanOrEqualTo(4));
        }

        [Test]
        public void Delta_SkippedWhenEveryConnectionGotTheSectorInFull()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(0, 0, 0), new Block(0x8001));
            rig.World.SetBlock(EntityA, new int3(128, 0, 0), new Block(0x8002));
            rig.World.SetBlock(EntityA, new int3(256, 0, 0), new Block(0x8003));

            rig.Exchange();

            // The entity was new to the only connection, so the shared delta drops it instead of
            // packing bytes nobody sends.
            Assert.That(rig.Server.DeltaBatchForTests.EntityCount, Is.EqualTo(0));
            Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView view), Is.True);
            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
        }

        [Test]
        public void WorldLifecycle_AddAndRemoveReachTheClient()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(1, 1, 1), new Block(0x8001));
            rig.Exchange();

            CaelixWorld second = rig.Server.CreateWorld(CaelixWorldConfig.Default(1, "second"));
            second.CreateEntity(EntityB, RigidTransform.identity, isStatic: true);
            second.SetBlock(EntityB, new int3(2, 2, 2), new Block(0x8007));
            rig.Exchange();

            ClientWorld replica = FindClientWorld(rig.Client, 1);
            Assert.That(replica, Is.Not.Null, "WorldAdd creates the replica of the new world");
            Assert.That(replica.TryGetView(EntityB, out EntityView view), Is.True);
            Assert.That(view.Data.GetBlock(new int3(2, 2, 2)), Is.EqualTo(new Block(0x8007)));

            var removed = new List<ushort>();
            rig.Client.WorldRemoving += w => removed.Add(w.Id);
            rig.Server.RemoveWorld(second);
            rig.Exchange();

            Assert.That(removed.Count, Is.EqualTo(1));
            Assert.That(removed[0], Is.EqualTo(1));
            Assert.That(FindClientWorld(rig.Client, 1), Is.Null);

            ClientWorld defaultReplica = FindClientWorld(rig.Client, 0);
            Assert.That(defaultReplica, Is.Not.Null, "the default world is untouched");
            Assert.That(defaultReplica.TryGetView(EntityA, out _), Is.True);
        }

        [Test]
        public void FrozenServer_RunsExactlyOneTick()
        {
            using var rig = new Rig();
            rig.Server.Frozen = true;

            Assert.That(rig.Server.Tick(), Is.True, "the first tick runs even while frozen");
            Assert.That(rig.Server.Tick(), Is.False);
            Assert.That(rig.Server.Tick(), Is.False);
            Assert.That(rig.Server.TickIndex, Is.EqualTo(1u));

            rig.Server.Step();
            Assert.That(rig.Server.TickIndex, Is.EqualTo(2u), "Step ignores the freeze");

            rig.Server.Frozen = false;
            Assert.That(rig.Server.Tick(), Is.True);
            Assert.That(rig.Server.TickIndex, Is.EqualTo(3u));
        }

        [Test]
        public void Hello_ArrivesWithNoWorldHeader()
        {
            using var rig = new Rig();
            rig.Exchange();

            Assert.That(rig.Client.IsConnected, Is.True);
            Assert.That(rig.Client.Hello.TickRate, Is.EqualTo(100f));

            ClientWorld replica = FindClientWorld(rig.Client, 0);
            Assert.That(replica, Is.Not.Null, "the world arrives as WorldAdd, not inside Hello");
            Assert.That(replica.ReplicatedSlotMask, Is.EqualTo(BrickReplication.DefaultReplicatedSlotMask));
        }

        [Test]
        public void SaveReload_ExistingGuidRemovesExtraBricksAndSectors()
        {
            string path = System.IO.Path.Combine(Application.temporaryCachePath, $"replication-{Guid.NewGuid()}.cxw");
            using var rig = new Rig();
            try
            {
                rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
                rig.World.SetBlock(EntityA, new int3(32), new Block(0x8001));
                rig.World.Save(path);
                rig.Exchange();
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    rig.Client.World.TryGetView(EntityA, out EntityView previous);
                    rig.World.SetBlock(EntityA, new int3(48), new Block(0x8002));
                    rig.World.SetBlock(EntityA, new int3(256), new Block(0x8003));
                    rig.Exchange();
                    rig.World.Load(path);
                    rig.Exchange();
                    rig.Client.World.TryGetView(EntityA, out EntityView current);
                    Assert.That(current, Is.Not.SameAs(previous));
                    Assert.That(current.Data.GetBlock(new int3(48)).isEmpty, Is.True);
                    Assert.That(current.Data.GetBlock(new int3(256)).isEmpty, Is.True);
                    AssertReplicaMatchesServer(rig, rig.Client);
                }
            }
            finally { System.IO.File.Delete(path); }
        }

        [Test]
        public void SectorReplacement_BetweenStepsSendsRemovalBeforeFullContents()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(32), new Block(0x8001));
            rig.Exchange();
            rig.Client.World.TryGetView(EntityA, out EntityView view);
            var data = rig.World.GetEntity(EntityA);
            data.RemoveRegion(int3.zero);
            data.EnsureRegion(int3.zero);
            rig.World.SetBlock(EntityA, new int3(48), new Block(0x8002));

            rig.Server.Step();
            rig.Client.Receive();
            rig.Client.PrepareRender();
            AssertRemovedBeforeUpdated(view, new int3(4, 4, 4), new int3(6, 6, 6));
            rig.Client.EndFrame();

            Assert.That(view.Data.GetBlock(new int3(32)).isEmpty, Is.True);
            Assert.That(view.Data.GetBlock(new int3(48)), Is.EqualTo(new Block(0x8002)));
            AssertReplicaMatchesServer(rig, rig.Client);
        }

        [Test]
        public void EmptyServerSectorIsNotReplicated()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(32), new Block(0x8001));
            rig.World.GetEntity(EntityA).EnsureRegion(new int3(3, 0, 0));

            rig.Exchange();

            Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView view), Is.True);
            Assert.That(view.Data.HasRegion(new int3(3, 0, 0)), Is.False,
                "a region with no brick has nothing to replicate");
            AssertReplicaMatchesServer(rig, rig.Client);
        }

        [Test]
        public void Delta_RemoveThenAddSameKeyInOneTick_ClientEndsWithNewContents()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(32), new Block(0x8001));
            rig.Exchange();

            rig.World.GetEntity(EntityA).RemoveRegion(int3.zero);
            rig.World.SetBlock(EntityA, new int3(32), new Block(0x8002));
            rig.World.SetBlock(EntityA, new int3(40, 32, 32), new Block(0x8003));
            rig.Exchange();

            Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView view), Is.True);
            Assert.That(view.Data.GetBlock(new int3(32)), Is.EqualTo(new Block(0x8002)));
            Assert.That(view.Data.GetBlock(new int3(40, 32, 32)), Is.EqualTo(new Block(0x8003)));
            AssertReplicaMatchesServer(rig, rig.Client);
        }

        [Test]
        public void WorldReplacement_BetweenStepsResetsInstanceAndSlotMask()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(32), new Block(0x8001));
            rig.Exchange();
            ClientWorld oldReplica = rig.Client.World;
            var lifecycle = new List<string>();
            rig.Client.WorldRemoving += w => lifecycle.Add("remove");
            rig.Client.WorldAdded += w => lifecycle.Add("add");
            rig.Server.RemoveWorld(rig.World);
            var config = CaelixWorldConfig.Default();
            config.replicatedSlotMask |= 1 << 2;
            var replacement = rig.Server.CreateWorld(config);
            replacement.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            replacement.SetBlock(EntityA, new int3(48), new Block(0x8002));
            replacement.SetSlot(EntityA, (SectorSlotId)2, new int3(48), (short)77);
            rig.Exchange();
            Assert.That(lifecycle, Is.EqualTo(new[] { "remove", "add" }));
            Assert.That(oldReplica.IsDisposed, Is.True);
            Assert.That(rig.Client.World, Is.Not.SameAs(oldReplica));
            Assert.That(rig.Client.World.ReplicatedSlotMask, Is.EqualTo(config.replicatedSlotMask));
            rig.Client.World.TryGetView(EntityA, out EntityView view);
            Assert.That(view.Data.GetBlock(new int3(32)).isEmpty, Is.True);
            Assert.That(view.Data.GetSlot<short>((SectorSlotId)2, new int3(48)), Is.EqualTo(77));
            AssertBlockSlotsEqual(replacement.GetEntity(EntityA), view.Data);
        }

        [Test]
        public void ForgetWorld_ResetsExistingReplicaBeforeResending()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(32), new Block(0x8001));
            rig.Exchange();
            ClientWorld previous = rig.Client.World;
            rig.Server.Connections[0].ForgetWorld(0);
            rig.Exchange();
            Assert.That(previous.IsDisposed, Is.True);
            AssertReplicaMatchesServer(rig, rig.Client);
        }

        [Test]
        public void FrozenInputs_QueriesPassQueuedEditsUntilOneManualStep()
        {
            using var rig = new Rig();
            rig.Server.RegisterQuery<TestQuery, TestReply>(
                (ServerConnection c, CaelixWorld w, in TestQuery q, ref NetMessageReader payload, NetMessageWriter reply) =>
                    new TestReply { Count = w.GetEntity(EntityA).GetBlock(new int3(32)).data });
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(32), new Block(0x8001));
            rig.Exchange();
            rig.Server.Frozen = true;
            uint tick = rig.Server.TickIndex;
            VoxelQueryReply voxelReply = null;
            int typedValue = -1;
            rig.Client.SetBlock(EntityA, new int3(32), new Block(0x8002));
            rig.Client.QueryVoxel(EntityA, new int3(32), BrickReplication.DefaultReplicatedSlotMask, r => voxelReply = r);
            rig.Client.SetBlock(EntityA, new int3(32), new Block(0x8003));
            rig.Client.SendQuery<TestQuery, TestReply>(default, ReadOnlySpan<byte>.Empty,
                (ushort id, in TestReply r, ref NetMessageReader payload) => typedValue = r.Count);

            Assert.That(rig.Server.Tick(), Is.False);
            rig.Server.Update(1f);
            rig.Server.ProcessIncoming();
            rig.Client.Receive();
            Assert.That(rig.Server.TickIndex, Is.EqualTo(tick));
            Assert.That(rig.Server.Connections[0].Channel.PendingCount, Is.EqualTo(2), "edits remain in the channel");
            Assert.That(voxelReply, Is.Not.Null);
            Assert.That(voxelReply.TryGetSlot(SectorSlotId.Block, out Block block), Is.True);
            Assert.That(block, Is.EqualTo(new Block(0x8001)));
            Assert.That(typedValue, Is.EqualTo(0x8001));
            Assert.That(rig.World.GetEntity(EntityA).GetBlock(new int3(32)), Is.EqualTo(block));

            rig.Exchange(); // Step is unconditional even when frozen.
            Assert.That(rig.Server.TickIndex, Is.EqualTo(tick + 1));
            Assert.That(rig.Server.Connections[0].Channel.PendingCount, Is.Zero);
            Assert.That(rig.World.GetEntity(EntityA).GetBlock(new int3(32)), Is.EqualTo(new Block(0x8003)));
            AssertReplicaMatchesServer(rig, rig.Client);
        }

        [Test]
        public void FrozenInitialTick_LeavesCommandsQueued()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.Server.Frozen = true;
            rig.Client.SetBlock(EntityA, new int3(32), new Block(0x8001));
            Assert.That(rig.Server.Tick(), Is.True);
            Assert.That(rig.Server.Connections[0].Channel.PendingCount, Is.EqualTo(1));
            Assert.That(rig.World.GetEntity(EntityA).GetBlock(new int3(32)).isEmpty, Is.True);
            rig.Server.Step();
            Assert.That(rig.World.GetEntity(EntityA).GetBlock(new int3(32)), Is.EqualTo(new Block(0x8001)));
        }

        [Test]
        public void MultipleStepsBeforeClientFrame_ReplacementLeavesOnlyLatestEntity()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(32), new Block(0x8001));
            rig.Exchange();
            rig.World.RemoveEntity(EntityA);
            rig.Server.Step();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(48), new Block(0x8002));
            rig.Server.Step();
            rig.World.SetBlock(EntityA, new int3(64), new Block(0x8003));
            rig.Server.Step();
            rig.Client.Receive();
            rig.Client.PrepareRender();
            rig.Client.EndFrame();
            AssertReplicaMatchesServer(rig, rig.Client);
        }

        private static ClientWorld FindClientWorld(CaelixClient client, ushort worldId)
        {
            IReadOnlyList<ClientWorld> worlds = client.Worlds;
            for (int i = 0; i < worlds.Count; i++)
            {
                if (worlds[i].Id == worldId) return worlds[i];
            }

            return null;
        }

        [Test]
        public void SectorRemoval_InvalidatesSurvivingBoundaryWithoutCreatingSectors()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(127, 32, 32), new Block(0x8001));
            rig.World.SetBlock(EntityA, new int3(128, 32, 32), new Block(0x8001));
            rig.Exchange();
            rig.World.GetEntity(EntityA).RemoveRegion(new int3(1, 0, 0));
            rig.Server.Step();
            rig.Client.Receive();
            rig.Client.PrepareRender();
            rig.Client.World.TryGetView(EntityA, out EntityView view);
            // The brick facing the removed region has to re-render.
            Assert.That(
                view.Data.GetRequiredFlags(new int3(15, 4, 4)) & BrickUpdateFlags.GeometryWithLocalNeighbor,
                Is.Not.EqualTo(BrickUpdateFlags.None));
            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
            Assert.That(view.Data.HasRegion(new int3(1, 0, 0)), Is.False);
        }

        [Serializable]
        private sealed class ScaleSample
        {
            public string stage;
            public double elapsedSeconds;
            public long privateBytes;
            public long residentBytes;
            public long peakResidentBytes;
            public long managedBytes;
            public long unityAllocatedBytes;
            public long queuedBytes;
            public long peakQueuedBytes;
            public long receivedBytes;
            public long serverSectorBytes;
            public long replicaSectorBytes;
            public int entities;
            public int regions;
            public long bricks;
        }

        [Serializable]
        private sealed class ScaleReport
        {
            public string source;
            public int systemMemoryMB;
            public string processor;
            public string gpu;
            public List<ScaleSample> samples = new();
        }

#if UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint cb, pageFaultCount;
            public UIntPtr peakWorkingSet, workingSet, quotaPeakPaged, quotaPaged;
            public UIntPtr quotaPeakNonPaged, quotaNonPaged, pagefile, peakPagefile, privateUsage;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);
#endif

        /// <summary>
        /// Small by default. Set CAELIX_VALIDATION_SAVE to exercise a real save, or
        /// CAELIX_VALIDATION_SECTORS for 4096 allocated bricks per synthetic sector.
        /// CAELIX_VALIDATION_REPORT writes measured stages as JSON for manual scale runs.
        /// </summary>
        [Test]
        public void ReplicationScale_LoadSyncDeltaAndReloadMaintainExactBlockStorage()
        {
            using var rig = new Rig();
            string save = Environment.GetEnvironmentVariable("CAELIX_VALIDATION_SAVE");
            int sectorCount = int.TryParse(Environment.GetEnvironmentVariable("CAELIX_VALIDATION_SECTORS"), out int n)
                ? math.clamp(n, 1, 1024) : 2;
            var report = new ScaleReport
            {
                source = string.IsNullOrEmpty(save) ? $"synthetic: {sectorCount} sectors" : System.IO.Path.GetFileName(save),
                systemMemoryMB = SystemInfo.systemMemorySize,
                processor = SystemInfo.processorType,
                gpu = SystemInfo.graphicsDeviceName,
            };
            var timer = System.Diagnostics.Stopwatch.StartNew();
            CaptureScaleSample(rig, report, "empty", timer.Elapsed.TotalSeconds);
            if (!string.IsNullOrEmpty(save))
            {
                rig.World.Load(save);
            }
            else
            {
                rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
                var data = rig.World.GetEntity(EntityA);
                for (int s = 0; s < sectorCount; s++)
                {
                    int3 regionPos = new int3(s * 2, 0, 0);
                    data.EnsureRegion(regionPos);
                    int3 firstKey = VoxelRegion.FirstKeyOf(regionPos);
                    for (int bz = 0; bz < VoxelRegion.SizeInBricks; bz++)
                    for (int by = 0; by < VoxelRegion.SizeInBricks; by++)
                    for (int bx = 0; bx < VoxelRegion.SizeInBricks; bx++)
                    {
                        int3 key = firstKey + new int3(bx, by, bz);
                        data.SetBlock(BrickKey.ToBlockOrigin(key) + new int3(3), new Block(0x8001));
                    }
                }
            }
            CaptureScaleSample(rig, report, "loaded", timer.Elapsed.TotalSeconds);
            rig.Server.Step();
            CaptureScaleSample(rig, report, "sync-queued", timer.Elapsed.TotalSeconds);
            rig.Client.Receive();
            rig.Client.PrepareRender();
            rig.Client.EndFrame();
            // Sampled BEFORE the comparison: the stage times the client frame, and the
            // brick-by-brick comparison below costs seconds on a large world.
            CaptureScaleSample(rig, report, "sync-applied", timer.Elapsed.TotalSeconds);
            AssertAllBlockStorageEqual(rig.World, rig.Client.World);

            // Several server ticks can arrive before one client frame. Use a non-physics entity
            // to make the edit workload independent of the saved world's moving bodies.
            var editGuid = new Guid128(0xCAEFu, 0xFEEDu, 0xBAADu, 0x600Du);
            rig.World.CreateEntity(editGuid, RigidTransform.identity, isStatic: true);
            for (int tick = 0; tick < 4; tick++)
            {
                for (int b = 0; b < 1000; b++)
                {
                    int3 key = new int3(
                        b % VoxelRegion.SizeInBricks,
                        (b / VoxelRegion.SizeInBricks) % VoxelRegion.SizeInBricks,
                        b / (VoxelRegion.SizeInBricks * VoxelRegion.SizeInBricks));
                    rig.World.SetBlock(
                        editGuid, BrickKey.ToBlockOrigin(key) + new int3(3),
                        new Block((ushort)(0x8010 + tick)));
                }
                rig.Server.Step();
            }
            CaptureScaleSample(rig, report, "four-deltas-queued", timer.Elapsed.TotalSeconds);
            rig.Client.Receive();
            rig.Client.PrepareRender();
            rig.Client.EndFrame();
            // Sampled BEFORE the comparison: the stage times the client frame, and the
            // brick-by-brick comparison below costs seconds on a large world.
            CaptureScaleSample(rig, report, "four-deltas-applied", timer.Elapsed.TotalSeconds);
            AssertAllBlockStorageEqual(rig.World, rig.Client.World);

            if (!string.IsNullOrEmpty(save))
            {
                rig.World.Load(save);
                rig.Server.Step();
                CaptureScaleSample(rig, report, "reload-queued", timer.Elapsed.TotalSeconds);
                rig.Client.Receive();
                rig.Client.PrepareRender();
                rig.Client.EndFrame();
                // Sampled BEFORE the comparison: the stage times the client frame, and the
                // brick-by-brick comparison below costs seconds on a large world.
                CaptureScaleSample(rig, report, "reload-applied", timer.Elapsed.TotalSeconds);
                AssertAllBlockStorageEqual(rig.World, rig.Client.World);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            CaptureScaleSample(rig, report, "after-gc", timer.Elapsed.TotalSeconds);
            rig.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            CaptureScaleSample(rig, report, "disposed-after-gc", timer.Elapsed.TotalSeconds, disposed: true);
            string json = JsonUtility.ToJson(report, true);
            TestContext.Out.WriteLine(json);
            string output = Environment.GetEnvironmentVariable("CAELIX_VALIDATION_REPORT");
            if (!string.IsNullOrEmpty(output)) System.IO.File.WriteAllText(output, json);
        }

        private static void CaptureScaleSample(Rig rig, ScaleReport report, string stage, double elapsed, bool disposed = false)
        {
            var sample = new ScaleSample
            {
                stage = stage,
                elapsedSeconds = elapsed,
                privateBytes = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64,
                managedBytes = GC.GetTotalMemory(false),
                unityAllocatedBytes = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(),
                queuedBytes = rig.FirstClientChannel.PendingBytes,
                peakQueuedBytes = rig.FirstClientChannel.PeakPendingBytes,
                receivedBytes = rig.FirstClientChannel.TotalReceivedBytes,
                entities = disposed ? 0 : rig.World.EntityCount,
            };
#if UNITY_EDITOR_WIN
            // Unity's Mono returns zero for Process.PrivateMemorySize64 on this Editor version.
            if (!GetProcessMemoryInfo(GetCurrentProcess(), out var counters,
                    (uint)Marshal.SizeOf<ProcessMemoryCounters>()))
                Assert.Fail("Could not read Windows process memory counters");
            sample.privateBytes = (long)counters.privateUsage.ToUInt64();
            sample.residentBytes = (long)counters.workingSet.ToUInt64();
            sample.peakResidentBytes = (long)counters.peakWorkingSet.ToUInt64();
#endif
            report.samples.Add(sample);
            if (disposed) return;
            // Byte totals come from the facade's per-entity accounting, which reports whole
            // kilobytes; the sample is a diagnostic, not an exact allocator figure.
            foreach (var entity in rig.World.Data.VoxelEntities)
            {
                sample.regions += entity.Value.RegionCount;
                sample.bricks += entity.Value.AllocatedBrickCount;
                sample.serverSectorBytes += (long)entity.Value.GetHostMemoryUsageKB() * 1024;
            }
            foreach (var world in rig.Client.Worlds)
            foreach (var view in world.Views)
                sample.replicaSectorBytes += (long)view.Data.GetHostMemoryUsageKB() * 1024;
        }

        private static void AssertAllBlockStorageEqual(CaelixWorld server, ClientWorld replica)
        {
            Assert.That(replica.Views.Count, Is.EqualTo(server.EntityCount));
            foreach (var entity in server.Data.VoxelEntities)
            {
                Assert.That(replica.TryGetView(entity.Key, out EntityView view), Is.True);
                AssertBlockSlotsEqual(entity.Value, view.Data);
            }
        }

        private static void AssertReplicaMatchesServer(Rig rig, CaelixClient client)
        {
            Assert.That(client.World.TryGetView(EntityA, out EntityView view), Is.True);
            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
        }

        /// <summary>
        /// The replica must hold exactly the server's ALLOCATED bricks, byte for byte. Sector sets
        /// are deliberately not compared: brick records are the only thing replication sends, so a
        /// server sector that holds no brick — a neighbour that propagation created, or an explicit
        /// <c>AddEmptySectorAt</c> — has nothing to replicate and never reaches the client.
        /// </summary>
        /// <summary>Require-update bits that make a brick worth a render job.</summary>
        private const BrickUpdateFlags RendererFlags =
            BrickUpdateFlags.GeometryWithLocalNeighbor | BrickUpdateFlags.BlockBrickAdded | BrickUpdateFlags.BlockBrickRemoved;

        /// <summary>Allocated bricks of one region.</summary>
        private static int CountBricksInRegion(VoxelEntityData data, int3 regionPos)
        {
            int count = 0;
            foreach (int3 unused in data.EnumerateBricks(
                         VoxelRegion.FirstKeyOf(regionPos), VoxelRegion.LastKeyOf(regionPos)))
            {
                count++;
            }

            return count;
        }

        /// <summary>Bricks whose require-update flags meet <paramref name="mask"/>.</summary>
        private static int CountRequiredBricks(VoxelEntityData data, BrickUpdateFlags mask)
        {
            var bricks = new Unity.Collections.NativeList<RequiredBrick>(16, Unity.Collections.Allocator.TempJob);
            data.CollectRequiredBricks(default, mask, includeEmpty: false, bricks);
            int count = bricks.Length;
            bricks.Dispose();
            return count;
        }

        /// <summary>True while any allocated brick still carries dirty flags.</summary>
        private static bool AnyBrickDirty(VoxelEntityData data)
        {
            foreach (BrickDirtyFlags unused in data.EnumerateBrickDirtyFlags(BrickUpdateFlags.All, dirtyOnly: true))
            {
                return true;
            }

            return false;
        }

        /// <summary>True when any allocated brick of the entity has storage for one slot.</summary>
        private static unsafe bool AnyBrickHasSlot(VoxelEntityData data, SectorSlotId slot)
        {
            foreach (int3 key in data.EnumerateBricks())
            {
                if (data.TryBindBrick(slot, key, out byte* _))
                {
                    return true;
                }
            }

            return false;
        }

        private static unsafe void AssertBlockSlotsEqual(VoxelEntityData server, VoxelEntityData replica)
        {
            int serverBricks = 0;
            foreach (int3 key in server.EnumerateBricks())
            {
                serverBricks++;
                Assert.That(server.TryBindBrick(SectorSlotId.Block, key, out Block* a), Is.True);
                Assert.That(replica.TryBindBrick(SectorSlotId.Block, key, out Block* b), Is.True,
                    $"the replica is missing brick {key}");
                Assert.That(
                    Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCmp(
                        a, b, BrickKey.BlocksInBrick * sizeof(Block)),
                    Is.Zero, $"brick {key} differs");
            }

            int replicaBricks = 0;
            foreach (int3 key in replica.EnumerateBricks())
            {
                replicaBricks++;
                Assert.That(server.IsBrickAllocated(key), Is.True, $"the replica has an extra brick {key}");
            }

            Assert.That(replicaBricks, Is.EqualTo(serverBricks));
        }
    }

    public class HostIntegrationTests
    {
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator AuthoredBody_ClientForcesWaitForServerStep()
        {
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            yield return new UnityEngine.TestTools.EnterPlayMode();
            float oldScale = Time.timeScale;
            float oldFixed = Time.fixedDeltaTime;
            var root = new GameObject("body-command-test");
            try
            {
                Time.timeScale = 0;
                var host = root.AddComponent<CaelixHost>();
                host.World.Config.physics.gravity = float3.zero;
                var authoredObject = new GameObject("authored-body");
                authoredObject.transform.SetParent(root.transform);
                var entity = authoredObject.AddComponent<VoxelEntity>();
                entity.IsStatic = false;
                var component = authoredObject.AddComponent<VoxelBody>();
                entity.SetBlock(new int3(8), new Block(0x8001));
                host.Step();
                yield return null;

                Assert.That(host.World.TryGetBody(entity.PersistentGuid, out var before), Is.True);
                Assert.That(before.massProperties.mass, Is.GreaterThan(0));
                component.AddForce(Vector3.right, VoxelBodyForceMode.VelocityChange);
                yield return null;
                Assert.That(host.World.TryGetBody(entity.PersistentGuid, out var queued), Is.True);
                Assert.That(queued.motionVelocity.LinearVelocity, Is.EqualTo(before.motionVelocity.LinearVelocity));
                Assert.That(host.Server.Connections[0].Channel.PendingCount, Is.EqualTo(1));

                host.Step();
                Assert.That(host.World.TryGetBody(entity.PersistentGuid, out var stepped), Is.True);
                Assert.That(stepped.motionVelocity.LinearVelocity.x, Is.GreaterThan(0.9f));
                component.enabled = false;
                Assert.That(host.World.HasBody(entity.PersistentGuid), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                Time.timeScale = oldScale;
                Time.fixedDeltaTime = oldFixed;
            }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator PausedHost_QueriesRunAtZeroTimeScaleAndAuthoredViewsSurviveReload()
        {
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            yield return new UnityEngine.TestTools.EnterPlayMode();
            float oldScale = Time.timeScale;
            float oldFixed = Time.fixedDeltaTime;
            var root = new GameObject("host-integration-test");
            string save = System.IO.Path.Combine(Application.temporaryCachePath,
                "host-integration-" + System.Guid.NewGuid() + ".cxw");
            try
            {
                Time.timeScale = 0;
                var host = root.AddComponent<CaelixHost>();
                var authoredObject = new GameObject("authored-entity");
                authoredObject.transform.SetParent(root.transform);
                var authored = authoredObject.AddComponent<VoxelEntity>();
                var guid = authored.PersistentGuid;
                int3 p = new int3(32);
                authored.SetBlock(p, new Block(0x8001));
                host.Step();
                yield return null;
                yield return null;
                Assert.That(authored.View, Is.Not.Null);
                Assert.That(authored.View.Component, Is.SameAs(authored));
                host.Save(save);

                for (int cycle = 0; cycle < 3; cycle++)
                {
                    uint tick = host.Server.TickIndex;
                    VoxelQueryReply reply = null;
                    host.Client.SetBlock(guid, p, new Block(0x8002));
                    host.Client.QueryVoxel(guid, p, BrickReplication.DefaultReplicatedSlotMask, r => reply = r);
                    host.Client.SetBlock(guid, new int3(256), new Block(0x8003));
                    for (int frame = 0; frame < 10 && reply == null; frame++) yield return null;
                    Assert.That(reply, Is.Not.Null, "Host.Update must service queries without FixedUpdate");
                    Assert.That(reply.TryGetSlot(SectorSlotId.Block, out Block queried), Is.True);
                    Assert.That(queried, Is.EqualTo(new Block(0x8001)));
                    Assert.That(host.Server.TickIndex, Is.EqualTo(tick));
                    Assert.That(host.Server.Connections[0].Channel.PendingCount, Is.EqualTo(2));
                    Assert.That(authored.GetBlock(p), Is.EqualTo(new Block(0x8001)));
                    host.Step();
                    yield return null;
                    Assert.That(host.Server.TickIndex, Is.EqualTo(tick + 1));
                    Assert.That(authored.GetBlock(p), Is.EqualTo(new Block(0x8002)));
                    Assert.That(authored.View.Data.GetBlock(new int3(256)), Is.EqualTo(new Block(0x8003)));
                    host.Load(save);
                    host.Step();
                    yield return null;
                    Assert.That(authored.View.Component, Is.SameAs(authored));
                    Assert.That(authored.View.Data.GetBlock(p), Is.EqualTo(new Block(0x8001)));
                    Assert.That(authored.View.Data.GetBlock(new int3(256)).isEmpty, Is.True);
                }

                var oldWorld = host.World;
                host.Server.RemoveWorld(oldWorld);
                var replacement = host.Server.CreateWorld(CaelixWorldConfig.Default());
                replacement.CreateEntity(guid, RigidTransform.identity, isStatic: true);
                replacement.SetBlock(guid, p, new Block(0x8004));
                host.Step();
                yield return null;
                Assert.That(authored.ServerWorld, Is.SameAs(replacement));
                Assert.That(authored.HasServerData, Is.True);
                Assert.That(authored.View.Component, Is.SameAs(authored));
                authored.SetBlock(p, new Block(0x8005));
                Assert.That(replacement.GetBlock(guid, p), Is.EqualTo(new Block(0x8005)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                Time.timeScale = oldScale;
                Time.fixedDeltaTime = oldFixed;
                if (System.IO.File.Exists(save)) System.IO.File.Delete(save);
            }
        }

        [UnityEngine.TestTools.UnityTearDown]
        public System.Collections.IEnumerator LeavePlayMode()
        {
            if (Application.isPlaying) yield return new UnityEngine.TestTools.ExitPlayMode();
        }
    }
}
