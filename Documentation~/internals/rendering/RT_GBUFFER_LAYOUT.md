# RT GBuffer Layout

## Current Render Targets

| Texture | Format | Channels |
| --- | --- | --- |
| `Caelix_outColor` | `ARGBFloat` | `RGB`: final composed radiance, `A`: coverage (see Conventions) |
| `Caelix_outDirectRadiance` / `DirectRadianceTarget` | `ARGBFloat` | `RGB`: primary emission plus direct radiance, `A`: hit mask |
| `Caelix_outIndirectRadianceRaw` / `IndirectRadianceTarget` | `ARGBHalf` | `RGB`: noisy single-frame indirect incident radiance, `A`: hit mask |
| `Caelix_outIndirectRadianceSpatialTemp` | `ARGBHalf` | `RGB`: horizontal spatial filter intermediate, `A`: hit mask |
| `Caelix_outIndirectRadianceFiltered` | `ARGBHalf` | `RGB`: spatially filtered current-frame indirect incident radiance, `A`: hit mask |
| `Caelix_outAlbedo` / `AlbedoTarget` | `Default` | `RGB`: primary albedo, `A`: unused/1 |
| `Caelix_outNormal` / `NormalTarget` | `ARGBHalf` | `RG`: Unity oct-packed normal in `[0, 1]`, `B`: 10-bit voxel-face hash stored as exact integer-valued half float, `A`: linear roughness |
| `Caelix_NormalDepth` / `DepthTarget` | `RFloat` | `R`: linear eye depth for depth copy |
| `Caelix_outMotionVector` / `MotionVectorTarget` | `ARGBFloat` | `RG`: previousUV minus current (un-jittered) UV, `B`: viewZprev minus viewZ, `A`: delta-checkerboard flag |
| `Caelix_outGiSurface` / `GiSurfaceTarget` (GI mode only) | `ARGBFloat` | `RGB`: real world position of the surface the primary delta chain ended on, `A`: virtual distance along the camera ray (`path.T`); `(0, 0, 0, K_T_MAX)` on a miss |
| `Caelix_outGiKey` / `GiKeyTarget` (GI mode only) | `R32G32B32A32_UInt` | `R`: path hash16 \| instance slot << 16, `G`: instance lifetime token, `B`: flags (bit 0 hit, bit 1 wants a diffuse GI estimate, bit 2 opaque), `A`: block ID; 0 on a miss |

The GI targets exist only when the trace runs with the `CAELIX_GI_MODE` keyword, which `CaelixGBufferPass` sets on the command buffer every frame (on for `CaelixGiRendererFeature`, off otherwise). In GI mode the diffuse lobe of an opaque primary is not traced: `CaelixGiPass` fills `DiffuseRadianceTarget` instead, and every traced path goes to `SpecularRadianceTarget`. See `GI_RESAMPLING.md`.

## Pass Order

1. `Caelix DXR Trace` writes raw direct radiance, raw indirect radiance, GBuffers, motion vectors, and current depth/normal history inputs.
2. `Caelix Combine Stochastic` sums the split diffuse/specular targets into the working signal.
3. `Caelix Indirect Spatial Filter X/Y`, or `Caelix Indirect A-Trous Filter 1..N`, filters that signal.
4. `Caelix Indirect Temporal Accumulation` reprojects the previous indirect history with motion vectors, applies plane-prediction depth and normal rejection, and writes the current indirect history.
5. `Caelix Composite` combines `direct + albedo * accumulatedIndirect`.
6. `Caelix Delta Checkerboard Resolve` averages the reflect/refract parities back together.
7. `Caelix Colour Resolve` accumulates the jittered samples against the reprojected colour history (TAA) and writes the current colour history. Its output is the presented colour.
8. `Caelix Copy To Camera` (stage 0, `AfterRenderingOpaques`) draws every fully covered pixel and writes camera depth, before the URP skybox.
9. `Caelix Copy To Camera (Edges)` (stage 1, `AfterRenderingSkybox + 1`) draws the partially covered silhouette pixels with `Blend One OneMinusSrcAlpha`, after the skybox.

