using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Temporal state that survives between frames for one camera: the double-buffered history targets,
/// the previous view matrix and this frame's sub-pixel jitter.
/// </summary>
/// <remarks>
/// Owned by the registry rather than by a pass, because two stages need the same view of it within a
/// frame — the G-buffer stage writes this frame's depth/normal history, the denoise stage reads last
/// frame's. <see cref="BeginFrame"/> is called exactly once per camera per frame by the G-buffer
/// stage while recording, so "previous" and "current" cannot drift apart between stages.
/// <para>
/// The jitter lives here for the same reason: the tracer offsets its primary rays by it, and both
/// filter chains have to reconstruct the exact ray a pixel was traced with, this frame and last.
/// </para>
/// </remarks>
public sealed class CaelixCameraHistory
{
    private static readonly Dictionary<Camera, CaelixCameraHistory> s_Cameras =
        new Dictionary<Camera, CaelixCameraHistory>();

    private RTHandle indirectRadianceA;
    private RTHandle indirectRadianceB;
    private RTHandle depthA;
    private RTHandle depthB;
    private RTHandle normalA;
    private RTHandle normalB;
    private RTHandle colorA;
    private RTHandle colorB;
    private bool useAAsPrevious = true;

    /// <summary>
    /// Frame this history was last advanced on, so <see cref="BeginFrame"/> and
    /// <see cref="EndFrame"/> are idempotent within a frame. Two Caelix features enabled on the
    /// same renderer would otherwise each roll the view matrix and flip the double buffer, leaving
    /// "previous" pointing at the frame before last and reprojection permanently one frame stale.
    /// </summary>
    private int lastAdvancedFrame = -1;
    private bool flippedThisFrame;

    /// <summary>Position in the jitter sequence. Reset whenever the targets are reallocated.</summary>
    private int jitterFrameIndex;

    /// <summary>Set by <see cref="MarkColorHistoryWritten"/>, consumed and cleared by <see cref="EndFrame"/>.</summary>
    private bool colorHistoryWrittenThisFrame;

    /// <summary>False on the first frame and after any reallocation, i.e. history must not be sampled.</summary>
    public bool IsValid { get; private set; }

    /// <summary>View matrix of the frame before this one, used for reprojection.</summary>
    public Matrix4x4 PreviousViewMatrix { get; private set; }

    /// <summary>View matrix of the frame currently being recorded.</summary>
    public Matrix4x4 ViewMatrix { get; private set; }

    /// <summary>How many frames have accumulated without a history reset, clamped to the accumulation window.</summary>
    public int ConvergedFrames { get; private set; }

    /// <summary>
    /// This frame's sub-pixel offset of every primary ray, in pixels, each component in [-0.5, 0.5].
    /// One value for the whole image: a global offset shifts the sampling lattice without giving
    /// neighbouring pixels different rays, which is what makes the guides reconstructible.
    /// </summary>
    public Vector2 Jitter { get; private set; }

    /// <summary>Last frame's <see cref="Jitter"/>, needed to reconstruct a history tap's own ray.</summary>
    public Vector2 PreviousJitter { get; private set; }

    /// <summary>tan(verticalFov / 2) of the camera being recorded.</summary>
    public float Zoom { get; private set; }

    /// <summary>Width over height of the target being recorded.</summary>
    public float Aspect { get; private set; }

    /// <summary>Length of the Halton jitter cycle.</summary>
    public int JitterSequenceLength => 8;

    /// <summary>False until a full colour-history frame has been written, i.e. it must not be sampled.</summary>
    public bool ColorHistoryValid { get; private set; }

    public RTHandle PreviousIndirectRadiance => useAAsPrevious ? indirectRadianceA : indirectRadianceB;
    public RTHandle CurrentIndirectRadiance => useAAsPrevious ? indirectRadianceB : indirectRadianceA;
    public RTHandle PreviousDepth => useAAsPrevious ? depthA : depthB;
    public RTHandle CurrentDepth => useAAsPrevious ? depthB : depthA;
    public RTHandle PreviousNormal => useAAsPrevious ? normalA : normalB;
    public RTHandle CurrentNormal => useAAsPrevious ? normalB : normalA;
    public RTHandle PreviousColor => useAAsPrevious ? colorA : colorB;
    public RTHandle CurrentColor => useAAsPrevious ? colorB : colorA;

    /// <summary>
    /// Fetches (creating on first use) the history for <paramref name="camera"/> and advances it into
    /// the frame being recorded: reallocates on resize, rolls the view matrix and the jitter forward,
    /// and updates the convergence counter.
    /// </summary>
    public static CaelixCameraHistory BeginFrame(
        Camera camera,
        Matrix4x4 viewMatrix,
        float verticalFovDegrees,
        int width,
        int height,
        int maximumAverageFrames)
    {
        if (!s_Cameras.TryGetValue(camera, out CaelixCameraHistory history))
        {
            history = new CaelixCameraHistory
            {
                ViewMatrix = viewMatrix,
                PreviousViewMatrix = viewMatrix
            };
            s_Cameras.Add(camera, history);
        }

        history.EnsureAllocated(width, height);

        if (history.lastAdvancedFrame == Time.frameCount)
        {
            return history;
        }

        history.lastAdvancedFrame = Time.frameCount;
        history.flippedThisFrame = false;

        history.PreviousViewMatrix = history.ViewMatrix;
        history.ViewMatrix = viewMatrix;
        history.ConvergedFrames = history.IsValid
            ? Mathf.Min(history.ConvergedFrames + 1, Mathf.Max(1, maximumAverageFrames))
            : 0;

        Vector2 jitter = Halton23(history.jitterFrameIndex % history.JitterSequenceLength);
        // On the first frame after an allocation there is no previous frame to reconstruct, so both
        // sides of the pair name the same ray rather than an offset that was never traced.
        history.PreviousJitter = history.jitterFrameIndex == 0 ? jitter : history.Jitter;
        history.Jitter = jitter;
        history.jitterFrameIndex++;

        history.Zoom = Mathf.Tan(Mathf.Deg2Rad * verticalFovDegrees * 0.5f);
        history.Aspect = height > 0 ? width / (float)height : 1.0f;

        return history;
    }

