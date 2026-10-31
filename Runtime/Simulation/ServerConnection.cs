using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using Caelix.Net;
using Caelix.Utils;

namespace Caelix.Simulation
{
    /// <summary>
    /// One connected client and what it already knows about each world. Replication is a diff
    /// between that knowledge and the world: a new entity goes out with every allocated brick, a
    /// known entity receives the shared per-tick delta, and transforms and flags go out when they
    /// changed. Knowledge is per ENTITY; brick residency is the client's own, derived from the
    /// records it received.
    /// </summary>
    public sealed class ServerConnection
    {
        private sealed class EntityKnown
        {
            public long InstanceId;
            public RigidTransform Transform;
            public bool IsStatic;
            public bool IsProtected;
            public bool HasBody;
        }

        private sealed class WorldKnown
        {
            public readonly Dictionary<Guid128, EntityKnown> Entities = new();
            public CaelixWorld Instance;
            public ushort SlotMask;
            public bool ResetRequested;
        }

        private static readonly ProfilerMarker s_ReplicateWorldMarker = new("Server.ReplicateWorld");
        private static readonly ProfilerMarker s_ReplicationFullBuildMarker = new("Server.ReplicationFullBuild");

        private readonly Dictionary<ushort, WorldKnown> worlds = new();
        private readonly List<Guid128> guidScratch = new();
        private readonly List<ushort> worldScratch = new();

        /// <summary>
        /// Entities this connection received in full during the current <see cref="ReplicateWorld"/>
        /// call. Their shared-delta messages are redundant and are skipped. Cleared at the start of
        /// <see cref="ReplicateWorld"/> and read afterwards, in the same <c>CaelixServer.Replicate</c>
        /// call, by <see cref="ReceivedFullThisTick"/> and <see cref="SendDelta"/>. Reused across ticks.
        /// </summary>
        private readonly HashSet<Guid128> sentFullThisTick = new();

        public int Id { get; }
        public INetChannel Channel { get; }

        /// <summary>Set once the handshake went out. Sent with the first replication, not at accept time,
        /// so that game types registered during scene start are part of the registry hash.</summary>
        public bool HelloSent { get; internal set; }

        /// <summary>Worlds this connection receives. Empty means every world.</summary>
        public HashSet<ushort> SubscribedWorlds { get; } = new();

        internal ServerConnection(int id, INetChannel channel)
        {
            Id = id;
            Channel = channel;
        }

        public bool IsConnected => Channel != null && Channel.IsConnected;

        public bool IsSubscribed(ushort worldId) => SubscribedWorlds.Count == 0 || SubscribedWorlds.Contains(worldId);

        public void Send(NetDelivery delivery, NetMessageWriter writer)
        {
            Channel.Send(delivery, writer.AsSpan());
        }

        /// <summary>Sends bytes a <see cref="ReplicationBatch"/> packed. The span is copied by the channel.</summary>
        public void Send(NetDelivery delivery, ReadOnlySpan<byte> payload)
        {
            Channel.Send(delivery, payload);
        }

        /// <summary>Forgets a world so the next <see cref="SyncWorlds"/> sends it again in full.</summary>
        public void ForgetWorld(ushort worldId)
        {
            if (worlds.TryGetValue(worldId, out WorldKnown known)) known.ResetRequested = true;
        }

