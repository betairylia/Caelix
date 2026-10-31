# Budget Mode Renderer

A fixed-cost alternative to the path-traced feature. Where `CaelixRendererFeature` spends an
unbounded stochastic path per pixel and denoises radiance, budget mode spends a *known* number of
rays per pixel, produces a small G-buffer, and evaluates a closed-form lighting model once in a
deferred pass.

Both features live side by side. **Enable exactly one of them on a renderer asset** — they both
drive `CaelixCameraHistory` and both publish into `CaelixFrameResources`, so running them
together would have each overwrite the other's frame. (`CaelixCameraHistory.BeginFrame` /
`EndFrame` are idempotent per camera per frame, so the failure mode is "one image wins", not
corrupted history.)

## Ray budget

Per pixel, per frame:

| Rays | What |
| --- | --- |
| 1 | primary |
| ≤ 1 | delta step — mirror reflection or glass refraction |
| ≤ `transparentSkipLimit` (default 6) | straight-line pass-through of transparent interfaces |
| 1 | hard sun shadow, towards the main directional light |
| `aoSampleCount` (default 1) | ambient occlusion, cosine hemisphere, capped at `K_MAX` |

Shadow and AO rays restart behind transparent voxels rather than being stopped by them, up to
`K_BUDGET_VISIBILITY_STEPS` (4) times each. So the hard worst case is
`(2 + transparentSkipLimit) + 4 * (1 + aoSampleCount)` traces — `8 + 8 = 16` at defaults, with
essentially all pixels far below that. When a visibility ray runs out of restarts it reports
*unoccluded*, so the failure mode of a deep stack of glass is a light leak, not a black blob.

A pane of glass costs two chain slots (see the interface model below): the delta on the way in, one
pass-through on the way out. `transparentSkipLimit = 6` therefore clears about three panes.

`K_MAX` is `budgetTrace.aoMaxDistance`, default **20** world units (voxels). Geometry beyond it
never occludes. AO is binary visibility, not a distance falloff — the 1spp signal is what the
filters are built for.

## Files

| File | Role |
| --- | --- |
| `Runtime/Rendering/RendererFeature/CaelixBudgetRendererFeature.cs` | settings surface + wiring |
| `Runtime/Rendering/RendererFeature/CaelixBudgetSettings.cs` | `CaelixBudgetTraceSettings`, `CaelixBudgetShadingSettings` |
| `Runtime/Rendering/RendererFeature/Passes/CaelixBudgetGBufferPass.cs` | stage 1, the compute dispatch; also hosts `CaelixBudgetLighting` (shared light/sky resolution) |
| `Runtime/Rendering/RendererFeature/Passes/CaelixBudgetDenoisePass.cs` | stage 2, filter + deferred shade |
| `Runtime/Rendering/RendererFeature/Passes/CaelixRayQueryDispatch.cs` | the scene bindings both G-buffer stages share: pages, instance table, material table, bake kernels |
| `Runtime/Rendering/Shaders/Budget/CaelixBudgetRQ.compute` | the trace kernel (`CaelixBudgetKernel`) |
| `Runtime/Rendering/Shaders/Budget/CaelixBudgetShade.shader` | 4 passes: a-trous, temporal, deferred shade, cross resolve |

Reused unchanged: `CaelixPresentPass`, `CaelixCameraHistory`, `CaelixFrameResources`,
`CaelixATrousFilterSettings`, `CaelixTemporalRadianceSettings`, `PostFlip.shader`, `RayPayload`,
and the whole ray query trace side — `RayQuery/CaelixRayQueryTrace.hlsl`,
`RayQuery/CaelixMaterialTable.hlsl`, `CaelixAabbInstance.shader` (the AABB instances' placeholder
material) and `CaelixRayQueryRenderer` itself, which owns the acceleration structure, the brick pool
and the instance table for whichever feature is enabled.

### Shared denoise kernels

The a-trous, temporal and cross-resolve kernels were factored out of the path tracer's shaders into
`Runtime/Rendering/Shaders/Denoise/`, and both paths now include the same HLSL:

| Include | Contents |
| --- | --- |
| `CaelixDenoiseCommon.hlsl` | `_CaelixFrameSize`, the shared G-buffer guide textures, coord/normal/luma/hash helpers |
| `CaelixATrous.hlsl` | `CaelixATrousFilter(coord)` — SVGF-lite edge-aware wavelet |
| `CaelixTemporal.hlsl` | `CaelixTemporalAccumulate(coord)` — validated bilinear history reprojection |
| `CaelixCrossResolve.hlsl` | `CaelixCrossResolve(coord)` — delta checkerboard averaging |

The signal each kernel operates on is named by a macro the including shader sets:
`CAELIX_ATROUS_SIGNAL_TEX` and `CAELIX_TEMPORAL_SIGNAL_TEX`. Both default to
`_IndirectRadianceTex`, so the path tracer's shaders needed no rewiring; budget mode points them at
`_BudgetAOShadowTex`. The signal convention both paths honour:

- `.rgb` — the quantity being filtered
- `.a` — validity (`<= 0.001` means "no data", pass through) on the way in; accumulated frame count
  in history and on the way out of temporal

