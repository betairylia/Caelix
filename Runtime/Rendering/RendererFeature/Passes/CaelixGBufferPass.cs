using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Caelix.Rendering.RayQuery;

/// <summary>
/// Stage 1 of 3: traces the voxel scene and produces the Caelix G-buffer.
/// </summary>
/// <remarks>
/// Writes deterministic (non-denoised) radiance, per-lobe stochastic radiance with hit distance
/// (diffuse and specular, NRD-style), albedo, normal+roughness, depth and 2.5D motion vectors,
/// plus this frame's depth/normal history for next frame's temporal rejection. Everything it
/// produces is published to <see cref="CaelixFrameResources"/> and, for the shaders that sample
/// them by name, as global textures. Downstream stages read that contract and never touch the tracer.
/// <para>
/// The trace itself is an inline ray query compute kernel (<c>CaelixPathTraceRQ.compute</c>)
/// dispatched one thread per pixel; <see cref="ConfigureSettings"/> binds the scene renderer that
/// owns the acceleration structure, the brick pool and the instance table it reads.
/// </para>
/// </remarks>
public class CaelixGBufferPass : ScriptableRenderPass
{
    private Texture2D blueNoiseTexture;
    private CaelixTraceSettings settings = new CaelixTraceSettings().Validated();

    private CaelixRayQueryRenderer rayQuery;
    private ComputeShader computeShader;
    private int kernel = -1;
    /// <summary>The three per-field material bake kernels; all must exist for the backend to be ready.</summary>
    private int[] bakeMaterialsKernels;

    /// <summary>Stand-in sky used when the sky provider has not published its cubemap yet.</summary>
    private static Cubemap s_FallbackSky;
    private static bool s_WarnedMissingSky;

    internal class PassData
    {
        internal uint width;
        internal uint height;
        internal float zoom;
        internal float aspectRatio;
        /// <summary>This frame's global sub-pixel ray offset, in pixels, in launch space.</summary>
        internal Vector2 jitter;
        internal int frameIndex;

        internal Matrix4x4 worldToCamera;
        internal Matrix4x4 cameraToWorld;
        internal Matrix4x4 previousWorldToCamera;
        internal Vector3 cameraWorldPosition;
        internal int convergedFrames;

        internal CaelixTraceSettings settings;
        internal Vector4 mainLightColor;

        internal TextureHandle DeterministicRadiance;
        internal TextureHandle DiffuseRadiance;
        internal TextureHandle SpecularRadiance;
        internal TextureHandle Albedo;
        internal TextureHandle Normal;
        internal TextureHandle Depth;
        internal TextureHandle MotionVector;
        internal TextureHandle CurrentDepthHistory;
        internal TextureHandle CurrentNormalHistory;

        internal RayTracingAccelerationStructure voxAS;
        internal Texture2D blueNoiseTexture;

        internal ComputeShader computeShader;
        internal int kernel;
        /// <summary>Group shape the trace kernel must decode this frame.</summary>
        internal RenderGroupSize groupSize;
        internal int[] bakeMaterialsKernels;
        /// <summary>Set the first time a material buffer is used: the bake kernel fills it before the trace.</summary>
        internal bool bakeMaterials;
        /// <summary>One VoxelMaterial per 16-bit block ID, bound as <c>g_Materials</c>.</summary>
        internal GraphicsBuffer materialTable;
        /// <summary>
        /// The brick pool's pages, bound as <c>g_bricks0..15</c>. Always
        /// <see cref="CaelixBrickPool.MaxNamedPages"/> long, and every entry is a real buffer: the
        /// shader declares them all and Unity logs an error every frame for any it never sees bound.
        /// </summary>
        internal GraphicsBuffer[] brickPages;
        /// <summary>Per-RTAS-instance records, bound as <c>g_Instances</c>.</summary>
        internal GraphicsBuffer instanceTable;
    }

    /// <summary>
    /// Scratch for <see cref="PassData.brickPages"/>, owned by this pass instance. The page buffers
    /// only change between frames, so the array is refilled at record time rather than reallocated.
    /// </summary>
    private readonly GraphicsBuffer[] brickPages = new GraphicsBuffer[CaelixBrickPool.MaxNamedPages];

    /// <summary>
    /// Binds the scene renderer, its compute kernel and this frame's settings.
    /// Called once per camera before enqueueing.
    /// </summary>
    public void ConfigureSettings(
        CaelixRayQueryRenderer rq, ComputeShader cs, Texture2D blueNoise, CaelixTraceSettings traceSettings)
    {
        rayQuery = rq;
        computeShader = cs;
        // HasKernel first: FindKernel logs an error and throws when the kernel is missing, and a
        // renderer asset can easily point at the wrong compute shader.
        kernel = (cs != null && cs.HasKernel("CaelixPathTraceKernel")) ? cs.FindKernel("CaelixPathTraceKernel") : -1;
        bakeMaterialsKernels = CaelixRayQueryDispatch.FindBakeKernels(cs);
        blueNoiseTexture = blueNoise;
        settings = traceSettings.Validated();
    }

