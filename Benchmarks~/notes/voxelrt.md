# VoxelRT harness: implementer notes

Research date 2026-10-18 (Fable, read-only). Repo `D:\RP_CG\VoxelRT`, branch `alt-renderers` (HEAD `c7c29b8`).
All paths below are relative to `D:\RP_CG\VoxelRT\` unless they start with a drive letter.
`file:N` and `file:N-M` are line numbers in the file as it is on disk today.

Purpose: input for adding (a) a headless or scripted `--bench` mode and (b) a `RendererNanoVDB` backend (PNanoVDB HDDA),
and for feeding converted scenes (`.dat` v4). Read section 11 (risks) before you design the benchmark loop.

---

## 1. Build

### 1.1 Toolchain (from `build/CMakeCache.txt`, `build/CMakeFiles/3.31.5/`)

| Item | Value |
| --- | --- |
| Generator | Ninja, `CMAKE_MAKE_PROGRAM=D:/ninja-win/ninja.exe` |
| CMake | 3.31.5 (`C:/Program Files/CMake/bin/cmake.exe`) |
| C++ compiler | `C:/llvm-20.1/llvm/bin/clang++.exe`, clang 20.1.0, GNU front-end variant (not clang-cl) |
| C compiler | `C:/llvm-20.1/llvm/bin/clang.exe` |
| Build type | `CMAKE_BUILD_TYPE=Debug` (`-O0 -g -gcodeview`, `-D_DEBUG`, msvcrtd) |
| Vulkan SDK | `C:/VulkanSDK/1.4.304.1` (found by `find_package(Vulkan)`) |
| Language | C++23 (`src/CMakeLists.txt:4`), compiled as `-std=gnu++23` |
| Common flags | `-Wno-unused -Wsign-conversion -ffast-math` + `-march=...` (`src/CMakeLists.txt:10-21`) |

Source root for CMake is `src/` (not the repo root): `cmake -S ./src -B ./build`. There is no `CMakePresets.json`.
`.vscode/settings.json` points CMake Tools at `D:/RP_CG/VoxelRT/src` and has `RelWithDebInfo` commented out.
`build/` is the only build directory. It is Debug.

### 1.2 `-march`

`src/CMakeLists.txt:14-21`: env `TARGET_CPU=AVX2` -> `-march=x86-64-v3`, `TARGET_CPU=AVX512` -> `-march=x86-64-v4`, otherwise
`-march=native` (prints "Compiling for march=native..."). The current build uses `-march=native` (all 28 entries in
`build/compile_commands.json`). The code does need at least AVX2: `src/LibHavk/SwRast/SIMD.h:15-22` has
`#error "Platform or compiler not supported"` without `__AVX2__`/`__AVX512F__`. `VoxelMap.h`, `TerrainGenerator.cpp`,
`Brush.cpp`, `RendererCPU.cpp`, `RendererESVO.cpp` (`_mm_movemask_epi8`) use AVX2 intrinsics. On the i7-14700KF
`-march=native` gives AVX2 (no AVX-512), so `SIMD_AVX2` paths are active. The binary is not portable to older CPUs.

### 1.3 Dependencies (CPM)

`src/CPM.cmake` + `src/package-lock.cmake` (all deps are declared in the lock file):
glfw 3.4, VulkanMemoryAllocator v3.1.0, glm 1.0.1, stb (master), fastgltf v0.8.0, magic_enum v0.9.6, FastNoise2
v0.10.0-alpha, madmann91/bvh (master), imgui v1.91.4 (built as a static lib in the lock file), Slang **v2024.14.5**
binary zip (`package-lock.cmake:43-47`, imported as `slang::slang` from `bin/slang.dll`, `lib/slang.lib`), zstd 1.5.6 (static).

Source cache: `src/CMakeLists.txt:26-30` sets `CPM_SOURCE_CACHE=${CMAKE_BINARY_DIR}/deps/` as a normal variable when the
environment variable `CPM_SOURCE_CACHE` is not set. A `-DCPM_SOURCE_CACHE=...` on the command line is shadowed by that
normal variable, so to reuse the already downloaded deps in a second build dir set the **environment** variable
`CPM_SOURCE_CACHE=D:/RP_CG/VoxelRT/build/deps`. Otherwise a new build dir downloads everything again from GitHub.

Downloaded deps today: `build/deps/<name>/<hash>/`. Slang: `build/deps/slang/7a5a7294d343a74382313f54e5b4fe72def99bd4/`
(contains `bin/slangc.exe`, `slang.dll`, `slang-glslang.dll`). stb: `build/deps/stb/2e4000b6b0706de128d011917ff8fc1642d976a4/`
(contains `stb_image_write.h`, not used yet).

Targets: `havk` static lib (`src/LibHavk/CMakeLists.txt`), links `slang::slang` and `libzstd` PRIVATE, Vulkan/VMA/glfw/glm/
imgui/magic_enum PUBLIC; public defines `GLM_FORCE_DEPTH_ZERO_TO_ONE`, `GLM_FORCE_XYZW_ONLY`; `${stb_SOURCE_DIR}` is a
PRIVATE include of `havk` only. `VoxelRT` executable (`src/VoxelRT/CMakeLists.txt`) lists every `.cpp` explicitly (a new
`Render/RendererNanoVDB.cpp` must be added to that list). Post-build step copies runtime DLLs (slang.dll,
slang-glslang.dll) next to the exe; `build/VoxelRT/` also holds `slang-core-module.bin`.

### 1.4 Release configure

No preset exists. Plain flags work (README "Building" suggests `RelWithDebInfo`):

```
set CPM_SOURCE_CACHE=D:/RP_CG/VoxelRT/build/deps        (optional, avoids re-download)
set TARGET_CPU=AVX2                                       (optional; default is -march=native)
cmake -S D:/RP_CG/VoxelRT/src -B D:/RP_CG/VoxelRT/build-release -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_C_COMPILER=C:/llvm-20.1/llvm/bin/clang.exe ^
  -DCMAKE_CXX_COMPILER=C:/llvm-20.1/llvm/bin/clang++.exe ^
  -DCMAKE_MAKE_PROGRAM=D:/ninja-win/ninja.exe
cmake --build D:/RP_CG/VoxelRT/build-release
```

Debug vs Release differences that matter for measurements: `NDEBUG` changes FlatDDA's per-frame upload batch
(`RendererFlatDDA.cpp:48-52`: 16 MiB in Release, 0.5 MiB in Debug), `assert`s are active in Debug, and the CPU-side
structure builds (ESVO, Tree64, BVH recursive builders) are very slow at `-O0`. GPU traversal time does not depend on
the C++ build type (shaders are compiled at runtime, see 1.5).

### 1.5 Runtime shader compilation, paths, hot reload

- `Main.cpp:394-399`: `havk::Create({ .Window = window, .ShaderBasePath = "src/VoxelRT/Shaders/", .EnableShaderHotReload = true, .EnableDebugLayers = true })`.
- Every path is relative to the **current working directory**: shaders (`src/VoxelRT/Shaders/`), `assets/bluenoise/...png`,
  `assets/skyboxes/evening_road_01_puresky_4k.hdr` (`Renderer.h:105-107`), `assets/Roboto-Medium.ttf` (`Main.cpp:407`),
  `logs/voxels_2k_sponza.dat`, `logs/voxelrt_settings.dat`, `logs/imgui.ini`. Run the exe with CWD = `D:\RP_CG\VoxelRT`
  (`.vscode/launch.json` uses `cwd: ${workspaceFolder}`).
- Shaders are Slang source compiled at pipeline creation by `PipelineBuilder::Compile` (`src/LibHavk/Havk/Pipeline.cpp:258-462`):
  one Slang session per compile; search path = only `ShaderBasePath` (`Pipeline.cpp:270-271`); target SPIR-V, profile
  `glsl_460`, capability `spirv_1_6`, `SLANG_TARGET_FLAG_GENERATE_SPIRV_DIRECTLY`, `forceGLSLScalarBufferLayout = true`
  (`Pipeline.cpp:278-285`), column-major matrices, preprocessor macros from `ShaderCompileParams::PrepDefs`.
  **Slang optimization is `SLANG_OPTIMIZATION_LEVEL_NONE` and debug info NONE** (`Pipeline.cpp:266-267`). Every entry
  point in the module is compiled; the VkShaderModule entry name is always `"main"` (`Pipeline.cpp:367`).
  Imports resolve relative to the importing file and to the search path (`import XBrickMap.Map` in
  `Shaders/Backends/Render.slang` finds `Shaders/Backends/XBrickMap/Map.slang`; `import Havk.Core` finds `Shaders/Havk/Core.slang`).
- Hot reload: `PipelineBuilder::Refresh()` runs from `DeviceContext::Tick()` (`Context.cpp:461-462`, called once per
  frame at `Main.cpp:453`). A `FileWatcher` on the base path recompiles any pipeline whose main file or any loaded module
  changed, then `MoveHandles` does `vkDeviceWaitIdle` and swaps handles (`Pipeline.cpp:209-252`). For a benchmark run set
  `EnableShaderHotReload = false` (an edit during a run would stall the GPU).
- `EnableDebugLayers = true` enables `VK_LAYER_KHRONOS_validation` when installed (it is: Vulkan SDK present) and a
  verbose debug messenger that prints every message (`Context.cpp:251-264`, `198-217`). Turn it off for timing runs.
- Offline syntax check of a backend without the app is possible with
  `build/deps/slang/7a5a.../bin/slangc.exe` (same Slang version), e.g.
  `slangc src/VoxelRT/Shaders/Backends/Render.slang -I src/VoxelRT/Shaders -DBACKEND_ID=11 -target spirv -profile glsl_460 -entry ComputeMain -stage compute -o out.spv`
  (the app passes no `-I` beyond the base path).

### 1.6 Uncommitted change

`git diff src/VoxelRT/Main.cpp` contains one hunk only (`Main.cpp:387`):

