using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Stage 3 of 3: chooses which Caelix buffer to display and copies it onto the camera target.
/// </summary>
/// <remarks>
/// The only stage that touches the camera's colour and depth attachments. It performs no filtering
/// of its own — switching <see cref="CaelixDebugView"/> only changes which handle from
/// <see cref="CaelixFrameResources"/> is used as the blit source and how the flip shader decodes
/// it, so a debug view costs nothing extra to render. Camera depth always comes from the G-buffer
/// depth target regardless of the view, so scene geometry keeps depth-testing correctly.
/// <para>
/// The stage runs twice per frame, as two instances. <see cref="Stage"/> 0 draws the fully covered
/// body of the image before the skybox, so it writes camera depth in time for everything that reads
/// <c>_CameraDepthTexture</c>; stage 1 draws the partially covered silhouette pixels the colour
/// resolve produced, after the skybox, so they blend against it. The two partition the image by
/// alpha, so nothing is drawn twice.
/// </para>
/// </remarks>
public class CaelixPresentPass : ScriptableRenderPass
{
    private Material flipMaterial;
    private CaelixDebugView debugView = CaelixDebugView.Regular;

    /// <summary>0 = the opaque body of the image, 1 = the partially covered silhouette pixels.</summary>
    public int Stage { get; set; }

    internal class PassData
    {
        internal CaelixDebugView debugView;
        internal int stage;
        internal TextureHandle Source;
        internal TextureHandle ColorDest;
        internal TextureHandle DepthDest;
        internal Material flipMaterial;
    }

    /// <summary>Binds the flip material owned by the renderer feature. Called when the feature is created.</summary>
    public void Setup(Material flip)
    {
        flipMaterial = flip;
    }

    /// <summary>Pushes this frame's debug view. Called once per camera before enqueueing.</summary>
    public void ConfigureSettings(CaelixDebugView view)
    {
        debugView = view;
    }

    /// <summary>True when the stage has everything it needs to record.</summary>
    public bool IsReady => flipMaterial != null;

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        CaelixFrameResources resources = frameData.GetOrCreate<CaelixFrameResources>();
        if (!resources.IsValid || !IsReady)
        {
            return;
        }

        // Stage 1 only ever fills in silhouette pixels of the composited image, so it ignores the
        // debug selection outright rather than trying to blend a G-buffer against the skybox.
        TextureHandle source = Stage == 0 ? resources.SelectDebugSource(debugView) : resources.Color;
        if (!source.IsValid())
        {
            source = resources.Color;
        }

        UniversalResourceData cameraResources = frameData.Get<UniversalResourceData>();
        string passName = Stage == 0 ? "Caelix Copy To Camera" : "Caelix Copy To Camera (Edges)";

        using (var builder = renderGraph.AddUnsafePass<PassData>(passName, out var passData))
        {
            passData.debugView = debugView;
            passData.stage = Stage;
            passData.Source = source;
            passData.ColorDest = cameraResources.activeColorTexture;
            passData.DepthDest = cameraResources.activeDepthTexture;
            passData.flipMaterial = flipMaterial;

            builder.UseTexture(passData.Source, AccessFlags.Read);
            builder.UseTexture(resources.Depth, AccessFlags.Read);
            builder.UseGlobalTexture(CaelixShaderIDs.DepthTex);
            builder.UseTexture(passData.ColorDest, AccessFlags.Write);
            builder.UseTexture(passData.DepthDest, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) => Execute(data, ctx));
        }
    }

    private static void Execute(PassData data, UnsafeGraphContext context)
    {
        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

        cmd.SetRenderTarget(data.ColorDest, data.DepthDest);
        data.flipMaterial.SetInt(CaelixShaderIDs.DebugView, (int)data.debugView);
        data.flipMaterial.SetInt(CaelixShaderIDs.PresentStage, data.stage);
        Blitter.BlitTexture(cmd, data.Source, new Vector4(1, 1, 0, 0), data.flipMaterial, data.stage);
    }
}
