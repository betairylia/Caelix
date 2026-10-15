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
    /// Owns, per view, the four targets the plugin writes (colour RGBA16F, depth R32F, motion
    /// RGBA16F, albedo RGBA8 with coverage in alpha; the library writes them with
    /// <c>CAELIX_RENDER_FLAG_FLIP_Y</c>, so they have Unity's row order) and a ring of packets.
    /// <see cref="Tick"/> (called by <see cref="CaelixHost"/> after the client's
    /// <c>PrepareRender</c>, like the ray query renderer) fills the scene section of the next
    /// packet: entity records, brick removes, and brick uploads copied from the client store by a
    /// Burst job, up to <see cref="BrickBudgetBytes"/> per packet. Each camera render,
    /// <see cref="CaelixNativeRendererFeature"/> asks for a packet (<see cref="TryPrepareFrame"/>),
    /// which adds the view index, the camera (with URP's TAA jitter), the options and the view's
    /// targets to the filled packet, and issues the plugin's render event with it. View 0 is the
    /// Game view, view 1 the Scene view (<see cref="SceneView"/>); the plugin has one renderer per
    /// view (own internal targets and denoiser history) over one shared scene.
    /// <para>
    /// Packets live in unmanaged memory. The render thread reads a packet during its event and then
    /// publishes the packet's sequence number; a slot is reused only after that. Brick data is
    /// COPIED into the packet in the same frame it is bound (the phase rule of the store), because
    /// the render thread runs after the next server tick may have changed the storage.
    /// </para>
    /// <para>
    /// The plugin's scene outlives Play sessions and components, so the first scene packet of an
    /// enabled component carries the reset flag and sends every view as new.
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

        /// <summary>How the Editor Scene view is drawn (view 1).</summary>
        public enum SceneViewMode
        {
            /// <summary>The Scene view is not drawn.</summary>
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

        /// <summary>
        /// Packet slots. Every view issues one packet per frame and the render thread may be a frame
        /// behind, so two views need about four slots in flight; six leaves room for a third view.
        /// </summary>
        private const int RingSize = 6;
        /// <summary>A slot whose packet was never consumed (its event was dropped) is reused after this many frames.</summary>
        private const int StaleSlotFrames = 8;

        /// <summary>Bytes of one brick in a packet: its key and its 512 block values.</summary>
        private const int BrickPacketBytes = 12 + 1024;

        [Header("Scene Feed")]
        [SerializeField, Tooltip("The host whose client world is drawn. Found in the scene when empty.")]
        private CaelixHost host;

        [SerializeField, Tooltip("Block encoding of the plugin's scene. Auto = the build's BlockEncoding.PackedSceneColor. " +
                                 "Changing it while running recreates the plugin's scene and sends the world again.")]
        private EncodingOverride encodingOverride = EncodingOverride.Auto;

        [SerializeField, Tooltip("Block-ID encoding only: material table entries (the Rust host's --materials JSON: id, " +
                                 "transparent, albedo, emission, smoothness, metallic, ior, extinction), applied over the " +
                                 "plugin's built-in table.")]
        private TextAsset materialsJson;

        [SerializeField, Min(1), Tooltip("Upper size of the brick data of one packet, in MiB (1036 bytes per brick).")]
        private int brickBudgetMiB = 64;

        [SerializeField, Min(0), Tooltip("Logs the plugin status every N frames (0 = off).")]
        private int statusLogInterval = 0;

        [SerializeField, Tooltip("Logs stream completion, encoding changes and material table loads to the Console.")]
        private bool verboseLogging = false;

        [Header("Scene View")]
        [SerializeField, Tooltip("Editor Scene view: Off = not drawn; Lit = the same mode as the Game view with its own " +
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
        [SerializeField, Tooltip("Lighting modes: show the procedural sky where the primary ray misses.")]
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

        /// <summary>The enabled instance the renderer feature draws. One at a time.</summary>
        public static CaelixNativeRenderer Active { get; private set; }

        /// <summary>
        /// The colour target the plugin writes for <paramref name="view"/> (RGBA16F, random write).
        /// Null before the view's first frame.
        /// </summary>
        public RTHandle ColorHandle(int view) => viewTargets[view].Color.Handle;

        /// <summary>Device depth of Unity's GPU projection (R32F) for <paramref name="view"/>; a miss is the far plane.</summary>
        public RTHandle DepthHandle(int view) => viewTargets[view].Depth.Handle;

        /// <summary>
        /// Motion of the primary hit for <paramref name="view"/> in pixels, previous - current, y up
        /// (flipped rows); w = checkerboard flag.
        /// </summary>
        public RTHandle MotionHandle(int view) => viewTargets[view].Motion.Handle;

        /// <summary>Albedo (RGBA8) for <paramref name="view"/>; alpha = coverage (1 hit, 0 miss).</summary>
        public RTHandle AlbedoHandle(int view) => viewTargets[view].Albedo.Handle;

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

        /// <summary>The render mode of <paramref name="view"/>: the Scene view (1) may use the primary mode.</summary>
        private RenderMode ModeForView(int view) =>
            view == 1 && sceneView == SceneViewMode.Primary ? RenderMode.Primary : mode;

        /// <summary>The debug view of <paramref name="view"/>: albedo for the Scene view's primary mode.</summary>
        private DebugView DebugViewForView(int view) =>
            view == 1 && sceneView == SceneViewMode.Primary ? DebugView.Albedo : debugView;

        /// <summary>The jitter of the newest prepared Game view (view 0) frame, in pixels (x right, y down).</summary>
        public Vector2 LastJitter { get; private set; }

        /// <summary>Packets issued since this component was enabled.</summary>
        public ulong PacketsIssued { get; private set; }

        /// <summary>Frames skipped because every packet slot was still waiting for the render thread.</summary>
        public ulong PacketsSkipped { get; private set; }

        /// <summary>Ticks that could not fill a packet (the previous one was not issued yet, or no slot was free).</summary>
        public ulong TicksDeferred { get; private set; }

        /// <summary>CPU time of the newest <see cref="Tick"/>, in milliseconds.</summary>
        public double LastTickMilliseconds { get; private set; }

        /// <summary>Bricks waiting on the C# side (not copied into a packet yet), over every view.</summary>
        public long BricksPendingCSharp { get; private set; }

        /// <summary>Bricks copied into packets since this component was enabled.</summary>
        public long BricksSentTotal { get; private set; }

        /// <summary>The block encoding of the scene packets (0 packed, 1 block-ID).</summary>
        public uint SceneEncoding => tickEncoding;

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
        public int BrickBudgetMiB { get => brickBudgetMiB; set => brickBudgetMiB = Mathf.Max(1, value); }

        /// <summary>
        /// Restarts the reference accumulation and the NRD history with the next frame (the Rust
        /// host's "Reset accumulation" button). The reference mode also restarts by itself when the
        /// camera or an option changes.
        /// </summary>
        [ContextMenu("Reset Accumulation")]
        public void ResetAccumulation()
        {
            foreach (ViewFrameState state in viewFrames) state.ResetRequested = true;
        }

        public EncodingOverride Encoding { get => encodingOverride; set => encodingOverride = value; }
        public TextAsset MaterialsJson { get => materialsJson; set => materialsJson = value; }
        public int StatusLogInterval { get => statusLogInterval; set => statusLogInterval = Mathf.Max(0, value); }
        public bool VerboseLogging { get => verboseLogging; set => verboseLogging = value; }
        public long BrickBudgetBytes => (long)Mathf.Max(1, brickBudgetMiB) << 20;

        /// <summary>The host whose client world this renderer draws, once resolved.</summary>
        public CaelixHost Host => host;

        // ---------------- packets ----------------
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
        }

        private static T[] PerView<T>() where T : new()
        {
            var array = new T[CaelixNativeBridge.MaxViews];
            for (int i = 0; i < array.Length; i++) array[i] = new T();
            return array;
        }

        private readonly ViewTargets[] viewTargets = PerView<ViewTargets>();
        private readonly ViewFrameState[] viewFrames = PerView<ViewFrameState>();
        /// <summary>
        /// One generation for every view: the plugin releases the cached views of every renderer on
        /// one release event, so a target replaced in any view needs that event.
        /// </summary>
        private uint generation;

        private readonly IntPtr[] slots = new IntPtr[RingSize];
        private readonly long[] slotCapacity = new long[RingSize];
        private readonly ulong[] slotSequence = new ulong[RingSize];
        private readonly int[] slotFrame = new int[RingSize];
        /// <summary>The slot holds a scene section from <see cref="Tick"/> that was not issued yet.</summary>
        private readonly bool[] slotFilled = new bool[RingSize];
        /// <summary>The slot's newest issued packet carried scene records.</summary>
        private readonly bool[] slotHadScene = new bool[RingSize];
        private readonly CaelixNativeSceneSection[] slotScene = new CaelixNativeSceneSection[RingSize];
        private readonly uint[] slotEncoding = new uint[RingSize];
        private uint frameSeed;
        private int lastPreparedFrame = -1;
        private bool warnedJitter;

        // ---------------- scene feed ----------------
        private ClientWorld source;
        private readonly Dictionary<EntityView, ViewState> views = new Dictionary<EntityView, ViewState>();
        private readonly List<ViewState> viewOrder = new List<ViewState>();
        private readonly List<Guid128> pendingEntityRemoves = new List<Guid128>();
        private readonly List<EntityView> removedScratch = new List<EntityView>();
        private NativeHashSet<int3> frameDedupe;
        private int viewStamp;
        private uint tickEncoding = uint.MaxValue;
        private bool sceneReset = true;
        private bool materialsDirty = true;
        private TextAsset parsedMaterialsAsset;
        private CaelixNativeMaterialEntry[] parsedMaterials;
        private int lastTickFrame = -1;

        // Stream tracking (logs one line when a burst of uploads has become traceable).
        private bool streaming;
        private int streamStartFrame;
        private long streamStartTicks;
        private double streamTickMsTotal;
        private double streamTickMsMax;
        private int streamTicks;
        private long streamBricks;
        private ulong lastUploadSequence;
        private int lastUploadSlot = -1;
        private ulong settleRenders;

        // Shared by every instance: sequence numbers and target generations must never repeat
        // within one plugin lifetime. The plugin outlives components and domain reloads; a domain
        // reload resets these to 0, so TryPrepareFrame continues s_sequence above the plugin's
        // last consumed sequence (slot reuse depends on it). Generations only feed a warning.
        private static ulong s_sequence;
        private static uint s_generation;
        private static uint s_releasedGeneration;
        private static uint s_issuedEncoding = uint.MaxValue;
        private static readonly List<(IntPtr memory, ulong sequence)> s_deferredFree = new List<(IntPtr, ulong)>();

        private sealed class ViewState
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
            for (int i = 0; i < RingSize; i++)
            {
                slots[i] = IntPtr.Zero;
                slotCapacity[i] = 0;
                slotSequence[i] = 0;
                slotFrame[i] = 0;
                slotFilled[i] = false;
                slotHadScene[i] = false;
            }
            for (int v = 0; v < viewFrames.Length; v++) viewFrames[v].LastMode = ModeForView(v);
            frameDedupe = new NativeHashSet<int3>(1024, Allocator.Persistent);
            sceneReset = true;
            materialsDirty = true;
            tickEncoding = ResolveEncoding();
        }

        private void OnDisable()
        {
            if (Active == this) Active = null;
            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            for (int i = 0; i < RingSize; i++)
            {
                if (slots[i] == IntPtr.Zero) continue;
                bool issuedAndPending = !slotFilled[i] && slotSequence[i] != 0 && status.lastConsumedSequence < slotSequence[i];
                if (!issuedAndPending)
                {
                    Marshal.FreeHGlobal(slots[i]);
                }
                else
                {
                    // The render thread may still read it: free it once it reports the sequence.
                    s_deferredFree.Add((slots[i], slotSequence[i]));
                }
                slots[i] = IntPtr.Zero;
                slotCapacity[i] = 0;
                slotFilled[i] = false;
            }
            foreach (ViewTargets targets in viewTargets) targets.Release();
            UnbindSource();
            if (frameDedupe.IsCreated) frameDedupe.Dispose();
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

        private bool EnsureSource()
        {
            if (source != null && !source.IsDisposed) return true;
            if (host == null) host = CaelixHost.Current;
            if (host == null) return false;
            host.EnsureInitialized();
            ClientWorld world = host.ClientWorld;
            if (world == null || world.IsDisposed) return false;
            UnbindSource();
            source = world;
            // A new world: the plugin's scene must forget the old one.
            sceneReset = true;
            materialsDirty = true;
            return true;
        }

        private void UnbindSource()
        {
            foreach (ViewState state in viewOrder) state.Dispose();
            views.Clear();
            viewOrder.Clear();
            pendingEntityRemoves.Clear();
            source = null;
        }

        /// <summary>Forgets every view; the next diff sends them all as new, after a scene reset.</summary>
        private void ResendEverything()
        {
            foreach (ViewState state in viewOrder) state.Dispose();
            views.Clear();
            viewOrder.Clear();
            pendingEntityRemoves.Clear();
            sceneReset = true;
            materialsDirty = true;
        }

        /// <summary>
        /// Collects this frame's scene changes and, when a packet slot is free, copies them into
        /// the next packet. Call once per frame after the client's <c>PrepareRender</c> and before
        /// <c>EndFrame</c>: the change lists are read here and nowhere cleared.
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
            if (!EnsureSource()) return;

            uint encoding = ResolveEncoding();
            if (encoding != tickEncoding)
            {
                if (verboseLogging)
                {
                    Debug.Log($"Caelix native: scene encoding {tickEncoding} -> {encoding}; the plugin recreates its scene and the world is sent again.", this);
                }
                tickEncoding = encoding;
                ResendEverything();
            }
            if (!ReferenceEquals(materialsJson, parsedMaterialsAsset))
            {
                materialsDirty = true;
            }

            CollectChanges();

            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            FreeDeferred(status.lastConsumedSequence);
            TrackStream(status);
            if (statusLogInterval > 0 && Time.frameCount % statusLogInterval == 0)
            {
                LogStatus(status);
            }

            for (int i = 0; i < RingSize; i++)
            {
                if (slotFilled[i])
                {
                    // The previous scene packet was not issued yet (no camera rendered): keep it,
                    // everything collected since waits in the view states.
                    TicksDeferred++;
                    return;
                }
            }
            int slot = FindFreeSlot(status.lastConsumedSequence);
            if (slot < 0)
            {
                TicksDeferred++;
                return;
            }
            FillScenePacket(slot, status);
        }

        /// <summary>Diffs the views and reads their change lists into the view states.</summary>
        private void CollectChanges()
        {
            IReadOnlyList<EntityView> list = source.Views;
            viewStamp++;
            for (int v = 0; v < list.Count; v++)
            {
                EntityView view = list[v];
                if (!views.TryGetValue(view, out ViewState state))
                {
                    state = new ViewState
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
                    views.Add(view, state);
                    viewOrder.Add(state);
                    state.Stamp = viewStamp;
                    continue;
                }
                state.Stamp = viewStamp;

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
            foreach (var kvp in views)
            {
                if (kvp.Value.Stamp != viewStamp) removedScratch.Add(kvp.Key);
            }
            foreach (EntityView view in removedScratch)
            {
                ViewState state = views[view];
                // A view that never reached the plugin needs no remove.
                if (!state.NeedsAdd) pendingEntityRemoves.Add(state.Guid);
                state.Dispose();
                views.Remove(view);
                viewOrder.Remove(state);
            }

            long waiting = 0;
            foreach (ViewState state in viewOrder) waiting += state.UploadsWaiting;
            BricksPendingCSharp = waiting;
        }

        private static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

        private unsafe void FillScenePacket(int slot, in CaelixNativeStatus status)
        {
            // Back-pressure: while the plugin still holds a packet's worth of bricks, send none.
            long budget = BrickBudgetBytes;
            if ((long)status.bricksPendingPlugin * BrickPacketBytes >= budget) budget = 0;

            // Entity records: removes first (the plugin applies them in list order), then the views
            // that have something to say.
            int entityCount = pendingEntityRemoves.Count;
            int removeCount = 0;
            int uploadViews = 0;
            foreach (ViewState state in viewOrder)
            {
                state.PacketIndex = -1;
                bool moved = !state.NeedsAdd && !TransformEquals(state.SentTransform, state.View.Data.transform);
                bool uploads = budget > 0 && state.UploadsWaiting > 0;
                if (state.NeedsAdd || moved || state.PendingRemoves.Length > 0 || uploads)
                {
                    state.PacketIndex = entityCount++;
                }
                removeCount += state.PendingRemoves.Length;
                if (uploads) uploadViews++;
            }

            bool sendMaterials = materialsDirty;
            uint materialMode = CaelixNativeBridge.MaterialsNone;
            int materialCount = 0;
            int materialBytes = 0;
            if (sendMaterials)
            {
                ParseMaterialsIfChanged();
                if (tickEncoding == 1u && parsedMaterials != null && parsedMaterials.Length > 0)
                {
                    materialMode = CaelixNativeBridge.MaterialsPartial;
                    materialCount = parsedMaterials.Length;
                    materialBytes = materialCount * sizeof(CaelixNativeMaterialEntry);
                }
                else if (!sceneReset)
                {
                    // No table (or a packed scene): back to the built-in table. A reset restores it anyway.
                    materialMode = CaelixNativeBridge.MaterialsFull;
                }
            }

            int packetSize = sizeof(CaelixNativePacket);
            int entitiesOffset = Align(packetSize, 16);
            int removesOffset = Align(entitiesOffset + entityCount * sizeof(CaelixNativeEntity), 16);
            int uploadsOffset = Align(removesOffset + removeCount * sizeof(CaelixNativeBrickRemove), 16);
            int materialsOffset = Align(uploadsOffset + uploadViews * sizeof(CaelixNativeUpload), 16);
            int dataOffset = Align(materialsOffset + materialBytes, 16);
            long required = dataOffset + (budget > 0 ? budget + 16L * uploadViews + 16 : 0);
            if (required > int.MaxValue)
            {
                Debug.LogError($"Caelix native: scene packet would need {required} bytes; lower the brick budget.", this);
                return;
            }
            EnsureSlotCapacity(slot, required);
            byte* packet = (byte*)slots[slot];

            // Entities.
            var entities = (CaelixNativeEntity*)(packet + entitiesOffset);
            int e = 0;
            foreach (Guid128 guid in pendingEntityRemoves)
            {
                entities[e] = default;
                WriteGuid(ref entities[e], guid);
                entities[e].flags = CaelixNativeBridge.EntityRemove;
                e++;
            }
            foreach (ViewState state in viewOrder)
            {
                if (state.PacketIndex < 0) continue;
                ref CaelixNativeEntity record = ref entities[state.PacketIndex];
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
            foreach (ViewState state in viewOrder)
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

            // Materials.
            if (materialMode == CaelixNativeBridge.MaterialsPartial)
            {
                fixed (CaelixNativeMaterialEntry* src = parsedMaterials)
                {
                    UnsafeUtility.MemCpy(packet + materialsOffset, src, materialBytes);
                }
            }

            // Uploads: one Burst job copies the waiting bricks of every view, in view order, up to
            // the budget. Bound pointers live only inside this synchronous run.
            int uploadCount = 0;
            int endOffset = dataOffset;
            if (budget > 0 && uploadViews > 0)
            {
                var sources = new NativeArray<UploadSource>(uploadViews, Allocator.TempJob);
                var owners = new ViewState[uploadViews];
                int s = 0;
                foreach (ViewState state in viewOrder)
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
                var consumed = new NativeArray<int>(uploadViews, Allocator.TempJob);
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
                long bricks = result[2];
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
                    lastUploadSlot = slot;
                    settleRenders = 0;
                }
                for (int i = 0; i < uploadViews; i++)
                {
                    ViewState state = owners[i];
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

            uint flags = sceneReset ? CaelixNativeBridge.SceneReset : 0u;
            slotScene[slot] = new CaelixNativeSceneSection
            {
                sceneFlags = flags,
                totalBytes = (uint)Math.Max(endOffset, dataOffset),
                entityCount = (uint)entityCount,
                entitiesOffset = (uint)entitiesOffset,
                removeCount = (uint)removeCount,
                removesOffset = (uint)removesOffset,
                uploadCount = (uint)uploadCount,
                uploadsOffset = (uint)uploadsOffset,
                materialMode = materialMode,
                materialCount = (uint)materialCount,
                materialsOffset = (uint)materialsOffset,
            };
            slotEncoding[slot] = tickEncoding;
            slotFilled[slot] = true;

            pendingEntityRemoves.Clear();
            sceneReset = false;
            if (sendMaterials) materialsDirty = false;

            long waiting = 0;
            foreach (ViewState state in viewOrder) waiting += state.UploadsWaiting;
            BricksPendingCSharp = waiting;
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

        private void EnsureSlotCapacity(int slot, long bytes)
        {
            if (slots[slot] != IntPtr.Zero && slotCapacity[slot] >= bytes) return;
            if (slots[slot] != IntPtr.Zero) Marshal.FreeHGlobal(slots[slot]);
            slots[slot] = Marshal.AllocHGlobal(new IntPtr(bytes));
            slotCapacity[slot] = bytes;
        }

        /// <summary>
        /// Ends the stream statistics when a burst of uploads is resident and traceable in the
        /// plugin, and logs one line then (only with <see cref="VerboseLogging"/>).
        /// </summary>
        private void TrackStream(in CaelixNativeStatus status)
        {
            if (!streaming || BricksPendingCSharp > 0 || lastUploadSlot < 0) return;
            // The last packet with uploads must have been issued and consumed.
            if (slotFilled[lastUploadSlot]) return;
            if (lastUploadSequence == 0 || status.lastConsumedSequence < lastUploadSequence) return;
            if (status.bricksPendingPlugin != 0 || status.asBuildsPending != 0)
            {
                settleRenders = 0;
                return;
            }
            // The statistics are published after the flush of the same event that consumed the
            // packet; two more renders make sure they belong to a flush after it.
            if (settleRenders == 0)
            {
                settleRenders = status.renders + 2;
                return;
            }
            if (status.renders < settleRenders) return;
            streaming = false;
            if (!verboseLogging) return;
            double seconds = (Stopwatch.GetTimestamp() - streamStartTicks) / (double)Stopwatch.Frequency;
            Debug.Log($"Caelix native: stream complete after {Time.frameCount - streamStartFrame} frames ({seconds:F2} s): " +
                      $"{streamBricks} bricks sent, plugin resident {status.bricksResident}, {status.entities} entities, " +
                      $"{status.groups} groups, uploaded total {status.uploadedTotal}; Tick {streamTickMsTotal / Math.Max(1, streamTicks):F3} ms " +
                      $"mean, {streamTickMsMax:F3} ms max over {streamTicks} ticks.", this);
        }

        private void LogStatus(in CaelixNativeStatus s)
        {
            Debug.Log($"Caelix native status (frame {Time.frameCount}): resident {s.bricksResident}, pending plugin " +
                      $"{s.bricksPendingPlugin}, pending C# {BricksPendingCSharp}, as_builds_pending {s.asBuildsPending}, entities " +
                      $"{s.entities}, groups {s.groups}, uploaded_total {s.uploadedTotal}, removed_total {s.removedTotal}, " +
                      $"encoding {s.sceneEncoding}, renders {s.renders}, failed {s.failedEvents}, last error {s.lastError}, " +
                      $"GPU flush {s.flushMs:F3} ms trace {s.traceMs:F3} ms composite {s.compositeMs:F3} ms denoise {s.denoiseMs:F3} ms; " +
                      $"Tick {LastTickMilliseconds:F3} ms, deferred {TicksDeferred}, skipped {PacketsSkipped}.", this);
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

        private unsafe void ParseMaterialsIfChanged()
        {
            if (ReferenceEquals(parsedMaterialsAsset, materialsJson) && (parsedMaterials != null || materialsJson == null)) return;
            parsedMaterialsAsset = materialsJson;
            parsedMaterials = null;
            if (materialsJson == null) return;
            MaterialJsonList list;
            try
            {
                list = JsonUtility.FromJson<MaterialJsonList>("{\"items\":" + materialsJson.text + "}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Caelix native: {materialsJson.name} is not a material JSON array: {ex.Message}", this);
                return;
            }
            if (list?.items == null) return;
            var entries = new List<CaelixNativeMaterialEntry>(list.items.Length);
            for (int i = 0; i < list.items.Length; i++)
            {
                MaterialJsonEntry item = list.items[i];
                if (item == null) continue;
                int limit = item.transparent ? 0x1FF : 0x7FFF;
                if (item.id < 0 || item.id > limit)
                {
                    Debug.LogWarning($"Caelix native: {materialsJson.name} entry {i}: id {item.id} out of range (0..{limit}); skipped.", this);
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
            parsedMaterials = entries.ToArray();
            if (verboseLogging)
            {
                Debug.Log($"Caelix native: {parsedMaterials.Length} material entries from {materialsJson.name}.", this);
            }
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
        /// Copies waiting bricks into the packet: per view, the keys (12 bytes each) then the
        /// 16-byte aligned block values (1024 bytes each) and one upload record. A key whose brick
        /// no longer exists is consumed without a copy (its removal is, or was, a remove record).
        /// Stops at the first view that does not fit completely; the rest waits for a later packet.
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
        /// Fills the next packet of <paramref name="view"/> (0 Game view, 1 Scene view) for
        /// <paramref name="camera"/> at <paramref name="width"/> x <paramref name="height"/> pixels
        /// and returns its address. <paramref name="releaseViews"/> is true when a target was
        /// replaced or the scene encoding changed: the caller then issues the plugin's release event,
        /// with the packet as its data, before the render event. False when the view index is out of
        /// range or no packet slot is free (the frame is skipped).
        /// </summary>
        public bool TryPrepareFrame(int view, Camera camera, Matrix4x4 jitteredProjection, int width, int height,
            out IntPtr packet, out bool releaseViews)
        {
            packet = IntPtr.Zero;
            releaseViews = false;
            if (view < 0 || view >= (int)CaelixNativeBridge.MaxViews) return false;
            if (width <= 0 || height <= 0) return false;

            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            FreeDeferred(status.lastConsumedSequence);
            // A domain reload resets s_sequence to 0 but the plugin keeps its newest consumed
            // sequence: continue above it, or every slot would look consumed while the render
            // thread may still read it.
            if (s_sequence < status.lastConsumedSequence) s_sequence = status.lastConsumedSequence;

            // The scene packet Tick filled goes with the first view prepared in the frame; otherwise
            // (also for the next view) a free slot with an empty scene section.
            int slot = -1;
            for (int i = 0; i < RingSize; i++)
            {
                if (slotFilled[i]) { slot = i; break; }
            }
            if (slot < 0)
            {
                slot = FindFreeSlot(status.lastConsumedSequence);
                if (slot < 0)
                {
                    PacketsSkipped++;
                    return false;
                }
                EnsureSlotCapacity(slot, Marshal.SizeOf<CaelixNativePacket>());
                slotScene[slot] = new CaelixNativeSceneSection { totalBytes = (uint)Marshal.SizeOf<CaelixNativePacket>() };
                slotEncoding[slot] = tickEncoding == uint.MaxValue ? ResolveEncoding() : tickEncoding;
            }

            ViewTargets targets = viewTargets[view];
            // Replacing a target bumps the generation, which makes the feature issue the release event first.
            if (targets.Ensure(width, height, view)) generation = ++s_generation;
            if (generation != s_releasedGeneration)
            {
                releaseViews = true;
                s_releasedGeneration = generation;
            }
            if (slotEncoding[slot] != s_issuedEncoding)
            {
                // The release event recreates the plugin's scene when the encoding differs.
                releaseViews = true;
                s_issuedEncoding = slotEncoding[slot];
            }

            // A copy: the slot's stored section stays as Tick wrote it.
            CaelixNativeSceneSection scene = slotScene[slot];
            scene.view = (uint)view;
            var data = new CaelixNativePacket
            {
                size = (uint)Marshal.SizeOf<CaelixNativePacket>(),
                version = CaelixNativeBridge.PacketVersion,
                sequence = ++s_sequence,
                encoding = slotEncoding[slot],
                targetGeneration = generation,
                color = targets.Color.ToPacket(width, height),
                depth = targets.Depth.ToPacket(width, height),
                motion = targets.Motion.ToPacket(width, height),
                albedo = targets.Albedo.ToPacket(width, height),
                scene = scene,
            };
            data.camera = BuildCamera(view, camera, jitteredProjection, width, height);
            data.options = BuildOptions(view, data.camera);
            unsafe
            {
                *(CaelixNativePacket*)slots[slot] = data;
            }
            bool hasScene = scene.entityCount != 0 || scene.removeCount != 0 || scene.uploadCount != 0 ||
                            scene.materialMode != 0 || scene.sceneFlags != 0;
            slotSequence[slot] = data.sequence;
            slotFrame[slot] = Time.frameCount;
            slotHadScene[slot] = hasScene;
            slotFilled[slot] = false;
            if (slot == lastUploadSlot && scene.uploadCount != 0) lastUploadSequence = data.sequence;
            PacketsIssued++;
            packet = slots[slot];
            return true;
        }

        private int FindFreeSlot(ulong lastConsumed)
        {
            int frame = Time.frameCount;
            for (int i = 0; i < RingSize; i++)
            {
                if (slotFilled[i]) continue;
                if (slotSequence[i] == 0 || lastConsumed >= slotSequence[i]) return i;
            }
            for (int i = 0; i < RingSize; i++)
            {
                if (slotFilled[i]) continue;
                if (frame - slotFrame[i] > StaleSlotFrames)
                {
                    if (slotHadScene[i])
                    {
                        // Its scene records never reached the plugin: send the whole world again.
                        Debug.LogError($"Caelix native: packet {slotSequence[i]} with scene records was never consumed by the " +
                                       "render thread; the world is sent again.", this);
                        ResendEverything();
                    }
                    slotHadScene[i] = false;
                    return i;
                }
            }
            return -1;
        }

        private static void FreeDeferred(ulong lastConsumed)
        {
            for (int i = s_deferredFree.Count - 1; i >= 0; i--)
            {
                if (lastConsumed >= s_deferredFree[i].sequence)
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
        /// <see cref="LastJitter"/> follows view 0 only.
        /// </summary>
        private unsafe CaelixNativeCamera BuildCamera(int view, Camera camera, Matrix4x4 jitteredProjection, int width,
            int height)
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
            if (view == 0) LastJitter = new Vector2(jitterX, jitterY);
            return result;
        }

        /// <summary>The options of <paramref name="view"/>, with its own mode, debug view and reset state.</summary>
        private unsafe CaelixNativeRenderOptions BuildOptions(int view, in CaelixNativeCamera camera)
        {
            RenderMode viewMode = ModeForView(view);
            ViewFrameState state = viewFrames[view];
            bool lighting = viewMode != RenderMode.Primary;
            uint flags = FlagFlipY;
            if (viewMode == RenderMode.Primary && shading) flags |= FlagShading;
            if (lighting && skyOnMiss) flags |= FlagSkyOnMiss;
            if (lighting && sunNextEventEstimation) flags |= FlagSunNee;
            if (viewMode == RenderMode.PathTrace && deltaCheckerboard) flags |= FlagDeltaCheckerboard;

            var o = new CaelixNativeRenderOptions
            {
                debugView = (uint)DebugViewForView(view),
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
            };
            Vector3 sun = lighting ? sunDirection : primarySunDirection;
            o.sunDir[0] = sun.x; o.sunDir[1] = sun.y; o.sunDir[2] = sun.z;
            o.skyColor[0] = primarySkyColor.x; o.skyColor[1] = primarySkyColor.y; o.skyColor[2] = primarySkyColor.z;
            o.sunRadiance[0] = sunRadiance.x; o.sunRadiance[1] = sunRadiance.y; o.sunRadiance[2] = sunRadiance.z;
            o.skyZenith[0] = skyZenith.x; o.skyZenith[1] = skyZenith.y; o.skyZenith[2] = skyZenith.z;
            o.skyHorizon[0] = skyHorizon.x; o.skyHorizon[1] = skyHorizon.y; o.skyHorizon[2] = skyHorizon.z;
            o.skyGround[0] = skyGround.x; o.skyGround[1] = skyGround.y; o.skyGround[2] = skyGround.z;

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