```diff
-    GLFWwindow* window = glfwCreateWindow(1280, 720, "VoxelRT", NULL, NULL);
+    GLFWwindow* window = glfwCreateWindow(2560, 1440, "VoxelRT", NULL, NULL);
```

Other untracked files: `MODEL_BIN_SPEC.md`, `VOXEL_DAT_SPEC.md`, `assets/models/San_Miguel.zip`, `assets/models/San_Miguel/`.

---

## 2. Renderer interface (`src/VoxelRT/Render/Renderer.h`)

### 2.1 `RendererId` (`Renderer.h:15-30`)

```cpp
enum class RendererId {
    CPU,                // 0
    // NOTE: these values are hardcoded in Render.slang due to technical debt, do not change
    XBrickMap = 1, PlainDDA = 2, MultiDDA = 3, ManhattanDF = 4, EuclideanDF = 5,
    OctantDF = 6, ESVO = 7, Tree64 = 8, StdBVH = 9, CWBVH = 10,
    Mesh,               // 11
    Benchmark,          // 12
};
```

`BACKEND_ID` in `Shaders/Backends/Render.slang:12-44` maps 1..10 to a Slang `TVoxelMap` type; any other value hits
`static_assert(false, "Unknown renderer backend ID")`. `Mesh`, `CPU`, `Benchmark` do not use Render.slang.
The settings file stores the selected renderer as the raw enum value (`Main.cpp:100-104` + `SettingStore::Combo`), so
inserting a value before `Mesh` renumbers `Mesh`/`Benchmark` in old settings files. Options: `NanoVDB = 11` and renumber
(old settings point at a different renderer once) or append after `Benchmark` with an explicit value (e.g. `NanoVDB = 13`)
and use the same number as `BACKEND_ID`.

### 2.2 `Renderer` (`Renderer.h:32-83`)

```cpp
struct Renderer {
    Renderer(havk::DeviceContext* ctx, std::shared_ptr<VoxelMap> map);
    virtual void RenderFrame(havx::Camera& cam, GBuffer* target, havk::CommandList& cmds) = 0;
    virtual void DrawSettings(havx::SettingStore& settings) { }
    static std::unique_ptr<Renderer> Create(havk::DeviceContext*, std::shared_ptr<VoxelMap>, RendererId);   // switch, :39-56
    // Emit CPU -> GPU world synchronization commands. *Must* be called at least once before RenderFrame().
    virtual bool SyncMap(havx::Camera& cam, havk::CommandList& cmds) { return false; }                // :60
protected: havk::DeviceContext* _ctx; std::shared_ptr<VoxelMap> _map;
private:   template<RendererId> static std::unique_ptr<Renderer> Create(...);  // one explicit specialization per id, :70-82
};
```

- Registration of a new renderer = 3 edits in `Renderer.h` (enum value, `case` in `Create`, specialization declaration)
  + the specialization definition at the bottom of its `.cpp` (pattern: `RendererFlatDDA.cpp:104-111`).
- `SyncMap` return value: `true` = "did sync work this call, call again"; `false` = "nothing (left) to do".
  `Benchmark.cpp:113-115` loops on it until `false`. Every renderer constructor calls `_map->MarkAllDirty()`, so the
  first `SyncMap` calls upload the whole map.
- `Main.cpp:206-207` calls `SyncMap` then `RenderFrame` every frame, on the same command list (the swapchain frame).

### 2.3 `FramePerfStats` (`Renderer.h:85-99`) and the shader side (`Shaders/Backends/PerfCounters.slang`, `PerfCounters.h.slang`)

```cpp
struct FramePerfStats {
    enum Key { RayCasts, TraversalIters, ClocksPerRay, ReservedStart_ = 10,
               Frame_StartTS /*11*/, Frame_EndTS /*12*/, Sync_StartTS /*13*/, Sync_EndTS /*14*/, Count_ };
    int64_t  Counters[16];
    uint32_t RayCastItersHistogram[32];
};   // 256 bytes; mirrors PerfStats in PerfCounters.slang:9-12
```

- `RayCasts`: incremented by 1 per call of `Map.RayCast` in `Render.slang:75` (per lane, global `InterlockedAdd` on a
  64-bit counter). Counts every primary ray (hit or miss) and every bounce ray actually cast (see 3.2).
- `TraversalIters`: incremented by each backend with its loop count after the traversal (`PERF_STAT_INC(TraversalIters, i)`;
  FlatDDA `Map.slang:61,148`, `ParametricTraversal.slang:49`, Tree64 `Map.slang:115,203,350`, ESVO `Raycast.slang:274`,
  StdBVH `:128`, CWBVH `:218`). One global atomic per ray per backend call.
- `ClocksPerRay`: `PERF_TIME_BEGIN/END` around `RayCast` (`Render.slang:76-78`), using `clockARB()` (subgroup clock).
  Only the **first active lane of each wave** adds its clock delta (`PerfCounters.h.slang:21-22`). It is a per-wave sum,
  not a per-ray sum. `Clocks/iter` in the UI and README = `ClocksPerRay / (TraversalIters + RayCasts)` (`Main.cpp:157`,
  `Benchmark.cpp:17`) mixes a per-wave numerator with a per-lane denominator; treat it as a relative number only.
- Instrumentation level: `PERF_TRACK_LEVEL = PERF_LEVEL_NORMAL` (`PerfCounters.h.slang:8`) -> counters + clocks on;
  `PERF_PRECISE_ITER_HEATMAP = 1` (`:10`) -> `IrradianceTex.w` holds the per-pixel iteration count instead of clocks
  (`Render.slang:124-126`). Setting `PERF_TRACK_LEVEL` to `PERF_LEVEL_OFF` removes all atomics and clock reads.
- `RayCastItersHistogram`: only written at `PERF_LEVEL_HIGH` (FlatDDA MultiDDA `Map.slang:149-151`,
  `ParametricTraversal.slang:50-52`); at the current level it stays zero. Note the index is `min(i, 32)` on a 32-entry
  array: at HIGH level a value 32 writes 4 bytes past the end of the 256-byte buffer (bug).
- `Frame_StartTS`/`Frame_EndTS`/`Sync_*`: GPU timestamps converted to **nanoseconds** (raw * `timestampPeriod`, rounded)
  in `DispatchRenderShader` (`Renderer.h:139-143`, `QueryPool::GetTimestampNanos` in `Havk.h:284-287`).
  Frame time in ms = `(Counters[Frame_EndTS] - Counters[Frame_StartTS]) / 1e6` (`Main.cpp:146`, `Benchmark.cpp:14`).

### 2.4 `GpuRenderer` (`Renderer.h:101-195`)

Constructor (`:102-121`): compiles `Backends/Render.slang` with `BACKEND_ID=<int(type)>`, loads the blue-noise PNG
(RG8_UINT, 128x8192) and converts the 4k HDR panorama to a cube map (`B10G11R11_UFLOAT`, face = width/4 = 1024,
`ImageIO.cpp:62-80`), creates `PerfCounterBuffer` (`TransientBuffer`, 256 B), `TimeQueryPool` (4 timestamp queries),
`_paletteBuffer` (256 x u64, host-visible uniform).

`DispatchRenderShader(cam, target, cmds, map)` (`:135-177`), in command order:
1. `TimeQueryPool->CopyResults(...)` copies the 4 queries into `PerfCounterBuffer->DeviceBuffer` at `Counters[11..14]`
   and resets them (`Resource.cpp:221-228`; no `WAIT_BIT`, unwritten queries leave zeros).
2. `memcpy(&LastFrameStats, PerfCounterBuffer->ReadBack(cmds), 256)` (see lag below), rescale timestamps to ns.
3. `PerfCounterBuffer->Write(cmds, nullptr)` = `vkCmdFillBuffer` 0 (reset counters).
4. `SyncPalette` (host write to the mapped palette buffer, flush if changed).
5. `WriteTimestamp(0, ALL_COMMANDS)` -> barrier on `AlbedoTex` -> `vkCmdDispatch(ceil(W/8), ceil(H/8), 1)` with push
   constants `RenderParams { Map; GBuffer*; MaxBounces; StbnTexture; SkyTexture; Palette*; PerfCounters* }` ->
   `WriteTimestamp(1, ALL_COMMANDS)`.

**What the timed interval contains:** only the Render.slang dispatch (all bounces of the path loop in one kernel, the
G-buffer image writes, the sky cube lookups, the perf atomics). It does not contain `SyncMap` work, the reprojection
pass, the blit/present, or ImGui. Slot 0 is written with `ALL_COMMANDS`, so it is written after all earlier work in
the queue is done; the interval is effectively "dispatch start to dispatch end".

**Lag of `LastFrameStats`:** `TransientBuffer::ReadBack` (`Resource.cpp:261-270`) records a device->host copy into the
staging slot of the current swapchain flight index and returns the host pointer of that slot, which the GPU filled
N frames earlier (N = swapchain image count, at least 3, `Swapchain.cpp:57-61`). The copied data was itself captured
at the start of that frame, before its dispatch. So `LastFrameStats` read during frame K describe the dispatch of
frame **K-N-1** (counters and timestamps are consistent with each other). After any change (renderer, bounces, camera)
discard at least N+1 samples. The first N+1 frames of a new `GpuRenderer` read uninitialized staging memory.

`Sync_StartTS/EndTS` (slots 2, 3) are written only by `Benchmark.cpp:111,117` around `SyncMap`.

`DrawPerfCounters()` is declared (`Renderer.h:123`) but has no definition anywhere.

---

## 3. G-buffer, lighting model, camera

### 3.1 `GBuffer` (`Render/GBuffer.h`)

Settings (`GBuffer.h:42-48`): `DebugChannelView` (None, Albedo, Irradiance, Normals, HeatMap, Variance),
`NumDenoiserPasses = 5`, `NumLightBounces = 1`, `EnableTAA = true`, `HeatmapRange = {0,256}`, `RenderSize`.
UI sliders: bounces 0..5, denoiser passes 0..5, TAA checkbox (`Main.cpp:129-131`), all persisted in the settings file.

