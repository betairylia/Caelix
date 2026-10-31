using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;
using Caelix.Net;
using Caelix.Utils;

namespace Caelix.Simulation
{
    /// <summary>Handler for a command followed by a payload. The reader holds the payload bytes; <c>Remaining</c> is their length.</summary>
    public delegate void PayloadCommandHandler<T>(ServerConnection connection, CaelixWorld world, in T command, ref NetMessageReader payload) where T : unmanaged;

    /// <summary>
    /// Handler for a typed query. Returns the reply struct; bytes written to <paramref name="replyPayload"/>
    /// follow it on the wire. The writer is reset before the call.
    /// </summary>
    public delegate TReply QueryHandler<TRequest, TReply>(ServerConnection connection, CaelixWorld world, in TRequest request, ref NetMessageReader requestPayload, NetMessageWriter replyPayload)
        where TRequest : unmanaged where TReply : unmanaged;

    /// <summary>
    /// Owns the worlds, the fixed-step clock they share, the client connections, command
    /// dispatch, and replication. All worlds tick in lockstep. No GameObject; a bootstrap
    /// component drives <see cref="Update"/>.
    /// </summary>
    public sealed class CaelixServer : IDisposable
    {
        private delegate void CommandHandler(ServerConnection connection, CaelixWorld world, ref NetMessageReader reader);

        private delegate void QueryDispatch(ServerConnection connection, CaelixWorld world, uint requestId, ref NetMessageReader reader);

        private static readonly ProfilerMarker s_ProcessIncomingMarker = new("Server.ProcessIncoming");
        private static readonly ProfilerMarker s_TickSimulateMarker = new("Server.TickSimulate");
        private static readonly ProfilerMarker s_ReplicateMarker = new("Server.Replicate");
        private static readonly ProfilerMarker s_ReplicationBuildMarker = new("Server.ReplicationBuild");
        private static readonly ProfilerMarker s_EndTickMarker = new("Server.EndTick");
        private static readonly ProfilerMarker s_DrainEventsMarker = new("Server.DrainEvents");

        private readonly List<CaelixWorld> worlds = new();
        private readonly List<ServerConnection> connections = new();
        private readonly Dictionary<ushort, CommandHandler> commandHandlers = new();
        private readonly Dictionary<ushort, QueryDispatch> queryHandlers = new();
        private readonly NetMessageWriter writer = new(64 * 1024);
        private readonly NetMessageWriter replyPayloadWriter = new(64 * 1024);
        private readonly HashSet<Type> unregisteredEventTypesWarned = new();

        /// <summary>
        /// Phase A of replication for the world being replicated: the shared per-tick delta, packed
        /// and sent in chunks of <see cref="ReplicationChunkBytes"/>.
        /// </summary>
        private readonly ReplicationBatch deltaBatch = new();

        /// <summary>
        /// Phase A again, for the entities one connection has not seen yet. Rebuilt per connection
        /// and, like the delta, streamed in chunks rather than packed whole.
        /// </summary>
        private readonly ReplicationBatch fullBatch = new();

        private float accumulator;
        private int nextConnectionId = 1;
        private bool disposed;

        /// <summary>Shared type registry. Engine types are registered by the constructor.</summary>
        public NetTypeRegistry Types { get; }

        public IReadOnlyList<CaelixWorld> Worlds => worlds;
        public IReadOnlyList<ServerConnection> Connections => connections;
        public CaelixWorld DefaultWorld => worlds.Count > 0 ? worlds[0] : null;

        /// <summary>Ticks per second for every world. The fixed step is 1 / TickRate.</summary>
        public float TickRate { get; set; } = 100f;

        /// <summary>
        /// While frozen, <see cref="Tick"/> runs nothing after the first tick. <see cref="Step()"/>
        /// always runs.
        /// </summary>
        public bool Frozen { get; set; }

        /// <summary>
        /// Upper bound on ticks per <see cref="Update"/>; the backlog is dropped past it. Only the
        /// accumulator path uses this; a host that steps from <c>FixedUpdate</c> relies on
        /// <c>Time.maximumDeltaTime</c> instead.
        /// </summary>
        public int MaxTicksPerUpdate { get; set; } = 4;

