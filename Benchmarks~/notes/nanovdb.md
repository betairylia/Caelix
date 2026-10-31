# NanoVDB as a VoxelRT method: source, CPU build, PNanoVDB HDDA, numbers, edits

Research notes, 2026-10-18. Every API statement below was read from the fetched headers
(line references are to the files in `third_party/nanovdb/` at the tag below). The C++ build
code and the shader in this file were compile-checked (clang 22.1.3 `-std=c++23
-fsyntax-only`, slangc 2024.17 from Vulkan SDK 1.4.304.1 + `spirv-val --target-env
vulkan1.3`). Nothing was run: no build time and no GPU time is measured here.

## 1. Source, version, license

| Item | Value |
| --- | --- |
| Repository | https://github.com/AcademySoftwareFoundation/openvdb, folder `nanovdb/nanovdb/` |
| Latest release | `v13.1.0`, published 2026-09-16 (previous: v13.0.0 2025-11-04, v12.1.1 2025-09-30) |
| Tag commit | `e26f5a569201d3236e0c30580572101022d3d754` |
| NanoVDB ABI in this tag | `NANOVDB_MAJOR/MINOR/PATCH_VERSION_NUMBER` = 32.9.2 (`NanoVDB.h:146-148`, same in `PNanoVDB.h`) |
| License | **Apache-2.0**. Root `LICENSE` is Apache 2.0; every fetched header has `SPDX-License-Identifier: Apache-2.0`. `RE-LICENSE_NOTE.txt` records that the contributors (NVIDIA, DreamWorks, SideFX, ...) relicensed their MPL-2.0 contributions to Apache-2.0. MPL-2.0 applied to old releases only. |
| Raw URL pattern | `https://raw.githubusercontent.com/AcademySoftwareFoundation/openvdb/v13.1.0/nanovdb/<path>` |

Fetched set (`third_party/nanovdb/`, layout `nanovdb/...`, so `-I third_party/nanovdb` makes
`#include <nanovdb/NanoVDB.h>` work). The set is the include closure of the entry headers,
CUDA headers excluded. `util/Util.h:121` names `nanovdb/path/Alternative.h` in a comment only.

| SHA256 | Bytes | File |
| --- | ---: | --- |
| `196396741bcc7ac3acaa06fcb3d08307bb7a5eba2d4a55f58695f76fa4b69d71` | 38115 | `nanovdb/GridHandle.h` |
| `f0eb5c1629c3068c09ed25b1a6555c5a3813f7ecca6f33aa3c4784aaaf250af5` | 25289 | `nanovdb/HostBuffer.h` |
| `ce92f53f079d753cef14fd915750dbd22ffddc4cedba7c01a2629bdaa6f2a37c` | 295676 | `nanovdb/NanoVDB.h` |
| `b794b427a8e2a7b390b88cd51870f8290370ab16389719e4c085cc9850866988` | 15801 | `nanovdb/NodeManager.h` |
| `b5eed25267271d9a0d0dae45936079b0bc3cbc0c8ca26d9cd9f9ca86f1ec7966` | 177802 | `nanovdb/PNanoVDB.h` |
| `3affbe2b8b9f738b4e5e8e70ddde1d0da3fc2e005f58a1ae96c2e290a5d3b976` | 773 | `nanovdb/Readme.md` |
| `6f463d1cf7aeddc2430d93dd445eeeb56145970141aeb7aa05fd893b7b1ee6cc` | 35274 | `nanovdb/io/IO.h` |
| `56a801714b9c65e40a460ad3e7b40b2056534b163be9fc4d6b173e542e9195ac` | 9979 | `nanovdb/math/DitherLUT.h` |
| `ce6dd978a2ea087ffa72a6e90fa3fca0be19e2dc75592b723bfe1fac7bef0ac3` | 21493 | `nanovdb/math/HDDA.h` |
| `885d30e29e074842776bd31cbb946595630df033b24131a6c8212aec0632adc1` | 83903 | `nanovdb/math/Math.h` |
| `fb8cf86fb04d38c4368d1e33846450c7d3f83baa71c39d1b9d215fda86b1f70d` | 19938 | `nanovdb/math/Ray.h` |
| `7ec552e349e87df9850231f4a370d9f48a530e48202c36848c85cd80428e5aea` | 104035 | `nanovdb/tools/CreateNanoGrid.h` |
| `16390379aa1646395754b5c48b528a795e44cf03d4cc24b096aa0536e8d46ee6` | 92179 | `nanovdb/tools/GridBuilder.h` |
| `e66dae2f8f0b63370c34a5286d53d76fe6b3c91cc42673ebc71c0ed8aef528f0` | 17652 | `nanovdb/tools/GridChecksum.h` |
| `9575e6167fbcad6920325ec647a64c9f81f1683db3d50434f4becb22b81e1a22` | 32734 | `nanovdb/tools/GridStats.h` |
| `9bed00048e6c85ba0a2e5befdf97a1731cb1fde5cf13905f75db3ada55cb0594` | 10655 | `nanovdb/tools/GridValidator.h` |
| `3e5509a1cd748a570244ad4f639e5f504fdf2ecbfa6708605742030409065b44` | 3981 | `nanovdb/util/ForEach.h` |
| `7c689c7e7bd2fdd771f468f276785a8c4495dbf386a2e4396cd289a4429beb4e` | 2561 | `nanovdb/util/Invoke.h` |
| `73d95937ee1264f58ae8a50ba809eda34157888122650b02d8df9dc2a4acce76` | 3059 | `nanovdb/util/PrefixSum.h` |
| `4c07b3378d7657c7f5296ef74ac380bdcd27bfdff594859f18c4f0940111a379` | 6240 | `nanovdb/util/Range.h` |
| `1f920c929baa705ed730c02d65db2626a93d396ab2dc3f2b52bff7be180954ff` | 5074 | `nanovdb/util/Reduce.h` |
| `e4d535c4a8b4c5f89b78e414a1c6bcc9f3fa0547461669759514a9d02d6ca0cd` | 30823 | `nanovdb/util/Util.h` |

