# Changelog

## [0.0.1-dev.1]

- Rename the engine package and API to Caelix.
- Split Core and Physics into explicit package dependencies.
- Move research documents into `Documentation~`.
- The automata stage collects and snapshots only bricks with a simulation bit pending (`BrickUpdateFlags.AutomataMask`), not geometry-only work. `AutomataStageInputs.BricksRequiredUpdateCount` carries the list length so hooks can skip an empty tick without touching the list after an earlier hook scheduled over it.