    /// <summary>
    /// Flips which half of each double buffer is "previous". Called once per frame by the denoise
    /// stage after it has recorded its write into <see cref="CurrentIndirectRadiance"/>.
    /// </summary>
    public void EndFrame()
    {
        if (flippedThisFrame)
        {
            return;
        }

        flippedThisFrame = true;
        useAAsPrevious = !useAAsPrevious;
        IsValid = true;

        // The colour history is optional (its resolve can be switched off), so unlike the other
        // halves it only becomes readable when this frame actually filled it.
        ColorHistoryValid = colorHistoryWrittenThisFrame;
        colorHistoryWrittenThisFrame = false;
    }

    /// <summary>
    /// Records that this frame writes a complete <see cref="CurrentColor"/>. Call before
    /// <see cref="EndFrame"/>; the write becomes readable as <see cref="PreviousColor"/> next frame.
    /// </summary>
    public void MarkColorHistoryWritten()
    {
        colorHistoryWrittenThisFrame = true;
    }

    /// <summary>Releases every camera's history. Call when the renderer feature is torn down.</summary>
    public static void ReleaseAll()
    {
        foreach (CaelixCameraHistory history in s_Cameras.Values)
        {
            history.Release();
        }

        s_Cameras.Clear();
    }

    private void EnsureAllocated(int width, int height)
    {
        RenderTextureDescriptor indirectDesc = MakeDescriptor(width, height, RenderTextureFormat.ARGBHalf);
        RenderTextureDescriptor depthDesc = MakeDescriptor(width, height, RenderTextureFormat.RFloat);
        RenderTextureDescriptor normalDesc = MakeDescriptor(width, height, RenderTextureFormat.ARGBHalf);

        bool reallocated = false;
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref indirectRadianceA, indirectDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "Caelix_HistoryIndirectRadiance_A");
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref indirectRadianceB, indirectDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "Caelix_HistoryIndirectRadiance_B");
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref depthA, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "Caelix_HistoryDepth_A");
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref depthB, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "Caelix_HistoryDepth_B");
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref normalA, normalDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "Caelix_HistoryNormal_A");
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref normalB, normalDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "Caelix_HistoryNormal_B");
        // Bilinear, because the colour resolve fetches this one through a Catmull-Rom filter built
        // out of bilinear taps rather than by integer coordinate.
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref colorA, indirectDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "Caelix_HistoryColor_A");
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref colorB, indirectDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "Caelix_HistoryColor_B");

        if (reallocated)
        {
            IsValid = false;
            ColorHistoryValid = false;
            useAAsPrevious = true;
            // The jitter pair is only meaningful against a history that survived, so a fresh
            // allocation restarts the sequence instead of carrying a stale "previous" offset.
            jitterFrameIndex = 0;
        }
    }

    private void Release()
    {
        indirectRadianceA?.Release();
        indirectRadianceB?.Release();
        depthA?.Release();
        depthB?.Release();
        normalA?.Release();
        normalB?.Release();
        colorA?.Release();
        colorB?.Release();

        indirectRadianceA = null;
        indirectRadianceB = null;
        depthA = null;
        depthB = null;
        normalA = null;
        normalB = null;
        colorA = null;
        colorB = null;
        IsValid = false;
        ColorHistoryValid = false;
    }

    /// <summary>
    /// Sub-pixel offset number <paramref name="index"/> of the Halton (2, 3) sequence, recentred on
    /// the pixel centre. Halton is used rather than a random draw because a fixed-length cycle
    /// covers the pixel evenly in exactly <see cref="JitterSequenceLength"/> frames.
    /// </summary>
    private static Vector2 Halton23(int index)
    {
        int sequencePosition = index + 1;
        return new Vector2(
            RadicalInverse(sequencePosition, 2) - 0.5f,
            RadicalInverse(sequencePosition, 3) - 0.5f);
    }

    private static float RadicalInverse(int index, int radix)
    {
        float result = 0.0f;
        float fraction = 1.0f / radix;

        while (index > 0)
        {
            result += (index % radix) * fraction;
            index /= radix;
            fraction /= radix;
        }

        return result;
    }

    private static RenderTextureDescriptor MakeDescriptor(int width, int height, RenderTextureFormat format)
    {
        return new RenderTextureDescriptor(width, height, format, 0)
        {
            enableRandomWrite = true,
            msaaSamples = 1,
            volumeDepth = 1,
            useMipMap = false,
            autoGenerateMips = false,
            sRGB = false
        };
    }
}
