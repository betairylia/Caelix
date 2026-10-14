using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// URP renderer feature of the native Vulkan renderer (<see cref="CaelixNativeRenderer"/>).
    /// </summary>
    /// <remarks>
    /// Two passes. <see cref="RenderPass"/> (before opaques) issues the plugin's render event with
    /// this frame's packet; the plugin records the trace into Unity's command buffer and writes the
    /// colour target. <see cref="PresentPass"/> (after the skybox, so the skybox does not cover it)
    /// copies the colour target onto the camera colour, full coverage, flipped vertically (the
    /// plugin's row 0 is the top row). No depth yet (P3). The scene itself is fed by
    /// <see cref="CaelixNativeRenderer.Tick"/> through the same packets.
    /// <para>
    /// Enable exactly one Caelix feature per renderer asset. Game cameras only: the plugin counts
    /// one frame per render event, and the Scene view would double the frames in flight.
    /// </para>
    /// The render event goes through <see cref="CommandBuffer.IssuePluginEventAndData"/> on the
    /// native command buffer of an unsafe pass (<see cref="CommandBufferHelpers.GetNativeCommandBuffer"/>):
    /// the RenderGraph command buffer types do not expose plugin events.
    /// </remarks>
    public class CaelixNativeRendererFeature : ScriptableRendererFeature
    {
        private RenderPass renderPass;
        private PresentPass presentPass;
        private bool loggedUnavailable;

        public override void Create()
        {
            renderPass = new RenderPass { renderPassEvent = RenderPassEvent.BeforeRenderingOpaques };
            presentPass = new PresentPass { renderPassEvent = RenderPassEvent.AfterRenderingSkybox };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (renderPass == null) return;
            if (renderingData.cameraData.cameraType != CameraType.Game) return;
            CaelixNativeRenderer native = CaelixNativeRenderer.Active;
            if (native == null || !native.isActiveAndEnabled) return;
            if (!CaelixNativeBridge.IsAvailable)
            {
                if (!loggedUnavailable)
                {
                    loggedUnavailable = true;
                    Debug.LogError("Caelix native: the plugin is not available (not loaded, layout mismatch, or no adopted " +
                                   "device; see the [caelix_unity] lines in the Editor log). Nothing is drawn.");
                }
                return;
            }

            renderer.EnqueuePass(renderPass);
            renderer.EnqueuePass(presentPass);
        }

        protected override void Dispose(bool disposing)
        {
            renderPass = null;
            presentPass = null;
        }

        /// <summary>Hands the imported colour target from the render pass to the present pass.</summary>
        private class FrameData : ContextItem
        {
            public TextureHandle Color;

            public override void Reset()
            {
                Color = TextureHandle.nullHandle;
            }
        }

        private class RenderPass : ScriptableRenderPass
        {
            private class PassData
            {
                internal IntPtr EventFunc;
                internal int RenderEventId;
                internal int ReleaseEventId;
                internal bool ReleaseViews;
                internal IntPtr Packet;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                FrameData frame = frameData.GetOrCreate<FrameData>();
                frame.Reset();

                CaelixNativeRenderer native = CaelixNativeRenderer.Active;
                if (native == null) return;
                if (!CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status)) return;

                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
                if (!native.TryPrepareFrame(cameraData.camera, descriptor.width, descriptor.height,
                        out IntPtr packet, out bool releaseViews))
                {
                    return;
                }

                frame.Color = renderGraph.ImportTexture(native.ColorHandle);

                using (var builder = renderGraph.AddUnsafePass<PassData>("Caelix Native Render", out var passData))
                {
                    passData.EventFunc = CaelixNativeBridge.RenderEventFunc;
                    passData.RenderEventId = status.renderEventId;
                    passData.ReleaseEventId = status.releaseEventId;
                    passData.ReleaseViews = releaseViews;
                    passData.Packet = packet;

                    builder.UseTexture(frame.Color, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                    {
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        if (data.ReleaseViews)
                        {
                            // The packet goes along: when its encoding differs from the plugin's
                            // scene, the release event (queue idle) also recreates the scene.
                            cmd.IssuePluginEventAndData(data.EventFunc, data.ReleaseEventId, data.Packet);
                        }
                        cmd.IssuePluginEventAndData(data.EventFunc, data.RenderEventId, data.Packet);
                    });
                }
            }
        }

        private class PresentPass : ScriptableRenderPass
        {
            private class PassData
            {
                internal TextureHandle Source;
                internal TextureHandle Destination;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<FrameData>()) return;
                FrameData frame = frameData.Get<FrameData>();
                if (!frame.Color.IsValid()) return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                using (var builder = renderGraph.AddUnsafePass<PassData>("Caelix Native Present", out var passData))
                {
                    passData.Source = frame.Color;
                    passData.Destination = resources.activeColorTexture;
                    builder.UseTexture(passData.Source, AccessFlags.Read);
                    builder.UseTexture(passData.Destination, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                    {
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        cmd.SetRenderTarget(data.Destination);
                        // The plugin writes row 0 = the TOP of the image (caelix.h); a Unity render
                        // texture on Vulkan/D3D stores row 0 = the bottom (Unity flips the projection
                        // when it renders into a texture). So the copy flips v.
                        Blitter.BlitTexture(cmd, data.Source, new Vector4(1, -1, 0, 1), 0.0f, false);
                    });
                }
            }
        }
    }
}