        /// <summary>Number of completed server ticks.</summary>
        public uint TickIndex { get; private set; }

        public float FixedDeltaTime => TickRate > 0f ? 1f / TickRate : 0f;

        /// <summary>
        /// Bytes one replication chunk packs at most, for both batches. See
        /// <see cref="ReplicationBatch.MaxChunkBytes"/>.
        /// </summary>
        public int ReplicationChunkBytes
        {
            get => deltaBatch.MaxChunkBytes;
            set
            {
                deltaBatch.MaxChunkBytes = value;
                fullBatch.MaxChunkBytes = value;
            }
        }

        /// <summary>The shared per-tick delta batch. For tests and stats.</summary>
        public ReplicationBatch DeltaBatchForTests => deltaBatch;

        /// <summary>The per-connection catch-up batch. For tests and stats.</summary>
        public ReplicationBatch FullBatchForTests => fullBatch;

        public CaelixServer(NetTypeRegistry types = null)
        {
            Types = types ?? new NetTypeRegistry();
            EngineNetTypes.RegisterAll(Types);
            EngineNetTypes.RegisterServerHandlers(this);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            for (int i = 0; i < connections.Count; i++)
            {
                connections[i].Channel.Dispose();
            }

            connections.Clear();

            for (int i = 0; i < worlds.Count; i++)
            {
                worlds[i].Dispose();
            }

            worlds.Clear();

            deltaBatch.Dispose();
            fullBatch.Dispose();
        }

        #region Worlds

        public CaelixWorld CreateWorld(CaelixWorldConfig config)
        {
            if (config.worldId == NetHeader.NoWorld)
                throw new ArgumentException("The no-world sentinel cannot identify a world.", nameof(config));
            if (FindWorld(config.worldId) != null)
            {
                throw new InvalidOperationException($"World id {config.worldId} already exists.");
            }

            var world = new CaelixWorld(config);
            worlds.Add(world);
            return world;
        }

        /// <summary>
        /// Removes a world and disposes it. Connections learn about it on the next
        /// <see cref="Step()"/> through <see cref="NetMessageType.WorldRemove"/>.
        /// </summary>
        public bool RemoveWorld(CaelixWorld world)
        {
            if (!worlds.Remove(world))
            {
                return false;
            }

            world.Dispose();
            return true;
        }

        public CaelixWorld FindWorld(ushort worldId)
        {
            for (int i = 0; i < worlds.Count; i++)
            {
                if (worlds[i].Id == worldId) return worlds[i];
            }

            return null;
        }

        #endregion

        #region Connections

        /// <summary>
        /// Accepts a channel. The client gets the handshake and the full world state on the next
        /// tick's replication.
        /// </summary>
        public ServerConnection AddConnection(INetChannel channel)
        {
            var connection = new ServerConnection(nextConnectionId++, channel);
            connections.Add(connection);
            return connection;
        }

        public bool RemoveConnection(ServerConnection connection)
        {
            if (!connections.Remove(connection))
            {
                return false;
            }

            for (int i = 0; i < worlds.Count; i++)
            {
                worlds[i].ReleaseDrags(connection.Id);
            }

            return true;
        }

        /// <summary>
        /// Sends the handshake. It is not scoped to a world, so its header carries
        /// <see cref="NetHeader.NoWorld"/>; the worlds follow as WorldAdd messages.
        /// </summary>
        private void SendHello(ServerConnection connection)
        {
            writer.Reset();
            NetHeader.Write(writer, NetMessageType.Hello, NetHeader.NoWorld, TickIndex);
            writer.Write(new HelloMessage
            {
                TickRate = TickRate,
                RegistryHash = Types.Hash,
            });
            connection.Send(NetDelivery.Reliable, writer);
        }

        #endregion

        #region Commands and queries

        /// <summary>
        /// Registers a command type and its handler. The handler runs on the main thread before the
        /// tick in which the command takes effect.
        /// </summary>
        public void RegisterCommand<T>(Action<ServerConnection, CaelixWorld, T> handler) where T : unmanaged
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ushort id = Types.Register<T>();
            commandHandlers[id] = (ServerConnection connection, CaelixWorld world, ref NetMessageReader reader) =>
            {
                handler(connection, world, reader.Read<T>());
            };
        }

