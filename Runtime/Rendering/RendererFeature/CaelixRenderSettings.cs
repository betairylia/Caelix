using System;
using UnityEngine;

/// <summary>
/// What the present stage displays. Values are serialized in renderer assets — keep them stable.
/// </summary>
public enum CaelixDebugView
{
    /// <summary>The composited image.</summary>
    Regular = 0,
    /// <summary>Screen-space motion vectors, encoded to a red/green/blue wheel.</summary>
    MotionVector = 1,
    /// <summary>Surface albedo.</summary>
    Albedo = 2,
    /// <summary>Surface normal, decoded from its octahedral encoding.</summary>
    Normal = 3,
    /// <summary>Depth as greyscale, shown in its raw post-projection form (the buffer itself is linear).</summary>
    Depth = 4,
    /// <summary>Non-denoised radiance only: primary emission plus sky.</summary>
    DeterministicRadiance = 5,
    /// <summary>Combined stochastic radiance (diffuse + specular), before filtering.</summary>
    IndirectRadianceRaw = 6,
    /// <summary>Combined stochastic radiance after the spatial filter, before temporal accumulation.</summary>
    IndirectRadianceFiltered = 7,
    /// <summary>Combined stochastic radiance after temporal accumulation, as fed to the composite.</summary>
    IndirectRadianceAccumulated = 8,
    /// <summary>Stochastic diffuse-path radiance straight out of the tracer (hit distance in alpha).</summary>
    StochasticDiffuse = 9,
    /// <summary>Stochastic specular-path radiance straight out of the tracer (hit distance in alpha).</summary>
    StochasticSpecular = 10,

    // --- Budget mode. Ignored by the path-traced feature, which has no such buffers. ---

    /// <summary>Budget mode's raw AO/shadow signal (red = AO, green = sun visibility).</summary>
    BudgetAOShadowRaw = 11,
    /// <summary>Budget mode's AO/shadow after the spatial filter.</summary>
    BudgetAOShadowFiltered = 12,
    /// <summary>Budget mode's AO/shadow after temporal accumulation, as fed to the deferred shade.</summary>
    BudgetAOShadowAccumulated = 13,
    /// <summary>Budget mode's surface target: packed view direction in red/green, metallic in blue.</summary>
    BudgetSurface = 14
}

/// <summary>
/// Ray tracing parameters for the G-buffer stage. Built from the renderer feature's serialized
/// fields once per camera and handed to the stage as an immutable snapshot.
/// </summary>
[Serializable]
public struct CaelixTraceSettings
{
    /// <summary>Build the acceleration structure inside the pass. Disable if it is built elsewhere.</summary>
    public bool buildAccelerationStructure;
    public int bounceCountOpaque;
    public int bounceCountTransparent;
    public int samplesPerPixel;
    public bool enableSkySun;
    public float sunDiskRadiusRadians;
    public float sunFlareRadiusRadians;
    /// <summary>Upper bound on the convergence counter fed to the tracer.</summary>
    public int maximumAverageFrames;

    /// <summary>Clamps user-authored values into ranges the shaders can handle.</summary>
    public CaelixTraceSettings Validated()
    {
        CaelixTraceSettings v = this;
        v.bounceCountOpaque = Mathf.Max(0, v.bounceCountOpaque);
        v.bounceCountTransparent = Mathf.Max(0, v.bounceCountTransparent);
        v.samplesPerPixel = Mathf.Max(1, v.samplesPerPixel);
        v.sunDiskRadiusRadians = Mathf.Max(0.0f, v.sunDiskRadiusRadians);
        v.sunFlareRadiusRadians = Mathf.Max(v.sunDiskRadiusRadians, v.sunFlareRadiusRadians);
        v.maximumAverageFrames = Mathf.Max(1, v.maximumAverageFrames);
        return v;
    }
}

/// <summary>
/// Temporal accumulation parameters for the denoise stage.
/// </summary>
[Serializable]
public struct CaelixTemporalRadianceSettings
{
    public bool enabled;
    /// <summary>Floor on the current frame's weight, so accumulation never fully stops responding.</summary>
    public float currentFrameMinWeight;
    public bool depthRejection;
    public float depthTolerance;
    public float relativeDepthTolerance;
    public bool normalRejection;
    /// <summary>Minimum dot product between current and reprojected normals to accept history.</summary>
    public float normalThreshold;
    public bool bilinearHistory;
    /// <summary>Accumulation window length; also the convergence counter clamp.</summary>
    public int maximumAverageFrames;

    /// <summary>Clamps user-authored values into ranges the shaders can handle.</summary>
    public CaelixTemporalRadianceSettings Validated()
    {
        CaelixTemporalRadianceSettings v = this;
        v.currentFrameMinWeight = Mathf.Clamp01(v.currentFrameMinWeight);
        v.depthTolerance = Mathf.Max(0.0f, v.depthTolerance);
        v.relativeDepthTolerance = Mathf.Max(0.0f, v.relativeDepthTolerance);
        v.normalThreshold = Mathf.Clamp(v.normalThreshold, -1.0f, 1.0f);
        v.maximumAverageFrames = Mathf.Max(1, v.maximumAverageFrames);
        return v;
    }
}

/// <summary>
/// TAA-style resolve of the final composited colour against its own reprojected history.
/// </summary>
/// <remarks>
/// The primary rays are jittered by one global sub-pixel offset per frame, which by itself only
/// moves the aliasing around. This pass is what turns that into anti-aliasing: it accumulates the
/// jittered samples over the Halton cycle, clipping the history to the current neighbourhood so
/// disocclusions and moving content do not ghost.
/// </remarks>
[Serializable]
public struct CaelixColorResolveSettings
{
    public bool enabled;
    /// <summary>Weight of the current frame. Lower converges smoother but reacts more slowly.</summary>
    [Range(0.02f, 1.0f)] public float blend;
    /// <summary>
    /// Half-width of the neighbourhood clipping box, in local standard deviations. Lower rejects
    /// more history (less ghosting, more aliasing); higher keeps more.
    /// </summary>
    [Range(0.5f, 3.0f)] public float clipScale;

    public static CaelixColorResolveSettings Default => new CaelixColorResolveSettings
    {
        enabled = true,
        blend = 0.1f,
        clipScale = 1.25f
    };

    /// <summary>Clamps user-authored values into ranges the shader can handle.</summary>
    public CaelixColorResolveSettings Validated()
    {
        CaelixColorResolveSettings v = this;
        v.blend = Mathf.Clamp(v.blend, 0.02f, 1.0f);
        v.clipScale = Mathf.Clamp(v.clipScale, 0.5f, 3.0f);
        return v;
    }
}
