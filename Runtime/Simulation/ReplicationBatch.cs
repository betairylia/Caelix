using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Caelix.Net;
using Caelix.Utils;

namespace Caelix.Simulation
{
    /// <summary>
    /// One entity to pack: its guid, its store, and whether every allocated brick goes (join, or an
    /// entity new to a connection) or only this cycle's change list (delta).
    /// </summary>
    public struct ReplicationEntityRef
    {
        public Guid128 Guid;
        public VoxelEntityData Data;

        /// <summary>Non-zero for a full entity, zero for a delta. A byte because the struct lives in native memory.</summary>
        public byte FullEntity;
    }

    /// <summary>
    /// One finished <see cref="NetMessageType.BrickBatch"/> message inside a
    /// <see cref="ReplicationBatch"/>'s byte buffer. <see cref="Length"/> 0 means the message
    /// carries nothing; a caller skips those ranges.
    /// </summary>
    public struct ReplicationRange
    {
        public Guid128 Guid;
        public int Offset;
        public int Length;
    }

    /// <summary>
    /// Phase A of replication: packs BrickBatch messages for a list of entities, in parallel over
    /// BRICKS, into a byte buffer. Built once per world per tick for the delta and shared by every
    /// connection; built again on demand for the entities a connection does not know yet. Owned and
    /// reused by <see cref="CaelixServer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A message belongs to one entity and carries a run of that entity's records; each record
    /// names its own brick key and its operation, so nothing here knows how bricks are grouped in
    /// storage. A full entity contributes every allocated brick; a delta entity contributes its
    /// change list — a <see cref="ChangeKind.Removed"/> entry becomes a <see cref="BrickOp.Remove"/>
    /// record, an updated entry whose dirty flags meet
    /// <see cref="BrickReplication.ReplicationDirtyMask"/> becomes a <see cref="BrickOp.Update"/>.
    /// Records keep change-list order, so a key removed and recreated in one tick reaches the client
    /// as Remove then Update.
    /// </para>
    /// <para>
    /// The batch packs in CHUNKS, not in one buffer: <see cref="Prepare"/> once, then
    /// <see cref="BuildNextChunk"/> until it returns false, sending the ranges of each chunk before
    /// the next one is built. A join of a large world hands the batch every brick of the world,
    /// which for an 8K world is about 2.9 million bricks of roughly a kilobyte each — near 3 GB.
    /// One buffer for that overflowed the <c>int</c> prefix sum and wrote gigabytes into a 64 KB
    /// allocation. <see cref="MaxChunkBytes"/> bounds a chunk, and also bounds one message, so a
    /// chunk is always at least one whole message.
    /// </para>
    /// <para>
    /// Phase B (<see cref="ServerConnection.ReplicateWorld"/>) is the per-connection half: it diffs
    /// the connection's entity knowledge, sends the lifecycle messages, and forwards the slices this
    /// batch produced. Nothing here touches a connection, so the same bytes serve all of them.
    /// </para>
    /// </remarks>
    public sealed unsafe class ReplicationBatch : IDisposable
    {
        /// <summary>Capacity the byte buffer is shrunk back to once a chunk sequence finished.</summary>
        private const int IdleBytesCapacity = 4 * 1024 * 1024;

        /// <summary>Entry count above which the per-brick lists are released instead of kept.</summary>
        private const int IdleBrickCapacity = 1 << 20;

        /// <summary>One brick record to pack, resolved against <see cref="entities"/> by index.</summary>
        private struct BrickWork
        {
            public int Entity;
            public int3 Key;
            public BrickOp Op;
        }

        /// <summary>One message: a run of one entity's bricks plus its own envelope.</summary>
        private struct MessageRange
        {
            public Guid128 Guid;
            public int Entity;
            public int FirstBrick;
            public int BrickCount;

            /// <summary>Total bytes, including <see cref="NetHeader"/> and <see cref="BrickBatchHeader"/>.</summary>
            public int Bytes;
        }

        /// <summary>Envelope bytes every message pays before its first record.</summary>
        private static readonly int MessageOverheadBytes =
            NetHeader.Size + UnsafeUtility.SizeOf<BrickBatchHeader>();

        private NativeList<ReplicationEntityRef> entities;
        private NativeList<BrickWork> bricks;
        private NativeList<int> sizes;
        private NativeList<MessageRange> messages;
        private NativeList<int> offsets;
        private NativeList<byte> bytes;
        private NativeList<ReplicationRange> ranges;
        private bool disposed;

