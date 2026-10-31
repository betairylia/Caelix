using UnityEngine;

/// <summary>
/// The camera state every Caelix screen-space filter needs, pushed to a material as one block.
/// </summary>
/// <remarks>
/// The filters no longer guide off depth alone: they rebuild the view-space ray a pixel was traced
/// with and predict a neighbour's depth from the centre surface's plane. That needs the frame size,
/// both frames' jitter, the projection and both view matrices, and every one of them has to agree
/// with what the raygen used — so they are captured once, from
/// <see cref="CaelixCameraHistory"/>, and applied verbatim by every pass.
/// <para>
/// <see cref="Snapshot"/> is a value type on purpose. Render funcs run after
/// <c>RecordRenderGraph</c> has returned, by which time the history has flipped its double buffer;
/// capturing values rather than the history object keeps the closures value-based, as the rest of
/// the pass data already is.
/// </para>
/// </remarks>
public static class CaelixDenoiseUniforms
{
    /// <summary>One frame's camera state, captured while recording and applied at submit time.</summary>
    public struct Snapshot
    {
        internal Vector4 frameSize;
        internal Vector4 jitter;
        internal Vector4 projection;
        internal Matrix4x4 worldToCamera;
        internal Matrix4x4 previousWorldToCamera;
    }

    /// <summary>Captures the camera state for a target of <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static Snapshot Capture(CaelixCameraHistory history, int width, int height)
    {
        return new Snapshot
        {
            frameSize = new Vector4(
                width,
                height,
                width > 0 ? 1.0f / width : 0.0f,
                height > 0 ? 1.0f / height : 0.0f),
            jitter = new Vector4(
                history.Jitter.x, history.Jitter.y, history.PreviousJitter.x, history.PreviousJitter.y),
            projection = new Vector4(history.Zoom, history.Aspect, 0.0f, 0.0f),
            worldToCamera = history.ViewMatrix,
            previousWorldToCamera = history.PreviousViewMatrix
        };
    }

    /// <summary>Pushes a captured snapshot onto <paramref name="material"/>.</summary>
    public static void Apply(Material material, in Snapshot snapshot)
    {
        material.SetVector(CaelixShaderIDs.FrameSize, snapshot.frameSize);
        material.SetVector(CaelixShaderIDs.CaelixJitter, snapshot.jitter);
        material.SetVector(CaelixShaderIDs.CaelixProjection, snapshot.projection);
        material.SetMatrix(CaelixShaderIDs.CaelixWorldToCamera, snapshot.worldToCamera);
        material.SetMatrix(CaelixShaderIDs.CaelixPrevWorldToCamera, snapshot.previousWorldToCamera);
    }
}
