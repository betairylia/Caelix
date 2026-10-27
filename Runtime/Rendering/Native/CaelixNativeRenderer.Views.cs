using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// The per-camera views of <see cref="CaelixNativeRenderer"/>: the view slots with their targets
    /// and reset state, the view packet of a camera render (<see cref="TryPrepareFrame"/>), and the
    /// camera, options, upscaler request and sky target that go into it.
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
            /// <summary>The newest prepared frame of the view upscales (see <see cref="ViewUpscales"/>).</summary>
            public bool Upscales;
            /// <summary>The upscaler key whose "not available" warning was logged for this view.</summary>
            public CaelixNativeUpscalerStatus WarnedUnsupported;
            public bool HasWarnedUnsupported;
        }

        /// <summary>The upscaler of one view frame (see <see cref="ResolveUpscaler"/>).</summary>
        private struct UpscalerRequest
        {
            /// <summary><c>CaelixUpscaler</c>; 0 = no upscaler for this frame.</summary>
            public uint Upscaler;
            public uint Quality;
            /// <summary>The render size from the plugin's query; 0 while it is not known yet.</summary>
            public uint RenderWidth;
            public uint RenderHeight;
        }

        private readonly ViewSlot[] viewSlots = CreateViewSlots();
        private readonly List<int> idleViewScratch = new List<int>();
        private uint frameSeed;
        private int lastPreparedFrame = -1;
        private bool warnedJitter;
        private bool warnedNoView;
        private bool warnedSkyNotCube;
        private bool warnedUpscalerTaa;

        // Sky cubemap: GetNativeTexturePtr may synchronize with the render thread, so the pointer is
        // read once per texture reference.
        private Texture skyTexture;
        private IntPtr skyTexturePtr;

        /// <summary>The sun angular radius the library uses for 0 (caelix.h).</summary>
        private const float DefaultSunAngularRadiusDegrees = 0.5f;
        // The sun sync problem logged last (verbose): one log per problem and light, not per frame.
        private SunLightProblem loggedSunProblem;
        private Light loggedSunProblemLight;

        private enum SunLightProblem
        {
            None = 0,
            NoLight = 1,
            NotDirectional = 2,
            Disabled = 3,
        }

        // Sky capture (see UpdateSkyCapture).
        /// <summary>A sun turn of more than this many degrees since the last capture triggers a new one.</summary>
        private const float SkyCaptureSunTurnDegrees = 0.1f;
        private Camera skyCaptureCamera;
        private RenderTexture skyCaptureTexture;
        /// <summary>The current <see cref="skyCaptureTexture"/> holds a capture (with mips).</summary>
        private bool skyCaptureValid;
        private int skyCaptureLastFrame;
        private Vector3 skyCaptureSunDirection;
        private bool skyCaptureLogged;
        private bool warnedSkyCaptureNoMaterial;
        private bool warnedSkyCaptureFailed;

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

        /// <summary>
        /// Device depth of Unity's GPU projection (R32F, camera size) for <paramref name="view"/>; a
        /// miss is the far plane.
        /// </summary>
        public RTHandle DepthHandle(int view) => viewSlots[view].Targets.Depth.Handle;

        /// <summary>
        /// Motion of the primary hit for <paramref name="view"/> in pixels, previous - current, y up
        /// (flipped rows); w = checkerboard flag.
        /// </summary>
        public RTHandle MotionHandle(int view) => viewSlots[view].Targets.Motion.Handle;

        /// <summary>Albedo (RGBA8) for <paramref name="view"/>; alpha = coverage (1 hit, 0 miss).</summary>
        public RTHandle AlbedoHandle(int view) => viewSlots[view].Targets.Albedo.Handle;

        /// <summary>
        /// The voxel id target of <paramref name="view"/> (R32_UINT at the camera size, the voxel hash of
        /// the camera ray's first hit, 0xFFFFFFFF on a miss). Null while the view has none
        /// (<see cref="VoxelIdTarget"/> off, or before the view's first frame).
        /// </summary>
        public RTHandle VoxelIdHandle(int view) => viewSlots[view].Targets.VoxelId.Handle;

        /// <summary>
        /// The background input of <paramref name="view"/> (RGBA16F at the geometry size, sampled
        /// only): the feature downsamples the camera colour into it after Unity's skybox, before the
        /// render event. Null before the view's first frame.
        /// </summary>
        public RTHandle BackgroundHandle(int view) => viewSlots[view].Targets.Background.Handle;

        /// <summary>
        /// True when the newest prepared frame of <paramref name="view"/> asks for the upscaler with a
        /// known render size (its geometry targets have that size, so the plugin upscales it).
        /// </summary>
        public bool ViewUpscales(int view) => viewSlots[view].Upscales;

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
        /// With an upscaler, the colour target keeps <paramref name="width"/> x <paramref name="height"/>
        /// and the motion, albedo and background targets get the render size that the plugin's status
        /// reports for the view's key (see <see cref="ResolveUpscaler"/>).
        /// </summary>
        public bool TryPrepareFrame(Camera camera, Matrix4x4 jitteredProjection, int width, int height,
            out int view, out IntPtr viewPacket, out IntPtr releasePacket)
        {
            view = -1;
            viewPacket = IntPtr.Zero;
            releasePacket = IntPtr.Zero;
            if (!sessionLive || camera == null || !rings.IsAllocated) return false;
            if (width <= 0 || height <= 0) return false;
            // The sky capture camera is a Reflection camera that the feature skips; never give it a view.
            if (ReferenceEquals(camera, skyCaptureCamera)) return false;

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
            UpscalerRequest upscale = ResolveUpscaler(slot, index, camera, status, width, height);
            slot.Upscales = upscale.Upscaler != 0 && upscale.RenderWidth != 0 && upscale.RenderHeight != 0;
            int geometryWidth = upscale.RenderWidth != 0 ? (int)upscale.RenderWidth : width;
            int geometryHeight = upscale.RenderHeight != 0 ? (int)upscale.RenderHeight : height;
            // Replacing a target bumps the shared generation, which needs a release event first.
            if (slot.Targets.Ensure(width, height, geometryWidth, geometryHeight, index, voxelIdTarget)) s_generation++;
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
                color = slot.Targets.Color.ToPacket(),
                depth = slot.Targets.Depth.ToPacket(),
                motion = slot.Targets.Motion.ToPacket(),
                albedo = slot.Targets.Albedo.ToPacket(),
                sky = SkyTarget(),
                voxelId = voxelIdTarget ? slot.Targets.VoxelId.ToPacket() : default,
                lightNee = new CaelixNativeLightNeeOptions
                {
                    enabled = lightNee ? 1u : 0u,
                    flags = lightNeeMis ? 0u : CaelixNativeBridge.LightNeeFlagNoMis,
                },
                background = slot.Targets.Background.ToPacket(),
            };
            data.camera = BuildCamera(camera, jitteredProjection, width, height, isGame, upscale);
            data.options = BuildOptions(slot, data.camera, upscale);
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
            claimed.HasWarnedUnsupported = false;
            return free;
        }

        /// <summary>
        /// The upscaler of this frame of <paramref name="slot"/>. None unless the view is in the path
        /// trace mode and <see cref="upscaler"/> is set. The plugin queries the upscaler for the key
        /// (upscaler, quality, <paramref name="width"/> x <paramref name="height"/>) and publishes the
        /// result in its status: when the status entry of the view has this key and says supported,
        /// the request carries its render size (the geometry targets get that size); before that the
        /// render size is 0 (geometry at the camera size, and the plugin renders this frame without
        /// the upscaler). When the entry says unsupported, the request is none (one warning per key).
        /// </summary>
        private UpscalerRequest ResolveUpscaler(ViewSlot slot, int view, Camera camera, in CaelixNativeStatus status,
            int width, int height)
        {
            var request = new UpscalerRequest();
            if (upscaler == Upscaler.None || ModeFor(slot.IsSceneView) != RenderMode.PathTrace) return request;
            request.Upscaler = (uint)upscaler;
            request.Quality = (uint)upscaleQuality;

            CaelixNativeUpscalerStatus entry = status.Upscaler(view);
            if (entry.Matches(request.Upscaler, request.Quality, width, height))
            {
                if (entry.supported == 0 || entry.renderWidth == 0 || entry.renderHeight == 0)
                {
                    if (!slot.HasWarnedUnsupported || !slot.WarnedUnsupported.Matches(entry.upscaler, entry.quality,
                            (int)entry.outputWidth, (int)entry.outputHeight))
                    {
                        slot.HasWarnedUnsupported = true;
                        slot.WarnedUnsupported = entry;
                        Debug.LogWarning($"Caelix native: {upscaler} {upscaleQuality} at {width}x{height} is not available " +
                                         $"(reason {entry.reason}: {UpscalerReasonText(entry.reason)}); view {view} renders " +
                                         "without the upscaler.", this);
                    }
                    return new UpscalerRequest();
                }
                request.RenderWidth = entry.renderWidth;
                request.RenderHeight = entry.renderHeight;
            }

            if (!warnedUpscalerTaa && camera.TryGetComponent(out UniversalAdditionalCameraData cameraData) &&
                cameraData.antialiasing == AntialiasingMode.TemporalAntiAliasing)
            {
                warnedUpscalerTaa = true;
                Debug.LogWarning($"Caelix native: DLSS replaces TAA; set the anti-aliasing of camera '{camera.name}' to None.", this);
            }
            return request;
        }

        /// <summary>Text of a <c>CaelixUpscalerUnsupportedReason</c>.</summary>
        private static string UpscalerReasonText(uint reason)
        {
            switch (reason)
            {
                case 0: return "available";
                case 1: return "the library was built without the upscaler";
                case 2: return "NGX did not start (NVIDIA driver, DLL or Vulkan device extension missing; see the Editor log)";
                case 3: return "the GPU or the NGX runtime lacks the feature";
                case 4: return "the driver is too old";
                case 5: return "the upscaler has no render size for this quality";
                case 6: return "bad argument (output below 64 x 32, or an unknown value)";
                default: return "unknown reason";
            }
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
            slot.HasWarnedUnsupported = false;
        }

        /// <summary>
        /// The sky target: <see cref="skyCubemap"/>, else the captured sky cube with
        /// <see cref="skyCapture"/> (once a capture exists, see <see cref="UpdateSkyCapture"/>), else
        /// <see cref="ReflectionProbe.defaultTexture"/> with <see cref="skyFromReflectionProbe"/> when it
        /// is a cubemap, else none (the procedural sky).
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
            else if (skyCapture && skyCaptureValid && skyCaptureTexture != null && skyCaptureTexture.IsCreated())
            {
                texture = skyCaptureTexture;
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

        #region Sun sync

        /// <summary>
        /// The sun of this frame. With <see cref="syncSunFromLight"/> and an enabled directional light
        /// (<see cref="sunLight"/>, else <see cref="RenderSettings.sun"/>): <paramref name="direction"/> =
        /// the light's -forward (toward the sun) and <paramref name="radiance"/> = E / (2 pi (1 - cos r)).
        /// E = colour x intensity is URP's irradiance of a surface facing the light; r is the sun's
        /// angular radius (0 = the library default, 0.5 degrees). The library's sun disk has constant
        /// radiance over that cone, so a surface facing it receives E again. Returns true then.
        /// Else (sync off, or no usable light: logged once per problem with verbose logging) the
        /// serialized <see cref="sunDirection"/> and <see cref="sunRadiance"/>, and false.
        /// </summary>
        private bool ResolveSun(out Vector3 direction, out Vector3 radiance)
        {
            if (syncSunFromLight)
            {
                Light light = sunLight != null ? sunLight : RenderSettings.sun;
                SunLightProblem problem = SunLightProblem.None;
                if (light == null) problem = SunLightProblem.NoLight;
                else if (light.type != LightType.Directional) problem = SunLightProblem.NotDirectional;
                else if (!light.isActiveAndEnabled) problem = SunLightProblem.Disabled;

                if (problem == SunLightProblem.None)
                {
                    loggedSunProblem = SunLightProblem.None;
                    loggedSunProblemLight = null;
                    direction = -light.transform.forward;
                    // Unity with linear light intensities (URP's setting): the light colour is picked in
                    // sRGB and converted to linear, then scaled by the intensity (and by the colour
                    // temperature's RGB when the project uses colour temperatures).
                    Color c = light.color.linear;
                    if (light.useColorTemperature && GraphicsSettings.lightsUseColorTemperature)
                    {
                        c *= Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature);
                    }
                    Vector3 irradiance = new Vector3(c.r, c.g, c.b) * light.intensity;
                    float degrees = sunAngularRadiusDegrees > 0.0f ? sunAngularRadiusDegrees : DefaultSunAngularRadiusDegrees;
                    // 2 pi (1 - cos r) = 4 pi sin^2(r / 2): no cancellation at small r.
                    float sinHalf = Mathf.Sin(0.5f * degrees * Mathf.Deg2Rad);
                    float solidAngle = 4.0f * Mathf.PI * sinHalf * sinHalf;
                    radiance = irradiance / solidAngle;
                    return true;
                }

                if (problem != loggedSunProblem || !ReferenceEquals(light, loggedSunProblemLight))
                {
                    loggedSunProblem = problem;
                    loggedSunProblemLight = light;
                    if (verboseLogging)
                    {
                        string reason = problem == SunLightProblem.NoLight
                            ? "Sun Light is empty and RenderSettings.sun is not set"
                            : problem == SunLightProblem.NotDirectional
                                ? $"light '{light.name}' is a {light.type} light, not a directional light"
                                : $"light '{light.name}' is disabled";
                        Debug.Log($"Caelix native: Sync Sun: {reason}; the Sun Direction and Sun Radiance fields are used.", this);
                    }
                }
            }
            direction = sunDirection;
            radiance = sunRadiance;
            return false;
        }

        #endregion

        #region Sky capture

        /// <summary>
        /// Keeps the captured sky cube current; the tick calls it before the cameras of the frame
        /// render. Without <see cref="skyCapture"/>, with an assigned <see cref="skyCubemap"/>, or
        /// without a skybox material, the cube and the capture camera are released (the sky then
        /// falls back as <see cref="SkyTarget"/> says). Else the cube is (re)created at
        /// <see cref="skyCaptureSize"/> and captured when it is new, every
        /// <see cref="skyCaptureInterval"/> frames, and when the sun direction of
        /// <see cref="ResolveSun"/> turned by more than <see cref="SkyCaptureSunTurnDegrees"/>.
        /// A capture renders the skybox (<see cref="skyCaptureMaterial"/> swapped into
        /// <see cref="RenderSettings.skybox"/> for the call) into the six faces with
        /// <see cref="Camera.RenderToCubemap(RenderTexture, int)"/>, then generates the mips.
        /// </summary>
        private void UpdateSkyCapture()
        {
            if (!skyCapture || skyCubemap != null)
            {
                ReleaseSkyCapture();
                return;
            }
            Material material = skyCaptureMaterial != null ? skyCaptureMaterial : RenderSettings.skybox;
            if (material == null)
            {
                if (!warnedSkyCaptureNoMaterial && verboseLogging)
                {
                    Debug.Log("Caelix native: sky capture: no Sky Capture Material and no RenderSettings.skybox; nothing " +
                              "is captured.", this);
                }
                warnedSkyCaptureNoMaterial = true;
                ReleaseSkyCapture();
                return;
            }
            warnedSkyCaptureNoMaterial = false;

            int size = Mathf.Clamp(skyCaptureSize, 16, 512);
            if (skyCaptureTexture != null && skyCaptureTexture.width != size) ReleaseSkyCaptureTexture();
            if (skyCaptureTexture == null)
            {
                skyCaptureTexture = new RenderTexture(size, size, GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.None)
                {
                    name = "Caelix Sky Capture",
                    dimension = TextureDimension.Cube,
                    useMipMap = true,
                    autoGenerateMips = false,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            bool fresh = false;
            if (!skyCaptureTexture.IsCreated())
            {
                // The first creation, or Unity lost the texture: a new native image under the same reference.
                skyCaptureTexture.Create();
                OnSkyCaptureImageChanged();
                fresh = true;
            }

            ResolveSun(out Vector3 sun, out _);
            bool due = fresh || Time.frameCount - skyCaptureLastFrame >= Mathf.Max(1, skyCaptureInterval) ||
                       (skyCaptureValid && Vector3.Angle(sun, skyCaptureSunDirection) > SkyCaptureSunTurnDegrees);
            if (!due) return;
            skyCaptureLastFrame = Time.frameCount;
            skyCaptureSunDirection = sun;

            Camera camera = EnsureSkyCaptureCamera();
            camera.transform.position = SkyCapturePosition();
            Material previous = RenderSettings.skybox;
            bool swap = material != previous;
            bool rendered;
            if (swap) RenderSettings.skybox = material;
            try
            {
                rendered = camera.RenderToCubemap(skyCaptureTexture, 63);
            }
            finally
            {
                if (swap) RenderSettings.skybox = previous;
            }
            if (!rendered)
            {
                // Retried every interval; until then the sky falls back past the capture.
                skyCaptureValid = false;
                if (!warnedSkyCaptureFailed)
                {
                    warnedSkyCaptureFailed = true;
                    Debug.LogWarning("Caelix native: sky capture: Camera.RenderToCubemap failed; the sky falls back to the " +
                                     "reflection probe or the procedural sky.", this);
                }
                return;
            }
            skyCaptureTexture.GenerateMips();
            skyCaptureValid = true;
            if (!skyCaptureLogged && verboseLogging)
            {
                skyCaptureLogged = true;
                Debug.Log($"Caelix native: sky captured: {size} px per face, {skyCaptureTexture.mipmapCount} mips, " +
                          $"material '{material.name}'.", this);
            }
        }

        /// <summary>
        /// The capture texture got a new native image: no capture in it yet, the cached native pointer
        /// is stale (<see cref="SkyTarget"/> re-reads it), and the plugin's renderers cache image views
        /// per image, so the next view packet first releases every target view (generation bump).
        /// </summary>
        private void OnSkyCaptureImageChanged()
        {
            skyCaptureValid = false;
            skyTexture = null;
            skyTexturePtr = IntPtr.Zero;
            s_generation++;
        }

        /// <summary>The capture camera: hidden, disabled, skybox only, a Reflection camera (the feature draws Game and Scene view cameras only).</summary>
        private Camera EnsureSkyCaptureCamera()
        {
            if (skyCaptureCamera != null) return skyCaptureCamera;
            var go = new GameObject("Caelix Sky Capture Camera") { hideFlags = HideFlags.HideAndDontSave };
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.cameraType = CameraType.Reflection;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.cullingMask = 0;
            camera.allowHDR = true;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            UniversalAdditionalCameraData data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
            data.renderShadows = false;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;
            skyCaptureCamera = camera;
            return camera;
        }

        /// <summary>The position of the newest Game view camera, else <see cref="Camera.main"/>, else the origin.</summary>
        private Vector3 SkyCapturePosition()
        {
            Camera best = null;
            int bestFrame = int.MinValue;
            foreach (ViewSlot slot in viewSlots)
            {
                if (!slot.InUse || slot.IsSceneView || slot.Camera == null || slot.LastFrame <= bestFrame) continue;
                best = slot.Camera;
                bestFrame = slot.LastFrame;
            }
            if (best == null) best = Camera.main;
            return best != null ? best.transform.position : Vector3.zero;
        }

        /// <summary>Destroys the capture texture (the next view packet releases the plugin's views of it).</summary>
        private void ReleaseSkyCaptureTexture()
        {
            if (skyCaptureTexture == null) return;
            if (ReferenceEquals(skyTexture, skyCaptureTexture))
            {
                skyTexture = null;
                skyTexturePtr = IntPtr.Zero;
            }
            skyCaptureTexture.Release();
            UnityEngine.Object.DestroyImmediate(skyCaptureTexture);
            skyCaptureTexture = null;
            skyCaptureValid = false;
            s_generation++;
        }

        /// <summary>Destroys the capture texture and the capture camera.</summary>
        private void ReleaseSkyCapture()
        {
            ReleaseSkyCaptureTexture();
            if (skyCaptureCamera != null)
            {
                UnityEngine.Object.DestroyImmediate(skyCaptureCamera.gameObject);
            }
            skyCaptureCamera = null;
            skyCaptureLogged = false;
        }

        #endregion

        /// <summary>
        /// The library camera of this frame. <paramref name="jitteredProjection"/> is URP's projection
        /// of the frame (<c>UniversalCameraData.GetProjectionMatrix()</c>): with TAA it is
        /// <c>Translate(o) * P</c>, which moves every projected point by o (NDC, y up, o = 2 * jitter /
        /// size). A pixel centre then sees the point that the unjittered projection puts o further
        /// back, so the library's sample offset is -o: in pixels (y down) jitter = ((P'02 - P02) * w / 2,
        /// -(P'12 - P12) * h / 2), because P'02 = P02 - o.x (P32 = -1) and likewise for y.
        /// Sign verified in Play Mode (P3): with this sign the TAA image of a static camera is sharp,
        /// with the opposite sign visibly blurred (Laplacian variance 169 vs 124).
        /// With an upscaler (<paramref name="upscale"/>) URP's jitter is ignored: the jitter is the
        /// library's Halton(2, 3) sequence over <see cref="Time.frameCount"/>, with the phase count
        /// the DLSS guide recommends for the render and output widths (render width unknown yet =
        /// ratio 1). The aspect and the projection stay those of the camera size: the render-size
        /// image covers the same frustum.
        /// <see cref="LastJitter"/> follows the Game cameras only (<paramref name="isGame"/>).
        /// </summary>
        private unsafe CaelixNativeCamera BuildCamera(Camera camera, Matrix4x4 jitteredProjection, int width,
            int height, bool isGame, in UpscalerRequest upscale)
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

            if (upscale.Upscaler != 0)
            {
                uint phases = CaelixNativeLibrary.UpscalerJitterPhases(upscale.Upscaler, upscale.RenderWidth, (uint)width);
                Vector2 halton = CaelixNativeLibrary.JitterHalton((uint)Time.frameCount, phases);
                result.jitter[0] = halton.x;
                result.jitter[1] = halton.y;
                if (isGame) LastJitter = halton;
                return result;
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

        /// <summary>
        /// The options of <paramref name="slot"/>, with its own mode, debug view, reset state and
        /// upscaler. <see cref="ResetAccumulation"/> also resets the upscaler's history (the library
        /// drops it with <c>reset_accumulation</c>).
        /// </summary>
        private unsafe CaelixNativeRenderOptions BuildOptions(ViewSlot slot, in CaelixNativeCamera camera,
            in UpscalerRequest upscale)
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
                voxelIdTMax = Mathf.Max(0.0f, voxelIdTMax),
                // sky stays zero: the plugin fills it from the view packet's sky target.
            };
            if (upscale.Upscaler != 0)
            {
                // Sent also while the render size is not known yet: that is how the plugin learns the key.
                o.upscaler = upscale.Upscaler;
                o.upscaleQuality = upscale.Quality;
                o.upscalePreset = (uint)Mathf.Clamp(upscalePreset, 0, 15);
                if (upscale.Upscaler == CaelixNativeBridge.UpscalerDlssSr && upscaleAutoExposure)
                {
                    o.upscaleFlags |= CaelixNativeBridge.UpscaleFlagAutoExposure;
                }
            }
            // The synced sun drives both modes; unsynced, the primary mode keeps its own Lambert direction.
            bool sunSynced = ResolveSun(out Vector3 sunDir, out Vector3 sunRad);
            Vector3 sun = lighting || sunSynced ? sunDir : primarySunDirection;
            o.sunDir[0] = sun.x; o.sunDir[1] = sun.y; o.sunDir[2] = sun.z;
            o.skyColor[0] = primarySkyColor.x; o.skyColor[1] = primarySkyColor.y; o.skyColor[2] = primarySkyColor.z;
            o.sunRadiance[0] = sunRad.x; o.sunRadiance[1] = sunRad.y; o.sunRadiance[2] = sunRad.z;
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
