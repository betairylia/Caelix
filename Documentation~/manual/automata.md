# Write an automaton

Status: Current API walkthrough. Checked against Caelix `253d632` and Core
`e5ead50` on 2026-09-06. The example was source-reviewed and compile-checked against
local Unity validation assemblies. It was not executed in Unity during this pass.

An automaton reads neighboring voxel state and writes the next state during a
world's automata stage. Start with a managed hook to understand the behavior, then
move expensive work into jobs using the same read and write rules.

## Before you start

Complete [Get started](get-started.md) and read [Tick and dirty propagation](../internals/tick-and-dirty.md).
You need the server's `CaelixWorld`, not the client's replica. Register hooks
before the first tick: `TickStage` locks registration on its first `Schedule`,
including when it initially has no hooks. With a host, call `EnsureInitialized()`
and install hooks during initialization before its first fixed step.

## Register a small rule

This example removes a local occupied cell if the cell immediately to its positive
X side is unoccupied. It is a simple erosion rule: a solid row recedes from its
positive-X end by one cell per active tick. It applies to all required bricks in
the world; use it in a disposable test world.

```csharp
using Caelix;
using Caelix.Simulation;
using Caelix.Tick;
using Unity.Mathematics;

public static class ErosionExample
{
    public static void Install(CaelixWorld world)
    {
        world.AutomataStage.RegisterHook(
            new ManagedHook<CaelixWorld.AutomataStageInputs>(inputs =>
            {
                foreach (var brick in inputs.BricksRequiredUpdate)
                {
                    var reader = inputs.ReadContext.CreateReader(brick);

                    // Dense rule evaluation within ONE already-selected brick.
                    // Brick discovery is done by the world's enumerator/collector.
                    for (int z = 0; z < Sector.SIZE_IN_BLOCKS; z++)
                    for (int y = 0; y < Sector.SIZE_IN_BLOCKS; y++)
                    for (int x = 0; x < Sector.SIZE_IN_BLOCKS; x++)
                    {
                        int3 p = brick.BrickOrigin + new int3(x, y, z);
                        if (reader.GetLocalBlock(p).isEmpty)
                            continue;
                        if (!reader.IsVoxelSpaceOccupied(p + new int3(1, 0, 0)))
                            brick.Sector.SetBlock(p.x, p.y, p.z, Block.Empty);
                    }
                }
            }));
    }
}
```

Call `ErosionExample.Install(world)` before `server.Step()` in the getting-started
setup. Seed a voxel through `world.SetBlock`. The first step establishes required
work through propagation; the second step removes an isolated voxel. Check the
server value with `world.GetBlock(entityId, position).isEmpty` after the second
step, and receive again to observe the empty value on the client. The original
getting-started assertion expects an occupied voxel, so replace it for this exercise.

The bounded 512-cell loop evaluates a local rule over an already selected brick.
It does not discover bricks by scanning the sector coordinate space. Bitmap
enumerators are appropriate for occupied-only algorithms once their masks are
current. Automata cannot generally assume a mask reflects edits made just before
the stage; rules that create cells also need to consider empty positions.

## Pick your channel out of the list

Require-update bits 0-11 are application channels (`BrickUpdateFlags.Automata0` ..
`Automata11`); the engine gives them no meaning. Name the ones you use in your own
code. The stage collects every brick with any channel bit pending (bricks with
geometry-only work are not in the list) and hands the same list to every hook. A
hook selects its own records at the top of its job:

```csharp
public const BrickUpdateFlags Channel = BrickUpdateFlags.Automata3;

public void Execute(int index)                 // IJobParallelForDefer over inputs.BricksRequiredUpdate
{
    RequiredBrick brick = bricks[index];
    if ((brick.Flags & Channel) == 0) return;  // another channel's work
    if (!brick.IsAllocated) return;            // drop this line only if your rule grows into empty space
    ...
}
```

