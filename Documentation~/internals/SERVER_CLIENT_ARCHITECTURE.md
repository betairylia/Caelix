# Caelix server-client architecture

**Status:** v1 landed (branch `dev/fable/server-client-v1`, started 2026-09-03,
commits `532b157` to `aa97d84`; later branches carry it forward). Then world lifecycle
messages, two-phase replication and one host per process, 2026-09-05. Pre-merge review
fixes landed 2026-09-07 (single decoder, `NetHeader.PeekType`, `TryReceive(predicate)`,
Core-side removal invalidation, tick velocity order). Section 10 lists
what is still open. Lifecycle and pause hardening checked on 2026-09-06;
see `RND_VALIDATION.md` for the supported use and measured checks.
Section 4's component API cleanup was reviewed against the working tree after
checkpoint `69c54a7` on 2026-09-06.
**Decided by:** owner, after the design discussion recorded in the family `CLAUDE.md` notes.

This document is the boundary contract. Code that crosses it without going
through the channel is a bug.

## 1. Roles

| Role | Owns | Runs | Needs |
|---|---|---|---|
| Server, authoritative | `VoxelEntityData`, `VoxelBodyData`, every sector slot | automata stage, dirty propagation, alien propagation, physics, save/load, streaming | Core, Physics. No URP, no Camera, no GameObject |
| Client, render replica | A copy of the replicated slots for every sector of every entity | delta apply, Geometry propagation, renderer, raycast, tools, UI, audio | Simulation (host mode), URP |
| Shared | Core data types, `Guid128`, sector serializer, message codec, channel interface, type registries | | |

The client is a **render replica**, not a mirror. It holds the Block slot,
runs no automata and no physics, and turns inputs into commands. A full
mirror (all slots, prediction) is a later level and is not planned for v1.

Rejected alternatives, and why:

- **Deterministic lockstep.** The physics step is multithreaded with
  `NativeStream` ordering and the automata random state is seeded from wall
  time. One divergence is a permanent desync with no cheap repair.
- **True dumb terminal, no voxel data on the client.** A ray tracing brick
  record is about the size of the Block slot, meshes are larger, and the tool
  raycast needs local block data at frame rate. It moves per-renderer work to
  the server and saves nothing.
- **Zero-copy host.** The renderer reading server sectors directly would give
  it two code paths, one for host and one for remote. The Block-only replica
  costs about one third of the server's per-brick memory. Accept the copy,
  measure, revisit only if memory becomes the constraint.

## 2. Worlds

- `Caelix.Simulation.CaelixWorld` is a plain class. It is constructed from a
  `CaelixWorldConfig` (id, name, physics settings including gravity,
  replicated slot mask, alien propagation flags, drag timeout). It owns its
  entity map, body map, `VoxelPhysicsWorld` (which owns the force command
  stream), automata stage, and outgoing event queue.
- `CaelixServer` owns a list of worlds, the tick clock, the connections,
  command dispatch, and replication. **All worlds tick at the same rate, in
  lockstep.** `Step()` runs one tick of every world. The loop inside `Step()`
  is world-major today: for each world, simulate, replicate, end tick. A
  stage-major loop, where a cross-world stage can sit between per-world
  stages, is deferred (section 9).
- **The server owns the clock rule, not the host.** `Step()` runs one tick
  unconditionally. `Tick()` is the gated form: it runs `Step()` unless the
  server is frozen, and it returns whether a tick ran. A frozen server still
  runs its very first tick, because replication happens inside a tick and a
  client that never received one has no state at all. So `Frozen` means "no
  tick after the first", and `Tick()` returning false is the normal steady
  state of a frozen scene. That initial frozen tick leaves edit commands queued.
  `Step()` explicitly consumes queued commands, then simulates and replicates one tick.
  Queries are serviced while frozen against the committed server data; they pass
  pending edits in the channel without applying them or changing their relative order.
- Two ways to drive the clock. `CaelixHost` calls `Tick()` once per Unity
  fixed step and nothing anywhere else (section 10). `CaelixServer.Update(deltaTime)`
  is a self-clocked driver with its own accumulator and a `MaxTicksPerUpdate`
  backlog cap, kept for a loop that has no fixed step of its own, such as a
  headless server; it calls `Tick()` too and drops the accumulator when the
  freeze stops it. Nothing calls it yet.
