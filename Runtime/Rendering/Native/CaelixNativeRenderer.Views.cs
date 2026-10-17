using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// The per-camera views of <see cref="CaelixNativeRenderer"/>: the view slots with their targets
    /// and reset state, the view packet of a camera render (<see cref="TryPrepareFrame"/>), and the
    /// camera, options and sky target that go into it.
    /// </summary>
    public partial class CaelixNativeRenderer
    {
        // caelix.h render flags.
        private const uint FlagShading = 1u << 0;
        private const uint FlagSkyOnMiss = 1u << 1;
        private const uint FlagSunNee = 1u << 2;
        private const uint FlagDeltaCheckerboard = 1u << 3;
        /// <summary>Unity render textures store row 0 at the bottom; the library writes them flipped.</summary>
        private const uint FlagFlipY = 1u << 4;

        /// <summary>A view whose camera has not rendered for this many frames is released.</summary>
        private const int ViewIdleFrames = 300;

        /// <summary>The reset state of one view: what its previous frame used.</summary>
        private sealed class ViewFrameState
        {
            public bool HasLastFrame;
            public CaelixNativeCamera LastCamera;
            public CaelixNativeRenderOptions LastOptions;
            public RenderMode LastMode;
            public bool ResetRequested;

            public void Reset(RenderMode viewMode)
            {
                HasLastFrame = false;
                LastCamera = default;
                LastOptions = default;
                LastMode = viewMode;
                ResetRequested = false;
            }
        }

        /// <summary>One view: the camera that owns it, its targets and its reset state.</summary>
        private sealed class ViewSlot
        {
            public Camera Camera;
            public bool InUse;
            public bool IsSceneView;
            /// <summary><see cref="Time.frameCount"/> of the camera's newest prepared frame.</summary>
            public int LastFrame;
            public readonly CaelixNativeViewTargets Targets = new CaelixNativeViewTargets();
            public readonly ViewFrameState State = new ViewFrameState();
        }

        private readonly ViewSlot[] viewSlots = CreateViewSlots();
        private readonly List<int> idleViewScratch = new List<int>();
        private uint frameSeed;
        private int lastPreparedFrame = -1;
        private bool warnedJitter;
        private bool warnedNoView;
        private bool warnedSkyNotCube;

        // Sky cubemap: GetNativeTexturePtr may synchronize with the render thread, so the pointer is
        // read once per texture reference.
        private Texture skyTexture;
        private IntPtr skyTexturePtr;

        // Shared by every instance: a target generation must never repeat within one plugin
        // lifetime (it only feeds a warning of the plugin, so a domain reload is harmless).
        private static uint s_generation;
        private static uint s_releasedGeneration;

        private static ViewSlot[] CreateViewSlots()
        {
            var slots = new ViewSlot[CaelixNativeBridge.MaxViews];
            for (int i = 0; i < slots.Length; i++) slots[i] = new ViewSlot();
            return slots;
        }

        /// <summary>
        /// The colour target the plugin writes for <paramref name="view"/> (RGBA16F, random write).
        /// Null before the view's first frame.
        /// </summary>
        public RTHandle ColorHandle(int view) => viewSlots[view].Targets.Color.Handle;

        /// <summary>Device depth of Unity's GPU projection (R32F) for <paramref name="view"/>; a miss is the far plane.</summary>
        public RTHandle DepthHandle(int view) => viewSlots[view].Targets.Depth.Handle;

        /// <summary>
        /// Motion of the primary hit for <paramref name="view"/> in pixels, previous - current, y up
        /// (flipped rows); w = checkerboard flag.
        /// </summary>
        public RTHandle MotionHandle(int view) => viewSlots[view].Targets.Motion.Handle;

        /// <summary>Albedo (RGBA8) for <paramref name="view"/>; alpha = coverage (1 hit, 0 miss).</summary>
        public RTHandle AlbedoHandle(int view) => viewSlots[view].Targets.Albedo.Handle;

        /// <summary>
        /// True when the colour target of <paramref name="view"/> holds values meant to be shown as
        /// they are (the primary mode's debug views, the path trace mode's motion view), not linear
        /// radiance. The present pass then decodes them from sRGB so that Unity's own output encode
        /// shows the stored value.
        /// </summary>
        public bool ColorIsDisplayEncoded(int view)
        {
            bool isSceneView = viewSlots[view].IsSceneView;
            RenderMode viewMode = ModeFor(isSceneView);
            return viewMode == RenderMode.Primary ||
                   (viewMode == RenderMode.PathTrace && DebugViewFor(isSceneView) == DebugView.Motion);
        }

        /// <summary>The render mode of a view: a Scene view camera may use the primary mode.</summary>
        private RenderMode ModeFor(bool isSceneView) =>
            isSceneView && sceneView == SceneViewMode.Primary ? RenderMode.Primary : mode;

        /// <summary>The debug view of a view: albedo for a Scene view camera in the primary mode.</summary>
        private DebugView DebugViewFor(bool isSceneView) =>
            isSceneView && sceneView == SceneViewMode.Primary ? DebugView.Albedo : debugView;

        /// <summary>
        /// Restarts the reference accumulation and the NRD history with the next frame (the Rust
        /// host's "Reset accumulation" button). The reference mode also restarts by itself when the
        /// camera or an option changes.
        /// </summary>
        [ContextMenu("Reset Accumulation")]
        public void ResetAccumulation()
        {
            foreach (ViewSlot slot in viewSlots) slot.State.ResetRequested = true;
        }

        /// <summary>
        /// Fills the view packet of <paramref name="camera"/> at <paramref name="width"/> x
        /// <paramref name="height"/> pixels. <paramref name="view"/> is the camera's view index (its
        /// own plugin renderer and targets; see <see cref="ColorHandle"/>); the caller issues the
        /// render event with <paramref name="viewPacket"/>. <paramref name="releasePacket"/> is
        /// nonzero when a target of any view was replaced: the caller then issues the release event
        /// with it before the render event. False when no session is live, every view is taken, or
        /// no packet slot is free (the frame of this camera is skipped and nothing is drawn).
        /// </summary>
        public bool TryPrepareFrame(Camera camera, Matrix4x4 jitteredProjection, int width, int height,
            out int view, out IntPtr viewPacket, out IntPtr releasePacket)
        {
            view = -1;
            viewPacket = IntPtr.Zero;
            releasePacket = IntPtr.Zero;
            if (!sessionLive || camera == null || !rings.IsAllocated) return false;
            if (width <= 0 || height <= 0) return false;

            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            CaelixNativePacketRings.Maintain(status);

            int index = FindViewSlot(camera);
            if (index < 0)
            {
                PacketsSkipped++;
                if (!warnedNoView)
                {
                    warnedNoView = true;
                    Debug.LogWarning($"Caelix native: every one of the {CaelixNativeBridge.MaxViews} views is taken; camera " +
                                     $"'{camera.name}' is not drawn.", this);
                }
                return false;
            }
            IntPtr memory = rings.TakeViewPacket(status, out ulong sequence);
            if (memory == IntPtr.Zero)
            {
                PacketsSkipped++;
                return false;
            }

            ViewSlot slot = viewSlots[index];
            slot.LastFrame = Time.frameCount;
            // Replacing a target bumps the shared generation, which needs a release event first.
            if (slot.Targets.Ensure(width, height, index)) s_generation++;
            if (s_generation != s_releasedGeneration)
            {
                releasePacket = rings.FillRelease(CaelixNativeBridge.ReleaseTargetViews, 0, status);
                if (releasePacket == IntPtr.Zero)
                {
                    // The plugin may still cache a view of a destroyed texture: no render without the release.
                    PacketsSkipped++;
                    return false;
                }
                s_releasedGeneration = s_generation;
            }

            bool isGame = camera.cameraType == CameraType.Game;
            var data = new CaelixNativeViewPacket
            {
                header = new CaelixNativePacketHeader
                {
                    size = (uint)CaelixNativePacketRings.ViewPacketBytes,
                    version = CaelixNativeBridge.PacketVersion,
                    kind = CaelixNativeBridge.PacketView,
                    sequence = sequence,
                },
                view = (uint)index,
                flags = isGame ? CaelixNativeBridge.ViewReportTimings : 0u,
                targetGeneration = s_generation,
                color = slot.Targets.Color.ToPacket(width, height),
                depth = slot.Targets.Depth.ToPacket(width, height),
                motion = slot.Targets.Motion.ToPacket(width, height),
                albedo = slot.Targets.Albedo.ToPacket(width, height),
                sky = SkyTarget(),
            };
            data.camera = BuildCamera(camera, jitteredProjection, width, height, isGame);
            data.options = BuildOptions(slot, data.camera);
            unsafe
            {
                *(CaelixNativeViewPacket*)memory = data;
            }
            PacketsIssued++;
            view = index;
            viewPacket = memory;
            return true;
        }

        /// <summary>The view of <paramref name="camera"/>, or a free view claimed for it; -1 when every view is taken.</summary>
        private int FindViewSlot(Camera camera)
        {
            int free = -1;
            for (int v = 0; v < viewSlots.Length; v++)
            {
                ViewSlot slot = viewSlots[v];
                if (slot.InUse)
                {
                    if (ReferenceEquals(slot.Camera, camera)) return v;
                }
                else if (free < 0)
                {
                    free = v;
                }
            }
            if (free < 0) return -1;
            ViewSlot claimed = viewSlots[free];
            claimed.InUse = true;
            claimed.Camera = camera;
            claimed.IsSceneView = camera.cameraType == CameraType.SceneView;
            claimed.LastFrame = Time.frameCount;
            claimed.State.Reset(ModeFor(claimed.IsSceneView));
            return free;
        }

        /// <summary>True for a view in use whose camera is gone or has not rendered for <see cref="ViewIdleFrames"/>.</summary>
        private bool ViewIsIdle(int v)
        {
            ViewSlot slot = viewSlots[v];
            if (!slot.InUse) return false;
            return slot.Camera == null || Time.frameCount - slot.LastFrame > ViewIdleFrames;
        }

        /// <summary>Frees view <paramref name="v"/> and its targets (the plugin's renderer must be released already).</summary>
        private void FreeViewSlot(int v)
        {
            ViewSlot slot = viewSlots[v];
            slot.Targets.Release();
            slot.InUse = false;
            slot.Camera = null;
            slot.IsSceneView = false;
            slot.State.Reset(mode);
        }

        /// <summary>
        /// The sky target: <see cref="skyCubemap"/>, else <see cref="ReflectionProbe.defaultTexture"/>
        /// with <see cref="skyFromReflectionProbe"/> when it is a cubemap, else none (the procedural sky).
        /// </summary>
        private CaelixNativeTarget SkyTarget()
        {
            Texture texture = null;
            if (skyCubemap != null)
            {
                if (skyCubemap.dimension == TextureDimension.Cube)
                {
                    texture = skyCubemap;
                }
                else if (!warnedSkyNotCube)
                {
                    warnedSkyNotCube = true;
                    Debug.LogWarning($"Caelix native: sky cubemap '{skyCubemap.name}' is not a cube texture; the procedural sky is used.", this);
                }
            }
            else if (skyFromReflectionProbe)
            {
                Texture probe = ReflectionProbe.defaultTexture;
                if (probe != null && probe.dimension == TextureDimension.Cube) texture = probe;
            }
            if (texture == null) return default;
            if (!ReferenceEquals(texture, skyTexture) || skyTexturePtr == IntPtr.Zero)
            {
                skyTexture = texture;
                skyTexturePtr = texture.GetNativeTexturePtr();
            }
            if (skyTexturePtr == IntPtr.Zero) return default;
            return new CaelixNativeTarget
            {
                nativeTexture = skyTexturePtr,
                nativeRenderBuffer = IntPtr.Zero,
                width = (uint)texture.width,
                height = (uint)texture.width,
            };
        }

        /// <summary>
        /// The library camera of this frame. <paramref name="jitteredProjection"/> is URP's projection
        /// of the frame (<c>UniversalCameraData.GetProjectionMatrix()</c>): with TAA it is
        /// <c>Translate(o) * P</c>, which moves every projected point by o (NDC, y up, o = 2 * jitter /
        /// size). A pixel centre then sees the point that the unjittered projection puts o further
        /// back, so the library's sample offset is -o: in pixels (y down) jitter = ((P'02 - P02) * w / 2,
        /// -(P'12 - P12) * h / 2), because P'02 = P02 - o.x (P32 = -1) and likewise for y.
        /// Sign verified in Play Mode (P3): with this sign the TAA image of a static camera is sharp,
        /// with the opposite sign visibly blurred (Laplacian variance 169 vs 124).
        /// <see cref="LastJitter"/> follows the Game cameras only (<paramref name="isGame"/>).
        /// </summary>
        private unsafe CaelixNativeCamera BuildCamera(Camera camera, Matrix4x4 jitteredProjection, int width,
            int height, bool isGame)
        {
            var result = new CaelixNativeCamera();
            Transform t = camera.transform;
            Vector3 p = t.position, r = t.right, u = t.up, f = t.forward;
            result.position[0] = p.x; result.position[1] = p.y; result.position[2] = p.z;
            result.right[0] = r.x; result.right[1] = r.y; result.right[2] = r.z;
            result.up[0] = u.x; result.up[1] = u.y; result.up[2] = u.z;
            result.forward[0] = f.x; result.forward[1] = f.y; result.forward[2] = f.z;
            result.tanHalfFovY = Mathf.Tan(0.5f * camera.fieldOfView * Mathf.Deg2Rad);
            result.aspect = (float)width / height;
            result.nearZ = camera.nearClipPlane;
            result.farZ = camera.farClipPlane;

            // Unity's view space looks down -Z; the library's view z is +forward, so every row is
            // multiplied by diag(1, 1, -1, 1) (column 2 negated). Rows 0 and 1 come from the
            // unjittered GL projection (clip y up, as NRD's viewToClipMatrix expects; the GPU
            // projection of a render texture has row 1 negated on Vulkan). Rows 2 and 3 come from
            // the GPU projection: its z is the device depth Unity's rasterizer writes (reversed Z
            // on Vulkan), which the depth target must match.
            Matrix4x4 gl = camera.projectionMatrix;
            Matrix4x4 gpu = GL.GetGPUProjectionMatrix(gl, true);
            for (int c = 0; c < 4; c++)
            {
                float sign = c == 2 ? -1.0f : 1.0f;
                for (int row = 0; row < 4; row++)
                {
                    float value = row < 2 ? gl[row, c] : gpu[row, c];
                    result.projection[c * 4 + row] = sign * value;
                }
            }

            float jitterX = (jitteredProjection.m02 - gl.m02) * 0.5f * width;
            float jitterY = -(jitteredProjection.m12 - gl.m12) * 0.5f * height;
            if (!(Mathf.Abs(jitterX) <= 1.0f && Mathf.Abs(jitterY) <= 1.0f))
            {
                // Not a TAA jitter (an oblique or off-centre projection that differs from
                // camera.projectionMatrix in another way): render unjittered.
                if (!warnedJitter)
                {
                    warnedJitter = true;
                    Debug.LogWarning($"Caelix native: the frame's projection differs from camera.projectionMatrix by " +
                                     $"({jitterX:F3}, {jitterY:F3}) px, more than a TAA jitter; the jitter is ignored.", this);
                }
                jitterX = 0.0f;
                jitterY = 0.0f;
            }
            result.jitter[0] = jitterX;
            result.jitter[1] = jitterY;
            if (isGame) LastJitter = new Vector2(jitterX, jitterY);
            return result;
        }

        /// <summary>The options of <paramref name="slot"/>, with its own mode, debug view and reset state.</summary>
        private unsafe CaelixNativeRenderOptions BuildOptions(ViewSlot slot, in CaelixNativeCamera camera)
        {
            RenderMode viewMode = ModeFor(slot.IsSceneView);
            ViewFrameState state = slot.State;
            bool lighting = viewMode != RenderMode.Primary;
            uint flags = FlagFlipY;
            if (viewMode == RenderMode.Primary && shading) flags |= FlagShading;
            if (lighting && skyOnMiss) flags |= FlagSkyOnMiss;
            if (lighting && sunNextEventEstimation) flags |= FlagSunNee;
            if (viewMode == RenderMode.PathTrace && deltaCheckerboard) flags |= FlagDeltaCheckerboard;

            var o = new CaelixNativeRenderOptions
            {
                debugView = (uint)DebugViewFor(slot.IsSceneView),
                flags = flags,
                // Unity on Vulkan uses reversed Z (GL.GetGPUProjectionMatrix).
                missDepth = SystemInfo.usesReversedZBuffer ? 0.0f : 1.0f,
                missViewZ = 1e30f,
                mode = (uint)viewMode,
                bounceCount = (uint)Mathf.Clamp(bounces, 0, 14),
                denoise = viewMode == RenderMode.PathTrace && denoise ? 1u : 0u,
                exposure = exposure,
                sunAngularRadius = sunAngularRadiusDegrees * Mathf.Deg2Rad,
                bounceCountTransparent = (uint)Mathf.Clamp(bouncesTransparent, 0, 15),
                emissionScale = emissionScale,
                fogDensity = Mathf.Max(0.0f, fogDensity),
                // sky stays zero: the plugin fills it from the view packet's sky target.
            };
            Vector3 sun = lighting ? sunDirection : primarySunDirection;
            o.sunDir[0] = sun.x; o.sunDir[1] = sun.y; o.sunDir[2] = sun.z;
            o.skyColor[0] = primarySkyColor.x; o.skyColor[1] = primarySkyColor.y; o.skyColor[2] = primarySkyColor.z;
            o.sunRadiance[0] = sunRadiance.x; o.sunRadiance[1] = sunRadiance.y; o.sunRadiance[2] = sunRadiance.z;
            o.skyZenith[0] = skyZenith.x; o.skyZenith[1] = skyZenith.y; o.skyZenith[2] = skyZenith.z;
            o.skyHorizon[0] = skyHorizon.x; o.skyHorizon[1] = skyHorizon.y; o.skyHorizon[2] = skyHorizon.z;
            o.skyGround[0] = skyGround.x; o.skyGround[1] = skyGround.y; o.skyGround[2] = skyGround.z;
            o.fogColor[0] = fogColor.r; o.fogColor[1] = fogColor.g; o.fogColor[2] = fogColor.b;

            // Reset rules of the Rust host: the reference mode restarts when the camera (jitter
            // excluded) or an option changes; both lighting modes on a mode change and on
            // ResetAccumulation. The path trace mode's NRD follows the camera through the motion.
            // Each view keeps its own reset state.
            bool cameraChanged = !state.HasLastFrame || !SameCamera(state.LastCamera, camera);
            bool optionsChanged = !state.HasLastFrame || !SameOptions(state.LastOptions, o);
            bool reset = state.ResetRequested || viewMode != state.LastMode ||
                         (viewMode == RenderMode.Reference && (cameraChanged || optionsChanged));
            o.resetAccumulation = reset ? 1u : 0u;
            state.ResetRequested = false;
            state.LastCamera = camera;
            state.LastOptions = o;
            state.HasLastFrame = true;

            // One new seed per Unity frame, not per camera.
            if (Time.frameCount != lastPreparedFrame)
            {
                lastPreparedFrame = Time.frameCount;
                frameSeed++;
            }
            o.frameSeed = frameSeed;
            state.LastMode = viewMode;
            return o;
        }

        /// <summary>Same pose and frustum (the jitter is ignored: TAA moves it every frame).</summary>
        private static unsafe bool SameCamera(CaelixNativeCamera a, CaelixNativeCamera b)
        {
            a.jitter[0] = a.jitter[1] = 0f;
            b.jitter[0] = b.jitter[1] = 0f;
            return UnsafeUtility.MemCmp(&a, &b, sizeof(CaelixNativeCamera)) == 0;
        }

        /// <summary>Same options, the frame seed and the reset flag ignored.</summary>
        private static unsafe bool SameOptions(CaelixNativeRenderOptions a, CaelixNativeRenderOptions b)
        {
            a.frameSeed = b.frameSeed = 0;
            a.resetAccumulation = b.resetAccumulation = 0;
            return UnsafeUtility.MemCmp(&a, &b, sizeof(CaelixNativeRenderOptions)) == 0;
        }
    }
}
