using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Caelix.Client;
using Caelix.Net;
using Caelix.Utils;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// The scene feed of <see cref="CaelixNativeRenderer"/>: what the plugin knows of every client
    /// entity, the change lists collected per frame, and the frame packet they are written into.
    /// </summary>
    /// <remarks>
    /// <see cref="CollectChanges"/> diffs the client world's entity views and reads their change
    /// lists (call it once per frame, after the client's <c>PrepareRender</c> and before
    /// <c>EndFrame</c>: the lists are read here and nowhere cleared). <see cref="FillFramePacket"/>
    /// writes the entity records (removes first, then every entity with something to say), the
    /// brick removes and, up to a budget, the waiting bricks, copied from the client store by a
    /// Burst job. Brick data is COPIED in the same frame it is bound (the phase rule of the store),
    /// because the render thread runs after the next server tick may have changed the storage.
    /// </remarks>
    internal sealed class CaelixNativeEntityFeed : IDisposable
    {
        /// <summary>Bytes of one brick in a packet: its key and its 512 block values.</summary>
        public const int BrickPacketBytes = 12 + 1024;

        /// <summary>The header fields of a frame packet that come from the session.</summary>
        public struct FrameConfig
        {
            /// <summary>The first packet of a session: carries the config and the material entries.</summary>
            public bool Reset;
            public uint Encoding;
            public uint StagingMiB;
            /// <summary>Material entries sent with the reset (block-ID encoding only); may be null.</summary>
            public CaelixNativeMaterialEntry[] Materials;
            /// <summary>Light tree options of the session (the plugin reads them with the reset only).</summary>
            public CaelixNativeLightOptions Light;
        }

        /// <summary>What the plugin knows of one client entity view.</summary>
        private sealed class EntityState
        {
            public EntityView View;
            public Guid128 Guid;
            public bool NeedsAdd = true;
            public RigidTransform SentTransform;
            /// <summary>Keys to upload, oldest first; [Head, Length) are still waiting.</summary>
            public UnsafeList<int3> PendingUploads;
            public int Head;
            public UnsafeList<int3> PendingRemoves;
            public int Stamp;
            public int PacketIndex = -1;

            public int UploadsWaiting => PendingUploads.Length - Head;

            public void Dispose()
            {
                if (PendingUploads.IsCreated) PendingUploads.Dispose();
                if (PendingRemoves.IsCreated) PendingRemoves.Dispose();
            }
        }

        private readonly Dictionary<EntityView, EntityState> entities = new Dictionary<EntityView, EntityState>();
        private readonly List<EntityState> entityOrder = new List<EntityState>();
        private readonly List<Guid128> pendingEntityRemoves = new List<Guid128>();
        private readonly List<EntityView> removedScratch = new List<EntityView>();
        private NativeHashSet<int3> frameDedupe;
        private int entityStamp;

        // Stream tracking (logs one line when a burst of uploads has become traceable).
        private bool streaming;
        private int streamStartFrame;
        private long streamStartTicks;
        private double streamTickMsTotal;
        private double streamTickMsMax;
        private int streamTicks;
        private long streamBricks;
        private ulong settleFrames;

        /// <summary>Bricks waiting on the C# side (not copied into a frame packet yet), over every entity.</summary>
        public long BricksPending { get; private set; }

        /// <summary>Bricks copied into frame packets since this feed was created.</summary>
        public long BricksSentTotal { get; private set; }

        /// <summary>Entities the feed knows.</summary>
        public int EntityCount => entityOrder.Count;

        /// <summary>True while a burst of uploads is on its way (see <see cref="TrackStream"/>).</summary>
        public bool Streaming => streaming;

        public CaelixNativeEntityFeed()
        {
            frameDedupe = new NativeHashSet<int3>(1024, Allocator.Persistent);
        }

        public void Dispose()
        {
            Forget();
            if (frameDedupe.IsCreated) frameDedupe.Dispose();
            streaming = false;
        }

        /// <summary>Forgets every entity; the next <see cref="CollectChanges"/> sends them all as new.</summary>
        public void Forget()
        {
            foreach (EntityState state in entityOrder) state.Dispose();
            entities.Clear();
            entityOrder.Clear();
            pendingEntityRemoves.Clear();
            BricksPending = 0;
        }

        /// <summary>Diffs the entity views of <paramref name="source"/> and reads their change lists into the entity states.</summary>
        public void CollectChanges(ClientWorld source)
        {
            IReadOnlyList<EntityView> list = source.Views;
            entityStamp++;
            for (int v = 0; v < list.Count; v++)
            {
                EntityView view = list[v];
                if (!entities.TryGetValue(view, out EntityState state))
                {
                    state = new EntityState
                    {
                        View = view,
                        Guid = view.Guid,
                        PendingUploads = new UnsafeList<int3>(64, Allocator.Persistent),
                        PendingRemoves = new UnsafeList<int3>(16, Allocator.Persistent),
                    };
                    // Everything the view already has; its change list this frame is covered by it.
                    foreach (int3 key in view.Data.EnumerateBricks())
                    {
                        state.PendingUploads.Add(key);
                    }
                    entities.Add(view, state);
                    entityOrder.Add(state);
                    state.Stamp = entityStamp;
                    continue;
                }
                state.Stamp = entityStamp;

                int count = view.Data.ChangeCount;
                if (count == 0) continue;
                NativeArray<BrickChange>.ReadOnly changes = view.Data.Changes;
                frameDedupe.Clear();
                for (int i = 0; i < changes.Length; i++)
                {
                    BrickChange change = changes[i];
                    if (change.Kind == ChangeKind.Removed)
                    {
                        state.PendingRemoves.Add(change.Key);
                        // A later Updated entry of the same key (removed, then allocated again)
                        // must still be uploaded.
                        frameDedupe.Remove(change.Key);
                    }
                    else if ((change.DirtyFlags & BrickReplication.ReplicationDirtyMask) != 0 && frameDedupe.Add(change.Key))
                    {
                        state.PendingUploads.Add(change.Key);
                    }
                }
            }

            // Views that are gone: one remove record each. Their data may be disposed already.
            removedScratch.Clear();
            foreach (var kvp in entities)
            {
                if (kvp.Value.Stamp != entityStamp) removedScratch.Add(kvp.Key);
            }
            foreach (EntityView view in removedScratch)
            {
                EntityState state = entities[view];
                // A view that never reached the plugin needs no remove.
                if (!state.NeedsAdd) pendingEntityRemoves.Add(state.Guid);
                state.Dispose();
                entities.Remove(view);
                entityOrder.Remove(state);
            }
            UpdateBricksPending();
        }

        private void UpdateBricksPending()
        {
            long waiting = 0;
            foreach (EntityState state in entityOrder) waiting += state.UploadsWaiting;
            BricksPending = waiting;
        }

        private static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

        /// <summary>
        /// Fills frame slot <paramref name="slot"/> of <paramref name="rings"/> with this frame's
        /// records and up to <paramref name="budgetBricks"/> bricks, commits the slot and returns
        /// its sequence number. False when the packet would be too large; nothing was consumed
        /// then. The material entries go into the packet only with <see cref="FrameConfig.Reset"/>.
        /// </summary>
        public unsafe bool FillFramePacket(CaelixNativePacketRings rings, int slot, long budgetBricks,
            in FrameConfig config, Object context, out ulong sequence)
        {
            sequence = 0;
            long budget = budgetBricks * BrickPacketBytes;

            // Entity records: removes first (the plugin applies them in list order), then the views
            // that have something to say.
            int entityCount = pendingEntityRemoves.Count;
            int removeCount = 0;
            int uploadEntities = 0;
            foreach (EntityState state in entityOrder)
            {
                state.PacketIndex = -1;
                bool moved = !state.NeedsAdd && !TransformEquals(state.SentTransform, state.View.Data.transform);
                bool uploads = budget > 0 && state.UploadsWaiting > 0;
                if (state.NeedsAdd || moved || state.PendingRemoves.Length > 0 || uploads)
                {
                    state.PacketIndex = entityCount++;
                }
                removeCount += state.PendingRemoves.Length;
                if (uploads) uploadEntities++;
            }

            // Material entries go with the reset only (the table is fixed for the session).
            int materialCount = config.Reset && config.Materials != null ? config.Materials.Length : 0;
            int materialBytes = materialCount * sizeof(CaelixNativeMaterialEntry);

            int materialsOffset = Align(sizeof(CaelixNativeFramePacket), 16);
            int entitiesOffset = Align(materialsOffset + materialBytes, 16);
            int removesOffset = Align(entitiesOffset + entityCount * sizeof(CaelixNativeEntity), 16);
            int uploadsOffset = Align(removesOffset + removeCount * sizeof(CaelixNativeBrickRemove), 16);
            int dataOffset = Align(uploadsOffset + uploadEntities * sizeof(CaelixNativeUpload), 16);
            long required = dataOffset + (budget > 0 ? budget + 16L * uploadEntities + 16 : 0);
            if (required > int.MaxValue)
            {
                Debug.LogError($"Caelix native: frame packet would need {required} bytes; lower the staging ring.", context);
                foreach (EntityState state in entityOrder) state.PacketIndex = -1;
                return false;
            }
            rings.EnsureFrameCapacity(slot, required);
            byte* packet = (byte*)rings.FramePacket(slot);

            // Materials.
            if (materialCount > 0)
            {
                fixed (CaelixNativeMaterialEntry* src = config.Materials)
                {
                    UnsafeUtility.MemCpy(packet + materialsOffset, src, materialBytes);
                }
            }

            // Entities.
            var entityRecords = (CaelixNativeEntity*)(packet + entitiesOffset);
            int e = 0;
            foreach (Guid128 guid in pendingEntityRemoves)
            {
                entityRecords[e] = default;
                WriteGuid(ref entityRecords[e], guid);
                entityRecords[e].flags = CaelixNativeBridge.EntityRemove;
                e++;
            }
            foreach (EntityState state in entityOrder)
            {
                if (state.PacketIndex < 0) continue;
                ref CaelixNativeEntity record = ref entityRecords[state.PacketIndex];
                record = default;
                WriteGuid(ref record, state.Guid);
                RigidTransform t = state.View.Data.transform;
                record.position[0] = t.pos.x; record.position[1] = t.pos.y; record.position[2] = t.pos.z;
                record.rotation[0] = t.rot.value.x; record.rotation[1] = t.rot.value.y;
                record.rotation[2] = t.rot.value.z; record.rotation[3] = t.rot.value.w;
                if (state.NeedsAdd) record.flags = CaelixNativeBridge.EntityAdd;
                else if (!TransformEquals(state.SentTransform, t)) record.flags = CaelixNativeBridge.EntityTransform;
                state.SentTransform = t;
                state.NeedsAdd = false;
            }

            // Brick removes.
            var removes = (CaelixNativeBrickRemove*)(packet + removesOffset);
            int r = 0;
            foreach (EntityState state in entityOrder)
            {
                for (int i = 0; i < state.PendingRemoves.Length; i++)
                {
                    int3 key = state.PendingRemoves[i];
                    removes[r++] = new CaelixNativeBrickRemove
                    {
                        entityIndex = (uint)state.PacketIndex, keyX = key.x, keyY = key.y, keyZ = key.z,
                    };
                }
                state.PendingRemoves.Clear();
            }

            // Uploads: one Burst job copies the waiting bricks of every entity, in entity order, up
            // to the budget. Bound pointers live only inside this synchronous run.
            int uploadCount = 0;
            int endOffset = dataOffset;
            long bricks = 0;
            if (budget > 0 && uploadEntities > 0)
            {
                var sources = new NativeArray<UploadSource>(uploadEntities, Allocator.TempJob);
                var owners = new EntityState[uploadEntities];
                int s = 0;
                foreach (EntityState state in entityOrder)
                {
                    if (state.PacketIndex < 0 || state.UploadsWaiting <= 0) continue;
                    owners[s] = state;
                    sources[s++] = new UploadSource
                    {
                        Data = state.View.Data,
                        Keys = state.PendingUploads.Ptr + state.Head,
                        Count = state.UploadsWaiting,
                        EntityIndex = (uint)state.PacketIndex,
                    };
                }
                var consumed = new NativeArray<int>(uploadEntities, Allocator.TempJob);
                var result = new NativeArray<long>(4, Allocator.TempJob);
                new FillUploadsJob
                {
                    Sources = sources,
                    Packet = packet,
                    UploadsOffset = uploadsOffset,
                    DataOffset = dataOffset,
                    DataEnd = dataOffset + budget,
                    Consumed = consumed,
                    Result = result,
                }.Run();
                uploadCount = (int)result[0];
                endOffset = (int)result[1];
                bricks = result[2];
                BricksSentTotal += bricks;
                if (bricks > 0) NoteBricksSent(bricks);
                for (int i = 0; i < uploadEntities; i++)
                {
                    EntityState state = owners[i];
                    state.Head += consumed[i];
                    if (state.Head >= state.PendingUploads.Length)
                    {
                        state.PendingUploads.Clear();
                        state.Head = 0;
                    }
                    else if (state.Head > 65536 && state.Head * 2 > state.PendingUploads.Length)
                    {
                        state.PendingUploads.RemoveRange(0, state.Head);
                        state.Head = 0;
                    }
                }
                sources.Dispose();
                consumed.Dispose();
                result.Dispose();
            }

            sequence = rings.CommitFrame(slot, bricks);
            *(CaelixNativeFramePacket*)packet = new CaelixNativeFramePacket
            {
                header = new CaelixNativePacketHeader
                {
                    size = (uint)sizeof(CaelixNativeFramePacket),
                    version = CaelixNativeBridge.PacketVersion,
                    kind = CaelixNativeBridge.PacketFrame,
                    sequence = sequence,
                },
                flags = config.Reset ? CaelixNativeBridge.FrameReset : 0u,
                totalBytes = (uint)Math.Max(endOffset, dataOffset),
                encoding = config.Encoding,
                stagingMib = config.StagingMiB,
                materialCount = (uint)materialCount,
                materialsOffset = (uint)materialsOffset,
                entityCount = (uint)entityCount,
                entitiesOffset = (uint)entitiesOffset,
                removeCount = (uint)removeCount,
                removesOffset = (uint)removesOffset,
                uploadCount = (uint)uploadCount,
                uploadsOffset = (uint)uploadsOffset,
                brickCount = (uint)bricks,
                light = config.Light,
            };

            pendingEntityRemoves.Clear();
            UpdateBricksPending();
            return true;
        }

        private static unsafe void WriteGuid(ref CaelixNativeEntity record, Guid128 guid)
        {
            uint4 v = guid.Value;
            fixed (byte* g = record.guid)
            {
                *(uint4*)g = v;
            }
        }

        private static bool TransformEquals(RigidTransform a, RigidTransform b)
        {
            return math.all(a.pos == b.pos) && math.all(a.rot.value == b.rot.value);
        }

        // ---------------- stream statistics ----------------

        private void NoteBricksSent(long bricks)
        {
            if (!streaming)
            {
                streaming = true;
                streamStartFrame = Time.frameCount;
                streamStartTicks = Stopwatch.GetTimestamp();
                streamTickMsTotal = 0;
                streamTickMsMax = 0;
                streamTicks = 0;
                streamBricks = 0;
            }
            streamBricks += bricks;
            settleFrames = 0;
        }

        /// <summary>Adds one tick's CPU time to the stream statistics while a stream is on its way.</summary>
        public void RecordTick(double milliseconds)
        {
            if (!streaming) return;
            streamTicks++;
            streamTickMsTotal += milliseconds;
            streamTickMsMax = Math.Max(streamTickMsMax, milliseconds);
        }

        /// <summary>
        /// Ends the stream statistics when a burst of uploads is resident and traceable in the
        /// plugin, and logs one line then (only with <paramref name="verbose"/>).
        /// </summary>
        public void TrackStream(in CaelixNativeStatus status, long bricksInFlight, bool verbose, Object context)
        {
            if (!streaming) return;
            if (BricksPending > 0 || bricksInFlight > 0 || status.asBuildsPending != 0)
            {
                settleFrames = 0;
                return;
            }
            // The statistics are published after the flush of the frame event that consumed the
            // last uploads; two more flushes make sure they belong to a flush after it.
            if (settleFrames == 0)
            {
                settleFrames = status.frames + 2;
                return;
            }
            if (status.frames < settleFrames) return;
            streaming = false;
            settleFrames = 0;
            if (!verbose) return;
            double seconds = (Stopwatch.GetTimestamp() - streamStartTicks) / (double)Stopwatch.Frequency;
            Debug.Log($"Caelix native: stream complete after {Time.frameCount - streamStartFrame} frames ({seconds:F2} s): " +
                      $"{streamBricks} bricks sent, plugin resident {status.bricksResident}, {status.entities} entities, " +
                      $"{status.groups} groups, uploaded total {status.uploadedTotal}; Tick {streamTickMsTotal / Math.Max(1, streamTicks):F3} ms " +
                      $"mean, {streamTickMsMax:F3} ms max over {streamTicks} ticks.", context);
        }

        // ---------------- the upload job ----------------

        private unsafe struct UploadSource
        {
            public VoxelEntityData Data;
            [NativeDisableUnsafePtrRestriction] public int3* Keys;
            public int Count;
            public uint EntityIndex;
        }

        /// <summary>
        /// Copies waiting bricks into the packet: per entity, the keys (12 bytes each) then the
        /// 16-byte aligned block values (1024 bytes each) and one upload record. A key whose brick
        /// no longer exists is consumed without a copy (its removal is, or was, a remove record).
        /// Stops at the first entity that does not fit completely; the rest waits for a later packet.
        /// </summary>
        [BurstCompile]
        private unsafe struct FillUploadsJob : IJob
        {
            [ReadOnly] public NativeArray<UploadSource> Sources;
            [NativeDisableUnsafePtrRestriction] public byte* Packet;
            public int UploadsOffset;
            public int DataOffset;
            public long DataEnd;
            public NativeArray<int> Consumed;
            /// <summary>0 upload records, 1 end offset, 2 bricks copied, 3 keys skipped.</summary>
            public NativeArray<long> Result;

            public void Execute()
            {
                long cursor = DataOffset;
                int uploads = 0;
                long bricks = 0;
                long skipped = 0;
                var pointers = new NativeList<IntPtr>(1024, Allocator.Temp);
                var keys = new NativeList<int3>(1024, Allocator.Temp);
                for (int s = 0; s < Sources.Length; s++)
                {
                    UploadSource source = Sources[s];
                    pointers.Clear();
                    keys.Clear();
                    BrickCursor brickCursor = default;
                    // Keys area + up to 15 bytes of alignment + blocks.
                    long available = DataEnd - cursor - 16;
                    int taken = 0;
                    for (int i = 0; i < source.Count; i++)
                    {
                        if ((long)(keys.Length + 1) * (12 + 1024) > available) break;
                        int3 key = source.Keys[i];
                        taken = i + 1;
                        if (source.Data.TryBindBrick(SectorSlotId.Block, key, ref brickCursor, out Block* blocks))
                        {
                            pointers.Add((IntPtr)blocks);
                            keys.Add(key);
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    Consumed[s] = taken;
                    int n = keys.Length;
                    if (n > 0)
                    {
                        long keysOffset = cursor;
                        UnsafeUtility.MemCpy(Packet + keysOffset, keys.GetUnsafeReadOnlyPtr(), (long)n * 12);
                        cursor = (keysOffset + (long)n * 12 + 15) & ~15L;
                        long blocksOffset = cursor;
                        for (int i = 0; i < n; i++)
                        {
                            UnsafeUtility.MemCpy(Packet + blocksOffset + (long)i * 1024, (void*)pointers[i], 1024);
                        }
                        cursor = blocksOffset + (long)n * 1024;
                        var record = (CaelixNativeUpload*)(Packet + UploadsOffset) + uploads;
                        *record = new CaelixNativeUpload
                        {
                            entityIndex = source.EntityIndex,
                            brickCount = (uint)n,
                            keysOffset = (uint)keysOffset,
                            blocksOffset = (uint)blocksOffset,
                        };
                        uploads++;
                        bricks += n;
                    }
                    if (taken < source.Count)
                    {
                        for (int r = s + 1; r < Sources.Length; r++) Consumed[r] = 0;
                        break;
                    }
                }
                pointers.Dispose();
                keys.Dispose();
                Result[0] = uploads;
                Result[1] = cursor;
                Result[2] = bricks;
                Result[3] = skipped;
            }
        }
    }
}
