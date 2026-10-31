using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Caelix.Rendering.RayQuery;

/// <summary>
/// The GI resampler: estimates the diffuse indirect lighting of every pixel by reusing path samples
/// traced in earlier frames and at other pixels, and writes it into the frame's
/// <see cref="CaelixFrameResources.StochasticDiffuse"/> target.
/// </summary>
/// <remarks>
/// Runs between <see cref="CaelixGBufferPass"/> (in GI mode) and <see cref="CaelixDenoisePass"/>,
/// as ONE unsafe render-graph pass that records the seven kernels of
/// <c>Shaders/Gi/CaelixGiRQ.compute</c> in the order of
/// <c>Documentation~/internals/rendering/GI_RESAMPLING.md</c>. The sample history lives in
/// per-camera <see cref="CaelixGiResources"/>; nothing clears it when the world changes.
/// </remarks>
public class CaelixGiPass : ScriptableRenderPass, IDisposable
{
    /// <summary>A camera's buffers are released after this many frames without a render.</summary>
    private const int UnusedFramesBeforeRelease = 120;

    private static readonly string[] KernelNames =
    {
        "GiPreparePixels",
        "GiPrepareBuckets",
        "GiGenerateSamples",
        "GiPrepareHistory",
        "GiReprojectLit",
        "GiReprojectUnlit",
        "GiRefineBuckets",
        "GiSpatial"
    };

    private const int KernelPreparePixels = 0;
    private const int KernelPrepareBuckets = 1;
    private const int KernelGenerateSamples = 2;
    private const int KernelPrepareHistory = 3;
    private const int KernelReprojectLit = 4;
    private const int KernelReprojectUnlit = 5;
    private const int KernelRefineBuckets = 6;
    private const int KernelSpatial = 7;

    /// <summary>Byte offset of the unlit triple in the indirect arguments buffer.</summary>
    private const uint UnlitArgsOffset = 3 * sizeof(uint);

    private CaelixRayQueryRenderer rayQuery;
    private ComputeShader computeShader;
    private int[] kernels;
    private Texture2D blueNoiseTexture;
    private CaelixTraceSettings traceSettings = new CaelixTraceSettings().Validated();
    private CaelixGiSettings giSettings = CaelixGiSettings.Default;

    private readonly Dictionary<Camera, CaelixGiResources> cameras = new Dictionary<Camera, CaelixGiResources>();
    private readonly List<Camera> pruneScratch = new List<Camera>();

    /// <summary>Scratch for <see cref="PassData.brickPages"/>, refilled at record time.</summary>
    private readonly GraphicsBuffer[] brickPages = new GraphicsBuffer[CaelixBrickPool.MaxNamedPages];

    internal class PassData
    {
        internal ComputeShader computeShader;
        internal int[] kernels;

        internal int width;
        internal int height;
        internal int bucketCount;
        internal int litCapacity;
        internal int unlitCapacity;
        internal uint ringFrame;
        internal int frameIndex;
        internal bool historyValid;

        internal Vector2 jitter;
        internal Vector2 projection;
        internal Matrix4x4 worldToCamera;
        internal Matrix4x4 cameraToWorld;
        internal Vector3 cameraPosition;

        internal CaelixGiSettings settings;
        internal int bounceCountOpaque;

        internal TextureHandle GiSurface;
        internal TextureHandle GiKey;
        internal TextureHandle Normal;
        internal TextureHandle DiffuseRadiance;
        internal TextureHandle MotionVector;
        internal TextureHandle PreviousIndirect;
        /// <summary>This frame's own sample per pixel, RGB9E5, for the spatial pass's fallback.</summary>
        internal TextureHandle OwnSample;

        internal BufferHandle litRing;
        internal BufferHandle unlitRing;
        internal BufferHandle refined;
        internal BufferHandle compact;
        internal BufferHandle buckets;
        internal BufferHandle pixels;
        internal BufferHandle counts;
        internal BufferHandle args;

        internal RayTracingAccelerationStructure voxAS;
        internal GraphicsBuffer[] brickPages;
        internal GraphicsBuffer instanceTable;
        internal GraphicsBuffer materialTable;
        internal RenderGroupSize groupSize;
        internal Texture2D blueNoiseTexture;
    }

