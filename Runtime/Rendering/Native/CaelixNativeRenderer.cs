using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Caelix.Client;
using Caelix.Net;
using Caelix.Utils;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// Scene renderer backed by the native Vulkan plugin <c>caelix_unity</c> (Caelix-Renderer).
    /// </summary>
    /// <remarks>
    /// A view is one camera: every camera that renders through <see cref="CaelixNativeRendererFeature"/>
    /// gets its own view (up to <see cref="CaelixNativeBridge.MaxViews"/>), with its own plugin
    /// renderer (internal targets, denoiser history) over one shared scene. This component owns,
    /// per view, the four targets the plugin writes (colour RGBA16F, depth R32F, motion RGBA16F,
    /// albedo RGBA8 with coverage in alpha; the library writes them with
    /// <c>CAELIX_RENDER_FLAG_FLIP_Y</c>, so they have Unity's row order).
    /// <para>
    /// <see cref="Tick"/> (called once per frame by <see cref="CaelixHost"/> after the client's
    /// <c>PrepareRender</c>, like the ray query renderer) fills ONE frame packet with the scene
    /// records of the frame: entity records, brick removes, and brick uploads copied from the
    /// client store by a Burst job, as many as the plugin's staging ring can take (the status'
    /// <c>upload_bricks_free</c> minus the bricks of frame packets still in flight, capped at
    /// <c>upload_bricks_cap</c>). It issues the plugin's frame event with
    /// <see cref="Graphics.ExecuteCommandBuffer"/>. Unity's device command queue is FIFO, so the
    /// event runs on the render thread before the cameras of the frame: the plugin flushes the
    /// scene once per frame with Unity's own frame numbers, and every view renders the same scene
    /// state. Each camera render, the feature asks for a view packet (<see cref="TryPrepareFrame"/>):
    /// the view index, the camera (with URP's TAA jitter), the options, the view's targets and the
    /// sky cubemap; it issues the plugin's render event with it.
    /// </para>
    /// <para>
    /// Sessions: the scene's block encoding, its material table and its staging ring size are
    /// fixed for a session. A session starts at the first tick with a client world (and again for
    /// a new client world): a release event that destroys the plugin's scene and renderers, then a
    /// frame packet with the reset flag and the session config; every entity is sent as new. It
    /// ends when the component is disabled (another release event: the plugin holds no scene
    /// between Play sessions), or with an error when the plugin dropped bricks (the next tick
    /// starts a new session, which sends the world again). Changing <see cref="Encoding"/> or
    /// <see cref="MaterialsJson"/> during a session is an error; disable the component first.
    /// </para>
    /// <para>
    /// Packets live in unmanaged memory, in one ring per packet kind (frame, view, release). The
    /// render thread reads a packet during its event and then publishes the packet's sequence
    /// number for its kind; a slot is reused only after that. Brick data is COPIED into the frame
    /// packet in the same frame it is bound (the phase rule of the store), because the render
    /// thread runs after the next server tick may have changed the storage.
    /// </para>
    /// Options mirror the Rust host's <c>RenderSettings</c> (Caelix-Renderer/host-rs) and its
    /// command-line defaults.
    /// </remarks>
    [DisallowMultipleComponent]
    public class CaelixNativeRenderer : MonoBehaviour
    {
        public enum RenderMode
        {
            Primary = 0,
            PathTrace = 1,
            Reference = 2,
        }

        /// <summary>How Editor Scene view cameras are drawn.</summary>
        public enum SceneViewMode
        {
            /// <summary>Scene views are not drawn.</summary>
            Off = 0,
            /// <summary>The Game view's mode and debug view, with its own denoiser history.</summary>
            Lit = 1,
            /// <summary>The primary mode's albedo view: no denoiser, no history.</summary>
            Primary = 2,
        }

        public enum DebugView
        {
            Albedo = 0,
            Normal = 1,
            Depth = 2,
            Group = 3,
            Brick = 4,
            Steps = 5,
            NrdValidation = 6,
            NrdDiffuse = 7,
            Motion = 8,
            Material = 9,
            NrdSpecular = 10,
        }

        /// <summary>The block encoding the plugin's scene uses.</summary>
        public enum EncodingOverride
        {
            /// <summary>The build's encoding: <see cref="BlockEncoding.PackedSceneColor"/>.</summary>
            Auto = 0,
            /// <summary>Packed scene colour (caelix.h encoding 0).</summary>
            Packed = 1,
            /// <summary>Block-ID (caelix.h encoding 1).</summary>
            BlockId = 2,
        }

        // caelix.h render flags.
        private const uint FlagShading = 1u << 0;
        private const uint FlagSkyOnMiss = 1u << 1;
        private const uint FlagSunNee = 1u << 2;
        private const uint FlagDeltaCheckerboard = 1u << 3;
        /// <summary>Unity render textures store row 0 at the bottom; the library writes them flipped.</summary>
        private const uint FlagFlipY = 1u << 4;

        /// <summary>Frame packet slots: one packet per frame, the render thread may be a frame or two behind.</summary>
        private const int FrameRingSize = 3;
        /// <summary>Release packet slots (fixed size).</summary>
        private const int ReleaseRingSize = 4;
        /// <summary>View packet slots (fixed size): every view issues one per frame, two frames in flight.</summary>
        private const int ViewRingSize = (int)CaelixNativeBridge.MaxViews * 2 + 2;
        /// <summary>A view whose camera has not rendered for this many frames is released.</summary>
        private const int ViewIdleFrames = 300;

        /// <summary>Bytes of one brick in a packet: its key and its 512 block values.</summary>
        private const int BrickPacketBytes = 12 + 1024;

        [Header("Scene Feed")]
        [SerializeField, Tooltip("The host whose client world is drawn. Found in the scene when empty.")]
        private CaelixHost host;

        [SerializeField, Tooltip("Block encoding of the plugin's scene. Auto = the build's BlockEncoding.PackedSceneColor. " +
                                 "Fixed for a session: disable the component before changing it.")]
        private EncodingOverride encodingOverride = EncodingOverride.Auto;

        [SerializeField, Tooltip("Block-ID encoding only: material table entries (the Rust host's --materials JSON: id, " +
                                 "transparent, albedo, emission, smoothness, metallic, ior, extinction), applied over the " +
                                 "plugin's built-in table. Fixed for a session: disable the component before changing it.")]
        private TextAsset materialsJson;

        [SerializeField, Min(4), Tooltip("Staging ring of the plugin's scene in MiB. Applied when a session starts (Play start " +
                                         "or a reset); a change during a session takes effect at the next one.")]
        private int stagingRingMiB = 256;

        [SerializeField, Min(0), Tooltip("Logs the plugin status every N frames (0 = off).")]
        private int statusLogInterval = 0;

        [SerializeField, Tooltip("Logs stream completion, session starts and material table loads to the Console.")]
        private bool verboseLogging = false;

        [Header("Scene View")]
        [SerializeField, Tooltip("Editor Scene views: Off = not drawn; Lit = the same mode as the Game view with its own " +
                                 "denoiser history; Primary = the fast albedo debug view (no denoiser, no history). Costs " +
                                 "one more render per Scene view repaint.")]
        private SceneViewMode sceneView = SceneViewMode.Lit;

        [Header("Mode")]
        [SerializeField] private RenderMode mode = RenderMode.PathTrace;
        [SerializeField, Tooltip("Primary mode: views 0..5 and 9. Path trace mode: views 6, 7, 8, 10.")]
        private DebugView debugView = DebugView.Albedo;
        [SerializeField, Range(0, 14)] private int bounces = 4;
        [SerializeField, Range(0, 15)] private int bouncesTransparent = 5;
        [SerializeField] private bool denoise = true;
        [SerializeField] private bool sunNextEventEstimation = true;
        [SerializeField] private bool deltaCheckerboard = true;
        [SerializeField, Tooltip("Lighting modes: show the sky where the primary ray misses.")]
        private bool skyOnMiss = true;
        [SerializeField, Tooltip("Primary mode: Lambert shading of the albedo view.")]
        private bool shading = true;

        [Header("Exposure, Sun and Sky (linear RGB)")]
        [SerializeField, Min(0.0f)] private float exposure = 1.0f;
        [SerializeField, Tooltip("Lighting modes: direction toward the sun (normalized by the library).")]
        private Vector3 sunDirection = new Vector3(0.45f, 0.7f, 0.55f);
        [SerializeField, Tooltip("Primary mode: direction of the Lambert shading.")]
        private Vector3 primarySunDirection = new Vector3(-0.45f, 0.8f, -0.3f);
        [SerializeField, Tooltip("Radiance of the sun disk. 0 = sun off (sky light only). Suggested 2e4, 1.9e4, 1.8e4.")]
        private Vector3 sunRadiance = Vector3.zero;
        [SerializeField, Min(0.0f)] private float sunAngularRadiusDegrees = 0.5f;
        [SerializeField] private Vector3 skyZenith = new Vector3(0.2f, 0.4f, 0.9f);
        [SerializeField] private Vector3 skyHorizon = new Vector3(0.7f, 0.8f, 0.95f);
        [SerializeField] private Vector3 skyGround = new Vector3(0.25f, 0.22f, 0.2f);
        [SerializeField, Tooltip("Primary mode: the colour of rays that hit nothing.")]
        private Vector3 primarySkyColor = new Vector3(0.55f, 0.7f, 0.9f);

        [Header("Sky Cubemap and Fog")]
        [SerializeField, Tooltip("Cubemap sampled as the sky of the lighting modes (a Cubemap or a cube RenderTexture with " +
                                 "mips). Must not contain the sun disk: the sun stays the Sun settings. Empty = the procedural sky.")]
        private Texture skyCubemap;
        [SerializeField, Tooltip("When no cubemap is set, use ReflectionProbe.defaultTexture when it is a cubemap.")]
        private bool skyFromReflectionProbe = false;
        [SerializeField, Min(0.0f), Tooltip("Lighting modes: extinction of the air per world unit (0 = no fog). Rays that " +
                                            "leave the scene are not fogged: choose a fog colour near the sky's horizon colour.")]
        private float fogDensity = 0.0f;
        [SerializeField, ColorUsage(false, true), Tooltip("Radiance of the fog, linear RGB (the exposure applies).")]
        private Color fogColor = new Color(0.7f, 0.8f, 0.95f, 1.0f);
        [SerializeField, Min(0.0f), Tooltip("Every mode: multiplies the emission of every material (0 is treated as 1).")]
        private float emissionScale = 1.0f;

        /// <summary>The enabled instance the renderer feature draws. One at a time.</summary>
        public static CaelixNativeRenderer Active { get; private set; }

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
            RenderMode viewMode = ModeForView(view);
            return viewMode == RenderMode.Primary ||
                   (viewMode == RenderMode.PathTrace && DebugViewForView(view) == DebugView.Motion);
        }

        /// <summary>The render mode of <paramref name="view"/>: a Scene view camera may use the primary mode.</summary>
        private RenderMode ModeForView(int view) => ModeFor(viewSlots[view].IsSceneView);

        private RenderMode ModeFor(bool isSceneView) =>
            isSceneView && sceneView == SceneViewMode.Primary ? RenderMode.Primary : mode;

        /// <summary>The debug view of <paramref name="view"/>: albedo for a Scene view camera in the primary mode.</summary>
        private DebugView DebugViewForView(int view) => DebugViewFor(viewSlots[view].IsSceneView);

        private DebugView DebugViewFor(bool isSceneView) =>
            isSceneView && sceneView == SceneViewMode.Primary ? DebugView.Albedo : debugView;

        /// <summary>The jitter of the newest prepared Game camera frame, in pixels (x right, y down).</summary>
        public Vector2 LastJitter { get; private set; }

        /// <summary>View packets issued since this component was enabled.</summary>
        public ulong PacketsIssued { get; private set; }

        /// <summary>Camera renders skipped: no free view, or no free view or release packet slot.</summary>
        public ulong PacketsSkipped { get; private set; }

        /// <summary>Ticks that issued no frame packet because every frame slot was still waiting for the render thread.</summary>
        public ulong TicksDeferred { get; private set; }

        /// <summary>CPU time of the newest <see cref="Tick"/>, in milliseconds.</summary>
        public double LastTickMilliseconds { get; private set; }

        /// <summary>Bricks waiting on the C# side (not copied into a frame packet yet), over every entity.</summary>
        public long BricksPendingCSharp { get; private set; }

        /// <summary>Bricks copied into frame packets since this component was enabled.</summary>
        public long BricksSentTotal { get; private set; }

        /// <summary>The block encoding of the live session (0 packed, 1 block-ID); <see cref="CaelixNativeBridge.NoScene"/> without one.</summary>
        public uint SceneEncoding => sessionLive ? sessionEncoding : CaelixNativeBridge.NoScene;

        /// <summary>True between the start of a session (the reset frame packet was issued) and its end.</summary>
        public bool SessionLive => sessionLive;

        public RenderMode Mode { get => mode; set => mode = value; }
        public SceneViewMode SceneView { get => sceneView; set => sceneView = value; }
        public DebugView View { get => debugView; set => debugView = value; }
        public int Bounces { get => bounces; set => bounces = Mathf.Clamp(value, 0, 14); }
        public int BouncesTransparent { get => bouncesTransparent; set => bouncesTransparent = Mathf.Clamp(value, 0, 15); }
        public bool Denoise { get => denoise; set => denoise = value; }
        public bool SunNextEventEstimation { get => sunNextEventEstimation; set => sunNextEventEstimation = value; }
        public bool DeltaCheckerboard { get => deltaCheckerboard; set => deltaCheckerboard = value; }
        public bool SkyOnMiss { get => skyOnMiss; set => skyOnMiss = value; }
        public bool Shading { get => shading; set => shading = value; }
        public float Exposure { get => exposure; set => exposure = Mathf.Max(0f, value); }
        public Vector3 SunDirection { get => sunDirection; set => sunDirection = value; }
        public Vector3 PrimarySunDirection { get => primarySunDirection; set => primarySunDirection = value; }
        public Vector3 SunRadiance { get => sunRadiance; set => sunRadiance = value; }
        public float SunAngularRadiusDegrees { get => sunAngularRadiusDegrees; set => sunAngularRadiusDegrees = Mathf.Max(0f, value); }
        public Vector3 SkyZenith { get => skyZenith; set => skyZenith = value; }
        public Vector3 SkyHorizon { get => skyHorizon; set => skyHorizon = value; }
        public Vector3 SkyGround { get => skyGround; set => skyGround = value; }
        public Vector3 PrimarySkyColor { get => primarySkyColor; set => primarySkyColor = value; }
        public Texture SkyCubemap { get => skyCubemap; set => skyCubemap = value; }
        public bool SkyFromReflectionProbe { get => skyFromReflectionProbe; set => skyFromReflectionProbe = value; }
        public float FogDensity { get => fogDensity; set => fogDensity = Mathf.Max(0f, value); }
        public Color FogColor { get => fogColor; set => fogColor = value; }
        public float EmissionScale { get => emissionScale; set => emissionScale = Mathf.Max(0f, value); }

        /// <summary>
        /// Staging ring of the plugin's scene in MiB. Applied when a session starts; a change during
        /// a session takes effect at the next one.
        /// </summary>
        public int StagingRingMiB { get => stagingRingMiB; set => stagingRingMiB = Mathf.Max(4, value); }

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

        /// <summary>Block encoding of the plugin's scene. Fixed for a session: setting another value then throws.</summary>
        public EncodingOverride Encoding
        {
            get => encodingOverride;
            set
            {
                if (sessionLive && value != sessionEncodingOverride)
                {
                    throw new InvalidOperationException(
                        "CaelixNativeRenderer.Encoding is fixed for the session; disable the component first.");
                }
                encodingOverride = value;
            }
        }

        /// <summary>Material entries of the block-ID encoding. Fixed for a session: setting another asset then throws.</summary>
        public TextAsset MaterialsJson
        {
            get => materialsJson;
            set
            {
                if (sessionLive && !ReferenceEquals(value, sessionMaterialsAsset))
                {
                    throw new InvalidOperationException(
                        "CaelixNativeRenderer.MaterialsJson is fixed for the session; disable the component first.");
                }
                materialsJson = value;
            }
        }

        public int StatusLogInterval { get => statusLogInterval; set => statusLogInterval = Mathf.Max(0, value); }
        public bool VerboseLogging { get => verboseLogging; set => verboseLogging = value; }

        /// <summary>The host whose client world this renderer draws, once resolved.</summary>
        public CaelixHost Host => host;

        // ---------------- views ----------------
        /// <summary>One render texture the plugin writes, with its native pointers.</summary>
        private sealed class NativeTarget
        {
            public RenderTexture Texture;
            public RTHandle Handle;
            public IntPtr TexturePtr;
            public IntPtr RenderBufferPtr;

            public bool Matches(int width, int height) =>
                Texture != null && Texture.width == width && Texture.height == height && Texture.IsCreated();

            public void Create(string name, GraphicsFormat format, int width, int height)
            {
                Texture = new RenderTexture(width, height, format, GraphicsFormat.None)
                {
                    name = name,
                    enableRandomWrite = true,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                Texture.Create();
                Handle = RTHandles.Alloc(Texture);
                // GetNativeTexturePtr may synchronize with the render thread: once per target.
                TexturePtr = Texture.GetNativeTexturePtr();
                RenderBufferPtr = Texture.colorBuffer.GetNativeRenderBufferPtr();
            }

            public void Release()
            {
                if (Handle != null)
                {
                    RTHandles.Release(Handle);
                    Handle = null;
                }
                if (Texture != null)
                {
                    Texture.Release();
                    DestroyImmediate(Texture);
                    Texture = null;
                }
                TexturePtr = IntPtr.Zero;
                RenderBufferPtr = IntPtr.Zero;
            }

            public CaelixNativeTarget ToPacket(int width, int height) => new CaelixNativeTarget
            {
                nativeTexture = TexturePtr,
                nativeRenderBuffer = RenderBufferPtr,
                width = (uint)width,
                height = (uint)height,
            };
        }

        /// <summary>
        /// The four targets of one view at the view's camera size: colour RGBA16F, depth R32F, motion
        /// RGBA16F, albedo RGBA8 UNORM (the library's accepted formats), all with random write.
        /// </summary>
        private sealed class ViewTargets
        {
            public readonly NativeTarget Color = new NativeTarget();
            public readonly NativeTarget Depth = new NativeTarget();
            public readonly NativeTarget Motion = new NativeTarget();
            public readonly NativeTarget Albedo = new NativeTarget();

            public bool Matches(int width, int height) =>
                Color.Matches(width, height) && Depth.Matches(width, height) &&
                Motion.Matches(width, height) && Albedo.Matches(width, height);

            /// <summary>Recreates all four when any does not match the size. True when they were replaced.</summary>
            public bool Ensure(int width, int height, int view)
            {
                if (Matches(width, height)) return false;
                Release();
                Color.Create($"Caelix Native Colour (view {view})", GraphicsFormat.R16G16B16A16_SFloat, width, height);
                Depth.Create($"Caelix Native Depth (view {view})", GraphicsFormat.R32_SFloat, width, height);
                Motion.Create($"Caelix Native Motion (view {view})", GraphicsFormat.R16G16B16A16_SFloat, width, height);
                Albedo.Create($"Caelix Native Albedo (view {view})", GraphicsFormat.R8G8B8A8_UNorm, width, height);
                return true;
            }

            public void Release()
            {
                Color.Release();
                Depth.Release();
                Motion.Release();
                Albedo.Release();
            }
        }

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
            public readonly ViewTargets Targets = new ViewTargets();
            public readonly ViewFrameState State = new ViewFrameState();
        }

        private readonly ViewSlot[] viewSlots = CreateViewSlots();

        private static ViewSlot[] CreateViewSlots()
        {
            var slots = new ViewSlot[CaelixNativeBridge.MaxViews];
            for (int i = 0; i < slots.Length; i++) slots[i] = new ViewSlot();
            return slots;
        }

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

        // ---------------- packets ----------------
        private static readonly int FramePacketBytes = Marshal.SizeOf<CaelixNativeFramePacket>();
        private static readonly int ViewPacketBytes = Marshal.SizeOf<CaelixNativeViewPacket>();
        private static readonly int ReleasePacketBytes = Marshal.SizeOf<CaelixNativeReleasePacket>();

        /// <summary>Frame packets: grown on demand to hold the records of one frame.</summary>
        private readonly IntPtr[] frameSlots = new IntPtr[FrameRingSize];
        private readonly long[] frameCapacity = new long[FrameRingSize];
        private readonly ulong[] frameSequence = new ulong[FrameRingSize];
        private readonly long[] frameBricks = new long[FrameRingSize];

        /// <summary>View packets: <see cref="ViewRingSize"/> fixed slots in one block.</summary>
        private IntPtr viewRing;
        private readonly ulong[] viewSequence = new ulong[ViewRingSize];

        /// <summary>Release packets: <see cref="ReleaseRingSize"/> fixed slots in one block.</summary>
        private IntPtr releaseRing;
        private readonly ulong[] releaseSequence = new ulong[ReleaseRingSize];

        /// <summary>Carries the frame event (and the release events of the tick) to <see cref="Graphics.ExecuteCommandBuffer"/>.</summary>
        private CommandBuffer frameCommands;
        private float lastDeferredWarningTime = float.NegativeInfinity;

        // ---------------- session ----------------
        private bool sessionLive;
        private EncodingOverride sessionEncodingOverride;
        private uint sessionEncoding;
        private TextAsset sessionMaterialsAsset;
        /// <summary>Material entries of the session (block-ID encoding only), sent with the reset.</summary>
        private CaelixNativeMaterialEntry[] sessionMaterials;
        private int sessionStagingMiB;
        /// <summary>Sequence of the session's reset frame packet.</summary>
        private ulong sessionResetSequence;
        /// <summary>The plugin consumed the reset packet: the status describes this session's scene.</summary>
        private bool sessionReady;
        private ulong sessionDroppedBaseline;
        private bool sessionConfigLogged;
        private bool sessionEncodingLogged;
        private bool sessionStagingLogged;

        // ---------------- scene feed ----------------
        private ClientWorld source;
        private readonly Dictionary<EntityView, EntityState> entities = new Dictionary<EntityView, EntityState>();
        private readonly List<EntityState> entityOrder = new List<EntityState>();
        private readonly List<Guid128> pendingEntityRemoves = new List<Guid128>();
        private readonly List<EntityView> removedScratch = new List<EntityView>();
        private NativeHashSet<int3> frameDedupe;
        private int entityStamp;
        private int lastTickFrame = -1;

        // Stream tracking (logs one line when a burst of uploads has become traceable).
        private bool streaming;
        private int streamStartFrame;
        private long streamStartTicks;
        private double streamTickMsTotal;
        private double streamTickMsMax;
        private int streamTicks;
        private long streamBricks;
        private ulong settleFrames;

        // Shared by every instance: sequence numbers and target generations must never repeat
        // within one plugin lifetime. The plugin outlives components and domain reloads; a domain
        // reload resets these to 0, so every packet kind continues its counter above the plugin's
        // last consumed sequence of that kind (slot reuse depends on it). Generations only feed a
        // warning of the plugin.
        private static ulong s_frameSequence;
        private static ulong s_viewSequence;
        private static ulong s_releaseSequence;
        private static uint s_generation;
        private static uint s_releasedGeneration;
        private static readonly List<(IntPtr memory, uint kind, ulong sequence)> s_deferredFree =
            new List<(IntPtr, uint, ulong)>();

        /// <summary>What the plugin knows of one client entity view.</summary>
        private sealed class EntityState
        {
            public EntityView View;
            public Guid128 Guid;
            public bool NeedsAdd = true;
            public RigidTransform SentTransform;
            /// <summary>Keys to upload, oldest first; [Head, Length) are still waiting.</summary>
            public UnsafeList<int3> PendingUploads;
            public int Head;
            public UnsafeList<int3> PendingRemoves;
            public int Stamp;
            public int PacketIndex = -1;

            public int UploadsWaiting => PendingUploads.Length - Head;

            public void Dispose()
            {
                if (PendingUploads.IsCreated) PendingUploads.Dispose();
                if (PendingRemoves.IsCreated) PendingRemoves.Dispose();
            }
        }

        private void OnEnable()
        {
            if (Active != null && Active != this)
            {
                Debug.LogWarning($"Caelix native: {name} replaces {Active.name} as the active native renderer.", this);
            }
            Active = this;
            for (int i = 0; i < FrameRingSize; i++)
            {
                frameSlots[i] = IntPtr.Zero;
                frameCapacity[i] = 0;
                frameSequence[i] = 0;
                frameBricks[i] = 0;
            }
            viewRing = Marshal.AllocHGlobal(ViewRingSize * ViewPacketBytes);
            Array.Clear(viewSequence, 0, viewSequence.Length);
            releaseRing = Marshal.AllocHGlobal(ReleaseRingSize * ReleasePacketBytes);
            Array.Clear(releaseSequence, 0, releaseSequence.Length);
            frameCommands = new CommandBuffer { name = "Caelix Native Frame" };
            for (int v = 0; v < viewSlots.Length; v++) FreeViewSlot(v);
            frameDedupe = new NativeHashSet<int3>(1024, Allocator.Persistent);
            sessionLive = false;
            streaming = false;
        }

        private void OnDisable()
        {
            if (Active == this) Active = null;
            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            FreeDeferred(status);
            ContinueSequences(status);
            if (sessionLive)
            {
                // The session ends: every renderer and the scene go away, the plugin holds nothing
                // between Play sessions. Issued before the targets below are released.
                IntPtr release = FillRelease(CaelixNativeBridge.ReleaseScene, 0);
                if (release == IntPtr.Zero)
                {
                    // Every ring slot is in flight and there is no next tick to wait for: a block of its own.
                    release = Marshal.AllocHGlobal(ReleasePacketBytes);
                    ulong sequence = WriteRelease(release, CaelixNativeBridge.ReleaseScene, 0);
                    s_deferredFree.Add((release, CaelixNativeBridge.PacketRelease, sequence));
                }
                frameCommands.Clear();
                frameCommands.IssuePluginEventAndData(CaelixNativeBridge.RenderEventFunc, status.releaseEventId, release);
                Graphics.ExecuteCommandBuffer(frameCommands);
                sessionLive = false;
            }
            frameCommands?.Release();
            frameCommands = null;

            // Packet memory the render thread may still read is freed once it reports the sequence.
            for (int i = 0; i < FrameRingSize; i++)
            {
                FreeOrDefer(frameSlots[i], CaelixNativeBridge.PacketFrame, frameSequence[i], status);
                frameSlots[i] = IntPtr.Zero;
                frameCapacity[i] = 0;
                frameSequence[i] = 0;
                frameBricks[i] = 0;
            }
            FreeOrDefer(viewRing, CaelixNativeBridge.PacketView, Max(viewSequence), status);
            viewRing = IntPtr.Zero;
            FreeOrDefer(releaseRing, CaelixNativeBridge.PacketRelease, Max(releaseSequence), status);
            releaseRing = IntPtr.Zero;

            for (int v = 0; v < viewSlots.Length; v++) FreeViewSlot(v);
            UnbindSource();
            if (frameDedupe.IsCreated) frameDedupe.Dispose();
        }

        /// <summary>Reverts a session-fixed field that the Inspector changed during a session.</summary>
        private void OnValidate()
        {
            if (!Application.isPlaying || !sessionLive) return;
            if (encodingOverride != sessionEncodingOverride)
            {
                Debug.LogError($"Caelix native: the encoding is fixed for the session; reverted to {sessionEncodingOverride}. " +
                               "Disable the component first.", this);
                encodingOverride = sessionEncodingOverride;
            }
            if (!ReferenceEquals(materialsJson, sessionMaterialsAsset))
            {
                Debug.LogError("Caelix native: the material JSON is fixed for the session; reverted. Disable the component first.", this);
                materialsJson = sessionMaterialsAsset;
            }
        }

        #region Scene feed

        private uint ResolveEncoding()
        {
            switch (encodingOverride)
            {
                case EncodingOverride.Packed: return 0u;
                case EncodingOverride.BlockId: return 1u;
                default: return BlockEncoding.PackedSceneColor ? 0u : 1u;
            }
        }

        /// <summary>Binds the host's client world. <paramref name="rebound"/> is true when a world was bound now.</summary>
        private bool EnsureSource(out bool rebound)
        {
            rebound = false;
            if (source != null && !source.IsDisposed) return true;
            if (host == null) host = CaelixHost.Current;
            if (host == null) return false;
            host.EnsureInitialized();
            ClientWorld world = host.ClientWorld;
            if (world == null || world.IsDisposed) return false;
            UnbindSource();
            source = world;
            rebound = true;
            return true;
        }

        private void UnbindSource()
        {
            ForgetEntities();
            source = null;
        }

        /// <summary>Forgets every entity; the next diff sends them all as new.</summary>
        private void ForgetEntities()
        {
            foreach (EntityState state in entityOrder) state.Dispose();
            entities.Clear();
            entityOrder.Clear();
            pendingEntityRemoves.Clear();
        }

        /// <summary>
        /// Collects this frame's scene changes and issues the frame event with them; starts a
        /// session first when none is live. Call once per frame after the client's
        /// <c>PrepareRender</c> and before <c>EndFrame</c>: the change lists are read here and
        /// nowhere cleared.
        /// </summary>
        public void Tick()
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                TickInner();
            }
            finally
            {
                LastTickMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                if (streaming)
                {
                    streamTicks++;
                    streamTickMsTotal += LastTickMilliseconds;
                    streamTickMsMax = Math.Max(streamTickMsMax, LastTickMilliseconds);
                }
            }
        }

        private void TickInner()
        {
            if (lastTickFrame == Time.frameCount)
            {
                Debug.LogWarning($"CaelixNativeRenderer: Tick ran twice in frame {Time.frameCount}; the change list is " +
                                 "read once per frame, so the second run sends nothing new.", this);
                return;
            }
            lastTickFrame = Time.frameCount;
            if (!CaelixNativeBridge.IsAvailable) return;
            if (!EnsureSource(out bool rebound)) return;
            if (rebound && sessionLive)
            {
                // A new client world: the session start below releases the old scene.
                if (verboseLogging)
                {
                    Debug.Log("Caelix native: a new client world; the session restarts and the world is sent again.", this);
                }
                sessionLive = false;
            }

            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            FreeDeferred(status);
            ContinueSequences(status);
            if (sessionLive && !CheckSession(status)) return;

            bool starting = !sessionLive;
            int frameSlot = FindFreeFrameSlot(status);
            if (starting)
            {
                if (frameSlot < 0 || !HasFreeRelease(status))
                {
                    TicksDeferred++;
                    WarnDeferred("the session start waits for a free packet slot");
                    return;
                }
                BeginSession();
            }

            CollectChanges();
            TrackStream(status);
            if (statusLogInterval > 0 && Time.frameCount % statusLogInterval == 0)
            {
                LogStatus(status);
            }

            frameCommands.Clear();
            bool issue = false;
            IntPtr eventFunc = CaelixNativeBridge.RenderEventFunc;
            if (starting)
            {
                // Destroys the previous scene and every renderer (when there are any); the reset
                // frame packet below creates the new scene.
                IntPtr release = FillRelease(CaelixNativeBridge.ReleaseScene, 0);
                frameCommands.IssuePluginEventAndData(eventFunc, status.releaseEventId, release);
                issue = true;
            }

            // Views whose camera is gone or idle: their renderers go (a scene release covers them).
            idleViewScratch.Clear();
            for (int v = 0; v < viewSlots.Length; v++)
            {
                ViewSlot slot = viewSlots[v];
                if (!slot.InUse) continue;
                if (slot.Camera != null && Time.frameCount - slot.LastFrame <= ViewIdleFrames) continue;
                if (!starting)
                {
                    IntPtr release = FillRelease(CaelixNativeBridge.ReleaseView, (uint)v);
                    // No free release slot: wait for the next tick.
                    if (release == IntPtr.Zero) break;
                    frameCommands.IssuePluginEventAndData(eventFunc, status.releaseEventId, release);
                    issue = true;
                }
                idleViewScratch.Add(v);
            }

            bool frameIssued = false;
            if (frameSlot >= 0)
            {
                if (FillFramePacket(frameSlot, status, starting))
                {
                    frameCommands.IssuePluginEventAndData(eventFunc, status.frameEventId, frameSlots[frameSlot]);
                    issue = true;
                    frameIssued = true;
                }
            }
            else
            {
                // Everything collected waits in the entity states for the next frame packet.
                TicksDeferred++;
                WarnDeferred("no frame packet slot is free; the scene records of this frame wait");
            }

            if (issue) Graphics.ExecuteCommandBuffer(frameCommands);
            // After the release events: the plugin drops its views of the targets before Unity destroys them.
            foreach (int v in idleViewScratch) FreeViewSlot(v);
            if (starting && frameIssued)
            {
                sessionLive = true;
                // The scene release also released every target view.
                s_releasedGeneration = s_generation;
                if (verboseLogging)
                {
                    Debug.Log($"Caelix native: session started: encoding {sessionEncoding}, staging ring {sessionStagingMiB} MiB, " +
                              $"{(sessionMaterials != null ? sessionMaterials.Length : 0)} material entries, " +
                              $"{entityOrder.Count} entities, {BricksPendingCSharp} bricks to send.", this);
                }
            }
        }

        /// <summary>Fixes the session config and forgets every entity: the world is sent again.</summary>
        private void BeginSession()
        {
            sessionEncodingOverride = encodingOverride;
            sessionEncoding = ResolveEncoding();
            sessionMaterialsAsset = materialsJson;
            sessionMaterials = sessionEncoding == 1u ? ParseMaterials(materialsJson) : null;
            if (sessionEncoding != 1u && materialsJson != null && verboseLogging)
            {
                Debug.Log($"Caelix native: {materialsJson.name} is ignored: the packed encoding has no material table.", this);
            }
            sessionStagingMiB = Mathf.Max(4, stagingRingMiB);
            sessionResetSequence = ulong.MaxValue;
            sessionReady = false;
            sessionDroppedBaseline = 0;
            sessionConfigLogged = false;
            sessionEncodingLogged = false;
            sessionStagingLogged = false;
            ForgetEntities();
        }

        /// <summary>
        /// The tripwires of a live session. False when the session ended (the plugin dropped
        /// bricks); the next tick starts a new one.
        /// </summary>
        private bool CheckSession(in CaelixNativeStatus status)
        {
            if (!sessionConfigLogged &&
                (ResolveEncoding() != sessionEncoding || !ReferenceEquals(materialsJson, sessionMaterialsAsset)))
            {
                sessionConfigLogged = true;
                Debug.LogError("Caelix native: the encoding or the material JSON changed during the session; the session " +
                               "keeps its own. Disable the component before changing them.", this);
            }
            if (!sessionStagingLogged && stagingRingMiB != sessionStagingMiB)
            {
                sessionStagingLogged = true;
                if (verboseLogging)
                {
                    Debug.Log($"Caelix native: staging ring {stagingRingMiB} MiB applies at the next session (this one has " +
                              $"{sessionStagingMiB} MiB).", this);
                }
            }

            // Until the plugin consumed the reset packet, the status describes the previous scene.
            if (status.lastConsumedFrameSequence < sessionResetSequence) return true;
            if (!sessionReady)
            {
                // Frame packets of an earlier session were consumed before the reset: their drops
                // are in this baseline, and this session sends no bricks before it is ready.
                sessionReady = true;
                sessionDroppedBaseline = status.bricksDropped;
            }
            if (!sessionEncodingLogged && status.sceneEncoding != CaelixNativeBridge.NoScene &&
                status.sceneEncoding != sessionEncoding)
            {
                sessionEncodingLogged = true;
                Debug.LogError($"Caelix native: the plugin's scene has encoding {status.sceneEncoding}, the session " +
                               $"{sessionEncoding}.", this);
            }
            if (status.bricksDropped > sessionDroppedBaseline)
            {
                Debug.LogError($"Caelix native: the plugin dropped {status.bricksDropped - sessionDroppedBaseline} bricks " +
                               "(its staging ring refused them); the session restarts and the world is sent again.", this);
                sessionLive = false;
                return false;
            }
            return true;
        }

        private void WarnDeferred(string reason)
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastDeferredWarningTime < 5.0f) return;
            lastDeferredWarningTime = now;
            Debug.LogWarning($"Caelix native: {reason} ({TicksDeferred} deferred ticks so far).", this);
        }

        /// <summary>Diffs the entity views and reads their change lists into the entity states.</summary>
        private void CollectChanges()
        {
            IReadOnlyList<EntityView> list = source.Views;
            entityStamp++;
            for (int v = 0; v < list.Count; v++)
            {
                EntityView view = list[v];
                if (!entities.TryGetValue(view, out EntityState state))
                {
                    state = new EntityState
                    {
                        View = view,
                        Guid = view.Guid,
                        PendingUploads = new UnsafeList<int3>(64, Allocator.Persistent),
                        PendingRemoves = new UnsafeList<int3>(16, Allocator.Persistent),
                    };
                    // Everything the view already has; its change list this frame is covered by it.
                    foreach (int3 key in view.Data.EnumerateBricks())
                    {
                        state.PendingUploads.Add(key);
                    }
                    entities.Add(view, state);
                    entityOrder.Add(state);
                    state.Stamp = entityStamp;
                    continue;
                }
                state.Stamp = entityStamp;

                int count = view.Data.ChangeCount;
                if (count == 0) continue;
                NativeArray<BrickChange>.ReadOnly changes = view.Data.Changes;
                frameDedupe.Clear();
                for (int i = 0; i < changes.Length; i++)
                {
                    BrickChange change = changes[i];
                    if (change.Kind == ChangeKind.Removed)
                    {
                        state.PendingRemoves.Add(change.Key);
                        // A later Updated entry of the same key (removed, then allocated again)
                        // must still be uploaded.
                        frameDedupe.Remove(change.Key);
                    }
                    else if ((change.DirtyFlags & BrickReplication.ReplicationDirtyMask) != 0 && frameDedupe.Add(change.Key))
                    {
                        state.PendingUploads.Add(change.Key);
                    }
                }
            }

            // Views that are gone: one remove record each. Their data may be disposed already.
            removedScratch.Clear();
            foreach (var kvp in entities)
            {
                if (kvp.Value.Stamp != entityStamp) removedScratch.Add(kvp.Key);
            }
            foreach (EntityView view in removedScratch)
            {
                EntityState state = entities[view];
                // A view that never reached the plugin needs no remove.
                if (!state.NeedsAdd) pendingEntityRemoves.Add(state.Guid);
                state.Dispose();
                entities.Remove(view);
                entityOrder.Remove(state);
            }

            long waiting = 0;
            foreach (EntityState state in entityOrder) waiting += state.UploadsWaiting;
            BricksPendingCSharp = waiting;
        }

        private static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

        /// <summary>Bricks of issued frame packets the render thread has not consumed yet.</summary>
        private long BricksInFlight(in CaelixNativeStatus status)
        {
            long bricks = 0;
            for (int i = 0; i < FrameRingSize; i++)
            {
                if (frameSequence[i] > status.lastConsumedFrameSequence) bricks += frameBricks[i];
            }
            return bricks;
        }

        /// <summary>
        /// Bricks the next frame packet may carry: the staging ring's free upload capacity after the
        /// newest flush, minus the bricks of frame packets issued since, capped at the per-packet
        /// cap. 0 before the plugin created this session's scene.
        /// </summary>
        private long UploadBudgetBricks(in CaelixNativeStatus status)
        {
            if (!sessionReady || status.sceneEncoding == CaelixNativeBridge.NoScene) return 0;
            long free = (long)Math.Min(status.uploadBricksFree, long.MaxValue) - BricksInFlight(status);
            long budget = Math.Min(free, (long)Math.Min(status.uploadBricksCap, long.MaxValue));
            return budget > 0 ? budget : 0;
        }

        /// <summary>
        /// Fills frame packet <paramref name="slot"/> with this frame's records (and, with
        /// <paramref name="reset"/>, the session config). False when the packet would be too large;
        /// nothing was consumed then.
        /// </summary>
        private unsafe bool FillFramePacket(int slot, in CaelixNativeStatus status, bool reset)
        {
            long budget = UploadBudgetBricks(status) * BrickPacketBytes;

            // Entity records: removes first (the plugin applies them in list order), then the views
            // that have something to say.
            int entityCount = pendingEntityRemoves.Count;
            int removeCount = 0;
            int uploadEntities = 0;
            foreach (EntityState state in entityOrder)
            {
                state.PacketIndex = -1;
                bool moved = !state.NeedsAdd && !TransformEquals(state.SentTransform, state.View.Data.transform);
                bool uploads = budget > 0 && state.UploadsWaiting > 0;
                if (state.NeedsAdd || moved || state.PendingRemoves.Length > 0 || uploads)
                {
                    state.PacketIndex = entityCount++;
                }
                removeCount += state.PendingRemoves.Length;
                if (uploads) uploadEntities++;
            }

            // Material entries go with the reset only (the table is fixed for the session).
            int materialCount = reset && sessionMaterials != null ? sessionMaterials.Length : 0;
            int materialBytes = materialCount * sizeof(CaelixNativeMaterialEntry);

            int materialsOffset = Align(sizeof(CaelixNativeFramePacket), 16);
            int entitiesOffset = Align(materialsOffset + materialBytes, 16);
            int removesOffset = Align(entitiesOffset + entityCount * sizeof(CaelixNativeEntity), 16);
            int uploadsOffset = Align(removesOffset + removeCount * sizeof(CaelixNativeBrickRemove), 16);
            int dataOffset = Align(uploadsOffset + uploadEntities * sizeof(CaelixNativeUpload), 16);
            long required = dataOffset + (budget > 0 ? budget + 16L * uploadEntities + 16 : 0);
            if (required > int.MaxValue)
            {
                Debug.LogError($"Caelix native: frame packet would need {required} bytes; lower the staging ring.", this);
                foreach (EntityState state in entityOrder) state.PacketIndex = -1;
                return false;
            }
            EnsureFrameCapacity(slot, required);
            byte* packet = (byte*)frameSlots[slot];

            // Materials.
            if (materialCount > 0)
            {
                fixed (CaelixNativeMaterialEntry* src = sessionMaterials)
                {
                    UnsafeUtility.MemCpy(packet + materialsOffset, src, materialBytes);
                }
            }

            // Entities.
            var entityRecords = (CaelixNativeEntity*)(packet + entitiesOffset);
            int e = 0;
            foreach (Guid128 guid in pendingEntityRemoves)
            {
                entityRecords[e] = default;
                WriteGuid(ref entityRecords[e], guid);
                entityRecords[e].flags = CaelixNativeBridge.EntityRemove;
                e++;
            }
            foreach (EntityState state in entityOrder)
            {
                if (state.PacketIndex < 0) continue;
                ref CaelixNativeEntity record = ref entityRecords[state.PacketIndex];
                record = default;
                WriteGuid(ref record, state.Guid);
                RigidTransform t = state.View.Data.transform;
                record.position[0] = t.pos.x; record.position[1] = t.pos.y; record.position[2] = t.pos.z;
                record.rotation[0] = t.rot.value.x; record.rotation[1] = t.rot.value.y;
                record.rotation[2] = t.rot.value.z; record.rotation[3] = t.rot.value.w;
                if (state.NeedsAdd) record.flags = CaelixNativeBridge.EntityAdd;
                else if (!TransformEquals(state.SentTransform, t)) record.flags = CaelixNativeBridge.EntityTransform;
                state.SentTransform = t;
                state.NeedsAdd = false;
            }

            // Brick removes.
            var removes = (CaelixNativeBrickRemove*)(packet + removesOffset);
            int r = 0;
            foreach (EntityState state in entityOrder)
            {
                for (int i = 0; i < state.PendingRemoves.Length; i++)
                {
                    int3 key = state.PendingRemoves[i];
                    removes[r++] = new CaelixNativeBrickRemove
                    {
                        entityIndex = (uint)state.PacketIndex, keyX = key.x, keyY = key.y, keyZ = key.z,
                    };
                }
                state.PendingRemoves.Clear();
            }

            // Uploads: one Burst job copies the waiting bricks of every entity, in entity order, up
            // to the budget. Bound pointers live only inside this synchronous run.
            int uploadCount = 0;
            int endOffset = dataOffset;
            long bricks = 0;
            if (budget > 0 && uploadEntities > 0)
            {
                var sources = new NativeArray<UploadSource>(uploadEntities, Allocator.TempJob);
                var owners = new EntityState[uploadEntities];
                int s = 0;
                foreach (EntityState state in entityOrder)
                {
                    if (state.PacketIndex < 0 || state.UploadsWaiting <= 0) continue;
                    owners[s] = state;
                    sources[s++] = new UploadSource
                    {
                        Data = state.View.Data,
                        Keys = state.PendingUploads.Ptr + state.Head,
                        Count = state.UploadsWaiting,
                        EntityIndex = (uint)state.PacketIndex,
                    };
                }
                var consumed = new NativeArray<int>(uploadEntities, Allocator.TempJob);
                var result = new NativeArray<long>(4, Allocator.TempJob);
                new FillUploadsJob
                {
                    Sources = sources,
                    Packet = packet,
                    UploadsOffset = uploadsOffset,
                    DataOffset = dataOffset,
                    DataEnd = dataOffset + budget,
                    Consumed = consumed,
                    Result = result,
                }.Run();
                uploadCount = (int)result[0];
                endOffset = (int)result[1];
                bricks = result[2];
                BricksSentTotal += bricks;
                if (bricks > 0)
                {
                    if (!streaming)
                    {
                        streaming = true;
                        streamStartFrame = Time.frameCount;
                        streamStartTicks = Stopwatch.GetTimestamp();
                        streamTickMsTotal = 0;
                        streamTickMsMax = 0;
                        streamTicks = 0;
                        streamBricks = 0;
                    }
                    streamBricks += bricks;
                    settleFrames = 0;
                }
                for (int i = 0; i < uploadEntities; i++)
                {
                    EntityState state = owners[i];
                    state.Head += consumed[i];
                    if (state.Head >= state.PendingUploads.Length)
                    {
                        state.PendingUploads.Clear();
                        state.Head = 0;
                    }
                    else if (state.Head > 65536 && state.Head * 2 > state.PendingUploads.Length)
                    {
                        state.PendingUploads.RemoveRange(0, state.Head);
                        state.Head = 0;
                    }
                }
                sources.Dispose();
                consumed.Dispose();
                result.Dispose();
            }

            ulong sequence = ++s_frameSequence;
            *(CaelixNativeFramePacket*)packet = new CaelixNativeFramePacket
            {
                header = new CaelixNativePacketHeader
                {
                    size = (uint)sizeof(CaelixNativeFramePacket),
                    version = CaelixNativeBridge.PacketVersion,
                    kind = CaelixNativeBridge.PacketFrame,
                    sequence = sequence,
                },
                flags = reset ? CaelixNativeBridge.FrameReset : 0u,
                totalBytes = (uint)Math.Max(endOffset, dataOffset),
                encoding = sessionEncoding,
                stagingMib = (uint)sessionStagingMiB,
                materialCount = (uint)materialCount,
                materialsOffset = (uint)materialsOffset,
                entityCount = (uint)entityCount,
                entitiesOffset = (uint)entitiesOffset,
                removeCount = (uint)removeCount,
                removesOffset = (uint)removesOffset,
                uploadCount = (uint)uploadCount,
                uploadsOffset = (uint)uploadsOffset,
                brickCount = (uint)bricks,
            };
            frameSequence[slot] = sequence;
            frameBricks[slot] = bricks;
            if (reset) sessionResetSequence = sequence;

            pendingEntityRemoves.Clear();
            long waiting = 0;
            foreach (EntityState state in entityOrder) waiting += state.UploadsWaiting;
            BricksPendingCSharp = waiting;
            return true;
        }

        private static unsafe void WriteGuid(ref CaelixNativeEntity record, Guid128 guid)
        {
            uint4 v = guid.Value;
            fixed (byte* g = record.guid)
            {
                *(uint4*)g = v;
            }
        }

        private static bool TransformEquals(RigidTransform a, RigidTransform b)
        {
            return math.all(a.pos == b.pos) && math.all(a.rot.value == b.rot.value);
        }

        private void EnsureFrameCapacity(int slot, long bytes)
        {
            if (frameSlots[slot] != IntPtr.Zero && frameCapacity[slot] >= bytes) return;
            if (frameSlots[slot] != IntPtr.Zero) Marshal.FreeHGlobal(frameSlots[slot]);
            frameSlots[slot] = Marshal.AllocHGlobal(new IntPtr(bytes));
            frameCapacity[slot] = bytes;
        }

        /// <summary>
        /// Ends the stream statistics when a burst of uploads is resident and traceable in the
        /// plugin, and logs one line then (only with <see cref="VerboseLogging"/>).
        /// </summary>
        private void TrackStream(in CaelixNativeStatus status)
        {
            if (!streaming) return;
            if (BricksPendingCSharp > 0 || BricksInFlight(status) > 0 || status.asBuildsPending != 0)
            {
                settleFrames = 0;
                return;
            }
            // The statistics are published after the flush of the frame event that consumed the
            // last uploads; two more flushes make sure they belong to a flush after it.
            if (settleFrames == 0)
            {
                settleFrames = status.frames + 2;
                return;
            }
            if (status.frames < settleFrames) return;
            streaming = false;
            settleFrames = 0;
            if (!verboseLogging) return;
            double seconds = (Stopwatch.GetTimestamp() - streamStartTicks) / (double)Stopwatch.Frequency;
            Debug.Log($"Caelix native: stream complete after {Time.frameCount - streamStartFrame} frames ({seconds:F2} s): " +
                      $"{streamBricks} bricks sent, plugin resident {status.bricksResident}, {status.entities} entities, " +
                      $"{status.groups} groups, uploaded total {status.uploadedTotal}; Tick {streamTickMsTotal / Math.Max(1, streamTicks):F3} ms " +
                      $"mean, {streamTickMsMax:F3} ms max over {streamTicks} ticks.", this);
        }

        private void LogStatus(in CaelixNativeStatus s)
        {
            Debug.Log($"Caelix native status (frame {Time.frameCount}): frames {s.frames}, renders {s.renders}, failed " +
                      $"{s.failedEvents}, last error {s.lastError}, resets {s.sceneResets}, live views 0x{s.liveViews:X2}, " +
                      $"encoding {s.sceneEncoding}, entities {s.entities}, groups {s.groups}, resident {s.bricksResident}, " +
                      $"pending C# {BricksPendingCSharp}, in flight {BricksInFlight(s)}, free/cap {s.uploadBricksFree}/" +
                      $"{s.uploadBricksCap}, dropped {s.bricksDropped}, as_builds_pending {s.asBuildsPending}, GPU flush " +
                      $"{s.flushMs:F3} ms trace {s.traceMs:F3} ms composite {s.compositeMs:F3} ms denoise {s.denoiseMs:F3} ms " +
                      $"(view {s.timingsView}); Tick {LastTickMilliseconds:F3} ms, deferred {TicksDeferred}, skipped " +
                      $"{PacketsSkipped}; jitter ({LastJitter.x:F3}, {LastJitter.y:F3}) px.", this);
        }

        [Serializable]
        private class MaterialJsonEntry
        {
            public int id = -1;
            public bool transparent;
            public float[] albedo;
            public float[] emission;
            public float smoothness;
            public float metallic;
            public float ior = 1.5f;
            public float extinction;
        }

        [Serializable]
        private class MaterialJsonList
        {
            public MaterialJsonEntry[] items;
        }

        /// <summary>The material entries of <paramref name="asset"/>; null without an asset or on a parse error.</summary>
        private unsafe CaelixNativeMaterialEntry[] ParseMaterials(TextAsset asset)
        {
            if (asset == null) return null;
            MaterialJsonList list;
            try
            {
                list = JsonUtility.FromJson<MaterialJsonList>("{\"items\":" + asset.text + "}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Caelix native: {asset.name} is not a material JSON array: {ex.Message}", this);
                return null;
            }
            if (list?.items == null) return null;
            var entries = new List<CaelixNativeMaterialEntry>(list.items.Length);
            for (int i = 0; i < list.items.Length; i++)
            {
                MaterialJsonEntry item = list.items[i];
                if (item == null) continue;
                int limit = item.transparent ? 0x1FF : 0x7FFF;
                if (item.id < 0 || item.id > limit)
                {
                    Debug.LogWarning($"Caelix native: {asset.name} entry {i}: id {item.id} out of range (0..{limit}); skipped.", this);
                    continue;
                }
                var entry = new CaelixNativeMaterialEntry
                {
                    code = item.transparent ? (uint)item.id : 0x8000u | (uint)item.id,
                };
                entry.material.albedo[0] = Element(item.albedo, 0, 1f);
                entry.material.albedo[1] = Element(item.albedo, 1, 1f);
                entry.material.albedo[2] = Element(item.albedo, 2, 1f);
                entry.material.emission[0] = Element(item.emission, 0, 0f);
                entry.material.emission[1] = Element(item.emission, 1, 0f);
                entry.material.emission[2] = Element(item.emission, 2, 0f);
                entry.material.smoothness = item.smoothness;
                entry.material.metallic = item.metallic;
                entry.material.ior = item.ior > 0f ? item.ior : 1.5f;
                entry.material.extinction = Mathf.Max(0f, item.extinction);
                entries.Add(entry);
            }
            CaelixNativeMaterialEntry[] parsed = entries.ToArray();
            if (verboseLogging)
            {
                Debug.Log($"Caelix native: {parsed.Length} material entries from {asset.name}.", this);
            }
            return parsed;
        }

        private static float Element(float[] values, int index, float fallback)
        {
            return values != null && values.Length == 3 ? values[index] : fallback;
        }

        private unsafe struct UploadSource
        {
            public VoxelEntityData Data;
            [NativeDisableUnsafePtrRestriction] public int3* Keys;
            public int Count;
            public uint EntityIndex;
        }

        /// <summary>
        /// Copies waiting bricks into the packet: per entity, the keys (12 bytes each) then the
        /// 16-byte aligned block values (1024 bytes each) and one upload record. A key whose brick
        /// no longer exists is consumed without a copy (its removal is, or was, a remove record).
        /// Stops at the first entity that does not fit completely; the rest waits for a later packet.
        /// </summary>
        [BurstCompile]
        private unsafe struct FillUploadsJob : IJob
        {
            [ReadOnly] public NativeArray<UploadSource> Sources;
            [NativeDisableUnsafePtrRestriction] public byte* Packet;
            public int UploadsOffset;
            public int DataOffset;
            public long DataEnd;
            public NativeArray<int> Consumed;
            /// <summary>0 upload records, 1 end offset, 2 bricks copied, 3 keys skipped.</summary>
            public NativeArray<long> Result;

            public void Execute()
            {
                long cursor = DataOffset;
                int uploads = 0;
                long bricks = 0;
                long skipped = 0;
                var pointers = new NativeList<IntPtr>(1024, Allocator.Temp);
                var keys = new NativeList<int3>(1024, Allocator.Temp);
                for (int s = 0; s < Sources.Length; s++)
                {
                    UploadSource source = Sources[s];
                    pointers.Clear();
                    keys.Clear();
                    BrickCursor brickCursor = default;
                    // Keys area + up to 15 bytes of alignment + blocks.
                    long available = DataEnd - cursor - 16;
                    int taken = 0;
                    for (int i = 0; i < source.Count; i++)
                    {
                        if ((long)(keys.Length + 1) * (12 + 1024) > available) break;
                        int3 key = source.Keys[i];
                        taken = i + 1;
                        if (source.Data.TryBindBrick(SectorSlotId.Block, key, ref brickCursor, out Block* blocks))
                        {
                            pointers.Add((IntPtr)blocks);
                            keys.Add(key);
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    Consumed[s] = taken;
                    int n = keys.Length;
                    if (n > 0)
                    {
                        long keysOffset = cursor;
                        UnsafeUtility.MemCpy(Packet + keysOffset, keys.GetUnsafeReadOnlyPtr(), (long)n * 12);
                        cursor = (keysOffset + (long)n * 12 + 15) & ~15L;
                        long blocksOffset = cursor;
                        for (int i = 0; i < n; i++)
                        {
                            UnsafeUtility.MemCpy(Packet + blocksOffset + (long)i * 1024, (void*)pointers[i], 1024);
                        }
                        cursor = blocksOffset + (long)n * 1024;
                        var record = (CaelixNativeUpload*)(Packet + UploadsOffset) + uploads;
                        *record = new CaelixNativeUpload
                        {
                            entityIndex = source.EntityIndex,
                            brickCount = (uint)n,
                            keysOffset = (uint)keysOffset,
                            blocksOffset = (uint)blocksOffset,
                        };
                        uploads++;
                        bricks += n;
                    }
                    if (taken < source.Count)
                    {
                        for (int r = s + 1; r < Sources.Length; r++) Consumed[r] = 0;
                        break;
                    }
                }
                pointers.Dispose();
                keys.Dispose();
                Result[0] = uploads;
                Result[1] = cursor;
                Result[2] = bricks;
                Result[3] = skipped;
            }
        }

        #endregion

        #region Packets

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
            if (!sessionLive || camera == null || viewRing == IntPtr.Zero) return false;
            if (width <= 0 || height <= 0) return false;

            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            FreeDeferred(status);
            ContinueSequences(status);

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
            int packetSlot = FindFreeViewPacket(status);
            if (packetSlot < 0)
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
                releasePacket = FillRelease(CaelixNativeBridge.ReleaseTargetViews, 0);
                if (releasePacket == IntPtr.Zero)
                {
                    // The plugin may still cache a view of a destroyed texture: no render without the release.
                    PacketsSkipped++;
                    return false;
                }
                s_releasedGeneration = s_generation;
            }

            bool isGame = camera.cameraType == CameraType.Game;
            ulong sequence = ++s_viewSequence;
            var data = new CaelixNativeViewPacket
            {
                header = new CaelixNativePacketHeader
                {
                    size = (uint)ViewPacketBytes,
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
            IntPtr memory = viewRing + packetSlot * ViewPacketBytes;
            unsafe
            {
                *(CaelixNativeViewPacket*)memory = data;
            }
            viewSequence[packetSlot] = sequence;
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

        private int FindFreeFrameSlot(in CaelixNativeStatus status)
        {
            for (int i = 0; i < FrameRingSize; i++)
            {
                if (frameSequence[i] == 0 || status.lastConsumedFrameSequence >= frameSequence[i]) return i;
            }
            return -1;
        }

        private int FindFreeViewPacket(in CaelixNativeStatus status)
        {
            for (int i = 0; i < ViewRingSize; i++)
            {
                if (viewSequence[i] == 0 || status.lastConsumedViewSequence >= viewSequence[i]) return i;
            }
            return -1;
        }

        private bool HasFreeRelease(in CaelixNativeStatus status)
        {
            if (releaseRing == IntPtr.Zero) return false;
            for (int i = 0; i < ReleaseRingSize; i++)
            {
                if (releaseSequence[i] == 0 || status.lastConsumedReleaseSequence >= releaseSequence[i]) return true;
            }
            return false;
        }

        /// <summary>
        /// Fills a free release packet slot and returns its address; zero when every slot is still
        /// waiting for the render thread (the caller tries again later).
        /// </summary>
        private IntPtr FillRelease(uint flags, uint view)
        {
            if (releaseRing == IntPtr.Zero) return IntPtr.Zero;
            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            for (int i = 0; i < ReleaseRingSize; i++)
            {
                if (releaseSequence[i] != 0 && status.lastConsumedReleaseSequence < releaseSequence[i]) continue;
                IntPtr memory = releaseRing + i * ReleasePacketBytes;
                releaseSequence[i] = WriteRelease(memory, flags, view);
                return memory;
            }
            return IntPtr.Zero;
        }

        private static unsafe ulong WriteRelease(IntPtr memory, uint flags, uint view)
        {
            ulong sequence = ++s_releaseSequence;
            *(CaelixNativeReleasePacket*)memory = new CaelixNativeReleasePacket
            {
                header = new CaelixNativePacketHeader
                {
                    size = (uint)sizeof(CaelixNativeReleasePacket),
                    version = CaelixNativeBridge.PacketVersion,
                    kind = CaelixNativeBridge.PacketRelease,
                    sequence = sequence,
                },
                flags = flags,
                view = view,
            };
            return sequence;
        }

        /// <summary>
        /// A domain reload resets the static counters to 0 but the plugin keeps its newest consumed
        /// sequences: continue above them, or every slot would look consumed while the render
        /// thread may still read it.
        /// </summary>
        private static void ContinueSequences(in CaelixNativeStatus status)
        {
            if (s_frameSequence < status.lastConsumedFrameSequence) s_frameSequence = status.lastConsumedFrameSequence;
            if (s_viewSequence < status.lastConsumedViewSequence) s_viewSequence = status.lastConsumedViewSequence;
            if (s_releaseSequence < status.lastConsumedReleaseSequence) s_releaseSequence = status.lastConsumedReleaseSequence;
        }

        private static ulong ConsumedSequence(in CaelixNativeStatus status, uint kind)
        {
            switch (kind)
            {
                case CaelixNativeBridge.PacketFrame: return status.lastConsumedFrameSequence;
                case CaelixNativeBridge.PacketView: return status.lastConsumedViewSequence;
                default: return status.lastConsumedReleaseSequence;
            }
        }

        private static ulong Max(ulong[] values)
        {
            ulong max = 0;
            foreach (ulong value in values) max = Math.Max(max, value);
            return max;
        }

        /// <summary>Frees <paramref name="memory"/> now, or once the render thread consumed packet <paramref name="sequence"/> of <paramref name="kind"/>.</summary>
        private static void FreeOrDefer(IntPtr memory, uint kind, ulong sequence, in CaelixNativeStatus status)
        {
            if (memory == IntPtr.Zero) return;
            if (sequence == 0 || ConsumedSequence(status, kind) >= sequence)
            {
                Marshal.FreeHGlobal(memory);
            }
            else
            {
                s_deferredFree.Add((memory, kind, sequence));
            }
        }

        private static void FreeDeferred(in CaelixNativeStatus status)
        {
            for (int i = s_deferredFree.Count - 1; i >= 0; i--)
            {
                if (ConsumedSequence(status, s_deferredFree[i].kind) >= s_deferredFree[i].sequence)
                {
                    Marshal.FreeHGlobal(s_deferredFree[i].memory);
                    s_deferredFree.RemoveAt(i);
                }
            }
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

        #endregion
    }
}
