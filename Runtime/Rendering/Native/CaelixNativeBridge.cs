using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Caelix.Rendering.Native
{
    /// <summary>Mirror of <c>CaelixUnityPacketHeader</c> (caelix_unity.h). 24 bytes, the start of every packet.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativePacketHeader
    {
        /// <summary>Size of the packet struct of <see cref="kind"/>.</summary>
        public uint size;
        /// <summary><see cref="CaelixNativeBridge.PacketVersion"/>.</summary>
        public uint version;
        /// <summary><c>CAELIX_UNITY_PACKET_*</c>.</summary>
        public uint kind;
        public uint pad;
        /// <summary>&gt; 0, strictly increasing per kind.</summary>
        public ulong sequence;
    }

    /// <summary>Mirror of <c>CaelixUnityTarget</c> (caelix_unity.h). 24 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeTarget
    {
        public IntPtr nativeTexture;
        public IntPtr nativeRenderBuffer;
        public uint width;
        public uint height;
    }

    /// <summary>Mirror of <c>CaelixUnityEntity</c> (caelix_unity.h). 48 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct CaelixNativeEntity
    {
        public fixed byte guid[16];
        public fixed float position[3];
        public fixed float rotation[4];
        /// <summary><c>CAELIX_UNITY_ENTITY_*</c>; 0 = a reference only.</summary>
        public uint flags;
    }

    /// <summary>Mirror of <c>CaelixUnityBrickRemove</c> (caelix_unity.h). 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeBrickRemove
    {
        public uint entityIndex;
        public int keyX, keyY, keyZ;
    }

    /// <summary>Mirror of <c>CaelixUnityUpload</c> (caelix_unity.h). 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeUpload
    {
        public uint entityIndex;
        public uint brickCount;
        public uint keysOffset;
        public uint blocksOffset;
    }

    /// <summary>Mirror of <c>CaelixMaterial</c> (caelix.h). 48 bytes, linear values.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct CaelixNativeMaterial
    {
        public fixed float albedo[3];
        public float smoothness;
        public fixed float emission[3];
        public float metallic;
        public float ior;
        public float extinction;
        public fixed float pad[2];
    }

    /// <summary>Mirror of <c>CaelixMaterialEntry</c> (caelix.h). 52 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeMaterialEntry
    {
        public uint code;
        public CaelixNativeMaterial material;
    }

    /// <summary>Mirror of <c>CaelixSkyCubemap</c> (caelix.h). 24 bytes. The plugin fills it from the view packet's sky target.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeSkyCubemap
    {
        /// <summary>VkImage; 0 = the procedural sky.</summary>
        public ulong image;
        public uint format;
        public uint layout;
        public uint size;
        public uint mipLevels;
    }

    /// <summary>Mirror of <c>CaelixRenderOptions</c> (caelix.h). 192 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct CaelixNativeRenderOptions
    {
        public uint debugView;
        public uint flags;
        /// <summary>xyz + pad.</summary>
        public fixed float sunDir[4];
        /// <summary>rgb + pad.</summary>
        public fixed float skyColor[4];
        public float missDepth;
        public float missViewZ;
        public uint mode;
        public uint bounceCount;
        public uint denoise;
        public uint resetAccumulation;
        public float exposure;
        public fixed float sunRadiance[3];
        public float sunAngularRadius;
        public fixed float skyZenith[3];
        public fixed float skyHorizon[3];
        public fixed float skyGround[3];
        public uint frameSeed;
        public uint bounceCountTransparent;
        /// <summary>Multiplies the emission of every material; 0 is treated as 1.</summary>
        public float emissionScale;
        /// <summary>Lighting modes: extinction of the air per world unit; 0 = no fog.</summary>
        public float fogDensity;
        /// <summary>Radiance of the fog, linear RGB.</summary>
        public fixed float fogColor[3];
        public float pad3;
        /// <summary>Written by the plugin from <see cref="CaelixNativeViewPacket.sky"/>; C# leaves it zero.</summary>
        public CaelixNativeSkyCubemap sky;
        /// <summary><c>CaelixUpscaler</c>: <see cref="CaelixNativeBridge.UpscalerNone"/>, <see cref="CaelixNativeBridge.UpscalerDlssSr"/> or <see cref="CaelixNativeBridge.UpscalerDlssRr"/>. Path trace mode only.</summary>
        public uint upscaler;
        /// <summary><c>CaelixUpscaleQuality</c>: DLAA 0, Quality 1, Balanced 2, Performance 3, Ultra Performance 4.</summary>
        public uint upscaleQuality;
        /// <summary>0 = the NGX default; else the raw NGX preset value of the chosen upscaler.</summary>
        public uint upscalePreset;
        /// <summary><c>CAELIX_UPSCALE_FLAG_*</c>: <see cref="CaelixNativeBridge.UpscaleFlagResetHistory"/>, <see cref="CaelixNativeBridge.UpscaleFlagAutoExposure"/>.</summary>
        public uint upscaleFlags;
    }

    /// <summary>Mirror of <c>CaelixCamera</c> (caelix.h). 152 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct CaelixNativeCamera
    {
        /// <summary>xyz + pad (the C struct has a pad float after each vector).</summary>
        public fixed float position[4];
        public fixed float right[4];
        public fixed float up[4];
        public fixed float forward[4];
        public float tanHalfFovY;
        public float aspect;
        public float nearZ;
        public float farZ;
        /// <summary>Column-major 4x4: element (row r, column c) = projection[c * 4 + r]. Without jitter.</summary>
        public fixed float projection[16];
        /// <summary>Sub-pixel offset of the primary sample in pixels, x right, y DOWN (caelix.h).</summary>
        public fixed float jitter[2];
    }

    /// <summary>Mirror of <c>CaelixUnityFramePacket</c> (caelix_unity.h). 80 bytes.</summary>
    /// <remarks>
    /// One per Unity frame, with the frame event. The scene records follow it in the same memory
    /// block; the offsets are bytes from the packet start (see caelix_unity.h for the apply order).
    /// The session config (encoding, staging size, material entries) is read only with
    /// <see cref="CaelixNativeBridge.FrameReset"/>.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeFramePacket
    {
        public CaelixNativePacketHeader header;
        /// <summary><c>CAELIX_UNITY_FRAME_*</c>.</summary>
        public uint flags;
        /// <summary>The whole block: the packet struct plus every record.</summary>
        public uint totalBytes;
        /// <summary>Scene block encoding: 0 packed scene colour, 1 block-ID.</summary>
        public uint encoding;
        /// <summary>Staging ring size in MiB; 0 = the library default.</summary>
        public uint stagingMib;
        public uint materialCount;
        public uint materialsOffset;
        public uint entityCount;
        public uint entitiesOffset;
        public uint removeCount;
        public uint removesOffset;
        public uint uploadCount;
        public uint uploadsOffset;
        /// <summary>The bricks of every upload record together (C# accounting).</summary>
        public uint brickCount;
        public uint pad;
    }

    /// <summary>Mirror of <c>CaelixUnityViewPacket</c> (caelix_unity.h). 504 bytes.</summary>
    /// <remarks>One per view per Unity frame, with the render event.</remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeViewPacket
    {
        public CaelixNativePacketHeader header;
        /// <summary>0 .. <see cref="CaelixNativeBridge.MaxViews"/> - 1: one view per camera.</summary>
        public uint view;
        /// <summary><c>CAELIX_UNITY_VIEW_*</c>.</summary>
        public uint flags;
        /// <summary>Changes whenever a target texture of any view was replaced (see the release event).</summary>
        public uint targetGeneration;
        public uint pad;
        public CaelixNativeTarget color;
        /// <summary>
        /// Optional geometry targets (nativeTexture zero = absent): the colour size, or the
        /// upscaler's render size (<see cref="CaelixNativeUpscalerStatus"/>) while the view upscales.
        /// </summary>
        public CaelixNativeTarget depth;
        public CaelixNativeTarget motion;
        public CaelixNativeTarget albedo;
        /// <summary>The sky cubemap of the lighting modes; nativeTexture zero = the procedural sky.</summary>
        public CaelixNativeTarget sky;
        public CaelixNativeCamera camera;
        public CaelixNativeRenderOptions options;
    }

    /// <summary>Mirror of <c>CaelixUnityReleasePacket</c> (caelix_unity.h). 32 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeReleasePacket
    {
        public CaelixNativePacketHeader header;
        /// <summary><c>CAELIX_UNITY_RELEASE_*</c>.</summary>
        public uint flags;
        /// <summary>With <see cref="CaelixNativeBridge.ReleaseView"/>: the view whose renderer goes.</summary>
        public uint view;
    }

    /// <summary>
    /// Mirror of <c>CaelixUnityUpscalerStatus</c> (caelix_unity.h). 32 bytes. The newest upscaler
    /// query of one view: the key (upscaler, quality, output size; all zero = no query yet) and its result.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeUpscalerStatus
    {
        /// <summary><c>CaelixUpscaler</c> of the query; 0 = no query yet.</summary>
        public uint upscaler;
        /// <summary><c>CaelixUpscaleQuality</c> of the query.</summary>
        public uint quality;
        /// <summary>The colour target size of the query.</summary>
        public uint outputWidth;
        public uint outputHeight;
        /// <summary>1 = the upscaler is available at this quality and size.</summary>
        public uint supported;
        /// <summary><c>CaelixUpscalerUnsupportedReason</c> when <see cref="supported"/> is 0.</summary>
        public uint reason;
        /// <summary>The size the geometry targets must have while the view upscales (0 when unsupported).</summary>
        public uint renderWidth;
        public uint renderHeight;

        /// <summary>True when this entry is the query of exactly this key.</summary>
        public bool Matches(uint upscalerValue, uint qualityValue, int width, int height) =>
            upscaler == upscalerValue && quality == qualityValue && outputWidth == (uint)width && outputHeight == (uint)height;
    }

    /// <summary>Mirror of <c>CaelixUnityStatus</c> (caelix_unity.h). 536 bytes. Counters are monotonic.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeStatus
    {
        public uint size;
        public uint packetVersion;
        public uint framePacketSize;
        public uint viewPacketSize;
        public uint releasePacketSize;
        public uint unityRenderer;
        public uint interceptRegistered;
        public uint interceptCalled;
        public uint instancePatched;
        public uint devicePatched;
        public uint deviceAdopted;
        public int lastError;
        public int frameEventId;
        public int renderEventId;
        public int releaseEventId;
        public uint pad0;

        // Packets the render thread is done with, per kind.
        public ulong lastConsumedFrameSequence;
        public ulong lastConsumedViewSequence;
        public ulong lastConsumedReleaseSequence;

        // Frames.
        public ulong lastUnityFrame;
        public ulong lastSafeFrame;
        public ulong lastFlushedFrame;
        /// <summary>Frame events that flushed.</summary>
        public ulong frames;
        public ulong renders;
        public ulong releases;
        public ulong failedEvents;

        // The scene, as of the newest flush.
        /// <summary>Encoding of the live scene; <see cref="CaelixNativeBridge.NoScene"/> = no scene.</summary>
        public uint sceneEncoding;
        public uint entities;
        public uint groups;
        public uint sceneResets;
        /// <summary>Bit v = view v has a renderer.</summary>
        public uint liveViews;
        /// <summary>1 = Unity's device has every Vulkan device extension the upscalers need.</summary>
        public uint upscalerExtensions;
        public ulong bricksResident;
        public ulong asBuildsPending;
        public ulong uploadedTotal;
        public ulong removedTotal;
        /// <summary>Bricks the staging ring refused; C# treats growth as an error.</summary>
        public ulong bricksDropped;
        /// <summary>Upload capacity of the staging ring after the newest flush, in bricks.</summary>
        public ulong uploadBricksFree;
        /// <summary>The most bricks one frame packet should carry.</summary>
        public ulong uploadBricksCap;
        public ulong recordBytes;
        public ulong blasBytes;
        public ulong stagingBytes;

        // GPU timings of the view that set CAELIX_UNITY_VIEW_REPORT_TIMINGS.
        public float flushMs;
        public float traceMs;
        public float compositeMs;
        public float denoiseMs;
        public ulong timingsFrame;
        public uint timingsView;
        /// <summary>The upscaler of that view (evaluate and encode); 0 when it did not upscale.</summary>
        public float upscaleMs;

        // The newest upscaler query of each view (CAELIX_UNITY_MAX_VIEWS = 8 entries); read them with Upscaler(view).
        public CaelixNativeUpscalerStatus upscaler0;
        public CaelixNativeUpscalerStatus upscaler1;
        public CaelixNativeUpscalerStatus upscaler2;
        public CaelixNativeUpscalerStatus upscaler3;
        public CaelixNativeUpscalerStatus upscaler4;
        public CaelixNativeUpscalerStatus upscaler5;
        public CaelixNativeUpscalerStatus upscaler6;
        public CaelixNativeUpscalerStatus upscaler7;

        /// <summary>The newest upscaler query of <paramref name="view"/> (0 .. <see cref="CaelixNativeBridge.MaxViews"/> - 1).</summary>
        public readonly CaelixNativeUpscalerStatus Upscaler(int view)
        {
            switch (view)
            {
                case 0: return upscaler0;
                case 1: return upscaler1;
                case 2: return upscaler2;
                case 3: return upscaler3;
                case 4: return upscaler4;
                case 5: return upscaler5;
                case 6: return upscaler6;
                case 7: return upscaler7;
                default: throw new ArgumentOutOfRangeException(nameof(view));
            }
        }
    }

    /// <summary>
    /// P/Invoke surface of the native plugin <c>caelix_unity</c> (Caelix-Renderer/plugin/unity).
    /// </summary>
    /// <remarks>
    /// The plugin is preloaded by Unity; these calls only read its status and the event callback.
    /// All renderer work runs on the render thread inside plugin events issued through
    /// <see cref="UnityEngine.Rendering.CommandBuffer.IssuePluginEventAndData"/>: the frame event
    /// (one per Unity frame, scene records and flush), the render event (one per view per frame)
    /// and the release event. The contract is caelix_unity.h (packet version 6).
    /// </remarks>
    public static class CaelixNativeBridge
    {
        private const string Dll = "caelix_unity";

        /// <summary>Must equal <c>CAELIX_UNITY_PACKET_VERSION</c>.</summary>
        public const uint PacketVersion = 6;

        /// <summary>Must equal <c>CAELIX_UNITY_MAX_VIEWS</c>: the number of views, one per camera, each with its own plugin renderer.</summary>
        public const uint MaxViews = 8;

        // Struct sizes of caelix_unity.h / caelix.h.
        public const int PacketHeaderSize = 24;
        public const int TargetSize = 24;
        public const int EntitySize = 48;
        public const int BrickRemoveSize = 16;
        public const int UploadSize = 16;
        public const int MaterialSize = 48;
        public const int MaterialEntrySize = 52;
        public const int SkyCubemapSize = 24;
        public const int RenderOptionsSize = 192;
        public const int CameraSize = 152;
        public const int FramePacketSize = 80;
        public const int ViewPacketSize = 504;
        public const int ReleasePacketSize = 32;
        public const int UpscalerStatusSize = 32;
        public const int StatusSize = 536;

        // CaelixUnityPacketHeader::kind.
        public const uint PacketFrame = 1u;
        public const uint PacketView = 2u;
        public const uint PacketRelease = 3u;

        // CaelixUnityEntity::flags.
        public const uint EntityAdd = 1u;
        public const uint EntityRemove = 2u;
        public const uint EntityTransform = 4u;

        /// <summary>CaelixUnityFramePacket::flags: the first frame packet of a session (reads the config).</summary>
        public const uint FrameReset = 1u;

        /// <summary>CaelixUnityViewPacket::flags: this view's GPU timings go to the status.</summary>
        public const uint ViewReportTimings = 1u;

        // CaelixUnityReleasePacket::flags.
        /// <summary>Release the cached target image views of every renderer.</summary>
        public const uint ReleaseTargetViews = 1u;
        /// <summary>Destroy the renderer of one view.</summary>
        public const uint ReleaseView = 2u;
        /// <summary>Destroy every renderer and the scene; the next frame packet must carry <see cref="FrameReset"/>.</summary>
        public const uint ReleaseScene = 4u;

        /// <summary>CaelixUnityStatus::scene_encoding when the plugin has no scene.</summary>
        public const uint NoScene = 0xFFFFFFFFu;

        // CaelixUpscaler (caelix.h).
        public const uint UpscalerNone = 0u;
        /// <summary>NVIDIA DLSS Super Resolution; NRD stays the denoiser.</summary>
        public const uint UpscalerDlssSr = 1u;
        /// <summary>NVIDIA DLSS Ray Reconstruction; replaces NRD.</summary>
        public const uint UpscalerDlssRr = 2u;

        // CAELIX_UPSCALE_FLAG_* (caelix.h).
        /// <summary>This frame: drop the upscaler's history.</summary>
        public const uint UpscaleFlagResetHistory = 1u;
        /// <summary>DLSS SR: NGX auto exposure instead of a fixed exposure of 1.</summary>
        public const uint UpscaleFlagAutoExposure = 2u;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr caelix_unity_get_render_event_func();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void caelix_unity_status(out CaelixNativeStatus status);

        private static bool s_checked;
        private static bool s_available;
        private static IntPtr s_eventFunc;

        /// <summary>
        /// True when the plugin is loaded, its struct layouts match this file, and it adopted
        /// Unity's device. Checked once; the result is logged.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (!s_checked) Check();
                return s_available && TryGetStatus(out CaelixNativeStatus status) && status.deviceAdopted != 0;
            }
        }

        /// <summary>The plugin's <c>UnityRenderingEventAndData</c> callback (every event kind).</summary>
        public static IntPtr RenderEventFunc
        {
            get
            {
                if (!s_checked) Check();
                return s_eventFunc;
            }
        }

        /// <summary>Reads the plugin status. False when the plugin is not loaded.</summary>
        public static bool TryGetStatus(out CaelixNativeStatus status)
        {
            status = default;
            if (s_checked && !s_available) return false;
            try
            {
                caelix_unity_status(out status);
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                return false;
            }
        }

        private static void Check()
        {
            s_checked = true;
            s_available = false;
            CaelixNativeStatus status;
            try
            {
                caelix_unity_status(out status);
                s_eventFunc = caelix_unity_get_render_event_func();
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                Debug.LogError($"Caelix native: plugin caelix_unity is not loaded ({e.GetType().Name}: {e.Message}).");
                return;
            }

            string wrong = null;
            void Expect<T>(int size) where T : struct
            {
                int actual = Marshal.SizeOf<T>();
                if (actual != size) wrong = (wrong == null ? "" : wrong + ", ") + $"{typeof(T).Name} {actual} B (expected {size} B)";
            }
            Expect<CaelixNativePacketHeader>(PacketHeaderSize);
            Expect<CaelixNativeTarget>(TargetSize);
            Expect<CaelixNativeEntity>(EntitySize);
            Expect<CaelixNativeBrickRemove>(BrickRemoveSize);
            Expect<CaelixNativeUpload>(UploadSize);
            Expect<CaelixNativeMaterial>(MaterialSize);
            Expect<CaelixNativeMaterialEntry>(MaterialEntrySize);
            Expect<CaelixNativeSkyCubemap>(SkyCubemapSize);
            Expect<CaelixNativeRenderOptions>(RenderOptionsSize);
            Expect<CaelixNativeCamera>(CameraSize);
            Expect<CaelixNativeFramePacket>(FramePacketSize);
            Expect<CaelixNativeViewPacket>(ViewPacketSize);
            Expect<CaelixNativeReleasePacket>(ReleasePacketSize);
            Expect<CaelixNativeUpscalerStatus>(UpscalerStatusSize);
            Expect<CaelixNativeStatus>(StatusSize);
            if (wrong == null && (Marshal.OffsetOf<CaelixNativeStatus>(nameof(CaelixNativeStatus.upscalerExtensions)).ToInt32() != 164 ||
                                  Marshal.OffsetOf<CaelixNativeStatus>(nameof(CaelixNativeStatus.upscaleMs)).ToInt32() != 276 ||
                                  Marshal.OffsetOf<CaelixNativeStatus>(nameof(CaelixNativeStatus.upscaler0)).ToInt32() != 280))
            {
                wrong = "CaelixNativeStatus upscaler field offsets (expected 164, 276, 280)";
            }
            if (wrong != null)
            {
                Debug.LogError($"Caelix native: a struct of CaelixNativeBridge.cs has the wrong size (see caelix_unity.h): {wrong}.");
                return;
            }
            if (status.size != StatusSize || status.packetVersion != PacketVersion || status.framePacketSize != FramePacketSize ||
                status.viewPacketSize != ViewPacketSize || status.releasePacketSize != ReleasePacketSize)
            {
                Debug.LogError(
                    $"Caelix native: struct layout mismatch between C# and the plugin: version {PacketVersion} (plugin " +
                    $"{status.packetVersion}), status {StatusSize} B (plugin {status.size} B), frame packet {FramePacketSize} B " +
                    $"(plugin {status.framePacketSize} B), view packet {ViewPacketSize} B (plugin {status.viewPacketSize} B), " +
                    $"release packet {ReleasePacketSize} B (plugin {status.releasePacketSize} B). Rebuild the plugin or update " +
                    "CaelixNativeBridge.cs.");
                return;
            }

            s_available = true;
            Debug.Log(
                $"Caelix native: plugin status: renderer {status.unityRenderer}, intercept registered " +
                $"{status.interceptRegistered}, called {status.interceptCalled}, instance patched {status.instancePatched}, " +
                $"device patched {status.devicePatched}, device adopted {status.deviceAdopted}, last error {status.lastError}, " +
                $"events frame/render/release {status.frameEventId}/{status.renderEventId}/{status.releaseEventId}, " +
                $"upscaler extensions {status.upscalerExtensions}, packet version {status.packetVersion}.");
        }
    }

    /// <summary>
    /// P/Invoke of the pure helper functions of the renderer library <c>caelix_renderer</c> (caelix.h).
    /// </summary>
    /// <remarks>
    /// The library DLL is preloaded next to the plugin; these functions touch no renderer state and
    /// may be called from any thread.
    /// </remarks>
    public static class CaelixNativeLibrary
    {
        private const string Dll = "caelix_renderer";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint caelix_upscaler_jitter_phases(uint upscaler, uint renderWidth, uint outputWidth);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe void caelix_jitter_halton(uint index, uint phases, float* outJitter);

        /// <summary>
        /// The jitter sequence length the DLSS guide recommends: 8 * ceil((output / render)^2), at
        /// least 32 for DLSS RR. A zero <paramref name="renderWidth"/> counts as a ratio of 1.
        /// </summary>
        public static uint UpscalerJitterPhases(uint upscaler, uint renderWidth, uint outputWidth) =>
            caelix_upscaler_jitter_phases(upscaler, renderWidth, outputWidth);

        /// <summary>
        /// The Halton(2, 3) point number (<paramref name="index"/> % <paramref name="phases"/>) + 1
        /// minus 0.5: a jitter in [-0.5, 0.5) pixels, x right, y down (<c>CaelixCamera::jitter</c>).
        /// </summary>
        public static unsafe Vector2 JitterHalton(uint index, uint phases)
        {
            float* jitter = stackalloc float[2];
            caelix_jitter_halton(index, phases, jitter);
            return new Vector2(jitter[0], jitter[1]);
        }
    }
}
