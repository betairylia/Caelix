# Caelix documentation

Caelix is an experimental Unity voxel engine. The server owns simulation data;
the client holds a replica for rendering and tools. `CaelixHost` runs both locally.

| Task | Page |
|---|---|
| Run a first data example | [Get started](manual/get-started.md) |
| Extend voxel behavior | [Write an automaton](manual/automata.md) |
| Choose a game integration model | [Use the server and client](manual/server-and-client.md) |
| Add game messages and callbacks | [Register commands, events, and queries](manual/commands-and-events.md) |
| Understand the simulation order | [Tick and dirty propagation](internals/tick-and-dirty.md) |
| Understand storage | [Core: VDB-like storage](https://github.com/betairylia/Caelix-Core/blob/main/Documentation~/internals/voxel-storage.md) |
| Choose a traversal API | [Core: enumerator rules](https://github.com/betairylia/Caelix-Core/blob/main/Documentation~/reference/enumerators.md) |
| Author a voxel spline | [Voxel spline authoring](manual/voxel-spline-authoring.md) |
| Work on rendering | [Budget renderer](internals/rendering/BUDGET_RENDERER.md), [RT G-buffer](internals/rendering/RT_GBUFFER_LAYOUT.md), [Ray query backend](internals/rendering/RAYQUERY_RENDERER.md) |
| Review the detailed network contract | [Server/client architecture](internals/SERVER_CLIENT_ARCHITECTURE.md) |
| Understand earlier decisions and measurements | [Historical documents](archive/index.md) |
| Write or maintain a page | [Documentation guide](documentation-guide.md) |

Start with Get started. Read the owning contract before changing a subsystem.
Cross-repository links target `main` and may await merging; local source links
follow this checkout.

## Source and validation

- [Package dependencies](../package.json)
- [Contributor rules](../AGENTS.md)
- [Editor tests](../Tests/Editor)
- [Recorded local-host validation](archive/validation/RND_VALIDATION.md)