    /// <summary>
    /// Binds the scene renderer, the GI compute shader and this frame's settings.
    /// Called once per camera before enqueueing.
    /// </summary>
    public void ConfigureSettings(
        CaelixRayQueryRenderer rq,
        ComputeShader cs,
        Texture2D blueNoise,
        CaelixTraceSettings trace,
        CaelixGiSettings gi)
    {
        rayQuery = rq;
        if (cs != computeShader)
        {
            computeShader = cs;
            kernels = FindKernels(cs);
        }

        blueNoiseTexture = blueNoise;
        traceSettings = trace.Validated();
        giSettings = gi.Validated();
    }

    /// <summary>True when the stage has everything it needs to record.</summary>
    public bool IsReady => rayQuery != null && rayQuery.HasResources && computeShader != null && kernels != null;

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        CaelixFrameResources resources = frameData.GetOrCreate<CaelixFrameResources>();
        if (!resources.IsValid || resources.History == null || !IsReady
            || !resources.GiSurface.IsValid() || !resources.GiKey.IsValid())
        {
            return;
        }

        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        Camera camera = cameraData.camera;
        CaelixCameraHistory history = resources.History;
        int width = cameraData.scaledWidth;
        int height = cameraData.scaledHeight;

        CaelixGiResources gi = GetResources(camera);
        gi.Ensure(width, height, giSettings);
        gi.LastUsedFrame = Time.frameCount;
        PruneUnusedCameras();

