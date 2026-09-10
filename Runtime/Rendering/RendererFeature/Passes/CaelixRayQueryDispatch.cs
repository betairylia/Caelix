using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Caelix.Rendering.RayQuery;

/// <summary>
/// The scene-side bindings every inline ray query trace kernel needs, shared by the G-buffer
/// stages of both renderer features.
/// </summary>
/// <remarks>
/// A ray query has no shader table, so everything the DXR hit group used to receive per instance
/// is a global buffer instead: the acceleration structure, the brick pool's pages, the per-instance
/// record table and the baked material table. The two stages differ only in what they trace, not in
/// how the scene reaches the kernel, so that part lives here.
/// </remarks>
internal static class CaelixRayQueryDispatch
{
    /// <summary>Shader property names of the brick pool pages, indexed by page.</summary>
    public static readonly string[] BrickPageNames = Enumerable
        .Range(0, CaelixBrickPool.MaxNamedPages)
        .Select(i => $"g_bricks{i}")
        .ToArray();

    private static readonly string[] BakeKernelNames =
    {
        "CaelixBakeMaterialsAlbedo", "CaelixBakeMaterialsEmission", "CaelixBakeMaterialsScalars"
    };

    /// <summary>All three bake kernel indices, or null when any is missing from the compute shader.</summary>
    public static int[] FindBakeKernels(ComputeShader cs)
    {
        if (cs == null)
        {
            return null;
        }

        int[] kernels = new int[BakeKernelNames.Length];
        for (int i = 0; i < kernels.Length; i++)
        {
            if (!cs.HasKernel(BakeKernelNames[i]))
            {
                return null;
            }

            kernels[i] = cs.FindKernel(BakeKernelNames[i]);
        }

        return kernels;
    }

    /// <summary>
    /// Refills <paramref name="pages"/> with the pool's page buffers, or returns null when there is
    /// no pool to bind (a renderer that released its resources between record and now).
    /// </summary>
    /// <remarks>
    /// The array is the caller's scratch, refilled at record time rather than reallocated: the page
    /// buffers only change between frames. Every entry is a real buffer, because the shader
    /// declares them all and Unity logs an error every frame for any it never sees bound.
    /// </remarks>
    public static GraphicsBuffer[] FillBrickPages(CaelixBrickPool pool, GraphicsBuffer[] pages)
    {
        if (pool == null)
        {
            return null;
        }

        for (int i = 0; i < pages.Length; i++)
        {
            // GetPageBuffer clamps to the last open page, so the slots no instance names are still
            // bound to a real buffer.
            pages[i] = pool.GetPageBuffer(i);
        }

        return pages;
    }

    /// <summary>
    /// Enables exactly the group-shape keyword the renderer's grouping needs on this kernel's
    /// compute shader.
    /// </summary>
    /// <remarks>
    /// Recorded into the command buffer rather than set with <c>ComputeShader.EnableKeyword</c>,
    /// which would mutate the asset's runtime keyword state instead of the command stream.
    /// </remarks>
    public static void SetGroupKeyword(CommandBuffer cmd, ComputeShader cs, RenderGroupSize size)
    {
        string wanted = RenderGroupPresets.Keyword(size);
        foreach (string name in RenderGroupPresets.Keywords)
        {
            cmd.SetKeyword(cs, new LocalKeyword(cs, name), name == wanted);
        }
    }

    /// <summary>
    /// Binds the acceleration structure, the brick pool pages, the instance records and the
    /// material table to one trace kernel.
    /// </summary>
    public static void BindSceneInputs(
        CommandBuffer cmd,
        ComputeShader cs,
        int kernel,
        RayTracingAccelerationStructure voxAS,
        GraphicsBuffer[] brickPages,
        GraphicsBuffer instanceTable,
        GraphicsBuffer materialTable)
    {
        cmd.SetRayTracingAccelerationStructure(cs, kernel, "g_AccelStruct", voxAS);

        for (int i = 0; i < brickPages.Length; i++)
        {
            cmd.SetComputeBufferParam(cs, kernel, BrickPageNames[i], brickPages[i]);
        }

        cmd.SetComputeBufferParam(cs, kernel, "g_Instances", instanceTable);
        cmd.SetComputeBufferParam(cs, kernel, "g_Materials", materialTable);
    }

    /// <summary>
    /// Records the three per-field material bake dispatches that fill <paramref name="materialTable"/>.
    /// </summary>
    /// <remarks>
    /// The static material tables cannot live in a trace kernel — D3D12 refuses the compute pipeline
    /// state — so they are copied into a buffer once, by kernels small enough to carry them. See the
    /// MATERIALS note in <c>Shaders/RayQuery/CaelixMaterialTable.hlsl</c>.
    /// </remarks>
    public static void BakeMaterials(
        CommandBuffer cmd, ComputeShader cs, int[] bakeKernels, GraphicsBuffer materialTable)
    {
        for (int i = 0; i < bakeKernels.Length; i++)
        {
            int bake = bakeKernels[i];
            cmd.SetComputeBufferParam(cs, bake, "g_MaterialsOut", materialTable);
            cmd.DispatchCompute(cs, bake, CaelixRayQueryRenderer.MaterialTableEntries / 64, 1, 1);
        }
    }
}
