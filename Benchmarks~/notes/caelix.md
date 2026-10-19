# Caelix native renderer: facts for a headless `--bench` mode in `caelix-app`

Research notes, 2026-10-18. Read-only survey of `F:\RP_Games\Caelix-family\Caelix-Renderer`
(HEAD `18a1dfd`, tree clean except `.idea/` and `.vscode/`). Paths below are relative to that
repository unless they start with a drive letter. `file:line` references are to the current files.

Abbreviations: `R` = `Caelix-Renderer/`, `app/` = `host-rs/caelix-app/src/`.

---

## 1. `caelix-app` CLI and frame flow

### 1.1 Arguments relevant to benchmarking (`app/main.rs:124-314`)

| Flag | Default | Meaning |
|---|---|---|
| `--frames N` | none (run until close) | Exit with code 0 after N **presented** frames, counted from the first frame (load frames included) (`main.rs:891-903`). |
| `--size WxH` | `1280x720` | Inner window size in physical pixels (`main.rs:164-166`, `445-447`). The swapchain and every target take the real window size (`frame.width/height`), which Windows can clamp to the screen. Record the real size, not the argument. |
| `--pose x,y,z,pitch,yaw` | world player pose, else `0,64,-128,15,0` | See 1.2. `allow_hyphen_values` is set, so negative values work (`main.rs:147-150`, `559-563`). |
| `--fov DEG` | `60` | **Vertical** field of view in degrees (`main.rs:151-153`, `camera.rs:96`). |
| `--mode primary\|pt\|ref` | `pt` with `--world`, else `primary` (`main.rs:1265`) | Always pass it explicitly. |
| `--view ...` | `albedo` | Primary mode debug view (albedo, normal, depth, group, brick, steps, material); pt-only views nrd-validation, nrd-diff, nrd-spec, motion (`settings.rs` `View`, lines 16-87). |
| `--no-shading` | off | Primary mode: no Lambert factor. |
| `--bounces N` | `4` | Lighting modes: stochastic bounces after the primary hit, 0..14 (`CAELIX_MAX_BOUNCES`, `caelix.h:633`). |
| `--bounces-transparent N` | `5` | Delta chain budget through smooth glass, 0..15; 0 = off. |
| `--no-checkerboard` | checkerboard on | pt mode: removes the glass checkerboard AND the cross-resolve pass (see 1.4). |
| `--no-denoise` | denoise on | pt mode: no NRD. |
| `--no-sun-nee` | NEE flag on | Clears `CAELIX_RENDER_FLAG_SUN_NEE`. |
| `--sun-radiance R,G,B` | `0,0,0` (sun off) | Sun disk radiance. |
| `--sky-zenith/--sky-horizon/--sky-ground` | `0.2,0.4,0.9` / `0.7,0.8,0.95` / `0.25,0.22,0.2` | Procedural sky. |
| `--sky PATH` | none | Cubemap sky (adds the SH dispatch inside `trace_ms`). Do not pass for the benchmark. |
| `--fog-density` | `0` | 0 = no fog. |
| `--no-sky-on-miss` | sky on miss | Composite only. |
| `--exposure`, `--emission-scale` | `1`, `1` | Irrelevant for timing. |
| `--world PATH` | none | `.cxw` streamed in by `world.rs`. |
| `--staging-mib N` | `64` | Staging ring size (>= 4) (`main.rs:141-143`, `500`). |
| `--group-shift N` | `4` (16^3 bricks) | Render group edge = 1 << N, 1..5. |
| `--encoding packed\|block-id` | `packed` | `CaelixSceneDesc::encoding` (`settings.rs:98-105` maps to 0 / 1). |
| `--warmup N` | `0` | pt mode: the `--screenshot` waits N frames (at least 3) after the world is complete. |
| `--screenshot PATH`, `--exit-after-screenshot` | | PNG readback 3 (or `--warmup`) frames after the world is complete; then the timing line is logged once (`main.rs:739-815`). |
| `--no-input` | off | Ignore keyboard/mouse and do not create the egui overlay at all (`main.rs:462-468`). Use it for the benchmark. |
| `--verbose` | off | Debug log level. |
| `--test-entity`, `--test-edits` | off | See section 6. |

Validation in `main()` (`main.rs:1262-1308`): bounces <= 14, `--accumulate` needs `ref`, transparent bounces <= 15, pt-only views need `pt`, `material` view needs `primary`. The start line logs mode, bounces, NEE flag, denoise, sky, fog, window size (`main.rs:1291-1308`).

### 1.2 `--pose` and the camera (`app/camera.rs`)

