#ifndef CAELIX_MATERIAL_TABLE_INCLUDED
#define CAELIX_MATERIAL_TABLE_INCLUDED

// The material table every trace kernel reads, and the kernels that bake it. Include this AFTER
// Assets/Caelix/VoxelMaterials.hlsl; the including .compute declares the three
// `#pragma kernel CaelixBakeMaterials*` lines for the functions at the bottom.

// MATERIALS. VoxelMaterials.hlsl holds its tables as `static` arrays, i.e. ~150 KB of immediate
// constant data. The DXR pipeline swallows that, but D3D12 refuses to create a COMPUTE pipeline
// state for the full trace kernel with the tables in it ("could not create a compute pipeline
// state object [8007000e]" = E_OUTOFMEMORY, logged only in the Editor log, and the dispatch is
// silently skipped). So the trace kernel never touches the static tables: it reads materials from
// g_Materials, which the CaelixBakeMaterials* kernels below fill once from those same tables, one
// entry per 16-bit block ID, so GET_MATERIAL keeps its exact semantics (face bits included).
//
// The bake is split by FIELD on purpose. A kernel that copies the whole struct keeps the whole
// 181 KB array of structs as one immediate constant and fails the same way; a kernel that reads
// single fields lets DXC scalar-replace the table into per-field arrays, each under the 64 KB
// (4096 float4) immediate-constant limit, which is why the GET_MATERIAL reads inside the probe
// kernels survive.
#define CAELIX_MATERIAL_TABLE_SIZE 65536u
StructuredBuffer<VoxelMaterial> g_Materials;
RWStructuredBuffer<VoxelMaterial> g_MaterialsOut;
#if !defined(CAELIX_PACKED_SCENE_COLOR)
#define CAELIX_SOURCE_MATERIAL GetMaterial
#undef GET_MATERIAL
#define GET_MATERIAL(id) g_Materials[uint(id) & 0xFFFFu]
#else
#define CAELIX_SOURCE_MATERIAL GetMaterial_Color
#endif

// The three bake kernels copy the static material tables into g_MaterialsOut, indexed by the raw
// 16-bit block ID, one group of fields each (see the note on MATERIALS above). The G-buffer stage
// dispatches all three once per material buffer, CAELIX_MATERIAL_TABLE_SIZE / 64 groups each.
[numthreads(64, 1, 1)]
void CaelixBakeMaterialsAlbedo(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= CAELIX_MATERIAL_TABLE_SIZE)
    {
        return;
    }

    g_MaterialsOut[id.x].albedo = CAELIX_SOURCE_MATERIAL(int(id.x)).albedo;
}

[numthreads(64, 1, 1)]
void CaelixBakeMaterialsEmission(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= CAELIX_MATERIAL_TABLE_SIZE)
    {
        return;
    }

    g_MaterialsOut[id.x].emission = CAELIX_SOURCE_MATERIAL(int(id.x)).emission;
}

[numthreads(64, 1, 1)]
void CaelixBakeMaterialsScalars(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= CAELIX_MATERIAL_TABLE_SIZE)
    {
        return;
    }

    VoxelMaterial material = CAELIX_SOURCE_MATERIAL(int(id.x));
    g_MaterialsOut[id.x].smoothness = material.smoothness;
    g_MaterialsOut[id.x].metallic = material.metallic;
    g_MaterialsOut[id.x].IOR = material.IOR;
    g_MaterialsOut[id.x].extinction = material.extinction;
}

#endif
