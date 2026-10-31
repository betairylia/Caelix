using System;
using UnityEngine;

public enum CaelixIndirectSpatialFilterMode
{
    Disabled = 0,
    Separable15Tap = 1,
    ATrous = 2
}

[Serializable]
public struct CaelixSeparable15TapFilterSettings
{
    public const int MaxRadius = 7;

    [Range(1, MaxRadius)] public int radius;
    [Min(0.0001f)] public float distanceSigma;

    public static CaelixSeparable15TapFilterSettings Default => new CaelixSeparable15TapFilterSettings
    {
        radius = 7,
        distanceSigma = 18.0f
    };
}

[Serializable]
public struct CaelixATrousFilterSettings
{
    /// <summary>
    /// Upper bound on iterations. Also the number of materials the denoise stage allocates, since
    /// each iteration needs its own to carry a distinct step width to submission.
    /// </summary>
    public const int MaxIterations = 6;

    [Range(1, MaxIterations)] public int iterations;
    public bool useFaceHash;
    [Tooltip("Jitter the sparse taps of iterations with step width > 1 by a per-pixel/per-frame hash. Breaks the structured checkerboard / grid-dot patterns the a-trous hole pattern leaves in heavy noise; the stochastic residue is removed by temporal accumulation.")]
    public bool jitterTaps;
    [Min(0.0f)] public float normalPower;
    [Tooltip("Absolute plane-distance tolerance in world units: how far a tap's depth may sit off the center surface's plane before it is rejected. Roughly a quarter voxel is a good start.")]
    [Min(0.0f)] public float depthTolerance;
    [Tooltip("Plane-distance tolerance as a fraction of the center depth, taken whenever it exceeds the absolute one. Covers depth quantization far from the camera.")]
    [Min(0.0f)] public float relativeDepthTolerance;
    [Tooltip("Multiplier on the local luminance std-dev for the radiance edge-stopping weight (SVGF-style). ~4 is a good start; larger blurs more across lighting edges.")]
    [Min(0.0001f)] public float radianceSigma;

    public static CaelixATrousFilterSettings Default => new CaelixATrousFilterSettings
    {
        iterations = 4,
        useFaceHash = false,
        jitterTaps = true,
        normalPower = 64.0f,
        depthTolerance = 0.2f,
        relativeDepthTolerance = 0.005f,
        radianceSigma = 4.0f
    };

    /// <summary>
    /// Clamps user-authored values into ranges the shader can handle. Budget mode passes this
    /// struct on its own, so the clamping lives here rather than only in the owning settings block.
    /// </summary>
    public CaelixATrousFilterSettings Validated()
    {
        CaelixATrousFilterSettings v = this;
        v.iterations = Mathf.Clamp(v.iterations, 1, MaxIterations);
        v.normalPower = Mathf.Max(0.0f, v.normalPower);
        // The shader divides by the tolerance, so it must never reach zero.
        v.depthTolerance = Mathf.Max(0.0001f, v.depthTolerance);
        v.relativeDepthTolerance = Mathf.Max(0.0f, v.relativeDepthTolerance);
        v.radianceSigma = Mathf.Max(0.0001f, v.radianceSigma);
        return v;
    }
}

[Serializable]
public struct CaelixIndirectDenoisingSettings
{
    public CaelixIndirectSpatialFilterMode mode;

    [Header("Separable 15 Tap")]
    public CaelixSeparable15TapFilterSettings separable15Tap;

    [Header("A-Trous")]
    public CaelixATrousFilterSettings aTrous;

    public static CaelixIndirectDenoisingSettings Default => new CaelixIndirectDenoisingSettings
    {
        mode = CaelixIndirectSpatialFilterMode.Separable15Tap,
        separable15Tap = CaelixSeparable15TapFilterSettings.Default,
        aTrous = CaelixATrousFilterSettings.Default
    };

    /// <summary>Clamps user-authored values into ranges the shaders can handle.</summary>
    public CaelixIndirectDenoisingSettings Validated()
    {
        CaelixIndirectDenoisingSettings v = this;

        v.separable15Tap.radius = Mathf.Clamp(
            v.separable15Tap.radius, 1, CaelixSeparable15TapFilterSettings.MaxRadius);
        v.separable15Tap.distanceSigma = Mathf.Max(0.0001f, v.separable15Tap.distanceSigma);

        v.aTrous = v.aTrous.Validated();

        return v;
    }
}
