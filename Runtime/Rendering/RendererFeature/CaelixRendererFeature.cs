using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Caelix.Rendering.RayQuery;

/// <summary>
/// URP renderer feature that injects ray-traced voxel rendering into the pipeline.
/// </summary>
/// <remarks>
/// This type is only the settings surface and the wiring; the work is split across three stages that
/// hand off through <see cref="CaelixFrameResources"/> in the frame's context container:
/// <list type="number">
/// <item><see cref="CaelixGBufferPass"/> — the voxel trace producing the Caelix G-buffer, an
/// inline ray query compute kernel dispatched against the scene's acceleration structure;</item>
/// <item><see cref="CaelixDenoisePass"/> — spatial filter, temporal accumulation, composite;</item>
/// <item><see cref="CaelixPresentPass"/> — debug view selection and copy to the camera target.</item>
/// </list>
/// All three are enqueued at the same <see cref="RenderPassEvent"/>; URP sorts the queue stably, so
/// enqueue order here is the execution order.
/// <para>
/// Serialized field names are load-bearing — they are what renderer assets store. Renaming one drops
/// the authored value back to its default, so use <c>[FormerlySerializedAs]</c> if one must change.
/// </para>
/// </remarks>
public class CaelixRendererFeature : ScriptableRendererFeature
{
    /// <summary>Inline ray query path tracer (CaelixPathTraceRQ.compute).</summary>
    [SerializeField] private ComputeShader rayQueryTracer;

    [SerializeField] private Shader indirectPipelineShader, indirectATrousShader;

    /// <summary>Material used to copy Caelix output onto the camera target and write depth.</summary>
    [SerializeField] private Material postProcessMaterialFlip;

    /// <summary>
    /// Length of the temporal accumulation window. Higher converges smoother but ghosts more on
    /// moving objects.
    /// </summary>
    [SerializeField, Min(1)] private int maximumAverageFrames = 120;

    [Header("Ray Tracing")]
    [SerializeField, Tooltip("Build the Caelix ray tracing acceleration structure inside the render pass each frame. Disable only if the structure is built elsewhere before tracing.")]
    private bool buildAccelerationStructure = true;
    [SerializeField, Min(0)] private int bounceCountOpaque = 4;
    [SerializeField, Min(0)] private int bounceCountTransparent = 5;
    [SerializeField, Min(1)] private int samplesPerPixel = 1;
    [SerializeField, Tooltip("Optional 128x8192 R8G8 UInt spatiotemporal blue-noise texture bound as stbnTexture.")]
    private Texture2D blueNoiseTexture;

    [Header("Sky Sun")]
    [SerializeField] private bool enableSkySun = true;
    [SerializeField, Min(0.0f)] private float sunDiskRadiusRadians = 0.004363323f;
    [SerializeField, Min(0.0f)] private float sunFlareRadiusRadians = 0.03490659f;

    [Header("Indirect Denoising")]
    [SerializeField] private CaelixIndirectDenoisingSettings indirectDenoising = CaelixIndirectDenoisingSettings.Default;

    [Header("Delta Checkerboard")]
    [SerializeField, Tooltip("Average the reflect/refract checkerboard the tracer writes at the first transparent interface back together, as a cross filter on the composited colour. Turn off to see the raw checkerboard.")]
    private bool resolveDeltaCheckerboard = true;

    [Header("Colour Resolve")]
    [SerializeField] private CaelixColorResolveSettings colorResolve = CaelixColorResolveSettings.Default;

    [Header("Temporal Radiance")]
    [SerializeField] private bool enableTemporalRadiance = true;
    [SerializeField, Range(0.0f, 1.0f)] private float temporalRadianceCurrentFrameMinWeight = 0.0f;
    [SerializeField] private bool temporalRadianceDepthRejection = true;
    [SerializeField, Min(0.0f)] private float temporalRadianceDepthTolerance = 0.1f;
    [SerializeField, Min(0.0f)] private float temporalRadianceRelativeDepthTolerance = 0.005f;
    [SerializeField] private bool temporalRadianceNormalRejection = true;
    [SerializeField, Range(-1.0f, 1.0f)] private float temporalRadianceNormalThreshold = 0.85f;
    [SerializeField] private bool temporalRadianceBilinearHistory = true;

    [Header("Debug")]
    [SerializeField, Tooltip("Which Caelix buffer to display. Debug views reuse buffers the frame already produced, so they cost nothing extra to render.")]
    private CaelixDebugView debugView = CaelixDebugView.Regular;

    private CaelixGBufferPass gbufferPass;
    private CaelixDenoisePass denoisePass;
    private CaelixPresentPass presentPass;
    private CaelixPresentPass presentEdgesPass;

    // Materials are owned here rather than by the passes: Create() re-runs on every inspector edit,
    // so per-pass ownership leaked a material set each time a slider moved.
    private Material indirectMaterial;
    private Material[] aTrousMaterials;
    private Material flipMaterial;
    /// <summary>
    /// The edge stage's own flip material. Blitter resolves material properties when the command
    /// buffer is submitted rather than when the blit is recorded, so the two present stages sharing
    /// one instance would both run with whichever _CaelixPresentStage was written last.
    /// </summary>
    private Material flipEdgesMaterial;