`SetCamera(cmds, cam, renderSize, resetHistory)` (`:66-109`): (re)creates images when `renderSize` changes (all with
STORAGE|SAMPLED|TRANSFER_SRC): `AlbedoTex`/`PrevAlbedoTex` RGBA8 (+COLOR_ATTACHMENT), `IrradianceTex`/`Prev`/`Temp`
RGBA16F, `DepthTex`/`Prev` R32F, `MomentsTex`/`Prev` RG16F, `HistoryLenTex` R8_UINT (49 B/pixel, ~181 MB at 2560x1440).
Stores `CurrentPos = cam.ViewPosition` (double), `CurrentProj = cam.GetProjMatrix() * cam.GetViewMatrix(false)`
(view **without translation**), swaps current/previous albedo, depth, moments, increments `FrameNo`, uploads
`GBufferUniforms` with `vkCmdUpdateBuffer`.

`WriteUniforms` (`:111-144`): `InvProjMat = inverse(ProjMat)` rescaled to take pixel coordinates `[0..W]x[0..H]`
(`GetInverseProjScreenMat`, `:211-217`, no half-pixel offset; the shader adds 0.5). `OriginFrac = fract(pos)`,
`WorldOrigin = floor(pos)` (int3). Line 140 has a precedence bug:
`NumSteadyFrames = resetHistory || moveDist || !EnableTAA > 0.001 ? 0 : NumSteadyFrames + 1;` -> any non-zero
`moveDist` or `EnableTAA == false` resets it to 0. With `EnableTAA = false`, `NumSteadyFrames` is always 0, so there is
**no sub-pixel jitter**: primary rays go exactly through pixel centres (`Render.slang:131-140`).

`Resolve(target, cmds)` (`:146-208`): unless the debug channel is HeatMap or Albedo, dispatches `Denoise/Reproject.slang`
(temporal accumulation, see 3.3), then draws a fullscreen triangle with `GBufferBlit.slang` into `target`
(pipeline output format = swapchain format, `GBuffer.h:55-59`). The A-trous filter code is commented out
(`:159-185`); `Denoise/Filter.comp` is an unused GLSL leftover. `NumDenoiserPasses` has only one effect today:
when it is 0, `PrevIrradianceTex` and `IrradianceTex` are swapped at the end of `Resolve` (`:205-207`); when it is
non-zero (default 5) they are never swapped, so the reprojection reads a stale previous-irradiance image.

`RenderSize` is independent of the window: `Main.cpp:106-109` uses `_gbuffer->RenderSize` when non-zero, else the
swapchain size; the "Render Size" combo offers 0.25x..2.0x of the window (`:115-127`). The blit samples the G-buffer
with normalized UVs, so any render size is scaled to the window.

### 3.2 Path loop (`Shaders/Backends/Render.slang:60-129`)

```
irradiance = MaxBounces == 0 ? 1 : 0;  throughput = 1
for bounceNo in 0..MaxBounces (inclusive):
    RayCasts += 1
    hit = Map.RayCast(GBuffer->WorldOrigin, rayPos, rayDir, coarse = (bounceNo != 0))
    if bounceNo == 0:
        miss  -> Albedo = 1, irradiance = sky(dir, mip 0, x1), Depth = -1, break
        MaxBounces == 0 -> irradiance *= face factor (x-normal 0.8, z-normal 0.5, y<0 0.3, y>0 1.0)   // fake shading
        write AlbedoTex (albedo, packed normal, emissive flag), DepthTex (reverse-Z NDC depth)
    else:
        miss  -> irradiance += sky(dir, mip 3, x4) * throughput, break
        throughput *= albedo
    irradiance += throughput * emission
    rayDir = normalize(hit.Normal + GetSphereDir(blueNoise))     // cosine-weighted diffuse
    rayPos = hit.Pos + hit.Normal * 0.01
IrradianceTex = (irradiance, clocks or iteration count)
```

- `NumLightBounces` = `MaxBounces` = number of **diffuse bounces after the primary hit**. 0 = primary only (fake face
  shading, no secondary rays). 1 = primary + 1 bounce. 2 = primary + 2 bounces (protocol mode B2).
- Rays counted in `RayCasts`: all primary rays + every bounce ray that is cast (a bounce is cast only when the previous
  ray hit). P mode: `RayCasts = (8*ceil(W/8)) * (8*ceil(H/8))` (no bounds check in `ComputeMain`; 2560x1440 is a
  multiple of 8, so it equals W*H). B2: W*H + primary hits + first-bounce hits.
- No sun, no NEE, no shadow rays, no Russian roulette, no specular/metal (MetalFuzziness unused here), 1 sample per
  pixel per frame. Sky = cube map from `evening_road_01_puresky_4k.hdr`, linear sampler (`Render.slang:142-150`).
- Primary albedo is not in `irradiance`; the blit multiplies `AlbedoTex.rgb * IrradianceTex.rgb`. Material colour is
  RGB565, squared in the shader as an sRGB approximation (`BackendCommon.slang:19-38`; host encoding
  `VoxelMap.h:30-44`).
- `coarse = true` for bounce rays changes traversal in some backends (approximate hits, lower iteration caps), see 5.2.
- Blue noise: STBN 128x128x64 texture, R2 sequence offsets (`Utilities.slang:24-50`).

### 3.3 Reprojection with `NumDenoiserPasses = 0`, `EnableTAA = false`

`Reproject.slang` still runs (only HeatMap/Albedo channels skip it). With `NumSteadyFrames == 0` it calls `Reproject()`:
bilinear history fetch with normal and plane-distance rejection, blend `1/(historyLen+1)`, history length up to 64
(`Reproject.slang:39-96`). A static camera therefore converges over ~64 frames even with TAA off. Accumulation never
affects the timed dispatch, but a "1 spp" screenshot needs this pass skipped (e.g. a bench flag in `Resolve`, or read
`AlbedoTex`/`IrradianceTex` before `Resolve`).

Blit (`GBufferBlit.slang:21-39`): `color = albedo * irradiance * 0.48`, `aces_approx` (which scales by 0.6
first), `pow(color, 0.45)`, written to a UNORM swapchain image (`B8G8R8A8_UNORM` preferred, `Swapchain.cpp:13-19`).

### 3.4 Camera (`src/LibHavk/Havx/Camera.h`)

```cpp
glm::dvec3 Position;  glm::vec2 Euler;  // (yaw, pitch) in radians
float FieldOfView = 90 (degrees, VERTICAL); AspectRatio; NearZ = 0.01; MoveSpeed;
glm::dvec3 ViewPosition; glm::quat ViewRotation;   // smoothed values, these are what the renderer uses
```

- World: **Y up, right-handed**, 1 voxel = 1 world unit. View space looks down -Z, +X right, +Y up.
- `ViewRotation` target = `glm::eulerAngleXY(-pitch, yaw)` (`Camera.h:54`) = `RotX(-pitch) * RotY(yaw)` (verified in
  `build/deps/glm/.../gtx/euler_angles.inl:107-123`). `GetViewMatrix(false)` = `mat4_cast(ViewRotation)`.
- Exact direction formulas (yaw = `Euler.x`, pitch = `Euler.y`):
  - forward = `( cos(pitch)*sin(yaw),  sin(pitch), -cos(pitch)*cos(yaw) )`
  - right   = `( cos(yaw), 0, sin(yaw) )`
  - up      = `( -sin(pitch)*sin(yaw), cos(pitch), sin(pitch)*cos(yaw) )`
  - yaw 0, pitch 0 looks along -Z; yaw +pi/2 looks along +X; positive pitch looks up.
- Projection (`Camera.h:36-46`): reverse-Z infinite perspective, `f = 1/tan(FOV/2)`, `x_clip = (f/Aspect) x`,
  `y_clip = -f y` (Y flip for Vulkan), `z_clip = NearZ`, `w_clip = -z`. NDC depth = `NearZ / -z_view`.
- Pixel -> ray (pixel (0,0) top-left, `Render.slang:131-140`): `u = 2*(px+0.5)/W - 1`, `v = 2*(py+0.5)/H - 1`,
  `dir_view = normalize( u * tan(FOV/2) * Aspect, -v * tan(FOV/2), -1 )`, `dir_world = right*dx + up*dy - forward*dz`
  with `dir_view = (dx, dy, dz)`, i.e. `dir_world = normalize(right*u*tan*Aspect - up*v*tan + forward)`.
  Ray origin in the shader = `OriginFrac` with integer offset `WorldOrigin` (camera position split to keep precision).
- `Update()` (`Camera.h:48-101`) needs an ImGui frame: reads mouse/keys, then slerps `ViewRotation` and lerps
  `ViewPosition` towards the targets with `blend = 1 - 0.7^(dt*60)`, and **sets `AspectRatio = io.DisplaySize.x / y`
  (window aspect)**. For a fixed render size with another aspect, override `AspectRatio` after `Update()`. For an exact
  pose, set `Position = ViewPosition`, `Euler`, and `ViewRotation = glm::quat(glm::eulerAngleXY(-pitch, yaw))` directly
  (Benchmark.cpp:74-75 sets only Position/ViewPosition/Euler, so the rotation slerps in over the next frames).
- Startup pose: `Main.cpp:69-71` `Position (512,128,512)`, `MoveSpeed 180`, `Euler (1.52, -0.5)`, then overwritten on
  the first frame by the persisted "Pos"/"Rot"/"FOV" settings (`Main.cpp:195-198`, see 9.3).

---

## 4. `Render/Benchmark.cpp`

- It is a `Renderer` (`RendererId::Benchmark`, `Benchmark.cpp:239-242`). Trigger: select "Benchmark" in the Settings
  "Renderer" combo, then press "Run" in the "Benchmark Runner" ImGui window (`:56-60`). GUI only, no CLI.
