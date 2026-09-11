# Inline ray query renderer

**The DXR pipeline path was removed on 2026-09-10.** Inline ray queries are now the only way Caelix
traces: `CaelixRenderer`, `SectorRenderer`, `Full_raygen.raytrace` and `CaelixBudget.raytrace` are
gone, and both renderer features drive a compute kernel. This document keeps the comparison
measurements and the DXR findings as history, because they are what the decision rests on and what
a future reader will want if the question is reopened.

A compute kernel walks the hardware acceleration structure with DXR 1.1 inline ray queries
(`RayQuery` / `TraceRayInline`). Everything downstream is unchanged from the DXR days: the G-buffer
contract, the denoise chain and the present stage are the same.

## The trace entry

The path-tracing body lives in `Runtime/Rendering/Shaders/PathTrace_full/CaelixPathTraceCommon.hlsl`
and the entry file supplies three macros before including it:

| Macro | Compute entry (`RayQuery/CaelixPathTraceRQ.compute`) |
| --- | --- |
| `CAELIX_TRACE_RAY(ray, payload)` | `CaelixRayQueryTrace(ray, payload)` |
| `CAELIX_LAUNCH_INDEX` | the thread's `SV_DispatchThreadID.xy` |
| `CAELIX_LAUNCH_DIM` | the `g_LaunchDim` uniform |

The indirection is kept: it is what let the DXR entry and the compute entry share one body, and it
is what would let a second entry share it again.

`CaelixRayQueryTrace` (in `RayQuery/CaelixRayQueryTrace.hlsl`) does the work the intersection shader
and the closest-hit shader used to: it runs the brick DDA (`CaelixTraceBrickPrimitiveCore`) on every
procedural candidate, commits the nearest hit, and fills the same `RayPayload`. On a miss it leaves
the payload cleared, which is exactly what `CaelixApplyVoxelMiss` produced.

Budget mode has its own entry, `Shaders/Budget/CaelixBudgetRQ.compute`, with the same skeleton and
its own body; the two share `RayQuery/CaelixMaterialTable.hlsl` and, on the C# side,
`RendererFeature/Passes/CaelixRayQueryDispatch.cs`.

## Pool and instance table

A ray query has no shader table, so there is no per-instance binding. Two global buffers replace it:

* **`CaelixBrickPool`** — the brick records of every render group, held in up to `MaxNamedPages` = 16 raw
  `GraphicsBuffer`s bound as `g_bricks0..15`. Pages exist because one buffer cannot exceed
  `SystemInfo.maxGraphicsBufferSize` (about 3.9 GB) and one record is 1096 bytes, so a few million
  live bricks do not fit one buffer. Each page is capped at `PageCapacityLimitBricks` (serialized on
  the component, clamped to the platform maximum and to a power of two).
* **`CaelixRayQueryInstanceTable`** — a structured buffer bound as `g_Instances`, one 80-byte record
  per RTAS instance, indexed by `InstanceID()`. The record holds the sector's page, its word offset
  inside that page, the previous object-to-world matrix (as four rows, for motion vectors) and the
  hash seed. The slot index *is* the instance ID, passed to `AddInstance(config, matrix, id)`. The
  kernel reads the record once per procedural candidate and switches every brick load on its page.

Sectors own power-of-two ranges inside a page, handed out by a bump pointer with a per-capacity free
list, and hold a `CaelixBrickPool.Handle` — a class, because the pool moves ranges. **Growth
compacts.** When a page has to grow, its live ranges are re-packed contiguously (largest first, so
every range stays aligned to its own size), the free lists are dropped and the page gets a new
buffer. Without compaction, streaming fragments the pool badly enough that it asks for a buffer past
the size cap. Growth targets 1.5x what the page needs rather than doubling; when a page cannot grow
any further the pool opens the next one, and when every page is full it logs an error once and
returns an invalid handle — that sector is then skipped, and it retries every tick.

A sector that only moved (new range, or a compacted page) republishes its instance record but does
**not** rebuild its RTAS instance: the AABBs did not change. It notices the move by comparing its
handle's current offset and page against the ones its record names.

## Records live only on the GPU

### Initial uploads across frames

`CaelixRayQueryRenderer` queues a group's first upload instead of preparing every group's records
on the frame an initial replica arrives. The Inspector's **Initial Upload** settings default to
65,536 allocated bricks and 16 new groups per frame. Both limits apply across all entity views.
The group limit also bounds new instance builds when the world contains many sparse groups.
Groups finish as a unit; a group larger than the brick budget runs alone so the queue can progress.

