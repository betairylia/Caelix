# Background: voxel ray traversal structures and their published performance

Prepared 2026-10-18 as the related-work and context section for the Caelix voxel benchmark.
Audience: graduate CS students who know what an octree, a BVH and a distance field are, but not
the details of each method.

## How to read this document

- Every number has a source. Numbers in **tables copied from a paper** are marked with the table
  number. Numbers that this document **computes** from published values are marked "(derived)".
- "Unverified" means: the claim comes from a search snippet, a secondary source, or memory, and
  this author could not read the primary source.
- Mrays/s = million rays per second. "Primary" = camera rays (coherent). "Secondary" = bounce
  rays (incoherent). ms = milliseconds of GPU time unless stated otherwise.
- Bibliography keys like [LK10] point to Section 8.

### Corrections to the starting assumptions of this survey

| Assumption | What the sources say |
|---|---|
| NAADF is in Computer Graphics Forum 2025 (EGSR 2025?) | NAADF is **Eurographics 2026**, Computer Graphics Forum **45(2), article 53, May 2026**, DOI 10.1111/cgf.70413 [UOM26]. |
| NAADF supports 16384³ worlds | The README says "up to 16384^3". The paper tests 16384 x 8192 x 16384 (2^41, "2 tera") voxels and states a DirectX 11 limit of 32-bit buffer pointers and "a maximum resolution of 2 trillion voxels" [UOM26 Sec. 5.3]. The 8256² figure is the horizontal size of the Minecraft world in the README teaser; its height is not stated. |
| "Tree64" has an academic origin paper | No single origin. The name "sparse 64-tree" in the real-time voxel community traces to a 2024 blog post [Dub24]. Earlier academic 64-way voxel trees exist [LR13], and a 2026 journal paper uses the name "tetrahexacontree" [BK26]. See Section 2.1. |
| VoxelRT numbers describe a desktop GPU | The VoxelRT README states: "All tests were run on an integrated GPU" [VRT]. |
| ESVO node = 64 bits | Correct for the original [LK10]. The VoxelRT "ESVO" port and the 64-tree blog use only the 32-bit topology half (4 bytes per node, no contours) [VRT, Dub24]. |

---

## 1. Sparse Voxel Octrees: Laine and Karras, "Efficient Sparse Voxel Octrees" (ESVO)

Sources: I3D 2010 paper [LK10a], the extended NVIDIA technical report NVR-2010-001 [LK10b], and the
journal version in IEEE TVCG 17(8):1048-1059, 2011 [LK11]. Open-source CUDA code was released with
the paper (Google Code, now archived).

### 1.1 Data structure

ESVO stores a sparse octree of **surface** voxels. Each node is a voxel; a voxel that is subdivided
keeps both itself and its children in the tree. Most data of a voxel is stored with its **parent**,
so leaf voxels have no node of their own.

**Child descriptor (64 bits, one per non-leaf voxel)** [LK10a Sec. 3.1, Fig. 2]:

| Half | Field | Bits | Meaning |
|---|---|---|---|
| Topology (first 32 bits) | child pointer | 15 | Relative offset to the first child descriptor. Children with non-leaf status are stored contiguously, so a child is found by "incrementing the pointer based on the bitmasks". |
| | far bit | 1 | If set, the 15-bit field is an index to a separate 32-bit far pointer stored nearby. Depth-first ordering inside each block makes far pointers rare. |
| | valid mask | 8 | One bit per child slot: the slot contains a voxel. |
| | leaf mask | 8 | One bit per child slot: the voxel in the slot is a leaf. (valid=0: empty; valid=1, leaf=0: non-leaf; both=1: leaf.) |
| Contours (second 32 bits) | contour pointer | 24 | Offset to a list of 32-bit contour values. |
| | contour mask | 8 | Which children have a contour. |

- **Contour** (32 bits): a pair of parallel planes that bound the surface inside the voxel: three
  6-bit integers for the normal and two 7-bit integers for the plane positions. The final shape of a
  voxel is the intersection of its cube with the contours of the voxel and of its ancestors. Contours
  let the builder stop subdividing earlier on smooth surfaces [LK10a Sec. 3.2-3.4].
- **Blocks and pages**: the tree is cut into blocks (contiguous memory, also the streaming unit).
  A 32-bit page header every 8 KB points to the block info section [LK10a Sec. 3.1].
- **Attributes**: DXT1-like colour blocks (about 1 byte per voxel) and a custom normal compression
  (about 2 bytes per voxel), each compression block shared by the 16 child slots of two descriptors
  with the same parent [LK10a Sec. 3.5].

**Memory per voxel.** With an average branching factor of four (surfaces are locally two-dimensional),
the descriptor array costs about 2 bytes per voxel; with colours and normals the "theoretical optimum"
is about 5 bytes per voxel [LK10a Sec. 3.5]. Measured averages for the largest datasets, including
all overhead, were 5.16 (CONFERENCE) to 8.10 (SIBENIK-D) bytes per voxel [LK10a Table 1]. The
technical report converts this to about 6.67 bytes per leaf sample at the target resolution, compared
to about 2 bytes per sample for textured, displaced triangles [LK10b Sec. 2, Table 1].

### 1.2 Traversal algorithm

The ray caster is a stack-based, top-down parametric traversal (in the line of Revelles et al. and
Amanatides-Woo [AW87]). The current voxel is identified by a position `pos` and an integer `scale`.
Each iteration does one of three actions [LK10a Sec. 4.1, Fig. 8]:

- **PUSH**: if the ray's active t-span intersects the child (cube plus contours), descend. The parent
  is written to a stack only if the ray will need it again (the `h` check avoids redundant writes).
- **ADVANCE**: step to the next sibling with an Amanatides-Woo style comparison of the three
  exit-plane t-values.
- **POP**: when the ray leaves the parent, find the new scale as the **highest differing bit** between
  the old and new positions and read the ancestor from `stack[scale]`.

Key tricks:

1. Positions live in [1, 2], so the float bit pattern **is** the path through the tree. POP uses
   bit operations on the float representation instead of integer conversions [LK10a Sec. 4.1].
   (The depth is therefore bounded by the 23-bit float mantissa; the exact s_max of the released code
   is unverified here.)
2. The stack has one entry per level and is indexed directly by `scale`.
3. **Coordinate mirroring**: the octree is mirrored so that every component of the ray direction is
   negative; child indices are XOR-ed with an octant mask. This removes sign checks.
4. **Level-of-detail termination**: traversal stops when the voxel is smaller than the pixel
   footprint (exp2(scale) compared against a linear function of t).
5. **Beam optimization** (primary rays only): a coarse pass casts one ray per corner of 4x4 or 8x8
   pixel blocks and stops at the first voxel that is too small to be certain to cover a coarse ray.
   The minimum of the four corner distances minus a constant becomes the start t of each full-
   resolution ray. Contours are disabled in the coarse pass so the bound stays conservative
   [LK10a Sec. 4.2].
6. A variable-radius post-process blur hides the blockiness of per-voxel shading [LK10a Sec. 4.3].

### 1.3 Editing and updates

ESVO is a **read-optimized, static** structure. It is built on the CPU from triangle meshes (all four
cores; build times from 6 s to 2541 s in [LK10a Table 1]). The system streams blocks by distance to
the camera, but there is no edit path: changing topology means rebuilding the affected subtrees and
re-packing the descriptor array. Later DAG work (SVDAG [KSA13], HashDAG [CBE20]) adds editing to
compressed trees; NAADF summarizes HashDAG's editing cost as a 1.5-2x traversal slowdown [UOM26 Sec. 2.1].

