# Write an automaton

Source reviewed: Caelix `3d013d8`, Core `881efac3`, 2026-09-16.
The revised example has not been compiled or executed in Unity.

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
positive-X end by one cell per active tick. Use a disposable world, reserve
`Automata3` for this example, and disable packed scene color mode (which skips automata).

```csharp
using Caelix;
using Caelix.Simulation;
using Caelix.Tick;
using Unity.Mathematics;

public static class ErosionExample
{
    public const BrickUpdateFlags Channel = BrickUpdateFlags.Automata3;

    public static void Install(CaelixWorld world)
    {
        world.AutomataStage.RegisterHook(
            new ManagedHook<CaelixWorld.AutomataStageInputs>(inputs =>
            {
                foreach (var brick in inputs.BricksRequiredUpdate)
                {
                    if ((brick.Flags & Channel) == 0 || !brick.IsAllocated)
                        continue;
                    var access = inputs.ReadContext.OpenBrick(brick);
                    var reader = inputs.ReadContext.CreateReader(brick, access);

                    // Dense rule evaluation within ONE already-selected brick.
                    // Brick discovery is done by the world's enumerator/collector.
                    for (int z = 0; z < BrickKey.BlocksPerAxis; z++)
                    for (int y = 0; y < BrickKey.BlocksPerAxis; y++)
                    for (int x = 0; x < BrickKey.BlocksPerAxis; x++)
                    {
                        int3 p = access.Origin + new int3(x, y, z);
                        if (reader.GetLocalBlock(p).isEmpty)
                            continue;
                        if (!reader.IsVoxelSpaceOccupied(p + new int3(1, 0, 0)))
                            access.SetBlock(p, Block.Empty, Channel);
                    }
                }
            }));
    }
}
```

Call `ErosionExample.Install(world)` before `server.Step()` in the getting-started
setup. Before the seed write, configure
`AutomataDefaults.SetBlockDefault(0x8001, ErosionExample.Channel)`, then use
`world.SetBlock(entityId, position, block)`. Defaults are application-wide; run
this exercise with no other simulation active. The first step establishes required
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
geometry-only work are not in the list) and hands the same list to every hook. Filter `brick.Flags` as in the example. Allow unallocated records when your rule
can grow into empty space.

In a scheduling hook, use `inputs.BricksRequiredUpdateCount` to check for no work.
Reading the list's `Length` can conflict with an earlier scheduled job. Preserve
the incoming job dependency when skipping scheduling. Hooks still run on empty ticks.

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

- `brick.Key` uses entity-local brick units. `access.Origin` and all reader/writer
  positions use entity-local block units.
- `GetLocalBlock` reads only this entity. `GetBlock` and `IsVoxelSpaceOccupied`
  use local data first and can consult alien occupancy when local space is empty.
  The reader handles neighboring sectors; do not hand-code the coordinate transforms.
- The example tests local occupancy before writing, so it cannot erase a voxel
  that belongs to another entity. Alien fallback is a read-only influence.
- `AutomataBrick.SetBlock` writes through the pending buffer and marks changes.
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

If work is missing, check that propagation produced your channel's required flags,
the server is stepping, and registration happened before the first tick.

Test a single cell, a row, a brick boundary, a sector boundary, and a negative
coordinate. For an alien-aware rule, add two translated/rotated entities. Assert
both the resulting Block values and the tick on which they change.

- [World stage and inputs](../../Runtime/Simulation/CaelixWorld.cs)
- [Core hook APIs](https://github.com/betairylia/Caelix-Core/tree/main/Runtime/Tick)
- [Automata brick tests](https://github.com/betairylia/Caelix-Core/blob/main/Tests/Editor/AutomataBrickTests.cs)
- [Tick-hook tests](https://github.com/betairylia/Caelix-Core/blob/main/Tests/Editor/TickStageTests.cs)