- External drivers can set `CaelixHost.ManualDrive = true` to suppress its automatic
  fixed-step and client-frame pumps. Call `Step(count)` to advance simulation, then
  `PumpClientFrame()` to receive replication and prepare/render the client once.
  Explicit steps contribute to `LastTickTimings.ServerTicks` and server elapsed
  time; pumping consumes that accumulator once. World sub-buckets still describe
  only the last tick. `ClientPendingBytes`, `ClientPeakPendingBytes`, and
  `ClientReceivedBytes` expose the existing LocalChannel counters without exposing
  a mutable channel. Titania's benchmark runner uses this path.
- A connection receives every world by default. `ServerConnection.SubscribedWorlds`
  restricts it to a subset.
- An entity lives in exactly one world. The wire address of an entity is
  world id plus `Guid128`. The address of a brick is world id, guid, sector
  position, brick index.
- Cross-world dynamics are out of scope. Nothing in the world class may
  assume one world per process.
- Custom tick logic is done through hook stages for now. Generic worlds and
  Burst function pointers stay available if a game must replace a stage
  rather than add to it.

## 3. The server keeps no GameObjects

- The world's `NativeHashMap<Guid128, VoxelEntityData>` and
  `NativeHashMap<Guid128, VoxelBodyData>` are the source of truth and are
  updated in place. There is no per-tick copy in and copy back.
- `VoxelEntityData.transform` is the truth. `previousTransform` is copied
  from `transform` at tick start. The physics export writes `transform`. No
  Unity `Transform` is read or written on the server. Runtime code that moves
  an entity calls `CaelixWorld.SetEntityTransform`.
- Inspector settings live on bootstrap components and are pushed into the
  running world each frame. Debug views draw from data.
- PhysX is gone: no `Rigidbody`, no `UnityPhysicsCollider`, no
  `TemporaryCharacterCollider`, no `VoxelBody.physicsEnabled`.

### Automata and the shared physics BVH

After the topology boundary, entity/body membership, staticness and transforms stay
fixed until physics. When simulation bricks require work, `PrepareSpatialQueries`
refreshes collider storage views and body mappings, then builds the existing physics
BVH from current poses and storage bounds. Every entity participating in alien reads
must have a body. This preparation does not compute mass or apply forces.

`CollectAutomataCandidates` queries each source entity's aggregate required-brick
bounds, then narrows the returned entities per brick. Each source brick expands by
one voxel in local space before its bounds are transformed. No target brick keys are
enumerated. Reusable candidate indices and `RequiredBrick.AlienCandidateRange`
preserve the existing alien-view priority independently of BVH traversal order.
`AutomataReader` resolves blocks and metadata local-first through that range, using
a `BrickCursor` within the read phase. Recreate readers after snapshot commits or
storage mutation.

After automata commits snapshots, normal dirty propagation, occupancy, collision
data, mass properties and forces are prepared. Physics refreshes collider storage
views again and rebuilds the static tree only if its bounds changed since spatial
preparation; ordinary WireWorld state changes preserve these bounds. The dynamic
tree is prepared for motion. Post-physics alien propagation keeps its own overlap
results and synchronizes the collision world with the resulting poses before
querying, including when simulation-time synchronization was disabled.

## 4. Authoring without a bake step

`VoxelEntity` handles authoring and client view binding. `VoxelBody` supplies
**body authoring settings** and registers scene-authored bodies on the local
server. Replicated objects use `EntityView.HasBody` and do not carry `VoxelBody`.
Client tools send forces and drag input through `CaelixClient`. `VoxelBody` retains
`AddForce`, `AddTorque`, and `AddForceAtPosition` as convenience wrappers around
client commands. It exposes no drag wrappers, body-record copy, mass-property
computation, or direct velocity setter. The
server's `CaelixWorld.TickSimulate` calls `VoxelBodyData.ComputePhysicsProperties`
after dirty propagation and before forces and physics. Server systems inspect
bodies with `TryGetBody` and set velocity with `SetBodyVelocity`.

They live in the client assembly. In `OnEnable` a `VoxelEntity` finds its
host by convention and registers:

1. a serialized host reference on the component, if set;
2. otherwise a `CaelixHost` found through the parents;
3. otherwise `CaelixHost.Current`, the host of this process.

