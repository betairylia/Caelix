# Benchmark protocol (draft 1, 2026-10-18, Fable)

Owner decisions (2026-10-18): the shared metric is "primary rays" and "primary + 2 diffuse
bounces", nothing else: no denoiser, no sun NEE, no shadow rays, no TAA, 1 sample per pixel,
sky light only. NanoVDB runs as a method inside the VoxelRT Vulkan harness (PNanoVDB HDDA).
Dynamic scene: methods without incremental edits report their full rebuild time per frame,
with a timeout that turns into "cannot run interactively".

## Machine

- CPU Intel Core i7-14700KF, 64 GB RAM, Windows 11 Pro for Workstations 10.0.26200
- GPU NVIDIA GeForce RTX 5080 16 GB, driver 610.88
- Caelix-Renderer: Vulkan 1.3, hardware ray query. VoxelRT (+NanoVDB): Vulkan compute, Slang
  shaders, clang 20. NAADF: D3D11 compute (MonoGame), HLSL.

## Modes

| Mode | Rays per pixel | Lighting | Notes |
| --- | --- | --- | --- |
| P   | 1 primary | none (albedo / Lambert) | pure visibility |
| B2  | 1 primary + 2 diffuse bounces | sky only, cosine-weighted hemisphere, no RR before bounce 2 | ray count is what each harness reports; the nominal count is pixels x 3 |

Resolution 2560x1440, vertical FOV 60 deg, same poses (Caelix pose convention, converted per
harness, verified by side-by-side screenshots).

## Measurements per (method, scene, pose, mode)

- GPU time per frame (timestamp queries), warm-up 30 frames, 200 measured frames: median, p5, p95.
- Rays per frame (counted where the harness counts; else nominal) -> Mrays/s.
- Traversal iterations per ray where available (VoxelRT counters).
- GPU memory: structure bytes reported by the harness + process VRAM.
- Construction: wall time from file open to first traceable frame (secondary).

## Dynamic (FractalCave)

- World 384x1024x384 (3x8x3 regions of 128^3), simplex-noise fill, 18432 bricks toggled per frame.
- Per frame: CPU edit time, GPU update time (structure sync / rebuild), render time (mode B2), total.
- Timeout: a method whose per-frame update exceeds 2 s is reported as "cannot run interactively"
  with its rebuild time.

## Open until research returns

- Exact ray generation parity (pixel centre, jitter off) per harness.
- Whether every harness can count rays; fallback is nominal rays.
- NAADF: GPU timestamp support in MonoGame D3D11.

## Poses (final, 2026-10-19)

| Scene | Pose | position, pitch, yaw |
| --- | --- | --- |
| bistro-8192 | p1 | 802.0,585.0,2616.4, 0.900, 56.200 |
| bistro-8192 | p2 | 2216.7,491.8,4393.1, -0.600, 83.300 |
| bistro-8192 | p3 (interior) | 2826.3,432.3,3579.6, 6.700, 28.700 |
| bistro-4096 | p1 | 401.0,292.5,1308.2, 0.900, 56.200 |
| bistro-4096 | p2 | 1108.35,245.9,2196.55, -0.600, 83.300 |
| bistro-4096 | p3 (interior, nudged 8 voxels forward: the half-scaled point is inside a wall) | 1417.0,215.2,1796.8, 6.700, 28.700 |
| rootfall | p1..p4 | as given by the owner |
| fractalcave | dyn | 192,1450,-500, 43, 0 |