    /// <summary>
    /// True when the stage has everything it needs to record.
    /// </summary>
    /// <remarks>
    /// <see cref="CaelixRayQueryRenderer.HasResources"/> is part of it: the component only owns its
    /// GPU buffers between Awake/Tick and OnDisable, so without that check a Scene view camera in
    /// edit mode (or a disabled component in play mode) would dispatch against null buffers and log
    /// "Property (g_Materials) ... is not set" every frame.
    /// </remarks>
    public bool IsReady => rayQuery != null && rayQuery.HasResources && computeShader != null
        && kernel >= 0 && bakeMaterialsKernels != null;

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        CaelixFrameResources resources = frameData.GetOrCreate<CaelixFrameResources>();
        if (!IsReady)
        {
            return;
        }

        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        cameraData.camera.forceIntoRenderTexture = true;

        Matrix4x4 worldToCamera = cameraData.GetViewMatrix();
        CaelixCameraHistory history = CaelixCameraHistory.BeginFrame(
            cameraData.camera,
            worldToCamera,
            cameraData.camera.fieldOfView,
            cameraData.scaledWidth,
            cameraData.scaledHeight,
            settings.maximumAverageFrames);

        resources.History = history;
        resources.DeterministicRadiance = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.BaseDescriptor(cameraData), "Caelix_outDeterministicRadiance", false);
        resources.StochasticDiffuse = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf),
            "Caelix_outStochasticDiffuse", false);
        resources.StochasticSpecular = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf),
            "Caelix_outStochasticSpecular", false);
        resources.Albedo = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.Default),
            "Caelix_outAlbedo", false);
        resources.Normal = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf),
            "Caelix_outNormal", false);
        resources.Depth = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.RFloat),
            "Caelix_outDepth", false);
        // ARGBFloat, NRD 2.5D: .xy = previousUV - currentUV, .z = viewZprev - viewZ, .a reserved.
        resources.MotionVector = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.BaseDescriptor(cameraData), "Caelix_outMotionVector", false);

        // The history halves are persistent RTHandles, so they are imported rather than created.
        // Only the "current" halves are imported here; the denoise stage imports the "previous" ones,
        // so no RTHandle enters the graph twice.
        resources.CurrentDepthHistory = renderGraph.ImportTexture(history.CurrentDepth);
        resources.CurrentNormalHistory = renderGraph.ImportTexture(history.CurrentNormal);

        using (var builder = renderGraph.AddUnsafePass<PassData>("Caelix RayQuery Trace", out var passData))
        {
            passData.width = (uint)cameraData.scaledWidth;
            passData.height = (uint)cameraData.scaledHeight;
            // The camera's own vertical FOV, and the jitter the filters will reconstruct rays with:
            // both come from the history so the tracer and every consumer share one projection.
            passData.zoom = history.Zoom;
            passData.aspectRatio = history.Aspect;
            passData.jitter = history.Jitter;
            passData.frameIndex = Time.frameCount;

            passData.worldToCamera = worldToCamera;
            passData.cameraToWorld = worldToCamera.inverse;
            passData.previousWorldToCamera = history.PreviousViewMatrix;
            passData.cameraWorldPosition = passData.cameraToWorld.MultiplyPoint3x4(Vector3.zero);
            passData.convergedFrames = history.ConvergedFrames;

            passData.settings = settings;
            passData.mainLightColor = ResolveMainLightColor(frameData.Get<UniversalLightData>());

            passData.DeterministicRadiance = resources.DeterministicRadiance;
            passData.DiffuseRadiance = resources.StochasticDiffuse;
            passData.SpecularRadiance = resources.StochasticSpecular;
            passData.Albedo = resources.Albedo;
            passData.Normal = resources.Normal;
            passData.Depth = resources.Depth;
            passData.MotionVector = resources.MotionVector;
            passData.CurrentDepthHistory = resources.CurrentDepthHistory;
            passData.CurrentNormalHistory = resources.CurrentNormalHistory;

            passData.voxAS = rayQuery.voxelScene;
            passData.blueNoiseTexture = blueNoiseTexture;

            passData.computeShader = computeShader;
            passData.kernel = kernel;
            passData.groupSize = rayQuery.GroupSize;
            passData.brickPages = CaelixRayQueryDispatch.FillBrickPages(rayQuery?.Pool, brickPages);
            passData.instanceTable = rayQuery?.Instances?.Buffer;
            passData.bakeMaterialsKernels = bakeMaterialsKernels;
            passData.materialTable = rayQuery?.MaterialTable;
            // The bake is recorded ahead of the trace in the same command buffer, so flipping the
            // flag at record time is safe; the pass is never culled.
            passData.bakeMaterials = rayQuery != null && !rayQuery.MaterialsBaked;
            if (rayQuery != null)
            {
                rayQuery.MaterialsBaked = true;
            }

            builder.UseTexture(passData.DeterministicRadiance, AccessFlags.Write);
            builder.UseTexture(passData.DiffuseRadiance, AccessFlags.Write);
            builder.UseTexture(passData.SpecularRadiance, AccessFlags.Write);
            builder.UseTexture(passData.Albedo, AccessFlags.Write);
            builder.UseTexture(passData.Normal, AccessFlags.Write);
            builder.UseTexture(passData.Depth, AccessFlags.Write);
            builder.UseTexture(passData.MotionVector, AccessFlags.Write);
            builder.UseTexture(passData.CurrentDepthHistory, AccessFlags.Write);
            builder.UseTexture(passData.CurrentNormalHistory, AccessFlags.Write);

            builder.SetGlobalTextureAfterPass(passData.DeterministicRadiance, CaelixShaderIDs.DeterministicRadianceTex);
            builder.SetGlobalTextureAfterPass(passData.DiffuseRadiance, CaelixShaderIDs.DiffuseRadianceTex);
            builder.SetGlobalTextureAfterPass(passData.SpecularRadiance, CaelixShaderIDs.SpecularRadianceTex);
            builder.SetGlobalTextureAfterPass(passData.Albedo, CaelixShaderIDs.AlbedoTex);
            builder.SetGlobalTextureAfterPass(passData.Normal, CaelixShaderIDs.NormalTex);
            builder.SetGlobalTextureAfterPass(passData.Depth, CaelixShaderIDs.DepthTex);
            builder.SetGlobalTextureAfterPass(passData.MotionVector, CaelixShaderIDs.MotionVectorTex);
            builder.SetGlobalTextureAfterPass(passData.CurrentDepthHistory, CaelixShaderIDs.CurrentDepthHistoryTex);
            builder.SetGlobalTextureAfterPass(passData.CurrentNormalHistory, CaelixShaderIDs.CurrentNormalHistoryTex);

            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) => Execute(data, ctx));
        }

        resources.IsValid = true;
    }

    private static Vector4 ResolveMainLightColor(UniversalLightData lightData)
    {
        int index = lightData.mainLightIndex;
        if (index < 0 || index >= lightData.visibleLights.Length)
        {
            return Vector4.zero;
        }

        Light light = lightData.visibleLights[index].light;
        if (light == null)
        {
            return Vector4.zero;
        }

        Color temperature = light.useColorTemperature
            ? Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature)
            : Color.white;

        return light.color.linear * light.intensity * temperature;
    }

    /// <summary>
    /// Binds the scene, the targets and this frame's uniforms to the trace kernel, then dispatches
    /// one thread per pixel.
    /// </summary>
    /// <remarks>
    /// Everything goes through the native command buffer: the unsafe pass context exposes no
    /// compute-shader setters of its own.
    /// </remarks>
    private static void Execute(PassData data, UnsafeGraphContext context)
    {
        // Belt and braces: the renderer can only release its buffers between frames, and IsReady
        // already refuses to record without them, but a null page here would be a driver-level error.
        if (data.brickPages == null)
        {
            return;
        }

        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        ComputeShader cs = data.computeShader;
        int k = data.kernel;

        if (data.settings.buildAccelerationStructure)
        {
            cmd.BuildRayTracingAccelerationStructure(data.voxAS);
        }

        // The static material tables cannot live in the trace kernel (see CaelixMaterialTable.hlsl),
        // so they are copied into a buffer once, by kernels small enough to carry them.
        if (data.bakeMaterials)
        {
            CaelixRayQueryDispatch.BakeMaterials(cmd, cs, data.bakeMaterialsKernels, data.materialTable);
        }

        CaelixRayQueryDispatch.SetGroupKeyword(cmd, cs, data.groupSize);

        CaelixRayQueryDispatch.BindSceneInputs(
            cmd, cs, k, data.voxAS, data.brickPages, data.instanceTable, data.materialTable);

        cmd.SetComputeTextureParam(cs, k, "DeterministicRadianceTarget", (RTHandle)data.DeterministicRadiance);
        cmd.SetComputeTextureParam(cs, k, "DiffuseRadianceTarget", (RTHandle)data.DiffuseRadiance);
        cmd.SetComputeTextureParam(cs, k, "SpecularRadianceTarget", (RTHandle)data.SpecularRadiance);
        cmd.SetComputeTextureParam(cs, k, "AlbedoTarget", (RTHandle)data.Albedo);
        cmd.SetComputeTextureParam(cs, k, "NormalTarget", (RTHandle)data.Normal);
        cmd.SetComputeTextureParam(cs, k, "DepthTarget", (RTHandle)data.Depth);
        cmd.SetComputeTextureParam(cs, k, "MotionVectorTarget", (RTHandle)data.MotionVector);
        cmd.SetComputeTextureParam(cs, k, "g_CurrentDepthHistory", (RTHandle)data.CurrentDepthHistory);
        cmd.SetComputeTextureParam(cs, k, "g_CurrentNormalHistory", (RTHandle)data.CurrentNormalHistory);

        // The sky provider publishes its cubemap as a plain global, which render graph cannot track
        // (builder.UseGlobalTexture does not see it), so it is fetched directly here.
        // TODO: FIXME: Maybe PR to PBSky repo so we can use the texture properly ... idk
        cmd.SetComputeTextureParam(cs, k, "g_Sky", ResolveSkyTexture());

        if (data.blueNoiseTexture != null)
        {
            cmd.SetComputeTextureParam(cs, k, CaelixShaderIDs.BlueNoiseTexture, data.blueNoiseTexture);
        }

        cmd.SetComputeIntParam(cs, "g_FrameIndex", data.frameIndex);
        cmd.SetComputeIntParam(cs, "g_ConvergenceStep", data.convergedFrames);
        cmd.SetComputeIntParam(cs, "g_BounceCountOpaque", data.settings.bounceCountOpaque);
        cmd.SetComputeIntParam(cs, "g_BounceCountTransparent", data.settings.bounceCountTransparent);
        cmd.SetComputeIntParam(cs, "g_spp", data.settings.samplesPerPixel);
        cmd.SetComputeIntParam(cs, "g_EnableSkySun", data.settings.enableSkySun ? 1 : 0);
        cmd.SetComputeFloatParam(cs, "g_SkySunDiskRadius", data.settings.sunDiskRadiusRadians);
        cmd.SetComputeFloatParam(cs, "g_SkySunFlareRadius", data.settings.sunFlareRadiusRadians);
        cmd.SetComputeFloatParam(cs, "g_Zoom", data.zoom);
        cmd.SetComputeFloatParam(cs, "g_AspectRatio", data.aspectRatio);
        cmd.SetComputeVectorParam(cs, "g_Jitter", data.jitter);
        cmd.SetComputeVectorParam(cs, "g_CameraWorldPosition", data.cameraWorldPosition);
        cmd.SetComputeVectorParam(cs, "g_mainLightColor", data.mainLightColor);
        cmd.SetComputeMatrixParam(cs, "g_CurrentWorldToCamera", data.worldToCamera);
        cmd.SetComputeMatrixParam(cs, "g_CurrentCameraToWorld", data.cameraToWorld);
        cmd.SetComputeMatrixParam(cs, "g_PrevWorldToCamera", data.previousWorldToCamera);

        // Replaces DispatchRaysDimensions: the kernel needs the launch size both to flip the
        // vertical axis and to discard the threads the 8x8 rounding adds.
        cmd.SetComputeIntParams(cs, "g_LaunchDim", (int)data.width, (int)data.height);

        cmd.DispatchCompute(cs, k, (int)((data.width + 7) / 8), (int)((data.height + 7) / 8), 1);
    }

    private static Texture ResolveSkyTexture()
    {
        Texture sky = Shader.GetGlobalTexture(CaelixShaderIDs.GlossyEnvironmentCubeMap);
        if (sky != null)
        {
            return sky;
        }

        if (!s_WarnedMissingSky)
        {
            // Warn once: this is normal for a frame or two after a sky change, and log spam here is
            // expensive enough to skew frame timings.
            Debug.LogWarning("Caelix: cannot obtain the current sky cubemap. Using a white fallback sky.");
            s_WarnedMissingSky = true;
        }

        if (s_FallbackSky == null)
        {
            s_FallbackSky = new Cubemap(1, GraphicsFormat.B8G8R8A8_SRGB, TextureCreationFlags.None)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            for (int face = 0; face < 6; face++)
            {
                s_FallbackSky.SetPixel((CubemapFace)face, 0, 0, Color.white);
            }

            s_FallbackSky.Apply();
        }

        return s_FallbackSky;
    }
}
