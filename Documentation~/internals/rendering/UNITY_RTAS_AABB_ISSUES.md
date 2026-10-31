# Unity RayTracingAccelerationStructure: two AABB-instance behaviours worth reporting

Written 2026-09-10 as a report-ready record. Issue 1 was verified that day with the scripts named
below. Issue 2 was verified in June and July 2026 while building the renderer; it is written up from
those notes and from the code that still works around it, and was not re-run for this document.

Environment: Unity 6000.5.6f1 (issue 2 first seen on 6000.3.15f1), Windows 11 Pro for Workstations
10.0.26200, Direct3D12, NVIDIA RTX 5080. `RayTracingAccelerationStructure` in
`ManagementMode.Manual`, `rayTracingModeMask = Everything`, instances added with
`AddInstance(in RayTracingAABBsInstanceConfig, Matrix4x4, uint id)` on procedural AABB geometry
(one AABB per 8x8x8 voxel brick, up to 4096 AABBs per instance, `dynamicGeometry = false`,
`accelerationStructureBuildFlagsOverride = true`, `PreferFastTrace`).

## Issue 1: disposing a GraphicsBuffer referenced by a live AABB instance silently removes the instance and recycles its handle

### Observed

1. Add instances A and B on their own AABB buffers. `GetInstanceCount()` is 2. `Build()` succeeds.
2. `bufferA.Dispose()`. No API call on the acceleration structure.
3. `GetInstanceCount()` is now 1. Instance A is gone. No exception, no console message.
4. The next `AddInstance` returns A's old handle. The allocator is a monotonic counter with a LIFO
   free list, and the dispose pushed A's handle onto it.
5. A caller that still holds A's handle and later calls `RemoveInstance(handleA)` removes the
   instance that inherited the handle. Again no message. `RemoveInstance` of a handle that is not
   live is otherwise a silent no-op.

Steps 2 and 3 happen whether or not `Build()` ran between add and dispose. One instance is removed
per disposed buffer.

### Why it matters

Any renderer that replaces an instance's AABB buffer (see issue 2 for why we must) and does the
`RemoveInstance` + `AddInstance` in a later pass than the buffer replacement gets, with two or more
instances replacing buffers in one frame, two owners of one handle. Each owner's rebuild then removes
the other's instance. In our case two adjacent voxel sectors edited in one frame rendered as one
half, and every later edit swapped which half was visible. The acceleration structure reported one
instance fewer than the renderer owned, and nothing else. It took a handle ledger on our side to
name the colliding call.

### Minimal reproduction (Edit Mode, no camera, no scene)

```csharp
var settings = new RayTracingAccelerationStructure.Settings
{
    rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything,
    managementMode = RayTracingAccelerationStructure.ManagementMode.Manual,
    layerMask = -1,
};
var AS = new RayTracingAccelerationStructure(settings);
Material mat = /* any material with a ray tracing pass */;

GraphicsBuffer MakeBuffer(int boxes)
{
    var b = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4096, 24);
    var data = new Vector3[boxes * 2];
    for (int i = 0; i < boxes; i++) { data[2 * i] = new Vector3(i, 0, 0); data[2 * i + 1] = new Vector3(i + 1, 1, 1); }
    b.SetData(data);
    return b;
}

RayTracingAABBsInstanceConfig Config(GraphicsBuffer b, int boxes) =>
    new RayTracingAABBsInstanceConfig(b, boxes, false, mat)
    {
        dynamicGeometry = false,
        accelerationStructureBuildFlagsOverride = true,
        accelerationStructureBuildFlags = RayTracingAccelerationStructureBuildFlags.PreferFastTrace,
    };

GraphicsBuffer a = MakeBuffer(70), b = MakeBuffer(271);
int ha = AS.AddInstance(Config(a, 70), Matrix4x4.identity, 0);                          // 1
int hb = AS.AddInstance(Config(b, 271), Matrix4x4.Translate(Vector3.right * 128), 1);   // 2
AS.Build();
Debug.Log(AS.GetInstanceCount());   // 2

// The pattern a renderer uses when a tight AABB set changes (see issue 2): new buffer per instance.
a.Dispose();
b.Dispose();
Debug.Log(AS.GetInstanceCount());   // 0  <-- both instances are gone already
GraphicsBuffer a2 = MakeBuffer(71), b2 = MakeBuffer(271);

AS.RemoveInstance(ha);                                                                  // no-op, 1 is dead
int ha2 = AS.AddInstance(Config(a2, 71), Matrix4x4.identity, 0);                        // returns 2 (LIFO)
AS.RemoveInstance(hb);                                                                  // deletes the instance just added as ha2
int hb2 = AS.AddInstance(Config(b2, 271), Matrix4x4.Translate(Vector3.right * 128), 1); // returns 2
Debug.Log($"{ha2} {hb2} {AS.GetInstanceCount()}");   // "2 2 1": two owners, one instance
```

Moving the two `Dispose()` calls to after the last `AddInstance` gives handles 1 and 2 and a count
of 2. An intervening `Build()` does not change the outcome; creating the new buffers before
disposing the old ones does not either.

### Allocator facts established alongside (for the report's context)