        /// <summary>
        /// Registers a command type whose message carries trailing payload bytes. The handler reads
        /// them from the reader it is given; <c>Remaining</c> is how many are left.
        /// </summary>
        public void RegisterCommand<T>(PayloadCommandHandler<T> handler) where T : unmanaged
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ushort id = Types.Register<T>();
            commandHandlers[id] = (ServerConnection connection, CaelixWorld world, ref NetMessageReader reader) =>
            {
                T command = reader.Read<T>();
                handler(connection, world, in command, ref reader);
            };
        }

        /// <summary>
        /// Registers a query type and its reply type, in that order, together with the handler that
        /// answers it. The reply struct and whatever the handler wrote to the reply payload writer
        /// are sent back to the asking connection.
        /// </summary>
        public void RegisterQuery<TRequest, TReply>(QueryHandler<TRequest, TReply> handler)
            where TRequest : unmanaged where TReply : unmanaged
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ushort requestTypeId = Types.Register<TRequest>();
            Types.Register<TReply>();
            queryHandlers[requestTypeId] = (ServerConnection connection, CaelixWorld world, uint requestId, ref NetMessageReader reader) =>
            {
                TRequest request = reader.Read<TRequest>();
                replyPayloadWriter.Reset();
                TReply reply = handler(connection, world, in request, ref reader, replyPayloadWriter);

                writer.Reset();
                NetHeader.Write(writer, NetMessageType.TypedQueryReply, world.Id, world.TickIndex);
                writer.Write(new TypedQueryHeader { TypeId = Types.GetId<TReply>(), RequestId = requestId });
                writer.Write(reply);
                writer.WriteBytes(replyPayloadWriter.AsSpan());
                connection.Send(NetDelivery.Reliable, writer);
            };
        }

        /// <summary>
        /// Drains commands and queries while running; while frozen only services queries.
        /// Step explicitly consumes commands even while frozen.
        /// </summary>
        public void ProcessIncoming()
            => ProcessIncoming(includeCommands: !Frozen);

        /// <summary>Services queries against committed state, leaving commands in the channel.</summary>
        public void ProcessQueries() => ProcessIncoming(includeCommands: false);

        private static readonly Predicate<byte[]> IsQuery = message =>
        {
            NetMessageType type = NetHeader.PeekType(message);
            return type == NetMessageType.Query || type == NetMessageType.TypedQuery;
        };

        private void ProcessIncoming(bool includeCommands)
        {
            using var _ = s_ProcessIncomingMarker.Auto();
            for (int c = 0; c < connections.Count; c++)
            {
                ServerConnection connection = connections[c];
                if (!connection.IsConnected) continue;
                ReceivedMessage message;
                while (includeCommands
                    ? connection.Channel.TryReceiveLease(out message)
                    : connection.Channel.TryReceiveLease(IsQuery, out message))
                {
                    try
                    {
                        Dispatch(connection, message);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                    finally { message.Dispose(); }
                }
            }
        }

        private void Dispatch(ServerConnection connection, ReceivedMessage message)
        {
            var reader = new NetMessageReader(message.Buffer, message.Length);
            NetHeader header = NetHeader.Read(ref reader);
            if (header.WorldId == NetHeader.NoWorld)
            {
                Debug.LogWarning(
                    $"[CaelixServer] World-agnostic message type {header.Type} from connection {connection.Id} has no handler.");
                return;
            }

            CaelixWorld world = FindWorld(header.WorldId);
            if (world == null)
            {
                return;
            }

            switch (header.Type)
            {
                case NetMessageType.Command:
                {
                    var typed = reader.Read<TypedPayloadHeader>();
                    if (commandHandlers.TryGetValue(typed.TypeId, out CommandHandler handler))
                    {
                        handler(connection, world, ref reader);
                    }
                    else
                    {
                        Debug.LogWarning($"[CaelixServer] Unhandled command type id {typed.TypeId}.");
                    }

                    break;
                }
                case NetMessageType.Query:
                {
                    var query = reader.Read<VoxelQueryMessage>();
                    AnswerVoxelQuery(connection, world, in query);
                    break;
                }
                case NetMessageType.TypedQuery:
                {
                    var h = reader.Read<TypedQueryHeader>();
                    if (queryHandlers.TryGetValue(h.TypeId, out QueryDispatch dispatch))
                    {
                        dispatch(connection, world, h.RequestId, ref reader);
                    }
                    else
                    {
                        Debug.LogWarning($"[CaelixServer] Unhandled query type id {h.TypeId}.");
                    }

                    break;
                }
                default:
                    Debug.LogWarning($"[CaelixServer] Unexpected message type {header.Type} from connection {connection.Id}.");
                    break;
            }
        }

        private void AnswerVoxelQuery(ServerConnection connection, CaelixWorld world, in VoxelQueryMessage query)
        {
            writer.Reset();
            NetHeader.Write(writer, NetMessageType.QueryReply, world.Id, world.TickIndex);
            int headerOffset = writer.Reserve<VoxelQueryReplyHeader>();

            bool found = false;
            if (world.TryGetEntity(query.Guid, out VoxelEntityData data))
            {
                // Writes nothing at all when no region holds the position, so the !found branch
                // below still decides what an absent voxel looks like on the wire.
                found = data.WriteVoxelSlots(writer, query.Position, query.SlotMask);
            }

            if (!found)
            {
                writer.Write((byte)0);
            }

            writer.Patch(headerOffset, new VoxelQueryReplyHeader
            {
                RequestId = query.RequestId,
                Guid = query.Guid,
                Position = query.Position,
                Found = (byte)(found ? 1 : 0),
            });
            connection.Send(NetDelivery.Reliable, writer);
        }

        #endregion

        #region Ticking

        /// <summary>
        /// Self-clocked driver: processes incoming commands, then runs as many fixed steps as the
        /// accumulated time allows. Call once per frame from a loop that has no fixed step of its
        /// own, such as a headless server. <c>CaelixHost</c> does not use this; it calls
        /// <see cref="Step()"/> once per Unity fixed step. Both paths share <see cref="Step()"/>.
        /// </summary>
        public void Update(float deltaTime)
        {
            ProcessIncoming();

            if (TickRate <= 0f)
            {
                return;
            }

            float dt = FixedDeltaTime;
            accumulator += Mathf.Max(0f, deltaTime);
            int steps = 0;
            while (accumulator >= dt && steps < MaxTicksPerUpdate)
            {
                if (!Tick())
                {
                    // Frozen past the first tick: nothing will run, so do not bank the time.
                    accumulator = 0f;
                    break;
                }

                accumulator -= dt;
                steps++;
            }

            if (steps >= MaxTicksPerUpdate)
            {
                // Drop the backlog rather than spiral.
                accumulator = 0f;
            }
        }

        /// <summary>
        /// Runs one tick unless the server is frozen. A frozen server still runs its very first
        /// tick, so a client always receives the initial state. Returns whether a tick ran.
        /// <see cref="Step()"/> is the unconditional form.
        /// </summary>
        public bool Tick()
        {
            if (Frozen && TickIndex > 0)
            {
                ProcessQueries();
                return false;
            }
            RunStep(includeCommands: !Frozen);
            return true;
        }

        /// <summary>
        /// Runs exactly one tick of every world and replicates the result. Works while frozen.
        /// Pending commands and queries are applied first, so a command sent before this call
        /// takes effect in this tick.
        /// </summary>
        public void Step()
            => RunStep(includeCommands: true);

        private void RunStep(bool includeCommands)
        {
            ProcessIncoming(includeCommands);

            for (int c = 0; c < connections.Count; c++)
            {
                if (!connections[c].HelloSent && connections[c].IsConnected)
                {
                    SendHello(connections[c]);
                    connections[c].HelloSent = true;
                }
            }

            // World lifecycle before any world replicates: WorldAdd must precede the first
            // BrickBatch of a world, and ReplicateWorld relies on the world already being known.
            for (int c = 0; c < connections.Count; c++)
            {
                if (connections[c].IsConnected)
                {
                    connections[c].SyncWorlds(worlds, writer, TickIndex);
                }
            }

            float dt = FixedDeltaTime;
            for (int w = 0; w < worlds.Count; w++)
            {
                CaelixWorld world = worlds[w];
                using (s_TickSimulateMarker.Auto()) world.TickSimulate(dt);
                using (s_ReplicateMarker.Auto()) Replicate(world);
                using (s_EndTickMarker.Auto()) world.EndTick();
            }

            TickIndex++;
        }

        public void Step(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Step();
            }
        }

        /// <summary>
        /// Replicates one world in two phases, phase B first. Phase B is
        /// <see cref="ServerConnection.ReplicateWorld"/>: per connection it diffs entity knowledge,
        /// packs every allocated brick of whatever that connection still has to catch up on into
        /// <see cref="fullBatch"/> and streams it. It records, per connection, which entities went
        /// out in full. Phase A then packs the tick's changed entities once, in parallel, into
        /// <see cref="deltaBatch"/> — minus the entities every connection just received in full —
        /// and streams them in chunks; every connection forwards a chunk before the next is built.
        /// </summary>
        private void Replicate(CaelixWorld world)
        {
            for (int c = 0; c < connections.Count; c++)
            {
                if (connections[c].IsConnected && connections[c].IsSubscribed(world.Id))
                {
                    connections[c].ReplicateWorld(world, writer, fullBatch);
                }
            }

            using (s_ReplicationBuildMarker.Auto())
            {
                deltaBatch.Clear();
                deltaBatch.CollectChangedEntities(world);
                DropDeltaEntitiesNobodyNeeds(world.Id);
                deltaBatch.Prepare(world.Id, world.TickIndex, world.Config.replicatedSlotMask);
            }

            while (true)
            {
                bool more;
                using (s_ReplicationBuildMarker.Auto())
                {
                    more = deltaBatch.BuildNextChunk();
                }

                if (!more)
                {
                    break;
                }

                for (int c = 0; c < connections.Count; c++)
                {
                    if (connections[c].IsConnected && connections[c].IsSubscribed(world.Id))
                    {
                        connections[c].SendDelta(deltaBatch);
                    }
                }
            }

            DrainEvents(world);
        }

        /// <summary>
        /// Drops every collected delta entity that each connected, subscribed connection already
        /// received in full this tick. On the tick a world is loaded that is all of them, and
        /// packing gigabytes nobody sends is what froze the Editor. With nobody to send to, the
        /// whole delta goes.
        /// </summary>
        private void DropDeltaEntitiesNobodyNeeds(ushort worldId)
        {
            for (int i = deltaBatch.EntityCount - 1; i >= 0; i--)
            {
                Guid128 guid = deltaBatch.GetEntity(i).Guid;
                bool needed = false;
                for (int c = 0; c < connections.Count; c++)
                {
                    ServerConnection connection = connections[c];
                    if (!connection.IsConnected || !connection.IsSubscribed(worldId))
                    {
                        continue;
                    }

                    if (!connection.ReceivedFullThisTick(guid))
                    {
                        needed = true;
                        break;
                    }
                }

                if (!needed)
                {
                    deltaBatch.RemoveEntityAtSwapBack(i);
                }
            }
        }

        private void DrainEvents(CaelixWorld world)
        {
            List<CaelixWorld.PendingEvent> events = world.PendingEvents;
            if (events.Count == 0)
            {
                return;
            }

            using var _ = s_DrainEventsMarker.Auto();

            for (int i = 0; i < events.Count; i++)
            {
                CaelixWorld.PendingEvent evt = events[i];
                if (!Types.TryGetId(evt.Type, out ushort typeId))
                {
                    if (unregisteredEventTypesWarned.Add(evt.Type))
                    {
                        Debug.LogWarning($"[CaelixServer] Event type {evt.Type.FullName} is not registered; dropped.");
                    }

                    continue;
                }

                writer.Reset();
                NetHeader.Write(writer, NetMessageType.Event, world.Id, world.TickIndex);
                writer.Write(new TypedPayloadHeader { TypeId = typeId });
                writer.WriteBytes(evt.Payload);

                for (int c = 0; c < connections.Count; c++)
                {
                    if (connections[c].IsConnected && connections[c].IsSubscribed(world.Id))
                    {
                        connections[c].Send(NetDelivery.Reliable, writer);
                    }
                }
            }

            events.Clear();
        }

        #endregion
    }
}
