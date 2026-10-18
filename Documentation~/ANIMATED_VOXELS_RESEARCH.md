# Animated Voxels Research: How John Lin Does It, and How Caelix Can

**Date:** 2026-10-18
**Status:** research + design proposal. Nothing in here is implemented yet.
**Scope:** data storage and rendering of animated voxel content (characters, vegetation, moving
objects) and sub-pixel ("micro") voxels, with a concrete plan for Caelix. Networking is out of
scope; everything is assumed to run on one machine.

Reference point from the current engine: Caelix sustains roughly 36k bricks whose *voxel data*
changes every frame at 60 FPS through the ray-traced path. That number is used throughout as the
baseline to beat.

---

## 1. Summary

1. **John Lin has never published how his animation works.** The two videos are his 2020
   "Animated Voxel Trees - Detail Enhancement Preview" and his May 2021 "New Voxel Engine Reveal -
   Crystal Islands Experiment" (the micro-voxel engine). What he *has* stated: a Vulkan hardware
   ray-traced path tracer on an RTX 2070, an 8x linear (512x volumetric) detail increase in the
   2021 rewrite, polygon vegetation assets (PlantFactory, Megascans) converted to voxels, "breeze
   animations" on those vegetation *models*, and a temporal-only denoiser. He shelved the
   micro-voxel direction in June 2021 after audience feedback and has published no engine update
   since. The best-supported community reading is that the animated vegetation is a skinned or
   wind-deformed polygon asset **re-voxelized on the GPU every frame into an object-aligned grid**,
   then traced like a Teardown shape. This is inference, not confirmed. See section 2.

2. **The industry has converged on one answer for moving voxel objects: do not touch the voxels,
   move the ray.** A moving object keeps its voxel data static and is a transformed instance (a
   TLAS instance of a per-object BLAS of brick AABBs, or a rasterized OBB that ray-marches a 3D
   texture). Teardown, Gustafsson's 2024 brickmap prototype, Gabe Rundlett's gvox_engine, and the
   Espe et al. SVO paper all do this. Smooth deformation extends the same idea: warp the ray into
   rest-pose space (shell maps, tetrahedral cages) rather than warping the voxels. See section 3.

3. **Caelix already has the right primitive** (one RTAS instance per sector, `UpdateInstanceTransform`
   for rigid motion, a brick-local DDA in the intersection shader). What it lacks is a way to
   express *animation* that does not go through the voxel-edit path, which costs CPU neighbor
   scans, a ~1.1 KB per brick re-upload, and a BLAS rebuild. The 36k-bricks-per-frame figure is a
   measure of how fast the engine can *rewrite voxels*, which is the wrong tool for animation.

4. **Proposal (section 6), in order of payoff per effort:**
   - **Tier 0, fix the edit path:** stop reallocating the AABB `GraphicsBuffer` on every bounds
     change, upload only dirty AABB ranges, parallelize the render-data job per brick, and skip
     per-voxel face culling for entities flagged as animated. Expect 2x to 4x on the current number.
   - **Tier 1, rigid-part rigs (bone = instance):** a character is N static brick volumes with a
     shared BLAS per part and one TLAS instance per bone. Animation cost becomes O(bones) matrix
     updates and a TLAS rebuild, independent of voxel count. This alone covers Teardown-class
     content and most "pixel art but 3D" characters.
   - **Tier 2, ray warping in the intersection shader:** per-instance deformation (wind sway,
     bend, squash) applied as an inverse warp on the ray before the brick DDA, with AABBs inflated
     by the maximum displacement. Zero data traffic. This is the cheapest route to "animated trees".
   - **Tier 3, GPU re-voxelization of skinned meshes:** compute-shader voxelization of a skinned
     mesh straight into a brick volume on the GPU, BLAS rebuilt as dynamic geometry. Needed only for
     true smooth skinning or imported polygon animation (the Lin-style pipeline).
   - **Sub-pixel voxels:** per-brick colour/occupancy mips with a ray-footprint LOD stop in the
     DDA, plus downsampled LOD sectors as separate instances chosen by distance (optionally
     stochastically via instance masks). TAA/temporal accumulation on top of prefiltering, never
     instead of it.

---

## 2. What is actually known about John Lin's engine

Sources were reachable only as search-engine snippets in this session (YouTube, X, his blog, Hacker
News and Beyond3D were all blocked), so quotes may be truncated. Handle: X `@ProgrammerLin`,
GitHub `Lin20`, blog voxely.net.

### 2.1 Video identification

| Video | Identified as | Date |
| --- | --- | --- |
| `BObFTsNeeGc` | "Animated Voxel Trees - Detail Enhancement Preview" | ~Sept 2020 |
| `8ptH79R53c0` | "New Voxel Engine Reveal - Crystal Islands Experiment" (the micro-voxel engine) | May 2021 |