        /// <summary>
        /// Brings this connection's world set in line with the server's: a WorldRemove for every
        /// world it knew that is gone or no longer subscribed, then a WorldAdd for every subscribed
        /// world it does not know yet. Runs once per <c>Step</c>, before any world replicates, so
        /// <see cref="ReplicateWorld"/> can rely on the world being known.
        /// </summary>
        internal void SyncWorlds(IReadOnlyList<CaelixWorld> serverWorlds, NetMessageWriter writer, uint tick)
        {
            // Removes first: a world id that is freed and reused in the same tick must reach the
            // client as remove-then-add, not add-then-remove.
            worldScratch.Clear();
            foreach (var kvp in worlds)
            {
                ushort worldId = kvp.Key;
                bool stillThere = false;
                for (int i = 0; i < serverWorlds.Count; i++)
                {
                    if (ReferenceEquals(serverWorlds[i], kvp.Value.Instance) &&
                        serverWorlds[i].Id == worldId &&
                        serverWorlds[i].Config.replicatedSlotMask == kvp.Value.SlotMask &&
                        !kvp.Value.ResetRequested)
                    {
                        stillThere = true;
                        break;
                    }
                }

                if (!stillThere || !IsSubscribed(worldId))
                {
                    worldScratch.Add(worldId);
                }
            }

            for (int i = 0; i < worldScratch.Count; i++)
            {
                worlds.Remove(worldScratch[i]);
                writer.Reset();
                NetHeader.Write(writer, NetMessageType.WorldRemove, worldScratch[i], tick);
                writer.Write(new WorldRemoveMessage());
                Send(NetDelivery.Reliable, writer);
            }

            for (int i = 0; i < serverWorlds.Count; i++)
            {
                CaelixWorld world = serverWorlds[i];
                if (!IsSubscribed(world.Id) || worlds.ContainsKey(world.Id))
                {
                    continue;
                }

                worlds.Add(world.Id, new WorldKnown { Instance = world, SlotMask = world.Config.replicatedSlotMask });
                writer.Reset();
                NetHeader.Write(writer, NetMessageType.WorldAdd, world.Id, tick);
                writer.Write(new WorldAddMessage { ReplicatedSlotMask = world.Config.replicatedSlotMask });
                Send(NetDelivery.Reliable, writer);
            }
        }