The queue stores only the entity view and group key. Admission happens before the render job
allocates its temporary records. An admitted group reads its current allocated bricks, completes
its job, stages its upload and publishes its ray-tracing instance in that tick. Existing groups
continue to consume the normal change list. Loading therefore reveals the world progressively.

This works both when the renderer observes the initial join and when it binds to a populated
client world. Changes to a deferred group are coalesced by key: it reads current storage when
admitted, even after the client has cleared earlier frame changes. Despawning a view cancels its
queued keys before its storage is disposed. World changes, disable/re-enable and group-size changes
discard the queue along with the old render resources and discover the new source again.

`initialUploadGroupsPending`, `initialUploadGroupsThisTick` and `initialUploadBricksThisTick` expose
progress; the last counts allocated input bricks before culling. `bricksStagedThisTick` still counts
all staged output records, including ordinary updates. A raw record is 1,096 bytes, so the default
brick budget represents at most 68.5 MiB of initial record payload per tick; list capacities, AABB
buffers, upload staging and driver allocations add to that amount.

The budget reduces simultaneous CPU render staging and new GPU work. It does not cap total process
memory, replication queues, ordinary updates, pool-growth copies, the accumulated acceleration
structure build or tracing cost. It is not a guarantee against a GPU timeout. Keep the existing
batched scatter upload: splitting work across frames must not restore per-group write/dispatch
buffer reuse.

The small GPU-backed cases in `RayQueryIdleGroupTests` cover budgeted progress after `EndFrame`,
late binding, deferred edits/removals and entity/world replacement. They do not establish peak
memory or crash behavior for Epic Citadel 16K.

Validation on 2026-09-11: Unity 6000.5.6f1 compilation succeeded; `RayQueryIdleGroupTests` (10),
`RenderGroupRebuildTests` (6) and `MeshingRendererTests` (9) all passed with no skips. The large
standalone demo was not launched and memory peaks were not measured.

### Buffer ownership

No sector keeps a host copy of its brick records. That copy used to be 1096 bytes per brick — about
4 GB on the 8K San Miguel and 7 GB on the 16K citadel — and existed purely so that a record could be uploaded again whenever a buffer
was replaced. `CaelixBrickGpuOps` (`Runtime/Rendering/CaelixBrickGpuOps.cs`, three kernels in
`Runtime/Resources/CaelixBrickPoolOps.compute`) removes it:

* **A page that grows** copies every live range into the new buffer with `CopyRanges`, in the same
  pass that decides the compacted layout. So compaction is no longer free — it costs one GPU copy of
  the page's live data — but a growth already copies all of it, and packing costs nothing on top.
* **A range that is resized** goes through `CaelixBrickPool.Reallocate`, which allocates the new
  range *while the old one is still live* (allocating can compact the page and move it), copies
  `min(old, new)` bricks with `MoveRanges` on one page or `CopyRanges` across two, and then frees the
  old range. If the pool is full, both ranges are gone and the sector sets `needsFullRebuild`, which
  makes its next render job re-emit every brick.
* **The render job** writes only the bricks it actually rewrote, into two `TempJob` lists
  (`stagingWords`, one 274-word record each, and `stagingSlots`, the renderer brick id of each), and
  the sector hands them to `StageScatter`. A record is staged zeroed, so the job no longer has to
  clear stale words.

### Scatters are batched, and that is load-bearing

`StageScatter` does not dispatch. It copies the sector's records into that sector's own region of one
**frame staging buffer** and appends one `uint2` per record — staging index, absolute destination
brick — to a list keyed by the destination buffer. The renderer calls `FlushScatter()` once, after
every sector has staged; the flush writes the pair buffer and then dispatches once per destination.

The reason is a device hang. The first version wrote a small reused staging buffer and dispatched per
sector. Writing a buffer the GPU is still reading makes the D3D12 backend rename or stage the **whole
buffer** per call, so on the first frame after a world load about 6800 sectors each asked for their
own 4.5 MB copy: `Ran out of Graphics Ring Buffer space` in the Editor log, 25 GB of non-local memory
reserved by the driver with none available, then `DXGI_ERROR_DEVICE_HUNG` (887a0006). Batched, a frame
costs one buffer's worth of upload memory however many sectors moved, and the dispatch count drops to
one per destination — one per page in pool storage.