        private int cursor;
        private ushort buildWorldId;
        private uint buildTick;
        private ushort buildSlotMask;

        public ReplicationBatch()
        {
            entities = new NativeList<ReplicationEntityRef>(64, Allocator.Persistent);
            bricks = new NativeList<BrickWork>(256, Allocator.Persistent);
            sizes = new NativeList<int>(256, Allocator.Persistent);
            messages = new NativeList<MessageRange>(64, Allocator.Persistent);
            offsets = new NativeList<int>(256, Allocator.Persistent);
            bytes = new NativeList<byte>(64 * 1024, Allocator.Persistent);
            ranges = new NativeList<ReplicationRange>(64, Allocator.Persistent);
        }

        /// <summary>
        /// Upper bound on the bytes one chunk packs, and on the bytes one message packs. A join of
        /// a large world streams through chunks of this size instead of one buffer holding the
        /// whole world (which overflowed int at ~3 GB and froze the Editor). A single record larger
        /// than this still goes alone; one brick of Block is about 1 KB, far under the default.
        /// </summary>
        public int MaxChunkBytes { get; set; } = 64 * 1024 * 1024;

        /// <summary>
        /// Number of chunks the last <see cref="Prepare"/> / <see cref="BuildNextChunk"/> sequence
        /// produced. For tests and stats.
        /// </summary>
        public int LastChunkCount { get; private set; }

        /// <summary>Number of packed ranges in the CURRENT chunk. Zero until a chunk was built.</summary>
        public int Count => ranges.Length;

        /// <summary>Number of entities added since the last <see cref="Clear"/>, built or not.</summary>
        public int EntityCount => entities.Length;

        public ReplicationRange this[int i] => ranges[i];

        /// <summary>Drops the entities and the bytes of the previous build. Keeps the capacity.</summary>
        public void Clear()
        {
            entities.Clear();
            bricks.Clear();
            sizes.Clear();
            messages.Clear();
            offsets.Clear();
            bytes.Clear();
            ranges.Clear();
            cursor = 0;
        }

        /// <summary>The entity added at <paramref name="i"/>. Only valid before <see cref="Prepare"/>.</summary>
        internal ReplicationEntityRef GetEntity(int i) => entities[i];

        /// <summary>
        /// Drops the entity at <paramref name="i"/>, moving the last one into its place. Order of a
        /// batch does not matter, so a caller may filter the added entities before
        /// <see cref="Prepare"/>.
        /// </summary>
        internal void RemoveEntityAtSwapBack(int i)
        {
            entities.RemoveAtSwapBack(i);
        }

        /// <summary>Queues one entity. <paramref name="fullEntity"/> sends every allocated brick.</summary>
        public void Add(Guid128 guid, in VoxelEntityData data, bool fullEntity)
        {
            entities.Add(new ReplicationEntityRef
            {
                Guid = guid,
                Data = data,
                FullEntity = (byte)(fullEntity ? 1 : 0),
            });
        }

        /// <summary>
        /// Adds every entity of <paramref name="world"/> that has a change list this cycle, as a
        /// delta. Main-thread walk of the world's entity map; which of those changes actually
        /// travel is the collect job's decision, and an entity whose changes are all
        /// non-replicated simply produces no message.
        /// </summary>
        public void CollectChangedEntities(CaelixWorld world)
        {
            if (world == null)
            {
                return;
            }

            foreach (var entityEntry in world.Data.VoxelEntities)
            {
                VoxelEntityData data = entityEntry.Value;
                if (data.ChangeCount == 0)
                {
                    continue;
                }

                Add(entityEntry.Key, in data, fullEntity: false);
            }
        }