        /// <summary>
        /// Phase B of replication: the per-connection diff. Sends despawns, spawns, state and
        /// transforms, then packs every allocated brick of the entities this connection has not
        /// seen into <paramref name="full"/> and streams them chunk by chunk. Runs before phase A;
        /// the shared delta follows through <see cref="SendDelta"/>.
        /// </summary>
        /// <remarks>
        /// There is no per-sector diff any more. A known entity is served entirely by the shared
        /// delta, which is derived from that entity's change list, so a sector attached, filled or
        /// removed between steps reaches the client as Remove records for the old bricks followed
        /// by Update records for the new ones. A new entity is served entirely by the catch-up
        /// batch and never by a delta message.
        /// </remarks>
        internal unsafe void ReplicateWorld(CaelixWorld world, NetMessageWriter writer, ReplicationBatch full)
        {
            using var _ = s_ReplicateWorldMarker.Auto();
            if (!IsSubscribed(world.Id))
            {
                return;
            }

            if (!worlds.TryGetValue(world.Id, out WorldKnown known))
            {
                // SyncWorlds runs first every tick and creates it; nothing to do before it did.
                return;
            }

            sentFullThisTick.Clear();
            full.Clear();

            ushort worldId = world.Id;
            uint tick = world.TickIndex;
            ushort slotMask = world.Config.replicatedSlotMask;
            NativeHashMap<Guid128, VoxelEntityData> entities = world.Data.VoxelEntities;

            // Despawns.
            guidScratch.Clear();
            foreach (var kvp in known.Entities)
            {
                if (!entities.ContainsKey(kvp.Key) || world.GetEntityInstanceId(kvp.Key) != kvp.Value.InstanceId)
                {
                    guidScratch.Add(kvp.Key);
                }
            }

            for (int i = 0; i < guidScratch.Count; i++)
            {
                known.Entities.Remove(guidScratch[i]);
                writer.Reset();
                NetHeader.Write(writer, NetMessageType.EntityDespawn, worldId, tick);
                writer.Write(new EntityDespawnMessage { Guid = guidScratch[i] });
                Send(NetDelivery.Reliable, writer);
            }

            // Spawns, state, transforms, catch-up.
            foreach (var kvp in entities)
            {
                Guid128 guid = kvp.Key;
                VoxelEntityData data = kvp.Value;
                bool hasBody = world.HasBody(guid);

                bool isNew = !known.Entities.TryGetValue(guid, out EntityKnown entityKnown);
                if (isNew)
                {
                    entityKnown = new EntityKnown
                    {
                        InstanceId = world.GetEntityInstanceId(guid),
                        Transform = data.transform,
                        IsStatic = data.isStatic,
                        IsProtected = data.isProtected,
                        HasBody = hasBody,
                    };
                    known.Entities.Add(guid, entityKnown);

                    writer.Reset();
                    NetHeader.Write(writer, NetMessageType.EntitySpawn, worldId, tick);
                    writer.Write(new EntitySpawnMessage
                    {
                        Guid = guid,
                        Transform = data.transform,
                        IsStatic = (byte)(data.isStatic ? 1 : 0),
                        IsProtected = (byte)(data.isProtected ? 1 : 0),
                        HasBody = (byte)(hasBody ? 1 : 0),
                    });
                    Send(NetDelivery.Reliable, writer);

                    // Every brick of an entity new to this connection. Its shared-delta message,
                    // if the tick produced one, would be a redundant subset.
                    full.Add(guid, in data, fullEntity: true);
                    sentFullThisTick.Add(guid);
                }
                else
                {
                    if (entityKnown.IsStatic != data.isStatic ||
                        entityKnown.IsProtected != data.isProtected ||
                        entityKnown.HasBody != hasBody)
                    {
                        entityKnown.IsStatic = data.isStatic;
                        entityKnown.IsProtected = data.isProtected;
                        entityKnown.HasBody = hasBody;

                        writer.Reset();
                        NetHeader.Write(writer, NetMessageType.EntityState, worldId, tick);
                        writer.Write(new EntityStateMessage
                        {
                            Guid = guid,
                            IsStatic = (byte)(data.isStatic ? 1 : 0),
                            IsProtected = (byte)(data.isProtected ? 1 : 0),
                            HasBody = (byte)(hasBody ? 1 : 0),
                        });
                        Send(NetDelivery.Reliable, writer);
                    }

                    if (!TransformEquals(entityKnown.Transform, data.transform))
                    {
                        entityKnown.Transform = data.transform;

                        writer.Reset();
                        NetHeader.Write(writer, NetMessageType.EntityTransform, worldId, tick);
                        writer.Write(new EntityTransformMessage { Guid = guid, Transform = data.transform });
                        // Reliable for now: the in-process channel never drops, and a lost final
                        // pose would leave a settled body drawn in the wrong place.
                        Send(NetDelivery.Reliable, writer);
                    }
                }
            }

            // Catch-up: the entities this connection did not know, every allocated brick of each.
            // A join of a large world is far too much to hold in one buffer, so the batch streams
            // it: build a chunk, send it, repeat.
            if (full.EntityCount > 0)
            {
                using (s_ReplicationFullBuildMarker.Auto())
                {
                    full.Prepare(worldId, tick, slotMask);
                }

                while (true)
                {
                    bool more;
                    using (s_ReplicationFullBuildMarker.Auto())
                    {
                        more = full.BuildNextChunk();
                    }

                    if (!more)
                    {
                        break;
                    }

                    for (int i = 0; i < full.Count; i++)
                    {
                        if (full[i].Length > 0)
                        {
                            Send(NetDelivery.Reliable, full.Slice(i));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Forwards the ranges of the shared delta's CURRENT chunk, minus the entities this
        /// connection already received in full earlier this tick — for those the delta is a subset
        /// of what already went out.
        /// </summary>
        internal void SendDelta(ReplicationBatch delta)
        {
            for (int i = 0; i < delta.Count; i++)
            {
                ReplicationRange range = delta[i];
                if (range.Length == 0 || sentFullThisTick.Contains(range.Guid))
                {
                    continue;
                }

                Send(NetDelivery.Reliable, delta.Slice(i));
            }
        }

        /// <summary>
        /// Whether this connection received that entity in full during this tick's
        /// <see cref="ReplicateWorld"/>. The server drops a delta entity no connection still needs.
        /// </summary>
        internal bool ReceivedFullThisTick(Guid128 guid)
            => sentFullThisTick.Contains(guid);

        private static bool TransformEquals(in RigidTransform a, in RigidTransform b)
        {
            return math.all(a.pos == b.pos) && math.all(a.rot.value == b.rot.value);
        }
    }
}