- Scenes: 6 presets with camera pos/rot and file (`:42-49`): `logs/voxels_2k_sponza.dat`, `voxels_2k_ecohouse.dat`,
  `voxels_4k_bistro.dat`, `voxels_4k_san_miguel.dat`, `voxels_4k_forestlake.dat`, `voxels_4k_highway_i95.dat`.
  Only `logs/voxels_2k_sponza.dat` exists on disk; a missing file throws from `Deserialize` (uncaught -> crash).
- Targets: PlainDDA, MultiDDA, XBrickMap, ESVO, Tree64, StdBVH, ManhattanDF, EuclideanDF (`:37-41`; OctantDF commented,
  CWBVH absent).
- State machine (`:53-155`): `SetupNext` (clear `Sectors`, `Deserialize`, set camera, create renderer) -> `Sync`
  (timestamps 2/3 + `std::chrono` around `SyncMap` until it returns false; sums `CpuSyncMs` and `GpuSyncMs`) ->
  `Profile`: 128+1 frames with `NumLightBounces = 0` then 128 frames with `NumLightBounces = 1` (`:128-142`;
  the first Profile sample uses whatever bounce count the user had) -> next target. Samples are `PerfSample(LastFrameStats)`,
  so they carry the N+1 frame lag of 2.4 (a few samples after each switch are mislabeled; the median hides it).
- Output (`:180-234`): markdown table with columns Method, Mrays/s (median frame time of the P samples:
  `RayCasts / FrameTime`), Mrays/s PT1 (same with 1 bounce, ratio to P), Iters/ray, Clocks/iter, GPU sync, CPU sync.
  Printed to stdout as `---- BENCHMARK RESULTS ----` and shown in a read-only ImGui text box.
- README tables were produced by this runner (identical column layout), at the window-sized render resolution of that
  time (window was 1280x720 in the committed `Main.cpp`), on an integrated GPU (README says so), with the whole frame
  loop (TAA/reprojection on, Slang opt NONE, validation layers on). "GPU sync" is 0.0 ms for ESVO/Tree64/BVH because
  their sync is CPU-only (mapped-memory writes, no GPU commands); their "CPU sync" includes the full CPU build.
  GpuSyncMs is unreliable: it adds `Sync_EndTS - Sync_StartTS` of every frame's (lagged) stats, slots 2/3 are copied
  in the same command buffer where they were written, without wait.

---

## 5. Per-renderer facts

### 5.1 Sync strategy, GPU memory, limits

"Host-visible" below = `VMA_ALLOCATION_CREATE_HOST_ACCESS_SEQUENTIAL_WRITE_BIT` + `VMA_MEMORY_USAGE_AUTO_PREFER_DEVICE`:
VMA picks DEVICE_LOCAL|HOST_VISIBLE (ReBAR/BAR heap) when it fits, else system memory read over PCIe.

| Renderer | `SyncMap` on dirty sectors | GPU allocations (bytes) | Existing size readout | World / format limits |
| --- | --- | --- | --- | --- |
| XBrickMap (`RendererXBrickMap.cpp`) | **Incremental per sector**: for each dirty sector, re-reserve its slot range in a free-list arena (`BrickSlotAllocator`), then re-upload **all bricks of that sector** (`:135` "TODO: support updates at brick level"); GPU compute `XBrickMap/UpdateMap.slang` copies bytes and builds 4³ occupancy masks. Per call at most 32768 sectors or 64 MiB staging (`:65-66`), then returns true (multi-frame). | `StorageBuffer = sizeof(GpuVoxelStorage) + 2 GiB` (header = 1,048,576 sectors x (4+8) + 1/64 x 8 = ~12.1 MiB) + `OccupancyStorage = 256 MiB` (`:51-62`), device-only | UI text "Storage: used/total MB" (`:201-209`): used = header + `SlotAllocator.Arena.NumAllocated * 16` | Window `[0,4096) x [0,2048) x [0,4096)` voxels (`:12`, `XBrickMap/Map.slang:43-49`); sectors outside are skipped. 32-bit byte addresses (2 GiB buffer, `assert` only in Debug, `:105`). Materials: yes (8-bit ids). LOD code present but `GetSectorLOD` returns 0 (`:165-166`). |
| PlainDDA / MultiDDA (`RendererFlatDDA.cpp`) | **Incremental per dirty brick** (occupancy bits only): one 80-byte record per brick, GPU `FlatDDA/UpdateMap.slang` writes 8 x u64 and the brick bit. Batch limit per call 16 MiB (Release) / 0.5 MiB (Debug) of records (`:48-52`), multi-frame. Missing sector -> all 64 bricks cleared. | One buffer `sizeof(GpuMapStorage)` = 4 MiB brick bits + **2 GiB** voxel bits (`:6-13`), device-only, zero-filled on creation | none (fixed size) | Dense grid `[0,4096) x [0,1024) x [0,4096)`. **No bounds check**: `GetLinearIndex` masks coordinates, so bricks outside the grid wrap around and overwrite other bricks. No materials (hit id = 254). `MAX_RAY_STEPS = 512` (`Map.slang:19`): PlainDDA rays stop after 512 voxel steps (miss), MultiDDA after 512 brick steps. |
| ManhattanDF / EuclideanDF (`RendererDistField.cpp`) | **Incremental per 256³ tile**: any dirty sector marks its tile; the whole tile's 4³ occupancy is rebuilt on CPU and its distance field recomputed by 3 axis passes of `DistField/UpdateMap.slang`. At most 16 tiles per call (`:69`), multi-frame. | 3D image 1024x256x1024 R8_UINT (Manhattan, 256 MiB) or R16_UINT (Euclidean, 512 MiB) + occupancy buffer 2^34/8 = **2 GiB** (`:51-67`), plus 2 MiB staging per tile per call | none | Grid `[0,4096) x [0,1024) x [0,4096)` (tiles outside skipped). No materials. Uses `ParametricRayCast` caps (5.2). |
| OctantDF (`RendererOctantDF.cpp`) | **Full rebuild** whenever `DirtyLocs` is non-empty (`:44-45`): enumerates every sector, allocates a new buffer for all occupied 256³ tiles, fills occupancy on CPU, 3 GPU passes per tile. | `sizeof(MapStorage)` (16x4x16 x 8 = 8 KiB) + 4 MiB per occupied tile (8 x 64³ distances + 2 MiB occupancy), **host-visible** (`:61-66`) | UI "Storage: X MB" = buffer size (`:117-123`) | Same grid as DF. Not in the Benchmark target list. |
| ESVO (`RendererESVO.cpp`) | **Full CPU rebuild** (recursive, single thread) on any dirty sector (`:114-135`); new buffer, filled through mapped memory ("TODO: make this copy staged", `:132`). | `data.size() * 4` bytes (one u32 per child descriptor + far pointers), **host-visible** | UI "Storage: X MB" (`:138-144`) | `TreeScale = 12` hard-coded (`:117`) -> cube `[0,4096)³` from the origin; negative coordinates and >= 4096 are not built. No materials. 23-level stack, `MAX_RAYCAST_ITERATIONS 1000` (`ESVO/Raycast.slang:36-37`). Ray origin is mapped into [1,2) floats (`ESVO/Map.slang:16-17`). |
| Tree64 (`RendererTree64.cpp`) | **Full CPU rebuild** (recursive, single thread) on any dirty sector (`:114-138`), mapped-memory write. | `nodes * 12` (packed `{IsLeaf:1, ChildPtr:31, PopMask u64}`) + left-packed leaf voxel bytes, **host-visible** | UI "Storage: X MB" (`:141-147`) | `TreeScale = 14` (`:117`) -> `[0,16384)³` from the origin. `ChildPtr` 31 bits: < 2^31 nodes and < 2 GiB leaf data. Materials: yes. Group-shared stack `gs_stack[64][11]` assumes 64 threads per group (`Tree64/Map.slang:137-139`). |
| StdBVH (`RendererBVH.cpp`) | **Full CPU rebuild** (madmann91/bvh, high quality, single thread; "Parallel build generates broken trees", `:97`), one leaf per non-empty 8³ brick. | `nodes * 16` (`PackedNode`: i16vec3 min/max + u32) + `bricks * 64` (512-bit occupancy), **host-visible** | UI "Storage: X MB" (`:176-182`) | Node bounds are `int16` (`:6-10`) -> all coordinates must be in [-32768, 32767]. No materials. `maxIters = 128` (`StdBVH.slang:73`) for node + brick steps. |
| CWBVH (`RendererBVH.cpp:150-171`, `BVH/CWBVH.slang`) | Same CPU build + BVH8 collapse (`BVH8Converter.cpp`). | `nodes * 80` + `bricks * 64`, host-visible | same UI line | **Not functional**: brick intersection missing (`CWBVH.slang:195` `// FIXME: brick intersect`), `bvh8_trace` returns `HitInfo()` (`:219`); brick order is not permuted by `bvh8_indices`. README lists it as TODO. |
| Mesh (`RendererMesh.cpp`) | Incremental per 62³ chunk (binary greedy mesher), max 32 chunks per call. Not a `GpuRenderer`: no perf stats, no timestamps; it rasterizes into `AlbedoTex` and forces the Albedo debug channel. | Quad storage 32M x 8 B = 256 MiB + 2 KiB palette (host-visible), index buffer, per-frame indirect buffer, D32 depth | UI "Quads: N K" | Chunk coordinates packed in 8 bits per axis (`InstanceOffset`), unsigned. Dirty sector -> chunk uses `sectorPos*32/CS_P` (64) while meshing uses `chunkPos*CS` (62): mismatch, some chunks may not refresh. |
| CPU (`RendererCPU.cpp`) | Incremental per dirty brick into dense host arrays. Not a `GpuRenderer`. | Host RAM: dense 2048x512x2048 bytes (2 GiB) + occupancy (256 MiB) | none | `[0,2048) x [0,512) x [0,2048)` (`:11-14`). |