Path-tracer behaviour is byte-identical to before the refactor.

## G-buffer

| Texture | Format | Channels |
| --- | --- | --- |
| `Caelix_outDeterministicRadiance` | `ARGBFloat` | `RGB`: emission (or sky, for a delta ray that missed), pre-multiplied by chain throughput. `A`: 1 surface / 0 direct miss — the present stage clips on it so URP's own skybox shows through |
| `Caelix_outAlbedo` | `ARGBHalf` | `RGB`: surface albedo × chain throughput. `A`: unused |
| `Caelix_outNormal` | `ARGBHalf` | `RG`: oct-packed world normal in `[0,1]`, `B`: voxel face hash, `A`: linear roughness |
| `Caelix_outBudgetSurface` | `ARGBHalf` | `RG`: oct-packed direction *from surface towards viewer*, `B`: metallic, `A`: 1 surface / 0 sky |
| `Caelix_outBudgetAOShadowRaw` | `ARGBHalf` | `R`: AO, `G`: sun visibility, `B`: unused, `A`: validity |
| `Caelix_outDepth` | `RFloat` | `R`: linear view depth of the **primary** hit |
| `Caelix_outMotionVector` | `ARGBFloat` | `XY`: previousUV − currentUV, `Z`: viewZprev − viewZ (NRD 2.5D), `A`: delta-checkerboard flag |
| `Caelix_HistoryDepth_*` | `RFloat` | `R`: linear view depth of the **PSR virtual** hit |
| `Caelix_HistoryNormal_*` | `ARGBHalf` | `RG`: oct normal, `B`: this surface's expected previous-frame depth, `A`: valid |

Albedo is `ARGBHalf` here, not the path tracer's 8-bit `Default`: it carries the delta chain's
throughput, and the checkerboard's ×2 Fresnel weight puts it above 1.

`Caelix_outBudgetSurface` exists purely because of PSR — the shaded surface is not on the camera
ray, so the view direction cannot be re-derived from the pixel in the deferred pass and the raygen
has to publish it.

## Primary surface replacement

The chain is: primary ray → at most one delta event → pass through smooth transparent voxels →
shade at the first opaque or rough hit. Everything in the G-buffer describes **that** terminating
surface, not what the primary ray hit first.

The one exception is `DepthTarget`, which keeps the **primary** hit distance so `SV_Depth` in the
present stage still composites correctly against ordinary rasterized geometry. The full path length
goes to `g_CurrentDepthHistory`, which is what the denoiser reprojects against — so reflections and
refractions filter and accumulate at their own apparent distance rather than the glass surface's.

### The interface model (read this before touching the chain)

Transparency detection is **already done by the DDA**, by testing the ray direction against which
of the cell's faces border a different medium. Budget mode inherits that rather than re-deriving it.

- `BrickRecordLayout.GetRendererBlockData` builds `faceMask`: bit *i* is set when the
  neighbour in `NeighborhoodSettings.Directions[i]` is a *different* transparent medium.
  `Directions` is `+X, -X, +Y, -Y, +Z, -Z`.
- `CaelixBrickTrace.hlsl:273` terminates on `GetFaceBits(blockID) & normalFlags`, where
  `normalFlags` names the face the ray *entered through* — a ray moving `+X` sets bit 1, which is
  the `-X` neighbour's bit.

Put together, a transparent cell only reports a hit when the cell the ray just came from was a
different medium. So:

> **Every transparent hit is a genuine medium crossing, and `materialID` is the medium being
> ENTERED.**

