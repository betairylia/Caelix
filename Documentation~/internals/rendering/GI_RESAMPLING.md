# GI resampling (NAADF-style bucket reuse)

Status: design, 2026-10-12. Implementation on branch `dev/fable/naadf-gi`.

This renderer feature estimates the diffuse indirect lighting of every pixel by reusing path
samples that were traced in earlier frames and at other pixels. It follows the global illumination
pipeline of the NAADF paper (Ulschmid et al., CGF 2026, `Research/NAADF` holds the code) with four
deliberate changes:

1. No intersection planes and no mirror chain. The G-buffer stores the real surface the primary
   delta chain ended on, plus the virtual point along the camera ray, plus a 16-bit path hash of the
   delta interfaces it crossed. A sample is rejected when its hash does not match the landing pixel.
2. Entity motion is applied to the primary surface only, through object-space storage and the
   instance's current transform. Secondary hits stay in world space. Mirrors, glass and anything
   seen through them do not get motion handling.
3. No scene version. Nothing clears the sample history when the world is edited. Stale samples are
   rejected pixel by pixel through the depth-range and hash tests at their landing pixel, and
   lighting changes lag by up to the history length.
4. Only the diffuse lobe of opaque primaries is resampled. Specular lobes, glass and mirrors are
   still traced every frame by the existing path tracer and go to the specular target.

The rest of the frame (a-trous, temporal accumulation, composite, colour resolve, present) is the
existing `CaelixDenoisePass` and `CaelixPresentPass`, unchanged.

## Frame overview

`CaelixGiRendererFeature` enqueues, all at `AfterRenderingOpaques`:

1. `CaelixGBufferPass` in GI mode (keyword `CAELIX_GI_MODE` on `CaelixPathTraceRQ.compute`). Same
   trace as today plus the GI targets, and the diffuse bounce is not traced.
2. `CaelixGiPass`, seven compute dispatches in `Shaders/Gi/CaelixGiRQ.compute`:
   1. `GiPreparePixels`: per pixel, 3x3 virtual-distance range and hash set.
   2. `GiPrepareBuckets`: per 8x8 bucket, zero the counters, normal mask and distance range.
   3. `GiGenerateSamples`: per eligible pixel, one new path sample into the lit or unlit ring.
   4. `GiPrepareHistory`: one thread, decides how many stored samples are valid and writes the
      indirect dispatch arguments.
   5. `GiReprojectLit` and `GiReprojectUnlit`: per stored sample, move it to the current frame,
      validate it at its landing pixel, count it into its bucket.
   6. `GiRefineBuckets`: per bucket, lit ratio, sample leveling, keep at most 8 samples.
   7. `GiSpatial`: per pixel, 12 reservoir iterations over nearby buckets, one visibility ray,
      write the diffuse incident radiance into `StochasticDiffuse`.
3. `CaelixDenoisePass` and the two `CaelixPresentPass` stages, unchanged.

## Conventions shared with the tracer

Pixel mapping, jitter and projection are the ones in `RT_GBUFFER_LAYOUT.md`. A world point `P` is
projected to a texel with:

```
view = WorldToCamera * P            // this frame's view matrix
depth = -view.z                     // reject if depth <= 0
ndc = view.xy / (depth * float2(zoom * aspect, zoom))
launch = (ndc * 0.5 + 0.5) * size - 0.5 - jitter
texel = int2(floor(launch + 0.5))   // reject if outside the image
```

`zoom = tan(fov / 2)`, `aspect = width / height`, `jitter` in pixels, all from
`CaelixCameraHistory`, pushed as `g_GiProjection`, `g_GiJitter`, `g_GiWorldToCamera`,
`g_GiCameraPosition`, `g_GiFrameSize`. Texel rows follow the G-buffer (no flip).

Axis normals: voxel normals are always one of six axis directions. `GiAxisIndex(n)` returns
`0..5` as `+X, -X, +Y, -Y, +Z, -Z` (the `objectNormals` order in `CaelixBrickTrace.hlsl`).