Common per `GpuRenderer`: 2 KiB palette, 256 B perf buffer + N x 320 B staging, blue noise 2 MiB, sky cube 6 x 1024² x 4 B
(+ mips). G-buffer: 49 B/pixel. No renderer reports its memory programmatically: the size fields are inside
anonymous-namespace classes in each `.cpp`. Options: add `virtual uint64_t GetGpuMemoryBytes()` to `Renderer`, and/or
query the process total with `vmaGetHeapBudgets(ctx->Allocator, ...)` (the allocator is created with
`VMA_ALLOCATOR_CREATE_EXT_MEMORY_BUDGET_BIT`, `Context.cpp:317-324`).

Memory release: replaced resources go to a deletion queue freed in `ctx->Tick()` after the GPU passes their timestamp
(`Context.cpp:451-477`; assert if more than 16 pending queues, so `Tick()` must run every frame).

### 5.2 Traversal caps and the `coarse` flag (affects ray counts and correctness)

`Render.slang:77` passes `coarse = (bounceNo != 0)`.

| Backend | Primary cap | Bounce (`coarse`) behaviour |
| --- | --- | --- |
| XBrickMap, Manhattan/Euclidean/OctantDF (`ParametricTraversal.slang:30,38,46`) | 256 iterations, then miss | 80 iterations; after 20 iterations `GetStepPos(..., coarse)` is called with `coarse = true`; XBrickMap then reports a hit at the first occupied voxel of a partially filled 4³ cell (`XBrickMap/Map.slang:134-138`) |
| Tree64 (`Tree64/Map.slang:57-58,160-161`) | 256 iterations | after 20 iterations it stops at the first leaf (4³ node) = approximate hit |
| PlainDDA / MultiDDA | 512 steps | `coarse` ignored |
| ESVO | 1000 iterations | ignored |
| StdBVH | 128 iterations | ignored |

For a fair B2 comparison with external harnesses, consider forcing `coarse = false` and noting the iteration caps
(large 4k/8k views can exceed 256 iterations and turn hits into misses).

---

## 6. `VoxelMap` (`src/VoxelRT/VoxelMap.h`, `VoxelMap.cpp`) and `.dat` v4

### 6.1 Data model

- `Voxel { uint8_t Data; }` = material id, 0 = empty (`VoxelMap.h:12-24`).
- `Material` (`:25-54`): `alignas(16) { uint8_t Color[3] = {0,0,0}; uint8_t MetalFuzziness = 255; float Emission = 0; }`,
  `sizeof == 16` (bytes 8..15 padding). `GetEncoded()` -> u64: RGB565 in bits 0..15, half-float emission in bits 16..31,
  fuzziness in bits 32..39.
- Indexers (`:63-116`): `LinearIndexer3D<ShiftXZ, ShiftY, Signed>`, index = `x | z << ShiftXZ | y << (2*ShiftXZ)` (XZY order).
  - `WorldSectorIndexer = <12, 8, true>`: sector index `(x & 0xFFF) | (z & 0xFFF) << 12 | (y & 0xFF) << 24`, signed decode.
    Sector range X/Z [-2048, 2047], Y [-128, 127] -> voxel range X/Z [-65536, 65535], **Y [-4096, 4095]**.
  - `MaskIndexer = <2, 2, false>`: brick index in sector `x + z*4 + y*16`, 4³ bricks per sector.
  - `BrickIndexer = <3, 3, false>`: voxel index in brick `x + z*8 + y*64`.
- `Brick { Voxel Data[512]; }` (`:118-199`), 512 bytes. `GetOccupancyMask` -> 8 x u64 in brick-linear XZY order;
  `PackBits64` (AVX2 compare + movemask); `DispatchSIMD` (see 8).
- `Sector` (`:202-219`): `std::vector<Brick> Storage; uint8_t BrickSlots[64];` slot = storage index + 1, 0 = no brick.
  `GetAllocationMask()` = bit i set when slot i != 0. **`GetBrick(i, create=true)` may `emplace_back` and reallocate
  `Storage`, invalidating every `Brick*` previously taken from the same sector.** `DeleteBricks(mask)` rebuilds the sector.
- `VoxelMap` (`:237-319`): `std::unordered_map<uint32_t, Sector> Sectors` keyed by world sector index;
  `std::map<uint32_t, uint64_t> DirtyLocs` (sector index -> dirty brick mask); `Material Palette[256]`.
  A sector may exist with zero bricks (the terrain generator inserts such sectors).
- Coordinates: Y up, 1 voxel = 1 world unit, brick 8³, **sector 32³ voxels** (4³ bricks). Voxel `p` -> brick `p >> 3`,
  sector `p >> 5`. Voxelized glTF keeps the model's Y-up axis, centres X/Z in the box and puts the base at y = 0
  (`Voxelize.cpp:232-243`).

### 6.2 API

- `Get(pos)`: `GetBrick(pos >> 3)` (hash lookup), returns empty when absent (`:249-252`).
- `Set(pos, voxel)` (`:253-259`): `GetBrick(pos >> 3, create=true, markAsDirty=true)` -> `GetSector` bounds check,
  `unordered_map::find` (+insert), **`DirtyLocs[sectorIdx] |= bit` (std::map lookup/insert) on every call**
  (`VoxelMap.cpp:84-110`), brick create if needed, then a byte store. Setting 0 never frees bricks or sectors.
- `MarkAllDirty()` (`:265-269`): `DirtyLocs[idx] = sector.GetAllocationMask()` for every sector (does not clear
  previously dirty bits of removed bricks; it overwrites with the current mask).
- `RayCast(origin, dir, maxIters=1024)` (`VoxelMap.cpp:127-157`): scalar CPU ray cast with sector/brick skipping, for
  picking. Returns `HitResult {Distance (<=0 = miss), Normal, UV, VoxelPos}`.
- `RegionDispatchSIMD(min, max, createEmpty, visitFn, filterFn)` (`:280-318`): see 8.
- `Deserialize(path)` does **not** clear `Sectors` and does **not** mark anything dirty (renderer constructors call
  `MarkAllDirty`). `Benchmark.cpp:77-78` clears `Sectors` first.

### 6.3 `.dat` v4 layout (verified against `VoxelMap.cpp:192-260` and `src/LibHavk/Havx/BinaryIO.cpp:35-92`; `VOXEL_DAT_SPEC.md` is correct)

All little-endian, raw struct dumps.

```
u64  magic   = 0x00000004786F7663            ("cvox" + u32 version 4), VoxelMap.cpp:195
u32  numSectors                              (= Sectors.size(), includes sectors with mask 0)
blob palette:   u32 compressedSize; u8[compressedSize] zstd frame -> exactly 4096 bytes = Material[256] (16 B each)
repeat until numSectors records are parsed:
    u32  packUncompressedSize
    blob pack:  u32 compressedSize; u8[compressedSize] zstd frame -> packUncompressedSize bytes
    pack payload = concatenation of sector records:
        u32 sectorIndex      (WorldSectorIndexer packing above)
        u64 allocMask        (bit i = brick i present, i = x + z*4 + y*16)
        Brick[popcount(allocMask)]   512 B each, ascending bit order, voxel index x + z*8 + y*64
```

- zstd framing (`WriteCompressed`): default level, **checksum flag on**, one frame per blob; the u32 compressed size
  precedes the frame; the uncompressed size is not inside the wrapper (palette: fixed 4096; packs: the u32 before).
- Packs are flushed when the uncompressed staging stream reaches 16 MiB (`MaxPackSize`, `:196`), checked after each
  whole sector, so a pack can exceed 16 MiB by one sector (<= 12 + 32 KiB). A final pack is always written
  (`FlushPack(true)`, `:260`); it can be empty (u32 0 + an empty zstd frame) if the last sector exactly triggered a flush.
  The reader stops after `numSectors` records, so a trailing empty pack is ignored.
- Empty space: a brick whose bit is 0 is absent (all empty). A present brick may still be all zeros (`Set(p, 0)` does not
  delete bricks). A sector record may have `allocMask = 0` (no brick payload).
- Record order is `unordered_map` order (random). The reader indexes by `sectorIndex`; a duplicate index overwrites
  bricks of the earlier record.
- Reader failure modes: wrong magic -> "Incompatible file"; missing file -> "File not found"; short zstd output ->
  "Decompressed stream is too short". No other validation.
- Palette entries 241, 245-248, 254, 255 are overwritten in the `Application` constructor after loading
  (`Main.cpp:46-53`); a later `Deserialize` (Benchmark) restores the file palette. Backends without materials return
  id 254 (`kDefaultOpaqueMaterialId`, `BackendCommon.slang:17`), so their colour = `Palette[254]`.

---

## 7. Shaders: binding model, backend pattern, PNanoVDB

### 7.1 Binding model (LibHavk)

- Only two descriptor sets exist: set 0 = global bindless heap of SAMPLED_IMAGE (binding 0) and STORAGE_IMAGE
  (binding 1), 65536 each (`Internal.h:122`, `Resource.cpp:273-331`), declared in `Shaders/Havk/Core.slang:3-4` as
  `havk__TextureHeap[]`/`havk__RWTextureHeap[]`; set 1 = immutable samplers from `[SamplerDesc(...)]` attributes.
- **There are no buffer descriptors.** All buffers are passed as **buffer device addresses inside one push-constant
  struct** (`[vk::push_constant] DispatchParams pc;`). Any other global parameter (e.g. `StructuredBuffer<uint> buf;`)
  is reported as `warning: unhandled shader parameter` and is never bound (`Pipeline.cpp:375-432`). Only one
  push-constant parameter per pipeline is allowed.
- `DeviceContext::CreateBuffer` adds `SHADER_DEVICE_ADDRESS` automatically to STORAGE/UNIFORM buffers and fills
  `Buffer::DeviceAddress` (`Resource.cpp:15-17,42-45`). `cmds.GetDeviceAddress(buffer, UseBarrier::ComputeRead)` emits
  a barrier and returns the address (`Havk.h:424-429`).
