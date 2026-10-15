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
    /// Four passes, all Game cameras only:
    /// <list type="number">
    /// <item><see cref="RenderPass"/> (before opaques) fills the camera part of this frame's packet
    /// (camera, jitter from URP's jittered projection, options, targets) and issues the plugin's
    /// render event. The plugin records the trace into Unity's command buffer and writes four
    /// targets: colour, device depth, motion and albedo (alpha = coverage). The library writes them
    /// with <c>CAELIX_RENDER_FLAG_FLIP_Y</c>, so their rows already have Unity's order (row 0 at the
    /// bottom) and no pass flips.</item>
    /// <item><see cref="PresentPass"/> stage 0 (after opaques) draws the pixels with coverage
    /// &gt;= 0.99: colour and <c>SV_Depth</c> into the camera colour and depth, depth-tested against
    /// the opaque geometry. URP's TAA reads that depth (<c>resourceData.cameraDepth</c>).</item>
    /// <item><see cref="MotionPass"/> (after the skybox, so after URP's motion vector pass in every
    /// depth copy schedule TAA allows) overwrites <c>resourceData.motionVectorColor</c> with the
    /// plugin's motion where coverage &gt;= 0.5 and the voxel is the visible surface (depth test
    /// against the camera depth, no depth write). Only when URP renders motion vectors.</item>
    /// <item><see cref="PresentPass"/> stage 1 (after the skybox) blends the pixels with
    /// 0.01 &lt;= coverage &lt; 0.99 over the skybox (premultiplied colour), no depth write.</item>
    /// </list>
    /// The scene itself is fed by <see cref="CaelixNativeRenderer.Tick"/> through the same packets.
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
        /// <summary>Name of the present shader under a Resources folder of the package.</summary>
        private const string PresentShaderResource = "CaelixNativePresent";

        private static readonly int ColorTexId = Shader.PropertyToID("_CaelixNativeColor");
        private static readonly int DepthTexId = Shader.PropertyToID("_CaelixNativeDepth");
        private static readonly int MotionTexId = Shader.PropertyToID("_CaelixNativeMotion");
        private static readonly int AlbedoTexId = Shader.PropertyToID("_CaelixNativeAlbedo");
        private static readonly int PrimaryViewId = Shader.PropertyToID("_CaelixPrimaryView");

        // Shader pass indices of Caelix/NativePresent.
        private const int PassPresent = 0;
        private const int PassPresentEdges = 1;
        private const int PassMotion = 2;

        private RenderPass renderPass;
        private PresentPass presentPass;
        private PresentPass presentEdgesPass;
        private MotionPass motionPass;
        private Material presentMaterial;
        private bool loggedUnavailable;
        private bool loggedNoShader;

        public override void Create()
        {
            renderPass = new RenderPass { renderPassEvent = RenderPassEvent.BeforeRenderingOpaques };
            presentPass = new PresentPass { renderPassEvent = RenderPassEvent.AfterRenderingOpaques, Stage = 0 };
            motionPass = new MotionPass { renderPassEvent = RenderPassEvent.AfterRenderingSkybox + 1 };
            presentEdgesPass = new PresentPass { renderPassEvent = RenderPassEvent.AfterRenderingSkybox + 1, Stage = 1 };

            CoreUtils.Destroy(presentMaterial);
            presentMaterial = null;
            Shader shader = Resources.Load<Shader>(PresentShaderResource);
            if (shader != null)
            {
                presentMaterial = CoreUtils.CreateEngineMaterial(shader);
            }
            presentPass.Material = presentMaterial;
            presentEdgesPass.Material = presentMaterial;
            motionPass.Material = presentMaterial;
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
            if (presentMaterial == null)
            {
                if (!loggedNoShader)
                {
                    loggedNoShader = true;
                    Debug.LogError($"Caelix native: shader Resources/{PresentShaderResource} not found; nothing is presented.");
                }
                return;
            }

            // URP renders its motion vectors only on request. TAA requests them itself; asking here
            // too keeps the texture valid whenever TAA is requested on this camera.
            bool wantsMotion = renderingData.cameraData.antialiasing == AntialiasingMode.TemporalAntiAliasing;
            presentPass.ConfigureInput(wantsMotion ? ScriptableRenderPassInput.Motion : ScriptableRenderPassInput.None);

            renderer.EnqueuePass(renderPass);
            renderer.EnqueuePass(presentPass);
            if (wantsMotion) renderer.EnqueuePass(motionPass);
            renderer.EnqueuePass(presentEdgesPass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(presentMaterial);
            presentMaterial = null;
            renderPass = null;
            presentPass = null;
            presentEdgesPass = null;
            motionPass = null;
        }

        /// <summary>Hands the imported targets from the render pass to the present and motion passes.</summary>
        private class FrameData : ContextItem
        {
            public TextureHandle Color;
            public TextureHandle Depth;
            public TextureHandle Motion;
            public TextureHandle Albedo;
            public bool PrimaryView;

            public override void Reset()
            {
                Color = TextureHandle.nullHandle;
                Depth = TextureHandle.nullHandle;
                Motion = TextureHandle.nullHandle;
                Albedo = TextureHandle.nullHandle;
                PrimaryView = false;
            }

            public bool IsValid => Color.IsValid() && Depth.IsValid() && Motion.IsValid() && Albedo.IsValid();
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

                // The camera is filled here, not in Tick: URP computes this frame's TAA jitter when
                // it sets up the camera, and GetProjectionMatrix() returns the jittered projection.
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
                if (!native.TryPrepareFrame(cameraData.camera, cameraData.GetProjectionMatrix(), descriptor.width,
                        descriptor.height, out IntPtr packet, out bool releaseViews))
                {
                    return;
                }

                frame.Color = renderGraph.ImportTexture(native.ColorHandle);
                frame.Depth = renderGraph.ImportTexture(native.DepthHandle);
                frame.Motion = renderGraph.ImportTexture(native.MotionHandle);
                frame.Albedo = renderGraph.ImportTexture(native.AlbedoHandle);
                frame.PrimaryView = native.ColorIsDisplayEncoded;

                using (var builder = renderGraph.AddUnsafePass<PassData>("Caelix Native Render", out var passData))
                {
                    passData.EventFunc = CaelixNativeBridge.RenderEventFunc;
                    passData.RenderEventId = status.renderEventId;
                    passData.ReleaseEventId = status.releaseEventId;
                    passData.ReleaseViews = releaseViews;
                    passData.Packet = packet;

                    builder.UseTexture(frame.Color, AccessFlags.Write);
                    builder.UseTexture(frame.Depth, AccessFlags.Write);
                    builder.UseTexture(frame.Motion, AccessFlags.Write);
                    builder.UseTexture(frame.Albedo, AccessFlags.Write);
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

        /// <summary>Binds the four plugin targets as globals for the present shader.</summary>
        private static void BindTargets(CommandBuffer cmd, TextureHandle color, TextureHandle depth,
            TextureHandle motion, TextureHandle albedo, bool primaryView)
        {
            cmd.SetGlobalTexture(ColorTexId, color);
            cmd.SetGlobalTexture(DepthTexId, depth);
            cmd.SetGlobalTexture(MotionTexId, motion);
            cmd.SetGlobalTexture(AlbedoTexId, albedo);
            cmd.SetGlobalFloat(PrimaryViewId, primaryView ? 1.0f : 0.0f);
        }

        /// <summary>
        /// Stage 0: covered pixels (albedo.a &gt;= 0.99) with depth, after the opaques. Stage 1: the
        /// partially covered pixels, blended over the skybox, no depth write.
        /// </summary>
        private class PresentPass : ScriptableRenderPass
        {
            public int Stage { get; set; }
            public Material Material { get; set; }

            private class PassData
            {
                internal TextureHandle Color, Depth, Motion, Albedo;
                internal TextureHandle ColorDest, DepthDest;
                internal bool PrimaryView;
                internal Material Material;
                internal int ShaderPass;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (Material == null || !frameData.Contains<FrameData>()) return;
                FrameData frame = frameData.Get<FrameData>();
                if (!frame.IsValid) return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                string name = Stage == 0 ? "Caelix Native Present" : "Caelix Native Present (Edges)";
                using (var builder = renderGraph.AddUnsafePass<PassData>(name, out var passData))
                {
                    passData.Color = frame.Color;
                    passData.Depth = frame.Depth;
                    passData.Motion = frame.Motion;
                    passData.Albedo = frame.Albedo;
                    passData.ColorDest = resources.activeColorTexture;
                    passData.DepthDest = resources.activeDepthTexture;
                    passData.PrimaryView = frame.PrimaryView;
                    passData.Material = Material;
                    passData.ShaderPass = Stage == 0 ? PassPresent : PassPresentEdges;

                    builder.UseTexture(frame.Color, AccessFlags.Read);
                    builder.UseTexture(frame.Depth, AccessFlags.Read);
                    builder.UseTexture(frame.Motion, AccessFlags.Read);
                    builder.UseTexture(frame.Albedo, AccessFlags.Read);
                    builder.UseTexture(passData.ColorDest, AccessFlags.Write);
                    builder.UseTexture(passData.DepthDest, Stage == 0 ? AccessFlags.Write : AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                    {
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        cmd.SetRenderTarget(data.ColorDest, data.DepthDest);
                        BindTargets(cmd, data.Color, data.Depth, data.Motion, data.Albedo, data.PrimaryView);
                        // The library already wrote Unity's row order (CAELIX_RENDER_FLAG_FLIP_Y): no flip.
                        Blitter.BlitTexture(cmd, data.Color, new Vector4(1, 1, 0, 0), data.Material, data.ShaderPass);
                    });
                }
            }
        }

        /// <summary>
        /// Writes the plugin's motion into URP's motion vector texture where the voxels are the
        /// visible surface, converted to URP's convention (see the shader).
        /// </summary>
        private class MotionPass : ScriptableRenderPass
        {
            public Material Material { get; set; }

            private class PassData
            {
                internal TextureHandle Color, Depth, Motion, Albedo;
                internal TextureHandle MotionDest, DepthDest;
                internal Material Material;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (Material == null || !frameData.Contains<FrameData>()) return;
                FrameData frame = frameData.Get<FrameData>();
                if (!frame.IsValid) return;
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (!resources.motionVectorColor.IsValid()) return;

                using (var builder = renderGraph.AddUnsafePass<PassData>("Caelix Native Motion Vectors", out var passData))
                {
                    passData.Color = frame.Color;
                    passData.Depth = frame.Depth;
                    passData.Motion = frame.Motion;
                    passData.Albedo = frame.Albedo;
                    passData.MotionDest = resources.motionVectorColor;
                    passData.DepthDest = resources.activeDepthTexture;
                    passData.Material = Material;

                    builder.UseTexture(frame.Color, AccessFlags.Read);
                    builder.UseTexture(frame.Depth, AccessFlags.Read);
                    builder.UseTexture(frame.Motion, AccessFlags.Read);
                    builder.UseTexture(frame.Albedo, AccessFlags.Read);
                    builder.UseTexture(passData.MotionDest, AccessFlags.ReadWrite);
                    builder.UseTexture(passData.DepthDest, AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                    {
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        cmd.SetRenderTarget(data.MotionDest, data.DepthDest);
                        BindTargets(cmd, data.Color, data.Depth, data.Motion, data.Albedo, false);
                        Blitter.BlitTexture(cmd, data.Motion, new Vector4(1, 1, 0, 0), data.Material, PassMotion);
                    });
                }
            }
        }
    }
}