Build notes: header-only, no OpenVDB, no TBB needed. `util/ForEach.h` uses TBB when
`NANOVDB_USE_TBB` is defined, otherwise `std::thread` with `hardware_concurrency() >> 1`
threads (14 on the i7-14700KF). `io/IO.h` needs zlib/blosc only with `NANOVDB_USE_ZIP` /
`NANOVDB_USE_BLOSC`. On MSVC headers define `_CRT_SECURE_NO_WARNINGS` (fopen/strncpy
deprecation warnings, no errors).

## 2. Tree layout, node sizes, grid size formula

Fixed configuration (PNanoVDB supports only this one):

| Level | Node | Fan-out | Voxel span | Size in bytes (all value types below) |
| --- | --- | --- | --- | --- |
| 3 | Root | sorted table of tiles, key = `ijk >> 12` per axis | unbounded | 64 + 32 per tile (OnIndex/Index: 96 + 32 per tile) |
| 2 | Upper internal | 32^3 = 32768 | 4096^3 | **270,400** (32768 x 8 B tile/child table + 2 x 4096 B masks + 64 B header) |
| 1 | Lower internal | 16^3 = 4096 | 128^3 | **33,856** (4096 x 8 B + 2 x 512 B masks + 64 B header) |
| 0 | Leaf | 8^3 = 512 | 8^3 | depends on value type, table below |

Leaf size per value type (`PNanoVDB.h:1897` `pnanovdb_grid_type_constants`, field `leaf_size`;
the header is 80 B = bbox min 12 + bbox dif/flags 4 + value mask 64):

| Build type | GridType | Leaf bytes | What the leaf holds | Material id in the grid? |
| --- | --- | ---: | --- | --- |
| `ValueMask` | Mask (8) | 96 | active mask only | no |
| `ValueOnIndex` | OnIndex (20) | 96 | mask + `mOffset` (u64) + 9-bit prefix sums (u64) | yes, through a sidecar: 1 B per active voxel (+1 background) |
| `ValueIndex` | Index (19) | 96 | mask + `mOffset` (512 slots per leaf) | yes, sidecar 512 B per leaf |
| `bool` | Boolean (11) | 160 | mask + 512 bits | no |
| `Fp4` | Fp4 (13) | 352 | 4-bit quantized float | lossy, no |
| `uint8_t` | UInt8 (26) | **608** | mask + stats + 512 x u8 (values of inactive voxels are stored too) | yes, in the leaf |
| `Fp8` | Fp8 (14) | 608 | 8-bit quantized float (min + quantum per leaf) | lossy, do not use for ids |
| `uint32_t`, `float` | UInt32 (10), Float (1) | 2144 | 512 x 4 B | yes, 4x waste |

`uint8_t` IS a supported build type: `GridType::UInt8 = 26` (`NanoVDB.h:245`), mapped by
`toGridType<uint8_t>()` (`NanoVDB.h:899`), present in PNanoVDB as `PNANOVDB_GRID_TYPE_UINT8`
with its own stride row. `BuildToValueMap<uint8_t>` is the identity.

Grid buffer (single contiguous buffer, breadth-first: grid, tree, root, uppers, lowers,
leaves, blind metadata, blind data; `CreateNanoGrid.h:937-946`):

```
bytes = 672 (GridData) + 64 (TreeData) + root (64|96 + 32 * N_upper)
      + 270400 * N_upper + 33856 * N_lower + leafBytes * N_leaf
      [+ 288 per blind-data channel + align32(channel bytes)]
```

For an OnIndex grid with one uint8 channel, channel bytes = 1 + activeVoxelCount.

Coordinates are signed 32-bit, so 8192+ extents are fine. The HDDA works in float; at
coordinates near 12k the float spacing is about 0.001 voxel, which is fine.

### Measured node counts and predicted sizes

`scratchpad/nvdb_size.py` read only the sector index and the uncompressed brick previews
(no payload inflate) and counted leaves, lower and upper nodes exactly (a cxw sector is
128^3 = one NanoVDB lower node when the origin is 128-aligned):