- Images: `ImageHandle<T>` = u32 heap index; `.Instance` gives the texture object (`Core.slang:6-27`).
- Push-constant layout: keep the C++ mirror struct field order and natural alignment identical (existing renderers rely
  on std430-like rules: `{u32; u64; u64}` = 24 bytes with padding works, see Tree64). `RenderParams` (C++, `Renderer.h:149-159`)
  and `DispatchParams` (Slang, `Render.slang:46-56`) start with the backend's map struct; keep the map struct a multiple
  of 8 bytes. Put the C++ mirror structs in an **anonymous namespace** (`RendererXBrickMap.cpp:4-7`: same-named structs
  in two TUs silently break `decltype(map)` and push-constant sizes).

### 7.2 How a GPU backend plugs in (pattern to copy)

1. `Shaders/Backends/<Name>/Map.slang`: `namespace backend::<name> { public struct VoxelMap : IRayCastable { ...fields...;
   public HitInfo RayCast(int3 worldOrigin, float3 rayPos, float3 rayDir, bool coarse); } }`. `IRayCastable` and
   `HitInfo {Dist, Pos, Normal, FaceUV, MaterialId; Miss = MaterialId == 0}` are in `BackendCommon.slang:1-15`.
   `rayPos` is relative to `worldOrigin` (camera floor); return `hit.Pos` in the same relative frame; `Normal` is an
   axis unit vector (packed into 2 bits per axis by `PackAlbedo`); `MaterialId` 0 = miss.
   Perf: `#include "../PerfCounters.h.slang"` and `PERF_STAT_INC(TraversalIters, n)`.
2. `Render.slang`: add `#elif BACKEND_ID == <id>  import <Name>.Map;  typealias TVoxelMap = backend::<name>::VoxelMap;`.
3. `Render/Renderer<Name>.cpp`: `struct Renderer<Name> : GpuRenderer` with ctor `GpuRenderer(ctx, map, RendererId::<Name>)`
   + `_map->MarkAllDirty()`; `RenderFrame` -> `GpuRenderer::DispatchRenderShader(cam, target, cmds, GpuVoxelMap{...})`;
   `SyncMap` builds/uploads; optional `DrawSettings` with a size line. Specialize `Renderer::Create<RendererId::<Name>>`.
4. `Renderer.h`: enum, switch case, specialization declaration. `src/VoxelRT/CMakeLists.txt`: add the `.cpp`.
5. Optional update compute shader: `ctx->PipeBuilder->CreateCompute("Backends/<Name>/UpdateMap.slang")`, dispatched with
   a push-constant struct of device addresses (FlatDDA `UpdateMap.slang`).

### 7.3 Simplest existing backend: PlainDDA (quoted)

`Shaders/Backends/FlatDDA/Map.slang:5-74` (abridged only in comments):

```hlsl
import "../BackendCommon";
#include "../PerfCounters.h.slang"

namespace backend::flat_dda {

public static const int GRID_SIZE_XZ = 4096, GRID_SIZE_Y = 1024, BRICK_SIZE = 8,
                        BRICK_STRIDE = BRICK_SIZE*BRICK_SIZE*BRICK_SIZE;
public static const uint64_t NUM_VOXELS = uint64_t(GRID_SIZE_XZ) * GRID_SIZE_Y * GRID_SIZE_XZ,
                             NUM_BRICKS = NUM_VOXELS / BRICK_STRIDE;
static const int MAX_RAY_STEPS = 512;

public struct MapStorage {
    public uint64_t BrickMasks[NUM_BRICKS / 64];
    public uint64_t VoxelMasks[NUM_VOXELS / 64];
}

public struct VoxelMap : IRayCastable {
    public MapStorage* Storage;                      // device address from push constants

    public HitInfo RayCast(int3 worldOrigin, float3 rayPos, float3 rayDir, bool coarse) {
        HitInfo hit;
        hit.MaterialId = 0;
        rayPos += worldOrigin;
        float3 invDir = 1.0 / rayDir;
        float3 startPos = floor(ClipRayToAABB(rayPos, rayDir, 0.001, float3(GRID_SIZE_XZ, GRID_SIZE_Y, GRID_SIZE_XZ) + 0.001));
        float3 sideDist = ((startPos - rayPos) + step(0.0, rayDir)) * invDir;
        int3 mapPos = int3(startPos);
        int3 raySign = sign(rayDir);
        bool3 stepMask = GetStepMask(sideDist);
        int i;
        for (i = 0; i < MAX_RAY_STEPS; i++) {
            if (!CheckBounds(mapPos, GRID_SIZE_XZ, GRID_SIZE_Y)) break;
            uint brickIdx = GetBrickIndex(mapPos / BRICK_SIZE);
            uint voxelIdx = GetLinearIndex(mapPos, BRICK_SIZE, BRICK_SIZE);
            uint maskIdx = brickIdx * (BRICK_STRIDE / 32) + (voxelIdx / 32);
            uint occMask = ((uint*)&Storage->VoxelMasks[0])[maskIdx];      // 32-bit loads through a pointer
            if ((occMask >> (voxelIdx & 31) & 1) != 0) { hit.MaterialId = kDefaultOpaqueMaterialId; break; }
            stepMask = GetStepMask(sideDist);
            mapPos += select(stepMask, raySign, 0);
            sideDist += select(stepMask, abs(invDir), 0);
        }
        PERF_STAT_INC(TraversalIters, i);
        if (hit.MaterialId != 0) {
            float3 mini = ((mapPos-rayPos) + 0.5 - 0.5*float3(raySign)) * invDir;
            float d = max(mini.x, max(mini.y, mini.z));
            hit.Dist = d;
            hit.Pos = rayPos + rayDir * d - worldOrigin;           // back to camera-relative frame
            hit.FaceUV = fract(select(stepMask.xz, hit.Pos.yy, hit.Pos.xz));
            hit.Normal = select(stepMask, -sign(rayDir), 0.0);
        }
        return hit;
    }
    public static uint GetBrickIndex(uint3 brickPos) { return GetLinearIndex(brickPos, GRID_SIZE_XZ / BRICK_SIZE, GRID_SIZE_Y / BRICK_SIZE); }
}
```

C++ side, `Render/RendererFlatDDA.cpp:3-46` (structure):

```cpp
namespace {
struct GpuMapStorage { /* mirrors MapStorage */ };
struct GpuVoxelMap { VkDeviceAddress Storage; };       // mirrors backend::flat_dda::VoxelMap

struct RendererFlatDDA : public GpuRenderer {
    havk::BufferPtr StorageBuffer;
    havk::ComputePipelinePtr UpdateShader;
    RendererFlatDDA(havk::DeviceContext* ctx, std::shared_ptr<VoxelMap> map, RendererId type) : GpuRenderer(ctx, map, type) {
        UpdateShader = ctx->PipeBuilder->CreateCompute("Backends/FlatDDA/UpdateMap.slang");
        _map->MarkAllDirty();
    }
    void RenderFrame(havx::Camera& cam, GBuffer* target, havk::CommandList& cmds) override {
        GpuRenderer::DispatchRenderShader(cam, target, cmds, GpuVoxelMap {
            .Storage = cmds.GetDeviceAddress(*StorageBuffer, havk::UseBarrier::ComputeRead),
        });
    }
    bool SyncMap(havx::Camera& cam, havk::CommandList& cmds) override {
        if (StorageBuffer == nullptr) {
            StorageBuffer = _ctx->CreateBuffer({ .Size = sizeof(GpuMapStorage),
                .Usage = VK_BUFFER_USAGE_STORAGE_BUFFER_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT,
                .AllocType = VMA_MEMORY_USAGE_AUTO_PREFER_DEVICE });
            cmds.Fill(*StorageBuffer, 0);
        }
        /* drain _map->DirtyLocs into a host staging buffer (HOST_ACCESS_SEQUENTIAL_WRITE), dispatch UpdateMap */
    }
};
}
template<> std::unique_ptr<Renderer> Renderer::Create<RendererId::PlainDDA>(havk::DeviceContext* ctx, std::shared_ptr<VoxelMap> map) {
    return std::make_unique<RendererFlatDDA>(ctx, map, RendererId::PlainDDA);
}
```

ESVO is the smallest full-rebuild example: `ESVO/Map.slang` (36 lines) wraps `CastRay` and maps `worldOrigin + rayPos`
into the tree's [1,2) cube; `RendererESVO.cpp:97-136` builds a `std::vector<uint32_t>` and writes it to a mapped buffer.

### 7.4 PNanoVDB in Slang (local copy: `F:\RP_Games\Caelix-family\Artifacts\caelix-benchmarks\third_party\nanovdb\nanovdb\PNanoVDB.h`, 3604 lines)

- Slang has a C preprocessor; `#include "file"` of a plain header works (`Render.slang:6`, `GBufferBlit.slang:1`
  `#include <Havk/FullscreenPass.slang>`). Put `PNanoVDB.h` under `src/VoxelRT/Shaders/` (for example
  `Shaders/Backends/NanoVDB/PNanoVDB.h`) and include it from `Backends/NanoVDB/Map.slang` with a relative path. Macros
  stay inside that module; only `public` declarations cross `import`.
- `PNANOVDB_HLSL` mode with its default buffer (`PNANOVDB_BUF_HLSL` -> `StructuredBuffer<uint>`, `PNanoVDB.h:200-211`)
  **cannot be used as is**: LibHavk does not bind storage-buffer descriptors (7.1). Use the custom buffer hook
  (`PNanoVDB.h:66-67`: `#if defined(PNANOVDB_BUF_CUSTOM) // NOP`). With `PNANOVDB_BUF_CUSTOM` you must define, before the
  include, everything the HLSL branch would have defined (`PNanoVDB.h:200-258`):

  ```hlsl
  #define PNANOVDB_HLSL
  #define PNANOVDB_BUF_CUSTOM
  #define pnanovdb_buf_t uint*                                  // device address of the grid buffer
  uint  pnanovdb_buf_read_uint32(pnanovdb_buf_t buf, uint byte_offset) { return buf[byte_offset >> 2u]; }
  uint2 pnanovdb_buf_read_uint64(pnanovdb_buf_t buf, uint byte_offset) { uint i = byte_offset >> 2u; return uint2(buf[i], buf[i + 1u]); }
  void  pnanovdb_buf_write_uint32(pnanovdb_buf_t buf, uint byte_offset, uint value) {}
  void  pnanovdb_buf_write_uint64(pnanovdb_buf_t buf, uint byte_offset, uint2 value) {}
  #define pnanovdb_grid_type_t uint
  #define PNANOVDB_GRID_TYPE_GET(grid_typeIn, nameIn) pnanovdb_grid_type_constants[grid_typeIn].nameIn
  #include "PNanoVDB.h"
  ```

  HLSL mode defaults to `PNANOVDB_ADDRESS_32` (`PNanoVDB.h:31-34`): 32-bit byte offsets, 64-bit values as `uint2`,
  only 32-bit loads. Grid buffer must be < 4 GiB. HLSL mode also turns on `PNANOVDB_HDDA` (`:51-53`).
