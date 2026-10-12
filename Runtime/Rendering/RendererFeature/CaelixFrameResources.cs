using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The per-frame handoff between the three Caelix render stages.
/// </summary>
/// <remarks>
/// Stored in the frame's <see cref="ContextContainer"/> so the stages stay decoupled: the G-buffer
/// stage produces, the denoise stage refines and composites, and the present stage only chooses what
/// to display. Consumers must check <see cref="IsValid"/> before touching any handle — the G-buffer
/// stage bails out (leaving every handle null) when the scene has no renderer or a shader is missing.
/// </remarks>
public class CaelixFrameResources : ContextItem
{
    /// <summary>True once the G-buffer stage has published a complete set of handles for this frame.</summary>
    public bool IsValid;

    // --- Produced by CaelixGBufferPass ---

    /// <summary>All radiance that needs no denoising: primary emission, sky on miss. ARGBFloat.</summary>
    public TextureHandle DeterministicRadiance;
    /// <summary>Stochastic diffuse-path radiance (albedo-demodulated), hit distance in .a. ARGBHalf.</summary>
    public TextureHandle StochasticDiffuse;
    /// <summary>Stochastic specular-path radiance (incl. transparent reflection/refraction), hit distance in .a. ARGBHalf.</summary>
    public TextureHandle StochasticSpecular;
    /// <summary>Surface albedo, used to modulate stochastic radiance at composite time.</summary>
    public TextureHandle Albedo;
    /// <summary>Octahedral-encoded surface normal in .xy ([0,1] range), voxel face hash in .z, linear roughness in .w.</summary>
    public TextureHandle Normal;
    /// <summary>Linear view depth in world units. The present stage converts it to raw depth for SV_Depth.</summary>
    public TextureHandle Depth;
    /// <summary>
    /// 2.5D motion vectors, NRD convention: .xy = previousUV - currentUV, .z = viewZprev - viewZ.
    /// .a is the delta-checkerboard flag (1 = the pixel traced one parity of a reflect/refract
    /// split and needs the cross resolve, 0 = single unsplit surface or sky). ARGBFloat.
    /// </summary>
    public TextureHandle MotionVector;
    /// <summary>This frame's linear depth, persisted for next frame's temporal rejection.</summary>
    public TextureHandle CurrentDepthHistory;
    /// <summary>This frame's normal, persisted for next frame's temporal rejection.</summary>
    public TextureHandle CurrentNormalHistory;

    // --- Produced by CaelixGBufferPass in GI mode only ---

    /// <summary>
    /// Real world position of the surface the primary delta chain ended on in .xyz, virtual
    /// distance along the camera ray in .w; (0, 0, 0, K_T_MAX) on a miss. ARGBFloat.
    /// </summary>
    public TextureHandle GiSurface;
    /// <summary>
    /// .x = path hash16 | instance slot &lt;&lt; 16, .y = instance lifetime token, .z = flags (bit 0 hit,
    /// bit 1 wants a diffuse GI estimate, bit 2 opaque), .w = block ID; 0 on a miss. R32G32B32A32_UInt.
    /// </summary>
    public TextureHandle GiKey;

    // --- Produced by CaelixBudgetGBufferPass (budget mode only) ---

    /// <summary>
    /// Octahedral direction from the shaded surface towards the viewer in .xy, metallic in .z,
    /// surface-valid flag in .w. The view direction cannot be re-derived from the pixel under
    /// primary surface replacement, so the tracer has to publish it.
    /// </summary>
    public TextureHandle Surface;
    /// <summary>Raw AO and sun visibility straight out of the tracer: .r = AO, .g = shadow, .a = validity.</summary>
    public TextureHandle AOShadowRaw;

    // --- Produced by CaelixBudgetDenoisePass (budget mode only) ---

    /// <summary>AO/shadow after the spatial filter.</summary>
    public TextureHandle AOShadowFiltered;
    /// <summary>AO/shadow after temporal accumulation, as consumed by the deferred shade.</summary>
    public TextureHandle AOShadowAccumulated;

    // --- Produced by CaelixDenoisePass ---