| Scene | Bricks = leaves | Lower | Upper | Leaves / lower | Mask or OnIndex grid | UInt8 grid | Float/UInt32 grid |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `epic-citadel.8192.packed.cxw` (8192x1272x7800) | 1,095,051 | 3,837 | 4 | 285 | 0.220 GiB | 0.742 GiB | 2.31 GiB |
| `concretecore-full.cxw` (12288x2768x12288, 6 entities) | 4,207,273 | 14,407 | 16 | 292 | 0.834 GiB | 2.841 GiB | 8.86 GiB |

Prediction for the target world (8192x2304x8192, 300 M active voxels, 4.2 M bricks): upper
nodes = 2 x 1 x 2 = 4 if the origin is 4096-aligned (up to 18 if not; align the origin). Both
real scenes have about 290 leaves per lower node, so N_lower is about 14,500 (the dense
upper bound is 64 x 18 x 64 = 73,728 lower nodes = 2.3 GiB, so count it from the data).

| Variant | Size |
| --- | --- |
| Mask (occupancy only) | 0.46 GiB grid+tree+root+upper+lower (lower nodes 0.457 GiB) + 0.38 GiB leaves = **0.83 GiB** |
| OnIndex + 1 uint8 channel (300 M B sidecar in the same buffer) | **about 1.11 GiB** (recommended) |
| UInt8 | **about 2.84 GiB** (fits the 4 GiB 32-bit address limit) |
| Float / UInt32 | about 8.9 GiB (over the 4 GiB PNanoVDB 32-bit address limit) |

Lower nodes are more than half of the Mask/OnIndex footprint (8 B per child slot, 4096 slots).
This is fixed by the format. Compare: the Caelix-Renderer holds the citadel in 1.37 GiB of records.

## 3. CPU build cookbook (no OpenVDB)

Types live in `nanovdb::tools::build` (`tools/GridBuilder.h`): `Grid<BuildT>` derives from
`Tree<BuildT>` which owns `RootNode<InternalNode<InternalNode<LeafNode<BuildT>>>>`. Conversion
to the linear NanoVDB buffer: `nanovdb::tools::createNanoGrid` (`tools/CreateNanoGrid.h`).

Ways to fill a `build::Grid`, from slowest to fastest:

1. `acc = grid.getAccessor(); acc.setValue(ijk, v)` per voxel. The accessor caches the leaf,
   lower and upper node (3 keys), so coherent writes are cheap, but it is 300 M calls on one
   thread. `getAccessor()` is "thread-safe reading and non-thread-safe writing".
2. `grid.getWriteAccessor()` per thread: each thread writes into a private `RootNode`, and the
   destructor merges it into the shared root under a mutex (`Tree::WriteAccessor`,
   `GridBuilder.h:1844`).
3. `grid(func, bbox)`: the functor build, parallel over leaves. **It prunes**: a fully active
   leaf with one uniform value becomes an active lower-node tile, and uniform lower/upper nodes
   become tiles (`GridBuilder.h:1958-2004`). Solid interior bricks then turn into tiles, which
   the occupancy HDDA below does not treat as hits. Do not use it, or handle active tiles.
4. **Leaf level (recommended):** construct `build::LeafNode<BuildT>` objects directly in
   parallel (one per brick, plain `new`, independent), write `mValueMask` words and `mValues`,
   then insert them serially with `root.addNode(leaf)`. `addNode` creates the upper/lower nodes
   on demand, takes ownership and sets the pointer to `nullptr` (`GridBuilder.h:531-551`,
   `addChild` deletes an existing child with the same origin). No pruning happens on this path,
   and `createNanoGrid` does not prune either.

Leaf voxel order: `n = (x & 7) << 6 | (y & 7) << 3 | (z & 7)` (z fastest,
`GridBuilder.h:1196`, `PNanoVDB.h:2052`). The cxw brick order is `x + 8y + 64z` (x fastest):
transpose when copying a brick.

Code (compile-checked; `scratchpad/cpptest/build_test.cpp`):