### 1.4 Memory scaling

Node count grows with **surface area**, not volume: about 4x per level for surface data (the
technical report measures the "local dimensionality" of all test scenes converging to about 2
[LK10b Sec. 8.2, Fig. 24]). Table 1 of [LK10a] shows this: CONFERENCE grows from 17 MB at 10 levels
to 1328 MB at 15 levels.

### 1.5 Published performance

Hardware: NVIDIA Quadro FX 5800 (4 GB), Core2 Quad Q9300, Windows XP 64, CUDA 2.1 [LK10a Sec. 5].
Primary rays only, averaged over several viewpoints, each frame repeated to amortize start-up.

Table 2 of [LK10a] (= Table 4 of [LK10b]), Mrays/s. Columns: triangle ray caster of [AL09] on the
same GPU / cubical voxels / voxels with contours / contours + beam optimization.

| Scene | 512x384 | 1024x768 | 2048x1536 |
|---|---|---|---|
| CITY | 46.7 / 45.1 / 79.9 / 88.6 | 68.5 / 54.3 / 89.1 / 106.0 | 77.1 / 63.9 / 97.4 / 123.8 |
| SIBENIK | 64.3 / 38.7 / 80.0 / 82.5 | 94.1 / 46.5 / 94.1 / 103.6 | 107.1 / 55.1 / 103.9 / 122.0 |
| SIBENIK-D (displaced) | - / 24.8 / 32.6 / 37.5 | - / 30.2 / 38.7 / 48.3 | - / 37.1 / 43.6 / 60.9 |
| HAIRBALL | 11.6 / 22.4 / 24.1 / 24.1 | 20.5 / 22.5 / 27.9 / 28.4 | 31.2 / 29.2 / 36.5 / 38.2 |
| FAIRY | 63.9 / 62.1 / 128.2 / 132.6 | 125.1 / 69.4 / 145.4 / 150.9 | 155.8 / 78.6 / 160.4 / 169.2 |
| CONFERENCE | 69.1 / 35.8 / 97.9 / 104.4 | 111.9 / 43.8 / 110.3 / 124.3 | 134.0 / 52.3 / 120.2 / 140.8 |

(The table is mangled in the PDF text layer; this author re-aligned it and checked it against the
Figure 1 caption: SIBENIK-D 60.9, SIBENIK 122.0 and triangles 107.1 Mrays/s.)

Other published facts:

- SIBENIK-D at about 5 mm resolution uses 2.7 GB of GPU memory [LK10a Fig. 1].
- A GeForce GTX 285 (same GPU chip, 14% higher clock, 56% more bandwidth) was 15-22% faster, 18% on
  average, at 1024x768 with contours and beam [LK10b Sec. 8.4, Table 5].
- SIMD utilization was about 70% without beam optimization and 60% with it [LK10b Sec. 8.5].
- Mrays/s **rises** with resolution only because of better ray coherence [LK10a Sec. 5.1].
- On a modern integrated GPU, a port without contours and beam reached 57-137 Mrays/s primary, about
  0.5-0.65x of the 64-tree in the same harness (derived), with 33-71 iterations per ray [VRT].

---

## 2. 64-trees (4³ branching) and the other structures in VoxelRT

### 2.1 Origin and lineage

There is no single origin paper. What exists:

| Year | Source | What it is |
|---|---|---|
| 2009 | GigaVoxels [CNLE09] | Octree whose leaves are dense bricks (typically 16³ or 32³ voxels), traversed with kd-restart; the ESVO report describes it this way [LK10b Sec. 2]. Whether the paper calls the tree an "N³-tree" is unverified here. |
| 2013 | Liu and Rokne, "A micro 64-tree structure for accelerating ray tracing on a GPU", Graphics Interface 2013, pp. 165-172 [LR13] | A **64-way full tree** that summarizes the occupancy of a uniform grid (for triangles): one bit per cell, each node's 64-cell subgrid in one 64-bit word, built bottom-up, top levels in shared memory. Claims fewer traversal steps and less bandwidth; the abstract gives no numbers. |
| 2013 | VDB [Mus13] | Wide nodes of 32³, 16³ and 8³ with bitmasks (Section 4). Not 4³, but the same "wide node + bitmask" idea. |
| 2024 | dubiousconst282, "A guide to fast voxel ray tracing using sparse 64-trees" (blog) [Dub24] | The source of the term "sparse 64-tree" in the hobby/indie voxel community. Node layout, traversal and numbers in 2.2-2.4. |
| 2024+ | dubiousconst282/VoxelRT README [VRT] | Benchmark harness; calls the structure "Tree64 ... aka. contree" (12 bytes per node). |
| 2024+ | expenses/tree64 (Rust) [E64] | "Implementation of a sparse 64-tree with hashing and modifications", based on [Dub24]; edits by path copying. |
| 2024 | teknologicus, "Vorxel" devlog [Tek] | Hobby engine using a "tetrahexacontree" with 4x4x4 occupancy bitmasks. |
| 2026 | Bakharev and Krylov, "Optimization of GPU Ray Tracing Algorithm for Voxel Data Using a Tetrahexacontree Data Structure", Doklady Mathematics 113(2):59-66 [BK26] | Peer-reviewed journal paper. Citation verified via Crossref. Search snippets report a 28% gain over naive traversal and 21% over an octree method; **unverified** (full text not read). |

### 2.2 Data structure and why 64-way branching suits GPUs

Node of [Dub24] (12 bytes, packed; stored as three `uint`s in shaders):

```
struct SvtNode64 {
    uint32_t IsLeaf   : 1;   // leaf: children are plain voxels
    uint32_t ChildPtr : 31;  // absolute offset to the packed child array
    uint64_t ChildMask;      // one bit per 4x4x4 child cell
};
```

Children (nodes, or voxel material bytes in leaves) are **left-packed**: only present children are
stored, and child i is at `ChildPtr + popcount(ChildMask & ((1 << i) - 1))`.

Reasons why 4³ beats 2³ on GPUs, as argued in [Dub24, VRT] (the first three are arithmetic facts):

1. **Half the depth.** A 4096³ world needs log8(4096) = 12 octree levels but log64(4096) = 6
   64-tree levels (derived). Each level is a dependent global-memory load, so fewer levels means
   fewer serialized latencies and a smaller ancestor stack (11 entries in [Dub24]).
2. **One machine word per node mask.** 64 children = one 64-bit mask; child indexing is one
   masked popcount (emulated with two 32-bit popcounts where 64-bit is not exposed in shaders).
3. **Lower overhead per voxel.** Best case for surface data: ESVO's 4-byte topology node serves at
   most 8 children, about 4/8 + 4/64 + ... = 0.57 bytes per voxel; the 12-byte 64-tree node serves
   64, about 12/64 + 12/4096 + ... = 0.19 bytes per voxel [Dub24].
4. **Mask arithmetic finds large empty cuboids.** One AND with a constant tests whether a 2³ group of
   cells is empty (`0x00330033` for the x + 4z + 16y cell order), so the ray can take 2x larger
   steps without reading more memory [Dub24].
5. **Coordinates map to the float mantissa in 2-bit chunks** (one chunk per level, coordinates in
   [1, 2)), the same trick as ESVO.

Costs: DAG deduplication works worse (a 64-child node has more entropy and cannot encode whole leaf
paths) [Dub24]; a sparse node wastes most of its 64 mask bits; an edit invalidates a larger node.

### 2.3 Traversal

[Dub24] replaces ESVO's two-level DDA (advance the position while descending) with **ray marching
over empty boxes**:

1. Descend from the current ancestor until the cell at `pos` is empty or a leaf voxel.
2. The empty cell (or empty 2³ group, trick 4 above) is an axis-aligned box. Intersect only its
   three exit planes; after **octant mirroring** this is `t = min((cellMin - origin) * invDir)`.
3. Move to the exit point and **clamp** it into the neighbour cell's bounds (instead of adding an
   epsilon, which caused visible artifacts).
4. Find the common ancestor with `firstbithigh((asuint(pos) ^ asuint(cellMin)) & 0xFFAAAAAA)` and
   reload it from the small ancestor stack (node indices, not node payloads).

Measured effect of each optimization, cycles per ray, Bistro at "4k" on the author's laptop
integrated GPU [Dub24]: ancestor memoization 16903 -> 8896; 2³ coalesced skips 8898 -> 7052 (21%);
octant mirroring 7061 -> 6358 (about 10%). The VoxelRT README adds that a group-shared-memory
ancestor stack gave about 9% (but slowed the ESVO port by 120%), and that the ray-marching form
was both simpler and faster than a recursive DDA over the node bitmasks [VRT].

### 2.4 Editing and memory

- Editing: sparse trees can be edited by **path copying** in O(log n) [VRT, E64]. The VoxelRT Tree64
  backend instead rebuilds the tree of a chunk on any change [VRT]. [Dub24] notes that in-place edits
  of compressed (left-packed) nodes need a pool allocator.
- Memory, Bistro voxelized at "8k-ish" resolution, materials disabled: 358.33 M voxels; 64-tree
  224.1 MB (1042.85 k inner nodes, 18545.01 k leaf nodes, about 0.62 bytes per voxel) against
  ESVO 368.0 MB (about 1.02 bytes per voxel) [Dub24]. The VoxelRT README reports "about half as much
  memory as ESVO" for most scenes [VRT].

### 2.5 Published numbers (VoxelRT harness, integrated GPU)

All methods in one harness, same scenes; scene sizes in occupied 8³ bricks [VRT]. The author
"guesstimates" 5-10x higher throughput on a discrete GPU (unverified estimate).

| Scene | Tree64 prim / PT1 | XBrickMap | ESVO (no contours) | StdBVH | ManhattanDF | PlainDDA |
|---|---|---|---|---|---|---|
| Sponza 2k (499k bricks) | 182.6 / 124.2 | 165.6 / 125.1 | 95.0 / 66.2 | 175.0 / 102 | 166.3 / 93.2 | 31.4 / 3.2 |
| San Miguel 4k (681k bricks) | 112.1 / 99.5 | 109.8 / 95.7 | 67.9 / 54.5 | 88.0 / 62 | 105.0 / 60.1 | 33.1 / 2.6 |
| NY Highway I95 4k (574k bricks) | 208.9 / 137.4 | 115.2 / 88.5 | 136.9 / 86.4 | 215.9 / 154 | 100.3 / 53.0 | 24.0 / 23.8 |

Values are Mrays/s; "PT1" = total Mrays/s over one primary plus one diffuse bounce. Tree64 needs
16-31 iterations per ray against 33-71 for the ESVO port and 354-512 for a flat-grid DDA.

### 2.6 Lineage of the other VoxelRT methods

| VoxelRT method | Idea | Origin |
|---|---|---|
| PlainDDA | Step voxel by voxel through a uniform grid | Amanatides and Woo 1987 [AW87] |
| MultiDDA, XBrickMap | Two/three-level grids: a coarse grid of pointers to dense 8³ bricks (512-bit occupancy each) | **Brick maps**: van Wingerden, MSc thesis, Utrecht 2015, supervisor J. Bikker [vW15]; CUDA re-implementation by stijnherfst [BMap] (bricks indexed inside 16³-brick "superchunks", 3 LOD levels, GPU-requested streaming). The thesis PDF could not be downloaded; its numbers are unverified. Dense bricks as tree leaves go back to GigaVoxels [CNLE09]. |
| ManhattanDF, EuclideanDF | March by a stored distance to the nearest solid voxel | Distance transforms for ray casting: Zuiderveld, Koning and Viergever 1992 [ZKV92]; "proximity clouds", Cohen and Sheffer 1994 [CS94]; sphere tracing, Hart 1996 [Har96]. |
| OctantDF | 8 directional (per ray octant) Manhattan fields | Directional/anisotropic distance idea; related to the per-axis distances of NAADF (Section 3) and to "directed distance fields" [KBSS01] as cited by [UOM26]. VoxelRT measured 20-25% fewer iterations than isotropic fields [VRT]. |
| ESVO | Sparse octree, contours | [LK10a] |
| StdBVH | Binary BVH with 8³ brick leaves, DDA inside leaves | BVH survey [MOB21]; compressed 8-wide BVH (CWBVH) of Ylitie et al. 2017 [YKL17]: 80-byte nodes for 8 children, 1.9-2.1x faster incoherent traversal than the prior state of the art, 35-60% of the memory of an uncompressed BVH. This is the software analogue of a hardware BLAS over brick AABBs (Section 5). |

---

## 3. NAADF: Nested Axis-Aligned Distance Fields