    /// <summary>Cached scene renderer. Resolved lazily because the feature can be created before the scene loads.</summary>
    private CaelixRayQueryRenderer rayQueryRenderer;

    public override void Create()
    {
        DestroyMaterials();
        CreateMaterials();

        gbufferPass = new CaelixGBufferPass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques
        };
        denoisePass = new CaelixDenoisePass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques
        };
        presentPass = new CaelixPresentPass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques,
            Stage = 0
        };
        // The silhouette pixels the colour resolve leaves at partial coverage have to be blended
        // against the URP skybox, which is drawn after AfterRenderingOpaques — so they are presented
        // in a second stage, once the skybox is on screen.
        presentEdgesPass = new CaelixPresentPass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingSkybox + 1,
            Stage = 1
        };

        denoisePass.Setup(indirectMaterial, aTrousMaterials);
        presentPass.Setup(flipMaterial);
        presentEdgesPass.Setup(flipEdgesMaterial);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (gbufferPass == null)
        {
            return;
        }

        if (!TryResolveRayQueryRenderer())
        {
            return;
        }

        gbufferPass.ConfigureSettings(rayQueryRenderer, rayQueryTracer, blueNoiseTexture, BuildTraceSettings());

        denoisePass.ConfigureSettings(
            indirectDenoising, BuildTemporalSettings(), colorResolve, resolveDeltaCheckerboard);
        presentPass.ConfigureSettings(debugView);
        presentEdgesPass.ConfigureSettings(debugView);

        if (!gbufferPass.IsReady)
        {
            return;
        }

        // Same RenderPassEvent for the first three; URP's queue sort is stable, so this is the run
        // order. The edge present sits at a later event of its own, past the skybox.
        renderer.EnqueuePass(gbufferPass);
        renderer.EnqueuePass(denoisePass);
        renderer.EnqueuePass(presentPass);
        renderer.EnqueuePass(presentEdgesPass);
    }

    protected override void Dispose(bool disposing)
    {
        CaelixCameraHistory.ReleaseAll();
        DestroyMaterials();

        gbufferPass = null;
        denoisePass = null;
        presentPass = null;
        presentEdgesPass = null;
    }

    private CaelixTraceSettings BuildTraceSettings()
    {
        return new CaelixTraceSettings
        {
            buildAccelerationStructure = buildAccelerationStructure,
            bounceCountOpaque = bounceCountOpaque,
            bounceCountTransparent = bounceCountTransparent,
            samplesPerPixel = samplesPerPixel,
            enableSkySun = enableSkySun,
            sunDiskRadiusRadians = sunDiskRadiusRadians,
            sunFlareRadiusRadians = sunFlareRadiusRadians,
            maximumAverageFrames = maximumAverageFrames
        };
    }

    private CaelixTemporalRadianceSettings BuildTemporalSettings()
    {
        return new CaelixTemporalRadianceSettings
        {
            enabled = enableTemporalRadiance,
            currentFrameMinWeight = temporalRadianceCurrentFrameMinWeight,
            depthRejection = temporalRadianceDepthRejection,
            depthTolerance = temporalRadianceDepthTolerance,
            relativeDepthTolerance = temporalRadianceRelativeDepthTolerance,
            normalRejection = temporalRadianceNormalRejection,
            normalThreshold = temporalRadianceNormalThreshold,
            bilinearHistory = temporalRadianceBilinearHistory,
            maximumAverageFrames = maximumAverageFrames
        };
    }

    private bool TryResolveRayQueryRenderer()
    {
        // Deliberately not CaelixRayQueryRenderer.instance: MonoSingleton spawns a temporary
        // GameObject when none exists, which would litter the scene from a renderer feature.
        if (rayQueryRenderer == null)
        {
            rayQueryRenderer = FindFirstObjectByType<CaelixRayQueryRenderer>();
        }

        return rayQueryRenderer != null;
    }

    private void CreateMaterials()
    {
        if (indirectPipelineShader != null)
        {
            indirectMaterial = CoreUtils.CreateEngineMaterial(indirectPipelineShader);
        }

        if (indirectATrousShader != null)
        {
            aTrousMaterials = new Material[CaelixATrousFilterSettings.MaxIterations];
            for (int i = 0; i < aTrousMaterials.Length; i++)
            {
                aTrousMaterials[i] = CoreUtils.CreateEngineMaterial(indirectATrousShader);
            }
        }

        if (postProcessMaterialFlip != null)
        {
            // Instance rather than the asset: the present stage writes _DebugView every frame, which
            // would otherwise dirty the material asset on disk in the editor. One instance per
            // stage, so the two cannot overwrite each other's _CaelixPresentStage before submit.
            flipMaterial = new Material(postProcessMaterialFlip)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            flipEdgesMaterial = new Material(postProcessMaterialFlip)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }
    }

    private void DestroyMaterials()
    {
        CoreUtils.Destroy(indirectMaterial);
        indirectMaterial = null;

        if (aTrousMaterials != null)
        {
            for (int i = 0; i < aTrousMaterials.Length; i++)
            {
                CoreUtils.Destroy(aTrousMaterials[i]);
            }

            aTrousMaterials = null;
        }

        CoreUtils.Destroy(flipMaterial);
        flipMaterial = null;

        CoreUtils.Destroy(flipEdgesMaterial);
        flipEdgesMaterial = null;
    }
}