```cpp
#include <nanovdb/NanoVDB.h>
#include <nanovdb/GridHandle.h>
#include <nanovdb/HostBuffer.h>
#include <nanovdb/tools/GridBuilder.h>
#include <nanovdb/tools/CreateNanoGrid.h>
#include <execution>
#include <numeric>
#include <span>

struct Brick { int32_t bx, by, bz; const uint8_t* ids; }; // ids[x + 8y + 64z], 0 = air
namespace nb = nanovdb::tools::build;

template<typename BuildT>
static nb::LeafNode<BuildT>* makeLeaf(const Brick& b)
{
    auto* leaf = new nb::LeafNode<BuildT>(nanovdb::Coord(b.bx * 8, b.by * 8, b.bz * 8), {}, false);
    uint64_t* words = leaf->mValueMask.words();
    for (uint32_t z = 0; z < 8; ++z)
        for (uint32_t y = 0; y < 8; ++y)
            for (uint32_t x = 0; x < 8; ++x)
            {
                const uint8_t id = b.ids[x + 8 * y + 64 * z];
                if (id == 0) continue;
                const uint32_t n = (x << 6) | (y << 3) | z;
                words[n >> 6] |= uint64_t(1) << (n & 63);
                if constexpr (!std::is_same_v<BuildT, nanovdb::ValueMask>) leaf->mValues[n] = id;
            }
    if (leaf->mValueMask.isOff()) { delete leaf; return nullptr; }
    return leaf;
}

template<typename BuildT>
static void fill(nb::Grid<BuildT>& src, std::span<const Brick> bricks)
{
    std::vector<nb::LeafNode<BuildT>*> leaves(bricks.size());
    std::vector<size_t> idx(bricks.size());
    std::iota(idx.begin(), idx.end(), size_t(0));
    std::for_each(std::execution::par, idx.begin(), idx.end(),
                  [&](size_t i) { leaves[i] = makeLeaf<BuildT>(bricks[i]); });
    auto& root = src.tree().root();
    for (auto*& leaf : leaves)
        if (leaf) root.addNode(leaf); // takes ownership, sets leaf = nullptr
}

// (a) UInt8 grid: ids in the leaves.
nanovdb::GridHandle<nanovdb::HostBuffer> buildUInt8(std::span<const Brick> bricks)
{
    nb::Grid<uint8_t> src(uint8_t(0), "caelix", nanovdb::GridClass::VoxelVolume);
    fill(src, bricks);
    return nanovdb::tools::createNanoGrid(src, nanovdb::tools::StatsMode::BBox,
                                          nanovdb::CheckMode::Disable);
}

// (b) OnIndex grid + one uint8 sidecar channel stored as blind data in the same buffer.
nanovdb::GridHandle<nanovdb::HostBuffer> buildOnIndex(std::span<const Brick> bricks)
{
    nb::Grid<uint8_t> src(uint8_t(0), "caelix");
    fill(src, bricks);
    return nanovdb::tools::createNanoGrid<nb::Grid<uint8_t>, nanovdb::ValueOnIndex>(
        src, /*channels*/ 1u, /*includeStats*/ false, /*includeTiles*/ false);
}

// (c) Mask grid: occupancy only (material must come from elsewhere).
nanovdb::GridHandle<nanovdb::HostBuffer> buildMask(std::span<const Brick> bricks)
{
    nb::Grid<nanovdb::ValueMask> src(false, "caelix");
    fill(src, bricks);
    return nanovdb::tools::createNanoGrid(src, nanovdb::tools::StatsMode::BBox,
                                          nanovdb::CheckMode::Disable);
}
```

Use of the result: `handle.data()` / `handle.bufferSize()` is the GPU upload (32 B aligned,
grid at offset 0); `handle.grid<uint8_t>()` / `handle.grid<nanovdb::ValueOnIndex>()`;
`grid->tree().nodeCount(0|1|2)`; `grid->indexBBox()`; for OnIndex
`grid->getBlindData<uint8_t>(0)` (element 0 = background) and `grid->valueCount()`.
`nanovdb::io::writeGrid(path, handle)` / `io::readGrid(path)` cache a build as `.nvdb` (a
memcpy-like load). `nanovdb::tools::validateGrid` (`tools/GridValidator.h`) checks a custom
build.

Notes:

* **Stats and HDDA.** `createNanoGrid` sets the root bbox to empty (`CreateNanoGrid.h:1600,
  1629`) and fills it only in `updateGridStats` (`postProcess`, line 1741). PNanoVDB's HDDA
  clips the ray against the root bbox, so `StatsMode::Disable` gives a grid every ray misses.
  Use `StatsMode::BBox` (bbox + active voxel count only, cheapest valid mode). The default
  `StatsMode::Default` = `All` also computes min/max/average/std-dev (not needed). For index
  grids `postProcess` always runs `min(BBox, mStats)` (line 1823). The HDDA does not use
  min/max; it uses only the root bbox and the child/value masks.
* **OnIndex indices.** Index 0 is the background; active voxels get 1..N in leaf order
  (`CreateNanoGrid.h:1129`). With `includeStats = true` each leaf also reserves 4 stat slots;
  with `includeTiles = true` active tile values are indexed too. Pass `false, false`.
* **Memory-light OnIndex variant.** Building from `build::Grid<uint8_t>` costs about 600 B per
  build leaf (2.5 GB for 4.2 M leaves) before the 1.1 GiB output. Alternative: build from
  `build::Grid<ValueMask>` (about 90 B per build leaf), convert with `channels = 0`, then fill
  your own uint8 sidecar by walking the NanoVDB leaves: value index =
  `leaf.mOffset + rank of the bit in the mask` (same formula as
  `pnanovdb_leaf_onindex_get_value_index`).
* **Build time is not measured.** The leaf phase is parallel; `addNode` and `createNanoGrid`'s
  allocation are serial; the conversion passes are parallel through `util::forEach`.
* **Peak RAM** for the UInt8 path at 4.2 M bricks: about 2.5 GB build tree + 2.84 GiB output.

## 4. PNanoVDB.h in HLSL / Slang

### Configuration macros (`PNanoVDB.h:17-60`)