    /// <summary>Combined diffuse+specular stochastic radiance — the legacy denoise chain's input signal.</summary>
    public TextureHandle RawIndirectRadiance;
    /// <summary>Indirect radiance after the spatial filter. Aliases <see cref="RawIndirectRadiance"/> when the filter is disabled.</summary>
    public TextureHandle FilteredIndirectRadiance;
    /// <summary>Indirect radiance after temporal accumulation.</summary>
    public TextureHandle AccumulatedIndirectRadiance;
    /// <summary>Final composited color, what the present stage shows in <see cref="CaelixDebugView.Regular"/>.</summary>
    public TextureHandle Color;

    /// <summary>
    /// Temporal state for the camera being rendered. Resolved once by the G-buffer stage so that
    /// stages cannot disagree about which history buffer is "previous" this frame.
    /// </summary>
    internal CaelixCameraHistory History;

    public override void Reset()
    {
        IsValid = false;

        DeterministicRadiance = TextureHandle.nullHandle;
        StochasticDiffuse = TextureHandle.nullHandle;
        StochasticSpecular = TextureHandle.nullHandle;
        Albedo = TextureHandle.nullHandle;
        Normal = TextureHandle.nullHandle;
        Depth = TextureHandle.nullHandle;
        MotionVector = TextureHandle.nullHandle;
        CurrentDepthHistory = TextureHandle.nullHandle;
        CurrentNormalHistory = TextureHandle.nullHandle;
        GiSurface = TextureHandle.nullHandle;
        GiKey = TextureHandle.nullHandle;

        Surface = TextureHandle.nullHandle;
        AOShadowRaw = TextureHandle.nullHandle;
        AOShadowFiltered = TextureHandle.nullHandle;
        AOShadowAccumulated = TextureHandle.nullHandle;

        RawIndirectRadiance = TextureHandle.nullHandle;
        FilteredIndirectRadiance = TextureHandle.nullHandle;
        AccumulatedIndirectRadiance = TextureHandle.nullHandle;
        Color = TextureHandle.nullHandle;

        History = null;
    }

    /// <summary>
    /// Resolves the debug view to the texture that should be blitted to the camera.
    /// Falls back to <see cref="Color"/> for any view whose source is unavailable.
    /// </summary>
    public TextureHandle SelectDebugSource(CaelixDebugView view)
    {
        switch (view)
        {
            case CaelixDebugView.MotionVector: return MotionVector;
            case CaelixDebugView.Albedo: return Albedo;
            case CaelixDebugView.Normal: return Normal;
            case CaelixDebugView.Depth: return Depth;
            case CaelixDebugView.DeterministicRadiance: return DeterministicRadiance;
            case CaelixDebugView.IndirectRadianceRaw: return RawIndirectRadiance;
            case CaelixDebugView.IndirectRadianceFiltered: return FilteredIndirectRadiance;
            case CaelixDebugView.IndirectRadianceAccumulated: return AccumulatedIndirectRadiance;
            case CaelixDebugView.StochasticDiffuse: return StochasticDiffuse;
            case CaelixDebugView.StochasticSpecular: return StochasticSpecular;
            case CaelixDebugView.BudgetAOShadowRaw: return AOShadowRaw;
            case CaelixDebugView.BudgetAOShadowFiltered: return AOShadowFiltered;
            case CaelixDebugView.BudgetAOShadowAccumulated: return AOShadowAccumulated;
            case CaelixDebugView.BudgetSurface: return Surface;
            case CaelixDebugView.Regular:
            default: return Color;
        }
    }

    // --- Shared descriptors -------------------------------------------------
    // All Caelix targets are screen-sized and share one base shape, so the stages agree on
    // format/size without having to pass descriptors between each other.

    /// <summary>Base descriptor every Caelix target derives from: screen-sized, UAV-capable, no depth.</summary>
    public static RenderTextureDescriptor BaseDescriptor(UniversalCameraData cameraData)
    {
        RenderTextureDescriptor desc = new RenderTextureDescriptor(
            cameraData.cameraTargetDescriptor.width,
            cameraData.cameraTargetDescriptor.height,
            RenderTextureFormat.ARGBFloat);

        // The ray tracing stage writes these as UAVs; harmless for the raster stages.
        desc.enableRandomWrite = true;
        return desc;
    }

    /// <summary>Descriptor for a target of the given format, matching the base shape.</summary>
    public static RenderTextureDescriptor DescriptorWithFormat(UniversalCameraData cameraData, RenderTextureFormat format)
    {
        RenderTextureDescriptor desc = BaseDescriptor(cameraData);
        desc.colorFormat = format;
        return desc;
    }
}
