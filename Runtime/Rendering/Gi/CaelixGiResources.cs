using System;
using UnityEngine;

/// <summary>
/// The persistent GPU buffers of the GI resampler for one camera: the lit and unlit sample rings,
/// the per-bucket and per-pixel scratch, the counters and the indirect dispatch arguments.
/// </summary>
/// <remarks>
/// Layouts are in <c>Documentation~/internals/rendering/GI_RESAMPLING.md</c>, section "GPU data",
/// and are mirrored byte for byte by the structs in <c>Shaders/Gi/CaelixGiCommon.hlsl</c>; the
/// stride constants below must match them.
/// <para>
/// Everything is reallocated when the frame size or a ring size changes, and the counters are
/// zeroed from C# then, so a fresh allocation starts with an empty history. Nothing else ever
/// clears the history: stale samples are rejected per pixel by the resampler itself.
/// </para>
/// </remarks>
public sealed class CaelixGiResources : IDisposable
{
    /// <summary>Bytes of one <c>GiLitSample</c>.</summary>
    public const int LitSampleStride = 56;
    /// <summary>Bytes of one <c>GiUnlitSample</c>.</summary>
    public const int UnlitSampleStride = 36;
    /// <summary>Bytes of one <c>GiBucketSample</c>.</summary>
    public const int BucketSampleStride = 32;
    /// <summary>Bytes of one <c>GiBucketInfo</c>.</summary>
    public const int BucketInfoStride = 32;
    /// <summary>Bytes of one <c>GiPixelInfo</c>.</summary>
    public const int PixelInfoStride = 8;

    /// <summary>Reprojected lit samples a bucket keeps per frame.</summary>
    public const int RefinedSamplesPerBucket = 32;
    /// <summary>Lit samples a bucket keeps after refinement, for the spatial pass.</summary>
    public const int CompactSamplesPerBucket = 8;
    /// <summary>Bucket edge length in pixels.</summary>
    public const int BucketSize = 8;

    /// <summary>
    /// Entries of the counts buffer: 8 ring words, then one lit and one unlit count per history slot.
    /// </summary>
    public const int CountsEntries = 8 + 2 * CaelixGiSettings.MaxHistorySlots;
    /// <summary>Two DispatchIndirect triples: lit, then unlit.</summary>
    public const int ArgsEntries = 6;