* `#define PNANOVDB_HLSL` selects HLSL. It implies `PNANOVDB_ADDRESS_32` (unless
  `PNANOVDB_ADDRESS_64` is defined) and `PNANOVDB_HDDA` (the HDDA is on by default for HLSL
  and GLSL, opt-in for C).
* `pnanovdb_address_t` is `struct { uint byte_offset; }` in 32-bit mode: **the grid buffer must
  be below 4 GiB**. `pnanovdb_address_offset64` drops the high bits silently.
  `pnanovdb_uint64_t` is `uint2` in this mode.
* Default buffer (`PNANOVDB_BUF_HLSL`): `#define pnanovdb_buf_t StructuredBuffer<uint>`
  (`RWStructuredBuffer` with `PNANOVDB_BUF_HLSL_RW`; `uint2` elements with
  `PNANOVDB_BUF_HLSL_64`). Writes are no-ops unless `PNANOVDB_BUF_HLSL_RW`.
* `PNANOVDB_BUF_BOUNDS_CHECK` affects **only** the C buffer path (`pnanovdb_buf_t` with a
  word count). It does nothing for HLSL or a custom buffer. In Vulkan, descriptor buffers get
  robust-access zeros out of bounds; device-address reads get no protection.
* `PNANOVDB_ADDRESS_64` in HLSL uses `uint64_t` byte offsets, but the default
  `StructuredBuffer` path still indexes with `uint(byte_offset >> 2)` (16 GiB of words). A
  custom 64-bit device-address reader removes the 4 GiB limit; internal NanoVDB offsets are 64-bit.

### Custom buffer (`PNANOVDB_BUF_CUSTOM`): the clean path for bindless / BDA

With `PNANOVDB_BUF_CUSTOM` the header defines no buffer code (`PNanoVDB.h:62-63`). Before the
include you must define, using only native HLSL types (the `pnanovdb_*` scalar typedefs do not
exist yet at that point):

```hlsl
#define PNANOVDB_HLSL
#define PNANOVDB_BUF_CUSTOM
#define pnanovdb_grid_type_t uint
#define PNANOVDB_GRID_TYPE_GET(grid_typeIn, nameIn) pnanovdb_grid_type_constants[grid_typeIn].nameIn
// variant 1: ByteAddressBuffer (or an element of a bindless array)
[[vk::binding(0, 0)]] ByteAddressBuffer g_grid;
struct pnanovdb_buf_t { uint unused; };
uint  pnanovdb_buf_read_uint32(pnanovdb_buf_t buf, uint byte_offset) { return g_grid.Load(byte_offset); }
uint2 pnanovdb_buf_read_uint64(pnanovdb_buf_t buf, uint byte_offset) { return g_grid.Load2(byte_offset); }
// variant 2: buffer device address (Slang pointer, SPIR-V PhysicalStorageBufferAddresses)
//   struct pnanovdb_buf_t { uint* base; };
//   uint  pnanovdb_buf_read_uint32(pnanovdb_buf_t b, uint o) { return b.base[o >> 2]; }
//   uint2 pnanovdb_buf_read_uint64(pnanovdb_buf_t b, uint o) { return uint2(b.base[o >> 2], b.base[(o >> 2) + 1]); }
void pnanovdb_buf_write_uint32(pnanovdb_buf_t buf, uint byte_offset, uint value) {}
void pnanovdb_buf_write_uint64(pnanovdb_buf_t buf, uint byte_offset, uint2 value) {}
#include <nanovdb/PNanoVDB.h>
```

`pnanovdb_buf_read_uint64` must return `uint2` in 32-bit mode (it returns
`pnanovdb_uint64_t`). The write functions are required because setter functions call them.

### Slang status (checked)

The test shader (`scratchpad/slangtest/nvdb_trace.slang`: default StructuredBuffer,
custom ByteAddressBuffer, custom `uint*` BDA; UInt8 and OnIndex grid types) compiles with
slangc 2024.17 `-target spirv -profile glsl_460 -stage compute` with no errors and no
warnings, and all six SPIR-V modules pass `spirv-val --target-env vulkan1.3`. No source edits
to PNanoVDB.h were needed. Capabilities emitted: Shader (+ PhysicalStorageBufferAddresses for
BDA); no Int64/Float64 (the double helpers are dead code). Recheck with the harness's own
Slang version.

Pitfalls:

* Pass grid types as **literals or `static const`** (`PNANOVDB_GRID_TYPE_UINT8`). Then
  `PNANOVDB_GRID_TYPE_GET` folds to constants (confirmed: no array left in the SPIR-V). A
  runtime grid type indexes a 32 x 28 uint `static const` table per access (local memory).
* `pnanovdb_root_find_tile` is a linear scan over root tiles. That is fine for 4..16 tiles.
* `pnanovdb_hdda_zero_crossing` is for level sets (sign change of a float value, reads the
  grid as FLOAT) and is wrong for occupancy. The C++ `nanovdb::math::firstActive`
  (`math/HDDA.h:430`) is the occupancy version but exists only in C++. Both probe the next cell
  at `t + 1.0001` voxel units, which can skip a voxel the ray only clips at a corner. The
  example below queries the entry voxel, clamped into the current cell, instead.