In the managed hook, test `inputs.BricksRequiredUpdateCount == 0` to skip
scheduling on an empty tick, and return the incoming `chained` handle so later
hooks keep their dependency. Never read the list's `Length` there: an earlier hook
may already have scheduled a job over the list. The hook runs every tick even
when the list is empty, so per-tick bookkeeping (draining queues, advancing a
seed) stays deterministic.

Writes name their channel. `access.SetBlock(pos, block, Channel)` and
`access.SetSlot(slot, pos, value, Channel)` raise exactly that channel (plus the
engine's geometry bookkeeping) and never consult the default tables. To be
scheduled again next tick without changing a voxel, call
`access.SetDirty(Channel)`; it marks the whole center brick dirty on that channel.
If a rule must wake another automaton (a new electron head next to grass), OR that
automaton's channel into the write that creates the trigger, and only then.

Generic writes without a mask (player edits, importers) use the application's
`AutomataDefaults` tables: a Block write raises `Blocks[previous.id] | Blocks[value.id]`,
any other slot raises its slot default. Fill the tables once before the first
generic write and the first tick; a `RuntimeInitializeOnLoadMethod` is the usual
place. A generator that writes inert geometry passes `BrickUpdateFlags.None`
explicitly so the air rule does not wake every automaton across a whole terrain.

## Read and write correctly

- `BrickOrigin` and the reader's input positions are sector-local voxel coordinates.
  `SectorPos` identifies that sector in entity-local sector space.
- `GetLocalBlock` reads only this entity. `GetBlock` and `IsVoxelSpaceOccupied`
  use local data first and can consult alien occupancy when local space is empty.
  The reader handles neighboring sectors; do not hand-code the coordinate transforms.
- The example tests local occupancy before writing, so it cannot erase a voxel
  that belongs to another entity. Alien fallback is a read-only influence.
- `SectorHandle.SetBlock` writes through the active snapshot and marks changes.
  Reads remain on the live state until the world completes and applies the stage.
- Own the cells you write. Keep entity add/remove, transforms, and static changes
  outside this stage. Do not clear flags or dispose stage inputs in the hook.

For cross-entity influence over time, enable `doAlienPropagation` in the world
configuration (or the host Inspector). It schedules existing target bricks after
physics. Alien reads and scheduling are separate concerns.

## Move the rule into jobs

Use `BurstParallelJobHook<CaelixWorld.AutomataStageInputs, TJob>` with one
`IJobParallelFor` index per collected brick. `PrepareJob` supplies the brick array
and read context; `GetBatchCount` returns the number of collected bricks. Start
with the default chained dependency. Only choose parallel hooks when their
writes are independent; chaining after earlier chained hooks does not wait for
unrelated parallel hooks. The world completes the returned stage handle before
applying snapshots.

`ManagedHook` completes its selected dependency and executes synchronously. It is
useful for this prototype but adds a synchronization point. Keep Unity objects
and managed callbacks out of Burst jobs.

## Debugging and checks

If work is missing, check that a prior propagation phase produced required flags,
the server is stepping, and registration happened before the first tick. For new
code, use brick coordinates rather than the collector's cached compact ID or
cached flags; the [enumerator reference](https://github.com/betairylia/Caelix-Core/blob/main/Documentation~/reference/enumerators.md#current-required-update-limitation)
describes the current limitation.

Test a single cell, a row, a brick boundary, a sector boundary, and a negative
coordinate. For an alien-aware rule, add two translated/rotated entities. Assert
both the resulting Block values and the tick on which they change.

- [World stage and inputs](../../Runtime/Simulation/CaelixWorld.cs)
- [Core hook APIs](https://github.com/betairylia/Caelix-Core/tree/main/Runtime/Tick)
- [Neighborhood-reader tests](https://github.com/betairylia/Caelix-Core/blob/main/Tests/Editor/VoxelNeighborhoodReaderTests.cs)
- [Tick-hook tests](https://github.com/betairylia/Caelix-Core/blob/main/Tests/Editor/TickStageTests.cs)
