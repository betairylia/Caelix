using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Unity.Profiling;
using Caelix.Net;
using Caelix.Simulation;
using Caelix.Utils;

namespace Caelix.Client
{
    /// <summary>
    /// One voxel's values as answered by a server query. Slots the server does not have for
    /// that sector are absent.
    /// </summary>
    public sealed class VoxelQueryReply
    {
        public Guid128 Guid;
        public int3 Position;
        public bool Found;
        public readonly List<(byte SlotId, byte[] Bytes)> Slots = new();

        public unsafe bool TryGetSlot<T>(SectorSlotId slotId, out T value) where T : unmanaged
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].SlotId != (byte)slotId) continue;
                byte[] bytes = Slots[i].Bytes;
                if (bytes.Length != UnsafeUtility.SizeOf<T>()) break;
                fixed (byte* src = bytes)
                {
                    UnsafeUtility.CopyPtrToStructure(src, out value);
                }

                return true;
            }

            value = default;
            return false;
        }
    }

    /// <summary>Receives a typed query reply. The reader holds the trailing payload; <c>Remaining</c> is its length.</summary>
    public delegate void QueryReplyHandler<TReply>(ushort worldId, in TReply reply, ref NetMessageReader payload) where TReply : unmanaged;

    /// <summary>
    /// The client end of the boundary: pumps the channel, applies replication into
    /// <see cref="ClientWorld"/> replicas, and sends commands and queries. Owns no simulation.
    /// </summary>
    public sealed class CaelixClient : IDisposable
    {
        private static readonly ProfilerMarker s_ClearRequireUpdateMarker = new("Client.ClearRequireUpdate");
        private static readonly ProfilerMarker s_ReceiveMarker = new("Client.Receive");
        private static readonly ProfilerMarker s_PropagateForRenderMarker = new("Client.PropagateForRender");
        private static readonly ProfilerMarker s_EndFrameMarker = new("Client.EndFrame");

        private delegate void EventHandler(ushort worldId, ref NetMessageReader reader);

        private delegate void TypedReplyDispatch(ushort worldId, ushort typeId, ref NetMessageReader reader);

        private readonly INetChannel channel;
        private readonly Dictionary<ushort, ClientWorld> worlds = new();
        private readonly List<ClientWorld> worldList = new();
        private readonly Dictionary<(ushort World, Guid128 Guid), VoxelEntity> authoredViews = new();
        private readonly Dictionary<ushort, EventHandler> eventHandlers = new();
        private readonly Dictionary<uint, Action<VoxelQueryReply>> pendingQueries = new();
        private readonly Dictionary<uint, TypedReplyDispatch> pendingTypedQueries = new();
        private readonly NetMessageWriter writer = new(4096);
        private readonly BrickReceiveBatch brickReceiveBatch;
        private uint nextRequestId = 1;
        private bool disposed;

        /// <summary>Shared type registry. Engine types are registered by the constructor.</summary>
        public NetTypeRegistry Types { get; }

        /// <summary>
        /// The host bootstrap that owns this client, when server and client share a process.
        /// Client-spawned view components use it to reach server data in host mode.
        /// </summary>
        public CaelixHost Host { get; set; }

        public bool IsConnected { get; private set; }
        public HelloMessage Hello { get; private set; }
        public uint LastServerTick { get; private set; }

        /// <summary>The default world (id 0), created on first use.</summary>
        public ClientWorld World => GetOrCreateWorld(0);

        public IReadOnlyList<ClientWorld> Worlds => worldList;

        /// <summary>
        /// Raised after a replica was created, either by a <see cref="NetMessageType.WorldAdd"/> or
        /// by the first message that named a world this client did not have yet.
        /// </summary>
        public event Action<ClientWorld> WorldAdded;

        /// <summary>
        /// Raised when the server dropped a world, before the replica is disposed. Renderers and
        /// tools release whatever they hold for its views here.
        /// </summary>
        public event Action<ClientWorld> WorldRemoving;

        public CaelixClient(INetChannel channel, NetTypeRegistry types = null)
        {
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            Types = types ?? new NetTypeRegistry();
            EngineNetTypes.RegisterAll(Types);
            brickReceiveBatch = new BrickReceiveBatch();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            brickReceiveBatch.Dispose();

            for (int i = 0; i < worldList.Count; i++)
            {
                WorldRemoving?.Invoke(worldList[i]);
                worldList[i].Dispose();
            }

            worldList.Clear();
            worlds.Clear();
            authoredViews.Clear();
            channel.Dispose();
        }

        /// <summary>
        /// The replica of <paramref name="worldId"/>, created on first use with the default slot
        /// mask. A <see cref="NetMessageType.WorldAdd"/> normally arrives first and corrects the
        /// mask; a message for an unknown world still gets a replica so nothing is lost.
        /// </summary>
        public ClientWorld GetOrCreateWorld(ushort worldId)
        {
            if (!worlds.TryGetValue(worldId, out ClientWorld world))
            {
                world = new ClientWorld(this, worldId, BrickReplication.DefaultReplicatedSlotMask);
                worlds.Add(worldId, world);
                worldList.Add(world);
                foreach (var entry in authoredViews)
                {
                    if (entry.Key.World == worldId && entry.Value != null)
                        world.RegisterAuthored(entry.Value);
                }
                WorldAdded?.Invoke(world);
            }

            return world;
        }

        /// <summary>Drops the replica of <paramref name="id"/>, if this client has one.</summary>
        private void RemoveWorld(ushort id)
        {
            if (!worlds.TryGetValue(id, out ClientWorld world))
            {
                return;
            }

            WorldRemoving?.Invoke(world);
            world.Dispose();
            worlds.Remove(id);
            worldList.Remove(world);
        }

        #region Authored views

        public void RegisterAuthoredView(VoxelEntity component, ushort worldId = 0)
        {
            if (component == null) return;
            authoredViews[(worldId, component.PersistentGuid)] = component;
            GetOrCreateWorld(worldId).RegisterAuthored(component);
        }

        public void UnregisterAuthoredView(VoxelEntity component, ushort worldId = 0)
        {
            if (component == null) return;
            var key = (worldId, component.PersistentGuid);
            if (authoredViews.TryGetValue(key, out VoxelEntity authored) && authored == component)
                authoredViews.Remove(key);
            if (worlds.TryGetValue(worldId, out ClientWorld world))
            {
                world.UnregisterAuthored(component);
            }
        }

        #endregion

        #region Frame

        /// <summary>
        /// Brings every replica up to the latest tick that arrived: clears last frame's
        /// require-update flags, then applies every pending message. Call once per frame before
        /// <see cref="PrepareRender"/>.
        /// </summary>
        public void Receive()
        {
            using (s_ClearRequireUpdateMarker.Auto())
            {
                for (int i = 0; i < worldList.Count; i++)
                {
                    worldList[i].ClearRequireUpdate();
                }
            }

            using (s_ReceiveMarker.Auto())
            {
                try
                {
                    while (channel.TryReceiveLease(out ReceivedMessage message))
                    {
                        try
                        {
                            Apply(ref message);
                        }
                        catch (Exception exception)
                        {
                            Debug.LogException(exception);
                        }
                        finally { message.Dispose(); }
                    }
                }
                finally
                {
                    brickReceiveBatch.Flush();
                }
            }
        }

        /// <summary>
        /// Turns this frame's applied bricks into require-update flags for the renderers. Call
        /// after <see cref="Receive"/> and before the renderers run.
        /// </summary>
        public void PrepareRender()
        {
            using (s_PropagateForRenderMarker.Auto())
            {
                for (int i = 0; i < worldList.Count; i++)
                {
                    worldList[i].PropagateForRender();
                }
            }
        }

        public void EndFrame()
        {
            using var _ = s_EndFrameMarker.Auto();
            for (int i = 0; i < worldList.Count; i++)
            {
                worldList[i].EndFrame();
            }
        }

        private void Apply(ref ReceivedMessage message)
        {
            var reader = new NetMessageReader(message.Buffer, message.Length);
            NetHeader header = NetHeader.Read(ref reader);
            // All observable messages are ordering barriers: callbacks and lifecycle handlers
            // must see preceding voxel writes, and may replace or free their storage.
            if (header.Type != NetMessageType.BrickBatch) brickReceiveBatch.Flush();
            if (header.Tick > LastServerTick)
            {
                LastServerTick = header.Tick;
            }

            // The sentinel world id belongs to Hello alone; every other message is world-scoped.
            if (header.WorldId == NetHeader.NoWorld && header.Type != NetMessageType.Hello)
            {
                Debug.LogWarning($"[CaelixClient] Message type {header.Type} arrived with no world id; dropped.");
                return;
            }

            switch (header.Type)
            {
                case NetMessageType.Hello:
                {
                    Hello = reader.Read<HelloMessage>();
                    IsConnected = true;
                    if (Hello.RegistryHash != Types.Hash)
                    {
                        Debug.LogError(
                            "[CaelixClient] Net type registry mismatch with the server. Commands and events " +
                            "must be registered in the same order on both ends.");
                    }

                    break;
                }
                case NetMessageType.WorldAdd:
                {
                    var m = reader.Read<WorldAddMessage>();
                    ClientWorld world = GetOrCreateWorld(header.WorldId);
                    world.ReplicatedSlotMask = m.ReplicatedSlotMask;
                    break;
                }
                case NetMessageType.WorldRemove:
                {
                    reader.Read<WorldRemoveMessage>();
                    RemoveWorld(header.WorldId);
                    break;
                }
                case NetMessageType.EntitySpawn:
                {
                    var m = reader.Read<EntitySpawnMessage>();
                    GetOrCreateWorld(header.WorldId).OnSpawn(in m);
                    break;
                }
                case NetMessageType.EntityDespawn:
                {
                    var m = reader.Read<EntityDespawnMessage>();
                    GetOrCreateWorld(header.WorldId).OnDespawn(in m);
                    break;
                }
                case NetMessageType.EntityState:
                {
                    var m = reader.Read<EntityStateMessage>();
                    GetOrCreateWorld(header.WorldId).OnState(in m);
                    break;
                }
                case NetMessageType.EntityTransform:
                {
                    var m = reader.Read<EntityTransformMessage>();
                    GetOrCreateWorld(header.WorldId).OnTransform(in m);
                    break;
                }
                case NetMessageType.BrickBatch:
                {
                    var m = reader.Read<BrickBatchHeader>();
                    // Creating a world raises a user callback, so it is also an apply barrier.
                    if (!worlds.ContainsKey(header.WorldId)) brickReceiveBatch.Flush();
                    // Unknown entity: the whole message is dropped; nothing reads the payload.
                    if (GetOrCreateWorld(header.WorldId).TryGetView(m.Guid, out EntityView view))
                    {
                        brickReceiveBatch.Add(view.Data, ref message, reader.Position, m.BrickCount);
                        ClientWorld.MarkBricksApplied(view);
                    }

                    break;
                }
                case NetMessageType.Event:
                {
                    var typed = reader.Read<TypedPayloadHeader>();
                    if (eventHandlers.TryGetValue(typed.TypeId, out EventHandler handler))
                    {
                        handler(header.WorldId, ref reader);
                    }

                    break;
                }
                case NetMessageType.QueryReply:
                {
                    var h = reader.Read<VoxelQueryReplyHeader>();
                    var reply = new VoxelQueryReply { Guid = h.Guid, Position = h.Position, Found = h.Found != 0 };
                    int count = reader.Read<byte>();
                    for (int i = 0; i < count; i++)
                    {
                        byte slotId = reader.Read<byte>();
                        int stride = reader.Read<ushort>();
                        reply.Slots.Add((slotId, reader.ReadSpan(stride).ToArray()));
                    }

                    if (pendingQueries.TryGetValue(h.RequestId, out Action<VoxelQueryReply> callback))
                    {
                        pendingQueries.Remove(h.RequestId);
                        callback?.Invoke(reply);
                    }

                    break;
                }
                case NetMessageType.TypedQueryReply:
                {
                    var h = reader.Read<TypedQueryHeader>();
                    if (pendingTypedQueries.TryGetValue(h.RequestId, out TypedReplyDispatch dispatch))
                    {
                        pendingTypedQueries.Remove(h.RequestId);
                        dispatch(header.WorldId, h.TypeId, ref reader);
                    }

                    break;
                }
                default:
                    Debug.LogWarning($"[CaelixClient] Unexpected message type {header.Type}.");
                    break;
            }
        }

        #endregion

        #region Commands, queries, events

        /// <summary>Sends a registered command to the server. Applied before the server's next tick.</summary>
        public void SendCommand<T>(in T command, ushort worldId = 0) where T : unmanaged
        {
            ushort typeId = Types.GetId<T>();
            writer.Reset();
            NetHeader.Write(writer, NetMessageType.Command, worldId, LastServerTick);
            writer.Write(new TypedPayloadHeader { TypeId = typeId });
            writer.Write(command);
            channel.Send(NetDelivery.Reliable, writer.AsSpan());
        }

        /// <summary>
        /// Sends a registered command with trailing payload bytes. The server handler registered
        /// with <c>CaelixServer.RegisterCommand&lt;T&gt;(PayloadCommandHandler&lt;T&gt;)</c> reads them.
        /// </summary>
        public void SendCommand<T>(in T command, ReadOnlySpan<byte> payload, ushort worldId = 0) where T : unmanaged
        {
            ushort typeId = Types.GetId<T>();
            writer.Reset();
            NetHeader.Write(writer, NetMessageType.Command, worldId, LastServerTick);
            writer.Write(new TypedPayloadHeader { TypeId = typeId });
            writer.Write(command);
            writer.WriteBytes(payload);
            channel.Send(NetDelivery.Reliable, writer.AsSpan());
        }

        /// <summary>
        /// Sends a typed query and calls <paramref name="onReply"/> when the reply arrives during a
        /// later <see cref="Receive"/>. Both the request and the reply may carry a trailing payload.
        /// </summary>
        public void SendQuery<TRequest, TReply>(
            in TRequest request,
            ReadOnlySpan<byte> payload,
            QueryReplyHandler<TReply> onReply,
            ushort worldId = 0)
            where TRequest : unmanaged where TReply : unmanaged
        {
            uint requestId = nextRequestId++;
            pendingTypedQueries[requestId] = (ushort replyWorldId, ushort typeId, ref NetMessageReader reader) =>
            {
                if (typeId != Types.GetId<TReply>())
                {
                    Debug.LogError(
                        $"[CaelixClient] Query reply type id {typeId} does not match the expected " +
                        $"{typeof(TReply).FullName}; dropped.");
                    return;
                }

                TReply reply = reader.Read<TReply>();
                onReply?.Invoke(replyWorldId, in reply, ref reader);
            };

            writer.Reset();
            NetHeader.Write(writer, NetMessageType.TypedQuery, worldId, LastServerTick);
            writer.Write(new TypedQueryHeader { TypeId = Types.GetId<TRequest>(), RequestId = requestId });
            writer.Write(request);
            writer.WriteBytes(payload);
            channel.Send(NetDelivery.Reliable, writer.AsSpan());
        }

        public void SetBlock(Guid128 entity, int3 position, Block block, ushort worldId = 0)
        {
            SendCommand(new SetBlockCommand { Entity = entity, Position = position, Block = block }, worldId);
        }

        public void SetEntityStatic(Guid128 entity, bool isStatic, ushort worldId = 0)
        {
            SendCommand(new SetEntityStaticCommand { Entity = entity, IsStatic = (byte)(isStatic ? 1 : 0) }, worldId);
        }

        /// <summary>
        /// Asks the server to create an empty entity with a guid this client chose, so that commands
        /// sent in the same frame can already address it. Refused when the guid is already in use.
        /// </summary>
        public void SpawnEntity(Guid128 guid, RigidTransform transform, bool isStatic, bool hasBody, ushort worldId = 0)
        {
            SendCommand(
                new SpawnEntityCommand
                {
                    Guid = guid,
                    Transform = transform,
                    IsStatic = (byte)(isStatic ? 1 : 0),
                    HasBody = (byte)(hasBody ? 1 : 0),
                },
                worldId);
        }

        public void AddForce(in VoxelBodyForceCommand command, ushort worldId = 0)
        {
            SendCommand(in command, worldId);
        }

        /// <summary>
        /// Starts or refreshes a held drag. Send it whenever the target moves; the server keeps the
        /// latest and applies the spring every tick. Resend within the world's drag timeout to keep
        /// it alive.
        /// </summary>
        public void SetDrag(in DragCommand command, ushort worldId = 0)
        {
            SendCommand(in command, worldId);
        }

        public void ReleaseDrag(Guid128 entity, ushort worldId = 0)
        {
            SendCommand(new ReleaseDragCommand { Entity = entity }, worldId);
        }

        /// <summary>Asks the server for one voxel's slot values. The callback runs when the reply arrives.</summary>
        public void QueryVoxel(Guid128 entity, int3 position, ushort slotMask, Action<VoxelQueryReply> callback, ushort worldId = 0)
        {
            uint requestId = nextRequestId++;
            pendingQueries[requestId] = callback;
            writer.Reset();
            NetHeader.Write(writer, NetMessageType.Query, worldId, LastServerTick);
            writer.Write(new VoxelQueryMessage
            {
                RequestId = requestId,
                Guid = entity,
                Position = position,
                SlotMask = slotMask,
            });
            channel.Send(NetDelivery.Reliable, writer.AsSpan());
        }

        /// <summary>Registers an event type and its handler. Handlers run on the main thread during <see cref="Receive"/>.</summary>
        public void RegisterEvent<T>(Action<ushort, T> handler) where T : unmanaged
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ushort id = Types.Register<T>();
            eventHandlers[id] = (ushort worldId, ref NetMessageReader reader) => handler(worldId, reader.Read<T>());
        }

        #endregion
    }
}