- Units: world units = voxels (blocks). Entities loaded from `.cxw` and a procedural entity at position 0 / identity rotation put entity-local block coordinates directly in world space. The free-camera speed is "world units (blocks) per second" (`main.rs:52-53`).
- Axes (`camera.rs:1-13`): Y up, LEFT-handed like Unity: +X right, +Y up, +Z forward. Roll is always 0.
- `pitch` = Unity `eulerAngles.x` in degrees, **positive looks down**. `yaw` = Unity `eulerAngles.y` in degrees, yaw 90 looks along +X. Angles are wrapped to (-180, 180] (Unity's 350 becomes -10) and pitch is clamped to +-89 (`camera.rs:34-38`, `170-177`).
- Basis (`camera.rs:52-59`):
  - `forward = (sin(yaw) cos(pitch), -sin(pitch), cos(yaw) cos(pitch))`
  - `right   = (cos(yaw), 0, -sin(yaw))`
  - `up      = (sin(yaw) sin(pitch), cos(pitch), cos(yaw) sin(pitch))`
- `Camera::to_caelix` (`camera.rs:85-103`) fills `CaelixCamera`: `position`, `right`, `up`, `forward` (pads 0), `tan_half_fov_y = tan(fov_y/2)` (vertical), `aspect = frame.width / frame.height` (`main.rs:714-715`), `near_z = 1`, `far_z = 8192` (depth debug view only, `main.rs:66-68`), `projection` = reverse-Z infinite (`camera.rs:110-118`, used only for a depth target, which the app does not pass), `jitter = [0, 0]`.
- Library ray generation (`caelix.h:585-591`, `shaders/common/caelix_frame.hlsl:130-137`): pixel (px, py), row 0 = top: `s = (px + 0.5 + jitter.x, py + 0.5 + jitter.y)`, `ndc = (s.x / W * 2 - 1, 1 - s.y / H * 2)`, `dir = normalize(right * ndc.x * tan * aspect + up * ndc.y * tan + forward)`.
- `P` in the window logs the pose in `--pose ... --fov ...` form (`camera.rs:78-83`, `main.rs:1011-1013`).

### 1.3 Mode `primary` (benchmark configuration A)

- One compute dispatch: the trace kernel `shaders/render/caelix_path_trace.hlsl` with `[numthreads(8,8,1)]`, groups `ceil(W/8) x ceil(H/8)` (`lib/src/render/renderer_render.cpp:554-578`).
- Per pixel: exactly ONE ray (`TraceScene`, `caelix_path_trace.hlsl:180-230`, called at `:544`). The delta chain is skipped in this mode (budget 0 when `!lighting`, `:559`; it draws no random number). No bounce, no shadow ray, no NEE.
- Shading: the albedo view writes the block albedo, times `0.25 + 0.75 * saturate(dot(n, sun_dir))` with `CAELIX_RENDER_FLAG_SHADING` (default on; `--no-shading` removes it) (`:570-608`). `sun_dir` in primary mode defaults to `-0.45,0.8,-0.3` (`settings.rs:9`). A miss writes `sky_color = (0.55, 0.7, 0.9)` (`settings.rs:11`). Alpha is 1 everywhere in this mode.
- The app passes only the colour target (the swapchain image, `B8G8R8A8_UNORM` preferred, `caelix-vk/src/swapchain.rs:96-103`) (`main.rs:700-713`), so no geometry target, no motion, no internal targets, no composite.
- `trace_ms` in this mode = this one dispatch. `composite_ms = 0`, `denoise_ms = 0`.

### 1.4 Mode `pt` (benchmark configuration B) and the minimal "nothing else" setting

Command line for B: `--mode pt --bounces 2 --no-denoise --no-sun-nee --sun-radiance 0,0,0 --bounces-transparent 0 --no-checkerboard` (no `--sky`, `--fog-density 0`).

What each flag removes:

- `--sun-radiance 0,0,0` (the default) already disables NEE in the kernel: `nee = (flags & SUN_NEE) != 0 && any(sun_radiance > 0)` (`caelix_path_trace.hlsl:420`). `--no-sun-nee` additionally clears the flag. With both, no shadow ray is ever traced; misses return the procedural sky (`SkyDiffuseMiss` -> `ProceduralSky`, `shaders/render/caelix_shading.hlsl:374-381`, `438-447`), and with radiance 0 the sun disk adds 0. This is pure sky-light path tracing.
- `--bounces-transparent 0`: the delta chain loop breaks at the first hit without crossing anything (`caelix_path_trace.hlsl:349-352`). For an opaque-only world the budget is irrelevant for rays (opaque hits end the chain at `:356-359`), but 0 is the cleanest setting.
- `--no-checkerboard`: IMPORTANT. With the flag on, `cross_resolve = composite && mode == PT && flag` (`renderer_render.cpp:273-274`) adds a second full-screen dispatch inside `composite_ms` (`:646-662`) even when the scene has no glass. `--no-checkerboard` removes that pass.
- `--no-denoise`: `options.denoise = 0` (`settings.rs:228`); `EnsureDenoiser` is never called (`renderer_render.cpp:292-295`), so no NRD instance, no NRD dispatches, `denoise_ms = 0` (`renderer_targets.cpp:306-310` reads only a recorded span). The composite still runs (it writes the colour target).

Dispatches recorded in pt mode with these flags: [trace `ceil(W/8) x ceil(H/8)`] -> barrier -> [composite `ceil(W/8) x ceil(H/8)`] -> barrier (`renderer_render.cpp:554-664`). The sky SH dispatch (1 workgroup) runs only with a host cubemap (`:559-571`).

Bounce semantics (`TracePath`, `caelix_path_trace.hlsl:416-511`):

1. Primary hit shaded by `ShadeVoxelHit` (`caelix_shading.hlsl:306-343`): for an opaque packed block, smoothness 0 and metallic 0 give `specular_chance = 0`, so the lobe is always diffuse, `k = 1`, direction `normalize(n + RandomUnitVector)`, origin pushed 0.002 along n.
2. `throughput = 1 / k = 1` (demodulated: the primary albedo is applied in the composite).
3. Loop `bounce_index = 1; while bounce_index <= bounce_count`: trace one segment (`TMax = t_max = 1e4`, `lib/src/render/frame_constants.h:110`). Miss -> add sky * throughput and stop. Hit -> shade, `bounce_index += 1`, add emission * throughput, `throughput *= albedo / k`, then **Russian roulette after every bounce hit, from the first one**: `p = max(throughput.rgb)`; stop if `p <= 0 || p < rand`; else `throughput /= p` (`:494-501`). The loop condition then decides whether another segment exists.
4. With `--bounces 2`: segment 1 is traced when the primary hits; segment 2 is traced only when segment 1 hits AND RR passes with `p = max(albedo of the first bounce hit)`. After segment 2 hits, RR draws a number but no third ray follows. So at most 3 rays per pixel.
5. The primary ray uses `TMax = 1e30` (`:94`, `:542`).

What `trace_ms` covers in pt mode: primary ray, delta chain (a no-op here), the bounce segments, and the writes of the internal targets (`view_z`, `normal_roughness`, `motion`, `albedo`, `deterministic`, `diff`, `spec`; `:690-770`). Motion is computed for every hit pixel in the lighting modes (`:647`), which loads the entity info rows. `composite_ms` = composite (+ cross resolve when the checkerboard is on).

Internal targets of the lighting modes are allocated even without NRD: all 12 images of `kInternalSpecs` (`lib/src/render/renderer_internal.h:46-59`; created in `EnsureInternalTargets`, `renderer_targets.cpp:246-275`) = 4+4+8+4+8+8+8+16+8+8+4+8 = **88 bytes per pixel** (for example 2560x1440: 324 MB). Not allocated in the primary mode.

Mode `ref` = pt with accumulation; not needed for the benchmark.

### 1.5 Is the app headless?

No. `create_state` always creates a winit window and a Vulkan surface/swapchain (`main.rs:442-460`). The swapchain uses `PresentModeKHR::FIFO` (`caelix-vk/src/swapchain.rs:164`), so presentation is vsync-limited (144 Hz on the dev machine per `docs/STEP6_LIGHTING.md:253-257`, which also notes "FIFO leaves the GPU partly idle"). `FRAMES_IN_FLIGHT = 2` (`caelix-vk/src/lib.rs:44`); `safe_frame_number` is the frame that last used the same slot, i.e. normally `frame_number - 2` (`lib.rs:463-466`). A minimized window gives size 0 and `begin_frame` returns `None` (frames are skipped, `lib.rs:406-408`, `main.rs:791-793`). `--no-input` + `--frames N` + `--size` give an unattended run, but a visible window stays required.

### 1.6 Per-frame order in `App::draw` (`main.rs:574-906`)

`begin_frame` -> (egui, absent with `--no-input`) -> `TestScene::update` + entity transforms (`:644-661`) -> `caelix_scene_recycle(safe)` (`:664`) -> test entity uploads (`:669`) -> world loader upload (`:670-673`) -> `caelix_scene_flush` (`:680`) -> loader `after_flush` (`:688-695`) -> `caelix_renderer_render` (`:734`) -> screenshot readback -> `end_frame` (submit + present) -> NRD info line -> timing line -> `--frames` exit. `frame_seed = frame_number as u32` (`:717`). In pt mode `reset_accumulation` is set while the world is not ready (`:726-731`).

### 1.7 GPU timings

`caelix_renderer_get_gpu_timings` fills `CaelixGpuTimings` (`include/caelix/caelix.h:896-911`):

| Field | Type | Unit | Covers |
|---|---|---|---|
| `trace_ms` | float | ms | Timestamps around the trace dispatch; with a host sky cubemap also the SH dispatch + barrier before it (`renderer_render.cpp:556-579`). |
| `flush_ms` | float | ms | `caelix_scene_flush` of that frame: growth copies, AABB moves/fills, staging -> pool copies, finalize dispatches, BLAS + TLAS builds (`lib/src/scene/scene_flush.cpp:684-770`). 0 when the flush recorded nothing. |
| `frame_number` | uint64 | - | The frame the values belong to; 0 = none yet. |
| `composite_ms` | float | ms | Composite dispatch (+ cross resolve). 0 in primary mode. |
| `denoise_ms` | float | ms | All NRD dispatches. 0 when not denoised. |

Mechanics (`lib/src/rhi/gpu_timer.cpp`, `renderer_targets.cpp:287-311`): timestamps are `ALL_COMMANDS` at both ends (`gpu_timer.cpp:89`, `99`); results are read without wait in `Resolve(safe)`, which runs inside `caelix_renderer_render` (`renderer_render.cpp:278-280`) and inside the flush for `flush_ms` (`scene_flush.cpp:503`). So the struct describes the newest **completed** frame, normally 2 frames behind the frame just recorded. `trace_ms` is the newest value; the other three are looked up by that frame number in a 16-entry history (`gpu_timer.h:21-22`). A span whose query slot is still pending is not measured (`gpu_timer.cpp:81-84`; 8 slots vs 2 frames in flight, so this does not happen in practice). There is no whole-frame GPU timestamp; barriers between passes and the host's present are outside every span.

Existing output: only log text. `timing_line` (`main.rs:1143-1150`): `"Caelix Renderer | trace X ms | denoise X ms | composite X ms | flush X ms | N fps"`, logged every 120 frames (`:877-882`), after a CLI screenshot (`:809`), and as the window title once per second (`:869-876`). The world loader logs load summaries (section 5). **No JSON or CSV output exists.** `serde_json` is already a dependency of `caelix-app` (`host-rs/caelix-app/Cargo.toml`), used by `materials.rs`.

Implementation hint for `--bench`: after each `caelix_renderer_render`, call `gpu_timings()` (`main.rs:1115-1121`); store a sample when `frame_number` is new and above the warm-up boundary; run 3 extra frames at the end (the last 2 frames are never read back otherwise). Also read `scene_stats()` per frame for the dynamic world (finalized and builds counters, `as_builds_pending`, `upload_bricks_free`).

---

## 2. Scene statistics and memory

`CaelixSceneStats` (`caelix.h:342-373`, filled in `lib/src/scene/scene_entities.cpp:580-624`):

| Field | Meaning |
|---|---|
| `entities`, `groups` (u32) | Live entities and render groups. |
| `bricks_resident` | Bricks with a GPU record. |
| `bricks_known` | Bricks with a slot. |
| `record_bytes` | Record pool allocation = `page_bytes * pages` (`lib/src/rhi/alloc/record_pool.h:64-67`). Page = `records_per_page` (default 2^18, `scene_entities.cpp:20`) x 1096 B = 274 MiB. **Page-granular**: use `bricks_resident * 1096` for the payload. |
| `slot_table_bytes` | Slot row pool (16 B per dense id, ranges double as groups grow, min 16 rows, `slot_table_pool.h:33`). |
| `staging_bytes` | Ring capacity (host-visible upload memory, not device-local). |
| `uploaded_bricks_total`, `pending_bricks` | Total accepted; staged but not flushed yet. |
| `record_pages` | Pages. |
| `finalized_bricks_total` | Finalize kernel workgroups (bricks) recorded so far. |
| `reverse_table_bytes`, `aabb_bytes`, `grid_bytes` | Finalize lookup tables (u16 per group-local position), AABB rows (24 B per dense id), entity group grids (u32 cells). |
| `blas_count`, `blas_bytes`, `tlas_bytes` | Groups with a BLAS; AS storage incl. blocks waiting for a safe frame. |
| `scratch_bytes` | AS build scratch capacity (default 128 MiB, grows). |
| `as_builds_total`, `as_builds_pending` | Builds recorded; groups whose BLAS is out of date + 1 if the TLAS is. |
| `material_table_bytes` | 65536 x 48 B = 3 MiB. |
| `upload_bricks_free` | Upper bound of bricks the next `upload_bricks` accepts (see 6.3). |

Scene GPU memory = `record_bytes + slot_table_bytes + reverse_table_bytes + aabb_bytes + grid_bytes + blas_bytes + tlas_bytes + scratch_bytes + material_table_bytes` (device-local) + `staging_bytes` (host-visible). Not reported by any API: page table (8 B per page), group infos (32 B per group), entity infos (128 B per entity), TLAS instance buffer (64 B per group), finalize dirty list (16 B per dirty brick, grows), deferred "growth source" buffers.

Renderer memory: no API. Lighting modes: 88 B/px internal targets (1.4); 3+ frame slots with a 528-byte constants buffer and descriptor pools; a 144-byte sky SH buffer. NRD only when denoising: `CaelixNrdInfo::permanent_bytes + transient_bytes` (`caelix.h:913-933`, `caelix_renderer_get_nrd_info`). Shared per-device kernels (pipelines, a 1x1 fallback cube). The swapchain images belong to the host.

Allocator statistics: the library uses VMA internally (`lib/src/rhi/vma.cpp`) but exposes no VMA statistics or budget call; `caelix-vk` does not enable `VK_EXT_memory_budget` (no reference in `host-rs/caelix-vk/src`). Options for a total: (a) sum the stats above + 88 B/px; (b) add `VK_EXT_memory_budget` in `caelix-vk` and read `VkPhysicalDeviceMemoryBudgetPropertiesEXT` before and after scene creation/load (heap usage deltas); (c) DXGI `QueryVideoMemoryInfo` or NVML per process.

---

## 3. Ray counting

There is **no ray counter** in any kernel (no `Interlocked*` in `shaders/render/`). The only per-ray statistic is the procedural-candidate count of the PRIMARY ray, shown by `--mode primary --view steps` as a heat map 0..64 (`caelix_path_trace.hlsl:143`, `203`, `574-578`); it is not read back.

Exact termination rules for configuration B (opaque-only packed world, NEE off, transparent budget 0):

- Ray 1 (primary): always, per pixel `W*H`.
- Ray 2 (bounce segment 1): iff the primary hits (`surface = chain.surface.hit`, `:695`, `:712-716`).
- Ray 3 (bounce segment 2): iff segment 1 hits AND `u < max(albedo_1)` where `albedo_1` is the linear material albedo of the first bounce hit (packed opaque: `rgb5 / 31`, no sRGB decode, `lib/src/scene/material_table.cpp:90-92`; throughput = `albedo_1` because the primary k = 1 and its albedo is demodulated).
- No other ray: no shadow ray (NEE off), no delta-chain ray (budget 0), no ray in the composite.

Estimate: `rays_B = P + H0 + sum over pixels whose segment 1 hit of max(albedo_1)`, with `P = W*H` and `H0` = primary hits. Bounds: `P <= rays_B <= 3P`. Ways to get numbers without changing the library:

- `H0` exactly: in pt mode the colour alpha is coverage (1 hit, 0 miss) (`caelix.h:823-826`); a readback (`Context::readback_now` / `take_readback`, RGBA8 swizzled from BGRA, `caelix-vk/src/lib.rs:291-379`) counts it. (`write_png` forces alpha 255; count before that.)
- Segment-1 hit fraction and `E[max(albedo_1)]`: not observable from the host. Either estimate (for an enclosed cave most segments hit; FractalCave `max(albedo) >= 16/31 = 0.516`, see section 7), or add an opt-in counter to the library: a BDA/RW buffer with one `InterlockedAdd` per wave (`WaveActiveCountBits`) in `TraceScene`, behind a frame-constant flag, read back by the host. That is a library change, outside the host-only scope.

For configuration A: rays = `W*H` exactly (one per pixel, including misses; a miss still traverses the TLAS).

---

## 4. Rendering architecture

- **Hardware ray tracing via inline ray queries** (`VK_KHR_ray_query` in a COMPUTE shader), not a ray tracing pipeline. Required extensions/features come from `caelix_get_device_requirements` (`caelix.h:114-142`): acceleration structure, ray query, BDA, descriptor indexing, sync2, scalar layout, int64, storage image read/write without format, subgroup quad ops for NRD.
- **Acceleration structures** (`caelix.h:222-234`, `lib/src/scene/scene_accel.cpp`): one BLAS per render group (default 16^3 bricks = 128^3 voxels) of **procedural AABB geometry, one AABB primitive per dense brick id** (`AabbGeometry`, stride 24 B, `VK_GEOMETRY_OPAQUE_BIT`, `scene_accel.cpp:23-32`). The AABB is the **tight bound of the brick's occupied voxels** in entity-local block coordinates, written by the finalize kernel; a free id or a brick without occupied voxels has a NaN row = inactive primitive. One TLAS with one instance per group with a BLAS; instance transform = entity transform; `instanceCustomIndex` = scene-wide group index (24 bits). Build mode BUILD + `PREFER_FAST_TRACE` only; no refit, no compaction (`scene_accel.cpp:45-58`). A group's BLAS is fully rebuilt whenever any of its bricks or AABB rows change; the TLAS is rebuilt when any BLAS was built, a group was freed or an entity moved.
- **How a ray finds a voxel** (`caelix_path_trace.hlsl:151-230`, `shaders/render/caelix_brick_trace.hlsl`):
  1. `RayQuery<RAY_FLAG_FORCE_OPAQUE>.TraceRayInline(g_tlas, ...)`; `while (q.Proceed())` visits procedural candidates (TLAS -> BLAS -> AABB hit, done by the RT hardware).
  2. Per candidate (`TraceCandidate`, `:151-174`): `CandidateInstanceID()` = group, `CandidatePrimitiveIndex()` = dense brick id. Load the 32-B `GroupInfo` (slot row base), the 16-B slot row; skip unless state = resident; record address = `page_table[record_index >> shift] + slot * 1096`.
  3. `CaelixTraceBrickPrimitiveCore` (`caelix_brick_trace.hlsl:365-438`): reads record words 0-1, slab-tests the ray (object space) against the tight bounds from word 1, rejects if beyond `CommittedRayT()`, computes the entry face.
  4. `CaelixTraceBrickRay` (`:289-357`): DDA in the 8^3 brick, at most 22 steps: skip empty 4^3 coarse cells via the 8-bit coarse occupancy (word 0 bits 16-23), else load the 64-bit micro-occupancy pair for that coarse cell and test the voxel's bit; on an occupied voxel read the 16-bit block value and terminate if opaque, or if transparent and the face bit of the entered face is set (`CaelixShouldTerminateBrickRay`, `:265-274`). Returns `t` and `matID << 16 | face flags`.
  5. The kernel commits `t` if `TMin <= t < CommittedRayT()` (`:210-216`); the closest commit wins after traversal. Normal = object face normal rotated by `CommittedObjectToWorld3x4`.
- **Record format** (`lib/src/scene/record_layout.h:10-43`, `shaders/common/caelix_record.hlsl:10-30`): 274 words = **1096 bytes per brick**, 8-byte aligned:
  - word 0: group-local brick position (bits 0-15) + coarse occupancy 2x2x2 (bits 16-23);
  - word 1: tight bounds `minX|minY<<3|minZ<<6|maxX<<9|maxY<<12|maxZ<<15`;
  - words 2-17: micro occupancy, 512 bits = the alive-and-nonzero mask (one bit per voxel, grouped per 4^3 coarse cell);
  - words 18-273: 512 raw 16-bit block values, two per word, voxel (x,y,z) in word `18 + (x>>1) + 4y + 32z`, even x in the high half.
  - So: 1024 B raw values + 64 B occupancy + 8 B header. Face bits live inside the 16-bit values of TRANSPARENT voxels only (packed: bits 8-13; block-ID: bits 9-14); opaque values stay unchanged (lossless rule, `caelix.h:206-217`).
  - "Occupied" = alive AND value != 0, alive = not all six neighbours opaque (`shaders/scene/caelix_finalize.hlsl:4-10`). Fully buried voxels are not in the mask, and a brick whose voxels are all buried or air gets a NaN AABB (no BLAS primitive).
- **Slot row** (`caelix_record.hlsl:31-48`, `caelix.h:380-382`): 16 B per dense brick id: `pos_state` (bits 0-23 group-local position, bits 24-31 state 0 free / 1 resident / 2 summary-only / 3 requested), `summary`, `record_index` (0xFFFFFFFF if not resident), `last_used`.
- Other tables: AABB row 24 B per dense id; reverse table u16 per group-local position (4096 per 16^3 group = 8 KiB); `GroupInfo` 32 B; `EntityInfo` 128 B (cur/prev object-to-world rows); TLAS instance 64 B.
- **Per-frame kernel list**:
  | Kernel | Where | Dispatch |
  |---|---|---|
  | finalize | `caelix_scene_flush` (`scene_flush.cpp:731-751`) | 1 workgroup of 64 threads per dirty brick (uploaded + resident 6-neighbours of uploaded/removed bricks), batches of <= 65535 (`gpu_structs.h:75`) |
  | BLAS builds, TLAS build | `caelix_scene_flush` (`:756-761`) | One build per dirty group (<= 256 build infos per call, `scene_accel.cpp:16`), then the TLAS |
  | sky SH | `caelix_renderer_render` | 1 workgroup, only with a host cubemap |
  | trace | `caelix_renderer_render` | `ceil(W/8) x ceil(H/8)` workgroups of 8x8 |
  | NRD (REBLUR_DIFFUSE_SPECULAR) | `caelix_renderer_render` | about 30 dispatches, only with denoise |
  | composite | `caelix_renderer_render` | `ceil(W/8) x ceil(H/8)`, lighting modes with a colour target |
  | cross resolve | `caelix_renderer_render` | `ceil(W/8) x ceil(H/8)`, pt + checkerboard flag |

---

## 5. World loading (`app/world.rs`, `host-rs/caelix-cxw`)

- `WorldLoader::open` (`world.rs:94-171`): memory-maps the `.cxw` (`caelix-cxw/src/lib.rs`: format <= 8, Deflate), adds one scene entity per file entity (`caelix_scene_add_entity`, `:108-134`), starts thread `cxw-decode` that runs `CxwFile::decode_all` on the rayon pool and sends every decoded sector (non-air bricks only, keys `[i32;3]`, blocks `u16 x 512` per brick) into a bounded channel of 32 sectors (`:136-148`).
- `upload` (`:209-258`), once per frame after `recycle` and before `flush`: drains the channel into `caelix_scene_upload_bricks` until `consumed < count` (ring full); the rest of that sector waits in `pending`.
- `after_flush` (`:265-308`): when the decoder is done and nothing is pending, logs the summary once (`log_summary`, `:378-455`: entities, groups, bricks resident, record pages, GPU memory of records/slots/staging, AS stats, decode and upload time, bricks/s, `scene stats: {stats:?}`), then sets `as_ready` at the first flush with `as_builds_pending == 0`.
- **Load-to-traceable** is printed: `"acceleration structures complete after the flush of frame N: X.XXX s after load start, ..."` (`world.rs:288-301`). `load_start` is taken at `open` (before the file is mapped). `is_ready()` = `as_ready` (`:175-177`); the screenshot / `--warmup` / `--screenshot-every` flows wait for it (`main.rs:697`, `747-755`, `1097-1113`). Without `--world`, `world_ready` is always true.
- Exit check: a brick count mismatch makes the process fail (`main.rs:1406-1408`).

---

## 6. Test/edit facilities and the upload API

### 6.1 `--test-entity`, `--test-edits` (`app/testscene.rs`)

A procedural entity without any `.cxw`: proof that the host can build a world from code. A hollow 24^3 box (56 bricks, coloured packed opaque faces, emissive corners) is added with `caelix_scene_add_entity` (`testscene.rs:377-426`), its bricks are built on the CPU (`box_brick`, `:620-631`) and queued; `upload_pending` (`:503-532`) calls `caelix_scene_upload_bricks` and keeps the unconsumed tail. It orbits and spins (transform per frame through `caelix_scene_set_entity_transform`, `main.rs:646-661`). `--test-edits` every `--test-edit-interval` frames: `wave` removes (`caelix_scene_remove_bricks`, `:481-499`) and uploads solid brick columns; `flicker` re-uploads 4 corner bricks (replace). Packed helpers: `opaque(r,g,b) = 0x8000 | r<<10 | g<<5 | b` (`:71-73`), `emissive(r,g,b,s) = 0x4000 | r<<10 | g<<6 | b<<2 | s` (`:75-77`). It logs AS builds/s, finalized bricks/s, flush mean/max per second and for the whole run (`:536-580`, `main.rs:1062-1076`). Use it as the template for FractalCave.

### 6.2 Library API (all in `caelix.h`)

- `caelix_scene_add_entity(scene, &CaelixEntityDesc{guid[16], position[3], rotation[4] xyzw}, &id)` (`:274-283`).
- `caelix_scene_upload_bricks(scene, entity, count, keys, blocks, &consumed)` (`:296-308`): `keys` = `CaelixBrickKey{int32 x,y,z}` = entity-local block >> 3, each axis in [-2^20, 2^20); `blocks` = `count * 512` values of `block_bytes` (only 2 is implemented: **raw u16 per voxel, 1024 B per brick**), voxel index `x + 8y + 64z`; may be unaligned. Same key = replace (same record, no new allocation: `scene_bricks.cpp:47-75`). Accepts as many as the ring holds; `consumed < count` means: flush, submit, and upload the rest later. One bad key rejects the whole call. Transparent values must carry no face bits.
- `caelix_scene_remove_bricks(scene, entity, count, keys)` (`:309-312`): unknown keys ignored; a group whose last brick goes is freed; neighbours are re-finalized.
- `caelix_scene_set_entity_transform`, `caelix_scene_remove_entity`.
- `caelix_scene_recycle(scene, safe)` (`:335-340`), `caelix_scene_flush(scene, &CaelixFrameDesc{cmd, frame_number, safe_frame_number})` (`:314-333`): frame numbers strictly increasing and > safe.
- `caelix_scene_get_stats`, `caelix_scene_query_brick` (debug), `caelix_scene_get_tlas`.

Per brick upload cost inside the library (`scene_bricks.cpp:269-305`): one 1096-byte staging span (8-byte aligned), CPU conversion into the record layout (`WriteRecord`, `:246-267`), queue finalize for the brick and its resident 6-neighbours (dedup per flush), mark the group's BLAS dirty.

### 6.3 Limits the implementer must respect

- **Staging ring** (`lib/src/rhi/staging_ring.h:18-34`, `staging_ring.cpp`): records may fill only `capacity - (capacity/16 + 1 MiB)` (the rest is reserved for rows). A frame's spans are released only when that frame is <= `safe_frame_number` (via `recycle` or the next flush). With 2 frames in flight, at upload time of frame f the spans of frame f-1 are still held.
- **`upload_bricks_free`** (`scene_entities.cpp:611-623`) = `(record space - used) / 1096`, an upper bound (wrap padding can lower it; `consumed` is the truth). Read it after a flush (or after `recycle`) to size the next frame's uploads, as the Unity plugin does.
- **Back-pressure**: `consumed` is the only hard signal; never assume a full accept. A partial accept in the dynamic benchmark means that frame did not apply all 18432 changes: log it (the Unity side counts such bricks as `bricks_dropped`).
- AS scratch budget 128 MiB per flush (`scratch_bytes`); builds that do not fit wait (`as_builds_pending > 0` after the flush). Log `as_builds_pending` per frame.
- Records: `records_per_page` 2^18 = 274 MiB per page; the pool allocates a whole new page when needed.
- Group index is 24 bits; dense ids are 16 bits (group shift <= 5).

### 6.4 FractalCave in the host: plan and numbers

Unity source: `F:\RP_Games\Caelix-family\Caelix\Runtime\TestWorld.cs` (class `TestWorld`, the SampleScene object "(DynamicBenchmark) FractalCave", `numSectors = (3, 8, 3)`, `CorruptionTick = 1`, parent and own transform at the origin, object inactive in the committed scene; `F:\RP_Games\Titania\Assets\Caelix\Scenes\SampleScene.unity:2240-2290`). `VoxelRegion.SizeInBlocks = 1 << 7 = 128` (`Caelix-Core/Runtime/VoxelRegion.cs:21-36`), region origin = `regionPos << 7`.

Fill (`TestWorld.cs:36-64`, `88-109`): for region `(i, k, j)` with i in 0..3 (x), k in 0..8 (y), j in 0..3 (z), every voxel `p = origin + (x, y, z)` (integer block coordinates, not centres):
`n = snoise(p / 72) + 0.5 * snoise(p / 24)` (Unity.Mathematics `noise.snoise(float3)`, float32); if `n > 0` the voxel is `new Block(n, p.y / 512f, 0.5f, 0f)`, else air.

Toggle every frame (`TestWorld.cs:136-210`), `p = Time.frameCount`, for every region: x in 0..128, z in 0..128 (the z expression reduces to `z = i` because `128 / Zs = 1`), y in `[(p % 32) * 4, (p % 32) * 4 + 4)`; each voxel becomes air if it was solid, or `Block(0.5f, 1.0f, 0.8f, 0f)` if it was air (XOR of occupancy, not a fill). The 4 rows lie in brick row `by = (p % 32) / 2` because 4 divides 8. Per region: 16 x 1 x 16 = 256 bricks; 72 regions: **18432 bricks per frame**, every one of them changed every frame. The phase cycles every 32 frames; the content returns to the start state every 64 frames.

Host world size: 384 x 1024 x 384 voxels = 151 M voxels; bricks 48 x 128 x 48 = **294,912**; CPU copy as u16 = 302 MB (or keep per-brick `[u16; 512]` arrays). With `--group-shift 4` one region = exactly one render group: **72 groups**, and every group is edited every frame.

Recommended host implementation:

1. One entity at position 0, rotation (0, 0, 0, 1). Encoding `packed`.
2. Generate on the CPU with rayon (already in `Cargo.lock` via `caelix-cxw`; add it to `caelix-app/Cargo.toml`). No noise crate is in `Cargo.lock`; port Unity's `noise.snoise(float3)` (Ashima Arts / Ian McEwan webgl-noise `noise3D.glsl`, see 6.5).
3. Initial upload: all 294,912 bricks (also the all-air ones, so the record set already matches the steady state after the slab passes over them; the Unity client allocates them too once a toggle writes them; see risks). 294,912 x 1096 B = 308 MiB of staging in total, so it takes several frames: call `upload_bricks` with the remaining keys every frame until done.
4. Ready = the flush after the last upload with `pending_bricks == 0` and `as_builds_pending == 0`. Then warm up, then measure.
5. Every measured frame (after `recycle`, before `flush`): toggle the slab in the CPU copy, build 18432 keys + 18432 x 512 u16 contiguous, one `upload_bricks` call (all replaces: no record allocation), check `consumed == 18432`.
6. Expected GPU work per frame: 18432 x 1096 B = 19.3 MiB of staging -> pool copies; finalize of the 18432 bricks plus their resident y+-1 neighbours (x/z neighbours are in the slab already; dedup per flush) = up to 55,296 workgroups; 72 BLAS rebuilds (up to 4096 AABBs each) + 1 TLAS. Unity reference (CLAUDE.md, validation 2026-10-18): flush 3.9-4.7 ms for this world through the plugin.
7. Staging: steady state needs about 2 x 19.3 = 38.5 MiB of record space (frame f-1 in flight + frame f). The 64 MiB default gives 59 MiB of record space: enough, but use `--staging-mib 256` (the Unity validation value) so the initial load takes fewer frames and wrap padding never refuses a slab.

Block values (section 7): fill `0x8000 | rr<<10 | gg<<5 | 16` with `rr = clamp(floor(n*32), 0, 31)`, `gg = clamp(floor(y*32/512), 0, 31) = min(y >> 4, 31)`; toggle colour `0xC3F9`.

### 6.5 Simplex noise reference (port to Rust, f32)

Unity.Mathematics `noise.snoise(float3)` is a direct port of webgl-noise `noise3D.glsl` (Ian McEwan, Ashima Arts; MIT). The Unity.Mathematics source is NOT in the local PackageCache (`com.unity.mathematics@8306a64d0cd6` 1.4.0 only holds `Forwarders.cs`; Unity 6.5 moved the code into the engine), so the listing below is the published GLSL from memory; verify the constants (`0.6` / `42.0` in the 2011 version that Unity ported; the 2022 webgl-noise update uses `0.5` / `105.0`). Bit-exact parity with Unity is not needed for a benchmark; the same field statistics are.

```glsl
vec3 mod289(vec3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 mod289(vec4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 permute(vec4 x) { return mod289(((x * 34.0) + 1.0) * x); }
vec4 taylorInvSqrt(vec4 r) { return 1.79284291400159 - 0.85373472095314 * r; }
float snoise(vec3 v) {
  const vec2 C = vec2(1.0/6.0, 1.0/3.0);
  const vec4 D = vec4(0.0, 0.5, 1.0, 2.0);
  vec3 i  = floor(v + dot(v, C.yyy));
  vec3 x0 = v - i + dot(i, C.xxx);
  vec3 g = step(x0.yzx, x0.xyz);
  vec3 l = 1.0 - g;
  vec3 i1 = min(g.xyz, l.zxy);
  vec3 i2 = max(g.xyz, l.zxy);
  vec3 x1 = x0 - i1 + C.xxx;
  vec3 x2 = x0 - i2 + C.yyy;
  vec3 x3 = x0 - D.yyy;
  i = mod289(i);
  vec4 p = permute(permute(permute(i.z + vec4(0.0, i1.z, i2.z, 1.0))
                                  + i.y + vec4(0.0, i1.y, i2.y, 1.0))
                                  + i.x + vec4(0.0, i1.x, i2.x, 1.0));
  float n_ = 0.142857142857;            // 1/7
  vec3 ns = n_ * D.wyz - D.xzx;
  vec4 j = p - 49.0 * floor(p * ns.z * ns.z);
  vec4 x_ = floor(j * ns.z);
  vec4 y_ = floor(j - 7.0 * x_);
  vec4 x = x_ * ns.x + ns.yyyy;
  vec4 y = y_ * ns.x + ns.yyyy;
  vec4 h = 1.0 - abs(x) - abs(y);
  vec4 b0 = vec4(x.xy, y.xy);
  vec4 b1 = vec4(x.zw, y.zw);
  vec4 s0 = floor(b0) * 2.0 + 1.0;
  vec4 s1 = floor(b1) * 2.0 + 1.0;
  vec4 sh = -step(h, vec4(0.0));
  vec4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
  vec4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
  vec3 p0 = vec3(a0.xy, h.x); vec3 p1 = vec3(a0.zw, h.y);
  vec3 p2 = vec3(a1.xy, h.z); vec3 p3 = vec3(a1.zw, h.w);
  vec4 norm = taylorInvSqrt(vec4(dot(p0,p0), dot(p1,p1), dot(p2,p2), dot(p3,p3)));
  p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;
  vec4 m = max(0.6 - vec4(dot(x0,x0), dot(x1,x1), dot(x2,x2), dot(x3,x3)), 0.0);
  m = m * m;
  return 42.0 * dot(m * m, vec4(dot(p0,x0), dot(p1,x1), dot(p2,x2), dot(p3,x3)));
}
```
`step(edge, x)` = `x < edge ? 0 : 1`.

---

## 7. Block encoding

Native library (`caelix.h:211-217`, `shaders/common/caelix_block.hlsl:14-28`, `caelix.h:428-438`):

- **packed (0)**: opaque iff `(v & 0xC000) != 0`. Opaque colour code (bit 15 set): `0x8000 | r5<<10 | g5<<5 | b5`, albedo `rgb5 / 31` (linear, no sRGB decode), smoothness 0, metallic 0, IOR 1.5 -> diffuse only. Emissive (bits 15-14 = 01): `0x4000 | r4<<10 | g4<<6 | b4<<2 | s`, emission = albedo x 8^s. Transparent (bits 15-14 = 00): glass palette id `v & 0xFF`, face bits 8-13; 0 = air.
- **block-ID (1)**: opaque iff bit 15; transparent id `v & 0x1FF`, face bits 9-14; built-in colours are pastel hashes (the host is expected to load a material table with `--materials`).

Caelix-Core (`F:\RP_Games\Caelix-family\Caelix-Core\Runtime\Block.cs:61-97`, `PackedSceneColor.cs:8-13`, `BlockEncoding.cs:1-18`):

- `Block(float r, float g, float b, float emission)` (`Block.cs:89-96`): `rr = clamp((int)floor(r*32), 0, 31)` (same for g, b), `emi = emission > 0`, then `Block(int rr, int gg, int bb, bool emi)`.
- `Block(int r, int g, int b, bool emission)` (`Block.cs:68-80`):
  - packed build (`CAELIX_PACKED_SCENE_COLOR` defined): opaque `0x8000 | r<<10 | g<<5 | b`; emissive `PackedSceneColor.Emissive(r*15/31, g*15/31, b*15/31, 0)`.
  - block-ID build (define absent): `(r<<11) | (g<<6) | (b<<1) | emission`.
- `BlockEncoding.cs:1` has `// #define CAELIX_PACKED_SCENE_COLOR` (commented out) in the committed Caelix-Core (`c41dc07`, file clean), and Titania's `ProjectSettings.asset` does not define it: **the current Unity build is the block-ID build**.

FractalCave values:

| Voxel | `new Block(...)` | packed value | block-ID value |
|---|---|---|---|
| fill | `(n, y/512, 0.5, 0)`: rr = clamp(floor(32n)), gg = min(y>>4, 31), bb = 16 | `0x8000 \| rr<<10 \| gg<<5 \| 16` (always opaque) | `rr<<11 \| gg<<6 \| 32` (opaque only when rr >= 16) |
| toggle | `(0.5, 1.0, 0.8, 0)`: 16, 31 (32 clamped), 25 | `0xC3F9` | `0x87F2` |

For the host: use packed with the packed formulas; every solid voxel is opaque and diffuse with albedo `(rr/31, gg/31, 16/31)`, so `max(albedo) >= 16/31 = 0.516` (relevant for the RR estimate in section 3).

---

## 8. Build state and running

- Library: `cmake --preset vs2022 && cmake --build --preset release` (from `R`). Output `build/bin/Release/caelix_renderer.dll` + `.lib`, dated 2026-10-18 02:00:58; no file under `lib/`, `shaders/` or `include/` is newer, so the DLL is current (HEAD `18a1dfd`; library commit `e2241e9`). `ctest --test-dir build -C Release` runs the 6 GPU tests.
- Host: `cd host-rs && cargo build --release -p caelix-app` (or `cargo run --release -p caelix-app -- ...`). `host-rs/.cargo/config.toml` sets `LIBCLANG_PATH` for bindgen.
- DLL lookup (`host-rs/caelix-sys/build.rs`): links `caelix_renderer.lib` from `CAELIX_LIB_DIR`, default `<repo>/build/bin/Release`; copies `caelix_renderer.dll`/`.pdb` next to the executable (`target/<profile>/`); reruns when `caelix.h`, the import lib or `CAELIX_LIB_DIR` change. The Rust host links the Release DLL even in debug builds unless `CAELIX_LIB_DIR` is set.
- **`host-rs/target/release/caelix-app.exe` is STALE**: built 2026-10-16 10:44:40 (newer than every `host-rs` source, but against `caelix.h` before 2026-10-18 01:57:58), and `target/release/caelix_renderer.dll` next to it is from 2026-10-16 10:27:58. Commit `e2241e9` appended `CaelixSceneStats::upload_bricks_free` (the struct grew by 8 bytes): the header says a host built against an older v2 header must be rebuilt (`caelix.h:63-67`). The old exe works with its old DLL copy, but do not pair it with the new DLL. Rebuilding refreshes both. Other target dirs (`target/mat`, `target/motion`, `target/refactor`) hold older experiments; use `CARGO_TARGET_DIR=target/<name>` when a running app locks the DLL.
- Environment for every run: `DISABLE_VULKAN_OW_OVERLAY_LAYER=1 DISABLE_VULKAN_OW_OBS_CAPTURE=1` (Overwolf implicit layers; `README.md:1115-1118`; on driver 610.88 the hook crashed Unity's present). Optional `CAELIX_SHADER_DIR` replaces embedded SPIR-V. Release builds have no validation layer; debug builds enable `VK_LAYER_KHRONOS_validation` (do not benchmark debug builds).
- Example (existing flags only), 8K citadel, configuration B:
  `DISABLE_VULKAN_OW_OVERLAY_LAYER=1 DISABLE_VULKAN_OW_OBS_CAPTURE=1 target/release/caelix-app.exe --world <cxw> --pose x,y,z,pitch,yaw --fov 60 --size 2560x1440 --mode pt --bounces 2 --no-denoise --no-sun-nee --bounces-transparent 0 --no-checkerboard --no-input --frames 600 --staging-mib 256`

---

## 9. Open risks

1. **Stale executable**: rebuild `caelix-app` before any measurement (section 8).
2. **FIFO vsync** (`swapchain.rs:164`): the GPU idles between frames and may lower its clocks, which inflates per-pass times; earlier docs saw run-to-run drift for this reason. Options: add an IMMEDIATE/MAILBOX present mode to `caelix-vk` for `--bench`, or lock GPU clocks. Report the present mode with the results.
3. **No ray counter**: rays for B are an estimate (section 3) unless the library gains an opt-in counter.
4. **Timing lag and gaps**: values trail by 2 frames, only the newest is exposed, the history is 16 frames; poll every frame and run extra frames at the end. There is no whole-frame GPU time; the sum of spans excludes barriers and present.
5. **Memory totals are partial**: `record_bytes` is page-granular (274 MiB pages); internal targets (88 B/px) and small tables are not reported; no allocator or budget query exists.
6. **The window must exist and be visible**; Windows may clamp `--size` to the screen. Record `frame.width/height`.
7. **Unity encoding parity**: committed Caelix-Core is the block-ID build, while the owner's SampleScene forces Packed (`encodingOverride: 1`). If the Unity FractalCave numbers came from that combination, the renderer saw block-ID values decoded as packed (fill voxels with rr < 8 become glass ids, and all colours differ), so the Unity occupancy/material mix may not match a packed host replica. Confirm which build produced the Unity measurement before claiming parity.
8. **Noise port**: Unity.Mathematics source is not local; the snoise constants above are from memory (6.5).
9. **Empty bricks**: whether Unity keeps all-air bricks resident (and uploads them) is not verified; it changes `bricks_resident`, record memory and the finalize count. The plan uploads every brick of every slab each frame (18432) and never removes.
10. **Finalize count is about 3x the changed bricks** (y+-1 neighbours): compare "changed bricks/frame" (18432) with `finalized_bricks_total` deltas explicitly.
11. **Group alignment**: the "one region = one group, 72 BLAS rebuilds per frame" result holds only for `--group-shift 4` and an entity at the origin; another shift changes the AS work.
12. **Default mode trap**: `--world` without `--mode` gives pt with NRD, 4 bounces, transparent 5 and checkerboard on; always pass every flag of the configuration.
13. **Configuration B still runs the composite** (and allocates all 12 internal targets): report `trace_ms` and `composite_ms` separately. A trace-only lighting render needs a geometry target instead of the colour target, which the app does not create.