Radiance packing: RGB9E5, `GiPackRadiance` / `GiUnpackRadiance` (shared RGB9E5 helpers from URP
`Packing.hlsl` are fine).

Random numbers: `GiGenerateSamples` uses the STBN blue noise through `RandomFloat01` and
`RandomUnitVector` exactly as the tracer does (launch index + `g_FrameIndex`). Every other kernel
uses a PCG hash stream `GiRand(inout uint state)` seeded from pixel, frame and kernel id.

## Shared code changes

### Ray payload and instance record

- `RayPayload` gains `uint instanceId`. `CaelixRayQueryTrace` sets it to the committed instance;
  `CaelixClearRayPayload` zeroes it. The budget kernel ignores it.
- `CaelixRayQueryInstance` (C# and HLSL) gains the current object-to-world transform as three rows
  `curRow0..2` (the fourth row is `0,0,0,1`) and renames `pad1` to `lifetimeToken`. Stride 80 goes
  to 128. `CaelixRayQueryInstanceTable.Allocate` assigns a fresh non-zero token from a static
  counter; `Set` takes the current transform too and preserves the token; `Free` zeroes the record.
  `RayQueryGroupRenderer.RenderModifyAS` passes `objectToWorld` to `Set`. HLSL helpers:
  `CaelixInstanceObjectToWorld(inst)` and `CaelixInstanceWorldToObject(inst)` (rigid inverse:
  transpose of the 3x3, translation `-R^T t`; entity scale is 1 by contract).

### Tracer in GI mode (`CaelixPathTraceCommon.hlsl`)

Always on, mode independent: `CaelixPathState.pathHash` (init 0). In `AdvanceDeltaRay`, at every
transparent interface after `doRefraction` is decided:

```
path.pathHash = CaelixHashUint(path.pathHash
    ^ (materialID & 0xFFFFu)
    ^ ((payload.materialID_voxelFaceHash & 63u) << 16)
    ^ (doRefraction == 1.0f ? 0x800000u : 0u));
```

(Opaque mirrors are not followed by the delta chain today, so this is the full chain.)

Under `#if defined(CAELIX_GI_MODE)`:

- Declare `RWTexture2D<float4> GiSurfaceTarget;` and `RWTexture2D<uint4> GiKeyTarget;`.
- On a primary hit write `GiSurfaceTarget = float4(surfaceWorldPosition, path.T)` (real surface
  position, virtual distance along the camera ray) and
  `GiKeyTarget = uint4(hash16 | (payload.instanceId << 16), token, flags, materialID)` where
  `hash16 = path.pathHash == 0 ? 0 : max(1, (h ^ (h >> 16)) & 0xFFFF)` and
  `token = g_Instances[payload.instanceId].lifetimeToken`. Flags: bit 0 = hit, bit 1 = this pixel
  wants a diffuse GI estimate (see below), bit 2 = opaque.
- On a miss write `GiSurfaceTarget = float4(0, 0, 0, K_T_MAX)` and `GiKeyTarget = 0`.
- Bounce section: after `CaelixShadeVoxelHit` on the primary, let
  `diffuseLobe = IsOpaque(materialID) && primaryShade.specularLobe < 0.5 && !PathBounceIndexIsSpecular(path)`.
  If `diffuseLobe`: do not run the bounce loop; write `DiffuseRadianceTarget = (0,0,0,K_T_MAX)`,
  `SpecularRadianceTarget = 0`, set flag bit 1. Otherwise run the loop exactly as today but write
  its result to `SpecularRadianceTarget` and zero to `DiffuseRadianceTarget`, flag bit 1 clear.
  (Today an opaque specular-lobe sample lands in the diffuse target unless the chain reflected;
  in GI mode the diffuse target belongs to the resampler alone.)

`CaelixPathTraceRQ.compute` adds `#pragma multi_compile _ CAELIX_GI_MODE`.

### G-buffer pass and frame resources

`CaelixGBufferPass.GiMode` (bool, set by the GI feature through `ConfigureSettings`). When true the
pass creates `Caelix_outGiSurface` (ARGBFloat) and `Caelix_outGiKey` (R32G32B32A32_UInt), binds
them, publishes them as `_GiSurfaceTex` / `_GiKeyTex` and stores the handles in
`CaelixFrameResources.GiSurface` / `GiKey`. The keyword is set on the command buffer every frame
(`cmd.SetKeyword(cs, new LocalKeyword(cs, "CAELIX_GI_MODE"), giMode)`), both ways, so the regular
feature never inherits it.

## GPU data

All per-camera, owned by `CaelixGiResources`, reallocated on resize (counts zeroed from C#).
`pixels = width * height`, `buckets = ceil(w/8) * ceil(h/8)`.

```
struct GiLitSample            // 56 bytes, ring of litRingFrames * pixels
{
    uint   key;               // pathHash16 | instanceSlot << 16
    uint   token;             // instance lifetime token at sample time
    float3 objectPosition;    // primary surface, object space of the instance
    float3 virtualOffset;     // virtual point minus real point, world space (0 without delta chain)
    uint   primaryNormal;     // axis index 0..5 | flags << 8 (bit 8 = sky sample)
    float3 secondaryPosition; // reconnection point, world space (unit direction for sky samples)
    uint   secondaryNormal;   // oct-encoded, 2 x 16 bit (unused for sky)
    uint   radiance;          // RGB9E5, incident radiance along the sample direction
};

struct GiUnlitSample          // 36 bytes, ring of unlitRingFrames * pixels
{
    uint key; uint token; float3 objectPosition; float3 virtualOffset; uint primaryNormal;
};

struct GiBucketSample         // 32 bytes; refined: buckets * 32, compact: buckets * 8
{
    float3 primaryPosition;   // current world position of the sample's primary surface
    uint   normals;           // primary axis (bits 0-2) | sky flag (bit 3) | secondary oct x 12 bits << 8 | oct y 12 bits << 20
    float3 secondaryPosition; // world (unit direction for sky)
    uint   radiance;          // RGB9E5 (after lit-ratio and leveling scale in the compact buffer)
};

struct GiBucketInfo           // 32 bytes, buckets
{
    uint  litCount;           // atomic, this frame
    uint  unlitCount;         // atomic, this frame (each stored unlit sample counts unlitKeepDivisor)
    uint  distRange;          // f16 min | f16 max of the bucket's pixels' virtual distance
    uint  packed;             // storedCount (4) | normalMask6 << 4 | totalComp3 << 10
    float litRatio;
    float maxLuminance;       // of the stored lit samples
    uint  reserved0, reserved1;
};

struct GiPixelInfo            // 8 bytes, pixels
{
    uint distRange;           // f16 min | f16 max of the 3x3 virtual distances
    uint hashes;              // centre hash16 | bloom16 << 16 (bloom bit = 1 << (h & 15) of each of the 8 neighbours)
};

counts buffer (uint, 8 + 2 * 128 entries):
  [0] litHead  [1] unlitHead          // ring write heads, monotonically increased mod capacity
  [2] litValid [3] unlitValid         // valid totals this frame (set by GiPrepareHistory)
  [4] litStart [5] unlitStart         // ring index of the oldest valid sample
  [6] litCoprime [7] unlitCoprime     // odd multipliers for the group shuffle
  [8 + 2*f], [9 + 2*f]                // per-frame lit / unlit counts, f = ringFrame % 128

`ringFrame` counts the frames this camera's buffers have run (`CaelixGiResources.RingFrame`,
pushed as `g_GiRingFrame`), not `Time.frameCount`: a frame on which the camera did not render
would otherwise leave a stale count from 128 frames earlier inside the history sum. Thread 0 of
`GiPrepareBuckets` zeroes this frame's two slots before `GiGenerateSamples` adds to them.

indirect args buffer: two DispatchIndirect triples (lit, unlit).
```

## Kernels

### GiPreparePixels (8x8 threads per pixel)

Read `GiSurfaceTarget.w` (virtual distance, `K_T_MAX` on miss) and `GiKeyTarget.x & 0xFFFF` for the
3x3 neighbourhood. Write min/max distance (misses included, like NAADF) and the hash word.

### GiPrepareBuckets (64 threads, one per bucket)

Zero `litCount`, `unlitCount`. Over the bucket's pixels that hit: OR `1 << GiAxisIndex(normal)` into
the normal mask (normal from `_NormalTex`), min/max of the virtual distance. Write `distRange`,
`packed` (mask only, stored 0, totalComp 0), `litRatio = 0`, `maxLuminance = 0`.

### GiGenerateSamples (8x8 threads per pixel)

Eligible: key flags bit 0 and bit 1 set, and `((g_FrameIndex + x + y) % sampleRateDivisor) == 0`.

```
P = GiSurface.xyz; n = normal; T = GiSurface.w
inst = g_Instances[slot]; objPos = WorldToObject(inst) * P
camRay = normalize(P_virtual - cam) where P_virtual = cam + ComputeRayDirection(pixel) * T  // same mapping as the tracer
virtualOffset = P_virtual - P

d = normalize(n + RandomUnitVector(rng))          // cosine distribution, same as the tracer
ray from P pushed off along n, direction d
hit = trace(ray)
if miss:
    if RandomFloat01(rng) < skySampleProbability: c = Sky(d) / skySampleProbability else c = 0
    secondary = d (direction), sky flag
else:
    q = hit position, nq = hit normal (flipped to face the ray), material m
    c = m.emission
    continue the path from q with the tracer's own CaelixShadeVoxelHit loop for up to
    bounceCountOpaque - 1 more segments, throughput starting at 1 (the first segment's
    cosine / pdf cancels), medium extinction and Russian roulette as in the tracer,
    adding emission * throughput and sky * throughput on a miss (sky again gated by
    skySampleProbability). Everything after q is baked into c.

// lit / unlit split (NAADF radiance leveling)
s = luminance(c)
if s < litThreshold:
    keep = max(s, 0.05 * litThreshold) / litThreshold
    c *= 1 / keep
    if RandomFloat01(rng) > keep: c = 0
if any(c > 0):
    append GiLitSample to the lit ring (wave-aggregated atomic on counts[8 + 2f], index = litHead + k)
else if RandomFloat01(rng) < 1 / unlitKeepDivisor:
    append GiUnlitSample to the unlit ring the same way
```

`key = hash16 | slot << 16`, `token` from the key target. Samples whose first hit is a mirror or
glass voxel are stored with that voxel as the reconnection point. That is an approximation (the
radiance beyond it is view dependent) and is accepted for now.

### GiPrepareHistory (1 thread)

```
f = g_FrameIndex % 128
litHead = (litHead + counts[8 + 2f]) % litCapacity        // this frame's samples are now below the head
(same for unlit)
total = 0; for i in 0 .. maxHistoryFrames-1: n = counts[8 + 2*((f - i) mod 128)]; if total + n > capacity: break; total += n
litValid = total; litStart = (litHead - total) mod capacity
litCoprime = GiHash(g_FrameIndex) | 1
args = ceil(total / 64) groups, rounded up to a power of two (the shuffle needs it)
```

Per-frame counts older than 128 frames are overwritten naturally.

### GiReprojectLit / GiReprojectUnlit (64 threads, indirect)

Thread `i` processes sample `start + shuffle(i)`, `shuffle(i) = (coprime * group + frame) % groups * 64 + lane`,
skipped when `>= valid`. The shuffle makes the 32 samples that win a bucket slot a random subset
every frame instead of always the oldest.

```
inst = g_Instances[slot]
if inst.lifetimeToken != sample.token: return          // instance slot was reused
P = ObjectToWorld(inst) * sample.objectPosition         // current world position of the primary
Pv = P + sample.virtualOffset
texel = project(Pv); if invalid: return
info = pixelInfo[texel]; dist = length(Pv - cam)
if dist < min * (1022/1024) or dist > max * (1026/1024): return
if hash16 != info.centre and (info.bloom & (1 << (hash16 & 15))) == 0: return
bucket = texel / 8
lit:   k = InterlockedAdd(bucket.litCount, 1); if k < 32: refined[bucket * 32 + k] = { P, normals, secondaryPosition, radiance }
unlit: InterlockedAdd(bucket.unlitCount, unlitKeepDivisor)
```

The sample's primary normal is not re-derived; it is carried through as the axis index.

### GiRefineBuckets (64 threads, one per bucket)

```
lit = min(litCount, 32); unlitEff = unlitCount * (lit / max(litCount, 1))   // NAADF: scale unlit down when lit overflowed
ratio0 = litCount / max(litCount + unlitCount, 1)
maxLum = max over the lit samples of luminance(radiance)
extraUnlit = 0; stored = 0
for each lit sample i (slot order):
    r = sampleLeveling ? maxLum / max(lum_i, 1e-6) : 1
    if GiRand() * r > 1: extraUnlit++; continue          // removed with probability 1 - 1/r
    if stored < 8: compact[bucket * 8 + stored++] = sample with radiance * r
litRatio = (lit - extraUnlit) / max(1, lit - extraUnlit + unlitEff + extraUnlit)
if lit + unlitEff < minBucketSamples: stored = 0
write packed (stored, normalMask kept, totalComp = min(7, (litCount + unlitCount) / 64)), litRatio, maxLum
```

The leveling scale `r` is baked into the compact record's radiance (the spatial pass has no
per-sample data to recompute it). The lit ratio is applied at use time (`radiance * litRatio` in
the spatial pass), because it is a bucket property.

### GiSpatial (8x8 threads per pixel)

Skip pixels without flags bit 1; write `DiffuseRadianceTarget = 0`. Otherwise:

```
P, n, hash from the G-buffer; V = normalize(cam - P); myAxis = GiAxisIndex(n); myDist = GiSurface.w
radius = spatialRadius * (adaptiveRadius ? AdaptiveFactor() : 1)
sumW = 0; M = 0; selected = none
for it in 0 .. spatialIterations-1:
    offset = (GiRand2() - 0.5) * radius; texel = mirror(pixel + offset) into the image
    b = bucketInfo[texel / 8]
    if (b.normalMask & (1 << myAxis)) == 0: continue
    distFac = 0.95 * max(0.2, pow(max(dot(n, V), 0), 0.25))
    if myDist < b.min * distFac or myDist > min(b.min * 2, b.max) / distFac: continue
    if b.stored == 0: M += 1; continue                   // an all-unlit bucket counts as one zero sample
    s = compact[bucket * 8 + GiRand() * b.stored]
    if s.primaryAxis != myAxis: continue
    if sky: d = s.secondaryPosition; J = 1; dist2 = inf
    else:   seg = s.secondaryPosition - P; dist2 = dot(seg, seg); d = seg * rsqrt(dist2)
            cosQnew = dot(nq, -d); segOld = s.secondaryPosition - s.primaryPosition
            cosQold = dot(nq, -normalize(segOld)); J = (cosQnew / cosQold) * dot(segOld, segOld) / dist2
            if cosQold <= 1e-7 or cosQnew <= 0: continue
    cosNew = dot(n, d); if cosNew <= 1e-4: continue
    if J < jacobianMin or J > jacobianMax: continue
    c = s.radiance * b.litRatio
    pHat = luminance(c) * cosNew
    // Source pdf: the sample was drawn cosine-distributed at its own primary surface, whose
    // normal is the same axis as mine (checked above). Sky samples use d itself as segOld.
    pdfOld = max(dot(n, sky ? d : normalize(segOld)), 1e-4) / PI
    w = pHat * J / pdfOld
    M += 1; sumW += w
    if GiRand() * sumW < w: selected = (c, d, dist2, pHat)
if selected is none or sumW <= 0: write 0
visible = trace shadow ray from P (push-off) along d with TMax = sqrt(dist2) - 2 * pushOff (sky: K_T_MAX) and no hit
if !visible: write 0
W = sumW / (M * pHat_sel)
L = c_sel * cosNew_sel * W / PI            // incident radiance in the cosine-sampled convention
DiffuseRadianceTarget = float4(L, sqrt(dist2))
```

Check: a pixel's own cosine sample (J = 1, M = 1, pdfOld = cosNew / PI) gives `L = c`, which is what
the tracer writes today, so the composite (`albedo * indirect`) is unchanged.

`AdaptiveFactor()` is NAADF's `isVaryingResmaplingRadius` probe: 12 probes, 6 at 0.1 radius and 6
at full radius, each accepted by the same mask and distance tests; it combines the probes' lit
ratio, stored count, total count and maximum luminance into a factor in `[0.07, 1.07]` with
`radiusLitFactor` as the knob. Port the formula from `renderSpatialResampling.fx` lines 82-149
verbatim, using `litRatio`, `stored`, `totalComp` and `maxLuminance` from `GiBucketInfo`.

## Settings (serialized on the feature)

| Field | Default | Meaning |
|---|---|---|
| `sampleRateDivisor` | 1 | A pixel traces a new sample every N frames (1 = every frame). |
| `skySampleProbability` | 1.0 | Chance a sky hit is kept (weighted by 1/p). Lower it when the sky is bright and storage is tight. |
| `litThreshold` | 1.0 | Luminance below which a sample is stochastically dropped or boosted (NAADF leveling). |
| `unlitKeepDivisor` | 8 | One unlit sample in N is stored, weighted N. |
| `litRingFrames` | 2 | Lit ring capacity in frames of pixels. |
| `unlitRingFrames` | 4 | Unlit ring capacity in frames of pixels. |
| `maxHistoryFrames` | 64 | Oldest frame whose samples are still reprojected (at most 128). |
| `spatialIterations` | 12 | Reservoir iterations per pixel. |
| `spatialRadius` | 500 | Edge length in pixels of the square the neighbour offsets are drawn from (NAADF's `spatialResampleSize`), before the adaptive factor. |
| `adaptiveRadius` | true | NAADF's varying radius. |
| `radiusLitFactor` | 3.0 | Knob of the adaptive radius. |
| `sampleLeveling` | true | Remove dim samples per bucket and boost the rest. |
| `minBucketSamples` | 12 | A bucket with fewer lit + unlit samples is not used. |
| `jacobianMin` / `jacobianMax` | 0.3 / 2.5 | Reconnection Jacobian gate. |

Plus the tracer, denoiser, colour resolve and debug fields that `CaelixRendererFeature` already
has, copied, because `CaelixGBufferPass` and `CaelixDenoisePass` take the same settings objects.

Memory at 2560x1440 with the defaults: lit ring 413 MB, unlit ring 531 MB, buckets 7 MB, pixel info
30 MB. At 1280x720 a quarter of that.

## Status 2026-10-12

Implemented and validated on Bistro (see `Artifacts/gi-resampling-20261012/README.md`). Two defaults differ
from the design table above: `spatialRadius` is 160 (500 doubled the occlusion loss on walls) and a new
`ownSampleFallback` (default on) writes the pixel's own fresh sample instead of black when no candidate is
usable or the chosen reconnection is occluded; it lifted the final brightness from 0.81-0.92 to 0.93-0.95 of
the path tracer's. `debugMode` (0..4) colours the spatial pass's outcome for diagnosis. Per-kernel GPU time at
1440p: GenerateSamples 8.5 ms, Spatial 2.5, reprojection 1.6, prep and refine 0.9; the whole frame is 20 ms
against 14.8 ms for the path tracer, and `sampleRateDivisor` is the lever (NAADF runs at 0.25 spp).

## Known approximations

- A sample whose first hit is a mirror or glass voxel reconnects at that voxel.
- Refraction has no true virtual point; `path.T` along the camera ray is used, as the motion
  vectors already do.
- Empty buckets count as one zero sample in the spatial pass (NAADF). This darkens hard areas; the
  pixel's own sample is deliberately not a fallback.
- The primary surface's medium is assumed to be air for the new sample's first segment.
