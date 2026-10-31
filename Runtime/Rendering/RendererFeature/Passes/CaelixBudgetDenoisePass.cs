using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Budget mode, stage 2 of 3: denoises the AO/shadow signal, then shades the G-buffer once.
/// </summary>
/// <remarks>
/// Four sub-steps, recorded in order:
/// <list type="number">
/// <item>spatial filter — the shared a-trous kernel, run over AO/shadow instead of radiance;</item>
/// <item>temporal accumulation — the shared kernel, against the same reprojected history;</item>
/// <item>deferred shade — sun (lambert + GGX, hard shadowed) plus sky ambient scaled by AO, once
/// per pixel, into the final colour target;</item>
/// <item>cross resolve — averages the delta checkerboard back together.</item>
/// </list>
/// Steps 1, 2 and 4 are the *same HLSL* the path-traced chain runs (Shaders/Denoise/*.hlsl); only
/// the signal pointed at them differs. Step 3 is the one genuinely new piece of shading in budget
/// mode. Reads only <see cref="CaelixFrameResources"/>; it has no knowledge of the tracer.
/// </remarks>
public class CaelixBudgetDenoisePass : ScriptableRenderPass
{
    private const int PassATrousFilter = 0;
    private const int PassTemporalAccumulation = 1;
    private const int PassDeferredShade = 2;
    private const int PassCrossResolve = 3;

    private Material shadeMaterial;
    /// <summary>
    /// One material per a-trous iteration. Blitter resolves material properties when the command
    /// buffer is submitted, not when the blit is recorded, so every iteration needs its own
    /// instance — sharing one would silently give all iterations the last iteration's step width.
    /// </summary>
    private Material[] aTrousMaterials;

    private CaelixATrousFilterSettings aTrousSettings = CaelixATrousFilterSettings.Default;
    private bool spatialFilterEnabled = true;
    private CaelixTemporalRadianceSettings temporalSettings = new CaelixTemporalRadianceSettings().Validated();
    private CaelixBudgetShadingSettings shadingSettings = CaelixBudgetShadingSettings.Default.Validated();
    private bool resolveDeltaCheckerboard = true;

    private static Cubemap s_FallbackSky;
    private static bool s_WarnedMissingSky;

    internal class ATrousFilterPassData
    {
        internal CaelixDenoiseUniforms.Snapshot uniforms;
        internal int stepWidth;
        internal int frameIndex;
        internal CaelixATrousFilterSettings settings;
        internal TextureHandle Source;
        internal Material material;
    }

    internal class TemporalAccumulationPassData
    {
        internal CaelixDenoiseUniforms.Snapshot uniforms;
        internal bool historyValid;
        internal CaelixTemporalRadianceSettings settings;
        internal TextureHandle FilteredAOShadow;
        internal TextureHandle PreviousAOShadowHistory;
        internal TextureHandle PreviousDepthHistory;
        internal TextureHandle PreviousNormalHistory;
        internal Material material;
    }

    internal class DeferredShadePassData
    {
        internal CaelixDenoiseUniforms.Snapshot uniforms;
        internal CaelixBudgetShadingSettings settings;
        internal Vector4 mainLightColor;
        internal Vector3 mainLightDirection;
        internal TextureHandle Albedo;
        internal Material material;
    }

    internal class CrossResolvePassData
    {
        internal CaelixDenoiseUniforms.Snapshot uniforms;
        internal TextureHandle Source;
        internal Material material;
    }

    /// <summary>Binds the materials owned by the renderer feature. Called when the feature is created.</summary>
    public void Setup(Material shade, Material[] aTrousIterationMaterials)
    {
        shadeMaterial = shade;
        aTrousMaterials = aTrousIterationMaterials;
    }

    /// <summary>Pushes this frame's denoising and shading settings. Called once per camera before enqueueing.</summary>
    public void ConfigureSettings(
        bool spatialEnabled,
        CaelixATrousFilterSettings aTrous,
        CaelixTemporalRadianceSettings temporal,
        CaelixBudgetShadingSettings shading,
        bool resolveCheckerboard)
    {
        spatialFilterEnabled = spatialEnabled;
        aTrousSettings = aTrous.Validated();
        temporalSettings = temporal.Validated();
        shadingSettings = shading.Validated();
        resolveDeltaCheckerboard = resolveCheckerboard;
    }