Three details keep that property:

* **Two frame staging buffers alternate.** A batch that reaches `MaxBatchBricks` (2^18 records, about
  287 MB) flushes early and switches buffers, so a second flush in one frame never rewrites the
  buffer the first flush's dispatches are still reading. The pair buffers alternate with them.
* **Growing a staging buffer carries the batch over with `CopyRanges`,** which does leave a dispatch
  on the buffer the next write touches — the pattern above, once per growth. It is bounded rather
  than removed: the buffer starts at 2^14 records (18 MB), never shrinks, and grows by powers of two,
  so a cold frame does a handful and a settled renderer does none.
* **`CopyRanges` / `MoveRanges` stay immediate.** There are a handful per frame and their range table
  is a few bytes. Ordering still works out: every range copy of a frame is recorded during pass 2a
  and every scatter dispatch in the flush after pass 2b, so a copy always carries the previous
  frames' records and the scatter then overwrites the ones this frame's job rewrote.

After 300 consecutive flushes with nothing staged, the staging and pair buffers are released; they
come back on demand.

Two things are load-bearing in the job. Records are addressed by slot, and `SparseBrickIdTable` hands
a freed id straight back out, so a brick removed early in a sweep and a brick added later in the same
sweep can name the same slot; two staged records for one slot would race inside the scatter kernel,
so the later brick takes over the removal's (already zeroed) record instead of appending a second
one. And `syncRecord` is down to one element, "some AABB changed" — the modified-brick range it used
to carry only existed to bound a partial upload.

The host AABB list stays. The AABB `GraphicsBuffer` is thrown away and replaced whenever a tight box
moves (Unity builds static AABB geometry once per buffer and ignores later writes), and a fresh
buffer needs the complete set, not the boxes that changed.

`CaelixRayQueryRenderer.Tick` therefore runs its GPU sync in two loops: pass 2a settles every
sector's pool range (which can grow and compact a page), pass 2b scatters records and updates the
acceleration structure. Doing both in one loop would write into a buffer a later sector then
replaces.

## DXR on the pool (history, removed 2026-09-10)

The comparison that decided the removal. The two backends differed in two independent ways — the
dispatch model (shader table vs. one compute kernel) and the brick storage (a buffer per sector vs.
the shared pool) — so `CaelixRenderer.brickStorage` was made to separate them and the DXR path was
run on the ray query path's own pool. On the 8K citadel from the courtyard camera, whole-frame GPU:
DXR per-sector ~10.0 ms, DXR on the pool ~10.7-11 ms, DXR per-sector with only the pool keyword
variant ~10.7 ms, and a third mode that also moved per-instance data into `g_Instances` (so every
hit-group shader record was identical) ~12.0 ms. **The ray query win is the dispatch model, not the
storage** — an intersection shader is a separate shader-table call with its own state-object
occupancy — and pooled storage on DXR was consistently a little slower than local root arguments.

Two facts from that work are still worth keeping:

* **D3D12 caps a buffer VIEW at 2^27 elements: 512 MB for a raw buffer.** A hit group read its page
  through a view from its shader record, so with pages larger than that every brick past 512 MB read
  as zero: sky leaking through walls, and a slower frame because the rays then travelled further.
  The compute kernel binds its pages as root descriptors and is not affected, which is why the ray
  query path rendered the same pool correctly. `CaelixBrickPool.DefaultDxrPageCapacityLimitBricks`
  (2^18) survives as the record of that limit; the live renderer uses 2^21.
* `MaxPages` (32) is what the pool may open; `MaxNamedPages` (16) is what a shader can switch over,
  and `CaelixRayQueryRenderer` logs an error when the pool opens more. Sixteen rather than four
  exists for the view limit above.

`CAELIX_BRICK_POOL` / `CAELIX_BRICK_POOL_TABLE`, `_BrickBase`, `_PrevObjectToWorld` and
`_SectorHashSeed` were the hit group's side of this and are gone with it.

## Readiness

`CaelixGBufferPass.IsReady` demands `CaelixRayQueryRenderer.HasResources` for this backend. The
component only owns its pool, instance table, material buffer and acceleration structure between
`Awake`/`Tick` and `OnDisable`, so the check is false in edit mode (a Scene view camera, where
`Awake` never ran) and while the component is disabled. Without it the pass dispatches against null
buffers and Unity logs `Property (g_Materials) at kernel index (0) is not set` every frame.