* `pnanovdb_hdda_ray_clip` divides by the direction: a zero component with the origin exactly
  on a bbox plane gives `0 * inf = NaN`. Avoid exact-zero directions or nudge them.

### Is the HDDA hierarchical?

Yes. `pnanovdb_readaccessor_get_dim` (`PNanoVDB.h:2952`) returns the span of the empty node
that contains `ijk`: **4096** (no root tile or a root tile value), **128** (tile inside an
upper node, no lower child), **8** (tile inside a lower node, no leaf), **1** (inside an
existing leaf, active or not). `pnanovdb_hdda_update` re-aligns the DDA cell to the new size;
`pnanovdb_hdda_step` advances one cell of the current size. So empty space is skipped in
steps of 4096 / 128 / 8 voxels, and every allocated leaf is walked voxel by voxel, even an
all-empty one. The read accessor (`key` + leaf/lower/upper/root handles) caches the last path,
so consecutive queries in the same leaf cost one mask read.

### Example: first active voxel (UInt8 or OnIndex grid), index space

The grid is built with the default identity transform, so index space = voxel units. Returns
hit voxel, entry-face normal, entry `t`, material.

```hlsl
static const uint GT = PNANOVDB_GRID_TYPE_UINT8; // or PNANOVDB_GRID_TYPE_ONINDEX
struct NvdbHit { bool hit; float t; int3 ijk; int3 normal; uint material; uint steps; };

NvdbHit nvdb_trace(pnanovdb_buf_t buf, float3 o, float3 d, float tmin, float tmax)
{
    NvdbHit h = (NvdbHit)0;
    pnanovdb_grid_handle_t grid = { pnanovdb_address_null() };
    pnanovdb_root_handle_t root = pnanovdb_tree_get_root(buf, pnanovdb_grid_get_tree(buf, grid));
    pnanovdb_readaccessor_t acc;
    pnanovdb_readaccessor_init(acc, root);
    // 1. clip to the root bbox (needs StatsMode >= BBox); keep the entry axis for the normal
    int3 ibmin = pnanovdb_root_get_bbox_min(buf, root);
    int3 ibmax = pnanovdb_root_get_bbox_max(buf, root);
    float3 inv = 1.0f / d;
    float3 t0 = (float3(ibmin) - o) * inv, t1 = (float3(ibmax + 1) - o) * inv;
    float3 tn = min(t0, t1), tf = max(t0, t1);
    float tnear = max(tn.x, max(tn.y, tn.z)), tfar = min(tf.x, min(tf.y, tf.z));
    int axis = (tn.x >= tn.y && tn.x >= tn.z) ? 0 : (tn.y >= tn.z ? 1 : 2);
    if (tnear > tmin) { tmin = tnear; } else { axis = -1; } // origin inside: no entry face
    tmax = min(tmax, tfar);
    if (tmin > tmax) return h;
    // 2. hierarchical DDA, cell size from get_dim (4096 / 128 / 8 / 1)
    int3 ijk = clamp(int3(floor(o + d * tmin)), ibmin, ibmax);
    int dim = int(pnanovdb_readaccessor_get_dim(GT, buf, acc, ijk));
    pnanovdb_hdda_t hdda;
    pnanovdb_hdda_init(hdda, o, tmin, d, tmax, dim);
    for (uint it = 0; it < 65536u; ++it)
    {
        h.steps = it;
        // Only leaf voxels can hit: the builder in section 3 never creates active tiles.
        if (hdda.dim == 1 && pnanovdb_readaccessor_is_active(GT, buf, acc, hdda.voxel))
        {
            h.hit = true; h.t = hdda.tmin; h.ijk = hdda.voxel; h.normal = int3(0, 0, 0);
            if (axis >= 0) { h.normal[axis] = -hdda.step[axis]; }
            pnanovdb_address_t a = pnanovdb_readaccessor_get_value_address(GT, buf, acc, h.ijk);
            if (GT == PNANOVDB_GRID_TYPE_UINT8)
            {
                h.material = pnanovdb_read_uint8(buf, a);
            }
            else // ONINDEX: index into channel 0 (blind data in the same buffer), 0 = background
            {
                pnanovdb_uint64_t idx = pnanovdb_leaf_onindex_get_value_index(buf, a, h.ijk);
                pnanovdb_address_t side = pnanovdb_grid_get_gridblindmetadata_value_address(buf, grid, 0u);
                h.material = pnanovdb_read_uint8(buf, pnanovdb_address_offset(side, pnanovdb_uint64_low(idx)));
            }
            return h;
        }
        // axis of the next face crossing (same test as pnanovdb_hdda_step)
        axis = (hdda.next.x < hdda.next.y && hdda.next.x < hdda.next.z) ? 0 : (hdda.next.y < hdda.next.z ? 1 : 2);
        if (!pnanovdb_hdda_step(hdda)) break;
        // entry voxel of the new cell, clamped into the cell against float round-off
        ijk = clamp(int3(floor(o + d * hdda.tmin)), hdda.voxel, hdda.voxel + hdda.dim - 1);
        dim = int(pnanovdb_readaccessor_get_dim(GT, buf, acc, ijk));
        pnanovdb_hdda_update(hdda, o, d, dim); // no-op if dim is unchanged
    }
    return h;
}
```

