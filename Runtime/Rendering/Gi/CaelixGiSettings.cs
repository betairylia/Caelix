using System;
using UnityEngine;

/// <summary>
/// Parameters of the GI resampling pass (<see cref="CaelixGiPass"/>). Built from the GI feature's
/// serialized field once per camera and handed to the pass as an immutable snapshot.
/// </summary>
/// <remarks>
/// The meaning of every field is in <c>Documentation~/internals/rendering/GI_RESAMPLING.md</c>,
/// section "Settings". The ring sizes are counted in frames of pixels, so the sample storage scales
/// with the resolution.
/// </remarks>
[Serializable]
public struct CaelixGiSettings
{
    /// <summary>Most frames whose samples the resampler can still reproject; the per-frame counters cycle over this many slots.</summary>
    public const int MaxHistorySlots = 128;

    [Tooltip("A pixel traces a new GI sample every N frames (1 = every frame).")]
    [Min(1)] public int sampleRateDivisor;

    [Tooltip("Apply the divisor to every pixel. Off (NAADF): a pixel with n frames of temporal history traces every min(n + 1, divisor) frames, so pixels that just appeared sample every frame and their bucket is full after one frame.")]
    public bool uniformSampleRate;

    [Tooltip("Chance a sky hit is kept, weighted by 1/p. Lower it when the sky is bright and storage is tight.")]
    [Range(0.001f, 1.0f)] public float skySampleProbability;

    [Tooltip("Luminance below which a sample is stochastically dropped or boosted (NAADF radiance leveling).")]
    [Min(0.0001f)] public float litThreshold;

    [Tooltip("One unlit sample in N is stored, weighted N.")]
    [Min(1)] public int unlitKeepDivisor;

    [Tooltip("Lit sample ring capacity, in frames of pixels.")]
    [Range(1, 8)] public int litRingFrames;

    [Tooltip("Unlit sample ring capacity, in frames of pixels.")]
    [Range(1, 8)] public int unlitRingFrames;

    [Tooltip("Oldest frame whose samples are still reprojected.")]
    [Range(1, MaxHistorySlots)] public int maxHistoryFrames;

    [Tooltip("Reservoir iterations per pixel in the spatial pass.")]
    [Range(1, 64)] public int spatialIterations;

    [Tooltip("Size of the neighbour square in pixels, before the adaptive factor.")]
    [Min(1.0f)] public float spatialRadius;

    [Tooltip("NAADF's varying resampling radius: shrink the neighbour square where nearby buckets are well lit.")]
    public bool adaptiveRadius;

    [Tooltip("Knob of the adaptive radius. Higher keeps the radius larger.")]
    [Min(0.0f)] public float radiusLitFactor;

    [Tooltip("Remove dim samples per bucket and boost the rest, so every stored sample has the bucket's maximum luminance.")]
    public bool sampleLeveling;

    [Tooltip("A bucket with fewer lit + unlit samples than this is not used.")]
    [Min(0)] public int minBucketSamples;

    [Tooltip("Lower bound of the reconnection Jacobian gate.")]
    [Min(0.0f)] public float jacobianMin;

    [Tooltip("Upper bound of the reconnection Jacobian gate.")]
    [Min(0.0f)] public float jacobianMax;

    [Tooltip("When the spatial pass finds no usable candidate, or its chosen candidate is occluded, output the pixel's own fresh sample instead of black. Removes most of the darkening on walls and in recesses at the price of some grain there.")]
    public bool ownSampleFallback;

    [Tooltip("Diagnostic output of the spatial pass into the diffuse target (view it with the StochasticDiffuse debug view): 0 off, 1 candidates found per pixel, 2 outcome (red none, blue occluded, green used), 3 bucket state (R stored, G lit ratio, B sample count), 4 adaptive radius factor.")]
    [Range(0, 4)] public int debugMode;

    public static CaelixGiSettings Default => new CaelixGiSettings
    {
        sampleRateDivisor = 1,
        uniformSampleRate = false,
        skySampleProbability = 1.0f,
        litThreshold = 1.0f,
        unlitKeepDivisor = 8,
        litRingFrames = 2,
        unlitRingFrames = 4,
        maxHistoryFrames = 64,
        spatialIterations = 12,
        spatialRadius = 160.0f,
        adaptiveRadius = true,
        radiusLitFactor = 3.0f,
        sampleLeveling = true,
        minBucketSamples = 12,
        jacobianMin = 0.3f,
        jacobianMax = 2.5f,
        ownSampleFallback = true,
        debugMode = 0
    };

    /// <summary>Clamps user-authored values into ranges the shaders can handle.</summary>
    public CaelixGiSettings Validated()
    {
        CaelixGiSettings v = this;
        v.sampleRateDivisor = Mathf.Max(1, v.sampleRateDivisor);
        v.skySampleProbability = Mathf.Clamp(v.skySampleProbability, 0.001f, 1.0f);
        v.litThreshold = Mathf.Max(0.0001f, v.litThreshold);
        v.unlitKeepDivisor = Mathf.Max(1, v.unlitKeepDivisor);
        v.litRingFrames = Mathf.Clamp(v.litRingFrames, 1, 8);
        v.unlitRingFrames = Mathf.Clamp(v.unlitRingFrames, 1, 8);
        v.maxHistoryFrames = Mathf.Clamp(v.maxHistoryFrames, 1, MaxHistorySlots);
        v.spatialIterations = Mathf.Clamp(v.spatialIterations, 1, 64);
        v.spatialRadius = Mathf.Max(1.0f, v.spatialRadius);
        v.radiusLitFactor = Mathf.Max(0.0f, v.radiusLitFactor);
        v.minBucketSamples = Mathf.Max(0, v.minBucketSamples);
        v.jacobianMin = Mathf.Max(0.0f, v.jacobianMin);
        v.jacobianMax = Mathf.Max(v.jacobianMin, v.jacobianMax);
        v.debugMode = Mathf.Clamp(v.debugMode, 0, 4);
        return v;
    }
}