## Material table

`Assets/Caelix/VoxelMaterials.hlsl` (generated by Titania) holds its material tables as `static`
arrays, about 150 KB of immediate constant data. The DXR pipeline accepts that; a compute pipeline
does not. With the tables in the trace kernel, D3D12 fails `CreateComputePipelineState` with
`8007000e` (E_OUTOFMEMORY). **That failure is logged only in the Editor log
(`Logs/Editor.log`), not in the console, and the dispatch is silently skipped** — the symptom is a
frame with no Caelix output at all (URP sky only) while `HasKernel`/`IsSupported` still say yes.

So the compute kernel never touches the static tables. `CaelixPathTraceRQ.compute` redefines
`GET_MATERIAL(id)` to read `g_Materials[id & 0xFFFF]`, a structured buffer with one `VoxelMaterial`
per 16-bit block ID (`CaelixRayQueryRenderer.MaterialTable`, 65536 x 40 bytes). Three small
`CaelixBakeMaterials*` kernels fill it once from the static tables, one group of fields each:
copying whole structs keeps the whole array as one immediate constant and fails the same way,
while per-field reads let DXC split the table into per-field arrays that each fit the 64 KB
(4096 float4) immediate-constant limit. `CaelixGBufferPass` records the bake before the first
trace of every new material buffer (`CaelixRayQueryRenderer.MaterialsBaked`).

## Scene setup

1. Add a `CaelixRayQueryRenderer` component to the scene and assign its `brickMat`
   (`Caelix/AabbInstance`, the material `Runtime/Resources/Caelix_AabbInstance.mat`). The trace
   never runs a hit group, but `RayTracingAABBsInstanceConfig` requires a material.
2. Wire the component into `CaelixHost.rayQueryRenderer`.
3. On the URP renderer asset's Caelix feature, assign `RayQuery/CaelixPathTraceRQ.compute` to
   `rayQueryTracer`. For budget mode, assign `Budget/CaelixBudgetRQ.compute` to the budget
   feature's `rayQueryTracer`, and enable exactly one of the two features.

## One scene renderer at a time

Enable exactly one scene renderer per `ClientWorld`. Some of the state a renderer reads is
single-consumer:

* `EntityView.ShouldResetMotionVectors` is a flag its consumer **clears** after its per-view loop, so
  a second renderer never sees the frame an entity settled and keeps reprojecting it.

## Binding a renderer to a world that already exists

`SetSource(ClientWorld)` retires every group of the previous world, binds the new one and raises a
full-upload flag; `EnsureSource` calls it, `ReleaseResources` calls it with null, and `OnEnable`
raises the flag too. On the next `Tick`, a renderer holding that flag builds each view's work from
`VoxelEntityData.EnumerateBricks()` — every key as an `Updated` change carrying
`BlockBrickAdded | GeometryWithLocalNeighbor` — instead of from the cycle's change list.

This is what removed the old limitation that **a renderer enabled mid-Play drew nothing**: the
bricks that arrived before the renderer was looking had already had their require-update flags
consumed, and nothing would ever name them again.

## Notes

* The kernel writes 9 UAVs. That is fine on D3D12, but the D3D11 variant Unity also compiles logs
  "more than the 8 maximum currently supported on D3D11.0" as an error at import. Harmless here;
  the backend is D3D12-only anyway (inline ray tracing needs it).
* `CaelixPathTraceRQ.compute` declares `#pragma require inlineraytracing Int64`. `Int64` is there
  because the brick DDA loads the 64-bit micro-occupancy word in one go. If an editor ever rejects
  `Int64` as an unknown feature, drop it from that line rather than changing the DDA.
* `CaelixBrickTrace.hlsl` contains no DXR intrinsic at all: the DDA core takes its ray as
  parameters, and the brick-record loads go through the page switch in `CaelixBrickPages.hlsl`,
  which the including file must define first (`CaelixRayQueryTrace.hlsl` is where that order is
  fixed). The record layout it decodes is `Caelix.Rendering.BrickRecordLayout` on the C# side.
* The compute kernel is `CaelixPathTraceKernel`, dispatched at 8x8 threads per group; threads past
  `g_LaunchDim` return immediately.