**There is one enabled host per process.** `CaelixHost` inherits
`MonoSingleton<CaelixHost>`. Ownership is claimed before resource initialization
and released on disable or destruction. A duplicate logs an error and disables
itself before creating a server/client. It can be explicitly re-enabled after the
owner releases the slot. `Current` finds an existing enabled scene component
before its `Awake`, without creating or initializing anything. The base does not
persist objects across scenes. A disabled host retains its resources until
destruction and resumes them when re-enabled; only the enabled owner may tick,
save, or load. Initialization failures clean up partial resources and disable
the host.

The per-scene lookup (`FindForScene`, `All`) went. A host owns a server, a
client and a channel between them, so one host per scene meant several
independent servers in one process with no path from one to another, and an
entity in an additively loaded scene would silently join a different world.
Worlds, not hosts, are how one process holds several simulations (section 2).
`CaelixHost.Any` remains as an obsolete alias of `Current`.

In Host role the component registers the entity with the server world and is
bound as the client view for the same guid when the replication spawn message
arrives. Importers and generators that write blocks through the component
keep working, because in a process that runs the server the component's data
API is a direct write into the server world. In a process that does not run
the server the API splits: `SetBlock` and `IsStatic` send a command, `GetBlock`
and `GetSlot` read the view, and the sector-level API (`SetSlot`, sector
add/remove, authoring propagation) throws. Unused entity-record copy, dirty-clear,
voxel-collection, and explicit transform-sync wrappers have been removed; their
owning server data or client view APIs remain available.

The guid is serialized on the component (generated once in the editor). A
prefab instance needs a fresh guid on first placement. That handling is
deferred.

> **Divergence (2026-09-04).** The implementation does not generate the guid
> in the editor. `VoxelEntity.ResolveGuid` draws a random guid at runtime when
> the serialized value is zero and does not store it, so an authored entity
> gets a new guid every play session unless code sets `PersistentGuid`. The
> design above and the implementation are both on the table; which one to
> keep is an open decision.

Edit-mode visibility of handmade entities is a later feature. It needs a
voxel asset type and an edit-mode renderer driver. The view-source design of
the renderer is what makes it possible.

## 5. Replication