    /// <summary>True when the stage has everything it needs to record.</summary>
    public bool IsReady => shadeMaterial != null;

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        CaelixFrameResources resources = frameData.GetOrCreate<CaelixFrameResources>();
        if (!resources.IsValid || resources.History == null || !IsReady || !resources.AOShadowRaw.IsValid())
        {
            return;
        }

        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        CaelixCameraHistory history = resources.History;
        int width = cameraData.scaledWidth;
        int height = cameraData.scaledHeight;
        CaelixDenoiseUniforms.Snapshot uniforms = CaelixDenoiseUniforms.Capture(history, width, height);

        resources.AOShadowFiltered = RecordSpatialFilter(renderGraph, resources, cameraData, uniforms);
        resources.AOShadowAccumulated = RecordTemporalAccumulation(renderGraph, resources, history, uniforms);

        // The double buffer flips once the write into "current" has been recorded; everything above
        // has already captured the handles it needs, and nothing downstream reads history this frame.
        history.EndFrame();

        resources.Color = RecordDeferredShade(renderGraph, resources, frameData, cameraData, uniforms);

        if (resolveDeltaCheckerboard)
        {
            resources.Color = RecordCrossResolve(renderGraph, resources, cameraData, uniforms);
        }
    }

    // --- Spatial ------------------------------------------------------------

    private TextureHandle RecordSpatialFilter(
        RenderGraph renderGraph,
        CaelixFrameResources resources,
        UniversalCameraData cameraData,
        in CaelixDenoiseUniforms.Snapshot uniforms)
    {
        if (!spatialFilterEnabled || aTrousMaterials == null || aTrousMaterials.Length == 0)
        {
            return resources.AOShadowRaw;
        }

        RenderTextureDescriptor descriptor =
            CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf);
        TextureHandle scratch = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, descriptor, "Caelix_outBudgetAOShadowSpatialTemp", false);
        TextureHandle filtered = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, descriptor, "Caelix_outBudgetAOShadowFiltered", false);

        int iterations = Mathf.Min(aTrousSettings.iterations, aTrousMaterials.Length);
        TextureHandle source = resources.AOShadowRaw;
        TextureHandle destination = source;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            destination = (iteration & 1) == 0 ? scratch : filtered;
            string passName = $"Caelix Budget AO A-Trous Filter {iteration + 1}";

            using (var builder = renderGraph.AddRasterRenderPass<ATrousFilterPassData>(passName, out var passData))
            {
                passData.uniforms = uniforms;
                passData.stepWidth = 1 << iteration;
                passData.frameIndex = Time.frameCount;
                passData.settings = aTrousSettings;
                passData.Source = source;
                passData.material = aTrousMaterials[iteration];

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(resources.Normal, AccessFlags.Read);
                builder.UseTexture(resources.CurrentDepthHistory, AccessFlags.Read);
                builder.UseGlobalTexture(CaelixShaderIDs.BudgetAOShadowTex);
                builder.UseGlobalTexture(CaelixShaderIDs.NormalTex);
                builder.UseGlobalTexture(CaelixShaderIDs.CurrentDepthHistoryTex);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(destination, CaelixShaderIDs.BudgetAOShadowTex);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((ATrousFilterPassData data, RasterGraphContext ctx) =>
                {
                    CaelixDenoiseUniforms.Apply(data.material, data.uniforms);
                    data.material.SetInt(CaelixShaderIDs.ATrousStepWidth, data.stepWidth);
                    data.material.SetInt(CaelixShaderIDs.ATrousUseFaceHash, data.settings.useFaceHash ? 1 : 0);
                    data.material.SetInt(CaelixShaderIDs.ATrousJitterTaps, data.settings.jitterTaps ? 1 : 0);
                    data.material.SetInt(CaelixShaderIDs.ATrousFrameIndex, data.frameIndex);
                    data.material.SetFloat(CaelixShaderIDs.ATrousNormalPower, data.settings.normalPower);
                    data.material.SetFloat(CaelixShaderIDs.ATrousDepthTolerance, data.settings.depthTolerance);
                    data.material.SetFloat(CaelixShaderIDs.ATrousRelativeDepthTolerance, data.settings.relativeDepthTolerance);
                    data.material.SetFloat(CaelixShaderIDs.ATrousRadianceSigma, data.settings.radianceSigma);

                    Blitter.BlitTexture(ctx.cmd, data.Source, FullScreenScaleBias, data.material, PassATrousFilter);
                });
            }

            source = destination;
        }

        return destination;
    }

    // --- Temporal -----------------------------------------------------------

    private TextureHandle RecordTemporalAccumulation(
        RenderGraph renderGraph,
        CaelixFrameResources resources,
        CaelixCameraHistory history,
        in CaelixDenoiseUniforms.Snapshot uniforms)
    {
        // The history double buffer is signal-agnostic ARGBHalf, so budget mode stores AO/shadow
        // in the same targets the path tracer stores radiance in. Only one feature can be enabled
        // at a time, so they never contend for it.
        TextureHandle accumulated = renderGraph.ImportTexture(history.CurrentIndirectRadiance);
        TextureHandle previousAOShadow = renderGraph.ImportTexture(history.PreviousIndirectRadiance);
        TextureHandle previousDepth = renderGraph.ImportTexture(history.PreviousDepth);
        TextureHandle previousNormal = renderGraph.ImportTexture(history.PreviousNormal);

        using (var builder = renderGraph.AddRasterRenderPass<TemporalAccumulationPassData>(
                   "Caelix Budget AO Temporal Accumulation", out var passData))
        {
            passData.uniforms = uniforms;
            passData.historyValid = history.IsValid;
            passData.settings = temporalSettings;
            passData.FilteredAOShadow = resources.AOShadowFiltered;
            passData.PreviousAOShadowHistory = previousAOShadow;
            passData.PreviousDepthHistory = previousDepth;
            passData.PreviousNormalHistory = previousNormal;
            passData.material = shadeMaterial;

            builder.UseTexture(passData.FilteredAOShadow, AccessFlags.Read);
            builder.UseTexture(resources.MotionVector, AccessFlags.Read);
            builder.UseTexture(passData.PreviousAOShadowHistory, AccessFlags.Read);
            builder.UseTexture(passData.PreviousDepthHistory, AccessFlags.Read);
            builder.UseTexture(passData.PreviousNormalHistory, AccessFlags.Read);
            builder.UseTexture(resources.CurrentNormalHistory, AccessFlags.Read);
            builder.UseGlobalTexture(CaelixShaderIDs.BudgetAOShadowTex);
            builder.UseGlobalTexture(CaelixShaderIDs.MotionVectorTex);
            builder.UseGlobalTexture(CaelixShaderIDs.CurrentNormalHistoryTex);
            builder.SetRenderAttachment(accumulated, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(accumulated, CaelixShaderIDs.BudgetAOShadowAccumulatedTex);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((TemporalAccumulationPassData data, RasterGraphContext ctx) =>
            {
                Material material = data.material;
                CaelixDenoiseUniforms.Apply(material, data.uniforms);
                material.SetInt(CaelixShaderIDs.IndirectRadianceHistoryValid, data.historyValid ? 1 : 0);
                material.SetInt(CaelixShaderIDs.TemporalRadianceEnabled, data.settings.enabled ? 1 : 0);
                material.SetInt(CaelixShaderIDs.TemporalRadianceBilinearHistory, data.settings.bilinearHistory ? 1 : 0);
                material.SetInt(CaelixShaderIDs.TemporalRadianceDepthRejectionEnabled, data.settings.depthRejection ? 1 : 0);
                material.SetInt(CaelixShaderIDs.TemporalRadianceNormalRejectionEnabled, data.settings.normalRejection ? 1 : 0);
                material.SetFloat(CaelixShaderIDs.TemporalRadianceCurrentFrameMinWeight, data.settings.currentFrameMinWeight);
                material.SetFloat(CaelixShaderIDs.TemporalRadianceDepthTolerance, data.settings.depthTolerance);
                material.SetFloat(CaelixShaderIDs.TemporalRadianceRelativeDepthTolerance, data.settings.relativeDepthTolerance);
                material.SetFloat(CaelixShaderIDs.TemporalRadianceNormalThreshold, data.settings.normalThreshold);
                material.SetFloat(CaelixShaderIDs.TemporalRadianceMaxFrames, data.settings.maximumAverageFrames);
                material.SetTexture(CaelixShaderIDs.PreviousIndirectRadianceHistoryTex, data.PreviousAOShadowHistory);
                material.SetTexture(CaelixShaderIDs.PreviousDepthHistoryTex, data.PreviousDepthHistory);
                material.SetTexture(CaelixShaderIDs.PreviousNormalHistoryTex, data.PreviousNormalHistory);

                Blitter.BlitTexture(ctx.cmd, data.FilteredAOShadow, FullScreenScaleBias, material, PassTemporalAccumulation);
            });
        }

        return accumulated;
    }

    // --- Deferred shade -----------------------------------------------------

    private TextureHandle RecordDeferredShade(
        RenderGraph renderGraph,
        CaelixFrameResources resources,
        ContextContainer frameData,
        UniversalCameraData cameraData,
        in CaelixDenoiseUniforms.Snapshot uniforms)
    {
        TextureHandle color = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.BaseDescriptor(cameraData), "Caelix_outColor", false);

        UniversalLightData lightData = frameData.Get<UniversalLightData>();

        using (var builder = renderGraph.AddRasterRenderPass<DeferredShadePassData>("Caelix Budget Deferred Shade", out var passData))
        {
            passData.uniforms = uniforms;
            passData.settings = shadingSettings;
            passData.mainLightColor = CaelixBudgetLighting.ResolveMainLightColor(lightData);
            passData.mainLightDirection = CaelixBudgetLighting.ResolveMainLightDirection(lightData);
            passData.Albedo = resources.Albedo;
            passData.material = shadeMaterial;

            builder.UseTexture(resources.DeterministicRadiance, AccessFlags.Read);
            builder.UseTexture(resources.Albedo, AccessFlags.Read);
            builder.UseTexture(resources.Normal, AccessFlags.Read);
            builder.UseTexture(resources.Surface, AccessFlags.Read);
            builder.UseTexture(resources.AOShadowAccumulated, AccessFlags.Read);
            builder.UseGlobalTexture(CaelixShaderIDs.DeterministicRadianceTex);
            builder.UseGlobalTexture(CaelixShaderIDs.AlbedoTex);
            builder.UseGlobalTexture(CaelixShaderIDs.NormalTex);
            builder.UseGlobalTexture(CaelixShaderIDs.BudgetSurfaceTex);
            builder.UseGlobalTexture(CaelixShaderIDs.BudgetAOShadowAccumulatedTex);
            builder.SetRenderAttachment(color, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((DeferredShadePassData data, RasterGraphContext ctx) =>
            {
                Material material = data.material;
                CaelixDenoiseUniforms.Apply(material, data.uniforms);
                material.SetVector(CaelixShaderIDs.BudgetMainLightColor, data.mainLightColor);
                material.SetVector(CaelixShaderIDs.BudgetMainLightDirection, data.mainLightDirection);
                material.SetFloat(CaelixShaderIDs.BudgetSkyIntensity, data.settings.skyIntensity);
                material.SetFloat(CaelixShaderIDs.BudgetSkyDiffuseMip, data.settings.skyDiffuseMip);
                material.SetFloat(CaelixShaderIDs.BudgetSkySpecularMaxMip, data.settings.skySpecularMaxMip);
                material.SetFloat(CaelixShaderIDs.BudgetAOStrength, data.settings.aoStrength);
                material.SetFloat(CaelixShaderIDs.BudgetAOAffectsSpecular, data.settings.aoAffectsSpecular);
                // The sky provider publishes its cubemap as a plain global that render graph cannot
                // track, so it is fetched and bound directly, as the trace stage does.
                material.SetTexture(CaelixShaderIDs.BudgetSkyTex,
                    CaelixBudgetLighting.ResolveSkyTexture(ref s_FallbackSky, ref s_WarnedMissingSky));

                Blitter.BlitTexture(ctx.cmd, data.Albedo, FullScreenScaleBias, material, PassDeferredShade);
            });
        }

        return color;
    }

    // --- Cross resolve ------------------------------------------------------

    private TextureHandle RecordCrossResolve(
        RenderGraph renderGraph,
        CaelixFrameResources resources,
        UniversalCameraData cameraData,
        in CaelixDenoiseUniforms.Snapshot uniforms)
    {
        TextureHandle resolved = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.BaseDescriptor(cameraData), "Caelix_outColorResolved", false);

        using (var builder = renderGraph.AddRasterRenderPass<CrossResolvePassData>(
                   "Caelix Budget Delta Checkerboard Resolve", out var passData))
        {
            passData.uniforms = uniforms;
            passData.Source = resources.Color;
            passData.material = shadeMaterial;

            builder.UseTexture(passData.Source, AccessFlags.Read);
            builder.UseTexture(resources.MotionVector, AccessFlags.Read);
            builder.UseGlobalTexture(CaelixShaderIDs.MotionVectorTex);
            builder.SetRenderAttachment(resolved, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((CrossResolvePassData data, RasterGraphContext ctx) =>
            {
                CaelixDenoiseUniforms.Apply(data.material, data.uniforms);
                Blitter.BlitTexture(ctx.cmd, data.Source, FullScreenScaleBias, data.material, PassCrossResolve);
            });
        }

        return resolved;
    }

    private static readonly Vector4 FullScreenScaleBias = new Vector4(1, 1, 0, 0);
}
