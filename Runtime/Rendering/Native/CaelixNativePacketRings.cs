using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// The unmanaged packet memory of <see cref="CaelixNativeRenderer"/>: one ring per packet kind
    /// (frame, view, release), the sequence counters, and the deferred frees.
    /// </summary>
    /// <remarks>
    /// The render thread reads a packet during its plugin event and then publishes the packet's
    /// sequence number for its kind (<see cref="CaelixNativeStatus"/>); a slot is reused only after
    /// that. Frame packets grow on demand to hold the records of one frame; view and release
    /// packets have a fixed size and live in one block each.
    /// <para>
    /// Sequence numbers must never repeat within one plugin lifetime. The plugin outlives
    /// components and domain reloads; a domain reload resets the static counters to 0, so every
    /// kind continues its counter above the plugin's last consumed sequence of that kind
    /// (<see cref="Maintain"/>). Memory the render thread may still read is freed once the plugin
    /// reports its sequence.
    /// </para>
    /// </remarks>
    internal sealed class CaelixNativePacketRings
    {
        /// <summary>Frame packet slots: one packet per frame, the render thread may be a frame or two behind.</summary>
        public const int FrameRingSize = 3;
        /// <summary>Release packet slots (fixed size).</summary>
        public const int ReleaseRingSize = 4;
        /// <summary>View packet slots (fixed size): every view issues one per frame, two frames in flight.</summary>
        public static readonly int ViewRingSize = (int)CaelixNativeBridge.MaxViews * 2 + 2;

        public static readonly int FramePacketBytes = Marshal.SizeOf<CaelixNativeFramePacket>();
        public static readonly int ViewPacketBytes = Marshal.SizeOf<CaelixNativeViewPacket>();
        public static readonly int ReleasePacketBytes = Marshal.SizeOf<CaelixNativeReleasePacket>();

        private readonly IntPtr[] frameSlots = new IntPtr[FrameRingSize];
        private readonly long[] frameCapacity = new long[FrameRingSize];
        private readonly ulong[] frameSequence = new ulong[FrameRingSize];
        private readonly long[] frameBricks = new long[FrameRingSize];

        private IntPtr viewRing;
        private readonly ulong[] viewSequence = new ulong[ViewRingSize];

        private IntPtr releaseRing;
        private readonly ulong[] releaseSequence = new ulong[ReleaseRingSize];

        private static ulong s_frameSequence;
        private static ulong s_viewSequence;
        private static ulong s_releaseSequence;
        private static readonly List<(IntPtr memory, uint kind, ulong sequence)> s_deferredFree =
            new List<(IntPtr, uint, ulong)>();

        /// <summary>True between <see cref="Allocate"/> and <see cref="Release"/>.</summary>
        public bool IsAllocated => viewRing != IntPtr.Zero;

        /// <summary>Allocates the view and release rings; frame slots grow on first use.</summary>
        public void Allocate()
        {
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
        }

        /// <summary>Frees every block now, or once the render thread consumed its newest packet.</summary>
        public void Release(in CaelixNativeStatus status)
        {
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
        }

        /// <summary>
        /// Frees the deferred blocks the plugin is done with and continues the sequence counters
        /// above the plugin's consumed values (see the class remarks). Call with every fresh status.
        /// </summary>
        public static void Maintain(in CaelixNativeStatus status)
        {
            for (int i = s_deferredFree.Count - 1; i >= 0; i--)
            {
                if (ConsumedSequence(status, s_deferredFree[i].kind) >= s_deferredFree[i].sequence)
                {
                    Marshal.FreeHGlobal(s_deferredFree[i].memory);
                    s_deferredFree.RemoveAt(i);
                }
            }
            if (s_frameSequence < status.lastConsumedFrameSequence) s_frameSequence = status.lastConsumedFrameSequence;
            if (s_viewSequence < status.lastConsumedViewSequence) s_viewSequence = status.lastConsumedViewSequence;
            if (s_releaseSequence < status.lastConsumedReleaseSequence) s_releaseSequence = status.lastConsumedReleaseSequence;
        }

        // ---------------- frame packets ----------------

        /// <summary>A frame slot the render thread is done with, or -1.</summary>
        public int FindFreeFrameSlot(in CaelixNativeStatus status)
        {
            for (int i = 0; i < FrameRingSize; i++)
            {
                if (frameSequence[i] == 0 || status.lastConsumedFrameSequence >= frameSequence[i]) return i;
            }
            return -1;
        }

        /// <summary>The memory of frame slot <paramref name="slot"/> (zero before its first <see cref="EnsureFrameCapacity"/>).</summary>
        public IntPtr FramePacket(int slot) => frameSlots[slot];

        /// <summary>Grows frame slot <paramref name="slot"/> to at least <paramref name="bytes"/>; the contents are not kept.</summary>
        public void EnsureFrameCapacity(int slot, long bytes)
        {
            if (frameSlots[slot] != IntPtr.Zero && frameCapacity[slot] >= bytes) return;
            if (frameSlots[slot] != IntPtr.Zero) Marshal.FreeHGlobal(frameSlots[slot]);
            frameSlots[slot] = Marshal.AllocHGlobal(new IntPtr(bytes));
            frameCapacity[slot] = bytes;
        }

        /// <summary>
        /// Marks frame slot <paramref name="slot"/> as issued with <paramref name="bricks"/> bricks and
        /// returns the sequence number its header must carry.
        /// </summary>
        public ulong CommitFrame(int slot, long bricks)
        {
            ulong sequence = ++s_frameSequence;
            frameSequence[slot] = sequence;
            frameBricks[slot] = bricks;
            return sequence;
        }

        /// <summary>Bricks of issued frame packets the render thread has not consumed yet.</summary>
        public long BricksInFlight(in CaelixNativeStatus status)
        {
            long bricks = 0;
            for (int i = 0; i < FrameRingSize; i++)
            {
                if (frameSequence[i] > status.lastConsumedFrameSequence) bricks += frameBricks[i];
            }
            return bricks;
        }

        // ---------------- view packets ----------------

        /// <summary>
        /// Takes a free view packet slot: its memory and the sequence number its header must carry.
        /// Zero when every slot is still waiting for the render thread.
        /// </summary>
        public IntPtr TakeViewPacket(in CaelixNativeStatus status, out ulong sequence)
        {
            sequence = 0;
            if (viewRing == IntPtr.Zero) return IntPtr.Zero;
            for (int i = 0; i < ViewRingSize; i++)
            {
                if (viewSequence[i] != 0 && status.lastConsumedViewSequence < viewSequence[i]) continue;
                sequence = ++s_viewSequence;
                viewSequence[i] = sequence;
                return viewRing + i * ViewPacketBytes;
            }
            return IntPtr.Zero;
        }

        // ---------------- release packets ----------------

        public bool HasFreeRelease(in CaelixNativeStatus status)
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
        /// waiting for the render thread (the caller tries again later). <paramref name="sequence"/>
        /// is the packet's sequence (0 when none was filled): the plugin publishes it as
        /// <c>lastConsumedReleaseSequence</c> once the release ran.
        /// </summary>
        public IntPtr FillRelease(uint flags, uint view, in CaelixNativeStatus status, out ulong sequence)
        {
            sequence = 0;
            if (releaseRing == IntPtr.Zero) return IntPtr.Zero;
            for (int i = 0; i < ReleaseRingSize; i++)
            {
                if (releaseSequence[i] != 0 && status.lastConsumedReleaseSequence < releaseSequence[i]) continue;
                IntPtr memory = releaseRing + i * ReleasePacketBytes;
                releaseSequence[i] = WriteRelease(memory, flags, view);
                sequence = releaseSequence[i];
                return memory;
            }
            return IntPtr.Zero;
        }

        public IntPtr FillRelease(uint flags, uint view, in CaelixNativeStatus status) =>
            FillRelease(flags, view, status, out _);

        /// <summary>
        /// A release packet in a block of its own, freed once the plugin consumed it: for a caller
        /// that cannot wait for a free ring slot (the component is being disabled).
        /// </summary>
        public static IntPtr FillDetachedRelease(uint flags, uint view)
        {
            IntPtr memory = Marshal.AllocHGlobal(ReleasePacketBytes);
            ulong sequence = WriteRelease(memory, flags, view);
            s_deferredFree.Add((memory, CaelixNativeBridge.PacketRelease, sequence));
            return memory;
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

        // ---------------- helpers ----------------

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
    }
}
