# TODO

## Rendering

- Handle ray tracing AABB removal/compaction for culled renderer bricks.
  `GenerateGroupRenderDataJob.ProcessBrick` can remove entries from `rendererBrickMap`, but the group's RTAS instance still uses the existing AABB buffer layout. The zero-occupancy brick upload makes stale AABBs shader-miss, but the RTAS and renderer brick map can still contain dead primitive slots. Decide whether to keep tombstoned AABBs intentionally or add a compaction/rebuild strategy.

- Align mesh fallback invalidation with the dirty/require-update contract.
  `SectorMeshRenderer.ScheduleJobs` scans `brickDirtyFlags`, while world tick clears dirty flags before renderer tick. Mesh rendering should consume propagated `requireUpdate` flags like the ray tracing renderer, and it should invalidate neighboring chunks/sectors when exposed faces cross chunk or sector boundaries.
