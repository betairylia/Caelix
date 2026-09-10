using System;
using System.Diagnostics;
using NUnit.Framework;
using Caelix.Client;
using Caelix.Net;
using Caelix.Rendering.RayQuery;
using Caelix.Utils;
using Unity.Mathematics;

namespace Caelix.Tests
{
    /// <summary>Opt-in receive-only measurements; run with Burst enabled and synchronous compilation.</summary>
    public class ClientReceivePerformanceTests
    {
        [TestCase(1, 1)]
        [TestCase(1, 1024)]
        [TestCase(128, 1)]
        [TestCase(128, 1024)]
        [Explicit("Receive microbenchmark: excludes sending, rendering and server simulation.")]
        public void Receive_InitialAndQueuedDeltaMilliseconds(int sectorCount, int bricksPerSector)
        {
            var guid = new Guid128(101, 102, 103, 104);
            var packets = new byte[sectorCount][];
            var writer = new NetMessageWriter();
            for (int s = 0; s < sectorCount; s++)
            {
                writer.Reset();
                NetHeader.Write(writer, NetMessageType.BrickBatch, 0, 1);
                writer.Write(new BrickBatchHeader { Guid = guid, BrickCount = bricksPerSector });
                int3 sectorOrigin = VoxelRegion.FirstKeyOf(new int3(s, 0, 0));
                for (int b = 0; b < bricksPerSector; b++)
                {
                    // Same x-fastest brick layout the storage region uses; see RenderGroupTests.
                    writer.Write(sectorOrigin + RenderGroup.Default.LocalBrickPos(b));
                    writer.Write((byte)BrickOp.Update);
                    writer.Write((byte)1);
                    writer.Write((byte)SectorSlotId.Block);
                    writer.Write((ushort)2);
                    for (int v = 0; v < BrickKey.BlocksInBrick; v++) writer.Write((ushort)(0x8001 + s));
                }
                packets[s] = writer.AsSpan().ToArray();
            }

            var initial = new double[7];
            var deltas = new double[7];
            // First run warms Burst jobs; exclude it from the reported medians.
            for (int run = -1; run < initial.Length; run++)
            {
                LocalChannel.CreatePair(out LocalChannel sender, out LocalChannel receiver);
                using (sender)
                using (var client = new CaelixClient(receiver))
                {
                    writer.Reset();
                    NetHeader.Write(writer, NetMessageType.EntitySpawn, 0, 0);
                    writer.Write(new EntitySpawnMessage { Guid = guid, Transform = RigidTransform.identity, IsStatic = 1 });
                    sender.Send(NetDelivery.Reliable, writer.AsSpan());
                    client.Receive();
                    foreach (byte[] packet in packets) sender.Send(NetDelivery.Reliable, packet);
                    var timer = Stopwatch.StartNew();
                    client.Receive();
                    timer.Stop();
                    if (run >= 0) initial[run] = timer.Elapsed.TotalMilliseconds;
                    client.EndFrame();
                    for (int tick = 0; tick < 3; tick++)
                        foreach (byte[] packet in packets) sender.Send(NetDelivery.Reliable, packet);
                    timer.Restart();
                    client.Receive();
                    timer.Stop();
                    if (run >= 0) deltas[run] = timer.Elapsed.TotalMilliseconds;
                    Assert.That(client.World.TryGetView(guid, out EntityView view), Is.True);
                    for (int s = 0; s < sectorCount; s++)
                    {
                        int3 regionPos = new int3(s, 0, 0);
                        int allocated = 0;
                        foreach (int3 unused in view.Data.EnumerateBricks(
                                     VoxelRegion.FirstKeyOf(regionPos), VoxelRegion.LastKeyOf(regionPos)))
                        {
                            allocated++;
                        }

                        Assert.That(allocated, Is.EqualTo(bricksPerSector));
                        int3 p = BrickKey.ToBlockOrigin(
                            VoxelRegion.FirstKeyOf(regionPos) +
                            RenderGroup.Default.LocalBrickPos(bricksPerSector - 1)) + new int3(7);
                        Assert.That(view.Data.GetBlock(p), Is.EqualTo(new Block((ushort)(0x8001 + s))));
                    }
                    client.EndFrame();
                }
            }
            Array.Sort(initial);
            Array.Sort(deltas);
            TestContext.Out.WriteLine($"Receive benchmark: sectors={sectorCount}, bricks/sector={bricksPerSector}, " +
                $"Burst={Unity.Burst.BurstCompiler.IsEnabled}, workers={Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount}, " +
                $"initial median ms={initial[3]:F4}, three queued deltas median ms={deltas[3]:F4}");
        }
    }
}