- Handles start at 1 and are never 0; 0 is the documented failure value.
- `RemoveInstance` pushes the handle on a LIFO free list; `AddInstance` pops the most recent one,
  otherwise returns high-water + 1. The high-water mark never decreases.
- The free list is global to the acceleration structure; the handle does not depend on the config,
  the buffer, the matrix or the `id` argument.
- `Build()` and `ClearInstances()` do not reset the counter. `ClearInstances` frees every live
  handle into the same list.
- `RemoveInstance` of an already freed or never issued handle (0, 9999) is a silent no-op that
  does not touch the free list.

### What we would ask for

- Document the ownership rule: an AABB instance references its buffer without keeping it alive,
  and disposing the buffer removes the instance.
- Preferably do not recycle the handle of an instance removed this way until the owner calls
  `RemoveInstance`, or raise an error on the dispose, or log a warning. Silent removal plus
  immediate handle reuse is what turns a lifetime mistake into corruption of unrelated instances.

### Workaround in this project

Keep the previous buffer alive until `RenderModifyAS` has removed and re-added the instance on the
new buffer, then dispose it (`staleAabbBuffer` in `RayQueryGroupRenderer`).
A handle ledger (`InstanceHandleLedger`) logs any claim of a live handle or release by a
non-owner. `RtasAabbBufferLifetimeTests` pins the behaviour so an upgrade that changes it is
noticed.

Scripts used, kept in `rtas-repro/` next to this file: `rtas_handles.cs`, `rtas_handles2.cs`,
`rtas_handles3.cs` (allocator facts, with their `_out.txt` logs), `rtas_test.cs` and `rtas_diag.cs`
(buffer dispose). They are `unity command eval_file` bodies: no `using` directives, fully qualified
names, run inside the Editor. The test class above is the committed form.

## Issue 2: static AABB geometry is built once per (buffer, aabbCount) and never refreshed from later buffer writes

### Observed (June and July 2026, 6000.3.15f1; the workaround is still required on 6000.5.6f1)

1. Add an AABB instance with `dynamicGeometry = false` on buffer B with N boxes. Build. Correct.
2. Write new box extents into B with `SetData` (same N). `RemoveInstance` + `AddInstance` with the
   same config. Build. The instance still traces the OLD boxes: the bottom-level structure is
   reused from a cache keyed by (buffer, aabbCount), and nothing invalidates it. Visible effect:
   an edit that grows a brick's tight box is cropped; an edit that shrinks it leaves phantom
   volume. To the user, edits are ignored or the scene shows the state one edit earlier.
3. `dynamicGeometry = true` is the documented way out, but it is baked into the instance at
   `AddInstance` and rebuilds that bottom-level structure on every `Build()`, so a large scene
   where every instance may be edited pays a full rebuild every frame.
4. Toggling per instance (register as dynamic while edits land, re-add as static on the falling
   edge) only half works: the first static add per buffer is correct, every later static add of
   the same buffer resurrects the first cached entry. Alternating between two buffers, or between
   two `aabbOffset` slots of one buffer, fails the same way: the alternate add lands on the
   poisoned key again.
5. `UpdateInstanceGeometry` is documented as having no effect on AABB instances.
   `accelerationStructureBuildFlags` on the config is ignored unless
   `accelerationStructureBuildFlagsOverride` is set (otherwise the structure-level
   `Settings.buildFlagsStaticGeometries` / `DynamicGeometries` win, and `new Settings()` leaves
   both `None`).
6. Only a brand-new `GraphicsBuffer` produces a fresh bottom-level structure.

### Why it matters

Correct static AABB geometry after an edit requires allocating a new buffer per edited instance
per frame (96 KB at 4096 boxes) and re-uploading the whole box list, because the cache cannot be
invalidated and dynamic geometry is too expensive for a whole scene. That reallocation is exactly
what triggers issue 1.

### Reproduction sketch (not re-run for this document)

```csharp
// Instance on buffer B with N boxes, dynamicGeometry = false. Build once and trace: correct.
// Then, on an edit:
B.SetData(newBoxes);                 // same N, different extents
AS.RemoveInstance(h);
h = AS.AddInstance(sameConfig, m);   // same buffer, same aabbCount
AS.Build();
// Trace again: old extents. Replace B with a fresh GraphicsBuffer and repeat: new extents.
```

### What we would ask for

- An explicit invalidation for procedural geometry: make `UpdateInstanceGeometry` work for AABB
  instances, or add a geometry version to `RayTracingAABBsInstanceConfig`, or document that the
  cache key is (buffer, aabbCount) and that `RemoveInstance` + `AddInstance` does not rebuild.
- A per-instance refit or rebuild request that does not require `dynamicGeometry` for the life of
  the instance.

### Workaround in this project

On any AABB change the renderer disposes the instance's buffer and allocates a fresh one at full
capacity, uploads the whole box list, zeroes `aabbCount` on the config so it is rebuilt against the
new buffer, and removes and re-adds the instance. Since 2026-09-10 the dispose is deferred until
after the re-add (issue 1). The fallback if the churn ever becomes a problem is content-independent
full-brick boxes, which change the AABB set only on brick add and remove.
