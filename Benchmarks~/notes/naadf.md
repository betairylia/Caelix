# NAADF research notes for the benchmark harness (2026-10-18)

Scope: everything an implementer needs to add a benchmark mode to NAADF and run bistro-8192,
rootfall and fractalcave under the protocol in `protocol.md` / `results-schema.md`.
All findings come from reading the code. Nothing was built or run.

Path convention: `N/` = `F:\RP_Games\Caelix-family\Research\NAADF\NAADF\` (the project folder).
Shader paths are under `N/Content/shaders/`. Line numbers are for upstream HEAD `3ea3bb6`
(2026-05-18), the commit checked out in `Research/NAADF`.

Terms used by the code (they differ from the task brief):

| Code term | Size | Notes |
| --- | --- | --- |
| voxel | 1 | 16 bit |
| block | 4^3 voxels | 32 bit entry |
| chunk | 4^3 blocks = **16^3 voxels** | not 64^3 |
| group / "queue group" | 4^3 chunks = 64^3 voxels | unit of the chunk-AADF work queues |
| world-gen segment | `worldGenSegmentSizeInGroups` groups per axis (default 4 = 256 voxels) | unit of world size and of generation |

---

## 1. Build feasibility

### 1.1 Project and packages

- `N/NAADF.csproj`: `net8.0-windows`, `RollForward Major`, `AllowUnsafeBlocks`, `UseWindowsForms`.
  Packages: `Accord.MachineLearning 3.8.2-alpha`, `ImGui.NET 1.91.6.1`,
  `MonoGame.Content.Builder.Task.Compute 3.8.3`, `MonoGame.Framework.Compute.WindowsDX.NoMemoryLeak 3.8.1.2`.
- Custom target `RestoreDotnetTools` (BeforeTargets `CollectPackageReferences`) runs `dotnet tool restore`.
  The content task runs `dotnet tool restore` a second time before MGCB.
- Tool manifest `N/.config/dotnet-tools.json`: `dotnet-mgcb-compute`, `dotnet-mgcb-editor-compute`,
  `-linux`, `-mac`, `-windows`, all 3.8.3. `tool restore` restores all five.
- NuGet cache `%USERPROFILE%\.nuget\packages` (checked):
  - `monogame.framework.compute.windowsdx.nomemoryleak/3.8.1.2` (lib `net8.0-windows7.0/MonoGame.Framework.dll`),
    `monogame.content.builder.task.compute/3.8.3`, all five `dotnet-mgcb-*` 3.8.3 tool packages,
    `sharpdx*` 4.0.1, `imgui.net`, `accord*`.
  - `N/obj/project.nuget.cache` (2026-09-07): `"success": true`, 74 expected package files, **0 missing**
    (verified with a script).
- SDKs: 3.1.426, 5.0.x, 8.0.206, 9.0.302, **10.0.400**. No `global.json`, so SDK 10.0.400 builds the
  net8.0-windows target. Runtimes include Microsoft.NETCore.App 8.0.32 and WindowsDesktop 8.0.32.
- Conclusion: `dotnet build -c Release N/NAADF.csproj` should not need the network: the restore inputs
  and all tool packages are in the global packages folder. Not verified by a build. If the build
  stalls on `dotnet tool restore`, run it once by hand to see the error.
- State of the clone: `N/obj` holds a restore and a design-time build from 2026-09-07; `N/bin/Debug/net8.0-windows`
  is empty and `N/Content/bin` / `N/Content/obj` do not exist. The project was never built here.

### 1.2 Content pipeline and shader compilation

- `N/Content/Content.mgcb`: 24 effects, all `/importer:EffectImporter /processor:EffectProcessor
  /processorParam:DebugMode=Auto`, `/platform:Windows`, `/profile:Reach` (the app runs HiDef).
- The task package sets `MGCBCommand=mgcb-compute` and `EnableMGCBItems=true`
  (`...\monogame.content.builder.task.compute\3.8.3\build\*.props`), so `**/*.mgcb` is picked up
  without an explicit `MonoGameContentReference`.
- `RunContentBuilder` runs `dotnet mgcb-compute /@:Content.mgcb /platform:Windows
  /outputDir:N/Content/bin/Windows/Content /intermediateDir:N/Content/obj/Windows/net8.0-windows/Content`.
  The `.xnb` files are then added as `Content` items with `CopyToOutputDirectory=PreserveNewest`, so they
  land in `bin/Release/net8.0-windows/Content/shaders/.../*.xnb`.
- Shaders are compiled by `mgfxc` (in the tool package) through `SharpDX.D3DCompiler` ->
  `d3dcompiler_47.dll`. All effects use `cs_5_0` / `vs_5_0` / `ps_5_0`. The DLL is not in the tool
  package; Windows provides it (`C:\Windows\System32\d3dcompiler_47.dll`, present, 2026-10-14).
  **No DirectX SDK / fxc.exe is needed.**
- `oasis.cvox` and `obj2voxel.exe` are copied to the output (`N/NAADF.csproj` `None Update`).
- Build flags must agree between C# and HLSL: `#define ENTITIES` in `N/Settings.cs:5` and
  `N/Content/shaders/settings.fxh:8`. With ENTITIES the chunk texture is `uint2` (`settings.fxh:14-18`).

### 1.3 Prebuilt `Research/NAADF_win_x64`

- `NAADF.dll`/`NAADF.exe` dated 2026-02-21 04:48 +0900 (= 2026-02-20 20:48 +0100). Upstream initial
  commit `088a10f` is 2026-02-20 22:32 +0100. The prebuilt is the release that matches the initial
  commit (its README is the pre-`dc7f276` text), **not HEAD**.
- Source changes after the initial commit (`git log --stat`):
  - `7538aa2` (2026-03-24): multi-file `.vox` import (`ModelData.ImportFromVox`), one line in base TAA.
  - `cae246f` (2026-03-25): base renderer quality (base GI, sample refine, spatial resampling, TAA,
    `commonOther.fxh`), TAA history 64 -> 128 frames and Halton jitter in `WorldRender.cs`, entity history x2.
  - `a1c661d` (2026-04-13): Albedo crash fix (TAA arrays 64 -> 128; the crash came from `cae246f`),
    `Camera.cs` unary operator fix, base `radiusLitFactor` 1.5 -> 3.0.
- Unchanged since the initial commit (only `088a10f` in their history): `rayTracing.fxh`, all
  `pathTracer/*` shaders, `WorldRenderPathTracer.cs`, all `world/*` shaders (generation, AADF, edits).
- The prebuilt cannot take arguments (`N/Program.cs:10` `Main()` has no args). The benchmark needs a
  build from source in any case.

---

## 2. Rendering pipeline

### 2.1 Frame structure (all versions)

- `App.Update` (`N/App.cs:98-116`) -> `worldHandler.Update` **only if `IsActive`** (`N/App.cs:110-111`):
  `voxelTypeHandler.Update` (material table upload), `pathHandler.Update` (camera path),
  `WorldRender.render.Update` (camera, sun keys, `frameCount`), `worldData.Update` (entities, edits,
  CPU->GPU change sync, chunk-AADF passes). See `N/World/WorldHandler.cs:69-75`,
  `N/World/Data/WorldData.cs:109-118`.
- `App.Draw` (`N/App.cs:118-142`): `GraphicsDevice.Clear`, `worldHandler.Render` ->
  `WorldRender.Render` (`N/World/Render/WorldRender.cs:92-101`: sun direction, sun colour, then
  `RenderInternal` of the active version), then ImGui, then MonoGame presents.
- Version selection: `Settings.data.render.version` (default `Base`, `N/Settings.cs:54`).
  `WorldRender.ApplyRenderVersion(v)` **ignores its argument** and reads `Settings.data.render.version`
  (`WorldRender.cs:33-42`). Set the setting first, then call it.
- `frameCount` increments every Update unless key P is held (`WorldRender.cs:80-88`);
  `taaIndex = 127 - frameCount % 128`. `randValues[32]` come from an unseeded `System.Random`
  (`WorldRender.cs:31,84`), so noise differs per run unless the generator is seeded.
- Sun: `skySunDir = Transform((0,1,0), RotZ(sunAngle) * RotY(30 deg))`, `sunAngle = 60` (`WorldRender.cs:30,95`);
  `sunColor = Atmosphere.GetLightForPoint((0,10,0))` (`WorldRender.cs:96`, `N/World/Render/Atmosphere.cs:71-76`).
  The sky LUT does **not** contain a sun disk (`renderAtmosphere.fx` calls `addLightForDirection` without
  `includeSun`); sun light enters only through explicit shadow rays (NEE).
- Sky: a 1024x1024 octahedral LUT `atmosphereComp` (uint3 = half RGB light + half RGB transmittance),
  one quarter of the texels recomputed per frame (`pathTracer/renderAtmosphere.fx:12`), 24 x 6
  scattering steps per texel (`N/Gui/Main/UiDebug/UiSkyDebug.cs:30-31`). A ray miss calls
  `applyAtmosphere` = one LUT fetch (`common/atmosphere/atmospherePrecomputed.fxh:9-22`).
- All compute passes are `[numthreads(64,1,1)]` over `W*H` pixels, dispatched as
  `(W*H+63)/64` groups in X (see 5.4 about the 65535 limit).
- Final pass: a cube draw with `WorldProjection = Translate(-0.5) * viewProj` and a pixel shader that
  tone-maps (`lerp(c/(exposure+lum), c/(k+c), c/(k+c))`, `exposure = 1`, `k = toneMappingFac = 1.5`)
  into the `ColorSRgb` back buffer (hardware sRGB encode).

### 2.2 Albedo version (`N/World/Render/Versions/WorldRenderAlbedo.cs`)

Settings (`WorldRenderAlbedo.cs:12-25`): `isTAAJitter=false`, `checkSun=false`, `taaSampleMaxAge=1`.

| # | Pass | Shader | Work |
| --- | --- | --- | --- |
| 1 | FirstHit | `render/versions/albedo/renderFirstHit.fx` | per pixel: ray/world-AABB clip, **one** `shootRay` with `MAX_RAY_STEPS_PRIMARY = 120` (`:63`). On hit: `light = albedo * sunColor * 0.2 * dot(sunDir, normalize(n + 1.01*sunDir))` (ambient-like Lambert term, `:104-105`); optional sun shadow ray if `checkSun` (`:91-103`, 120 steps). On miss: `light = 0` (**black sky**, no atmosphere). Writes `taaSampleAccum` and, if TAA, `firstHitData`/`taaSamples`. |
| 2 | ReprojectOld | `albedo/renderTaaSampleReverse.fx` | only if `taaSampleMaxAge > 1` and not `showSteps` (`WorldRenderAlbedo.cs:74,109-132`). Off by default. |
| 3 | Final | `albedo/renderFinal.fx` | `taaSampleAccum` -> tone map (`toneMappingFac` is hard-coded 1.0 here). |

No mirrors, no bounce, no atmosphere pass. **This is the "P" mode** (primary rays only): FirstHit is the
traced work. Two caveats: the 120-step cap (a ray that needs more steps becomes a miss/black) and the
lighting term (not plain albedo). The 120-step cap is a compile-time constant (`render/rayTracing.fxh:7`).
`taaSamples` (W*H*32*8 B = 944 MB at 2560x1440) is allocated even when TAA is off (`WorldRenderAlbedo.cs:57`).

### 2.3 Base version (the paper renderer, `N/World/Render/Versions/WorldRenderBase.cs`)

Settings (`WorldRenderBase.cs:14-53`): `bounceCount=3`, `globalIllumMaxAccum=128`, `taaSampleMaxAge=32`,
`spatialResampleSize=500`, `spatialResampleVisibilityTestMaxDepth=80` (passed but the shader uses the
constant), `denoiseThresh=400`, `radiusLitFactor=3`, `noiseSupressionFactor=0.4`, flags `isDenoise`,
`isTAAJitter`, `skipSamples`, `isSampleLeveling`, `isVaryingResmaplingRadius`, `isAtmosphereInteraction`
all true.

Passes in order (`RenderInternal`, `WorldRenderBase.cs:173-457`):

| # | Pass | Shader | What it traces / does |
| --- | --- | --- | --- |
| 1 | Atmosphere | `base/renderAtmosphere.fx` | sky LUT, 1/4 per frame |
| 2 | FirstHit | `base/renderFirstHit.fx` | primary ray, up to 4 segments through perfect mirrors (`materialBase 3`), each `shootRay(...,120)`; stops at the first non-mirror; emission added; atmosphere in-scatter along each segment if `isAtmosphereInteraction`; miss -> LUT. Writes `firstHitData` (normal/plane per segment, half distance, type), `firstHitAbsorption`, `finalColor`. |
| 3 | ReprojectOld | `base/renderTaaSampleReverse.fx` | TAA: reverse reprojection of up to `taaSampleMaxAge` (32) stored past samples per pixel with 3x3 depth/normal/plane checks -> `taaSampleAccum`. |
| 4 | ClearBucketsAndCalcMask | `base/renderSampleRefine.fx` | per 8x8 bucket: normal mask, min/max depth. |
| 5 | RayQueue + RayQueueStore | `base/rayQueueCalc.fx` | chooses pixels that get a GI path: all hit pixels, or with `skipSamples` 1 of 1..4 pixels depending on TAA history (0.25-1 spp). Writes an indirect dispatch count. |
| 6 | GlobalIlum (indirect) | `base/renderGlobalIllum.fx` | per queued pixel: one BSDF direction at the primary hit (uniform hemisphere, or VNDF for rough metal, or mirror), then `min(bounceCount,3)` segments with `MAX_RAY_STEPS_SECONDARY = 100`. At each diffuse/rough hit a **sun shadow ray** (cone 0.9999, `MAX_RAY_STEPS_SUN_SECONDARY = 80`). Miss -> sky LUT, but only with probability 1/16 and weight 16 (`:131-132`). Stochastic termination of low-radiance samples (`:239-248`). Stores the path as a "valid" or "invalid" sample in ring buffers indexed by `accumIndex` (128-frame history). |
| 7 | ValidHistory, CountValidAndRefine (indirect), CountInvalid (indirect), RefineBuckets | `base/renderSampleRefine.fx` | temporal reuse: reprojects stored samples of the last `globalIllumMaxAccum` frames into 8x8 buckets (32 per bucket, compressed to 8). |
| 8 | SpatialResampling | `base/renderSpatialResampling.fx` | per pixel: 12 candidate samples from buckets within a radius (500 px, varying), RIS weights with jacobian, **1 visibility ray** (`MAX_RAY_STEPS_VISIBILITY = 60`, through up to 3 mirrors) and **1 sun shadow ray** at the primary hit (`MAX_RAY_STEPS_SUN = 120`). |
| 9 | CalcDenoiseHorizontal/Vertical | `base/renderDenoiseSplit.fx` | separable bilateral filter (21 taps), if `isDenoise`. |
| 10 | CalcNewTaaSample | `base/renderTaaSampleReverse.fx` | stores the new TAA sample, adds it to the accumulation. |
| 11 | Final | `base/renderFinal.fx` | tone map. |

Base is not usable for the protocol (resampling, denoise, TAA, sun NEE are built in).

### 2.4 PathTracer version (`N/World/Render/Versions/WorldRenderPathTracer.cs`)

Settings (`WorldRenderPathTracer.cs:12-30`): `bounceCount=3`, `maxSamples=8192`, `isTAAJitter=true`,
`isAtmosphereInteraction=true`.

| # | Pass | Shader | Work |
| --- | --- | --- | --- |
| 1 | Atmosphere | `pathTracer/renderAtmosphere.fx` | sky LUT, 1/4 of texels per frame (`WorldRenderPathTracer.cs:87-95`) |
| 2 | FirstHit | `pathTracer/renderFirstHit.fx` | same as Base FirstHit but `shootRay(..., 2000)` (`:66`): primary ray + up to 3 more mirror segments; miss -> LUT; optional in-scatter along segments (`:83-84`). |
| 3 | GlobalIlum | `pathTracer/renderGlobalIllum.fx` | one thread per pixel (no queue), see below |
| 4 | Final | `pathTracer/renderFinal.fx` | tone map of `sampleAccumulated` |

`pathTracer/renderGlobalIllum.fx` in detail:

- Reconstructs the primary hit from the stored planes (`getHitDataFromPlanes`, `common/commonRenderPipeline.fxh:154-213`).
- First direction at the primary hit P (`:56-73`): mirror -> reflect; rough metal -> VNDF sample, weight G*F;
  diffuse -> `getUniformHemisphereSample(n, 0)` = **uniform** hemisphere (`common/commonRayTracing.fxh:75-84`),
  the cos/pdf weight `2*cos` is applied at `:190`.
- Loop `for (bounce = 0; bounce < min(maxBounceCount, 3) && P is a hit; ++bounce)` (`:79`): **hard cap 3**.
  Each iteration traces one ray (`shootRay(...,2000)`, `:82`).
  - Miss: add sky LUT (`applyAtmosphere(..., 1)`, full weight) and stop (`:86-92`).
  - Hit Q: albedo applied for diffuse/emissive (`:106-107`); **sun NEE**: one shadow ray toward a jittered
    sun direction for diffuse/rough surfaces (`:110-139`); emission `colorLayer.r` added (`:143-144`);
    then the next direction is sampled (uniform hemisphere with weight `2*cos`, `:170-174`; VNDF or mirror
    for metals). The direction sampled at the last hit is never traced.
- After the loop: `finalCol = emission/sky of primary + radiance * absorption * 2 * cos(P)` (`:186-190`),
  then a **second sun NEE** shadow ray at P (`:192-216`).
- **No Russian roulette.** A rough-metal sample below the surface ends the path (`:163-164`).
- Accumulation (`:218-222`): `n = min(maxSamples, a + 1)`, `acc = lerp(acc, finalCol, 1/n)`.
  `maxSamples` passed to the shader is 1 when `camera.anyViewChange` (mouse/keyboard) or a UI change, else
  `settings.maxSamples` (`WorldRenderPathTracer.cs:84`). `SetPos`/`SetDir` do not set `anyViewChange`.

Meaning of `bounceCount = B` (B <= 3): the path has 1 primary ray (plus up to 3 extra mirror segments) and
B secondary rays, from P, Q1, ..., Q(B-1). Light is collected from: sky at the first miss of any of these
rays, emission at P and at Q1..QB, and (if NEE stays) sun at P and at Q1..QB. B = 2 gives camera -> P -> Q1 -> Q2
with sky on escape, i.e. "primary + 2 diffuse bounces".

How to get "one frame = 1 spp, primary + 2 diffuse bounces, sky only, nothing else" (protocol mode B2):

1. `Settings.data.render.version = PathTracer`, `renderPathTracer.bounceCount = 2`, `maxSamples = 1`
   (`lerp` factor 1 = overwrite), `isTAAJitter = false` (pixel centre; the jitter is `rand.NextDouble()-0.5`,
   `WorldRenderPathTracer.cs:81`), `isAtmosphereInteraction = false`.
2. Remove both sun NEE blocks (`pathTracer/renderGlobalIllum.fx:119-139` and `:192-216`), or guard them
   with a new `bool useSunNee` parameter. Setting `sunColor = 0` removes the light but **not** the rays.
3. Optional, for protocol parity: cosine-weighted hemisphere (`z = sqrt(u)` instead of uniform, and weight 1
   instead of `2*cos` at `:173` and `:190`). Cost is the same; noise differs.
4. Atmosphere pass: keep it out of `gpu_ms` (it is sky precomputation), or run it 4 frames before
   measuring and skip it during measurement (the LUT is static while the sun does not move).
5. Mirrors: FirstHit follows perfect mirrors (`materialBase 3`) for up to 4 segments. The converter maps
   Caelix glass to mirror or rough metal (section 9). For strict parity convert glass to diffuse.
6. Traced work = FirstHit + GlobalIlum. Nominal rays = W*H*3. There are no ray counters; add an
   `InterlockedAdd` on a `RWByteAddressBuffer` in `shootRay` callers if counted rays are needed.

The PathTracer version with these changes is the closest match to B2. The Albedo version serves as P.

### 2.5 `showSteps`

`Settings.data.render.showSteps` (`N/Settings.cs:53`), passed as `showRayStep`:

- Albedo: `taaSampleAccum[i].x = rayResult.stepCount` (`albedo/renderFirstHit.fx:122-123`); Final shows
  `steps * 0.01` as grey (`albedo/renderFinal.fx:41-46`). Disables TAA (`WorldRenderAlbedo.cs:74`).
- Base / PathTracer: FirstHit writes `stepCount` into `firstHitData.z` bits 0-14 instead of the voxel type
  (`pathTracer/renderFirstHit.fx:121`); Final shows `steps * 0.01`. Downstream passes then read the step count
  as a material index, so the lighting is garbage in that mode.
- `stepCount` counts DDA iterations of the last `shootRay` call of the primary ray (all of them when there is
  no mirror), plus entity steps.
- Use: read back `taaSampleAccum` (Albedo) once with `showSteps` on to get primary iterations per ray for
  `traversal.iters_per_ray`. Do it in a separate diagnostic frame, not in timed frames.

---

## 3. Ray traversal and data layout

### 3.1 GPU resources (`N/World/Data/WorldData.cs:57-93`)

| Resource | Type | Element | Index |
| --- | --- | --- | --- |
| `dataChunkGpu` | `Texture3D` `Rg64Uint` (ENTITIES) / `R32Uint` | 8 B / 4 B per chunk, **dense** grid `sizeInChunks` | `chunks[chunkPos]` |
| `dataBlockGpu` | `StructuredBuffer<uint>` (pool) | 4 B per block, 64 consecutive per non-uniform chunk | `chunk.ptr + bx + 4*by + 16*bz` |
| `dataVoxelGpu` | `StructuredBuffer<uint>` (pool) | 2 voxels per uint, 32 uints per non-uniform block, **deduplicated** by content hash | `block.ptr + vIndex/2`, half `vIndex & 1` |
| `voxelTypeData` | `StructuredBuffer<uint4>` | material table, 16 B per type | voxel/block type = render index (15 bit) |

Only the chunk level is dense. Blocks and voxels are pools addressed by 30-bit pointers. Uniform blocks and
chunks store their type inline and allocate nothing below them.

### 3.2 Encodings (empty space = AADF)

Chunk `x` (32 bit; `y` holds entity info with ENTITIES):
- bits 30-31 state: `0` empty, `1` uniform solid, `2` (bit 31) has 64 blocks.
- state 0: six 5-bit chunk-level AADFs: `-x` bits 0-4, `+x` 5-9, `-y` 10-14, `+y` 15-19, `-z` 20-24, `+z` 25-29
  (0..31 chunks, `rayTracing.fxh:79`, `boundsCalc.fx:156-157`). Computed incrementally over frames (3.5).
- state 1: type in low bits. state 2: block pointer (uint index into `blocks`) in bits 0-29.
- `y` (ENTITIES): `(count & 0xFF) | (start << 8)` into `entityChunkInstances`; nonzero `y` blocks chunk AADF growth.

Block (32 bit):
- bits 30-31 state: `0` empty, `1` uniform solid (type in low 15 bits), `2` mixed (voxel pointer, uint units, bits 0-29).
- state 0: six 2-bit AADFs in block units inside the chunk (0..3): `-x` bits 0-1, `+x` 2-3, `-y` 4-5,
  `+y` 6-7, `-z` 8-9, `+z` 10-11 (`rayTracing.fxh:78`, `chunkCalc.fx:233`, `boundsCommon.fxh:38-64`).

Voxel (16 bit):
- bit 15 solid. Solid: bits 0-14 = render index. Empty: six 2-bit AADFs in voxel units inside the block
  (same bit layout as the block AADF) (`chunkCalc.fx:208`).

Bytes: chunk 8 B (4 B without ENTITIES); block 4 B (only in non-uniform chunks, 256 B per chunk);
voxel 2 B (only in mixed blocks, 128 B per **unique** mixed block).

### 3.3 Traversal loop (`render/rayTracing.fxh:73-152`)

```
shootRay(originInt, originFrac, dir, maxSteps):
  invAbs = abs(1/dir); neg = dir < 0
  t = 0; mask = 0; steps = 0
  while steps < maxSteps:
    p    = originFrac + dir * t
    cell = uint3(floor(p + mask * sign(dir) * 0.5) + originInt)      // cell we are entering
    if any(cell >= worldMax): break                                   // negative wraps to huge
    c = chunks[cell / 16]; local = cell % 16
    if c.x has blocks (bit 31):
      b = blocks[(c.x & 0x3FFFFFFF) + flat(local / 4)]
      if b is mixed (bit 31):
        v = voxels[(b & 0x3FFFFFFF) + flat(cell % 4) / 2] >> 16 * (flat(cell % 4) & 1)
        node = v; if v.bit15: node |= SOLID
        skip = 2-bit AADF of v toward dir            (voxels)
      else
        node = b
        skip = AADF(b)*4 + distance to block wall toward dir   (voxels)
    else
      node = c.x
      skip = distance to chunk wall toward dir + 16 * AADF5(c.x)  (voxels)
    if node & SOLID (bit 30): hit: type = node & 0x7FFF, t, cell; break
    // leave the empty box [cell, cell + skip] along the ray
    d = (1 + skip - (1 - mask) * abs(neg - frac(p))) * invAbs
    tm = min(d.x, d.y, d.z); mask = (d == tm); t += max(tm, 1e-4); steps++
  (then, with ENTITIES, a small DDA per entity chunk instance collected on the way)
  miss: length = 99999999, return (steps == 0)
  hit:  normal = -mask * sign(dir); return true
```

Every iteration does 1 texture load, plus 1 buffer load for chunks with blocks, plus 1 more for mixed blocks.
The CPU has a copy for picking: `WorldData.RayTraversal` (`WorldData.cs:396-473`).

Step caps (`rayTracing.fxh:7-11`): primary 120, secondary 100, sun 120, sun-secondary 80, visibility 60
(Albedo and Base). PathTracer passes 2000 everywhere.

### 3.4 World size

- Size is in world-gen segments: `segmentVoxels = worldGenSegmentSizeInGroups * 64` (default 256).
  `sizeInVoxels = ceil(wanted / segmentVoxels) * segmentVoxels` per axis (`WorldData.cs:57-71`).
  `actualSizeInVoxels = wanted` limits what the generator writes.
- Limits: chunk texture <= 2048 per axis (D3D11 Texture3D); packed positions use x 11 bits, y 10 bits, z 11 bits
  in group/chunk units (`boundsCalc.fx:45`, `ChangeHandler.cs:76-78`, `EditingHandler.cs:163`,
  `worldChange.fx:122`), so y <= 1023 chunks
  (16368 voxels); README: "up to 16384^3". Converter: <= 16384 per axis.
- Pool limits: 30-bit pointers in uint units. Blocks <= 2^30 uints. Voxels <= 2^30 uints = **33,554,432 unique
  mixed blocks**. Both pools are also capped at `0xFFFF0000` bytes (just under 4 GiB) by
  `DynamicStructuredBuffer.SetNewMinCount` (`N/Common/DynamicStructuredBuffer.cs:25-36`) and
  `WorldData.AddVoxels/SetBlocks` (`WorldData.cs:313,361`). There is no overflow check on the GPU.
- Chunk-level empty skip <= 31 chunks = 496 voxels per step.

### 3.5 Construction and AADF computation

`WorldData.GenerateWorld` (`WorldData.cs:120-218`), synchronous, on the main thread:
1. For each segment (z, y, x order): `WorldGeneratorModel.CopyToChunkData` fills `segmentVoxelBuffer`
   (segment voxels in chunk/block/voxel order, `world/generator/generatorModel.fx:54-72`), then
   `chunkCalc.fx VoxelHash` (`chunkCalc.fx:117-181`): per block, hash the 64 voxels; uniform -> inline;
   else `GetVoxelPointer` = GPU open-addressing hash map (`chunkCalc.fx:57-115`) that deduplicates against
   all earlier blocks and appends new voxel records; per chunk, uniform -> inline, else append 64 blocks.
   After every segment a **GPU->CPU readback** of the two counters (`WorldData.cs:148`) grows the pools
   (`:149-151`).
2. Read back the chunk texture (with ENTITIES via a copy shader in 16M-chunk pieces, `:162-191`) and the full
   block and voxel pools to the CPU arrays `dataChunk/dataBlock/dataVoxel` (`:195-198`).
3. `ComputeVoxelBounds` (one 64-thread group per unique mixed block) and `ComputeBlockBounds` (one group per
   non-uniform chunk): 3 relaxation iterations in groupshared memory give the in-block / in-chunk 2-bit AADFs
   (`chunkCalc.fx:193-241`, `boundsCommon.fxh:38-64`).
4. `blockHashingHandler.SyncGpuToCpu()`, `isLoaded = true`.

Chunk AADFs are computed afterwards, a little every frame, by `WorldBoundHandler.Update`
(`N/World/Data/WorldBoundHandler.cs:91-121`, `boundsCalc.fx`): there are 31 levels x 3 axes of queues of
groups; every group starts in level 0 for x, y, z (`Initialize`, `:53-89`). Each frame runs 5 x
(`PrepareGroupBounds` 1 thread, `ComputeGroupBounds` indirect, <= `maxGroupBoundDispatch = 32768` groups).
A processed group always moves to the next level up to 30, so full convergence costs
`G * 93` group-steps (G = chunkCount / 64), i.e. **>= G*93/163840 frames** (bistro-8192: ~324 frames).
The ImGui slider "AADF speedup" sets `maxGroupBoundDispatch = speedup * 512` (`:123-129`).

---

## 3a. GPU memory model

Formulas for a world (ENTITIES on, segment 256). `C` = chunk count, `G = C/64`,
`B` = blocks entries in use (64 per non-uniform chunk), `U` = unique mixed blocks.

| Item | Bytes | Source |
| --- | --- | --- |
| chunk texture | `8 C` (4 C without ENTITIES) | `WorldData.cs:82` |
| block pool | between `4 (B + 262144)` and `8 (B + 262144)` (growth factor 2) | `WorldData.cs:150` |
| voxel pool | `min(0xFFFF0000, 8 (64 U + 8388608))`, i.e. **2x-4x** the `128 U` that is used: the call passes a voxel count where a uint count is expected, then doubles | `WorldData.cs:151` |
| hash map (GPU, and the same on CPU) | `12 * pow2 >= 2 (U + 524288)` | `BlockHashingHandler.cs:36-61,177-201`, `WorldData.cs:132` |
| chunk-AADF queues | `384 G + 12 G` | `WorldBoundHandler.cs:46-47` |
| segment buffer | 32 MiB (256^3 / 2 uints) | `WorldData.cs:73` |
| entities (fixed) | ~563 MB: `entityVoxelDataGpu` 400 MB, history 2 x 33.5 MB, chunk instances 2 x 40 MB, chunk updates 16 MB | `EntityHandler.cs:143-149` |
| change upload buffers | `8 G` + 16 MB + 8 MB + 20 MB | `ChangeHandler.cs:56-59` |
| render, PathTracer | 48 B per pixel + 12 MB LUT (177 MB at 2560x1440) | `WorldRenderPathTracer.cs:68-72` |
| render, Albedo | 16 + 256 + 8 B per pixel (`taaSamples` dominates; 1.0 GB at 2560x1440) | `WorldRenderAlbedo.cs:56-58` |
| render, Base | ~ 500 B per pixel (~1.9 GB at 2560x1440) | `WorldRenderBase.cs:134-170` |

Transient during generation: the model (`ModelData.CreateDataForRender`, `N/World/Model/ModelData.cs:74-124`)
is on the GPU at the same time: chunks `4 Cm`, blocks `4 Bm`, voxels `4 Vm/2` **but** if the voxel uint count
exceeds `0x1FFF0000` the buffer is allocated at `0x3FFF0000` uints = **4.29 GB** (`:121`; same rule for blocks
at `:116`); plus a 400 MB staging buffer in `Helper.CopyIntoStructuredBufferLarge` (`N/Common/Helper.cs:22-54`);
plus every pool resize briefly holds old and new buffers (`DynamicStructuredBuffer.Resize`, `:38-49`).
The default Oasis world (created in `WorldHandler.Initialize`, `N/World/WorldHandler.cs:29-35`) also stays
alive until the new world is built (`UiHeaderBar.cs:41-48`).

### Measured scene statistics (from the `.cxw` files, script in the session scratchpad)

| | bistro-8192 | rootfall |
| --- | --- | --- |
| cxw | v6, 1 static entity, identity | **v8**, 1 static entity, identity, player pose present |
| sector extent (voxels) | 7808 x 2304 x 8192 | 4608 x 2560 x 5632, origin (-2432, 0, -2432) |
| NAADF world (256-padded) | 7936 x 2304 x 8192 | 4608 x 2560 x 5632 |
| chunks C (dense) / groups G | 36,569,088 / 571,392 | 16,220,160 / 253,440 |
| occupied voxels | 301,341,112 | 1,393,576,433 |
| material codes | (7460 at 4096) | 648 (2 glass codes) |
| non-empty chunks -> block entries B | 983,175 -> 62,923,200 | 1,343,020 -> 85,953,280 |
| uniform solid blocks | 11 | 4,775,769 |
| mixed blocks (cvox, no dedup) | 17,739,339 | **35,743,498** |
| unique mixed blocks U (dedup) | 15,455,069 (factor 1.15) | 30,537,203 (factor 1.17) |
| bistro-4096 for reference | 4,194,717 mixed, 3,843,768 unique (1.09) | |

### Estimate for bistro-8192 (ENTITIES on, PathTracer 2560x1440)

| Item | MB |
| --- | --- |
| chunk texture | 293 |
| block pool (B = 62.9M) | 253-505 |
| voxel pool (needs 1,978; allocation hits the cap) | 4,295 |
| hash map (32M entries) | 403 |
| chunk-AADF queues | 226 |
| entities | 563 |
| segment, change buffers, types | ~85 |
| render | 177 |
| **steady state** | **~6.3-6.6 GB** |
| generation extra: model voxels (567.7M uints > 0x1FFF0000 -> 4.29 GB buffer), model blocks 252, model chunks 141, staging 400, last voxel-pool resize (old ~2 GB) | +7.1 GB transient |
| **peak** | **~13-14 GB** |

It fits in 16 GB, but the peak is tight. Cheap reductions: skip the Oasis world; build without ENTITIES
(-563 MB, chunk texture -146 MB); allocate the model buffers at their exact size instead of 0x3FFF0000
(-2.3 GB); pre-size the voxel pool once instead of growing it; dedup in the converter (U*32 = 494.6M uints
< 0x1FFF0000, so the model buffer is exact anyway). D3D11 can page to system memory instead of failing,
which would show up as a slow generation, not as an error.

### Estimate for rootfall

- **The current converter rejects it** twice: v8 header, and `35,743,498 * 32 = 1.144e9 uints > 2^30`
  ("Voxel pointer overflow", `convert.py:126`). Block dedup in the converter fixes the second issue
  (30.54M unique * 32 = 977M uints < 2^30).
- World voxel pool needs 977M uints = 3.91 GB, 91 % of the 4 GiB cap (no margin if the noise ends up less
  dedup-friendly). Hash map 64M entries = 805 MB (GPU) + 805 MB (CPU). Blocks 344-690 MB.
- Model voxel buffer: 977M uints > 0x1FFF0000 -> 4.29 GB. Peak ~14 GB, steady ~6.8 GB (with ENTITIES).
  Without the reductions above this is at the 16 GB limit.

### Estimate for fractalcave (384 x 1024 x 384)

With segment 256: world 512 x 1024 x 512, C = 65,536 chunks, G = 1,024. With
`worldGenSegmentSizeInGroups = 2` (128-voxel segments) the world is exactly 384 x 1024 x 384
(C = 24 x 64 x 24 = 36,864, G = 576). Memory is dominated by the fixed parts (entities, 32 MiB segment
buffer, hash map minimum 1M entries = 12 MB). Well under 1 GB plus render targets.

CPU RAM (64 GB machine): the CPU keeps full copies of the pools (`dataBlock`, `dataVoxel` sized like the GPU
buffers), the hash map, `dataChunk` (4 C), `editChunkDataPointer` (4 C), `chunkEntityData` (4 C), and the
model arrays (not freed after generation, only the model GPU buffers are). Expect ~9-12 GB for bistro-8192
and rootfall.

---

## 4. Camera

`N/Common/Camera.cs`:
- Position: `PositionSplit` = int3 + float3 fraction (`:14-71`), unit = 1 voxel. World voxel `(i,j,k)` occupies
  `[i, i+1)`. Same convention as Caelix (`voxel centre = p + 0.5`).
- `SetPos(Vector3)` (`:105-108`), `SetDir(Vector3)` (`:115-123`): `yaw = atan2(d.x, d.z)`,
  `pitch = -atan2(d.y, |d.xz|)`, `rotation = Matrix.CreateFromYawPitchRoll(yaw, pitch, 0)`.
- Every `Update` recomputes `camDir = Transform((0,0,1), rotation)` (`:162`), builds
  `view = CreateLookAt(0, camDir, Up)`, `viewProj = view * proj`, `invViewProj` (`:199-203`).
- Projection: `Matrix.CreatePerspectiveFieldOfView(fov, aspect, 0.1, 100000)` (`:100-103`), **vertical FOV**
  in degrees from `Settings.data.general.fov` (default 90, `N/Settings.cs:38`), applied every Update
  (`WorldRender.cs:72`).
- Direction formula (XNA order: pitch about X, then yaw about Y):
  `forward = (sin(yaw) cos(pitch), -sin(pitch), cos(yaw) cos(pitch))`, positive pitch looks down,
  yaw 0 looks +Z, yaw 90 looks +X. **Identical to the Caelix pose** (`Caelix-Renderer/host-rs/caelix-app/src/camera.rs:8`).
- Ray generation: `normalize(mul(float4((uv*2-1) * (1,-1), 1, 1), invViewProj).xyz)` with
  `uv = (pixel + 0.5 + jitter) / size` (`common/commonRenderPipeline.fxh:75-79`).
- Handedness: XNA is right-handed. Looking along +Z with +Y up, screen-right is world **-X**
  (`CreateLookAt` x-axis = `cross(up, -forward)`). Caelix/Unity is left-handed: screen-right is +X.
  Same pose -> the NAADF image is the **horizontal mirror** of the Caelix image. Flip the NAADF PNG
  horizontally before the side-by-side check (cheapest), or mirror X in the converter and use
  `yaw' = -yaw`, `x' = W - x`.
- Pose mapping: `naadf.position = caelix.position + world_offset`, where `world_offset = -source_origin`
  of the converter (bistro: 0; rootfall: (+2432, 0, +2432)). Then `camera.SetPos(position)` and
  `camera.SetDir(forward(pitch, yaw))`, `Settings.data.general.fov = fov_y`.
  Example: rootfall player pose `(-230.77, 1653.85, -1538.46)`, quaternion `(0.2701, 0.1309, -0.0371, 0.9532)`
  -> pitch 31.65, yaw 15.64 -> NAADF position `(2201.23, 1653.85, 893.54)`.
- Model placement: the generator samples model voxel `p` at world voxel `p` (no offset,
  `generatorModel.fx:16-52`). **It tiles the model in X and Z** (`voxelPos % modelSize`, `:20`) and only cuts Y
  (`:21,48-49`); voxels outside `actualSizeInVoxels` are empty (`:18-19`). "Fit to Model" uses
  `new WorldData(modelData.size, 4)` (`N/Gui/Main/HeaderBar/UiHeaderBar.cs:36-50`), so `actualSize = model size`
  and nothing repeats. "Fixed size" (`:115-162`) uses `segments * segmentSize * 64` and repeats the model in
  X/Z if the world is bigger.
- Camera outside the world is fine: FirstHit clips the ray to the world AABB `[0.1, size-0.1]` first
  (`pathTracer/renderFirstHit.fx:41-59`).
- Default camera: position (500, 200, 40) (`WorldRender.cs:49`). Mouse/keyboard input still moves the camera
  in `Camera.Update` (`:130-197`); the bench should call `SetPos/SetDir` every frame before `camera.Update`.

---

## 5. Timing, vsync, back buffer

### 5.1 What exists

- No GPU timing. `WorldRender.frameTime` is an EMA (0.01) of `gameTime.ElapsedGameTime` (CPU frame-to-frame,
  `WorldRender.cs:94`), shown in the Debug window (`N/Gui/Main/UiDebug/UiDebug.cs:23-24`).
  `ChangeHandler.changeProcessingTime` and `EntityHandler.chunkProcessingTime` are CPU stopwatch EMAs.
  `PathHandler` shows the average `gameTime` over a camera path (`N/Common/PathHandler.cs:128`).
- Vsync off: `SynchronizeWithVerticalRetrace = false` (`N/App.cs:64`). Fixed time step off:
  `IsFixedTimeStep = false` (`N/App.cs:66`). The "Lock to 60 fps" setting only applies when the checkbox is
  clicked (`N/Settings.cs:43-44`), not when `settings.json` is loaded.
- Back buffer: `ColorSRgb`, size `min(display - (80,160), 1920x1080)` (`N/App.cs:57-65`). HDR flag off.

### 5.2 Getting the D3D11 device and context

MonoGame fork (checked with Mono.Cecil on the cached `MonoGame.Framework.dll`):
- `GraphicsDevice._d3dDevice` (`SharpDX.Direct3D11.Device`) and `GraphicsDevice._d3dContext`
  (`SharpDX.Direct3D11.DeviceContext`) are **internal fields**; no public property. Use reflection:
  `typeof(GraphicsDevice).GetField("_d3dDevice", BindingFlags.NonPublic | BindingFlags.Instance)`.
- `DispatchCompute` -> `PlatformDispatchCompute`: `lock (_d3dContext) { ApplyComputeState(); _d3dContext.Dispatch(x,y,z); UnbindWriteableComputeResources(); }`.
  Dispatches go straight to the immediate context, so timestamps issued between `DispatchCompute` calls
  bracket the passes exactly. Lock the context object the same way when issuing queries.
- No splitting of large dispatches (see 5.4).
- `SharpDX.Direct3D11 4.0.1` (already referenced; `N/Common/Helper.cs:3` uses it) has `Query`,
  `QueryDescription`, `QueryType.Timestamp`, `QueryType.TimestampDisjoint`,
  `QueryDataTimestampDisjoint`, `DeviceContext.Begin/End/GetData<T>/IsDataAvailable`.

Sketch:

```csharp
var dev = (SharpDX.Direct3D11.Device)typeof(GraphicsDevice).GetField("_d3dDevice", NonPublic|Instance).GetValue(App.graphicsDevice);
var ctx = (SharpDX.Direct3D11.DeviceContext)typeof(GraphicsDevice).GetField("_d3dContext", NonPublic|Instance).GetValue(App.graphicsDevice);
// per frame slot (ring of >= 4 frames): one TimestampDisjoint + one Timestamp per marker
lock (ctx) { ctx.Begin(disjoint); ctx.End(ts[0]); }        // frame start (top of App.Update)
// ... after each DispatchCompute of interest: lock (ctx) ctx.End(ts[k]);
lock (ctx) { ctx.End(tsLast); ctx.End(disjoint); }         // after the Final draw, before ImGui
// 4 frames later:
while (!ctx.IsDataAvailable(disjoint)) {}
var dj = ctx.GetData<QueryDataTimestampDisjoint>(disjoint);
if (!dj.Disjoint) ms = (ctx.GetData<long>(ts[k]) - ctx.GetData<long>(ts[k-1])) * 1000.0 / dj.Frequency;
```

Markers for `gpu_ms_breakdown`: PathTracer `Atmosphere`, `FirstHit`, `GlobalIlum`, `Final`
(`WorldRenderPathTracer.cs:94-164`); Albedo `FirstHit`, `Final` (`WorldRenderAlbedo.cs:106-146`);
dynamic scene `edit upload` = `ChangeHandler.UpdateWorld` dispatches and `chunk AADF` = `WorldBoundHandler.Update`
(both run inside `WorldData.Update`, i.e. in `App.Update` before `Draw`).

### 5.3 Screenshot to PNG

- `GraphicsDevice.GetBackBufferData<T>(T[])` is public. Call it in `App.Draw` after the Final draw and before
  ImGui and Present. The back buffer is `ColorSRgb`, so the bytes are already sRGB-encoded RGBA8; save them with
  `new Texture2D(gd, W, H, false, SurfaceFormat.Color)` + `SetData` + `SaveAsPng(stream, W, H)` (public), or
  with `System.Drawing.Bitmap` (swap R/B).
- Do not copy the existing `pathFinalFrame.png` path (`WorldRenderPathTracer.cs:145-176`,
  `WorldRenderBase.cs:424-454`): it renders into `RenderTarget2D` with format `Color` (not sRGB), so the PNG
  lacks the sRGB encode and looks darker than the screen.
- For HDR comparisons read `accumulatedSamples` (`StructuredBuffer` of `Vector4`, linear radiance before tone
  mapping) with `GetData`.

### 5.4 Other loop details that affect a benchmark

- **`IsActive` gating** (`N/App.cs:110-111`): if the window is not focused, `worldHandler.Update` does not run:
  no camera matrices (all-zero on the first frame), no `frameCount`, no edits, no chunk-AADF progress, no
  material upload. A window started from a background shell may never be foreground. The bench must bypass
  the check.
- MonoGame `Game.InactiveSleepTime` (default 20 ms) sleeps every tick while inactive. Set it to `TimeSpan.Zero`.
- `Settings.Load()` reads `settings.json` from the **current directory** (`N/Settings.cs:135-139`); override every
  setting after it. `Content\oasis.cvox`, `imgui.ini` and `obj2voxel.exe` are also CWD-relative.
- The window: WinForms form, sizable border (`N/App.cs:75-79`). For exact 2560x1440 on a 2560x1440 monitor use
  `FormBorderStyle.None`, set `PreferredBackBufferWidth/Height`, `ApplyChanges()`. Windows clamps sizable
  windows to the screen.
- Dispatch sizes above 65535 groups: the code dispatches `W*H/64` groups in X (57,600 at 2560x1440, fine), but
  also `uniqueBlocks` groups (`WorldData.cs:204`), `nonUniformChunks` groups (`:207`) and 1.56M groups in
  `Helper` copies (`Helper.cs:47,74,102`) in X. D3D11 limits X to 65535; MonoGame does not split. The authors'
  large worlds only work if the NVIDIA driver accepts these oversized dispatches. Verify on this machine (the
  D3D11 debug layer would report them; a dropped dispatch here means missing voxel AADFs = correct but slow, or
  a zeroed CPU copy).
- Hide ImGui (`GuiHandler.ShowUi = false`) so it is not in the frame.

---

## 6. World loading, entry point

- `.cvox` loading: `ModelData.Load(path)` (`N/World/Model/ModelData.cs:181-258`): ZIP entry `data`; header
  `int version (3), int sizeX, sizeY, sizeZ, int typeCount, uint chunkCount, uint blockCount, uint voxelCount`;
  `typeCount` material records (`LoadVoxelType`, `:61-72`: NUL-terminated id, `Vector3 colorBase`,
  `Vector3 colorLayered`, `int materialBase`, `int materialLayer`, `float roughness`), each passed through
  `VoxelTypeHandler.ApplyVoxelType` (global table, deduplicated by id); then the raw `uint` chunk, block and
  voxel arrays (voxel array read in two parts above `0x1FFF0000` uints). Version < 3 pointer fix-up `:242-250`.
  The constructor calls `CreateDataForRender` (`:74-124`): remaps types to render indices on the CPU and uploads
  three model buffers.
- Model format = world format without AADFs: chunk `0` empty / `1<<30|type` / `2<<30|blockPtr`; block
  `0` / `1<<30|type` / `2<<30|voxelPtr` (uint units); voxel `0x8000|type`. Model chunk grid is
  `ceil(size/16)` per axis, x fastest.
- "Fit to Model" in code (bench path):
  ```csharp
  ModelData m = ModelData.Load(path);
  var gen = new WorldGeneratorModel(); gen.SetModel(m);
  App.worldHandler.ApplyAndGenerateNewWorldData(new WorldData(m.size, 4), gen);   // blocking
  ```
  (`UiHeaderBar.cs:41-48`, `WorldHandler.cs:57-62`). `GenerateWorld` is synchronous; when it returns,
  `worldData.isLoaded == true` and the model GPU buffers are disposed (`WorldData.cs:214-217`).
- "Generation finished" has two stages: (1) `GenerateWorld` returned (blocks, voxels and their AADFs done);
  (2) chunk-level AADFs converged: poll `worldData.boundHandler.boundQueueInfoGpu.GetData(BoundQueueInfo[96])`
  until every `size == 0` for a few consecutive frames (the read stalls the GPU, so only during the wait
  phase). Raise `boundHandler.maxGroupBoundDispatch` during the wait to converge faster
  (>65535 relies on the driver, 5.4). Bistro-8192 needs ~324 frames at the default.
- Console output: "Chunk Generation: N%" every 50 segments, "Construction time: X milliseconds"
  (`WorldData.cs:152-153,212`). Bistro-8192 has 31 x 9 x 32 = 8,928 segments, each with a blocking readback.
- Entry point and hooks:
  - `N/Program.cs:10-14`: `Main()` without arguments -> change to `Main(string[] args)` or use
    `Environment.GetCommandLineArgs()`. Parse `--bench` options into a static config here.
  - `App()` constructor (`N/App.cs:52-70`): back buffer size, vsync, fixed step -> apply bench resolution here.
  - `App.Initialize` (`:72-87`): after `Settings.Load()` override settings; set `InactiveSleepTime`.
  - `App.LoadContent` (`:89-96`) -> `WorldHandler.Initialize` (`WorldHandler.cs:29-35`): skip the Oasis load in
    bench mode and load the bench world instead (or create an empty small world and load in the first Update).
  - Per frame: a bench controller called at the top of `App.Update` (before `worldHandler.Update`, ignoring
    `IsActive`): sets camera pose, applies dynamic edits, steps the state machine
    `Load -> WaitAadf -> Warmup(30) -> Measure(200) -> Screenshot -> WriteJson -> Exit()`.
  - Render version: `Settings.data.render.version = X; WorldRender.ApplyRenderVersion(X);`
  - Existing scripted camera: `PathHandler` (camera path playback with `SetPos/SetDir`, F2, optional end
    screenshot). It is UI-driven; the bench controller is simpler.

---

## 7. Editing API and cost model

### 7.1 API (CPU side, `N/World/Data/EditingHandler.cs`)

- `uint ptr = editingHandler.getChunkDataToEdit(Point3 chunkPos)` (`:182-211`): first call per chunk per
  frame copies the chunk's current content into `editData` (2048 uints = 4096 voxels, block-major,
  `WorldData.FillChunkData`, `WorldData.cs:220-254`) and returns the offset; later calls return the same offset.
  Thread-safe through `_editDataLock`.
- Write voxels: `setVoxelData(ptr, voxelPosInChunk, type)` (`:228-242`), type = `0` (empty) or
  `0x8000 | renderIndex`; or write `editData` directly: block `b = bx + 4 by + 16 bz` owns uints
  `[ptr + 32 b, ptr + 32 b + 32)`, voxel `v = x + 4 y + 16 z` is half `v & 1` of uint `v / 2`.
  The tools do exactly this (`EditingToolCube.cs:77-101`).
- `processChunks()` (`:75-180`) runs automatically every frame from `WorldData.Update` -> `EditingHandler.Update`
  (`:72`). For each edited chunk (Parallel.For): hash the 64 blocks; uniform blocks inline; other blocks through
  `BlockHashingHandler.AddBlock` (CPU hash map, refcount, new voxel record via `WorldData.AddVoxels` if the content
  is new) **under one global lock**; old mixed blocks of the chunk released via `DeleteBlock` (refcount; frees the
  voxel slot at 0); uniform chunk inline, else `SetBlocks` (reuses the chunk's block slot). Appends to
  `ChangeHandler.changedVoxels` (33 uints per new voxel record), `changedBlocks` (65 uints per chunk),
  `changedChunks` (1 `Uint2`), and `SetChunk` marks the chunk's group if it changes between empty and non-empty
  (or stays empty) (`WorldData.cs:381-394`).
- `ChangeHandler.UpdateWorld()` (`N/World/Data/ChangeHandler.cs:69-255`), right after: CPU flood fill over groups
  within 7 group rings of every changed group (`:73-110`), CPU estimate of reset AADFs (`:118-174`), then GPU:
  `ApplyChunkChange` (1 thread per chunk), `ApplyBlockChange` (one 64-thread group per chunk: writes 64 blocks and
  recomputes in-chunk block AADFs), `ApplyVoxelChange` (one group per new voxel record: writes 32 uints and
  recomputes in-block voxel AADFs), `ApplyGroupChange` (one group per changed group: clamps chunk AADFs and puts the
  group back into the chunk-AADF queue at the lowest affected level) (`world/data/worldChange.fx`).
- Then `WorldBoundHandler.Update` continues the chunk-AADF queues (5 dispatch pairs per frame, 3.5). Chunk AADFs
  near edits are therefore conservative (smaller) for some frames: correct but slower traversal.

Granularity: **per chunk (16^3)**. Every touched chunk rehashes and re-uploads all 64 block entries; voxel data is
uploaded per new unique 4^3 block; chunk AADFs are recomputed per group (4^3 chunks) over the following frames.
Nothing is recomputed from raw voxels for the whole world; `chunkCalc.fx` is not used for edits.

### 7.2 Fractalcave slab toggle (72 x 128x4x128 per frame)

- A slab with x, z aligned to 16 and y aligned to 4 covers 8 x 1 x 8 = 64 chunks and, in each, a full
  16 x 4 x 16 layer = 16 whole blocks. 72 slabs -> up to **4,608 chunks per frame** (fewer if slabs share a chunk
  vertically: four 4-voxel slabs fit in one chunk layer).
- Case A, slabs set to a uniform state (all solid one type / all empty): the 16 blocks become uniform -> no new
  voxel records. Per frame: 4,608 x (copy 2048 uints + rehash 64 blocks + DeleteBlock pass), upload ~1.2 MB
  (`changedBlocks` 260 B per chunk), GPU `ApplyBlockChange` 4,608 groups. A chunk whose only content is the slab
  flips between empty and non-empty -> group flood fill and chunk-AADF requeue.
- Case B, voxel-wise XOR of a noise field: the 16 blocks per chunk stay mixed with new patterns ->
  up to 73,728 new voxel records per frame (132 B upload each, ~9.7 MB/frame), 73,728 `ApplyVoxelChange` groups,
  and 2 x 73,728 hash-map operations under the global lock (likely the CPU bottleneck).
- CPU estimate: 5-20 ms per frame (A), more for B because of the lock. GPU update estimate: well below 1 ms (A),
  ~1 ms (B), plus the constant 5 chunk-AADF dispatch pairs.
- Required fixes for this load:
  - Pre-size `editData` to `>= 2048 * chunksPerFrame` uints (initial 256K uints = 128 chunks, `EditingHandler.cs:38`).
    `getChunkDataToEdit` resizes it with `Array.Resize` while other Parallel.For threads may still write into the
    old array (lost writes).
  - `changedChunks` is a fixed array of 2,000,000 entries and is never resized (`ChangeHandler.cs:53`, write at
    `EditingHandler.cs:164-165`); `changedBlocks` resize happens under a lock but the write after it does not
    (`EditingHandler.cs:151-159`). Initial capacities (30,769 chunks, 151,515 voxel records) cover the load above.
  - Write `editData` with `Array.Fill`/`Span` per block instead of 4.7M `setVoxelData` calls.
  - Toggle types are render indices: take them from `voxelTypeHandler.typesById[id].renderIndex`.
- Timing: the edit CPU time = bench code + `processChunks` + CPU part of `UpdateWorld` (stopwatch around
  `WorldData.Update`); GPU update = timestamps around `ChangeHandler.UpdateWorld` + `WorldBoundHandler.Update`.
- Generating the scene: either write a v3 `.cvox` from Python (same writer as the converter) and load it with
  Fit to Model, or add a `WorldGenerator` subclass that fills `segmentVoxelBuffer` (format in
  `generatorModel.fx:54-72`: per chunk 2048 uints, block-major, `0x8000|type` per 16-bit voxel).
  `new WorldData(new Point3(384, 1024, 384), 2)` gives an exact-size world (128-voxel segments).

---

## 8. Memory readout

- Process VRAM: `SharpDX.DXGI 4.0.1` has `Adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local)`
  (verified). From the device: `dev.QueryInterface<SharpDX.DXGI.Device>().Adapter.QueryInterface<SharpDX.DXGI.Adapter3>()`;
  `CurrentUsage` gives the process total (includes swap chain and render targets). Read it before load, after
  generation, after AADF convergence.
- Structure bytes, per resource:
  - `StructuredBuffer` / `IndirectDrawBuffer` derive from `BufferResource`; its `Buffer` property
    (`SharpDX.Direct3D11.Buffer`) is **internal** -> reflection `GetProperty("Buffer", NonPublic|Instance)`, then
    `.Description.SizeInBytes`. Public alternative: `StructuredBuffer.ElementCount` x the struct size used at
    creation.
  - `Texture3D`: internal field `Texture._texture` (`SharpDX.Direct3D11.Resource`), or compute
    `Width*Height*Depth*8` (Rg64Uint) / `*4` (R32Uint).
  - `DynamicStructuredBuffer.GetBuffer()` is public.
- World structure (`memory_bytes.structure`): `worldData.dataChunkGpu`, `dataBlockGpu.GetBuffer()`,
  `dataVoxelGpu.GetBuffer()`, `voxelTypeHandler.typesRenderGpu.GetBuffer()`. Report separately (overhead, not
  needed for tracing): `blockHashingHandler.mapGpu`, `boundHandler.{boundQueueInfoGpu, boundRefinedInfoGpu,
  boundGroupQueuesGpu, boundGroupMasksGpu, boundGroupQueueDispatchCount}`, `segmentVoxelBuffer`,
  `blockVoxelCountGpu`, `entityHandler.{entityVoxelDataGpu, entityInstancesHistoryGpu, entityChunkInstancesGpu}`
  + its private dynamic buffers, `changeHandler` private buffers.
  Note that allocated pool sizes are 2-4x the used sizes; report both. Used sizes: `blockCount * 4`,
  `voxelCount * 2` (`WorldData.DrawDebugInfo`, `WorldData.cs:95-107`).
- A reflection walker over these objects that sums every field of type `BufferResource`, `Texture`,
  `DynamicStructuredBuffer` is the least code.

---

## 9. Converter `Research/cxw-to-cvox`

- Input (`convert.py:27-49`): magic `CXLS`, **version must be 6**, flags 1; exactly one entity, identity
  transform, `entityFlags == 2` (static, no body); compact VXS2 sectors with only the Block slot
  (`:58-61`). Packed colour codes: `0x8000|RGB555` opaque, `0x4000|RGB444|level` emissive, other codes must be
  glass ids in `Caelix/Tools~/RawColor/glass-palette.json` (`:71-87`).
- Placement: shifts the minimum **sector** corner to 0 (`origin = min sector * 128`) and pads each axis to a
  multiple of 256 (`size = ceil((maxSector*128+128-origin)/256)*256`, `:47-48`). The JSON report has
  `source_origin` (= minus the `world_offset`).
- Materials (`material()`, `:71-87`): opaque -> diffuse RGB (no gamma); emissive -> `materialBase 1`,
  `colorLayered.x = emission_scale * 2^(3*level)` (default 0.4); glass -> perfect mirror (`materialBase 3`) or
  rough metal (`2`, roughness `1 - smoothness`), reflectance `0.8 * tint` via inverse Schlick. Material ids
  `cxw_XXXX`, one per used code, <= 32767.
- Output: chunk array dense (`ceil(size/16)` per axis), 64 block entries per non-empty chunk, one 32-uint voxel
  record per mixed block **without deduplication** (`:118-130`). Uniform-solid blocks are inline. Pointer
  check: voxel uints < 2^30 (`:126`), blocks < 2^30, voxel count < 2^32 (`:134`). ZIP64 deflate level 6.
- Validation: re-reads the archive and compares every voxel of every sector (`validate`, `:162-209`).
- Bistro-8192: accepted (v6). Output 7936 x 2304 x 8192, 62.9M block entries, 17.74M voxel records
  (567.7M uints, ~2.27 GB uncompressed). Above `0x1FFF0000` uints, so NAADF allocates the 4.29 GB model buffer.
- Rootfall: **rejected** (v8). Needed changes: accept versions 6-8 and skip the 29-byte world record before
  `entityCount` when version >= 7 (entity record layout is unchanged); add block deduplication (hash the 128-byte
  records; required because 35.74M records exceed 2^30 uints); check that its 2 glass codes are in the palette.
  Output would be 4608 x 2560 x 5632, origin (-2432, 0, -2432).
- Both scenes fit the v3 format limits (<= 16384 per axis, < 32768 materials) once dedup is in; the binding limit
  is NAADF's 4 GiB voxel pool (rootfall uses 91 % of it).
- Runtime for 8K (estimate, not measured): three full passes over the sectors (histogram, conversion,
  validation). One numpy pass over bistro-8192 took 78 s in this session; rootfall 239 s. The validation pass builds
  `np.indices((128,128,128))` per sector and decodes every voxel with fancy indexing, so it is the slow part.
  Expect ~15-40 min for bistro-8192 and longer for rootfall. Temporary disk in the output folder: blocks + voxels
  + the uncompressed archive copy for validation, ~5-6 GB for bistro-8192, ~10 GB for rootfall.

---

## 10. Protocol mapping summary

| Protocol | NAADF |
| --- | --- |
| P (primary only) | Albedo version; traced work = `FirstHit` pass. Raise `MAX_RAY_STEPS_PRIMARY` (120) to 2000 for parity, or report the cap. Black sky on miss. |
| B2 (primary + 2 diffuse, sky only, 1 spp) | PathTracer version, `bounceCount 2`, `maxSamples 1`, jitter off, atmosphere interaction off, sun NEE removed (shader edit), optional cosine sampling. Traced work = `FirstHit + GlobalIlum`. |
| resolution 2560x1440, FOV 60 | `PreferredBackBuffer*`, borderless window; `Settings.data.general.fov = 60` (vertical). |
| pose | `SetPos(pos + world_offset)`, `SetDir(forward(pitch, yaw))`; flip the PNG horizontally to compare. |
| GPU time | D3D11 timestamp + disjoint queries via reflected `_d3dDevice/_d3dContext`. |
| rays | nominal (W*H*(1+bounces)); counted only with an added shader counter. |
| iterations per ray | `showSteps` diagnostic frame (Albedo) + readback. |
| memory | DXGI `QueryVideoMemoryInfo` + per-buffer reflection sizes. |
| construction | `ModelData.Load` time (file to CPU) + `GenerateWorld` time + AADF convergence frames. |
| dynamic | `EditingHandler` per-chunk API, CPU stopwatch + GPU timestamps around `UpdateWorld` and `WorldBoundHandler.Update`. |

---

## 11. Open risks

1. **D3D11 resource size.** The code relies on single buffers up to `0xFFFF0000` bytes (voxel pool, 4.29 GB model
   buffer). Microsoft's D3D11 resource-limit table lists 2048 MB as the required size; larger buffers depend on the
   driver. The authors' "16384^3" claim suggests the NVIDIA driver accepts them. Verify early with one 4 GB
   `StructuredBuffer` on the RTX 5080.
2. **Dispatches above 65535 groups in X** (unique blocks, non-uniform chunks, 100M-element copies). Same driver
   dependency. If the driver drops them, voxel AADFs are missing (slow but correct) or CPU copies are zero
   (corrupt edits). Check with the D3D11 debug layer or by comparing a CPU copy.
3. **Peak VRAM during generation** ~13-14 GB for bistro-8192 and rootfall with current code. D3D11 paging would make
   generation slow, not fail. Apply the reductions in 3a before the first run.
4. **Rootfall does not convert** with the current converter (v8 header, voxel pointer overflow). Needs v7/v8 support
   and block deduplication. After dedup it uses 91 % of NAADF's 4 GiB voxel pool.
5. **Window focus.** `IsActive` gating and `InactiveSleepTime` stop or slow the loop when the window is not focused.
6. **Chunk AADFs converge over frames.** Measuring before convergence understates NAADF. Wait for empty queues.
   In the dynamic scene, edits re-queue groups every frame, so traversal runs with partly reset chunk AADFs by design.
7. **Step caps.** Albedo/Base primary rays stop after 120 iterations and count as misses. PathTracer uses 2000.
   Decide the cap for P and state it in `notes`.
8. **Not a pure 1-primary + 2-diffuse path tracer**: mirror chains in FirstHit (up to 4 segments) for converted glass,
   uniform (not cosine) hemisphere sampling, sky from a precomputed LUT, no RR. Document deviations in `notes`.
9. **Non-determinism**: unseeded `System.Random` for `randValues` and PT jitter; seed it for reproducible screenshots.
10. **Edit-path races**: `editData` and `changedBlocks` resize while Parallel.For writes; `changedChunks` is fixed
    at 2M entries. Pre-size. The CPU hash-map lock serialises block hashing.
11. `worldChange.fx:35` initialises a `groupshared` array (`= {31,31,31}`); HLSL ignores groupshared initialisers,
    so `ApplyGroupChange` reads undefined values. Probably harmless (the min with 0 re-queues more work) but it can
    make post-edit AADF recovery slower than intended.
12. **Handedness**: the image is mirrored versus Caelix (section 4). Forgetting the flip makes side-by-side pose
    verification fail.
13. Build is unverified offline (`dotnet tool restore` with SDK 10.0.400). The prebuilt is the 2026-02-20 release and
    differs from HEAD in the Base renderer only, but it cannot run the bench.
14. MonoGame `Effect` parameter names: `Parameters["x"].SetValue` on a name the compiler optimised out throws a
    `NullReferenceException`; the code uses `?.` only in some places. New shader parameters (e.g. `useSunNee`) must
    be used by the shader or accessed with `?.`.