The double buffers flip at the very end of `CaelixDenoisePass.RecordRenderGraph`, once every write into a "current" half has been recorded.

## Temporal History

| Texture | Format | Channels |
| --- | --- | --- |
| `Caelix_HistoryIndirectRadiance_*` | `ARGBHalf` | `RGB`: accumulated indirect incident radiance, `A`: accumulated frame count |
| `Caelix_HistoryDepth_*` | `RFloat` | `R`: previous surface view depth |
| `Caelix_HistoryNormal_*` | `ARGBHalf` | `RG`: Unity oct-packed normal in `[0, 1]`, `B`: expected previous view depth of this surface, `A`: validity |
| `Caelix_HistoryColor_*` | `ARGBHalf` | `RGB`: resolved colour premultiplied by coverage, `A`: coverage |

## Conventions

These are shared by both trace kernels (`RayQuery/CaelixPathTraceRQ.compute`, `Budget/CaelixBudgetRQ.compute`), both denoise chains and the present stage. Changing one without the others silently misaligns history by a fraction of a pixel.

**Pixel-centre mapping.** A pixel's sample sits at launch-space position `index + 0.5 + jitter`, and its UV is that divided by `size` — never `size - 1`. The `size - 1` convention placed samples on the grid corners, put UV > 1 on the last row and column (which then never reprojected), and had to be mirrored in five places.

**Global Halton jitter.** `CaelixCameraHistory` rolls one sub-pixel offset per frame for the whole image: Halton (2, 3), recentred to `[-0.5, 0.5]`, cycling every 8 frames. It is global rather than per-pixel because the filters have to reconstruct the exact ray a pixel was traced with, and they can only do that from a value the whole image shares. It reaches the tracer as `g_Jitter` and the filters as `_CaelixJitter` (`xy` = this frame, `zw` = last frame).

**Motion vectors.** `previousUV(hit point) − jitteredCurrentUV`, i.e. the NRD "previous minus current" sign, with this frame's jitter subtracted out. A static scene therefore reprojects exactly onto its own texel. A consumer that needs the *jittered* previous position adds `_CaelixJitter.xy` back after scaling by the frame size, which is what the temporal filter does to anchor its plane.

**`CaelixViewRay` contract.** `CaelixViewRay(launchPos)` (`CaelixDenoiseCommon.hlsl`) returns the unnormalised view-space ray through a launch-space position, with `z = -1`, so the point `t * ray` has view-forward depth `t` — exactly the quantity the depth targets store. It must match the raygen mapping exactly: same launch space, same jitter added, same zoom and aspect. `_CaelixProjection` carries `x` = `tan(verticalFov / 2)` and `y` = aspect, both taken from the camera rather than hard-coded.

**Plane-prediction guides.** Neither filter compares depths directly any more. The a-trous filter turns the centre pixel's normal into a view-space plane and compares each tap against the depth that plane predicts *at that tap*; the temporal filter does the same in the previous frame's view, anchored at `expectedPreviousDepth`. Both reject a tap whose ray does not meet the plane on the same side (`tapNdotR * centreNdotR <= 1e-6`). The consequence is that a plane gets weight 1 in every direction at every angle — no anisotropic blur along the depth gradient, no camera-space band where a receding floor self-rejects — and only a genuine depth step is rejected.

**Coverage in alpha.** After the colour resolve, `Caelix_outColor.a` is fractional at silhouettes and the RGB is premultiplied by it. That is why the present stage is split in two: stage 0 draws `a >= 0.99` before the skybox (so it writes `_CameraDepthTexture` for the clouds and sky), stage 1 draws `0.01 <= a < 0.99` after it with a premultiplied-alpha blend. Debug views are opaque and draw in stage 0 only.