- Device features in place (8.10 below): `bufferDeviceAddress`, `shaderInt64`, `variablePointers`, scalar block layout.
  **`shaderFloat64` is not enabled.** PNanoVDB's HLSL branch defines `double`-returning helpers
  (`pnanovdb_uint64_as_double` with `asdouble`, `PNanoVDB.h:469-470`; `pnanovdb_read_double`, the grid `map` doubles).
  Slang emits only functions reachable from the entry point, so this is fine as long as the traversal code uses only
  float/int accessors (index-space rays, `pnanovdb_hdda_*`, `pnanovdb_readaccessor_*`); verify with `slangc` first.
  If a double path is pulled in, either enable `shaderFloat64` in `Context.cpp:265-270` (RTX 5080 supports it) or avoid it.
- `PNANOVDB_GRID_TYPE_GET` indexes a `static const` array of structs (`pnanovdb_grid_type_constants`). With Slang
  optimization NONE, a lookup with a non-literal grid type may stay a runtime array access; pass the grid type as a
  literal constant (e.g. `PNANOVDB_GRID_TYPE_FLOAT`/`_UINT32`/`_ONINDEX`) in the hot path.
- Push constants for the map: e.g. `struct VoxelMap { uint* Grid; uint GridType; uint _pad; }` (16 B) mirrored by
  `struct GpuVoxelMap { VkDeviceAddress Grid; uint32_t GridType; uint32_t _pad; }`.
- Upload: no generic staged-upload helper exists. Do not copy the ESVO/Tree64 pattern (mapped host-visible buffer)
  for traversal data; create a device-only STORAGE|TRANSFER_DST buffer + a host staging buffer
  (`HOST_ACCESS_SEQUENTIAL_WRITE`, `TRANSFER_SRC`) and `cmds.CopyBuffer(staging, device)` inside `SyncMap`.
- Coordinates: build the NanoVDB grid in VoxelRT voxel coordinates (index space = world voxel, Y up) so that
  `worldOrigin + rayPos` is directly the index-space ray origin; return `hit.Pos` minus `worldOrigin`.

---

## 8. Programmatic edits (TerrainGenerator, Brush, bulk APIs)

- `TerrainGenerator` (`TerrainGenerator.h`, `.cpp`): worker threads (3/4 of hardware threads) generate whole `Sector`
  objects from FastNoise2 + a tree SDF (`GenerateSector`, `.cpp:47-75`); `Poll()` returns finished `(sectorPos, unique_ptr<Sector>)`.
  `Main.cpp:82-89` swaps them in: `DirtyLocs[idx] = sector->GetAllocationMask(); Sectors[idx] = std::move(*sector);`.
  Note: bricks that existed before but are absent in the new sector are not marked dirty, so incremental
  per-brick renderers (FlatDDA) keep stale bricks; use `DirtyLocs[idx] |= oldMask | newMask` when you replace sectors.
- **The app always generates terrain at startup**: `Main.cpp:55-63` requests sectors x 0..23, y 0..6, z 0..23 (+ (1,3,1))
  = voxel box `[0,768) x [0,224) x [0,768)`, and every result **replaces** the loaded scene's sectors there (empty
  results insert empty sectors). Disable this in bench mode.
- `Brick::DispatchSIMD(fn, basePos)` (`VoxelMap.h:133-163`): iterates the 512 voxels in 8-wide (AVX2) groups; `fn(VInt3 pos,
  VInt& ids)` edits ids in place; returns `{Changed, Empty}`; stores only when something changed.
- `VoxelMap::RegionDispatchSIMD(regionMin, regionMax, createEmpty, visitFn, filterFn)` (`VoxelMap.h:280-318`): loops the
  sectors of a voxel box, creates sectors/bricks when `createEmpty`, calls `DispatchSIMD` per brick selected by
  `filterFn` (default: allocated bricks), deletes empty bricks/sectors, and ORs changed bricks into `DirtyLocs`.
  (`sectorMin` is clamped with `MinPos` in voxel units; harmless but inconsistent.) `Brush.cpp:12-65` is the worked
  example (capsule SDF, prints ms and voxels/s).
- `Brush`: `BrushSession::Dispatch` (Fill/Replace with probability), `UpdatePosFromRay` uses `VoxelMap::RayCast`.
- Cheapest bulk path for "toggle N bricks per frame" (FractalCave): per brick, `Sector* s = GetSector(sectorPos, true)`
  once, `Brick* b = s->GetBrick(i, true)`, fill `b->Data` (memcpy or SIMD), and set `DirtyLocs[idx] |= bits` once per
  sector; delete emptied bricks with `Sector::DeleteBricks(mask)` (and mark them dirty). Avoid `VoxelMap::Set` per voxel
  (two map lookups per call). Remember the `Storage` reallocation rule (6.1).
- `GenerateLOD.cpp`: only level 0 works (copy); other levels assert.

---

## 9. Window, render size, bench hook, readback

### 9.1 Frame loop (`Main.cpp:376-464`)

`glfwCreateWindow(2560, 1440)` -> `havk::Create` -> ImGui init -> `Application app` -> loop:
`Swapchain->AcquireImage()` (waits the fence of flight slot `_currSyncIdx`, begins its command buffer,
`Swapchain.cpp:152-177`) -> `glfwPollEvents` -> ImGui NewFrame -> `app.Render(image, cmdList)` -> ImGui draw on the
swapchain image (F1 toggles) -> `Swapchain->Present()` (submit + present) -> `ctx->Tick()`.
Present mode: IMMEDIATE when available (`Swapchain.cpp:21-35`), so no vsync cap. Frames in flight = swapchain image count.

`Application::Render` (`Main.cpp:76-210`): `ImGui::ShowMetricsWindow()`, `_cam.Update()`, brush UI, terrain poll,
Settings window (renderer combo creates the renderer on first frame), stats readout from `LastFrameStats`
(`:141-179`), camera UI, `_gbuffer->SetCamera(cmds, _cam, renderSize, DirtyLocs.size() > 0)` (`:201`),
F9 = `MarkAllDirty`, `_renderer->SyncMap` (`:206`), `_renderer->RenderFrame` (`:207`), `_gbuffer->Resolve(target)` (`:209`).

### 9.2 Offscreen / headless

- `havk::Create` accepts `Window = nullptr` (no surface, no swapchain extension, queue only needs COMPUTE,
  `Context.cpp:29-52,299-315`). But three things need the swapchain:
  `GBuffer` constructor uses `ctx->Swapchain->SurfaceFormat.format` for the present pipeline (`GBuffer.h:55-59`);
  `CreateTransientBuffer` sizes staging by `Swapchain->GetImageCount()` (`Resource.cpp:238-243`); `TransientBuffer::Write/ReadBack`
  use `Swapchain->GetFlightId()` (`Resource.cpp:251,262`). Every `GpuRenderer` uses a `TransientBuffer`. True headless
  needs LibHavk changes plus a frame loop with `ctx->Submit(cb)` + `Future::Wait` (`Context.cpp:401-430`).
- Lowest-risk option: keep the window and swapchain, decouple the render size: pass a fixed `glm::uvec2(2560,1440)` to
  `SetCamera` (`Main.cpp:106-109` logic) and set `cam.AspectRatio = 2560/1440.f` after `cam.Update()`. The window can be
  small; the blit scales. A hidden window (`glfwWindowHint(GLFW_VISIBLE, GLFW_FALSE)`) is possible but untested with
  this swapchain code. The loop skips frames while the framebuffer size is 0 (minimized, `Main.cpp:421-424`).
- With a 2560x1440 window on a 2560x1440 monitor the client area plus title bar does not fit; Windows may clamp or
  resize it, which changes the swapchain extent and the camera aspect. Another reason to fix `RenderSize` and aspect.
- ImGui must still run (Camera::Update, SettingStore, Benchmark UI call ImGui every frame).

### 9.3 Where `--bench` hooks in

- Parse `argc/argv` in `main` (`Main.cpp:376`); pass options to `Application`.
- In the `Application` constructor (`:28-74`) for bench mode: skip `_settings.Load(...)` (otherwise the persisted
  Renderer/Bounces/TAA/Pos/Rot/FOV values are applied on the first frame (`SettingStore::Sync`, `SettingStore.h:76-95`,
  `SettingStore.cpp:58`) and changes are auto-saved every 5 s to `logs/voxelrt_settings.dat`); skip the terrain requests;
  skip the default Sponza load / palette overrides if a scene path is given.
- In `Application::Render` (`:76-210`), before `SetCamera`: run a state machine like `Benchmark.cpp` (load scene ->
  `Renderer::Create` -> call `SyncMap` until false (time it) -> warm-up frames -> measured frames reading
  `LastFrameStats`, discarding the first N+1 after each change -> write CSV/JSON -> next case), then
  `glfwSetWindowShouldClose(window, 1)`. Set `NumLightBounces` (0 = P, 2 = B2), `EnableTAA = false`,
  `NumDenoiserPasses = 0`, fixed pose (set `ViewRotation` too), FOV 60.
