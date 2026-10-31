using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// URP renderer feature of the native Vulkan renderer (<see cref="CaelixNativeRenderer"/>).
    /// </summary>
    /// <remarks>
    /// Frame order ("Fix C" compositing, packet version 8): Unity's opaques, Unity's skybox at the
    /// full resolution, the background downsample, the native render, the present, the motion
    /// vectors, the edges (only without the voxel id mask), then Unity's transparents and 3D UI,
    /// depth-tested against the present's depth as before. The passes, for Game cameras and (unless
    /// <see cref="CaelixNativeRenderer.SceneView"/> is Off) Scene view cameras:
    /// <list type="number">
    /// <item><see cref="RenderPass"/> (after the skybox) asks for the camera's view packet (camera,
    /// jitter from URP's jittered projection, options, the view's targets, the sky cubemap), blits
    /// the camera colour (bilinear) into the view's background input at the geometry size, and
    /// issues the plugin's render event, after a release event when a target was replaced. The
    /// plugin records the trace into Unity's command buffer and writes the colour (the background
    /// at the misses, so an upscaler gets a complete image), the device depth at the camera size
    /// (with an upscaler from unjittered centre rays), motion and albedo (alpha = coverage) at the
    /// geometry size, and the optional voxel id target (<see cref="CaelixNativeRenderer.VoxelIdTarget"/>)
    /// at the camera size. The library writes them with <c>CAELIX_RENDER_FLAG_FLIP_Y</c>, so their
    /// rows already have Unity's order (row 0 at the bottom) and no pass flips.</item>
    /// <item><see cref="PresentPass"/> stage 0 (after the skybox + 1) draws the voxel pixels: colour
    /// and <c>SV_Depth</c> into the camera colour and depth, depth-tested against Unity's opaques.
    /// With a voxel id target (and no <see cref="CaelixNativeRenderer.VoxelIdTMax"/>) the mask is
    /// the voxel id (hit = not 0xFFFFFFFF), dilated by one pixel while the view upscales: the ring
    /// pixels carry the upscaler's blend of scene and background with the miss depth, and the
    /// pixels beyond it show Unity's own full-resolution skybox. Without the mask: coverage
    /// &gt;= 0.99. URP's TAA reads that depth (<c>resourceData.cameraDepth</c>).</item>
    /// <item><see cref="MotionPass"/> (after the skybox + 2, so after URP's motion vector pass in
    /// every depth copy schedule TAA allows) overwrites <c>resourceData.motionVectorColor</c> with the
    /// plugin's motion where the present's rule says "voxel" (the mask, or coverage &gt;= 0.5) and the
    /// voxel is the visible surface (depth test against the camera depth, no depth write). Only when
    /// URP renders motion vectors.</item>
    /// <item><see cref="PresentPass"/> stage 1 (after the skybox + 2), only without the voxel id
    /// mask: blends the pixels with 0.01 &lt;= coverage &lt; 0.99 over the skybox (premultiplied
    /// colour), no depth write.</item>
    /// </list>
    /// The scene itself is fed by <see cref="CaelixNativeRenderer.Tick"/> through its own frame
    /// packet, one per Unity frame, issued before the cameras render: every camera of a frame
    /// draws the same scene state. Nothing is drawn while the renderer has no live session.
    /// <para>
    /// Enable exactly one Caelix feature per renderer asset. Views are per camera, up to
    /// <see cref="CaelixNativeBridge.MaxViews"/>: every Game or Scene view camera gets its own view
    /// with its own plugin renderer (internal targets, NRD history) over the shared scene, so
    /// cameras never mix their denoiser history. A camera beyond the limit is not drawn. Scene view
    /// cameras have no TAA, so the motion pass does not run for them.
    /// </para>
    /// <para>
    /// The present and motion passes bind the targets as global textures (<c>_CaelixNativeColor</c>,
    /// <c>_CaelixNativeDepth</c>, <c>_CaelixNativeMotion</c>, <c>_CaelixNativeAlbedo</c> and, when the
    /// view has it, <c>_CaelixNativeVoxelId</c>: R32_UINT at the camera size, the voxel hash of the
    /// camera ray's first hit, 0xFFFFFFFF on a miss) and the floats <c>_CaelixUseVoxelIdMask</c> and
    /// <c>_CaelixMaskDilate</c>. The globals stay bound after those passes, so a later post-process of
    /// the camera can read the voxel id.
    /// </para>
    /// <para>
    /// The URP depth texture copy (<c>_CameraDepthTexture</c>) runs right after the skybox when the
    /// earliest depth reader is at that event, so before the present: it does not contain the
    /// voxels then (the camera depth attachment does). The opaque texture copy runs after the
    /// present and contains them.
    /// </para>
    /// The render and release events go through <see cref="CommandBuffer.IssuePluginEventAndData"/>
    /// on the native command buffer of an unsafe pass (<see cref="CommandBufferHelpers.GetNativeCommandBuffer"/>):
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
        private static readonly int VoxelIdTexId = Shader.PropertyToID("_CaelixNativeVoxelId");
        private static readonly int PrimaryViewId = Shader.PropertyToID("_CaelixPrimaryView");
        private static readonly int UseVoxelIdMaskId = Shader.PropertyToID("_CaelixUseVoxelIdMask");
        private static readonly int MaskDilateId = Shader.PropertyToID("_CaelixMaskDilate");

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
            // The render pass samples the camera colour (background downsample): it needs an
            // intermediate texture, never the back buffer.
            renderPass = new RenderPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox,
                requiresIntermediateTexture = true,
            };
            presentPass = new PresentPass { renderPassEvent = RenderPassEvent.AfterRenderingSkybox + 1, Stage = 0 };
            motionPass = new MotionPass { renderPassEvent = RenderPassEvent.AfterRenderingSkybox + 2 };
            presentEdgesPass = new PresentPass { renderPassEvent = RenderPassEvent.AfterRenderingSkybox + 2, Stage = 1 };

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
            CaelixNativeRenderer native = CaelixNativeRenderer.Active;
            if (native == null || !native.isActiveAndEnabled) return;
            CameraType cameraType = renderingData.cameraData.cameraType;
            bool sceneView = cameraType == CameraType.SceneView && native.SceneView != CaelixNativeRenderer.SceneViewMode.Off;
            if (cameraType != CameraType.Game && !sceneView) return;
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
            // The edges pass is the partial-coverage path of the presents without the voxel id mask.
            if (!UsesVoxelIdMask(native)) renderer.EnqueuePass(presentEdgesPass);
        }

        /// <summary>
        /// True when the present uses the voxel id target as its mask: the view has a voxel id target,
        /// and no <see cref="CaelixNativeRenderer.VoxelIdTMax"/> turns far voxels into misses (the mask
        /// would hide them).
        /// </summary>
        private static bool UsesVoxelIdMask(CaelixNativeRenderer native) =>
            native.VoxelIdTarget && native.VoxelIdTMax <= 0.0f;

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
            /// <summary>Optional: null while the view has no voxel id target.</summary>
            public TextureHandle VoxelId;
            /// <summary>The background input (the downsampled camera colour), read by the render event.</summary>
            public TextureHandle Background;
            public bool PrimaryView;
            /// <summary>The present uses the voxel id as its mask (see <see cref="UsesVoxelIdMask"/>).</summary>
            public bool UseVoxelIdMask;
            /// <summary>The view upscales this frame: the mask is dilated by one pixel.</summary>
            public bool MaskDilate;

            public override void Reset()
            {
                Color = TextureHandle.nullHandle;
                Depth = TextureHandle.nullHandle;
                Motion = TextureHandle.nullHandle;
                Albedo = TextureHandle.nullHandle;
                VoxelId = TextureHandle.nullHandle;
                Background = TextureHandle.nullHandle;
                PrimaryView = false;
                UseVoxelIdMask = false;
                MaskDilate = false;
            }

            public bool IsValid => Color.IsValid() && Depth.IsValid() && Motion.IsValid() && Albedo.IsValid();
        }

        private class RenderPass : ScriptableRenderPass
        {
            private bool warnedBackBuffer;

            private class PassData
            {
                internal IntPtr EventFunc;
                internal int RenderEventId;
                internal int ReleaseEventId;
                internal IntPtr Packet;
                internal IntPtr ReleasePacket;
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
                // Every camera has its own view (plugin renderer and targets).
                if (!native.TryPrepareFrame(cameraData.camera, cameraData.GetProjectionMatrix(), descriptor.width,
                        descriptor.height, out int view, out IntPtr packet, out IntPtr releasePacket))
                {
                    // A target was replaced: this frame issues only the release of the plugin's
                    // cached target views; the view renders from the next frame on (see
                    // CaelixNativeRenderer.TryPrepareFrame).
                    if (releasePacket != IntPtr.Zero)
                    {
                        using (var builder = renderGraph.AddUnsafePass<PassData>("Caelix Native Release", out var releaseData))
                        {
                            releaseData.EventFunc = CaelixNativeBridge.RenderEventFunc;
                            releaseData.ReleaseEventId = status.releaseEventId;
                            releaseData.ReleasePacket = releasePacket;
                            builder.AllowPassCulling(false);
                            builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                            {
                                CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                                cmd.IssuePluginEventAndData(data.EventFunc, data.ReleaseEventId, data.ReleasePacket);
                            });
                        }
                    }
                    return;
                }

                frame.Color = renderGraph.ImportTexture(native.ColorHandle(view));
                frame.Depth = renderGraph.ImportTexture(native.DepthHandle(view));
                frame.Motion = renderGraph.ImportTexture(native.MotionHandle(view));
                frame.Albedo = renderGraph.ImportTexture(native.AlbedoHandle(view));
                RTHandle voxelId = native.VoxelIdHandle(view);
                if (voxelId != null) frame.VoxelId = renderGraph.ImportTexture(voxelId);
                frame.Background = renderGraph.ImportTexture(native.BackgroundHandle(view));
                frame.PrimaryView = native.ColorIsDisplayEncoded(view);
                frame.UseVoxelIdMask = frame.VoxelId.IsValid() && UsesVoxelIdMask(native);
                frame.MaskDilate = native.ViewUpscales(view);

                // The background input: Unity's camera colour after its skybox, downsampled
                // (bilinear) to the geometry size. The render event reads it, so the graph orders this
                // blit first (the unsafe pass below declares the read).
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                if (!resourceData.isActiveTargetBackBuffer && resourceData.activeColorTexture.IsValid())
                {
                    renderGraph.AddBlitPass(resourceData.activeColorTexture, frame.Background, new Vector2(1, 1), Vector2.zero,
                        passName: "Caelix Native Background");
                }
                else if (!warnedBackBuffer)
                {
                    warnedBackBuffer = true;
                    Debug.LogWarning("Caelix native: the camera renders into the back buffer, so the background input is " +
                                     "not filled (the misses of the native image are undefined).");
                }

                using (var builder = renderGraph.AddUnsafePass<PassData>("Caelix Native Render", out var passData))
                {
                    passData.EventFunc = CaelixNativeBridge.RenderEventFunc;
                    passData.RenderEventId = status.renderEventId;
                    passData.ReleaseEventId = status.releaseEventId;
                    passData.Packet = packet;
                    passData.ReleasePacket = releasePacket;

                    builder.UseTexture(frame.Color, AccessFlags.Write);
                    builder.UseTexture(frame.Depth, AccessFlags.Write);
                    builder.UseTexture(frame.Motion, AccessFlags.Write);
                    builder.UseTexture(frame.Albedo, AccessFlags.Write);
                    if (frame.VoxelId.IsValid()) builder.UseTexture(frame.VoxelId, AccessFlags.Write);
                    builder.UseTexture(frame.Background, AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                    {
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        if (data.ReleasePacket != IntPtr.Zero)
                        {
                            // A target of some view was replaced: the plugin drops its cached image
                            // views (queue idle) before any render uses the new textures.
                            cmd.IssuePluginEventAndData(data.EventFunc, data.ReleaseEventId, data.ReleasePacket);
                        }
                        cmd.IssuePluginEventAndData(data.EventFunc, data.RenderEventId, data.Packet);
                    });
                }
            }
        }

        /// <summary>
        /// Binds the plugin targets as globals: the four the present shader always reads, the voxel id
        /// (when valid: the present's mask, and for later post-processes), and the two mask floats
        /// (<paramref name="useVoxelIdMask"/> only with a valid voxel id).
        /// </summary>
        private static void BindTargets(CommandBuffer cmd, TextureHandle color, TextureHandle depth,
            TextureHandle motion, TextureHandle albedo, TextureHandle voxelId, bool primaryView, bool useVoxelIdMask,
            bool maskDilate)
        {
            cmd.SetGlobalTexture(ColorTexId, color);
            cmd.SetGlobalTexture(DepthTexId, depth);
            cmd.SetGlobalTexture(MotionTexId, motion);
            cmd.SetGlobalTexture(AlbedoTexId, albedo);
            if (voxelId.IsValid()) cmd.SetGlobalTexture(VoxelIdTexId, voxelId);
            cmd.SetGlobalFloat(PrimaryViewId, primaryView ? 1.0f : 0.0f);
            cmd.SetGlobalFloat(UseVoxelIdMaskId, useVoxelIdMask && voxelId.IsValid() ? 1.0f : 0.0f);
            cmd.SetGlobalFloat(MaskDilateId, maskDilate ? 1.0f : 0.0f);
        }

        /// <summary>
        /// Stage 0: the voxel pixels with depth, after the skybox (the voxel id mask, dilated while the
        /// view upscales; without the mask albedo.a &gt;= 0.99). Stage 1 (only without the mask): the
        /// partially covered pixels, blended over the skybox, no depth write.
        /// </summary>
        private class PresentPass : ScriptableRenderPass
        {
            public int Stage { get; set; }
            public Material Material { get; set; }

            private class PassData
            {
                internal TextureHandle Color, Depth, Motion, Albedo, VoxelId;
                internal TextureHandle ColorDest, DepthDest;
                internal bool PrimaryView, UseVoxelIdMask, MaskDilate;
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
                    passData.VoxelId = frame.VoxelId;
                    passData.ColorDest = resources.activeColorTexture;
                    passData.DepthDest = resources.activeDepthTexture;
                    passData.PrimaryView = frame.PrimaryView;
                    passData.UseVoxelIdMask = frame.UseVoxelIdMask;
                    passData.MaskDilate = frame.MaskDilate;
                    passData.Material = Material;
                    passData.ShaderPass = Stage == 0 ? PassPresent : PassPresentEdges;

                    builder.UseTexture(frame.Color, AccessFlags.Read);
                    builder.UseTexture(frame.Depth, AccessFlags.Read);
                    builder.UseTexture(frame.Motion, AccessFlags.Read);
                    builder.UseTexture(frame.Albedo, AccessFlags.Read);
                    // The present's mask; also bound as a global for later passes.
                    if (frame.VoxelId.IsValid()) builder.UseTexture(frame.VoxelId, AccessFlags.Read);
                    builder.UseTexture(passData.ColorDest, AccessFlags.Write);
                    builder.UseTexture(passData.DepthDest, Stage == 0 ? AccessFlags.Write : AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                    {
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        cmd.SetRenderTarget(data.ColorDest, data.DepthDest);
                        BindTargets(cmd, data.Color, data.Depth, data.Motion, data.Albedo, data.VoxelId, data.PrimaryView,
                            data.UseVoxelIdMask, data.MaskDilate);
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
                internal TextureHandle Color, Depth, Motion, Albedo, VoxelId;
                internal TextureHandle MotionDest, DepthDest;
                internal bool UseVoxelIdMask, MaskDilate;
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
                    passData.VoxelId = frame.VoxelId;
                    passData.MotionDest = resources.motionVectorColor;
                    passData.DepthDest = resources.activeDepthTexture;
                    passData.UseVoxelIdMask = frame.UseVoxelIdMask;
                    passData.MaskDilate = frame.MaskDilate;
                    passData.Material = Material;

                    builder.UseTexture(frame.Color, AccessFlags.Read);
                    builder.UseTexture(frame.Depth, AccessFlags.Read);
                    builder.UseTexture(frame.Motion, AccessFlags.Read);
                    builder.UseTexture(frame.Albedo, AccessFlags.Read);
                    if (frame.VoxelId.IsValid()) builder.UseTexture(frame.VoxelId, AccessFlags.Read);
                    builder.UseTexture(passData.MotionDest, AccessFlags.ReadWrite);
                    builder.UseTexture(passData.DepthDest, AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                    {
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        cmd.SetRenderTarget(data.MotionDest, data.DepthDest);
                        BindTargets(cmd, data.Color, data.Depth, data.Motion, data.Albedo, data.VoxelId, false,
                            data.UseVoxelIdMask, data.MaskDilate);
                        Blitter.BlitTexture(cmd, data.Motion, new Vector4(1, 1, 0, 0), data.Material, PassMotion);
                    });
                }
            }
        }
    }
}
