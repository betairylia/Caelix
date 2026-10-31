# Changelog

## [0.2.0] - 2026-10-31

- Server-client v1: authoritative `CaelixServer` and `CaelixWorld`, Block-only client replica (`CaelixClient`, `ClientWorld`, `EntityView`), `CaelixHost` running both over `LocalChannel`, world lifecycle messages, two-phase replication. Contract: `Documentation~/internals/SERVER_CLIENT_ARCHITECTURE.md`.
- Brick-key facade, milestones 1 to 4: render groups and change-list consumption (`RenderGroup`, `ChangeBuckets`), keyed per-entity `BrickBatch` replication, mesh fallback on the facade, render group size as a renderer setting with shader variants.
- Inline ray-query renderer with a paged, compacting GPU brick pool and streamed initial uploads. The DXR render path is removed; the budget renderer runs on inline ray queries.
- `.cxw` save format v7: world record with the player pose passes through host and world save/load.
- Automata stage collects only simulation-channel work; alien candidates bind before automata.
- Native Vulkan renderer plugin bridge (`CaelixNativeRenderer`, `CaelixNativeRendererFeature`, packet v8): per-camera views and sessions, DLSS SR / RR, materials with subsurface fields, light tree and light NEE, voxel id target, sun from the scene's directional light, sky capture cubemap, background compositing with full-resolution depth. The binaries come from Caelix-Renderer and are not in this repository.
- License: GPL-3.0 with an additional permission (README, "License") for combining with Unity, the Caelix Physics package and the native renderer binaries.

## [0.0.1-dev.1]

- Rename the engine package and API to Caelix.
- Split Core and Physics into explicit package dependencies.
- Move research documents into `Documentation~`.
- The automata stage collects and snapshots only bricks with a simulation bit pending (`BrickUpdateFlags.AutomataMask`), not geometry-only work. `AutomataStageInputs.BricksRequiredUpdateCount` carries the list length so hooks can skip an empty tick without touching the list after an earlier hook scheduled over it.