- Create with `EnableDebugLayers = false`, `EnableShaderHotReload = false` in bench mode.

### 9.4 Readback / screenshot

- No image readback utility exists in LibHavk (only uploads: `Image::Upload`, `Image::LoadFile`). Reusable pieces:
  `TransientBuffer::ReadBack` (buffers only), `Buffer::Invalidate`, `cmds.Barrier(image, UseBarrier, layout)`.
- Swapchain images are created with `COLOR_ATTACHMENT | TRANSFER_DST` only (`Swapchain.cpp:71`): they cannot be copied
  from. Options: (a) add `VK_IMAGE_USAGE_TRANSFER_SRC_BIT` there; (b) call `Resolve` into an offscreen image of the
  swapchain format created with `COLOR_ATTACHMENT | TRANSFER_SRC` (the present pipeline is compiled for the swapchain
  format); (c) copy `AlbedoTex` (RGBA8) and `IrradianceTex` (RGBA16F) directly (they have TRANSFER_SRC) and compose on CPU.
- Recipe: host buffer `CreateBuffer({ .Size = W*H*4, .Usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT, .AllocFlags =
  VMA_ALLOCATION_CREATE_HOST_ACCESS_RANDOM_BIT, .AllocType = VMA_MEMORY_USAGE_AUTO_PREFER_HOST })`;
  `cmds.Barrier(img, { VK_ACCESS_TRANSFER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT }, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL)`;
  `vkCmdCopyImageToBuffer(cmds.Buffer, ...)`; after `Present()`, `ctx->WaitDeviceIdle()`, `buf->Invalidate()`, read
  `MappedData` (swap B and R for BGRA). The ImGui overlay is drawn on the swapchain image after `Resolve`, so read
  before it or hide the UI.
- PNG writing: `stb_image_write.h` is in `build/deps/stb/<hash>/` but only `havk` has the stb include dir (PRIVATE).
  Add `target_include_directories(VoxelRT PRIVATE ${stb_SOURCE_DIR})` and define `STB_IMAGE_WRITE_IMPLEMENTATION` in one TU.

---

## 10. LibHavk device setup (`src/LibHavk/Havk/Context.cpp`)

Instance: API 1.3, GLFW extensions, `VK_EXT_debug_utils` + validation layer when `EnableDebugLayers` (`:261-297`).
Physical device: first with the required extensions, a COMPUTE (+GRAPHICS and present when a surface exists) queue;
discrete GPU preferred (`:77-118`). One queue only (`MainQueue`), timeline semaphore `QueueSemaphore`.

Device extensions (`:302-306`): `VK_KHR_swapchain` (with window), `VK_KHR_variable_pointers`, `VK_EXT_memory_budget`,
`VK_KHR_shader_clock`. Nothing else (no ray tracing / ray query, no mesh shaders, no descriptor buffer).

Features enabled:

| Struct | Features |
| --- | --- |
| `VkPhysicalDeviceFeatures` (`:265-270`) | samplerAnisotropy, fragmentStoresAndAtomics, shaderInt16, shaderInt64, multiDrawIndirect, geometryShader |
| `VkPhysicalDeviceVulkan11Features` (`:165-173`) | uniformAndStorageBuffer16BitAccess, storagePushConstant16, variablePointersStorageBuffer, variablePointers |
| `VkPhysicalDeviceVulkan12Features` (`:140-164`) | uniformAndStorageBuffer8BitAccess, storagePushConstant8, **shaderBufferInt64Atomics**, shaderFloat16, shaderInt8, descriptorIndexing, shaderSampled/StorageImageArrayNonUniformIndexing, descriptorBinding{Sampled,Storage}ImageUpdateAfterBind, UpdateUnusedWhilePending, PartiallyBound, VariableDescriptorCount, runtimeDescriptorArray, **scalarBlockLayout**, hostQueryReset, timelineSemaphore, **bufferDeviceAddress** |
| `VkPhysicalDeviceVulkan13Features` (`:133-139`) | synchronization2, dynamicRendering |
| `VkPhysicalDeviceShaderClockFeaturesKHR` (`:128-131`) | **shaderSubgroupClock** only (`clockARB()`); shaderDeviceClock not enabled |

Not enabled: `shaderFloat64`, `shaderSharedInt64Atomics`, `storageBuffer16BitAccess`/`storageBuffer8BitAccess` as separate
bits (only the "uniformAndStorage" variants), any storage-buffer descriptor binding feature (not needed: BDA only).
PNanoVDB with 32-bit loads through a `uint*` needs only `bufferDeviceAddress` (+ `shaderInt64` for the pointer) — present.
VMA allocator: `BUFFER_DEVICE_ADDRESS_BIT | EXT_MEMORY_BUDGET_BIT` (`:317-324`).

---

## 11. Open risks

1. **World size vs 8K scenes.** Several backends cannot hold an 8192-wide scene: PlainDDA/MultiDDA and the DF family
   are 4096x1024x4096 (FlatDDA even aliases out-of-range bricks), XBrickMap 4096x2048x4096, ESVO 4096³ from the origin,
   CPU 2048x512x2048. Tree64 (16384³ from origin), StdBVH (|coord| <= 32767) and a NanoVDB grid can. `VoxelMap` itself
   allows Y only in [-4096, 4095]. Converted scenes must sit at non-negative coordinates for ESVO/Tree64/FlatDDA/DF/XBrickMap.
2. **Iteration caps turn hits into misses** on large views (Parametric 256/80, Tree64 256, PlainDDA 512 steps,
   StdBVH 128, ESVO 1000), and `coarse = true` makes bounce hits approximate in XBrickMap/Tree64/DF. Ray counts and
   images then differ from external harnesses. Decide whether to force `coarse = false` and raise caps for the study.
3. **Instrumentation cost**: at `PERF_LEVEL_NORMAL` every ray does 2 global 64-bit `InterlockedAdd`s on the same
   addresses plus clock reads. This inflates GPU time unevenly. Consider separate runs: timing with `PERF_LEVEL_OFF`,
   counting with `NORMAL` (the protocol needs both Mrays/s and iterations/ray).
4. **Slang optimization NONE** (`Pipeline.cpp:267`) for all shaders; the driver still optimizes SPIR-V, but numbers
   are not comparable to optimized HLSL/DXC builds. Changing it changes all VoxelRT numbers (README comparability).
5. **Validation layers on** by default (CPU overhead, log spam, possible GPU-assisted checks); turn off for timing.
6. **Stats lag N+1 frames**; first N+1 frames after a renderer is created read uninitialized memory. Also
   `vkCmdCopyQueryPoolResults` has no WAIT bit: filter samples with `EndTS <= StartTS`.
7. **Host-visible structure buffers** (ESVO, Tree64, StdBVH, CWBVH, OctantDF, Mesh): if the allocation does not fit a
   DEVICE_LOCAL|HOST_VISIBLE heap (ReBAR off = 256 MiB BAR), VMA places it in system memory and traversal reads go over
   PCIe. Check Resizable BAR on the RTX 5080 machine, or stage these uploads, before comparing them.
8. **Startup contamination**: terrain generator overwrites `[0,768)x[0,224)x[0,768)`; persisted settings override
   renderer/bounces/TAA/pose/FOV and are auto-saved; palette entries 241/245-248/254/255 are overwritten after load.
9. **Reprojection runs with TAA off**, and `NumDenoiserPasses > 0` leaves `PrevIrradianceTex` stale (`GBuffer.h:205-207`).
   Screenshots for parity need the reprojection skipped. `EnableTAA = false` is required for pixel-centre rays (jitter
   otherwise, via the `NumSteadyFrames` precedence bug at `GBuffer.h:140`).
10. **Headless needs LibHavk changes** (GBuffer present format, TransientBuffer). Keeping a window is simpler; the
    2560x1440 window from the uncommitted diff may not fit the screen, and `Camera::Update` sets the aspect from the window.
11. **No readback/screenshot code**; swapchain images lack TRANSFER_SRC.
12. **PNanoVDB integration**: no storage-buffer descriptors (must use `PNANOVDB_BUF_CUSTOM` + BDA pointer and also define
    `pnanovdb_grid_type_t` / `PNANOVDB_GRID_TYPE_GET`); `double` helpers need `shaderFloat64` if reached; 32-bit
    addressing (< 4 GiB); Slang HLSL compatibility of the 3600-line header is untested here (compile with `slangc` first).
13. **Push-constant mirror structs**: field order/alignment must match; use an anonymous namespace (silent ODR breakage
    otherwise). `RendererId` values are hard-coded in `Render.slang` and persisted by value in settings.
14. **Sync measurement**: CPU sync of full-rebuild renderers runs single-threaded recursive builders (`-O0` in the
    current Debug build); `GpuSyncMs` in Benchmark.cpp is not reliable. For the dynamic FractalCave test (18432 bricks per
    frame) ESVO/Tree64/BVH/OctantDF rebuild the whole structure every frame; XBrickMap re-uploads whole sectors;
    FlatDDA has a per-frame batch cap (multi-frame catch-up, which hides latency in a per-frame number).
15. **Edit API traps**: `Sector::GetBrick(i, true)` can reallocate and invalidate other `Brick*` of the sector; replacing
    a sector must mark old and new masks dirty; `Set(p, 0)` never frees bricks (empty allocated bricks stay in all
    structures, e.g. BVH leaves with empty boxes).
16. **CWBVH is non-functional**, `GenerateLOD` levels > 0 assert, `RayCastItersHistogram` overflows at PERF level HIGH,
    Mesh chunk indexing mismatch (`sectorPos*32/64` vs `chunkPos*62`).
17. **`Clocks/iter`** is a per-wave (first lane) clock sum divided by per-lane counts; do not compare it across GPUs or
    wave sizes as an absolute number.
18. **Rebuild of the Release tree** downloads all CPM deps (GitHub, Slang zip) unless the env var `CPM_SOURCE_CACHE`
    points at `build/deps`; the result is `-march=native` unless `TARGET_CPU` is set.