Why the clamp is consistent: `pnanovdb_hdda_update` computes the new cell as
`floor(pos(tmin)) & ~(dim-1)` clamped into the old cell, which equals `ijk & ~(dim-1)` for the
clamped `ijk` above, so the `dim` that was queried belongs to the cell the DDA moves to.

Per-voxel material access by grid type:

| Grid | Read |
| --- | --- |
| UInt8 | `pnanovdb_read_uint8(buf, pnanovdb_readaccessor_get_value_address(PNANOVDB_GRID_TYPE_UINT8, buf, acc, ijk))`. Byte address = leaf + 96 + n; `read_uint8` loads the aligned word and shifts. |
| OnIndex | `idx = pnanovdb_leaf_onindex_get_value_index(buf, valueAddr, ijk)` (popcount of the mask below n + 9-bit prefix sum + `mOffset`), then 1 byte at `blindDataAddress(0) + idx`. For tiles use `pnanovdb_readaccessor_get_value_address_and_level` + `pnanovdb_root_onindex_get_value_index(buf, addr, ijk, level)`. |
| Index | `pnanovdb_leaf_index_get_value_index` = `mOffset + n` (512 slots per leaf). |
| Mask | no value; `get_value_address` returns an address past the 96 B leaf. Material must come from a separate structure. |
| Fp8 / Fp4 | `pnanovdb_leaf_fp8_read_float` = `q * quantum + minimum` per leaf: lossy float, not for ids. |

Bounce rays: start at `hit point + normal * 1e-3` with `tmin = 0`; reusing the accessor from
the primary ray is allowed (it is only a cache; `pnanovdb_readaccessor_init` resets it).
Optional optimizations (not in the header): fuse `get_dim` and `is_active` into one walk; treat
an allocated leaf whose 64 B mask is zero as an 8-cell (it matters only if empty leaves exist).

## 5. Published performance material

No NVIDIA source found gives first-hit surface ray-casting Mrays/s for NanoVDB. What exists:

| Source | GPU | What is measured | Numbers |
| --- | --- | --- | --- |
| Museth, "NanoVDB: A GPU-Friendly and Portable VDB Data Structure For Real-Time Rendering And Simulation", **SIGGRAPH 2021 Talks** (not HPG), doi:10.1145/3450623.3464653, https://research.nvidia.com/labs/prl/nanovdb/nanovdb2021.pdf | figure: RTX 3090 (PBRT v4 renders) | 2-page talk, no timing table | Quantization codecs "reduce the memory footprints (typically by 4 to 6 times)" and give "small performance improvements (typically 10 to 30 %)", "suggesting that our benchmark tests are memory (vs compute) bound". CPU random access "faster ... than OpenVDB". Biggest limitation: no topology modification. |
| Hofmann, Hasselgren, Clarberg, Munkberg, "Interactive Path Tracing and Reconstruction of Sparse Volumes", Proc. ACM CGIT 4(1), 2021, https://dl.acm.org/doi/10.1145/3451256 | RTX 3090, 1920x1080 | NanoVDB volume path tracing (Falcor), 4 spp, up to 128 bounces, HDDA empty-space skipping | Render ms (uniform 4 spp): Dust Devil 2.5 M voxels 8.6; Gasoline Explosion 3.6 M 8.9; Building Implosion 4.0 M 8.4; Midair Explosion 5.4 M 16.5; Smoke Plume 11.4 M 20.1; Disney Cloud 188.4 M voxels 53.4 ms (5.3 M paths). Disney Cloud 585 MB sparse vs about 3 GB dense. Empty space skipping 63.2 -> 53.4 ms. |
| Williams et al., "fVDB", ACM TOG 43(4) 133, 2024, https://arxiv.org/pdf/2407.01781 | RTX 3090 Ti | HDDA over a NanoVDB index grid, rays march the WHOLE volume and output intervals (not first hit); bunny 3-voxel shell | M rays/s for 32^3..1024^3: 3.77, 3.40, 2.81, 2.24, 1.83, 1.43 (NerfAcc dense DDA: 2.57 .. 0.47). GPU memory 0.38 .. 8.85 MB. |
| Sifakis, Williams, ..., Museth, "Dynamic GPU Sparse Grids and Voxel-sequential Processing in NanoVDB", ACM TOG 45(6) 225, Dec 2026, doi:10.1145/3842564, https://research.nvidia.com/labs/prl/sifakis2026dynamic/dynamic_grids.pdf | RTX 6000 Ada 48 GB / RTX PRO 6000 Blackwell | GPU topology operators (CUDA) | Dilate 567 M active voxels in 2.0 ms (183x faster than a `voxelsToGrid` rebuild, 356 MiB vs 48.2 GiB peak); prune 8.4 ms; MeshToGrid 9.88 B active voxels in 3.24 s; final images rendered with the HDDA, no ray timings. |
| `nanovdb/examples/benchmark` (exists at tag v10.0.0, removed by v11.0.0), https://github.com/AcademySoftwareFoundation/openvdb/tree/v10.0.0/nanovdb/nanovdb/examples/benchmark | any CUDA GPU | CUDA kernel, 1280x720 = 921,600 rays, 6 camera angles, `nanovdb::ZeroCrossing` on a level-set torus (or `data/test.nvdb`), normal from central differences; CUDA-event timing printed at run time; also CPU (TBB) and dense-grid variants | No numbers are checked in. |

