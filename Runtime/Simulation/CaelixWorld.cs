using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using Caelix.IO;
using Caelix.Net;
using Caelix.Tick;
using Caelix.Utils;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Caelix.Simulation
{
    /// <summary>
    /// Construction-time and live-tunable settings of one <see cref="CaelixWorld"/>.
    /// </summary>
    [Serializable]
    public struct CaelixWorldConfig
    {
        public ushort worldId;
        public string name;
        public PhysicsWorldSettings physics;

        /// <summary>One bit per slot id. Only these slots are sent to clients. Default: Block only.</summary>
        public ushort replicatedSlotMask;

        /// <summary>Propagate dirtiness between entities through the post-physics brick-overlap graph.</summary>
        public bool doAlienPropagation;

        /// <summary>Flags a moving (non-static) entity's bricks hand to their alien neighbors.</summary>
        public BrickUpdateFlags alienMotionDirtyMask;

        /// <summary>Query every allocated brick of every non-static entity, not only the dirty ones.</summary>
        public bool alienIncludeMovingBricks;

        /// <summary>
        /// A held drag that a client has not refreshed for this long is released. Covers lost
        /// release commands and disconnects.
        /// </summary>
        public float dragTimeoutSeconds;

        public static CaelixWorldConfig Default(ushort worldId = 0, string name = "World")
        {
            return new CaelixWorldConfig
            {
                worldId = worldId,
                name = name,
                physics = PhysicsWorldSettings.Default,
                replicatedSlotMask = BrickReplication.DefaultReplicatedSlotMask,
                doAlienPropagation = false,
                alienMotionDirtyMask = BrickUpdateFlags.Automata0,
                alienIncludeMovingBricks = true,
                dragTimeoutSeconds = 0.5f,
            };
        }
    }

    /// <summary>
    /// Exclusive CPU timing buckets from the last completed world tick. Tick excludes the other
    /// two, so the three times sum to TotalMilliseconds.
    /// </summary>
    public struct TickTimingStats
    {
        public bool IsCreated;
        public double TickMilliseconds;
        public double PhysicsMilliseconds;
        public double BrickGraphMilliseconds;
        public double TotalMilliseconds;
    }

    /// <summary>
    /// One authoritative simulation world: entities, bodies, automata, physics, save and load.
    /// A plain class with no GameObject. The entity and body maps are the source of truth and
    /// are updated in place. See <c>Documentation~/SERVER_CLIENT_ARCHITECTURE.md</c>.
    /// </summary>
    public sealed class CaelixWorld : IDisposable, IWorldLoadTarget
    {
        #region ProfilerMarkers

        private static readonly ProfilerMarker s_PrepareTickMarker = new("Prepare Tick");
        private static readonly ProfilerMarker s_BeginAutomataWritesMarker = new("Begin automata writes");
        private static readonly ProfilerMarker s_CollectRequireUpdateBricksMarker = new("Collect RequireUpdate Bricks");
        private static readonly ProfilerMarker s_BuildAlienReadContextMarker = new("Build Alien Read Context");
        private static readonly ProfilerMarker s_AutomataStageScheduleMarker = new("Automata Stage Schedule");
        private static readonly ProfilerMarker s_WorkDispatchMarker = new("Work Dispatch");
        private static readonly ProfilerMarker s_EndAutomataWritesMarker = new("End automata writes");
        private static readonly ProfilerMarker s_DirtyPropagationMarker = new("Dirty Propagation");
        private static readonly ProfilerMarker s_ClearRequireUpdatesMarker = new("Clear Require Updates");
        private static readonly ProfilerMarker s_PropagateDirtyFlagsMarker = new("Propagate Dirty Flags");
        private static readonly ProfilerMarker s_BurstMarker = new("Burst");
        private static readonly ProfilerMarker s_MarkNonEmptyBlocksMarker = new("Mark Non-empty Blocks");
        private static readonly ProfilerMarker s_RecomputeBodyMassPropertiesMarker = new("Recompute body mass properties");
        private static readonly ProfilerMarker s_ApplyBodyForceCommandsMarker = new("Apply Body Force Commands");
        private static readonly ProfilerMarker s_PhysicsStepMarker = new("Physics Step");
        private static readonly ProfilerMarker s_AlienPropagationMarker = new("Alien Propagation");
        private static readonly ProfilerMarker s_ClearDirtyFlagsMarker = new("Clear Dirty Flags");

        #endregion

        public struct AutomataStageInputs
        {
            public NativeHashMap<Guid128, VoxelEntityData> VoxelEntities;

            /// <summary>
            /// Every brick with a simulation bit (<see cref="BrickUpdateFlags.AutomataMask"/>) pending,
            /// including flagged positions that hold no brick yet. Each hook selects its own records by
            /// testing <see cref="RequiredBrick.Flags"/> against its channel (and
            /// <see cref="RequiredBrick.IsAllocated"/> if it cannot work on empty positions) at the top
            /// of its job. Bricks with geometry-only work are not in the list.
            /// </summary>
            public NativeList<RequiredBrick> BricksRequiredUpdate;

            /// <summary>
            /// <c>BricksRequiredUpdate.Length</c>, read by the world before the stage runs. Test this
            /// to skip an empty tick: once an earlier hook has scheduled a job over the list, reading
            /// its length on the main thread is a safety violation.
            /// </summary>
            public int BricksRequiredUpdateCount;
            public AutomataReadContext ReadContext;
        }

        internal struct PendingEvent
        {
            public Type Type;
            public byte[] Payload;
        }

        public ushort Id => Config.worldId;
        public string Name => Config.name;

        /// <summary>Live-tunable settings. Physics settings are pushed into <see cref="Physics"/> at every tick.</summary>
        public CaelixWorldConfig Config;

        /// <summary>
        /// The world's entities and bodies. Public so physics jobs can take it by reference.
        /// Mutate entities through the methods on this class; the maps themselves must not be
        /// replaced.
        /// </summary>
        public PhysicsStepInputs Data;

        public VoxelPhysicsWorld Physics { get; }
        public TickStage<AutomataStageInputs> AutomataStage { get; }
        public VoxelBodyForceCommandStream Forces => Physics.BodyForceCommands;

        /// <summary>Number of completed ticks.</summary>
        public uint TickIndex { get; private set; }

        public bool IsDisposed => disposed;

        public TickTimingStats LastTickTimings { get; private set; }
        public BrickOverlapPropagationStats LastBrickOverlapPropagationStats { get; private set; }

        /// <summary>
        /// Read-only view of the brick-overlap graph published by the alien propagation stage.
        /// Earlier tick stages see the graph of the previous tick.
        /// </summary>
        public BrickOverlapGraph BrickOverlapGraph => Physics.BrickOverlapGraph;

        /// <summary>Events emitted since the last drain. The server drains them after every tick.</summary>
        internal readonly List<PendingEvent> PendingEvents = new();

        private struct DragState
        {
            public DragCommand Command;
            public uint RefreshedAtTick;
        }

        // Held drags, one per (client, body). Applied as a spring every tick until released or
        // stale. Owner id 0 is reserved for in-process callers with no connection.
        private readonly Dictionary<(int Owner, Guid128 Entity), DragState> drags = new();
        private readonly List<(int Owner, Guid128 Entity)> dragScratch = new();

        private AutomataStageInputs automataTickBuf;
        private NativeList<AlienEntityView> alienEntityViews;
        private bool disposed;
        private long nextEntityInstanceId;
        private readonly Dictionary<Guid128, long> entityInstances = new();

        internal long GetEntityInstanceId(Guid128 guid) => entityInstances[guid];

        public CaelixWorld(CaelixWorldConfig config)
        {
            Config = config;
            if (Config.replicatedSlotMask == 0)
            {
                Config.replicatedSlotMask = BrickReplication.DefaultReplicatedSlotMask;
            }

            Data.VoxelEntities = new NativeHashMap<Guid128, VoxelEntityData>(1, Allocator.Persistent);
            Data.VoxelBodies = new NativeHashMap<Guid128, VoxelBodyData>(1, Allocator.Persistent);
            Data.nDynamicBodies = 0;

            automataTickBuf.BricksRequiredUpdate = new NativeList<RequiredBrick>(Allocator.Persistent);
            alienEntityViews = new NativeList<AlienEntityView>(Allocator.Persistent);

            Physics = new VoxelPhysicsWorld(config.physics);
            AutomataStage = new TickStage<AutomataStageInputs>();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            using (var keys = Data.VoxelEntities.GetKeyArray(Allocator.Temp))
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    RemoveEntity(keys[i]);
                }
            }

            Data.VoxelEntities.Dispose();
            Data.VoxelBodies.Dispose();
            automataTickBuf.BricksRequiredUpdate.Dispose();
            alienEntityViews.Dispose();
            AutomataStage.Dispose();
            Physics.Dispose();
        }

        #region Entities

        public int EntityCount => Data.VoxelEntities.Count;

        /// <summary>Read-only enumeration of the entity map. Do not hold across a mutation.</summary>
        public NativeHashMap<Guid128, VoxelEntityData>.ReadOnly Entities => Data.VoxelEntities.AsReadOnly();

        public NativeArray<Guid128> GetEntityKeys(Allocator allocator) => Data.VoxelEntities.GetKeyArray(allocator);

        public bool HasEntity(Guid128 guid) => Data.VoxelEntities.ContainsKey(guid);

        public bool TryGetEntity(Guid128 guid, out VoxelEntityData data) => Data.VoxelEntities.TryGetValue(guid, out data);

        public VoxelEntityData GetEntity(Guid128 guid)
        {
            if (!Data.VoxelEntities.TryGetValue(guid, out VoxelEntityData data))
            {
                throw new KeyNotFoundException($"Entity {guid} is not in world {Name}.");
            }

            return data;
        }

        /// <summary>
        /// Writes scalar fields (transform, flags) back. The storage inside the record is shared,
        /// so writes through a copy are already visible; only scalars need this.
        /// </summary>
        public void SetEntity(Guid128 guid, in VoxelEntityData data)
        {
            if (!Data.VoxelEntities.ContainsKey(guid))
            {
                throw new KeyNotFoundException($"Entity {guid} is not in world {Name}.");
            }

            Data.VoxelEntities[guid] = data;
        }

        /// <summary>Creates an entity with no storage. Returns false if the guid is taken.</summary>
        public bool CreateEntity(
            Guid128 guid,
            RigidTransform transform,
            bool isStatic,
            bool isProtected = false,
            bool excludeFromSave = false)
        {
            if (guid.IsZero)
            {
                throw new ArgumentException("Entity guid must not be zero.", nameof(guid));
            }

            if (Data.VoxelEntities.ContainsKey(guid))
            {
                return false;
            }

            var data = new VoxelEntityData(Allocator.Persistent)
            {
                Guid = guid,
                transform = transform,
                previousTransform = transform,
                isStatic = isStatic,
                isProtected = isProtected,
                excludeFromSave = excludeFromSave,
            };
            Data.VoxelEntities.Add(guid, data);
            entityInstances.Add(guid, ++nextEntityInstanceId);
            return true;
        }

        /// <summary>Removes an entity and its body, disposing all of its storage.</summary>
        public bool RemoveEntity(Guid128 guid)
        {
            if (!Data.VoxelEntities.TryGetValue(guid, out VoxelEntityData data))
            {
                return false;
            }

            RemoveBody(guid);
            data.Dispose();
            Data.VoxelEntities.Remove(guid);
            entityInstances.Remove(guid);

            dragScratch.Clear();
            foreach (var kvp in drags)
            {
                if (kvp.Key.Entity == guid) dragScratch.Add(kvp.Key);
            }

            for (int i = 0; i < dragScratch.Count; i++)
            {
                drags.Remove(dragScratch[i]);
            }

            return true;
        }

        /// <summary>
        /// Moves an entity. With <paramref name="teleport"/> the previous transform is overwritten
        /// too, so dirty propagation does not see a sweep from the old pose.
        /// </summary>
        public void SetEntityTransform(Guid128 guid, RigidTransform transform, bool teleport = true)
        {
            VoxelEntityData data = GetEntity(guid);
            data.transform = transform;
            if (teleport)
            {
                data.previousTransform = transform;
            }

            Data.VoxelEntities[guid] = data;
        }

        /// <summary>Freezes or releases an entity. Freezing also stops its body.</summary>
        public void SetEntityStatic(Guid128 guid, bool isStatic)
        {
            VoxelEntityData data = GetEntity(guid);
            if (data.isStatic == isStatic)
            {
                return;
            }

            data.isStatic = isStatic;
            Data.VoxelEntities[guid] = data;

            if (isStatic)
            {
                SetBodyVelocity(guid, float3.zero, float3.zero);
            }
        }

        public void SetEntityProtected(Guid128 guid, bool isProtected)
        {
            VoxelEntityData data = GetEntity(guid);
            data.isProtected = isProtected;
            Data.VoxelEntities[guid] = data;
        }

        #endregion

        #region Bodies

        public bool HasBody(Guid128 guid) => Data.VoxelBodies.ContainsKey(guid);

        public bool TryGetBody(Guid128 guid, out VoxelBodyData body) => Data.VoxelBodies.TryGetValue(guid, out body);

        /// <summary>Adds a body to an existing entity. Returns false if the entity is missing or already has one.</summary>
        public bool AddBody(Guid128 guid, bool accuratePhysics = true)
        {
            if (!Data.VoxelEntities.ContainsKey(guid) || Data.VoxelBodies.ContainsKey(guid))
            {
                return false;
            }

            Data.VoxelBodies.Add(guid, VoxelBodyData.Create(Allocator.Persistent, accuratePhysics));
            return true;
        }

        public bool RemoveBody(Guid128 guid)
        {
            if (!Data.VoxelBodies.TryGetValue(guid, out VoxelBodyData body))
            {
                return false;
            }

            body.Dispose();
            Data.VoxelBodies.Remove(guid);
            return true;
        }

        public void SetBodyAccuratePhysics(Guid128 guid, bool accuratePhysics)
        {
            if (Data.VoxelBodies.TryGetValue(guid, out VoxelBodyData body) && body.accuratePhysics != accuratePhysics)
            {
                body.accuratePhysics = accuratePhysics;
                Data.VoxelBodies[guid] = body;
            }
        }

        /// <summary>Overwrites a body's velocity. No-op for entities without a body.</summary>
        public void SetBodyVelocity(Guid128 guid, float3 linearVelocity, float3 angularVelocity)
        {
            if (!Data.VoxelBodies.TryGetValue(guid, out VoxelBodyData body))
            {
                return;
            }

            body.motionVelocity.LinearVelocity = linearVelocity;
            body.motionVelocity.AngularVelocity = angularVelocity;
            Data.VoxelBodies[guid] = body;
        }

        #endregion

        #region Voxels

        public Block GetBlock(Guid128 guid, int3 position)
        {
            return Data.VoxelEntities.TryGetValue(guid, out VoxelEntityData data) ? data.GetBlock(position) : Block.Empty;
        }

        public T GetSlot<T>(Guid128 guid, SectorSlotId slotId, int3 position) where T : unmanaged
        {
            return Data.VoxelEntities.TryGetValue(guid, out VoxelEntityData data) ? data.GetSlot<T>(slotId, position) : default;
        }

        /// <summary>Writes one block. Storage is shared, so no write-back is needed.</summary>
        public bool SetBlock(Guid128 guid, int3 position, Block block)
        {
            if (!Data.VoxelEntities.TryGetValue(guid, out VoxelEntityData data))
            {
                return false;
            }

            data.SetBlock(position, block);
            return true;
        }

        public bool SetSlot<T>(Guid128 guid, SectorSlotId slotId, int3 position, T value)
            where T : unmanaged, IEquatable<T>
        {
            if (!Data.VoxelEntities.TryGetValue(guid, out VoxelEntityData data))
            {
                return false;
            }

            data.SetSlot(slotId, position, value);
            return true;
        }

        #endregion

        #region Drags

        /// <summary>Number of held drags across all clients.</summary>
        public int DragCount => drags.Count;

        /// <summary>
        /// Starts or refreshes a drag. Ignored for missing entities, entities without a body, and
        /// protected entities. A frozen body keeps its drag and is pulled once it is released.
        /// </summary>
        public void SetDrag(int ownerId, in DragCommand command)
        {
            if (!TryGetEntity(command.Entity, out VoxelEntityData entity) || entity.isProtected || !HasBody(command.Entity))
            {
                return;
            }

            drags[(ownerId, command.Entity)] = new DragState { Command = command, RefreshedAtTick = TickIndex };
        }

        public bool ReleaseDrag(int ownerId, Guid128 entity)
        {
            return drags.Remove((ownerId, entity));
        }

        /// <summary>Releases every drag held by one client, for example on disconnect.</summary>
        public void ReleaseDrags(int ownerId)
        {
            dragScratch.Clear();
            foreach (var kvp in drags)
            {
                if (kvp.Key.Owner == ownerId) dragScratch.Add(kvp.Key);
            }

            for (int i = 0; i < dragScratch.Count; i++)
            {
                drags.Remove(dragScratch[i]);
            }
        }

        /// <summary>
        /// Turns every held drag into this tick's spring force, computed from the body's current
        /// pose and velocity. Runs after mass properties are fresh and before force commands apply.
        /// </summary>
        private void ApplyDrags(float dt)
        {
            if (drags.Count == 0)
            {
                return;
            }

            uint timeoutTicks = (uint)math.max(1, (int)math.ceil(Config.dragTimeoutSeconds / math.max(dt, 1e-6f)));
            dragScratch.Clear();

            foreach (var kvp in drags)
            {
                if (TickIndex - kvp.Value.RefreshedAtTick > timeoutTicks)
                {
                    dragScratch.Add(kvp.Key);
                    continue;
                }

                DragCommand cmd = kvp.Value.Command;
                if (!TryGetEntity(cmd.Entity, out VoxelEntityData entity) ||
                    !TryGetBody(cmd.Entity, out VoxelBodyData body) ||
                    entity.isStatic || body.massProperties.mass <= 0f)
                {
                    continue;
                }

                // The centre-of-mass anchor is the stable hold for a scripted grab: it neither spins
                // the body nor depends on which face a camera happens to see.
                float3 anchorLocal = cmd.AnchorAtCenterOfMass != 0
                    ? body.massProperties.centerOfMass
                    : cmd.AnchorLocal;
                float3 anchorWorld = math.transform(entity.transform, anchorLocal);
                float3 centerOfMassWorld = math.transform(entity.transform, body.massProperties.centerOfMass);
                float3 angularVelocityWorld = math.rotate(entity.transform.rot, body.motionVelocity.AngularVelocity);
                float3 anchorVelocity = body.motionVelocity.LinearVelocity +
                                        math.cross(angularVelocityWorld, anchorWorld - centerOfMassWorld);

                float3 acceleration = (cmd.TargetWorld - anchorWorld) * cmd.Spring - anchorVelocity * cmd.Damping;
                if (cmd.MaxAcceleration > 0f)
                {
                    float length = math.length(acceleration);
                    if (length > cmd.MaxAcceleration)
                    {
                        acceleration *= cmd.MaxAcceleration / length;
                    }
                }

                Forces.AddForceAtPosition(cmd.Entity, acceleration, anchorWorld, VoxelBodyForceMode.Acceleration);
            }

            for (int i = 0; i < dragScratch.Count; i++)
            {
                drags.Remove(dragScratch[i]);
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Queues a blittable event for every connected client. The type must be registered on both
        /// ends (see <see cref="CaelixServer.Types"/>). Main thread only.
        /// </summary>
        public unsafe void EmitEvent<T>(T evt) where T : unmanaged
        {
            int size = UnsafeUtility.SizeOf<T>();
            var payload = new byte[size];
            fixed (byte* dst = payload)
            {
                UnsafeUtility.CopyStructureToPtr(ref evt, dst);
            }

            PendingEvents.Add(new PendingEvent { Type = typeof(T), Payload = payload });
        }

        #endregion

        #region Tick

        /// <summary>Runs one complete tick: simulate, then clear the dirty lifetime.</summary>
        public void Tick(float dt)
        {
            TickSimulate(dt);
            EndTick();
        }

        /// <summary>
        /// Everything in a tick up to, but not including, the dirty-flag clear. Between this and
        /// <see cref="EndTick"/> the dirty flags describe exactly what changed this tick, which is
        /// when replication reads them.
        /// </summary>
        public void TickSimulate(float dt)
        {
            long tickStartTicks = Stopwatch.GetTimestamp();

            Physics.Settings = Config.physics;

            NativeHashMap<Guid128, VoxelEntityData> entities = Data.VoxelEntities;
            NativeHashMap<Guid128, VoxelBodyData> bodies = Data.VoxelBodies;

            /////////////////////////////////////////////////////////////////////////
            // TOPOLOGY BOUNDARY
            //  Entity add/remove and static flips happen between ticks. From here on
            //  the entity set is fixed for the tick.
            /////////////////////////////////////////////////////////////////////////

            NativeArray<Guid128> entityKeys;
            using (s_PrepareTickMarker.Auto())
            {
                entityKeys = entities.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < entityKeys.Length; i++)
                {
                    VoxelEntityData e = entities[entityKeys[i]];
                    // Velocity of the interval that just ended (last tick's motion plus any pose set
                    // between ticks), computed BEFORE previousTransform is reset to mark the start of
                    // this tick.
                    e.ComputeVelocityForDirtyPropagation(dt);
                    e.previousTransform = e.transform;
                    entities[entityKeys[i]] = e;
                }

                // Body index assignment: dynamic bodies first, then static ones.
                int nDynamic = 0;
                using (NativeArray<Guid128> bodyKeys = bodies.GetKeyArray(Allocator.Temp))
                {
                    for (int i = 0; i < bodyKeys.Length; i++)
                    {
                        if (entities.TryGetValue(bodyKeys[i], out VoxelEntityData e) && !e.isStatic)
                        {
                            nDynamic++;
                        }
                    }

                    Data.nDynamicBodies = nDynamic;

                    int dynamicIndex = 0, staticIndex = 0;
                    for (int i = 0; i < bodyKeys.Length; i++)
                    {
                        VoxelBodyData body = bodies[bodyKeys[i]];
                        bool isStatic = !entities.TryGetValue(bodyKeys[i], out VoxelEntityData e) || e.isStatic;
                        body._cached_body_index = isStatic ? nDynamic + staticIndex++ : dynamicIndex++;
                        bodies[bodyKeys[i]] = body;
                    }
                }
            }

            /////////////////////////////////////////////////////////////////////////
            // VOXEL STAGE (automata)
            //  DO   modify voxel data within the 1-voxel/tick propagation limit, add forces
            //  DON'T add/remove entities, toggle isStatic, move transforms
            /////////////////////////////////////////////////////////////////////////

            // Only simulation bits schedule automata work. A sector or brick with geometry-only
            // work gets no snapshot copy and no record; each hook picks its own channel out of
            // the list inside its job.
            const BrickUpdateFlags automataMask = BrickUpdateFlags.AutomataMask;

            using (s_BeginAutomataWritesMarker.Auto())
            {
                for (int i = 0; i < entityKeys.Length; i++)
                {
                    entities[entityKeys[i]].BeginAutomataWrites(automataMask);
                }
            }

            using (s_CollectRequireUpdateBricksMarker.Auto())
            {
                automataTickBuf.VoxelEntities = entities;
                automataTickBuf.BricksRequiredUpdate.Clear();
                for (int i = 0; i < entityKeys.Length; i++)
                {
                    entities[entityKeys[i]].CollectRequiredBricks(
                        entityKeys[i], automataMask, includeEmpty: true,
                        automataTickBuf.BricksRequiredUpdate);
                }

                // Read once here: a hook must not touch the list's length after an earlier hook
                // scheduled a job over it.
                automataTickBuf.BricksRequiredUpdateCount = automataTickBuf.BricksRequiredUpdate.Length;
            }

            using (s_BuildAlienReadContextMarker.Auto())
            {
                BuildAlienReadContext();
            }

            JobHandle tickHandle;
            using (s_AutomataStageScheduleMarker.Auto())
            {
                // Packed colors are material payloads, not gameplay block IDs.
                // Keep snapshot, dirty and replication work running normally.
                tickHandle = BlockEncoding.PackedSceneColor
                    ? default
                    : AutomataStage.Schedule(automataTickBuf, default);
            }

            using (s_WorkDispatchMarker.Auto())
            {
                tickHandle.Complete();
            }

            using (s_EndAutomataWritesMarker.Auto())
            {
                // The snapshot swap exchanges whole slot tables, so every brick pointer bound
                // before it names the wrong buffer now. EndAutomataWrites declares the storage
                // mutated itself; the epoch is what a consumer asserts on.
                for (int i = 0; i < entityKeys.Length; i++)
                {
                    entities[entityKeys[i]].EndAutomataWrites();
                }
            }

            /////////////////////////////////////////////////////////////////////////
            // V-P BOUNDARY: dirty propagation
            /////////////////////////////////////////////////////////////////////////

            using (s_DirtyPropagationMarker.Auto())
            {
                using (s_ClearRequireUpdatesMarker.Auto())
                {
                    for (int i = 0; i < entityKeys.Length; i++)
                    {
                        VoxelEntityData e = entities[entityKeys[i]];
                        e.ClearRequireUpdates();
                        entities[entityKeys[i]] = e;
                    }
                }

                using (s_PropagateDirtyFlagsMarker.Auto())
                {
                    JobHandle handle = default;
                    for (int i = 0; i < entityKeys.Length; i++)
                    {
                        VoxelEntityData e = entities[entityKeys[i]];
                        handle = JobHandle.CombineDependencies(handle, e.PropagateDirtyFlags(BrickUpdateFlags.All, true));

                        // INVARIANT (load-bearing): the propagation phase may only ADD storage to an
                        // entity — it must never free or relocate what already exists — because the
                        // read-only views handed to the jobs above still point at it.
                        entities[entityKeys[i]] = e;
                    }

                    using (s_BurstMarker.Auto())
                    {
                        handle.Complete();
                    }
                }
            }

            // Dirty flags stay set through the physics step: alien propagation runs on the
            // stepped poses and selects its source bricks from them, and replication reads them
            // after this method returns. EndTick clears them.

            using (s_MarkNonEmptyBlocksMarker.Auto())
            {
                using NativeArray<VoxelEntityData> values = entities.GetValueArray(Allocator.Temp);
                for (int i = 0; i < values.Length; i++)
                {
                    values[i].RefreshNonEmptyMask();
                }
            }

            using (s_RecomputeBodyMassPropertiesMarker.Auto())
            {
                using NativeArray<Guid128> bodyKeys = bodies.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < bodyKeys.Length; i++)
                {
                    VoxelBodyData body = bodies[bodyKeys[i]];
                    VoxelEntityData entityData = entities[bodyKeys[i]];
                    body.ComputePhysicsProperties(entityData);
                    bodies[bodyKeys[i]] = body;
                }
            }

            using (s_ApplyBodyForceCommandsMarker.Auto())
            {
                ApplyDrags(dt);
                Forces.ApplyTo(ref Data, dt);
            }

            /////////////////////////////////////////////////////////////////////////
            // PHYSICS STAGE
            /////////////////////////////////////////////////////////////////////////

            long physicsElapsedTicks;
            using (s_PhysicsStepMarker.Auto())
            {
                long physicsStartTicks = Stopwatch.GetTimestamp();
                Physics.SimulateStep(dt, ref Data);
                physicsElapsedTicks = Stopwatch.GetTimestamp() - physicsStartTicks;
            }

            /////////////////////////////////////////////////////////////////////////
            // P-T BOUNDARY: alien propagation over the stepped poses
            /////////////////////////////////////////////////////////////////////////

            long alienElapsedTicks;
            using (s_AlienPropagationMarker.Auto())
            {
                long alienStartTicks = Stopwatch.GetTimestamp();
                LastBrickOverlapPropagationStats = default;
                if (Config.doAlienPropagation)
                {
                    var request = BrickOverlapQueryBuilder.Build(ref Data, new BrickOverlapQuerySettings
                    {
                        FlagsToPropagate = BrickUpdateFlags.All,
                        MotionDirtyMask = Config.alienMotionDirtyMask,
                        IncludeMovingBodies = Config.alienIncludeMovingBricks
                    });

                    if (request.IsCreated)
                    {
                        try
                        {
                            BrickOverlapGraph graph = Physics.BuildBrickOverlapGraph(
                                request.Batches, request.Bricks, rebuildBroadphase: false);

                            LastBrickOverlapPropagationStats = BrickOverlapDirtyPropagation.Propagate(
                                graph, request, ref Data.VoxelEntities);
                        }
                        finally
                        {
                            request.Dispose();
                        }
                    }
                }

                alienElapsedTicks = Stopwatch.GetTimestamp() - alienStartTicks;
            }

            // The change list is complete only here: alien propagation is the last stage that can
            // mark a brick, so every consumer of this tick's changes reads after this point.
            for (int i = 0; i < entityKeys.Length; i++)
            {
                entities[entityKeys[i]].BuildChangeList();
            }

            entityKeys.Dispose();

            long totalElapsedTicks = Stopwatch.GetTimestamp() - tickStartTicks;
            double totalMilliseconds = TicksToMilliseconds(totalElapsedTicks);
            double physicsMilliseconds = TicksToMilliseconds(physicsElapsedTicks);
            double brickGraphMilliseconds = TicksToMilliseconds(alienElapsedTicks);
            LastTickTimings = new TickTimingStats
            {
                IsCreated = true,
                TickMilliseconds = Math.Max(0.0, totalMilliseconds - physicsMilliseconds - brickGraphMilliseconds),
                PhysicsMilliseconds = physicsMilliseconds,
                BrickGraphMilliseconds = brickGraphMilliseconds,
                TotalMilliseconds = totalMilliseconds
            };
        }

        /// <summary>End of the dirty lifetime: every consumer of this tick's dirty flags has run.</summary>
        public void EndTick()
        {
            using (s_ClearDirtyFlagsMarker.Auto())
            {
                using NativeArray<Guid128> keys = Data.VoxelEntities.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; i++)
                {
                    VoxelEntityData e = Data.VoxelEntities[keys[i]];
                    e.ClearDirtyFlags();
                    e.ClearChanges();
                    Data.VoxelEntities[keys[i]] = e;
                }
            }

            TickIndex++;
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private void BuildAlienReadContext()
        {
            alienEntityViews.Clear();

            foreach (var kvp in Data.VoxelEntities)
            {
                VoxelEntityData entity = kvp.Value;
                float4x4 localToWorldMatrix = float4x4.TRS(entity.transform.pos, entity.transform.rot, 1f);
                float4x4 worldToLocal = math.inverse(localToWorldMatrix);
                float3 worldAabbMin = entity.transform.pos;
                float3 worldAabbMax = entity.transform.pos;

                if (entity.TryGetStorageBounds(out int3 minBlock, out int3 maxBlockExclusive))
                {
                    float3 localMin = minBlock;
                    float3 localMax = maxBlockExclusive;
                    for (int mask = 0; mask < 8; mask++)
                    {
                        float3 localCorner = new float3(
                            (mask & 1) == 0 ? localMin.x : localMax.x,
                            (mask & 2) == 0 ? localMin.y : localMax.y,
                            (mask & 4) == 0 ? localMin.z : localMax.z);
                        float3 worldCorner = math.transform(entity.transform, localCorner);
                        worldAabbMin = math.min(worldAabbMin, worldCorner);
                        worldAabbMax = math.max(worldAabbMax, worldCorner);
                    }
                }

                alienEntityViews.Add(new AlienEntityView
                {
                    EntityId = kvp.Key,
                    LocalToWorld = entity.transform,
                    WorldToLocal = worldToLocal,
                    Data = entity,
                    WorldAabbMin = worldAabbMin,
                    WorldAabbMax = worldAabbMax
                });
            }

            automataTickBuf.ReadContext = new AutomataReadContext
            {
                AlienQuery = new AlienOccupancyQuery
                {
                    EntitiesInDeterministicOrder = alienEntityViews.AsArray()
                },
                Entities = Data.VoxelEntities
            };
        }

        #endregion

        #region Save / Load

        /// <summary>Saves every entity of this world (except those excluded from save) to a <c>.cxw</c> file.</summary>
        public void Save(string path)
        {
            Save(path, WorldRecord.None);
        }

        /// <summary>
        /// Saves every entity of this world (except those excluded from save) plus the given
        /// world-level record (player pose) to a <c>.cxw</c> file. The world does not track a
        /// player pose itself; the game supplies it.
        /// </summary>
        public void Save(string path, in WorldRecord world)
        {
            var records = new List<EntitySaveRecord>(Data.VoxelEntities.Count);
            foreach (var kvp in Data.VoxelEntities)
            {
                bool hasBody = Data.VoxelBodies.TryGetValue(kvp.Key, out VoxelBodyData body);
                float3 linearVelocity = float3.zero;
                float3 angularVelocity = float3.zero;
                if (hasBody && !kvp.Value.isStatic)
                {
                    linearVelocity = body.motionVelocity.LinearVelocity;
                    angularVelocity = body.motionVelocity.AngularVelocity;
                }

                records.Add(new EntitySaveRecord(kvp.Key, kvp.Value, hasBody, linearVelocity, angularVelocity));
            }

            WorldSaver.Save(path, records, in world);
        }

        /// <summary>
        /// Loads every entity stored in a <c>.cxw</c> file into this world. An entity whose guid
        /// already exists is replaced. Returns the file's world-level record (player pose); the
        /// world does not apply it, the game does.
        /// </summary>
        public WorldRecord Load(string path)
        {
            return WorldLoader.Load(path, this);
        }

        bool IWorldLoadTarget.TryCreateEntity(in EntityRecord record, out VoxelEntityData data)
        {
            if (HasEntity(record.Guid))
            {
                RemoveEntity(record.Guid);
            }

            var transform = new RigidTransform(record.Transform.Rotation, record.Transform.Position);
            CreateEntity(record.Guid, transform, record.IsStatic, record.Protected);
            data = GetEntity(record.Guid);
            return true;
        }

        void IWorldLoadTarget.CommitEntity(in EntityRecord record, in VoxelEntityData data)
        {
            SetEntity(record.Guid, in data);
            if (record.HasBody)
            {
                // The accurate-physics flag is not persisted; loaded bodies use the direct solver.
                AddBody(record.Guid, accuratePhysics: true);
                if (!record.IsStatic)
                {
                    SetBodyVelocity(record.Guid, record.LinearVelocity, record.AngularVelocity);
                }
            }
        }

        #endregion
    }
}
