using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Caelix.Rendering.RayQuery;

/// <summary>
/// URP renderer feature for Caelix budget mode: a fixed, small ray budget per pixel instead of a
/// path trace.
/// </summary>
/// <remarks>
/// The sibling of <see cref="CaelixRendererFeature"/>. Enable exactly one of the two on a
/// renderer asset — they both drive <see cref="CaelixCameraHistory"/> and both write
/// <see cref="CaelixFrameResources"/>, so running them together would have each overwrite the
/// other's frame.
/// <para>
/// What budget mode renders, per pixel:
/// primary ray, one optional delta step (mirror reflection / glass refraction) with everything
/// transparent behind it passed straight through, one hard sun shadow ray, and N ambient occlusion
/// rays capped at K_MAX world units. Nothing is shaded during the trace; the G-buffer is filled and
/// the lighting is evaluated once, deferred, after the AO/shadow signal has been denoised.
/// </para>
/// <para>
/// The stages hand off through <see cref="CaelixFrameResources"/>:
/// <list type="number">
/// <item><see cref="CaelixBudgetGBufferPass"/> — the trace producing the G-buffer and raw AO/shadow;</item>
/// <item><see cref="CaelixBudgetDenoisePass"/> — spatial filter, temporal accumulation, deferred shade;</item>
/// <item><see cref="CaelixPresentPass"/> — reused unchanged: debug view selection and copy to the camera target.</item>
/// </list>
/// All three are enqueued at the same <see cref="RenderPassEvent"/>; URP sorts the queue stably, so
/// enqueue order here is the execution order.
/// </para>
/// <para>
/// Serialized field names are load-bearing — they are what renderer assets store. Renaming one drops
/// the authored value back to its default, so use <c>[FormerlySerializedAs]</c> if one must change.
/// </para>
/// </remarks>
public class CaelixBudgetRendererFeature : ScriptableRendererFeature
{
    /// <summary>Budget mode inline ray query kernel (CaelixBudgetRQ.compute).</summary>
    [SerializeField] private ComputeShader rayQueryTracer;

    /// <summary>Denoise + deferred shade shader (CaelixBudgetShade.shader).</summary>
    [SerializeField] private Shader budgetShadeShader;

    /// <summary>Material used to copy Caelix output onto the camera target and write depth.</summary>
    [SerializeField] private Material postProcessMaterialFlip;

    /// <summary>
    /// Length of the temporal accumulation window. Higher converges the AO smoother but ghosts more
    /// on moving objects.
    /// </summary>
    [SerializeField, Min(1)] private int maximumAverageFrames = 120;

    [SerializeField, Tooltip("Optional 128x8192 R8G8 UInt spatiotemporal blue-noise texture bound as stbnTexture.")]
    private Texture2D blueNoiseTexture;

    [Header("Ray Budget")]
    [SerializeField] private CaelixBudgetTraceSettings budgetTrace = CaelixBudgetTraceSettings.Default;

    [Header("Shading")]
    [SerializeField] private CaelixBudgetShadingSettings budgetShading = CaelixBudgetShadingSettings.Default;

    [Header("AO Denoising")]
    [SerializeField, Tooltip("Run the a-trous spatial filter over the AO/shadow signal before temporal accumulation.")]
    private bool enableSpatialFilter = true;
    [SerializeField] private CaelixATrousFilterSettings aTrous = CaelixATrousFilterSettings.Default;

    [Header("Delta Checkerboard")]
    [SerializeField, Tooltip("Average the reflect/refract checkerboard the tracer writes at the first transparent interface back together, as a cross filter on the shaded colour. Turn off to see the raw checkerboard.")]
    private bool resolveDeltaCheckerboard = true;

    [Header("Temporal AO")]
    [SerializeField] private bool enableTemporalAccumulation = true;
    [SerializeField, Range(0.0f, 1.0f)] private float temporalCurrentFrameMinWeight = 0.0f;
    [SerializeField] private bool temporalDepthRejection = true;
    [SerializeField, Min(0.0f)] private float temporalDepthTolerance = 0.1f;
    [SerializeField, Min(0.0f)] private float temporalRelativeDepthTolerance = 0.005f;
    [SerializeField] private bool temporalNormalRejection = true;
    [SerializeField, Range(-1.0f, 1.0f)] private float temporalNormalThreshold = 0.85f;
    [SerializeField] private bool temporalBilinearHistory = true;