- **Replicated slot mask.** `CaelixWorldConfig.replicatedSlotMask`, one bit
  per slot id, default Block only. Sent in the handshake. Delta records carry
  slot id, stride, and raw bytes, so the client never needs the C# type of a
  slot. Both ends must use the same fixed
  [application-wide slot definitions](https://github.com/betairylia/Caelix-Core/blob/main/Documentation~/internals/voxel-storage.md#application-wide-slot-definitions).
  A record's stride describes that definition; it cannot redefine a slot after
  storage removal or recreation. The app-level definition contract is documented;
  a frozen registry and comprehensive schema validation are not implemented yet.
- **Everything is a keyed brick record.** One message kind, `BrickBatch`, per
  entity. Every record carries its own brick key and an explicit operation,
  `Update` or `Remove`, so nothing outside Caelix-Core names a sector. There is
  no `SectorAdd`, no `SectorRemove`, and no per-sector message.
- **Delta detection is the change list.** `VoxelEntityData.Changes`, published
  after alien propagation and cleared by `EndTick`, is what replication reads.
  A `Removed` entry becomes a `Remove` record; an `Updated` entry whose
  `DirtyFlags` meet `BrickReplication.ReplicationDirtyMask` (`Geometry`,
  `BlockBrickAdded`, `BlockBrickRemoved` — the Block slot is the only writer that
  sets `Geometry`) becomes an `Update` record. Records keep change-list order, so
  a key removed and recreated in one tick reaches the client as Remove then
  Update. `SlotReplicate`, a bit set by writes to other replicated slots, is a
  possible later extension; it is not an existing `BrickUpdateFlags` member.
  `VoxelEntityData.AddSectorAt` marks every allocated brick of an attached
  storage unit `BlockBrickAdded | Geometry | GeometryWithLocalNeighbor`, because
  a generator or importer that hands over pre-filled storage would otherwise
  announce nothing.
  This dirty gate selects delta work. It does not prove that a record has any
  payload under the configured slot mask. Full uploads enumerate allocated
  bricks regardless of dirty flags, because a new client also needs unchanged
  data. Slot presence and current dirtiness are separate facts; the current
  codec can emit a zero-slot Update when no selected slot exists.
- **Two phases, phase B first.** Phase A, `ReplicationBatch`, packs BrickBatch
  messages for a list of entities, in parallel over BRICKS: a collect job flattens
  each entity into brick records, a size job runs over them, and a write job fills
  each record through `UnsafeNetWriter` at its own offset. It is built once per
  world per tick from the world's changed entities and every connection sends the
  same bytes. `CollectChangedEntities` walks the entity map and adds every entity
  with a non-empty change list. Phase B runs first, so phase A can pack only what
  is left.
- **Phase B** is `ServerConnection.ReplicateWorld`, one call per connection,
  and it runs before phase A. It diffs the connection's ENTITY knowledge, sends
  the lifecycle messages, then packs every allocated brick of whatever that
  connection still has to catch up on into a second batch and streams it. It
  records which entities it sent in full this tick. An entity new to a connection
  is served entirely by the catch-up batch and never by a shared delta message.
- **Phase A packs only what somebody still needs, in chunks.** After phase B,
  the server drops every collected delta entity that each connected, subscribed
  connection already received in full; on the tick a world is loaded that is
  all of them. Both batches then stream: `Prepare` sizes every record once and
  cuts the records into messages, `BuildNextChunk` packs the next run of messages
  that fits in `ReplicationBatch.MaxChunkBytes` (64 MB by default, `CaelixServer.
  ReplicationChunkBytes`), and every connection forwards that chunk with
  `SendDelta` before the next one is built. A message never spans entities and
  never grows past one chunk, so a chunk always holds at least one whole message.
  One buffer for the whole batch does not work: an 8K world is about 2.9 million
  bricks of roughly a kilobyte each, near 3 GB, so the `int` prefix sum overflowed
  negative, the resize was a no-op, and the write job wrote gigabytes into a
  64 KB allocation.
- **Topology.** Per connection, the server diffs the known entity set against the
  world every tick. It also compares world object identity and entity creation
  identity. Reusing an ID between ticks therefore produces remove then add,
  followed by full contents. Storage residency below the entity is NOT diffed per
  connection any more: it rides on the change list, and a client's residency is
  the set of keys it received. `ForgetWorld` and a changed replicated slot mask
  also force a world reset. Identity is server bookkeeping; the local ordered
  channel carries the existing lifecycle messages, with no new wire format.
- **Empty storage is not replicated.** A server region that holds no brick — a
  neighbour that propagation created, or an explicit `EnsureRegion` — has no
  record to send, so the client never learns about it. A replica matches the
  server's ALLOCATED BRICKS, not its region set.
- **Renderer lifetime.** `WorldRemoving` and `ViewDespawning` run while the
  corresponding storage is still alive. Mesh and ray renderers unsubscribe,
  complete their jobs, and release cached resources before disposal. There is no
  `SectorRemoving`: both renderers learn about a removal from the change list, and
  drop a render group that holds no allocated brick after its jobs completed.
  `VoxelEntityData.RemoveRegion`
  marks the facing bricks of every surviving neighbour region dirty with
  `GeometryWithLocalNeighbor` and no direction mask, so the flag reaches only
  those bricks and creates no sector; the server's next propagation turns it into
  the require-update the physics slot refresh reads, and the client's
  `PrepareRender` does the same for the renderers. A fresh renderer uploads a
  quiet replica in full, including after disable/re-enable. Authored components
  rebind to their replacement entity and world.
- **Transforms and flags.** Sent per entity when changed.
- **Never replicate** `PhysicsInfo`. It is derived, and the serializer already
  discards it.
- **Client apply.** `BrickBatchApplier` (Caelix-Core) is the only apply path.
  The client frame is three calls plus the renderers: `Receive` (clear
  require-update, then apply every pending message), `PrepareRender` (propagate
  the render flags `Geometry`, `GeometryWithLocalNeighbor`, `BlockBrickAdded`,
  `BlockBrickRemoved`), the renderers, then `EndFrame` (clear dirty and clear the
  change list). None of those four bits is in
  `DirtyPropagationSettings.DirtyFlagsCanAllocateLocalBricks`, so the client
  never grows phantom sectors.
- **Frozen server.** Replication runs inside the tick. Freeze is a server-level
  switch (`CaelixServer.Frozen`, driven by the host's `freeze` field), not a
  per-world flag. The first tick gives a frozen scene its initial state (section 2).
  Later frozen frames send query replies, while edits remain queued until explicit
  stepping or unfreezing. `INetChannel.TryReceive(predicate, out message)` provides
  selective receipt and preserves unmatched message order. Typed query handlers
  must be read-only; a handler with side effects would break the pause contract.
  The server's query filter reads the type through `NetHeader.PeekType`.

## 6. Messages, v1

Envelope: `u8 type, u16 worldId, u32 tick`. Then the payload.

World id 0 is a real world (the default world), so a message that is not scoped
to one world carries the sentinel `NetHeader.NoWorld` (`0xFFFF`) instead. Hello
is the only such message today. **Every dispatcher tests for the sentinel before
it looks a world up**, and warns for any other type that arrives with it; a
world-scoped message never carries it.

| Direction | Message | Payload |
|---|---|---|
| S to C | Hello | tick rate, registry hash. Header world id is `NoWorld` |
| S to C | WorldAdd | replicated slot mask. Header world id names the world |
| S to C | WorldRemove | (padding byte). Header world id names the world |
| S to C | EntitySpawn | guid, transform, isStatic, isProtected, hasBody |
| S to C | EntityDespawn | guid |
| S to C | EntityState | guid, isStatic, isProtected, hasBody |
| S to C | EntityTransform | guid, transform (rotation, position) |
| S to C | BrickBatch | guid, brick count; then per record: `int3 key`, `u8 op`, and for an Update the slot records |
| S to C | Event | registered type id, blittable payload |
| S to C | QueryReply | request id, guid, position, found; then slot records for one voxel |
| S to C | TypedQueryReply | request id, registered reply type id, reply struct, trailing payload |
| C to S | Command | registered type id, blittable payload |
| C to S | Query | request id, guid, position, slot mask |
| C to S | TypedQuery | request id, registered request type id, request struct, trailing payload |

A `BrickBatch` message belongs to one entity and carries a run of its records.
A record is `int3 key, u8 op`; an `Update` record then carries the brick's slot
records (`u8 slotCount`, then per slot `u8 slotId, u16 stride,
byte[stride * 512]`, a zero count when the brick is not allocated), and a
`Remove` record ends after the op byte, at 13 bytes. `BrickCount` is an `int`,
because a batch is bounded by bytes rather than by 65535 records. An entity new
to a connection sends every allocated brick; a known entity sends its change
list.

`CaelixClient.Receive` resolves the world and the view, then hands the payload to
`BrickBatchApplier` — nothing on the Caelix side sees a sector. The applier runs
four passes per flush. Pass 1 is one Burst job: it validates every message and
chains its records into per-storage-unit work items, in wire order. Pass 2 runs
on the main thread and creates the storage the records need. Pass 3 is a parallel
Burst job over work items; each owns its unit exclusively, applies Updates and
Removes in order, and refreshes the allocated-brick list whenever allocation
changed. Pass 4, back on the main thread, publishes one `Removed` change entry
per freed brick, frees a unit that ended up empty (which marks the surviving
cross-unit boundary), and advances the storage epoch. Removals are published
before the frame's `BuildChangeList`, so a consumer that applies entries in list
order sees Remove before the Update that recreated the same key.

A freed brick index keeps `BlockBrickRemoved | GeometryWithLocalNeighbor` dirty
with every direction set. Propagation reads the flag arrays by index and never
asks whether the brick still exists, so the 26 neighbours are re-rendered.

`LocalChannel` copies sends into pooled buffers and transfers their ownership through
`ReceivedMessage`. Its logical `Length` is separate from array capacity. The pool
retains at most two arrays per size bucket up to 64 MiB; larger messages remain valid
but are not retained for reuse. Legacy `INetChannel` implementations still work through
an adapter, and legacy array receives return independent, exactly sized arrays.

`BrickReceiveBatch` pins received buffers until its jobs complete, then releases the
pins before returning the leases. It flushes at 64 MiB of actual array capacity or
1,024 messages; a single oversized message runs alone. These limits
bound retained apply input, not the channel inbox. Every non-brick message, creation
of an unknown world (which raises a callback), and the end of `Receive` completes
pending writes before observation or storage replacement. No apply job survives
`Receive`. Consumers still complete their own previous-frame jobs before receiving.

The native decoder checks the operation byte, slot IDs, strides and payload bounds
before changing any brick in a message. Conflicting strides include records earlier
in the same message and in earlier messages of the same flush. An invalid message
changes no brick and creates no storage; it returns an error for main-thread
logging, and earlier and later messages still apply. Zero-slot records remain
no-ops, so they never create a storage unit either. A `Remove` for a key whose
storage does not exist is a no-op. This is framing validation, not a remote
transport or queue backpressure policy.

`ClientReceivePerformanceTests` provides opt-in initial-sync and queued-delta
receive timings. Run with Burst enabled and `--burst-force-sync-compilation`; the
test excludes its warm-up pass, sending, server simulation and rendering.

Measured on 2026-09-06 in Unity 6000.5.6f1, Burst 1.8.29, Collections 6.5.0,
Intel i7-14700KF, 27 job workers. Each value is the median of seven receive calls
after a warm-up pass. The baseline is Caelix `69c54a7` with Core `792ddd4`; the
optimized path is `dev/astra/server-client-v1`. Both use the identical test and
dependencies in the same isolated Editor project.

| Sectors × bricks per sector | Initial apply, before → after (ms) | Three queued updates, before → after (ms) |
|---|---:|---:|
| 1 × 1 | 0.0073 → 0.0056 | 0.0198 → 0.0026 |
| 1 × 1,024 | 0.1649 → 0.1403 | 0.3488 → 0.2233 |
| 128 × 1 | 0.8840 → 0.1243 | 2.5757 → 0.1421 |
| 128 × 1,024 | 37.9298 → 7.1636 | 62.1639 → 17.4829 |

The largest case sends about 128 MiB initially and 384 MiB in queued updates,
exercising multiple bounded flushes. These measurements describe CPU receive work
in the Editor, not total frame time or a rendered-world performance guarantee.

Validation: 278 Editor tests passed, including the Host Play Mode round trip,
the native decoder tests, callback/removal ordering, same-address writes across
worlds, and a 1,031-message run containing an invalid packet followed by valid
writes. The scale test compared every allocated Block brick after synchronizing
64 full synthetic sectors and applying queued edits. All four explicit benchmark
cases also passed. The isolated project uses Titania's
`UNITY_DISABLE_AUTOMATIC_SYSTEM_BOOTSTRAP` setting. Allocation stack tracing found
one existing leak in the unchanged Physics test
`VoxelEntityPhysicsTests.PhysicsWorldExportPersistsMotionForMultipleDynamicBodies`
(`SchedulePhysicsWorldBuild`'s GUID array); no receive-batch allocation was reported.

Order per connection, every `Step()`: `Hello` once, then `WorldAdd` /
`WorldRemove` for every world that appeared or went, then that tick's
replication. `Hello` goes out on the first `Step()` after the connection is
accepted, not at accept time, so game types registered during scene start are
part of the registry hash. World lifecycle is a diff like everything else:
`CreateWorld` and `RemoveWorld` notify nobody, and the next `Step()` compares
the connection's known world set against the server's, sending removes before
adds so a reused world id reaches the client in the right order. Unsubscribing
a connection from a world reads as a removal. The client creates a replica on
`WorldAdd` and disposes it on `WorldRemove`, raising `WorldAdded` and
`WorldRemoving`; a message naming an unknown world still creates a replica, so
nothing is lost if the order is ever violated.

Review note (mirrored from `HelloMessage`): the registry hash could be replaced
with a command list for validation, since a client only needs to be a subset of
the server's commands.

A command may also carry a trailing payload: send it with
`CaelixClient.SendCommand(in T, ReadOnlySpan<byte>)` and receive it with
`CaelixServer.RegisterCommand<T>(PayloadCommandHandler<T>)`. A typed query pairs a
request struct with a reply struct, and both may carry a payload
(`CaelixClient.SendQuery` / `CaelixServer.RegisterQuery`). In every case the
payload length is whatever remains in the message, so a handler reads the count
from its own struct.

Engine commands: `SetBlockCommand`, `SetEntityStaticCommand`,
`VoxelBodyForceCommand` (one-shot forces, impulses, velocity changes),
`DragCommand`, `ReleaseDragCommand` and `SpawnEntityCommand` (create an empty
entity with a client-chosen guid, so commands sent in the same frame can already
address it). A drag is held input, not a force: the
server keeps one per client and body, computes the spring every tick from the
body's own pose and velocity, and drops it on release or after
`dragTimeoutSeconds` without a refresh. Clients send the target whenever it
moves, at any rate. `DragCommand.AnchorAtCenterOfMass` holds the body by its
centre of mass instead of `AnchorLocal`, which is the stable hold for a scripted
grab and the only one a client cannot compute exactly. Games register their own
command and event types through the type registries. Engine types are registered
first, in a fixed order, on both sides.

Review note (mirrored from `EngineNetTypes`): `SetBlockCommand` could become a
general `SetSlotCommand`. Not yet, but it is likely to become useful.

## 7. Transport

`INetChannel` with two implementations: `LocalChannel` (in-process queue,
real serialization) and, later, `UtpChannel` (Unity Transport 2.x) in its own
optional assembly. Netcode for GameObjects, Netcode for Entities, and Mirror
were rejected; see the family notes and `ECS_vs_NonECS_Decision.md`.

Review note (2026-09-05, mirrored from `CaelixHost.EnsureInitialized`): should
connecting be abstracted the way `INetChannel` abstracts the channel? Yes, when
the Unity Transport channel lands. The shape is an `INetListener` (`Poll` +
`TryAccept(out INetChannel)`) on the server and an `INetConnector`
(`Connect(endpoint)`) on the client, with a `LocalTransport` implementing both
over the queue pair; `CaelixServer.Listen(listener)` polls accepts inside
`ProcessIncoming`. Deliberately not built ahead of UTP: the driver update,
per-delivery pipelines and connection events should shape the interface, and
with `LocalChannel` alone it would have one implementation and no test of fit.

## 8. Assemblies

| Assembly | Repo | Contents | References |
|---|---|---|---|
| `Caelix.Core` | Core | `VoxelEntityData` (the brick-key facade: `BrickKey`, `VoxelRegion`, `AutomataBrick`, `RequiredBrick`, `BrickChange`), the sector backend, serializer, dirty propagation, neighborhood reader, tick primitives, `Caelix.Net` codec, channel, registries | Burst, Collections, Mathematics |
| `Caelix.Physics` | Physics | `VoxelBodyData`, `VoxelPhysicsWorld`, `PhysicsWorldConfig` (scene settings holder), setup jobs, force commands | Core, low-level fork, Entities, Numerics |
| `Caelix.Simulation` | Caelix | `CaelixWorld`, `CaelixServer`, `ServerConnection`, replication, engine net types | Core, Physics, low-level fork, Entities, Numerics |
| `Caelix` | Caelix | `VoxelEntity`, `VoxelBody`, `CaelixHost`, `CaelixClient`, `ClientWorld`, renderers, raycast, importers, authoring | Simulation, URP |
| `Caelix.Transport.Utp` | Caelix | Unity Transport channel | Core, com.unity.transport. Later |

`PhysicsWorldConfig` stays in the physics package only because it keeps the
scene's serialized solver settings alive. It is a settings holder, not a
simulation object.

Namespaces do not follow assemblies. Most of `Caelix.Physics` declares
`namespace Caelix.Simulation` (only `PhysicsStepInputs` is in `Caelix`), the
same namespace as the Simulation assembly. Look at the asmdef, not the
namespace, to find which assembly owns a type.

## 9. v1 scope

Included:

- One server world, whole world sent on connect. No streaming, no interest
  management.
- No player entity. Free camera. Raycast on the client replica. Engine tools
  send commands.
- Block slot only. Other slots through queries.
- Server has no GameObjects. Data owns the transform.
- Host role in one Play Mode session with `LocalChannel`.
- Command and event registries. Titania's Spark and chime stay in Titania.
- Worlds are first-class from the start.

Deferred:

- Unity Transport channel and dedicated server build.
- Client and Server roles in the bootstrap.
- Streaming and per-client interest. `InfiniteLoader` stays unticked.
- Transform interpolation, edit prediction, compression.
- Serialized guid handling for prefab instances.
- Edit-mode voxel assets and rendering.
- Titania split into server-side and client-side assemblies.
- Stage-major batching of the physics step across worlds.

## 10. Implementation notes (v1, 2026-09-03)

- **`SharedHashMap` is load-bearing.** `VoxelEntityData` is copied by value everywhere:
  the world store, `GetDataCopy`, views, jobs. Its sector and neighbor maps therefore
  need handle semantics. `UnsafeHashMap` embeds its hash helper by value, so a copy has
  its own count and free index and, after a resize, a dangling buffer pointer. The
  wrapper allocates the map on the heap once and every copy points at it. The old
  copy-in / copy-back tick existed only to work around this. Do not replace the wrapper
  with a plain `UnsafeHashMap`; the symptom is a silent infinite loop in map
  enumeration on the second tick.
- **No Burst direct calls on the tick path.** `[BurstCompile]` static methods called from
  managed code compile synchronously in the Editor. Use a Burst job and `Run()` instead
  (`CollectBrickJob`, `PreviewBuilder`).
- **Host frame order.** `FixedUpdate` drives automatic ticks: push the
  inspector settings, then one `Server.Tick()`, timed. `driveFixedTimestep` sets
  `Time.fixedDeltaTime` from `targetTPS`. `Update` never ticks and never pushes
  settings; it pumps `Server.ProcessQueries()` even when `Time.timeScale == 0`,
  then runs `Client.Receive()`, `Client.PrepareRender()`, the raycast tick,
  the renderer ticks, then `Client.EndFrame()`, and collects the timings.
  `HostTimingStats.ServerTicks` is how many ticks ran since the last frame, so 0 on a
  frozen scene and n after a hitch. Render frame rate and tick rate are independent;
  the client applies whatever ticks landed since the last frame. No interpolation yet,
  so bodies show the latest tick's pose. The explicit `Host.Step(count)` API pushes
  current settings and runs exactly that many ticks while frozen.
  Review note (mirrored from `CaelixHost.Update`): could the per-frame
  `ProcessQueries` pump go, with `FixedUpdate` carrying it? No. Queries must also
  work on frames with no fixed step, including `Time.timeScale == 0`, and the pump
  leaves every edit command in the channel until `Server.Step` consumes it.
- **Profiler markers.** Server: `Server.ProcessIncoming`, `Server.TickSimulate`,
  `Server.Replicate`, `Server.ReplicationBuild` (phase A, the shared delta),
  `Server.ReplicateWorld` (phase B, per connection), `Server.ReplicationFullBuild`
  (phase A again, for one connection's catch-up), `Server.EndTick`,
  `Server.DrainEvents`. Client: `Client.ClearRequireUpdate`, `Client.Receive`,
  `Client.ApplyBrickBatch`, `Client.PropagateForRender`, `Client.EndFrame`. Host:
  `Host.ServerTick`, `Host.ClientFrame`, `Host.Renderers`.
- **Scene compatibility.** `CaelixHost.cs` and `PhysicsWorldConfig.cs` keep the script
  GUIDs of `CaelixWorld` and `CaelixPhysicsWorld`, and `VoxelEntity` / `VoxelBody` kept
  theirs across the assembly move, so existing scenes stay wired. `SimplePlayer` and
  `TemporaryCharacterCollider` were deleted; scenes that had them show a missing-script
  warning until the component is removed in the editor.
- **Titania.** Automata hooks register on `host.World.AutomataStage`. WireWorld chimes
  are `ChimeNoteEvent` events (declared in `Assets/Scripts/Dynamics/WireWorld.cs`) emitted by
  the server and played by a client handler in `TitaniaCore`. Interaction tools send commands
  and queries. Titania's own message types live in
  `Assets/Scripts/Interaction/TitaniaNetTypes.cs` (`WriteVoxelsCommand` with a `VoxelRecord`
  payload, `EntityVoxelsQuery`/`EntityVoxelsReply`); that file also registers `ChimeNoteEvent`.
  All are registered after the engine types on both ends by
  `TitaniaNetTypes.EnsureRegistered(host)`. That file is the reference for game-defined
  messages. The tools read the client replica (`EntityView`), never server data.
  Titania still calls the obsolete `CaelixHost.Any` alias until it moves onto this
  branch; that is why the alias exists.
- **Not done in v1:** the rendering assembly is not yet excluded from Dedicated Server
  builds; `InfiniteLoader` is not ticked; guids on authored entities are runtime-random
  unless set through `PersistentGuid`; a large-world join still materialises the whole
  world payload on the managed heap, because chunking bounds only the native packing
  buffer while `LocalChannel` copies every message into its inbox and the client drains
  once per frame. The fix is a resumable per-connection catch-up with backpressure
  (tracked, not done).