GTC: the NVIDIA developer page (https://developer.nvidia.com/nanovdb) mentions a GTC talk
with the same title; no GTC slide numbers were found. A 2020 NVIDIA blog quotes Pixar: an
unoptimized GPU ray-marching prototype with NanoVDB gave "an order of magnitude" over their
CPU reference (from a search summary, not opened).

Use for the report: NanoVDB is a volume/level-set structure; the HDDA was designed for empty
space skipping before volume integration or a zero crossing. Opaque first-hit voxel tracing
with 8^3 leaves walked voxel by voxel is the case we measure; there is no NVIDIA reference
number for it.

## 6. Incremental edits

Official position: the 2021 talk says "it does not include tools to modify the topology of
the tree. While modification of values is trivial, the same cannot be said about adding or
removing nodes". `NanoVDB.h:15-18`: "a read-only sparse GPU (and CPU) friendly data
structure".

What can change in place (CPU `NanoGrid` or GPU writes with `PNANOVDB_BUF_HLSL_RW` / own
stores):

| Edit | Allowed in place? | What breaks |
| --- | --- | --- |
| Value of a voxel (UInt8 leaf value, sidecar byte of an OnIndex voxel) | yes (`LeafNode::setValueOnly`, `NanoVDB.h:4570`; example `examples/ex_modify_nanovdb_thrust`) | Stats (min/max/avg) go stale; the HDDA does not read them. |
| Toggle the active bit of a voxel in an EXISTING leaf, UInt8 / Mask / Index grid | yes (CPU: `LeafData::setValue` / `setOn` set the bit, `mValueMask.setOff` clears it; GPU: `pnanovdb_leaf_set_value_mask`) | Leaf/lower/upper/root bboxes and `voxel_count` go stale. The HDDA uses only the root bbox: a new voxel outside it is missed. Fix: clip against a fixed world AABB instead, or rerun `tools::updateGridStats(grid, StatsMode::BBox)` (CPU, whole grid; `tools/cuda/GridStats.cuh` on CUDA). Checksum goes stale (only checked by IO/validation). |
| Toggle an active bit in an OnIndex grid | **no** | The prefix sums of the leaf and the `mOffset` of every later leaf shift: the whole sidecar index must be rebuilt. |
| Brick appears where no leaf exists, or a leaf should be removed | **no** | Needs a new leaf, maybe a new lower/upper node, new child-mask bits and offsets: a topology rebuild. An all-empty leaf left in place is valid (the HDDA walks it voxel by voxel because `get_dim` returns 1 in any leaf). |

Practical options for the dynamic FractalCave scene (384x1024x384, 18,432 bricks toggled per
frame):

1. Official path: full CPU rebuild per frame (`build::Grid` + `createNanoGrid`) and full
   upload. This is the protocol's "rebuild time per frame" case. Not measured.
2. NanoVDB-compatible extension: preallocate a leaf for every brick of the dynamic domain
   (48 x 128 x 48 = 294,912 leaves; Mask 28 MiB, UInt8 171 MiB, plus 72 lower nodes and 1
   upper node). Then every brick toggle is a mask + value write into a known leaf (build a
   brick-key -> leaf byte offset table once by walking `tree.getFirstLeaf() + i`). Cost: no
   8-voxel skipping through empty bricks inside the domain. This is not an official facility;
   report it as a separate variant.
3. GPU topology tools exist only for CUDA (`.cuh`, `nanovdb/tools/cuda/` in v13.1.0):
   `PointsToGrid.cuh` (`pointsToGrid`, `voxelsToGrid` = build any grid type from device voxel
   coordinates), `IndexToGrid`, `MergeGrids`, `PruneGrid`, `DilateGrid`, `CoarsenGrid`,
   `RefineGrid`, `TopologyBuilder`, `MeshToGrid`, `DistributedPointsToGrid`, `AddBlindData`,
   `GridStats`, `GridChecksum`, `GridValidator`, `SignedFloodFill`, `VoxelBlockManager`. Using
   them from a Vulkan harness needs CUDA plus Vulkan external-memory interop; out of scope for
   the harness unless the owner wants it.

## 7. Open points

* Build time for 4.2 M bricks and the per-frame FractalCave rebuild time: not measured.
* Empty leaves in `createNanoGrid` / `updateGridStats` (needed for option 2 above): read as
  supported (no pruning in the conversion), not run.
* The trace example was compiled, not executed: check it against the harness reference
  (same hit voxel and normal on a test pose) before timing.
