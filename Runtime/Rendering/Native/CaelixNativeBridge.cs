using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Caelix.Rendering.Native
{
    /// <summary>Mirror of <c>CaelixCamera</c> (caelix.h). 144 bytes.</summary>
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
        /// <summary>Column-major 4x4: element (row r, column c) = projection[c * 4 + r].</summary>
        public fixed float projection[16];
    }

    /// <summary>Mirror of <c>CaelixRenderOptions</c> (caelix.h). 128 bytes.</summary>
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

    /// <summary>Mirror of <c>CaelixUnityPacket</c> (caelix_unity.h). 368 bytes.</summary>
    /// <remarks>
    /// The fixed part of a packet. The scene records follow it in the same memory block; the
    /// offsets are bytes from the packet start (see caelix_unity.h for the apply order).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativePacket
    {
        public uint size;
        public uint version;
        public ulong sequence;
        public uint encoding;
        public uint targetGeneration;
        public CaelixNativeTarget color;
        public CaelixNativeCamera camera;
        public CaelixNativeRenderOptions options;

        // Scene section (version 2).
        public CaelixNativeSceneSection scene;
    }

    /// <summary>The scene section of <see cref="CaelixNativePacket"/> (version 2). 48 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeSceneSection
    {
        /// <summary><c>CAELIX_UNITY_SCENE_*</c>.</summary>
        public uint sceneFlags;
        /// <summary>The whole block: the packet struct plus every record.</summary>
        public uint totalBytes;
        public uint entityCount;
        public uint entitiesOffset;
        public uint removeCount;
        public uint removesOffset;
        public uint uploadCount;
        public uint uploadsOffset;
        /// <summary><c>CAELIX_UNITY_MATERIALS_*</c>.</summary>
        public uint materialMode;
        public uint materialCount;
        public uint materialsOffset;
        public uint pad;
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

    /// <summary>Mirror of <c>CaelixUnityMaterialEntry</c> (caelix_unity.h). 52 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeMaterialEntry
    {
        public uint code;
        public CaelixNativeMaterial material;
    }

    /// <summary>Mirror of <c>CaelixUnityStatus</c> (caelix_unity.h). 208 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixNativeStatus
    {
        public uint size;
        public uint packetSize;
        public uint packetVersion;
        public uint unityRenderer;
        public uint interceptRegistered;
        public uint interceptCalled;
        public uint instancePatched;
        public uint devicePatched;
        public uint deviceAdopted;
        public int lastError;
        public int renderEventId;
        public int releaseEventId;
        public ulong lastConsumedSequence;
        public ulong lastUnityFrame;
        public ulong lastSafeFrame;
        public ulong lastPluginFrame;
        public ulong renders;
        public ulong failedEvents;
        public ulong viewReleases;
        // Version 2: the scene, as of the newest flush.
        /// <summary>Encoding of the live scene; 0xFFFFFFFF = no scene.</summary>
        public uint sceneEncoding;
        public uint entities;
        public uint groups;
        public uint sceneRecreations;
        public ulong bricksResident;
        public ulong bricksPendingPlugin;
        public ulong asBuildsPending;
        public ulong uploadedTotal;
        public ulong removedTotal;
        public ulong scenePackets;
        public ulong recordBytes;
        public ulong blasBytes;
        public float flushMs;
        public float traceMs;
        public float compositeMs;
        public float denoiseMs;
        public ulong timingsFrame;
    }

    /// <summary>
    /// P/Invoke surface of the native plugin <c>caelix_unity</c> (Caelix-Renderer/plugin/unity).
    /// </summary>
    /// <remarks>
    /// The plugin is preloaded by Unity; these calls only read its status and the event callback.
    /// All renderer work runs on the render thread inside plugin events issued through
    /// <see cref="UnityEngine.Rendering.CommandBuffer.IssuePluginEventAndData"/>.
    /// </remarks>
    public static class CaelixNativeBridge
    {
        private const string Dll = "caelix_unity";

        /// <summary>Must equal <c>CAELIX_UNITY_PACKET_VERSION</c>.</summary>
        public const uint PacketVersion = 2;

        // caelix_unity.h constants.
        public const uint EntityAdd = 1u;
        public const uint EntityRemove = 2u;
        public const uint EntityTransform = 4u;
        public const uint SceneReset = 1u;
        public const uint MaterialsNone = 0u;
        public const uint MaterialsFull = 1u;
        public const uint MaterialsPartial = 2u;

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

        /// <summary>The plugin's <c>UnityRenderingEventAndData</c> callback.</summary>
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

            int packetSize = Marshal.SizeOf<CaelixNativePacket>();
            int statusSize = Marshal.SizeOf<CaelixNativeStatus>();
            bool recordsOk = Marshal.SizeOf<CaelixNativeEntity>() == 48 && Marshal.SizeOf<CaelixNativeBrickRemove>() == 16 &&
                             Marshal.SizeOf<CaelixNativeUpload>() == 16 && Marshal.SizeOf<CaelixNativeMaterial>() == 48 &&
                             Marshal.SizeOf<CaelixNativeMaterialEntry>() == 52 && packetSize == 368 && statusSize == 208;
            if (!recordsOk)
            {
                Debug.LogError("Caelix native: a record struct of CaelixNativeBridge.cs has the wrong size (see caelix_unity.h).");
                return;
            }
            if (status.size != statusSize || status.packetSize != packetSize || status.packetVersion != PacketVersion)
            {
                Debug.LogError(
                    $"Caelix native: struct layout mismatch between C# and the plugin: packet {packetSize} B (plugin " +
                    $"{status.packetSize} B), status {statusSize} B (plugin {status.size} B), version {PacketVersion} " +
                    $"(plugin {status.packetVersion}). Rebuild the plugin or update CaelixNativeBridge.cs.");
                return;
            }

            s_available = true;
            Debug.Log(
                $"Caelix native: plugin status: renderer {status.unityRenderer}, intercept registered " +
                $"{status.interceptRegistered}, called {status.interceptCalled}, instance patched {status.instancePatched}, " +
                $"device patched {status.devicePatched}, device adopted {status.deviceAdopted}, last error {status.lastError}, " +
                $"events {status.renderEventId}/{status.releaseEventId}, packet {packetSize} B.");
        }
    }
}