    public GraphicsBuffer LitRing { get; private set; }
    public GraphicsBuffer UnlitRing { get; private set; }
    /// <summary>Up to <see cref="RefinedSamplesPerBucket"/> reprojected lit samples per bucket.</summary>
    public GraphicsBuffer Refined { get; private set; }
    /// <summary>Up to <see cref="CompactSamplesPerBucket"/> refined lit samples per bucket.</summary>
    public GraphicsBuffer Compact { get; private set; }
    public GraphicsBuffer Buckets { get; private set; }
    public GraphicsBuffer Pixels { get; private set; }
    public GraphicsBuffer Counts { get; private set; }
    public GraphicsBuffer Args { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int BucketsX { get; private set; }
    public int BucketsY { get; private set; }
    public int BucketCount => BucketsX * BucketsY;
    /// <summary>Lit ring capacity in samples.</summary>
    public int LitCapacity { get; private set; }
    /// <summary>Unlit ring capacity in samples.</summary>
    public int UnlitCapacity { get; private set; }

    /// <summary>
    /// Frames the pass has run on these buffers since they were allocated. Selects the per-frame
    /// count slot, so a frame the camera did not render leaves no stale slot behind.
    /// </summary>
    public uint RingFrame { get; private set; }

    /// <summary><c>Time.frameCount</c> of the last frame that used these buffers, for pruning.</summary>
    public int LastUsedFrame { get; set; }

    private int litRingFrames;
    private int unlitRingFrames;

    /// <summary>Total size of every buffer, in bytes.</summary>
    public long EstimatedBytes =>
        (long)LitCapacity * LitSampleStride
        + (long)UnlitCapacity * UnlitSampleStride
        + (long)BucketCount * (RefinedSamplesPerBucket + CompactSamplesPerBucket) * BucketSampleStride
        + (long)BucketCount * BucketInfoStride
        + (long)Width * Height * PixelInfoStride
        + CountsEntries * sizeof(uint)
        + ArgsEntries * sizeof(uint);

    /// <summary>
    /// Makes the buffers match <paramref name="width"/> x <paramref name="height"/> and the ring
    /// sizes of <paramref name="settings"/>, reallocating (and so emptying the history) when any differs.
    /// </summary>
    /// <returns>True when the buffers were reallocated.</returns>
    public bool Ensure(int width, int height, in CaelixGiSettings settings)
    {
        width = Mathf.Max(1, width);
        height = Mathf.Max(1, height);

        if (Counts != null
            && width == Width
            && height == Height
            && settings.litRingFrames == litRingFrames
            && settings.unlitRingFrames == unlitRingFrames)
        {
            return false;
        }

        Release();

        Width = width;
        Height = height;
        BucketsX = (width + BucketSize - 1) / BucketSize;
        BucketsY = (height + BucketSize - 1) / BucketSize;
        litRingFrames = settings.litRingFrames;
        unlitRingFrames = settings.unlitRingFrames;

        long pixels = (long)width * height;
        LitCapacity = ClampCapacity(pixels * litRingFrames, LitSampleStride);
        UnlitCapacity = ClampCapacity(pixels * unlitRingFrames, UnlitSampleStride);

        LitRing = new GraphicsBuffer(GraphicsBuffer.Target.Structured, LitCapacity, LitSampleStride)
        {
            name = "Caelix_GiLitRing"
        };
        UnlitRing = new GraphicsBuffer(GraphicsBuffer.Target.Structured, UnlitCapacity, UnlitSampleStride)
        {
            name = "Caelix_GiUnlitRing"
        };
        Refined = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, BucketCount * RefinedSamplesPerBucket, BucketSampleStride)
        {
            name = "Caelix_GiRefined"
        };
        Compact = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, BucketCount * CompactSamplesPerBucket, BucketSampleStride)
        {
            name = "Caelix_GiCompact"
        };
        Buckets = new GraphicsBuffer(GraphicsBuffer.Target.Structured, BucketCount, BucketInfoStride)
        {
            name = "Caelix_GiBuckets"
        };
        Pixels = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (int)pixels, PixelInfoStride)
        {
            name = "Caelix_GiPixels"
        };
        Counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, CountsEntries, sizeof(uint))
        {
            name = "Caelix_GiCounts"
        };
        // Written by GiPrepareHistory as a typed RWBuffer<uint>: an indirect-arguments buffer cannot
        // also be a structured buffer on every backend.
        Args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, ArgsEntries, sizeof(uint))
        {
            name = "Caelix_GiArgs"
        };

        // Zero heads, totals and every per-frame count: the rings themselves may hold garbage,
        // because no sample is read unless a count says it was written.
        Counts.SetData(new uint[CountsEntries]);
        Args.SetData(new uint[ArgsEntries]);
        RingFrame = 0u;

        return true;
    }

    /// <summary>Returns this frame's ring frame index and advances the counter for the next frame.</summary>
    public uint AdvanceRingFrame()
    {
        return RingFrame++;
    }

    public void Dispose()
    {
        Release();
    }

    /// <summary>
    /// A ring the GPU cannot allocate in one buffer is shrunk to the largest one it can. Below one
    /// frame of pixels a busy frame then overwrites its own oldest samples, which only costs reuse.
    /// </summary>
    private static int ClampCapacity(long samples, int stride)
    {
        long maxSamples = SystemInfo.maxGraphicsBufferSize / stride;
        long clamped = Math.Min(Math.Min(samples, maxSamples), int.MaxValue);
        return (int)Math.Max(1L, clamped);
    }

    private void Release()
    {
        LitRing?.Dispose();
        UnlitRing?.Dispose();
        Refined?.Dispose();
        Compact?.Dispose();
        Buckets?.Dispose();
        Pixels?.Dispose();
        Counts?.Dispose();
        Args?.Dispose();

        LitRing = null;
        UnlitRing = null;
        Refined = null;
        Compact = null;
        Buckets = null;
        Pixels = null;
        Counts = null;
        Args = null;
    }
}