    [Header("Debug")]
    [SerializeField, Tooltip("Which Caelix buffer to display. Debug views reuse buffers the frame already produced, so they cost nothing extra to render. The stochastic views have no budget-mode equivalent and fall back to the shaded image.")]
    private CaelixDebugView debugView = CaelixDebugView.Regular;

    private CaelixBudgetGBufferPass gbufferPass;
    private CaelixBudgetDenoisePass denoisePass;
    private CaelixPresentPass presentPass;

    // Materials are owned here rather than by the passes: Create() re-runs on every inspector edit,
    // so per-pass ownership leaked a material set each time a slider moved.
    private Material shadeMaterial;
    private Material[] aTrousMaterials;
    private Material flipMaterial;

    /// <summary>Cached scene renderer. Resolved lazily because the feature can be created before the scene loads.</summary>
    private CaelixRayQueryRenderer rayQueryRenderer;

    public override void Create()
    {
        DestroyMaterials();
        CreateMaterials();

        gbufferPass = new CaelixBudgetGBufferPass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques
        };
        denoisePass = new CaelixBudgetDenoisePass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques
        };
        presentPass = new CaelixPresentPass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques
        };

        denoisePass.Setup(shadeMaterial, aTrousMaterials);
        presentPass.Setup(flipMaterial);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (gbufferPass == null || !TryResolveRayQueryRenderer())
        {
            return;
        }

        gbufferPass.ConfigureSettings(rayQueryRenderer, rayQueryTracer, blueNoiseTexture, BuildTraceSettings());
        denoisePass.ConfigureSettings(
            enableSpatialFilter, aTrous, BuildTemporalSettings(), budgetShading, resolveDeltaCheckerboard);
        presentPass.ConfigureSettings(debugView);

        if (!gbufferPass.IsReady)
        {
            return;
        }

        // Same RenderPassEvent for all three; URP's queue sort is stable, so this is the run order.
        renderer.EnqueuePass(gbufferPass);
        renderer.EnqueuePass(denoisePass);
        renderer.EnqueuePass(presentPass);
    }

    protected override void Dispose(bool disposing)
    {
        CaelixCameraHistory.ReleaseAll();
        DestroyMaterials();

        gbufferPass = null;
        denoisePass = null;
        presentPass = null;
    }

    private CaelixBudgetTraceSettings BuildTraceSettings()
    {
        CaelixBudgetTraceSettings settings = budgetTrace;
        settings.maximumAverageFrames = maximumAverageFrames;
        return settings;
    }

    private CaelixTemporalRadianceSettings BuildTemporalSettings()
    {
        return new CaelixTemporalRadianceSettings
        {
            enabled = enableTemporalAccumulation,
            currentFrameMinWeight = temporalCurrentFrameMinWeight,
            depthRejection = temporalDepthRejection,
            depthTolerance = temporalDepthTolerance,
            relativeDepthTolerance = temporalRelativeDepthTolerance,
            normalRejection = temporalNormalRejection,
            normalThreshold = temporalNormalThreshold,
            bilinearHistory = temporalBilinearHistory,
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
        if (budgetShadeShader != null)
        {
            shadeMaterial = CoreUtils.CreateEngineMaterial(budgetShadeShader);

            // One instance per a-trous iteration: Blitter resolves material properties at submit
            // time, so iterations sharing a material would all run the last one's step width.
            aTrousMaterials = new Material[CaelixATrousFilterSettings.MaxIterations];
            for (int i = 0; i < aTrousMaterials.Length; i++)
            {
                aTrousMaterials[i] = CoreUtils.CreateEngineMaterial(budgetShadeShader);
            }
        }

        if (postProcessMaterialFlip != null)
        {
            // Instance rather than the asset: the present stage writes _DebugView every frame, which
            // would otherwise dirty the material asset on disk in the editor.
            flipMaterial = new Material(postProcessMaterialFlip)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }
    }

    private void DestroyMaterials()
    {
        CoreUtils.Destroy(shadeMaterial);
        shadeMaterial = null;

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
    }
}
