using System;
using UnityEngine;
using UnityEngine.Rendering;
using Caelix.Client;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// Scene renderer backed by the native Vulkan plugin <c>caelix_unity</c> (Caelix-Renderer).
    /// </summary>
    /// <remarks>
    /// A view is one camera: every camera that renders through <see cref="CaelixNativeRendererFeature"/>
    /// gets its own view (up to <see cref="CaelixNativeBridge.MaxViews"/>), with its own plugin
    /// renderer (internal targets, denoiser history) over one shared scene. This component owns,
    /// per view, the four targets the plugin writes (<see cref="CaelixNativeViewTargets"/>).
    /// <para>
    /// <see cref="Tick"/> (called once per frame by <see cref="CaelixHost"/> after the client's
    /// <c>PrepareRender</c>, like the ray query renderer) fills ONE frame packet with the scene
    /// records of the frame (<see cref="CaelixNativeEntityFeed"/>): entity records, brick removes,
    /// and brick uploads copied from the client store, as many as the plugin's staging ring can
    /// take (the status' <c>upload_bricks_free</c> minus the bricks of frame packets still in
    /// flight, capped at <c>upload_bricks_cap</c>). It issues the plugin's frame event with
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
    /// Packets live in unmanaged memory, in one ring per packet kind
    /// (<see cref="CaelixNativePacketRings"/>). The render thread reads a packet during its event
    /// and then publishes the packet's sequence number for its kind; a slot is reused only after that.
    /// </para>
    /// Options mirror the Rust host's <c>RenderSettings</c> (Caelix-Renderer/host-rs) and its
    /// command-line defaults. The code is split by responsibility: this file holds the settings
    /// and the component lifecycle, <c>CaelixNativeRenderer.Session.cs</c> the tick and the
    /// sessions, <c>CaelixNativeRenderer.Views.cs</c> the per-camera views and their packets.
    /// </remarks>
    [DisallowMultipleComponent]
    public partial class CaelixNativeRenderer : MonoBehaviour
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
        public long BricksPendingCSharp => feed != null ? feed.BricksPending : 0;

        /// <summary>Bricks copied into frame packets since this component was enabled.</summary>
        public long BricksSentTotal => feed != null ? feed.BricksSentTotal : 0;

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

        private readonly CaelixNativePacketRings rings = new CaelixNativePacketRings();
        private CaelixNativeEntityFeed feed = new CaelixNativeEntityFeed();

        /// <summary>Carries the frame event (and the release events of the tick) to <see cref="Graphics.ExecuteCommandBuffer"/>.</summary>
        private CommandBuffer frameCommands;

        private void OnEnable()
        {
            if (Active != null && Active != this)
            {
                Debug.LogWarning($"Caelix native: {name} replaces {Active.name} as the active native renderer.", this);
            }
            Active = this;
            rings.Allocate();
            frameCommands = new CommandBuffer { name = "Caelix Native Frame" };
            for (int v = 0; v < viewSlots.Length; v++) FreeViewSlot(v);
            if (feed == null) feed = new CaelixNativeEntityFeed();
            sessionLive = false;
        }

        private void OnDisable()
        {
            if (Active == this) Active = null;
            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            CaelixNativePacketRings.Maintain(status);
            if (sessionLive)
            {
                // The session ends: every renderer and the scene go away, the plugin holds nothing
                // between Play sessions. Issued before the targets below are released. When every
                // ring slot is in flight there is no next tick to wait for: a block of its own.
                IntPtr release = rings.FillRelease(CaelixNativeBridge.ReleaseScene, 0, status);
                if (release == IntPtr.Zero)
                {
                    release = CaelixNativePacketRings.FillDetachedRelease(CaelixNativeBridge.ReleaseScene, 0);
                }
                frameCommands.Clear();
                frameCommands.IssuePluginEventAndData(CaelixNativeBridge.RenderEventFunc, status.releaseEventId, release);
                Graphics.ExecuteCommandBuffer(frameCommands);
                sessionLive = false;
            }
            frameCommands?.Release();
            frameCommands = null;

            // Packet memory the render thread may still read is freed once it reports the sequence.
            rings.Release(status);
            for (int v = 0; v < viewSlots.Length; v++) FreeViewSlot(v);
            UnbindSource();
            feed.Dispose();
            feed = null;
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
    }
}