Ulschmid, Ott, Macho, Wimmer, Ohrhallinger (TU Wien), "NAADF: Globally Illuminated Voxel Worlds
Accelerated with Nested Axis-Aligned Distance Fields", Computer Graphics Forum 45(2), article 53,
Eurographics 2026, open access, DOI 10.1111/cgf.70413 [UOM26]. Code: github.com/cg-tuwien/NAADF
(C#, MonoGame fork with compute shaders, DirectX 11) [NAADF-git].

### 3.1 Data structure

Three layers of **cells**, each layer in its own GPU buffer [UOM26 Sec. 3.1, Fig. 2]:

| Layer | Covers | Size per cell | State bits | Payload |
|---|---|---|---|---|
| Chunk | 4³ blocks = 16³ voxels | 32 bits (+32 bits for entities, Sec. 3.6) | 2 (empty / uniform / mixed) | empty: AADF, 6 x 5 bits; uniform: 15-bit voxel type; mixed: pointer to 64 consecutive blocks |
| Block | 4³ voxels | 32 bits | 2 | empty: AADF, 6 x 2 bits; uniform: type; mixed: pointer to a **hashed** group of 64 voxels |
| Voxel | 1 | 16 bits | 1 (empty / full) | full: 15-bit type (index into a material buffer, 16 bits per material entry); empty: AADF, 6 x 2 bits |

- **AADF** = for an empty cell, how many cells the empty axis-aligned box around the cell extends
  in each of the six directions (x+, x-, y+, y-, z+, z-), bounded by the parent cell for blocks and
  voxels. Maximum reach: voxel and block 3 cells (2 bits); chunk 31 chunks = 496 voxels (5 bits)
  [UOM26 Sec. 3.3].
- Difference to an SDF: an SDF stores one isotropic radius (a sphere or, for Chebyshev, a cube) that
  is limited by the nearest solid in **any** direction. An AADF stores a box that can be long in the
  ray's direction even when a wall is close on the side. The paper's own "Ours+SDF" variant
  (Chebyshev distance) shows the difference (Section 3.6).
- **Hashing**: mixed blocks are deduplicated by hashing their 64 voxel types (open addressing,
  linear probing, up to 100 probes, resize at 75% load) [UOM26 Sec. 3.2]. The paper also considered
  a two-layer variant with 8³ blocks; the third layer "improves compression without degrading
  performance" for large scenes.
- Consistency check (derived): the 1024 x 512 x 2048 test map has 64 x 32 x 128 = 262,144 chunks x
  4 B = 1 MB, and the 16384 x 8192 x 16384 map has 536,870,912 chunks x 4 B = 2147 MB. Both match the
  "Chunks" rows of [UOM26 Table 3]. At large scale the chunk layer dominates memory.

### 3.2 Construction

On the GPU, one thread group of 64 threads per chunk, one thread per block: test uniformity, hash
and insert mixed blocks, then thread 0 decides the chunk state and reserves 64 block slots
[UOM26 Alg. 1]. AADFs are computed afterwards by **iterative box growth**: alternate the three axes,
grow by one cell per iteration in both directions while the new slab is empty. All cells iterate in
lock-step, so a cell can merge its neighbour's already-grown box; this makes the cost linear,
O(3dn) for n cells and maximum distance d [UOM26 Sec. 3.3, Fig. 3].

### 3.3 Traversal

A DDA variant that jumps across a whole empty box per iteration [UOM26 Sec. 3.4]:

1. Start at the chunk that contains the ray origin.
2. Empty cell: intersect the ray with the cell's AADF box and move to the exit point, which can be
   many cells away along several axes.
3. Mixed cell: descend one layer. Full cell: hit, compute the intersection and stop.
4. Use the axis of the last exit plane to decide which voxel boundary the ray crossed.

The layer count is fixed at 3, so there is no stack and no pointer chain longer than two indirections.
Dynamic entities: after the main traversal, test the entities registered in the chunks the ray
passed (bounding box first, then the entity's own voxels with AADFs); about 10% overhead.

### 3.4 Editing

A CPU copy of chunks, blocks and voxels is kept and every change is synced to the GPU. AADFs are
**caches**, not required for correctness: modified cells are queued and AADFs are recomputed in the
background (one queue per layer; chunk AADFs use 3 x 31 queues, one queue processed per frame). When
a chunk changes between empty and non-empty, AADFs in the surrounding 63³ chunks (2 x 31 + 1) are
reset; a flood fill in groups of 4³ chunks marks each chunk once [UOM26 Sec. 3.3, 3.5]. A reset AADF
means "no skip", so correctness holds while the cache refills.

### 3.5 Global illumination pipeline

Order: G-buffer -> TAA -> resampling -> denoiser -> post-process [UOM26 Fig. 4]. Main ideas:

- **Plane G-buffer**: instead of position/normal buffers, store the planes the primary ray bounced
  off (3-bit axis normal + 14-bit distance along it), up to four planes through specular voxels. The
  full (virtual) path is reconstructed by reflecting the camera ray [UOM26 Sec. 4, Fig. 5].
- **Long-term-memory TAA**: each pixel keeps its samples from the **last 32 frames**, 64 bits each:
  RGB at 8 bits with exponential compression, 16-bit float distance to the first non-specular hit,
  5-bit roughness, 3-bit normal and a **16-bit hash** of the material types and normals of the
  primary ray's specular bounces (the "path hash"). Rejection is depth-based (3x3 min/max) plus a
  hash match against the 3x3 neighbourhood plus a 1-pixel reprojection test, not colour clamping.
  One 1440p frame of samples is 29 MB, against 59 MB for standard TAA [UOM26 Sec. 4.1, Fig. 6].
- **Sample generation**: only material (BSDF) sampling, no light lists, because voxel emitters can be
  many and change with edits. The TAA sample count steers where new paths are generated; secondary
  rays have at most 3 bounces; effectively 0.25-1 spp per frame [UOM26 Sec. 4.2, 5.2].
- **Sample compression**: unlit samples 16 B (primary hit only, every eighth one stored with weight
  8), lit samples 32 B (primary + secondary hit). Four frames of unlit storage cover at least 32
  frames; lit storage is two frames' worth, up to 64 frames of history.
- **Temporal resampling** into disjoint 8x8 screen regions: up to 32 lit samples per region,
  weak samples culled by brightness ratio with compensation, at most 8 kept as "refined" samples.
- **Spatial resampling** (ReSTIR GI style [OLK21], biased): 12 iterations picking a random 8x8
  region within an adaptive radius; **one** visibility ray, for the final selected sample only; no
  initial (own) sample, which reduces fireflies but causes local darkening.
- **Denoiser**: separable sparse bilateral filter (kernel 21, about every second pixel), 0.67 ms and
  89 MB at 1440p; optional SVGF 7.46 ms and 442 MB [UOM26 Sec. 4.3].

### 3.6 Published numbers

Hardware: NVIDIA RTX 3090 Ti (24 GB), AMD Ryzen 9800X3D, DirectX 11 compute shaders via MonoGame.
Scenes: SAN MIGUEL and OASIS at 1024 x 512 x 2048 voxels; OASISX240 at 16384 x 8192 x 16384.
Structure tests at 3840x2160 ("to reduce potential CPU bottleneck"); GI tests at 2560x1440
[UOM26 Sec. 5].

[UOM26 Table 1], Mrays/s at 2160p, primary / secondary:

| Structure | SAN MIGUEL | OASIS | OASISX240 |
|---|---|---|---|
| Grid | 794 / 100 | 286 / 47 | OOM |
| Octree (SVO [LK10a]) | 1076 / 249 | 828 / 263 | OOM |
| SVDAG [KSA13] | 1074 / 250 | 822 / 263 | OOM |
| Ours, no distance field | 2254 / 1018 | 1349 / 1000 | 208 / 28.6 |
| Ours + SDF (Chebyshev) | 4459 / 1050 | 3485 / 1244 | 1626 / 665 |
| Ours + AADF (NAADF) | **7029 / 2105** | **5924 / 2821** | **2765 / 817** |

Ratios vs. SVDAG (derived): primary 6.5x (SAN MIGUEL) and 7.2x (OASIS); secondary 8.4x and 10.7x.
The abstract states 3-5x for the nested cells alone; Table 1 gives 2.1x / 1.6x primary and 4.1x / 3.8x
secondary for "no distance field" (derived), so the 3-5x applies to secondary rays. In time units
(derived): 7029 Mrays/s is 1.18 ms for one primary ray per pixel at 2160p (8.29 M rays).

[UOM26 Table 2], build times: Grid 350 ms; Octree 6.2-6.3 s and SVDAG 6.5-6.6 s (CPU builds);
NAADF structure 200-203 ms plus AADF 6.2-7.3 ms on the GPU for the 1024 x 512 x 2048 scenes;
87.6 s plus 1.97 s for OASISX240.

[UOM26 Table 3], memory: SAN MIGUEL Grid 2.0 GB (16 bits per voxel), Octree 60.1 MB, SVDAG 38.5 MB,
NAADF 75 MB (chunks 1, blocks 7.9, voxels 66.1 MB). OASIS: Octree 104.5 MB, SVDAG 36.5 MB, NAADF
51.9 MB. OASISX240: NAADF 3419 MB (chunks 2147, blocks 1271, voxels 39.5 MB); the others ran out
of memory, partly because the comparison used a grid of trees.

[UOM26 Table 5], GI frame at 1440p, SAN MIGUEL (re-aligned from the PDF text layer; the totals add up):

| Step | NAADF 0.25 spp | NAADF 1 spp | Baseline (ReSTIR GI + TAA + SVGF), 1 spp |
|---|---|---|---|
| Atmosphere + primary | 0.12 + 0.71 | same | 0.12 + 0.71 |
| TAA reproject (32 samples) / select | 1.22 / 0.10 | same | - |
| Secondary trace | 4.91 | 19.4 | 19.4 |
| Resampling total | 5.37 | same | 6.83 |
| Denoise | 0.67 | same | 7.46 (SVGF) |
| Total without / with denoise | 12.78 / 13.45 ms | 27.16 / 27.83 ms | 27.37 / 34.83 ms |

Memory of the GI pipeline [UOM26 Table 4]: 32-frame TAA 973 MB (1.42 ms), total pipeline without
denoising 1645 MB, against 811 MB for the baseline.

Observations for a benchmark reader:

- The structure benchmark (Table 1) and the GI frame (Table 5) are not on the same footing: Table 1
  isolates traversal at 2160p; Table 5 includes shading, up to 3 bounces and specular chains.
- The "2x faster GI" claim comes mostly from tracing fewer secondary rays (0.25 spp guided by TAA),
  not from faster rays: at equal 1 spp the secondary trace is 19.4 ms in both columns.
- Limitations stated by the authors: bias (darkening), about 32 frames of temporal lag in indirect
  light after edits or sun changes, sun soft shadows not resampled, CPU-bound edits and entities.

---

## 4. VDB, HDDA and NanoVDB

### 4.1 VDB (Museth 2013)

"VDB: High-Resolution Sparse Volumes with Dynamic Topology", ACM TOG 32(3), article 27, 2013 [Mus13].
A shallow B+tree-like hierarchy with a fixed depth and **wide nodes**. The standard configuration
is written 5-4-3 (log2 of the node width per level):

| Level | Node | Children | Voxel extent per node |
|---|---|---|---|
| Root | sparse hash map of tiles / child pointers | unbounded (signed 32-bit coordinates) | - |
| Upper internal | 32³ | child mask + value mask (32768 bits each) | 4096³ |
| Lower internal | 16³ | masks of 4096 bits | 128³ |
| Leaf | 8³ | value mask (512 bits) + 512 values | 8³ |

An internal node slot holds either a child pointer or a **tile value** (a constant region). Random
access is constant time on average through accessors that cache the last path and look up
bottom-up. OpenVDB (open-sourced 2012) is a CPU library with dynamic topology.

### 4.2 HDDA (Museth 2014)

"Hierarchical Digital Differential Analyzer for Efficient Ray-Marching in OpenVDB", SIGGRAPH 2014
Talks, article 40, DOI 10.1145/2614106.2614136 [Mus14]. One DDA per tree level; the ray steps with the
DDA of the largest node level that is empty at the current position ("multi-resolution
leapfrogging"), and template metaprogramming collapses the levels at compile time. The talk gives no
timings. NanoVDB uses HDDA in `ZeroCrossing` (level-set root finding) and for volume empty-space
skipping [BM20]. The fVDB paper re-describes HDDA in detail and benchmarks it: on a 3-voxel-wide
narrow-band level set of the Stanford bunny at 32³ to 1024³, RTX 3090 Ti, HDDA was 1.5x to 3x faster
than the dense-bitfield DDA of NerfAcc with up to 100x less memory [WHS24 Sec. 4.1.2, Table 2].

### 4.3 NanoVDB (Museth 2021)

"NanoVDB: A GPU-Friendly and Portable VDB Data Structure For Real-Time Rendering And Simulation",
SIGGRAPH 2021 Talks, DOI 10.1145/3450623.3464653 [Mus21]; code in the OpenVDB repository.

- A **linearized, read-only snapshot** of an OpenVDB tree: one contiguous buffer, no pointers
  (offsets instead), 32-byte aligned nodes, typically breadth-first (all nodes of a level packed
  together). The buffer is also the file format and can be copied to the GPU as is.
- Same 5-4-3 tree; every node also stores min/max/average/standard deviation of its values and the
  bounding box of its active values (used for tighter ray clipping, for early termination and for
  building hardware-RT BVHs).
- Compression: fixed bit rates (Fp4, Fp8, Fp16) or an adaptive per-leaf bit rate (FpN); the talk
  reports 4-6x smaller footprints and 10-30% faster benchmarks, which the author reads as evidence
  that GPU volume rendering is memory bound.
- Limitation: no topology changes (values can be modified). [Mus21]

Bytes per node, derived by hand from the struct definitions in `NanoVDB.h` (current master; not
compiled, so treat as derived):

| Node / value type | Size | Per voxel slot |
|---|---|---|
| Leaf, `float` | 12 (bbox min) + 3 (bbox size) + 1 (flags) + 64 (value mask) + 16 (min, max, avg, std dev) = 96 B header + 512 x 4 B = 2144 B | 4.19 B |
| Leaf, `Fp8` / `Fp16` | header + 512 B / 1024 B of codes | about 1.2 B / 2.2 B |
| Leaf, `ValueMask` (topology only) | 96 B | 1.5 bits |
| Leaf, `ValueOnIndex` | 96 B, values elsewhere: 64-bit offset + 9-bit prefix sums; one value per **active** voxel | topology 1.5 bits + value |
| Lower internal (16³) | 2 masks x 512 B + 4096 table entries x 8 B + header, about 33 KB | - |
| Upper internal (32³) | 2 masks x 4096 B + 32768 x 8 B + header, about 264 KB | - |

A dense 8³ leaf stores all 512 slots, so a thin surface (one voxel layer through a leaf) wastes most of
the leaf; NanoVDB targets volumes and narrow-band level sets, not single-voxel-thick surfaces.

Published performance: NVIDIA blog by Braithwaite and Museth (2020) [BM20], 1024x1024 images:

| Test | OpenVDB, 88 CPU threads | NanoVDB, 88 CPU threads | NanoVDB, CUDA, Quadro RTX 8000 |
|---|---|---|---|
| Level-set ray tracing (dragon) | 148.182 ms | 11.554 ms | 2.427 ms |
| Fog-volume rendering (bunny cloud) | 243.985 ms | 223.195 ms | 4.971 ms |
| Collision, 100 M particles | - | 120.324 ms | 10.131 ms |

No Mrays/s figure was found for NanoVDB on opaque voxel scenes. GVDB (Hoetzlein, HPG 2016,
DOI 10.2312/hpg.20161197) [Hoe16] is the earlier GPU VDB-style structure with a dynamic memory pool
and its own hierarchical traversal; its numbers were not extracted here.

---

## 5. Hardware ray tracing for voxels (context for Caelix)

Caelix uses Vulkan ray queries over one BLAS of AABBs per 16³ render group and a DDA inside the
shader. This section collects what is published about that design family.

### 5.1 Mechanism (API facts)

In DXR and Vulkan, a BLAS can contain **AABB (procedural) primitives**. The RT hardware traverses the
BVH and tests the ray against the boxes; on a box hit, control returns to software: an intersection
shader (pipeline API) or the shader that runs the ray-query loop, which reports a candidate of type
AABB and must decide whether and where to commit a hit. So each box hit costs a round trip out of
the fixed-function unit, and the inner DDA runs on the SM like any compute code. Candidates are not
guaranteed to arrive in t order; the hardware only culls boxes behind the committed t.

### 5.2 Published measurements

**Lacewell, "Ray-Tracing Small Voxel Scenes", Ray Tracing Gems II, Chapter 37, 2021** [Lac21].
OptiX 7, RTX 3060 Ti, 1200x800, one path per pixel (at most 4 rays + 1 outline ray), average of 4800
launches. Device memory (geometry + acceleration structures) and kernel time:

| Layout | Small scene, 63,481 bricks | Merged scene, 4.94 M bricks |
|---|---|---|
| Triangle mesh (12 tris per brick) | 50.19 MB, 1.48 ms | 3903.56 MB, 2.75 ms |
| Custom-1 (one AABB per voxel) | 3.37 MB, 1.54 ms | 261.71 MB, 2.45 ms |
| Custom-8 (one AABB per 8³ block, DDA inside) | 0.22 MB, 2.90 ms | 12.76 MB, 4.29 ms |
| Instanced brick (IAS of a triangle brick) | 25.64 MB, 1.38 ms | 1244.31 MB, 2.34 ms |

(Re-aligned from the text layer; it matches the chapter's "about 60%" and "about 30%" outline
overheads.) Derived: Custom-1 costs about 53 B per AABB primitive including the acceleration
structure (261.71 MB / 4.94 M; also 3.37 MB / 63,481). The author notes that the 8³ DDA intersection
program "is not a trivial intersection shader" and probably not optimal, and concludes that large
voxel scenes are "probably best represented as custom primitives".

**Söderlund, Evans, Akenine-Möller, "Ray Tracing of Signed Distance Function Grids", JCGT 11(3),
2022** [SEA22]. RTX 3090, DXR in Falcor, path tracing with up to 3 bounces, camera paths of 900 frames,
30 s warm-up, average of 5 runs. Traversal methods: SVS = one AABB per surface voxel in a hardware
BVH; SBS = one AABB per 7³-voxel brick with a DDA in the intersection shader; SVO = software octree
after [LK10a]; GST = hierarchical sphere tracing in a dense grid. Frame times with the analytic
voxel test, ms [SEA22 Table 1] and memory, MB [SEA22 Table 2]:

| Scene | SVS | SBS | SVO | GST |
|---|---|---|---|---|
| Cheese | 9.7 ms, 209.0 MB | 17.8 ms, 24.6 MB | 16.7 ms, 45.0 MB | 14.6 ms, 177.3 MB |
| Ladies | 16.1 ms, 30.4 MB | 30.9 ms, 4.1 MB | 54.8 ms, 6.5 MB | 82.9 ms, 177.3 MB |

One AABB per voxel was the fastest everywhere; bricks were about 1.8-1.9x slower but used about
7-9x less memory (derived). The authors expected bricks to win through locality and "cannot see any such evidence".
Caveat: the per-voxel test (a cubic root solve on trilinear SDF values) is far more expensive than a
binary voxel hit test, and the grid resolution is not given in the parts read here.

**Derin, Harada, Takeda, Iba, "Sparse Volume Rendering using Hardware Ray Tracing and Block Walking",
SIGGRAPH Asia 2021 Technical Communications** [DHTI21]. The closest published design to Caelix:
Vulkan 1.2 **ray query in a compute shader**, AABBs per block of 8³ sub-blocks, a two-level DDA inside
a block, AMD Radeon PRO W6800, 2048x2048, RGBA volumes up to 2172x1448x1020 (5.06 GB). Findings:
issuing a new ray query for each next block is expensive; storing **adjacency** per block and
walking into the neighbour block without a new query cut acceleration-structure queries 5-10x and gave
about 1.5x on average (up to 2x) in block-traversal mode, with smaller or negative gains when voxel
access dominates or the data is very sparse. Instance cull masks (8 bits in Vulkan) were used as a
coarse density filter.

**Majercik, Crassin, Shirley, McGuire, "A Ray-Box Intersection Algorithm and Efficient Dynamic Voxel
Rendering", JCGT 7(3):66-81, 2018** [MCSM18]. Pre-RTX (GeForce 1080, Titan V): rasterized billboards
plus a fast ray-box test in the pixel shader, no spatial data structure, every voxel may move each
frame. Claims 2-10x faster ray-box tests than earlier algorithms and 20x primary-ray throughput
against rasterizing optimal meshes from the same voxels; 3.2 Mvoxels/ms in the teaser scene.

**Hobby benchmarks** (not reviewed; use only as hints):
- voxel-bench (ElipticalDrive, repository created 2026-10-10) [VBench]: wgpu ray queries, one BLAS
  per 32³ chunk with one AABB per occupied 4³ brick plus a small DDA, TLAS rebuilt each frame.
  README: "traces fastest on the RT cores, but its BLAS rebuilds make it CPU-bound when chunks stream
  or change"; a 64-tree compute renderer matched the grid DDA's speed with about 15% of its VRAM
  (RTX 5060 Laptop, 1080p). It also reports 1.7-3.0 ms run-to-run variation for the same
  configuration due to clocks and power state.
- VoxelRT StdBVH (software BVH over 8³ bricks) was within about 5% of Tree64 on two scenes and
  15-26% slower on the other four [VRT] (derived from the full README tables).

### 5.3 Claims and caveats to carry into the benchmark discussion

1. **No peer-reviewed head-to-head was found** between RT-core AABB + in-shader DDA and the best
   software voxel structures (NAADF, 64-trees) on the same scenes and GPU. NAADF compares only against
   software structures; [SEA22] compares against an SVO and a dense SDF hierarchy for SDF data.
2. Granularity trade-off (consistent across [Lac21], [SEA22], [DHTI21]): one box per voxel is fastest
   but costs tens of bytes per voxel in the acceleration structure; one box per brick cuts memory
   about 7-20x (derived) but moves work into software DDA and into repeated box hits.
3. Tight boxes matter: [SEA22] attributes part of SBS's performance to tighter boxes around the
   surface; an AABB that is mostly empty still costs a software invocation per hit.
4. Re-entering the acceleration structure has a cost; block walking [DHTI21] or a software grid walk
   between boxes avoids it.
5. Build and update cost: published voxel papers give no per-AABB BLAS build times (unverified gap).
   A refit is only valid if the primitive count and order stay the same, so a brick that appears or
   disappears forces a rebuild of its BLAS; per-group BLAS limits the rebuild to the changed group.
   [VBench] reports CPU-bound rebuilds during streaming (unreviewed).
6. Acceleration-structure memory per AABB is driver-specific; the only measured figure found is
   about 53 B per primitive including buffers on OptiX/RTX 3060 Ti (derived from [Lac21]).

---

## 6. Benchmark methodology in this literature

### 6.1 Metrics in use

| Metric | Used by | What it hides |
|---|---|---|
| Mrays/s, primary | [LK10a], [UOM26], [VRT], [YKL17] | Coherence; image content; whether shading is included |
| Mrays/s, secondary / diffuse bounce | [UOM26] Table 1, [VRT] "PT1", [YKL17] bounces 0/1/4 | How bounce rays were generated (cosine, from which hits) |
| ms per frame at a resolution | [UOM26] Table 5, [SEA22], [Lac21], [DHTI21] | Rays per pixel and path length |
| Iterations per ray, clocks per iteration | [LK10b] Table 6, [VRT], [Dub24] (cycles per ray) | Cost per iteration differs a lot between methods |
| Bytes per voxel / per node, total MB | [LK10a] Table 1, [UOM26] Table 3, [Dub24] | Whether materials, attributes and acceleration structures are included |
| Build / sync time | [LK10a] Table 1, [UOM26] Table 2, [VRT] | CPU vs GPU builds, single vs multi-threaded |
| Image quality (PSNR, MS-SSIM, FLIP) vs reference | [UOM26] (visual vs 8192 spp), [VBench] | Needed whenever GI methods trade samples for speed |

### 6.2 Known pitfalls

1. **Rays vs pixels.** Mrays/s is only comparable when the ray count per pixel is stated. A path with
   bounces, shadow rays and outline rays counts several rays per pixel ([Lac21] Table 37-2 lists them).
2. **Resolution changes Mrays/s.** Higher resolution means more coherent neighbouring rays: ESVO
   gains about 30-60% (with beam optimization) from 512x384 to 2048x1536 on the same scene (derived from [LK10a] Table 2). NAADF chose 2160p for its
   structure test to remove CPU limits [UOM26]. Compare at the same resolution, or report ms and the
   resolution together.
3. **Primary vs secondary rays.** Ratios between methods change with coherence: NAADF's advantage over
   SVDAG is 6.5x primary but 8.4x secondary on SAN MIGUEL; distance fields lose much more than
   space-partitioning methods on incoherent rays in VoxelRT (EuclideanDF PT1 ratio 0.13-0.41x) [VRT].
4. **Denoiser and resampling costs.** A "GI frame time" may or may not include the denoiser: NAADF
   reports both (12.78 vs 13.45 ms; baseline 27.37 vs 34.83 ms with SVGF) [UOM26 Table 5]. A faster
   pipeline can come from tracing fewer rays (0.25 spp) rather than from faster traversal.
5. **Isolated traversal vs in-pipeline cost.** Table 1 of NAADF implies 3.9 ms per secondary ray per
   pixel at 2160p (derived), while its GI pipeline spends 19.4 ms on 1 spp of secondary paths at 1440p;
   bounce count, shading and divergence explain the difference. State which one is measured.
6. **Warm-up, clocks and repeats.** [LK10a] repeated frames "to amortize startup and flush delays";
   [YKL17] reported the best of 10 launches with power management disabled and fixed SM/memory
   clocks; [SEA22] warmed up for 30 s and averaged 5 runs of a camera path; [DHTI21] averaged 128
   timestamp pairs; [VBench] saw 1.7-3.0 ms spread between runs and used interleaved repeats. Report
   which policy is used.
7. **Viewpoints.** Single viewpoints can flatter a method (beam optimization helps primary rays only;
   grazing rays near walls are worst cases for every marcher [Dub24]). Use several viewpoints or a
   camera path.
8. **Hardware generation.** ESVO ran on a 2008 Quadro FX 5800; NAADF on a 2022 RTX 3090 Ti; VoxelRT on
   an integrated GPU. Memory bandwidth alone differs by about 10x between the first two (about
   102 vs about 1008 GB/s, vendor specifications, not from the papers). Never compare absolute Mrays/s
   across papers; compare ratios inside one paper's harness.
9. **Memory accounting.** Some tables count topology only, some include materials, the hardware AS,
   history buffers (NAADF's 32-frame TAA is 973 MB at 1440p) or scratch. State what is counted.
10. **Scene representativeness.** Voxelized triangle scenes are surface-like (local dimensionality
   about 2 [LK10b]); terrain, fluids or dense volumes behave differently. Several papers note OOM for
   baselines at large scale, which can come from the harness (for example a grid of trees) rather than
   the method [UOM26 Sec. 5.1].
11. **CPU-side costs.** Edits, entity updates and BLAS rebuilds can make a GPU-fast method CPU-bound
   [UOM26 Sec. 5.3], [VBench]. Report CPU time and latency for dynamic scenes.

---

## 7. Summary table

| Method | Node / cell layout | Depth for 4096³ | Bytes per voxel (published) | Edit story | Best published throughput (hardware) |
|---|---|---|---|---|---|
| ESVO [LK10a] | 64-bit child descriptor (2x8-bit masks, 15-bit pointer, contour fields) | 12 | about 5-8 incl. colour + normal | static, CPU rebuild | 169 Mrays/s primary, 2048x1536, Quadro FX 5800 |
| 64-tree [Dub24, VRT] | 12 B node: 64-bit mask + 31-bit pointer + leaf bit | 6 | 0.62 (Bistro, no materials) | path copying O(log n), or chunk rebuild | 209 Mrays/s primary on an integrated GPU |
| NAADF [UOM26] | 32-bit chunk (16³) -> 32-bit block (4³) -> 16-bit voxel; AADF in empty cells; hashed voxel groups | 3 (fixed) | about 0.07 (SAN MIGUEL: 75 MB for 2^30 cells, derived) | CPU copy + background AADF refresh | 7029 / 2105 Mrays/s primary / secondary, 2160p, RTX 3090 Ti |
| NanoVDB [Mus21] | root hash -> 32³ -> 16³ -> 8³ leaf, 32 B aligned, stats per node | 3 below root | 4.19 per leaf slot (float), 1.5 bits (mask) | static topology | 2.4 ms level-set render, 1024², Quadro RTX 8000 [BM20] |
| HW AABB + DDA [Lac21, SEA22, DHTI21] | BLAS of boxes (voxel or brick), software DDA inside | 2 (TLAS/BLAS) + brick | about 53 B per AABB incl. AS (derived, OptiX) | rebuild BLAS of changed groups | scene-dependent; see Section 5.2 |

The NAADF bytes-per-voxel figure divides total memory by all grid cells including empty space, so it
is not comparable with the per-surface-voxel figures of the other rows.

---

## 8. Bibliography

Peer-reviewed or archival:

- [AW87] J. Amanatides, A. Woo. "A Fast Voxel Traversal Algorithm for Ray Tracing." Eurographics 1987 Technical Papers. DOI 10.2312/egtp.19871000. https://doi.org/10.2312/egtp.19871000
- [AL09] T. Aila, S. Laine. "Understanding the Efficiency of Ray Traversal on GPUs." High-Performance Graphics 2009, 145-149. DOI 10.1145/1572769.1572792. https://doi.org/10.1145/1572769.1572792
- [BK26] Bakharev, Krylov. "Optimization of GPU Ray Tracing Algorithm for Voxel Data Using a Tetrahexacontree Data Structure." Doklady Mathematics 113(2):59-66, 2026. DOI 10.1134/S1064562426700080. https://link.springer.com/article/10.1134/S1064562426700080 (full text not read)
- [CBE20] V. Careil, M. Billeter, E. Eisemann. "Interactively Modifying Compressed Sparse Voxel Representations." Computer Graphics Forum 39(2):111-119, 2020. DOI 10.1111/cgf.13916. https://doi.org/10.1111/cgf.13916
- [CNLE09] C. Crassin, F. Neyret, S. Lefebvre, E. Eisemann. "GigaVoxels: Ray-Guided Streaming for Efficient and Detailed Voxel Rendering." I3D 2009, 15-22. DOI 10.1145/1507149.1507152. https://doi.org/10.1145/1507149.1507152
- [CS94] D. Cohen, Z. Sheffer. "Proximity clouds - an acceleration technique for 3D grid traversal." The Visual Computer 11(1):27-38, 1994. DOI 10.1007/BF01900697. https://doi.org/10.1007/BF01900697
- [DHTI21] M. O. Derin, T. Harada, Y. Takeda, Y. Iba. "Sparse Volume Rendering using Hardware Ray Tracing and Block Walking." SIGGRAPH Asia 2021 Technical Communications. DOI 10.1145/3478512.3488608. Author PDF: https://gpuopen.com/download/SA2021_BlockWalk.pdf
- [Har96] J. C. Hart. "Sphere tracing: a geometric method for the antialiased ray tracing of implicit surfaces." The Visual Computer 12(10):527-545, 1996. DOI 10.1007/s003710050084. https://doi.org/10.1007/s003710050084
- [Hoe16] R. K. Hoetzlein. "GVDB: Raytracing Sparse Voxel Database Structures on the GPU." High-Performance Graphics 2016. DOI 10.2312/hpg.20161197. https://diglib.eg.org/handle/10.2312/hpg20161197
- [KBSS01] L. Kobbelt, M. Botsch, U. Schwanecke, H.-P. Seidel. "Feature Sensitive Surface Extraction from Volume Data." SIGGRAPH 2001 (directed distance fields; cited by [UOM26]; not re-checked here).
- [KSA13] V. Kämpe, E. Sintorn, U. Assarsson. "High Resolution Sparse Voxel DAGs." ACM TOG 32(4), 2013. DOI 10.1145/2461912.2462024. https://doi.org/10.1145/2461912.2462024
- [Lac21] D. Lacewell. "Ray-Tracing Small Voxel Scenes." Ray Tracing Gems II, Chapter 37, Apress 2021 (open access). DOI 10.1007/978-1-4842-7185-8_37. https://link.springer.com/content/pdf/10.1007/978-1-4842-7185-8_37.pdf
- [LK10a] S. Laine, T. Karras. "Efficient Sparse Voxel Octrees." I3D 2010, 55-63. DOI 10.1145/1730804.1730814. Paper: https://research.nvidia.com/publication/2010-02_efficient-sparse-voxel-octrees
- [LK10b] S. Laine, T. Karras. "Efficient Sparse Voxel Octrees - Analysis, Extensions, and Implementation." NVIDIA Technical Report NVR-2010-001, 2010. https://research.nvidia.com/sites/default/files/publications/laine2010tr1_paper.pdf
- [LK11] S. Laine, T. Karras. "Efficient Sparse Voxel Octrees." IEEE TVCG 17(8):1048-1059, 2011. DOI 10.1109/TVCG.2010.240. https://doi.org/10.1109/TVCG.2010.240
- [LR13] X. Liu, J. Rokne. "A micro 64-tree structure for accelerating ray tracing on a GPU." Graphics Interface 2013, 165-172. https://graphicsinterface.org/proceedings/gi2013/gi2013-22 (ACM DL id 10.5555/2532129.2532158)
- [MCSM18] A. Majercik, C. Crassin, P. Shirley, M. McGuire. "A Ray-Box Intersection Algorithm and Efficient Dynamic Voxel Rendering." JCGT 7(3):66-81, 2018. https://jcgt.org/published/0007/03/04/
- [MOB21] D. Meister, S. Ogaki, C. Benthin, M. J. Doyle, M. Guthe, J. Bittner. "A Survey on Bounding Volume Hierarchies for Ray Tracing." Computer Graphics Forum 40(2):683-712, 2021. DOI 10.1111/cgf.142662. https://doi.org/10.1111/cgf.142662
- [Mus13] K. Museth. "VDB: High-Resolution Sparse Volumes with Dynamic Topology." ACM TOG 32(3), article 27, 2013. DOI 10.1145/2487228.2487235. https://doi.org/10.1145/2487228.2487235
- [Mus14] K. Museth. "Hierarchical Digital Differential Analyzer for Efficient Ray-Marching in OpenVDB." SIGGRAPH 2014 Talks, article 40. DOI 10.1145/2614106.2614136. Author PDF: https://research.dreamworks.com/wp-content/uploads/2018/07/talk_hierarchical_digital_OpenVDB_v4-Edited.pdf
- [Mus21] K. Museth. "NanoVDB: A GPU-Friendly and Portable VDB Data Structure For Real-Time Rendering And Simulation." SIGGRAPH 2021 Talks. DOI 10.1145/3450623.3464653. PDF: https://research.nvidia.com/labs/prl/nanovdb/nanovdb2021.pdf
- [OLK21] Y. Ouyang, S. Liu, M. Kettunen, M. Pharr, J. Pantaleoni. "ReSTIR GI: Path Resampling for Real-Time Path Tracing." Computer Graphics Forum 40(8), 2021 (cited by [UOM26]; not re-checked here).
- [SEA22] H. H. Söderlund, A. Evans, T. Akenine-Möller. "Ray Tracing of Signed Distance Function Grids." JCGT 11(3):94-113, 2022. https://jcgt.org/published/0011/03/06/
- [UOM26] A. Ulschmid, M. Ott, J. Macho, M. Wimmer, S. Ohrhallinger. "NAADF: Globally Illuminated Voxel Worlds Accelerated with Nested Axis-Aligned Distance Fields." Computer Graphics Forum 45(2), article 53, Eurographics 2026. DOI 10.1111/cgf.70413. https://www.cg.tuwien.ac.at/research/publications/2026/ulschmid-2026-naadf/ ; PDF: https://www.cg.tuwien.ac.at/research/publications/2026/ulschmid-2026-naadf/ulschmid-2026-naadf-paper.pdf
- [vW15] T. van Wingerden. "Real-time Ray Tracing and Editing of Large Voxel Scenes." MSc thesis, Utrecht University, 2015. https://studenttheses.uu.nl/handle/20.500.12932/20460 (PDF not retrieved)
- [WHS24] F. Williams, J. Huang, J. Swartz, G. Klár, V. Thakkar, M. Cong, et al. "fVDB: A Deep-Learning Framework for Sparse, Large-Scale, and High-Performance Spatial Intelligence." ACM TOG 43(4), 2024. DOI 10.1145/3658226. arXiv: https://arxiv.org/abs/2407.01781
- [YKL17] H. Ylitie, T. Karras, S. Laine. "Efficient Incoherent Ray Traversal on GPUs Through Compressed Wide BVHs." High-Performance Graphics 2017. DOI 10.1145/3105762.3105773. https://research.nvidia.com/sites/default/files/publications/ylitie2017hpg-paper.pdf
- [ZKV92] K. J. Zuiderveld, A. H. J. Koning, M. A. Viergever. "Acceleration of ray-casting using 3-D distance transforms." Proc. SPIE 1808 (Visualization in Biomedical Computing 1992), 324-335. DOI 10.1117/12.131088. https://doi.org/10.1117/12.131088

Code, blogs and other non-reviewed sources:

- [BM20] W. Braithwaite, K. Museth. "Accelerating OpenVDB on GPUs with NanoVDB." NVIDIA Technical Blog, 2020-08-20. https://developer.nvidia.com/blog/accelerating-openvdb-on-gpus-with-nanovdb
- [BMap] stijnherfst. "BrickMap: a high performance realtime CUDA voxel path tracer." https://github.com/stijnherfst/BrickMap
- [Dub24] dubiousconst282. "A guide to fast voxel ray tracing using sparse 64-trees." Blog, 2024-10-03. https://dubiousconst282.github.io/2024/10/03/voxel-ray-tracing/
- [E64] expenses. "tree64: Implementation of a sparse 64-tree with hashing and modifications" (Rust). https://github.com/expenses/tree64
- [NAADF-git] cg-tuwien. NAADF reference implementation. https://github.com/cg-tuwien/NAADF
- [NanoVDB-src] OpenVDB repository, `nanovdb/nanovdb/NanoVDB.h` (struct layouts used in Section 4.3). https://github.com/AcademySoftwareFoundation/openvdb/blob/master/nanovdb/nanovdb/NanoVDB.h
- [Tek] teknologicus. "Tetahexacontree 4x4x4 Occupancy Bitmasks Lookup Table" (Vorxel devlog). https://teknologicus.itch.io/vorxel/devlog/839586/tetahexacontree-4x4x4-occupancy-bitmasks-lookup-table
- [VBench] ElipticalDrive. "voxel-bench / MultiRenderer: a test bench for voxel renderers." https://github.com/ElipticalDrive/voxel-bench (created 2026-10-10)
- [VRT] dubiousconst282. "VoxelRT: Voxel rendering experiments" (README benchmark). https://github.com/dubiousconst282/VoxelRT

Searched but not used (no technical publication found): Teardown (Tuxedo Labs) renderer details;
a published RTX-vs-software comparison on the same voxel scenes.