        using (var builder = renderGraph.AddUnsafePass<PassData>("Caelix GI Resampling", out var passData))
        {
            passData.computeShader = computeShader;
            passData.kernels = kernels;

            passData.width = gi.Width;
            passData.height = gi.Height;
            passData.bucketCount = gi.BucketCount;
            passData.litCapacity = gi.LitCapacity;
            passData.unlitCapacity = gi.UnlitCapacity;
            passData.ringFrame = gi.AdvanceRingFrame();
            passData.frameIndex = Time.frameCount;

            // The same projection the tracer used this frame, so a reprojected sample lands on the
            // texel whose primary ray passes through it.
            passData.jitter = history.Jitter;
            passData.projection = new Vector2(history.Zoom, history.Aspect);
            passData.worldToCamera = history.ViewMatrix;
            passData.cameraToWorld = history.ViewMatrix.inverse;
            passData.cameraPosition = passData.cameraToWorld.MultiplyPoint3x4(Vector3.zero);

            passData.settings = giSettings;
            passData.bounceCountOpaque = traceSettings.bounceCountOpaque;

            passData.GiSurface = resources.GiSurface;
            passData.GiKey = resources.GiKey;
            passData.Normal = resources.Normal;
            passData.DiffuseRadiance = resources.StochasticDiffuse;
            // Sample rate by pixel age (NAADF rayQueueCalc.fx): the previous temporal history holds
            // the per-pixel accumulated frame count in alpha, reached through this frame's motion vector.
            passData.MotionVector = resources.MotionVector;
            passData.PreviousIndirect = renderGraph.ImportTexture(history.PreviousIndirectRadiance);
            passData.historyValid = history.IsValid;

            RenderTextureDescriptor ownDesc = CaelixFrameResources.BaseDescriptor(cameraData);
            ownDesc.graphicsFormat = GraphicsFormat.R32_UInt;
            ownDesc.enableRandomWrite = true;
            passData.OwnSample = UniversalRenderer.CreateRenderGraphTexture(
                renderGraph, ownDesc, "Caelix_GiOwnSample", false, FilterMode.Point);

            passData.litRing = renderGraph.ImportBuffer(gi.LitRing);
            passData.unlitRing = renderGraph.ImportBuffer(gi.UnlitRing);
            passData.refined = renderGraph.ImportBuffer(gi.Refined);
            passData.compact = renderGraph.ImportBuffer(gi.Compact);
            passData.buckets = renderGraph.ImportBuffer(gi.Buckets);
            passData.pixels = renderGraph.ImportBuffer(gi.Pixels);
            passData.counts = renderGraph.ImportBuffer(gi.Counts);
            passData.args = renderGraph.ImportBuffer(gi.Args);

            passData.voxAS = rayQuery.voxelScene;
            passData.brickPages = CaelixRayQueryDispatch.FillBrickPages(rayQuery.Pool, brickPages);
            passData.instanceTable = rayQuery.Instances?.Buffer;
            passData.materialTable = rayQuery.MaterialTable;
            passData.groupSize = rayQuery.GroupSize;
            passData.blueNoiseTexture = blueNoiseTexture;

            builder.UseTexture(resources.GiSurface, AccessFlags.Read);
            builder.UseTexture(resources.GiKey, AccessFlags.Read);
            builder.UseTexture(resources.Normal, AccessFlags.Read);
            builder.UseTexture(resources.DeterministicRadiance, AccessFlags.Read);
            builder.UseTexture(passData.MotionVector, AccessFlags.Read);
            builder.UseTexture(passData.PreviousIndirect, AccessFlags.Read);
            // Every pixel is written: the resampled estimate, or 0 where no estimate is wanted.
            builder.UseTexture(resources.StochasticDiffuse, AccessFlags.Write);
            builder.UseTexture(passData.OwnSample, AccessFlags.ReadWrite);

            builder.UseBuffer(passData.litRing, AccessFlags.ReadWrite);
            builder.UseBuffer(passData.unlitRing, AccessFlags.ReadWrite);
            builder.UseBuffer(passData.refined, AccessFlags.ReadWrite);
            builder.UseBuffer(passData.compact, AccessFlags.ReadWrite);
            builder.UseBuffer(passData.buckets, AccessFlags.ReadWrite);
            builder.UseBuffer(passData.pixels, AccessFlags.ReadWrite);
            builder.UseBuffer(passData.counts, AccessFlags.ReadWrite);
            builder.UseBuffer(passData.args, AccessFlags.ReadWrite);

            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) => Execute(data, ctx));
        }
    }

    /// <summary>Releases every camera's GI buffers. The owning feature calls this when it is disposed.</summary>
    public void Dispose()
    {
        foreach (CaelixGiResources gi in cameras.Values)
        {
            gi.Dispose();
        }

        cameras.Clear();
    }

    private CaelixGiResources GetResources(Camera camera)
    {
        if (!cameras.TryGetValue(camera, out CaelixGiResources gi))
        {
            gi = new CaelixGiResources();
            cameras.Add(camera, gi);
        }

        return gi;
    }

    /// <summary>
    /// Releases the buffers of cameras that stopped rendering (a closed Scene view, a destroyed
    /// preview camera); at 1440p one camera holds about a gigabyte of samples.
    /// </summary>
    private void PruneUnusedCameras()
    {
        int frame = Time.frameCount;
        pruneScratch.Clear();
        foreach (KeyValuePair<Camera, CaelixGiResources> entry in cameras)
        {
            if (entry.Key == null || frame - entry.Value.LastUsedFrame > UnusedFramesBeforeRelease)
            {
                pruneScratch.Add(entry.Key);
            }
        }

        foreach (Camera camera in pruneScratch)
        {
            cameras[camera].Dispose();
            cameras.Remove(camera);
        }
    }

    /// <summary>All GI kernel indices, or null when any is missing from the compute shader.</summary>
    private static int[] FindKernels(ComputeShader cs)
    {
        if (cs == null)
        {
            return null;
        }

        int[] result = new int[KernelNames.Length];
        for (int i = 0; i < result.Length; i++)
        {
            // HasKernel first: FindKernel logs an error and throws when the kernel is missing.
            if (!cs.HasKernel(KernelNames[i]))
            {
                return null;
            }

            result[i] = cs.FindKernel(KernelNames[i]);
        }

        return result;
    }

    private static void Execute(PassData data, UnsafeGraphContext context)
    {
        if (data.brickPages == null || data.instanceTable == null || data.materialTable == null)
        {
            return;
        }

        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        ComputeShader cs = data.computeShader;
        int[] k = data.kernels;

        CaelixRayQueryDispatch.SetGroupKeyword(cmd, cs, data.groupSize);
        CaelixRayQueryDispatch.BindSceneInputs(
            cmd, cs, k[KernelGenerateSamples], data.voxAS, data.brickPages, data.instanceTable, data.materialTable);
        CaelixRayQueryDispatch.BindSceneInputs(
            cmd, cs, k[KernelSpatial], data.voxAS, data.brickPages, data.instanceTable, data.materialTable);
        // The reprojection reads the instance records too, for the current transform and token.
        cmd.SetComputeBufferParam(cs, k[KernelReprojectLit], "g_Instances", data.instanceTable);
        cmd.SetComputeBufferParam(cs, k[KernelReprojectUnlit], "g_Instances", data.instanceTable);

        Texture sky = CaelixGBufferPass.ResolveSkyTexture();
        for (int i = 0; i < k.Length; i++)
        {
            int kernel = k[i];
            cmd.SetComputeTextureParam(cs, kernel, "g_GiSurface", (RTHandle)data.GiSurface);
            cmd.SetComputeTextureParam(cs, kernel, "g_GiKey", (RTHandle)data.GiKey);
            cmd.SetComputeTextureParam(cs, kernel, "g_GiNormal", (RTHandle)data.Normal);
            cmd.SetComputeTextureParam(cs, kernel, "DiffuseRadianceTarget", (RTHandle)data.DiffuseRadiance);
            cmd.SetComputeTextureParam(cs, kernel, "g_GiOwnSample", (RTHandle)data.OwnSample);
            cmd.SetComputeTextureParam(cs, kernel, "g_GiMotionVector", (RTHandle)data.MotionVector);
            cmd.SetComputeTextureParam(cs, kernel, "g_GiPrevIndirect", (RTHandle)data.PreviousIndirect);
            cmd.SetComputeTextureParam(cs, kernel, "g_Sky", sky);
            if (data.blueNoiseTexture != null)
            {
                cmd.SetComputeTextureParam(cs, kernel, CaelixShaderIDs.BlueNoiseTexture, data.blueNoiseTexture);
            }

            cmd.SetComputeBufferParam(cs, kernel, "g_GiLitRing", data.litRing);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiUnlitRing", data.unlitRing);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiRefined", data.refined);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiCompact", data.compact);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiBuckets", data.buckets);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiPixels", data.pixels);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiCounts", data.counts);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiArgs", data.args);
        }

        // Camera, the same values the tracer used this frame.
        cmd.SetComputeIntParams(cs, "g_GiFrameSize", data.width, data.height);
        cmd.SetComputeIntParams(cs, "g_LaunchDim", data.width, data.height);
        cmd.SetComputeVectorParam(cs, "g_GiJitter", data.jitter);
        cmd.SetComputeVectorParam(cs, "g_GiProjection", data.projection);
        cmd.SetComputeMatrixParam(cs, "g_GiWorldToCamera", data.worldToCamera);
        cmd.SetComputeMatrixParam(cs, "g_GiCameraToWorld", data.cameraToWorld);
        cmd.SetComputeVectorParam(cs, "g_GiCameraPosition", data.cameraPosition);

        cmd.SetComputeIntParam(cs, "g_FrameIndex", data.frameIndex);
        cmd.SetComputeIntParam(cs, "g_GiRingFrame", unchecked((int)data.ringFrame));
        cmd.SetComputeIntParam(cs, "g_GiLitCapacity", data.litCapacity);
        cmd.SetComputeIntParam(cs, "g_GiUnlitCapacity", data.unlitCapacity);
        cmd.SetComputeIntParam(cs, "g_GiBucketCount", data.bucketCount);
        cmd.SetComputeIntParam(cs, "g_BounceCountOpaque", data.bounceCountOpaque);

        CaelixGiSettings s = data.settings;
        cmd.SetComputeIntParam(cs, "g_GiSampleRateDivisor", s.sampleRateDivisor);
        cmd.SetComputeIntParam(cs, "g_GiUniformSampleRate", s.uniformSampleRate ? 1 : 0);
        cmd.SetComputeIntParam(cs, "g_GiHistoryValid", data.historyValid ? 1 : 0);
        cmd.SetComputeFloatParam(cs, "g_GiSkySampleProbability", s.skySampleProbability);
        cmd.SetComputeFloatParam(cs, "g_GiLitThreshold", s.litThreshold);
        cmd.SetComputeIntParam(cs, "g_GiUnlitKeepDivisor", s.unlitKeepDivisor);
        cmd.SetComputeIntParam(cs, "g_GiMaxHistoryFrames", s.maxHistoryFrames);
        cmd.SetComputeIntParam(cs, "g_GiSpatialIterations", s.spatialIterations);
        cmd.SetComputeFloatParam(cs, "g_GiSpatialRadius", s.spatialRadius);
        cmd.SetComputeIntParam(cs, "g_GiAdaptiveRadius", s.adaptiveRadius ? 1 : 0);
        cmd.SetComputeFloatParam(cs, "g_GiRadiusLitFactor", s.radiusLitFactor);
        cmd.SetComputeIntParam(cs, "g_GiSampleLeveling", s.sampleLeveling ? 1 : 0);
        cmd.SetComputeIntParam(cs, "g_GiMinBucketSamples", s.minBucketSamples);
        cmd.SetComputeFloatParam(cs, "g_GiJacobianMin", s.jacobianMin);
        cmd.SetComputeFloatParam(cs, "g_GiJacobianMax", s.jacobianMax);
        cmd.SetComputeIntParam(cs, "g_GiDebugMode", s.debugMode);
        cmd.SetComputeIntParam(cs, "g_GiOwnSampleFallback", s.ownSampleFallback ? 1 : 0);

        int pixelGroupsX = (data.width + 7) / 8;
        int pixelGroupsY = (data.height + 7) / 8;
        int bucketGroups = (data.bucketCount + 63) / 64;
        GraphicsBuffer args = data.args;

        // One profiler sample per kernel, so the Profiler's GPU module (and Nsight) can attribute
        // the pass's time to a stage. The names are stable; tools key on them.
        cmd.BeginSample("Caelix GI 1 PreparePixels");
        cmd.DispatchCompute(cs, k[KernelPreparePixels], pixelGroupsX, pixelGroupsY, 1);
        cmd.EndSample("Caelix GI 1 PreparePixels");
        cmd.BeginSample("Caelix GI 2 PrepareBuckets");
        cmd.DispatchCompute(cs, k[KernelPrepareBuckets], bucketGroups, 1, 1);
        cmd.EndSample("Caelix GI 2 PrepareBuckets");
        cmd.BeginSample("Caelix GI 3 GenerateSamples");
        cmd.DispatchCompute(cs, k[KernelGenerateSamples], pixelGroupsX, pixelGroupsY, 1);
        cmd.EndSample("Caelix GI 3 GenerateSamples");
        cmd.BeginSample("Caelix GI 4 PrepareHistory");
        cmd.DispatchCompute(cs, k[KernelPrepareHistory], 1, 1, 1);
        cmd.EndSample("Caelix GI 4 PrepareHistory");
        cmd.BeginSample("Caelix GI 5 ReprojectLit");
        cmd.DispatchCompute(cs, k[KernelReprojectLit], args, 0u);
        cmd.EndSample("Caelix GI 5 ReprojectLit");
        cmd.BeginSample("Caelix GI 6 ReprojectUnlit");
        cmd.DispatchCompute(cs, k[KernelReprojectUnlit], args, UnlitArgsOffset);
        cmd.EndSample("Caelix GI 6 ReprojectUnlit");
        cmd.BeginSample("Caelix GI 7 RefineBuckets");
        cmd.DispatchCompute(cs, k[KernelRefineBuckets], bucketGroups, 1, 1);
        cmd.EndSample("Caelix GI 7 RefineBuckets");
        cmd.BeginSample("Caelix GI 8 Spatial");
        cmd.DispatchCompute(cs, k[KernelSpatial], pixelGroupsX, pixelGroupsY, 1);
        cmd.EndSample("Caelix GI 8 Spatial");
    }
}
