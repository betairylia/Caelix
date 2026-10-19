# Result file schema (every harness writes this JSON, one file per run)

File name: `results/<harness>/<method>__<scene>__<pose-id>__<mode>.json` (pose-id = p1, p2, ...; mode = P or B2;
fractalcave uses pose-id `dyn`). All times in milliseconds, all sizes in bytes, all counts as integers.

```json
{
  "schema": 1,
  "status": "ok | cannot_run | timeout",
  "reason": "only when status != ok: why (e.g. world exceeds the 4096^3 address window)",
  "world_offset": [0, 0, 0],
  "harness": "caelix | voxelrt | naadf",
  "method": "Caelix | PlainDDA | MultiDDA | XBrickMap | ManhattanDF | EuclideanDF | OctantDF | ESVO | Tree64 | StdBVH | NanoVDB | NAADF",
  "scene": "bistro-8192 | rootfall | fractalcave",
  "scene_file": "absolute path of the file that was loaded",
  "pose": {"id": "p1", "position": [x, y, z], "pitch_deg": 0.9, "yaw_deg": 56.2, "fov_y_deg": 60.0},
  "resolution": [2560, 1440],
  "mode": "P | B2",
  "bounces": 0,
  "frames_warmup": 30,
  "frames_measured": 200,
  "gpu_ms": {"per_frame": [], "median": 0, "p5": 0, "p95": 0, "mean": 0},
  "gpu_ms_breakdown": {"<pass name>": [] },
  "cpu_frame_ms": {"median": 0, "p95": 0},
  "rays": {"counted": true, "primary": 0, "bounce": [0, 0], "total": 0, "nominal": 11059200, "primary_hit_fraction": 0.0},
  "traversal": {"iters_per_ray": 0.0, "clocks_per_iter": 0.0},
  "memory_bytes": {"structure": 0, "breakdown": {"<buffer>": 0}, "process_vram_before": 0, "process_vram_after": 0},
  "load_seconds": {"file_to_cpu": 0.0, "cpu_to_gpu_traceable": 0.0, "total": 0.0},
  "dynamic": {"bricks_changed_per_frame": 18432, "voxels_toggled_per_frame": 4718592,
              "per_frame": [{"frame": 0, "cpu_edit_ms": 0, "gpu_update_ms": 0, "gpu_render_ms": 0}],
              "timeout_hit": false},
  "screenshot": "relative path of the PNG of the last measured frame",
  "machine": {"gpu": "NVIDIA GeForce RTX 5080", "driver": "610.88", "cpu": "Intel Core i7-14700KF", "os": "Windows 11"},
  "software": {"repo": "...", "branch": "...", "commit": "...", "api": "Vulkan 1.3 | D3D11", "build": "Release"},
  "timestamp": "2026-10-18T12:00:00",
  "notes": "free text: anything that deviates from the protocol"
}
```

Rules:
- `world_offset` is the translation the converter added to the voxel coordinates of the loaded file (scene JSON
  `world_offset`); the harness adds the same offset to the pose position so that all harnesses see the same view.
  Scenes with negative coordinates (rootfall) are shifted to a non-negative origin for harnesses that need it.
- `status` = `cannot_run` files carry the method/scene/mode fields and `reason`, nothing else is required.
- `gpu_ms.per_frame[i]` is the GPU time of the traced work of frame i (primary pass, plus bounce passes in B2).
  It excludes presentation, UI, blit to the swapchain and any denoiser/TAA. Each harness documents in
  `gpu_ms_breakdown` what the sum contains.
- `rays.counted = false` means the counts are estimates; say how they were estimated in `notes`.
- `nominal` = width * height * (1 + bounces).
- `memory_bytes.structure` is the GPU memory of the voxel data + acceleration structure only (no G-buffers).
- Dynamic runs: `gpu_ms` holds the render part; `dynamic.per_frame` holds the update parts.