        /// <summary>
        /// Collects the bricks of every added entity, sizes their records, and cuts them into
        /// messages. Call once, then loop <see cref="BuildNextChunk"/>. Safe to call with zero
        /// entities.
        /// </summary>
        public void Prepare(ushort worldId, uint tick, ushort slotMask)
        {
            ranges.Clear();
            bytes.Clear();
            messages.Clear();
            bricks.Clear();
            sizes.Clear();
            offsets.Clear();
            LastChunkCount = 0;
            cursor = 0;
            buildWorldId = worldId;
            buildTick = tick;
            buildSlotMask = slotMask;

            if (entities.Length == 0)
            {
                return;
            }

            new CollectBricksJob
            {
                Entities = entities.AsArray(),
                Bricks = bricks,
            }.Run();

            int count = bricks.Length;
            if (count == 0)
            {
                return;
            }

            sizes.ResizeUninitialized(count);
            new SizeJob
            {
                Entities = entities.AsArray(),
                Bricks = bricks.AsArray(),
                Sizes = sizes.AsArray(),
                SlotMask = slotMask,
            }.Schedule(count, 64).Complete();

            // Cut the bricks into messages: a message never spans entities, and never grows past
            // one chunk, so a chunk always holds at least one whole message.
            for (int i = 0; i < count; i++)
            {
                BrickWork brick = bricks[i];
                bool startNew = messages.Length == 0;
                if (!startNew)
                {
                    MessageRange open = messages[messages.Length - 1];
                    startNew = open.Entity != brick.Entity ||
                               (open.BrickCount > 0 && (long)open.Bytes + sizes[i] > MaxChunkBytes);
                }

                if (startNew)
                {
                    messages.Add(new MessageRange
                    {
                        Guid = entities[brick.Entity].Guid,
                        Entity = brick.Entity,
                        FirstBrick = i,
                        BrickCount = 0,
                        Bytes = MessageOverheadBytes,
                    });
                }

                MessageRange message = messages[messages.Length - 1];
                message.BrickCount++;
                message.Bytes += sizes[i];
                messages[messages.Length - 1] = message;
            }
        }

        /// <summary>
        /// Packs the next run of messages whose sizes sum to at most <see cref="MaxChunkBytes"/>
        /// (always at least one message) into the byte buffer and fills the ranges for them.
        /// Returns false, with <see cref="Count"/> 0, when every message was packed. The ranges of
        /// a chunk are only valid until the next call.
        /// </summary>
        public bool BuildNextChunk()
        {
            ranges.Clear();
            if (cursor >= messages.Length)
            {
                bytes.Clear();
                bricks.Clear();
                sizes.Clear();
                offsets.Clear();
                ShrinkBuffers();
                return false;
            }

            int end = cursor;
            long total = 0;
            while (end < messages.Length && (end == cursor || total + messages[end].Bytes <= MaxChunkBytes))
            {
                total += messages[end].Bytes;
                end++;
            }

            int chunkBricks = 0;
            for (int m = cursor; m < end; m++)
            {
                chunkBricks += messages[m].BrickCount;
            }

            offsets.ResizeUninitialized(chunkBricks);

            int offset = 0;
            int slot = 0;
            for (int m = cursor; m < end; m++)
            {
                MessageRange message = messages[m];
                ranges.Add(new ReplicationRange
                {
                    Guid = message.Guid,
                    Offset = offset,
                    Length = message.Bytes,
                });

                int recordOffset = offset + MessageOverheadBytes;
                for (int b = 0; b < message.BrickCount; b++)
                {
                    offsets[slot++] = recordOffset;
                    recordOffset += sizes[message.FirstBrick + b];
                }

                offset += message.Bytes;
            }

            bytes.ResizeUninitialized((int)total);
            var basePtr = (byte*)bytes.GetUnsafePtr();

            // Envelopes on the main thread; the records are packed in parallel over bricks.
            for (int i = 0; i < ranges.Length; i++)
            {
                ReplicationRange range = ranges[i];
                var header = new UnsafeNetWriter(basePtr + range.Offset, MessageOverheadBytes);
                NetHeader.Write(ref header, NetMessageType.BrickBatch, buildWorldId, buildTick);
                header.Write(new BrickBatchHeader
                {
                    Guid = range.Guid,
                    BrickCount = messages[cursor + i].BrickCount,
                });
            }

            if (chunkBricks > 0)
            {
                new WriteJob
                {
                    Entities = entities.AsArray(),
                    Bricks = bricks.AsArray(),
                    Sizes = sizes.AsArray(),
                    Offsets = offsets.AsArray(),
                    BasePtr = basePtr,
                    First = messages[cursor].FirstBrick,
                    SlotMask = buildSlotMask,
                }.Schedule(chunkBricks, 64).Complete();
            }

            cursor = end;
            LastChunkCount++;
            return true;
        }

        /// <summary>
        /// Gives the chunk buffer and the per-brick lists back once the sequence ended, so a
        /// one-off load of a large world does not pin its peak for the rest of the session.
        /// </summary>
        private void ShrinkBuffers()
        {
            // Every list is empty here, so the shrinks are always legal.
            if (bytes.Capacity > IdleBytesCapacity)
            {
                bytes.Capacity = IdleBytesCapacity;
            }

            if (bricks.Capacity > IdleBrickCapacity)
            {
                bricks.Capacity = 256;
            }

            if (sizes.Capacity > IdleBrickCapacity)
            {
                sizes.Capacity = 256;
            }

            if (offsets.Capacity > IdleBrickCapacity)
            {
                offsets.Capacity = 256;
            }
        }

