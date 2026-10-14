using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// Scene renderer backed by the native Vulkan plugin <c>caelix_unity</c> (Caelix-Renderer).
    /// </summary>
    /// <remarks>
    /// P1 (skeleton): owns the colour target and a ring of packets. Each Game camera render,
    /// <see cref="CaelixNativeRendererFeature"/> asks for a packet (<see cref="TryPrepareFrame"/>),
    /// issues the plugin's render event with it, and blits the colour target to the camera. The
    /// plugin's scene is still empty, so the image is the renderer's sky. Feeding entities and
    /// bricks through the packets is P2.
    /// <para>
    /// Packets live in unmanaged memory. The render thread copies a packet at the start of its
    /// event and publishes the packet's sequence number; a slot is reused only after that.
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

        // caelix.h render flags.
        private const uint FlagShading = 1u << 0;
        private const uint FlagSkyOnMiss = 1u << 1;
        private const uint FlagSunNee = 1u << 2;
        private const uint FlagDeltaCheckerboard = 1u << 3;

        private const int RingSize = 3;
        /// <summary>A slot whose packet was never consumed (its event was dropped) is reused after this many frames.</summary>
        private const int StaleSlotFrames = 8;

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
        [SerializeField] private Vector3 sunDirection = new Vector3(0.45f, 0.7f, 0.55f);
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

        /// <summary>The colour target the plugin writes (RGBA16F, random write). Null before the first frame.</summary>
        public RTHandle ColorHandle => colorHandle;

        /// <summary>Packets issued since this component was enabled.</summary>
        public ulong PacketsIssued { get; private set; }

        /// <summary>Frames skipped because every packet slot was still waiting for the render thread.</summary>
        public ulong PacketsSkipped { get; private set; }

        private RenderTexture color;
        private RTHandle colorHandle;
        private IntPtr colorTexturePtr;
        private IntPtr colorRenderBufferPtr;
        private uint generation;

        private readonly IntPtr[] slots = new IntPtr[RingSize];
        private readonly ulong[] slotSequence = new ulong[RingSize];
        private readonly int[] slotFrame = new int[RingSize];
        private uint frameSeed;
        private int lastPreparedFrame = -1;
        private RenderMode lastMode;

        // Shared by every instance: sequence numbers and target generations must never repeat
        // within one plugin lifetime (the plugin outlives components and domain reloads only
        // reset these to 0, which the plugin tolerates: it only uses them for ordering checks).
        private static ulong s_sequence;
        private static uint s_generation;
        private static uint s_releasedGeneration;
        private static readonly List<(IntPtr memory, ulong sequence)> s_deferredFree = new List<(IntPtr, ulong)>();

        private void OnEnable()
        {
            if (Active != null && Active != this)
            {
                Debug.LogWarning($"Caelix native: {name} replaces {Active.name} as the active native renderer.", this);
            }
            Active = this;
            int size = Marshal.SizeOf<CaelixNativePacket>();
            for (int i = 0; i < RingSize; i++)
            {
                slots[i] = Marshal.AllocHGlobal(size);
                slotSequence[i] = 0;
                slotFrame[i] = 0;
            }
            lastMode = mode;
        }

        private void OnDisable()
        {
            if (Active == this) Active = null;
            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            for (int i = 0; i < RingSize; i++)
            {
                if (slots[i] == IntPtr.Zero) continue;
                if (slotSequence[i] == 0 || status.lastConsumedSequence >= slotSequence[i])
                {
                    Marshal.FreeHGlobal(slots[i]);
                }
                else
                {
                    // The render thread may still read it: free it once it reports the sequence.
                    s_deferredFree.Add((slots[i], slotSequence[i]));
                }
                slots[i] = IntPtr.Zero;
            }
            ReleaseTarget();
        }

        /// <summary>
        /// Fills the next packet for <paramref name="camera"/> at <paramref name="width"/> x
        /// <paramref name="height"/> pixels and returns its address. <paramref name="releaseViews"/>
        /// is true when the colour target was replaced: the caller then issues the plugin's release
        /// event before the render event. False when no packet slot is free (the frame is skipped).
        /// </summary>
        public bool TryPrepareFrame(Camera camera, int width, int height, out IntPtr packet, out bool releaseViews)
        {
            packet = IntPtr.Zero;
            releaseViews = false;
            if (width <= 0 || height <= 0) return false;

            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            FreeDeferred(status.lastConsumedSequence);

            int slot = FindFreeSlot(status.lastConsumedSequence);
            if (slot < 0)
            {
                PacketsSkipped++;
                return false;
            }

            EnsureTarget(width, height);
            if (generation != s_releasedGeneration)
            {
                releaseViews = true;
                s_releasedGeneration = generation;
            }

            var data = new CaelixNativePacket
            {
                size = (uint)Marshal.SizeOf<CaelixNativePacket>(),
                version = CaelixNativeBridge.PacketVersion,
                sequence = ++s_sequence,
                encoding = BlockEncoding.PackedSceneColor ? 0u : 1u,
                targetGeneration = generation,
                color = new CaelixNativeTarget
                {
                    nativeTexture = colorTexturePtr,
                    nativeRenderBuffer = colorRenderBufferPtr,
                    width = (uint)width,
                    height = (uint)height,
                },
                camera = BuildCamera(camera, width, height),
                options = BuildOptions(),
            };
            Marshal.StructureToPtr(data, slots[slot], false);
            slotSequence[slot] = data.sequence;
            slotFrame[slot] = Time.frameCount;
            PacketsIssued++;
            packet = slots[slot];
            return true;
        }

        private int FindFreeSlot(ulong lastConsumed)
        {
            int frame = Time.frameCount;
            for (int i = 0; i < RingSize; i++)
            {
                if (slots[i] == IntPtr.Zero) continue;
                if (slotSequence[i] == 0 || lastConsumed >= slotSequence[i] || frame - slotFrame[i] > StaleSlotFrames)
                {
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

        private void EnsureTarget(int width, int height)
        {
            if (color != null && color.width == width && color.height == height && color.IsCreated())
            {
                return;
            }
            ReleaseTarget();
            color = new RenderTexture(width, height, GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.None)
            {
                name = "Caelix Native Colour",
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            color.Create();
            colorHandle = RTHandles.Alloc(color);
            // GetNativeTexturePtr may synchronize with the render thread: once per target.
            colorTexturePtr = color.GetNativeTexturePtr();
            colorRenderBufferPtr = color.colorBuffer.GetNativeRenderBufferPtr();
            generation = ++s_generation;
        }

        private void ReleaseTarget()
        {
            if (colorHandle != null)
            {
                RTHandles.Release(colorHandle);
                colorHandle = null;
            }
            if (color != null)
            {
                color.Release();
                DestroyImmediate(color);
                color = null;
            }
            colorTexturePtr = IntPtr.Zero;
            colorRenderBufferPtr = IntPtr.Zero;
        }

        private static unsafe CaelixNativeCamera BuildCamera(Camera camera, int width, int height)
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

            // Unity's view space looks down -Z; the library's view z is +forward. So the library's
            // projection is Unity's GPU projection times diag(1, 1, -1, 1): column 2 negated.
            Matrix4x4 gpu = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            for (int c = 0; c < 4; c++)
            {
                float sign = c == 2 ? -1.0f : 1.0f;
                for (int row = 0; row < 4; row++)
                {
                    result.projection[c * 4 + row] = sign * gpu[row, c];
                }
            }
            return result;
        }

        private unsafe CaelixNativeRenderOptions BuildOptions()
        {
            bool lighting = mode != RenderMode.Primary;
            uint flags = 0;
            if (mode == RenderMode.Primary && shading) flags |= FlagShading;
            if (lighting && skyOnMiss) flags |= FlagSkyOnMiss;
            if (lighting && sunNextEventEstimation) flags |= FlagSunNee;
            if (mode == RenderMode.PathTrace && deltaCheckerboard) flags |= FlagDeltaCheckerboard;

            var o = new CaelixNativeRenderOptions
            {
                debugView = (uint)debugView,
                flags = flags,
                // Unity on Vulkan uses reversed Z (GL.GetGPUProjectionMatrix).
                missDepth = SystemInfo.usesReversedZBuffer ? 0.0f : 1.0f,
                missViewZ = 1e30f,
                mode = (uint)mode,
                bounceCount = (uint)Mathf.Clamp(bounces, 0, 14),
                denoise = mode == RenderMode.PathTrace && denoise ? 1u : 0u,
                resetAccumulation = mode != lastMode ? 1u : 0u,
                exposure = exposure,
                sunAngularRadius = sunAngularRadiusDegrees * Mathf.Deg2Rad,
                frameSeed = frameSeed,
                bounceCountTransparent = (uint)Mathf.Clamp(bouncesTransparent, 0, 15),
            };
            o.sunDir[0] = sunDirection.x; o.sunDir[1] = sunDirection.y; o.sunDir[2] = sunDirection.z;
            o.skyColor[0] = primarySkyColor.x; o.skyColor[1] = primarySkyColor.y; o.skyColor[2] = primarySkyColor.z;
            o.sunRadiance[0] = sunRadiance.x; o.sunRadiance[1] = sunRadiance.y; o.sunRadiance[2] = sunRadiance.z;
            o.skyZenith[0] = skyZenith.x; o.skyZenith[1] = skyZenith.y; o.skyZenith[2] = skyZenith.z;
            o.skyHorizon[0] = skyHorizon.x; o.skyHorizon[1] = skyHorizon.y; o.skyHorizon[2] = skyHorizon.z;
            o.skyGround[0] = skyGround.x; o.skyGround[1] = skyGround.y; o.skyGround[2] = skyGround.z;

            // One new seed per Unity frame, not per camera.
            if (Time.frameCount != lastPreparedFrame)
            {
                lastPreparedFrame = Time.frameCount;
                frameSeed++;
            }
            lastMode = mode;
            return o;
        }
    }
}