Companion tweet for the first: "No voxel is safe from the wind anymore, thanks to finally
implementing the #1 most requested feature - animated trees!" (Sept 26, 2020,
https://twitter.com/ProgrammerLin/status/1309866612065878017). No implementation detail.

### 2.2 Statements by Lin (primary)

- Renderer: "a voxel ray tracer that uses the Nvidia's ray tracing extension for vulkan. Written in
  C++, running on a RTX 2070" (Dec 2019, https://x.com/ProgrammerLin/status/1207033076674514945);
  "100% #rtx" (https://x.com/ProgrammerLin/status/1209000354676760576); real-time path tracing,
  later "noiseless and run 20x faster (60fps HD!)" (Mar 2020,
  https://x.com/ProgrammerLin/status/1235967097739423745).
- 2021 rewrite goals (video description, via https://cramgaming.com/new-voxel-engine-reveal-crystal-islands-experiment-57455/):
  "An 8x (512 cubic) detail increase with animation support, high compression rates, and per-voxel
  material attributes"; "fast collision detection"; "full fracture and mutation of objects"; an asset
  pipeline that converts Quixel Megascans and PlantFactory/PlantCatalog vegetation into voxels;
  "A 10x speedup in rendering over the previous engine"; known issue "the path traced denoiser is
  lacking a spatial filter".
- "I've been working on a 3rd revision to my engine, which shrinks the #voxels and gives an 8x
  (512 cubic) detail increase" (Mar 2021, https://x.com/ProgrammerLin/status/1370405170392092674).
- "Gentle breeze animations and leaf palettes are now working for these new higher res #voxel tree
  (and vegetation) models." (Apr 2021, profile snippet). This is the only direct statement that the
  micro-voxel vegetation was animated, and it calls them *models*.
- Building system: "volumetrically materialized SDFs with a resizable optional grid, arbitrary
  rotations & scaling, and composite shapes" (Nov 2020,
  https://x.com/ProgrammerLin/status/1331647547408990210). Rigid bodies: "Voxel billiards, anyone?
  Cubes roll surprisingly well." (Sept 2021, https://x.com/ProgrammerLin/status/1442010607205576706).
- Outcome: "I've decided to shelf the micro-voxel goal. Instead, the optimizations gained from
  those experiments will be put forth towards the 'pixel art' style that so many people said they
  preferred." (June 2021, https://x.com/programmerlin/status/1401347286429442048).
- Blog "The Perfect Voxel Engine" (Sept 2021, https://voxely.net/blog/the-perfect-voxel-engine/):
  argues for using many voxel formats, one per job (rendering, collision, editing, characters),
  and says "The term '(micro) voxel engine' is basically synonymous with vaporware." It does not
  describe his animation or storage implementation.

### 2.3 Inferences by others (secondhand, unconfirmed)

- Beyond3D user Warrick on the micro-voxel demo: Lin "is simply voxelising polygon geometry on the
  fly to an object aligned grid for the skinned vegetation and then using raycasting similar to
  Teardown", with "the artifacts you would expect from conservative voxel rasterisation of the
  polygons on the GPU" (https://forum.beyond3d.com/threads/polygons-voxels-sdfs-what-will-our-geometry-be-made-of-in-the-future.59982/page-12).
  This fits the stated Megascans/PlantFactory pipeline and the "object intersections" bug.
- HN user Lichtso: in Lin's engine "all voxels stay axis and grid aligned, even in animations"
  (https://news.ycombinator.com/item?id=47239042). Consistent with per-frame voxelization onto a
  grid; conflicts with "arbitrary rotations" for built objects unless those are re-materialized.
- A 2019 NVIDIA forum thread by a user "Lin20" (same handle as his GitHub, identity unverified)
  describes rendering voxels through a custom AABB intersection shader on RTX and being told only
  BVH traversal is hardware accelerated
  (https://forums.developer.nvidia.com/t/using-rtx-acceleration-for-voxel-tracing/74007). If this
  is Lin, his 2019 engine fed voxels to RTX as procedural AABBs, the same model Caelix uses.

### 2.4 What this means for us

Nothing about Lin's pipeline is special enough to copy verbatim, and nothing is documented well
enough to copy anyway. The techniques he most plausibly used (hardware RT over per-object volumes,
per-frame GPU voxelization of deformed polygon assets, heavy temporal accumulation) are all covered
by better-documented sources below. His own retrospective conclusion, that micro-voxels were not
worth it for his audience, is also worth weighing before Caelix commits to sub-pixel voxels.

---

## 3. How animated voxel content is stored and rendered elsewhere

### 3.1 Rigid volumes with a transform (the dominant approach)

**Teardown (shipped).** The world is thousands of small volumes ("shapes"), each a 3D texture at
1 byte per voxel (palette index) with its own rigid transform; debris and moving objects are just
more shapes. Primary visibility: rasterize each shape's oriented bounding box (back faces), and in
the fragment shader ray-march the shape's 3D texture with a mip-assisted DDA, writing depth.
Secondary rays (AO, soft shadows, specular occlusion; no bounce GI) trace a separate world-space
1-bit occupancy volume with two coarser mips. Moving shapes are stamped into that world grid
axis-aligned each frame, which is why reflections of held objects look grid-snapped and why
Gustafsson says the shadow volume "made it hard to get swaying trees to light correctly".
Characters and ragdolls are rigid shapes under physics joints. Sources:
https://blog.voxagon.se/2018/10/17/from-screen-space-to-voxel-space.html,
https://acko.net/blog/teardown-frame-teardown/,
https://juandiegomontoya.github.io/teardown_breakdown.html.

**Gustafsson's new engine (2024-25).** Brickmap format ("large, sparse objects, unique properties
for each voxel, really fast scene updates"), path tracer that "relies heavily on hardware ray
tracing using intersection shaders, avoiding triangles entirely", and the shadow volume dropped,
giving "unlimited world size, sharp shadows, no light leakage and vast amounts of moving objects at
a very small cost". He also mentions "deformable voxels" with an approximate inverse map from a
unit cube to a deformed cuboid, details unpublished.
https://blog.voxagon.se/2024/12/29/year-summary.html

**gvox_engine (Gabe Rundlett, code read).** `VoxelObject` = sparse grid of 8^3 bricks with a
512-bit occupancy mask and 512 packed voxels per brick; one AABB per non-empty brick forms a BLAS
in object space; pose (position, quaternion, uniform scale) lives only in the TLAS instance
transform. Intersection shader: `gl_PrimitiveID` selects the brick, the ray is taken into brick
space via `gl_ObjectRayOrigin/Direction`, bitmask DDA, `reportIntersection`. Edits re-upload the
whole object and rebuild its BLAS. No skinning API.
https://github.com/GabeRundlett/gvox_engine (branch `rewrite`),
https://twitter.com/RundlettGabe/status/1781349885603668035

**Espe, Gjermundnes, Hendseth 2019, "Efficient Animation of Sparse Voxel Octrees for Real-Time Ray
Tracing".** Static SVO per model; rigid transforms (rotation, translation, anisotropic scale) at
the model level; the ray is transformed into model space after a bounding-volume test. "Does not
substantially slow down rendering compared to static SVOs". Deformation not supported; a character
is one model per bone segment. https://arxiv.org/abs/1911.06001

**Yalciner & Sahillioglu 2019, "Voxel transformation".** For a world-space GI grid, do not
re-voxelize dynamic meshes; transform pre-voxelized object data into the grid each frame, with a
filtering pass to repair holes and reduce flicker. Relevant if Caelix ever wants a unified
world-space occupancy/GI volume. https://link.springer.com/10.1007/s11554-019-00919-1

**Game-side rigs (mesh rendered, same data model).** Veloren: a skeleton of bones, each with its own
.vox part and pivot, animated by per-bone procedural equations
(https://veloren.net/blog/devblog-13/). AniVoxel and VoxEdit: grid-constrained skeletal rigs of
rigid parts. This is the "pixel art but 3D" look Lin's audience asked him to keep.

### 3.2 Deform the ray, not the voxels (smooth deformation that stays ray-traceable)

**Groen, Rensen, Eisemann, Molenaar, "Spline Based Shell Mapping for Smooth Voxel Deformation"
(TU Delft).** Volume stays static. A spline map (quad base, two spanning vectors, one cubic
Hermite spline) defines the deformation; rays are marched in deformed space and each sample is
mapped back to texture space, so the undeformed volume is sampled. Tight entry/exit test against
the spline map, distance-field step correction. 256^3 to 512^3 volumes at 4.4 to 11 ms on an RX 480
(2x faster with the distance field). Limits: monotonic splines, one spline shape per edge, quad
base. "Integrates into traditional ray-traced voxel scenes with no further modifications."
https://github.com/kr4b/Voxel-Animation

**Gruen, Benthin, Kern, McAllister (AMD, HPG 2026), "Ray Tracing Massive Amounts of Animated
Geometry".** A low-resolution tetrahedral cage is animated; rays entering a tet are transformed
piecewise-linearly into rest-pose space where a *static* acceleration structure is traced. Cost
scales with cage size, not geometry density: 585M uniquely animated triangles at 60 FPS, 1080p,
RX 9070 XT; update cost 300 ms to 3.3 ms. Written for triangles, but the mechanism is agnostic:
replace "static BVH" with "static brick volume".
https://gpuopen.com/learn/ray-tracing-massive-amounts-animated-geometry/

Lineage: Kurzion & Yagel 1995 ray deflectors; shell maps (Porumbescu 2005, Jeschke 2007);
GigaVoxels shell deformation (https://hal-imt.archives-ouvertes.fr/LJK_GI_ARTIS/hal-00840637v1);
Seyb et al. 2019 non-linear sphere tracing for deformed SDFs.

### 3.3 Re-voxelize a skinned mesh every frame

- Crassin et al. 2011 (voxel cone tracing) re-voxelize moving objects into the octree each frame via
  conservative rasterization; the OpenGL Insights chapter puts a 16K-triangle animated hand at about
  5.5 ms on a GTX 480 (https://www.icare3d.org/research/OpenGLInsights-SparseVoxelization.pdf).
- Cosin Ayerbe, Poulin, Patow 2024/25 (CGF) keep static and dynamic objects in separate voxel sets
  and re-voxelize only the dynamic ones plus a neighbourhood, to avoid temporal flicker
  (https://diglib.eg.org/handle/10.1111/cgf15262).
- Kraaijeveld et al. 2025/26 voxelize dynamic line sets per frame into an occupancy pyramid with
  frustum culling so acceleration structures are only built for visible voxels
  (https://arxiv.org/abs/2510.09081).
- Unity: `mattatz/unity-voxel` voxelizes a `SkinnedMeshRenderer` each frame in a compute shader
  (https://github.com/mattatz/unity-voxel).

Limitations: output is axis-aligned to whatever grid you voxelize into (shimmer on rotation unless
object-aligned), conservative rasterization over-fills thin parts, and the result must be rebuilt
into an acceleration structure every frame. This is the most likely mechanism behind Lin's
vegetation and explains his "object intersections" artifacts.

### 3.4 Baked voxel frames

One volume per keyframe, swapped per frame (common in hobby engines and marketplace assets,
e.g. https://www.fab.com/listings/097a2856-0f27-4632-bc27-32c8f0cc4e1f). Trivially ray-traceable
(each frame is another static BLAS) and zero per-frame work beyond swapping an instance. Memory is
`frames x bricks x 1 KB`. Fine for a few hero animations, hopeless for a crowd with blended clips.

### 3.5 Per-voxel skinning (what not to do)

Bautembach 2011, "Animated Sparse Voxel Octrees": per-voxel bone weights, voxels transformed at
render time. The hierarchy is destroyed by the deformation, so the result must be splatted, not
ray traced; holes are patched by enlarging voxels. ~40 FPS for a 3M-voxel model at 800x800 in
2011. https://github.com/denniskb/asvo . Not compatible with a ray-traced pipeline.

### 3.6 GPU brick data structures and update paths

- **VoxelRT benchmarks (dubiousconst282).** XBrickMap (sectors of 4^3 bricks, 8^3 bricks with 512-bit
  masks) vs Tree64 ("contree", 64-bit child masks) vs ESVO vs BVH on an integrated GPU: Tree64 and
  BVH edge out the brick grid by ~10% on primary rays (Sponza 2k: 182.6 vs 165.6 Mrays/s); brick
  grids win on O(1) edits and simple upload; trees are easier to LOD. Bitmask-driven marching was
  "simpler and faster than DDA variants". https://github.com/dubiousconst282/VoxelRT
- **stijnherfst/BrickMap (CUDA).** 8^3 bricks, 16^3 superchunks so brick indices fit 12 bits, per
  brick LOD occupancy 8^3 / 2^3 / 1^3, ray-driven streaming. https://github.com/stijnherfst/BrickMap
- **Upload patterns.** gvox: whole-object re-upload through a fresh staging buffer. vkguide
  "Ascendant": CPU staging then a compute-shader scatter into one GPU gigabuffer
  (https://vkguide.dev/docs/ascendant/ascendant_geometry/). Vorxel devlogs: GPU-to-GPU copies are
  faster than CPU-to-GPU on discrete cards; move to sparse per-brick uploads
  (https://teknologicus.itch.io/vorxel). tim-oster/voxel-rs: one persistent coherent buffer with
  ring-style region reuse.

Caelix's 8^3 bricks in 16^3-brick sectors with a 512-bit occupancy mask already matches
XBrickMap, BrickMap and Gustafsson's new format closely. The format is not the problem.

---

## 4. Sub-pixel (micro) voxels

The question is anti-aliasing and LOD, not storage.

- **Prefilter, then pick LOD from the ray footprint.** Heitz & Neyret 2012 (HPG) store per-voxel
  prefiltered appearance (coverage, albedo, a Gaussian micro-normal distribution) and cone-trace,
  choosing the level whose voxel size matches the pixel footprint; per-pixel cost is roughly
  constant across scales (https://hal.archives-ouvertes.fr/hal-00704461). GigaVoxels does the same
  with mipmapped bricks. Fabre et al. 2026 add hierarchical SGGX clustering for glossy
  microgeometry (https://arxiv.org/abs/2604.13191).
- **Occupancy mips for traversal.** BrickMap's 8^3/2^3/1^3 occupancy; Teardown's 2 extra mips for
  sparse stepping. Caelix already has the 2x2x2-of-4^3 coarse level in the brick record.
- **LOD selection and transitions.** Avoyd: stop descending the octree for distant volumes, with
  hysteresis on the switch distance to kill flicker, and material at coarse LOD "sampled from the most
  likely visible face" (https://www.enkisoftware.com/changelog-avoyd). NVIDIA stochastic LOD with
  DXR: put both LODs in the TLAS and pick per ray via instance mask
  (https://developer.nvidia.com/blog/implementing-stochastic-lod-with-microsoft-dxr).
- **TAA caveat.** Temporal supersampling turns spatial aliasing into flicker unless the geometry is
  prefiltered (Salvi, GDC 2016). Several hobby voxel path tracers report accumulation and TAA
  fighting each other. Prefiltering first, temporal on top.
- **Lin's own result.** He hit the look (barely visible individual voxels on an RTX 2070 with
  temporal-only denoising) and then abandoned it because the audience preferred chunky voxels.

---

## 5. Caelix today: where the 36k bricks per frame go

The ray-traced path, per tick (`CaelixRenderer.Tick`, `Runtime/Rendering/CaelixRenderer.cs:288`):

1. For every sector of every entity, `GenerateSectorRenderDataJob` (one `IJob` per sector,
   `Runtime/Rendering/Jobs/SectorRenderer.Jobs.cs:83`) scans all 4096 brick require-update flags,
   and for each flagged allocated brick (`ProcessBrick`, line 115) rebuilds the GPU brick record:
   512 voxels, each with up to 6 neighbour reads through `SectorNeighborhoodReaderHelper` for face
   culling (`GetRendererBlockData`, line 293), plus the 512-bit occupancy and tight bounds.
2. The brick record is 274 words = 1096 bytes (`SectorRenderer.BRICK_DATA_LENGTH`,
   `Runtime/Rendering/SectorRenderer.cs:50`): 2 info words, 16 occupancy words, 256 words of packed
   16-bit block ids.
3. Upload (`ApplyCompletedRenderJob`): the dirty brick range is uploaded with `SetData`
   (line 356). If any tight AABB changed, the whole 4096-entry AABB `GraphicsBuffer` is **disposed
   and re-created** (line 330) and uploaded in full (line 348), because Unity builds static AABB
   geometry once and ignores later buffer writes.
4. `RenderModifyAS` then does `RemoveInstance` + `AddInstance` (lines 459-460) so the BLAS is
   rebuilt from the new buffer, and the render pass rebuilds the TLAS every frame
   (`Passes/CaelixGBufferPass.cs:222`).
5. Rigid motion of a whole entity is already cheap: `UpdateInstanceTransform` (line 467) plus a
   property-block write of `_PrevObjectToWorld` for motion vectors (line 448).

Cost model for 36k changed bricks per frame (estimates, not measurements):

| Stage | Per frame | Note |
| --- | --- | --- |
| Voxel evaluations in the job | 36k x 512 = 18.4M | each with up to 6 neighbour reads, so ~100M block reads |
| Brick upload | 36k x 1096 B = 39.5 MB | 2.4 GB/s at 60 FPS, via `SetData` staging copies |
| AABB upload | 98 KB per dirty sector, full | plus a `GraphicsBuffer` alloc/free per dirty sector |
| BLAS builds | one per dirty sector | from scratch, `PreferFastTrace`, as static geometry |
| TLAS build | 1 | every frame regardless |

Two observations follow:

- The CPU job and the upload both scale with *voxels rewritten*, and the AABB path adds allocation
  churn on top. These are the bottlenecks of an edit pipeline, and they are what the 36k figure is
  really measuring.
- Animation that does not change voxels (rigid parts, ray warps) bypasses every row of the table
  except the TLAS build, which is paid anyway. That is the whole argument of this document.

Also worth knowing: face culling only buys anything for the ray tracer when it empties an entire
interior brick (fewer AABBs, less brick data). For a surface-heavy animated object it buys nothing
and costs 6 reads per voxel. A brick-level "fully enclosed" test on the 512-bit masks of a brick and
its 6 neighbours gives the same saving for opaque content at a fraction of the cost. Transparent
voxels still need the per-face neighbour test (`faceMask`), so keep that path for them.

---

## 6. Proposal for Caelix

### 6.0 Tier 0: make the existing edit path cheaper (days)

Pure engineering on `SectorRenderer`, no contract changes.

1. **Stop recreating the AABB buffer.** Allocate it once per sector and register the instance with
   `dynamicGeometry = true` only for sectors that actually change bounds this tick (the code already
   tracks `wantsDynamicGeometry` / `settles`; the comment at `SectorRenderer.cs:417` explains the
   trade-off). Upload only the dirty AABB range. This also removes the `AABBconfig = default` and
   `RemoveInstance/AddInstance` round trip for bounds-only changes.
2. **Parallelize per brick, not per sector.** The job is `IJob` per sector; a sector with hundreds of
   dirty bricks runs on one worker. An `IJobParallelFor` over the collected dirty-brick list (the
   world tick already builds one, `BrickCollector`) spreads 36k bricks over all workers.
3. **Skip per-voxel face culling for animated/opaque entities.** Use a brick-level enclosed test on
   the occupancy aux masks (`BrickBitmask`) instead. Keep the per-face path for transparent bricks.
4. **Upload path.** Coalesce dirty bricks into a few large `SetData` ranges per sector (already
   min/max based), or move to a persistent upload buffer with a compute scatter so the CPU does one
   memcpy per frame. GPU-side scatter is the pattern the community converged on.

Expected outcome: 2x to 4x on the "bricks rewritten per frame" number, and a flatter frame time.
This is worth doing regardless of the tiers below because destruction and automata will always
need the edit path.

### 6.1 Tier 1: rigid-part rigs, bone = TLAS instance (the main deliverable)

**Data model.** A `VoxelRig` asset is a set of *parts*, each a small static brick volume (sectors of
its own, or a compact single-sector form), plus a skeleton: parent index, local pivot, and a bind
offset per part. Animation is a stream of per-bone local transforms (keyframes, procedural, or
retargeted from Mixamo-style clips, piecewise-rigid). A `VoxelRigInstance` in the scene holds the
pose and nothing else.

**Rendering.** Each part gets one BLAS of brick AABBs, built once. Each rig instance adds one TLAS
instance *per part*, transform = `rigRoot x bone[b] x bindOffset[b]`. Per frame the renderer updates
N matrices with `UpdateInstanceTransform` and the per-instance `_PrevObjectToWorld` (so motion
vectors and the denoiser keep working exactly as they do for moving entities today). No brick data
moves, no BLAS builds.

**Instancing.** Many rig instances of the same model should share the part BLASes and the brick
buffers. Unity's `RayTracingAABBsInstanceConfig` is documented as a reusable config and Caelix's own
code notes "Remove+Add reuses the cached BLAS", which suggests Unity caches the BLAS per AABB
buffer, but this is **not confirmed** by documentation reachable in this session. Experiment E1
below settles it. If Unity does not share, fall back to one BLAS per part per *model* and accept
duplicate TLAS instances pointing at it (the TLAS instance count is not the problem; the BLAS count
would be).

**Cost.** For a 20-bone character: 20 matrix updates and 20 TLAS instances per frame. A crowd of 500
characters = 10k TLAS instances, a TLAS build of that size is a sub-millisecond affair on current
GPUs. Compare: a 48^3 character that is *rewritten* per frame costs ~216 bricks x 1096 B = 237 KB
of upload and one BLAS build. 36k bricks per frame buys about 170 such characters through the edit
path; the rig path is bounded by TLAS size, not by voxels.

**Fit with engine contracts (AGENTS.md).**

- Scale stays exactly 1; bones are rotation + translation only. Squash/stretch is Tier 2.
- Parts are off-grid when rotated, exactly like today's moving rigid bodies. Local voxel data stays
  authoritative; alien reads treat each part as an entity view. To keep the automata stage cheap,
  rigs should be flagged so their parts are excluded from alien propagation unless a gameplay system
  opts them in (a rig is kinematic, not simulated).
- Physics: a rig is a kinematic compound. If a part must be destructible, detach it into an ordinary
  `VoxelEntity` (the authoring tool already has "detached entity" creation).
- Sector granularity: today a `SectorRenderer` allocates a 4096-slot AABB buffer (98 KB) and a full
  brick index table per sector. A 16^3 part would waste most of that. Add a compact path where the
  AABB buffer is sized to the part's brick count, or let parts share one pooled AABB/brick buffer per
  model with `aabbOffset`/`aabbCount` per part (the config supports both fields).

**Why this first.** It is the approach Teardown ships, the approach Gustafsson, Rundlett and Espe
all chose for hardware RT, and it matches the "pixel art but 3D" style Lin's audience asked him to
keep. It reuses Caelix's existing instance path with no shader changes.

### 6.2 Tier 2: ray warping in the intersection shader (wind, bend, squash)

For deformations that are smooth and bounded (tree sway, grass, cloth-ish flutter, cartoon
squash), keep the voxels static and warp the ray:

1. Per instance, publish a small deformation descriptor through the property block (or a structured
   buffer indexed by `InstanceID()`): e.g. a sway axis, phase, amplitude and a height falloff, or the
   four Hermite spline control values of a shell map, or a per-part affine "bend" matrix blended by
   height.
2. In `CaelixTraceBrickPrimitive` (`CaelixBrickTrace.hlsl:358`), after taking `ObjectRayOrigin/
   Direction`, apply the inverse warp to the ray (for small deformations a per-brick local affine
   approximation is enough: evaluate the warp Jacobian at the brick centre and transform the ray by
   its inverse). The DDA then runs unchanged in rest-pose brick space. Normals are rotated by the
   warp Jacobian in the closest-hit shader.
3. Inflate the brick AABBs by the maximum displacement the descriptor allows, so the BLAS stays
   valid for every pose without rebuilding. Inflated boxes cost some wasted intersection calls;
   bound the amplitude or re-inflate on a slow cadence.

This is the Groen et al. and AMD tet-cage mechanism in its cheapest form, and it is almost certainly
good enough for "animated trees" at Caelix's voxel scale. Data traffic per frame is a handful of
floats per instance. Secondary rays and GI see the deformed geometry for free because they trace the
same TLAS.

### 6.3 Tier 3: GPU re-voxelization of skinned meshes (only if true skinning is required)

For imported, smoothly skinned characters, or to reproduce Lin's vegetation pipeline:

1. Skin the mesh on the GPU (Unity's `SkinnedMeshRenderer` can expose the skinned vertex buffer).
2. Conservatively voxelize in a compute shader into an *object-aligned* brick volume (the object's
   own sectors), writing block ids, 512-bit occupancy and tight bounds directly in the GPU brick
   record layout. Occupancy and bounds come from the same pass; no CPU neighbour scan.
3. Register the object's AABB instance with `dynamicGeometry = true` so Unity rebuilds its BLAS
   each frame. Cost is one voxelization dispatch plus one small BLAS build per character.

Expected cost per character is in the fraction-of-a-millisecond to low-millisecond range (the
OpenGL Insights baseline is 5.5 ms for a 16K-triangle hand on a GTX 480; modern GPUs are much
faster and character meshes for this look are small). It scales with character count, so it is a
hero-character tool, not a crowd tool. Grid-snapping shimmer is inherent; object-aligned grids and
temporal accumulation hide most of it, as they appear to in Lin's video.

Because the voxel data now originates on the GPU, these entities would be render-only unless the
volume is read back; keep them out of automata and alien propagation, or read back on a slow cadence
for gameplay queries.

### 6.4 Sub-pixel voxels in Caelix

Caelix voxels are a fixed world size, so "micro voxels" simply means more voxels per metre; the
scale contract is untouched. What changes is the renderer:

1. **Per-brick colour mips.** Add 8 average colours (one per 4^3 cell, RGB555 = 16 bytes) and one
   brick average to the brick record. The record grows from 1096 to ~1112 bytes.
2. **Footprint-based stop.** Carry a ray cone (pixel footprint at distance t). In
   `CaelixTraceBrickRay` (`CaelixBrickTrace.hlsl:290`), when the footprint exceeds a voxel, stop at
   the 4^3 cell level and return its average colour; when it exceeds a brick, return the brick
   average at the tight AABB entry. The coarse occupancy level already exists for traversal, so
   this is a shading-side change.
3. **Sector-level LOD.** Beyond one brick per pixel, build downsampled LOD sectors (each voxel = one
   brick's average and any-occupancy) as separate instances, and select per sector by camera
   distance with hysteresis (Avoyd), or include both levels in the TLAS and choose per ray with
   instance masks (NVIDIA stochastic LOD). Caelix's denoiser then resolves the stochastic choice.
4. **Prefilter before temporal.** Do not rely on the temporal accumulator to resolve sub-pixel
   voxels; it will flicker. Steps 1 to 3 are the prefilter; the existing a-trous/temporal chain goes
   on top.
5. Animated micro-voxel content is just Tiers 1 and 2 at a finer voxel scale; nothing else changes.

### 6.5 Verification experiments (need the Unity editor, cannot run in this environment)

- **E1, BLAS sharing.** Add 1000 TLAS instances that reference the same `aabbBuffer`/`brickBuffer`
  with different transforms. Measure `RayTracingAccelerationStructure.GetSize()` and build time
  versus 1000 distinct buffers. Decides the Tier 1 instancing design.
- **E2, dynamic AABB geometry.** Register one sector with `dynamicGeometry = true` and change its
  AABBs in place each frame without recreating the buffer; confirm the BLAS follows and measure the
  rebuild cost per sector. Decides Tier 0 step 1 and Tier 3.
- **E3, edit-path profile.** With the profiler markers already in `SectorRenderer` and
  `CaelixWorld`, split the 36k-brick case into job time, `SetData` time and BLAS/TLAS build time on
  the GPU, to confirm the cost model in section 5 before optimizing.
- **E4, ray warp prototype.** Implement a sway warp for one tree entity in the intersection shader
  with inflated AABBs; check for cracks between bricks (the per-brick affine approximation can
  disagree at brick boundaries; if visible, evaluate the warp per step instead of per brick).
- **E5, inline ray tracing.** `SystemInfo.supportsInlineRayTracing` and `#pragma require
  inlineraytracing` are available in Unity 6; a compute-shader `RayQuery` path would let the GPU
  side of Tier 3 (and future GPU-driven culling) query the TLAS without a raygen dispatch.

---

## 7. Recommendation

Do Tier 0 and Tier 1 together. Tier 0 keeps destruction and automata fast; Tier 1 makes animation
cost independent of voxel count and is the approach every serious hardware-ray-traced voxel engine
has landed on. Add Tier 2 for vegetation once a rig exists, because it reuses the same instance
descriptors and needs only shader work. Defer Tier 3 until there is a concrete need for imported
smooth skinning. Treat sub-pixel voxels as a renderer LOD feature, independent of animation, and
decide whether to pursue it at all with Lin's June 2021 retrospective in mind.

---

## 8. Source list

John Lin: https://x.com/ProgrammerLin , https://voxely.net/blog/ ,
https://cramgaming.com/new-voxel-engine-reveal-crystal-islands-experiment-57455/ ,
https://forum.beyond3d.com/threads/polygons-voxels-sdfs-what-will-our-geometry-be-made-of-in-the-future.59982/page-12 ,
https://news.ycombinator.com/item?id=47239042 ,
https://forums.developer.nvidia.com/t/using-rtx-acceleration-for-voxel-tracing/74007

Teardown: https://blog.voxagon.se/2018/10/17/from-screen-space-to-voxel-space.html ,
https://blog.voxagon.se/2024/12/29/year-summary.html , https://acko.net/blog/teardown-frame-teardown/ ,
https://juandiegomontoya.github.io/teardown_breakdown.html

Hardware-RT brick engines: https://github.com/GabeRundlett/gvox_engine ,
https://arxiv.org/abs/1911.06001 (Espe et al.)

Ray deformation: https://github.com/kr4b/Voxel-Animation (Groen et al.),
https://gpuopen.com/learn/ray-tracing-massive-amounts-animated-geometry/ (AMD HPG 2026),
https://hal-imt.archives-ouvertes.fr/LJK_GI_ARTIS/hal-00840637v1

Re-voxelization: https://www.icare3d.org/research/OpenGLInsights-SparseVoxelization.pdf ,
https://diglib.eg.org/handle/10.1111/cgf15262 , https://arxiv.org/abs/2510.09081 ,
https://link.springer.com/10.1007/s11554-019-00919-1 , https://github.com/mattatz/unity-voxel

Per-voxel skinning: https://github.com/denniskb/asvo

Brick structures and uploads: https://github.com/dubiousconst282/VoxelRT ,
https://github.com/stijnherfst/BrickMap , https://vkguide.dev/docs/ascendant/ascendant_geometry/ ,
https://teknologicus.itch.io/vorxel , https://github.com/tim-oster/voxel-rs

Sub-pixel voxels and LOD: https://hal.archives-ouvertes.fr/hal-00704461 (Heitz & Neyret),
https://arxiv.org/abs/2604.13191 , https://www.enkisoftware.com/changelog-avoyd ,
https://developer.nvidia.com/blog/implementing-stochastic-lod-with-microsoft-dxr ,
https://developer.download.nvidia.com/gameworks/events/GDC2016/msalvi_temporal_supersampling.pdf

Unity API: https://docs.unity3d.com/6000.1/Documentation/ScriptReference/Rendering.RayTracingAABBsInstanceConfig.html ,
https://docs.unity3d.com/ScriptReference/Rendering.RayTracingAccelerationStructure.AddInstance.html ,
https://docs.unity3d.com/ScriptReference/SystemInfo-supportsInlineRayTracing.html