        /// <summary>The bytes of range <paramref name="i"/>. Valid until the next chunk is built.</summary>
        public ReadOnlySpan<byte> Slice(int i)
        {
            ReplicationRange range = ranges[i];
            return new ReadOnlySpan<byte>(bytes.GetUnsafeReadOnlyPtr() + range.Offset, range.Length);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (entities.IsCreated) entities.Dispose();
            if (bricks.IsCreated) bricks.Dispose();
            if (sizes.IsCreated) sizes.Dispose();
            if (messages.IsCreated) messages.Dispose();
            if (offsets.IsCreated) offsets.Dispose();
            if (bytes.IsCreated) bytes.Dispose();
            if (ranges.IsCreated) ranges.Dispose();
        }

        /// <summary>
        /// Turns every added entity into the flat list of brick records it contributes, in add
        /// order. Single-threaded: record order inside an entity is the order a client must apply.
        /// </summary>
        [BurstCompile]
        private struct CollectBricksJob : IJob
        {
            [ReadOnly] public NativeArray<ReplicationEntityRef> Entities;
            public NativeList<BrickWork> Bricks;

            public void Execute()
            {
                for (int e = 0; e < Entities.Length; e++)
                {
                    ReplicationEntityRef entity = Entities[e];
                    if (entity.FullEntity != 0)
                    {
                        foreach (int3 key in entity.Data.EnumerateBricks())
                        {
                            Bricks.Add(new BrickWork { Entity = e, Key = key, Op = BrickOp.Update });
                        }

                        continue;
                    }

                    // The Changes property builds a safety handle; the indexed pair is the
                    // Burst-safe reader of the same list.
                    int changes = entity.Data.ChangeCount;
                    for (int i = 0; i < changes; i++)
                    {
                        BrickChange change = entity.Data.GetChange(i);
                        if (change.Kind == ChangeKind.Removed)
                        {
                            Bricks.Add(new BrickWork { Entity = e, Key = change.Key, Op = BrickOp.Remove });
                        }
                        else if ((change.DirtyFlags & BrickReplication.ReplicationDirtyMask) != 0)
                        {
                            Bricks.Add(new BrickWork { Entity = e, Key = change.Key, Op = BrickOp.Update });
                        }
                    }
                }
            }
        }

        [BurstCompile]
        private struct SizeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ReplicationEntityRef> Entities;
            [ReadOnly] public NativeArray<BrickWork> Bricks;
            public NativeArray<int> Sizes;
            public ushort SlotMask;

            public void Execute(int i)
            {
                BrickWork brick = Bricks[i];
                ReplicationEntityRef entity = Entities[brick.Entity];
                Sizes[i] = entity.Data.ReplicatedRecordBytes(brick.Key, brick.Op, SlotMask);
            }
        }

        [BurstCompile]
        private struct WriteJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ReplicationEntityRef> Entities;
            [ReadOnly] public NativeArray<BrickWork> Bricks;
            [ReadOnly] public NativeArray<int> Sizes;

            /// <summary>Chunk-relative byte offset of each record, indexed like this job.</summary>
            [ReadOnly] public NativeArray<int> Offsets;

            /// <summary>Base of the chunk buffer, already sized. The job never resizes it.</summary>
            [NativeDisableUnsafePtrRestriction] public byte* BasePtr;

            /// <summary>Index of the chunk's first brick. <c>Offsets</c> is chunk-relative, the other two are not.</summary>
            public int First;

            public ushort SlotMask;

            public void Execute(int i)
            {
                int index = First + i;
                BrickWork brick = Bricks[index];
                int size = Sizes[index];
                ReplicationEntityRef entity = Entities[brick.Entity];

                var writer = new UnsafeNetWriter(BasePtr + Offsets[i], size);
                entity.Data.WriteReplicatedRecord(ref writer, brick.Key, brick.Op, SlotMask);
                CheckExactFit(writer.Length, size);
            }

            [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private static void CheckExactFit(int written, int expected)
            {
                if (written != expected)
                {
                    throw new InvalidOperationException(
                        "ReplicationBatch wrote a record of the wrong length; the size job and the write job disagree.");
                }
            }
        }
    }
}