The air cell in front of a pane of glass is *not* reported to a ray arriving through air (its `-X`
neighbour is also air). The glass cell is. On the way out, the air cell behind the glass is — that
hit *is* the exit event, and it correctly reports air. This is the invariant the path tracer's
`previousTransparentMaterial` already relies on, and budget mode now uses the same convention
(`path.mediumMaterial`, a material ID, `0` = vacuum; `MatTableTransparent[0]` is air —
`{albedo (1,1,1), smoothness 0, metallic 0, IOR 1}` — so `GET_MATERIAL` is safe to call on it
unconditionally and yields vacuum's IOR).

The chain therefore never has to judge whether a hit is a real interface. It only remembers which
medium it is in:

```
opaque hit:
    smooth && metallic >= 0.5 && delta unspent  ->  mirror delta
    otherwise                                   ->  shade here

transparent hit  (always a real crossing into `material`):
    delta spent, or delta disabled              ->  pass through
    smoothness < deltaSmoothnessThreshold       ->  shade here     (frosted glass is a surface)
    otherwise                                   ->  refractive delta
```

The "delta spent" test is checked **first**, and that ordering is load-bearing: leaving a pane of
glass reports the air cell behind it, and air's smoothness is 0. Testing roughness before
spent-ness would shade that air cell as if it were frosted glass, painting a flat white surface
over the far side of every window.

- **Mirror delta** — one reflection, tinted by the metal's albedo. Only one branch exists, so it is
  not a checkerboard pair and `isPSR` stays false.
- **Refractive delta** — reflect or refract, split **spatially** by pixel parity so both branches
  exist every frame. The surviving branch carries `2 × fresnel` (or `2 × (1 − fresnel)`) so glass
  does not read as a 50% mirror at every angle; the ×2 cancels the 0.5 the cross resolve puts on
  each parity. Total internal reflection has no refracted branch, so it stays unsplit and
  unflagged. Refracting updates `mediumMaterial` and multiplies the medium's albedo in; reflecting does
  neither.
- **Pass through** — direction unchanged, `mediumMaterial` set to the cell entered, albedo of the
  cell *entered* multiplied in. Tinting on entry (not exit) is what makes a pane cost its colour
  exactly once: entering glass multiplies glass, leaving it multiplies air's white.
- Once the one delta is spent, every further interface passes through: no second refraction, no
  Fresnel. A glass pane therefore bends on entry and never un-bends on exit. That is the intended
  cheap look.
- If the skip budget runs out, whatever the chain is currently on gets shaded. Bounded, and not
  signalled anywhere.

Camera-inside-a-medium is not handled (the same gap the path tracer has): `mediumMaterial` starts at
vacuum regardless of where the camera actually is.

## Deferred shading

`CaelixBudgetShade.shader`, pass `DeferredShade`, one evaluation per pixel:

```
diffuseColor  = albedo * (1 - metallic)
specularColor = lerp(0.04, albedo, metallic)

sun     = mainLightColor * saturate(dot(N, L)) * sunVisibility
color   = diffuseColor * sun                                    // lambert
        + GGX(N, V, L, roughness, specularColor) * sun          // URP's analytic DirectBRDFSpecular
        + sky(N, skyDiffuseMip) * diffuseColor * ao             // ambient diffuse
        + sky(reflect(-V,N), roughness * skySpecularMaxMip)
              * envBRDF(specularColor, roughness, NoV) * specularAO
        + emission
```

**AO attenuates only the ambient half.** Occlusion is an ambient-visibility term; applying it to a
directional light double-counts the shadow that light already casts via its own ray. `aoStrength`
and `aoAffectsSpecular` scale it back if a more stylized look is wanted.

The sky cubemap is whatever the sky provider published as `_GlossyEnvironmentCubeMap` (PBRSky), same
as the path tracer, fetched directly because render graph cannot track a plain global. It stands in
for both irradiance (a high mip along the normal) and the reflection probe (a roughness-selected mip
along the reflection vector). **If that cubemap has no mip chain, both LODs clamp to mip 0** and the
ambient reads flatter but still plausible — `skyDiffuseMip` / `skySpecularMaxMip` are exposed to tune
against whatever the provider actually supplies.

Only the **main directional light** is lit. No additional punctual lights, no emissive-voxel
gathering — emissive voxels still glow (their emission is in the G-buffer) but they cast no light.

## Pass order

1. `Caelix Budget RayQuery Trace` — G-buffer + raw AO/shadow.
2. `Caelix Budget AO A-Trous Filter 1..N` — shared a-trous over AO/shadow, ping-ponged, rebinding
   `_BudgetAOShadowTex` after each iteration.
3. `Caelix Budget AO Temporal Accumulation` — shared temporal kernel into
   `CaelixCameraHistory`'s double buffer (signal-agnostic `ARGBHalf`, so AO reuses the targets
   the path tracer fills with radiance).
4. `Caelix Budget Deferred Shade` — the lighting model above.
5. `Caelix Budget Delta Checkerboard Resolve` — shared cross resolve, keyed off `MotionVector.a`.
6. `Caelix Copy To Camera` — `CaelixPresentPass`, unchanged.

## Setup

On the renderer asset, add **Caelix Budget Renderer Feature** and disable the path-traced
`Caelix Renderer Feature`. Assign:

- **Ray Query Tracer** → `Budget/CaelixBudgetRQ.compute`
- **Budget Shade Shader** → `Hidden/Caelix/BudgetShade`
- **Post Process Material Flip** → the same flip material the path-traced feature uses
- **Blue Noise Texture** → the same 128×8192 R8G8 STBN texture (required: AO ray directions come
  from `SampleBlueNoise`)

## Debug views

`CaelixDebugView` gained four budget entries (11–14): `BudgetAOShadowRaw`,
`BudgetAOShadowFiltered`, `BudgetAOShadowAccumulated`, `BudgetSurface`. AO shows as red, sun
visibility as green. The stochastic views (9, 10) have no budget-mode equivalent and fall back to
the shaded image. `PostFlip.shader` needed no change.

## Known gaps / next steps

- The AO signal shares `_ATrous*` and `_TemporalRadiance*` uniform names with the path tracer's
  chain. Harmless (one feature at a time) but the names read as radiance-specific.
- AO and sun shadow are denoised together as one `.rg` signal, so they share edge-stopping weights.
  Shadow is far lower variance than AO and could take a cheaper filter.
- No specular occlusion beyond scaling AO — a proper horizon-based term would help grazing angles.
- `g_BudgetTransparentSkipLimit` exhaustion silently shades a transparent voxel. Bounded and rare,
  but not signalled anywhere.
- Camera starting inside a transparent medium is unhandled (shared with the path tracer).
