using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Caelix.Rendering.RayQuery
{
    /// <summary>
    /// Per-RTAS-instance record read by the ray query kernel, indexed by <c>InstanceID()</c>.
    /// Mirrors the HLSL <c>CaelixRayQueryInstance</c> struct in <c>CaelixRayQueryTrace.hlsl</c>.
    /// </summary>
    /// <remarks>
    /// The previous transform is stored as four rows rather than a <see cref="Matrix4x4"/> so the
    /// layout does not depend on HLSL matrix packing rules.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixRayQueryInstance
    {
        public Vector4 prevRow0;
        public Vector4 prevRow1;
        public Vector4 prevRow2;
        public Vector4 prevRow3;

        /// <summary>Word offset of this sector's first brick inside its brick pool page.</summary>
        public uint brickBase;

        /// <summary>Per-sector seed for the voxel face hash.</summary>
        public uint hashSeed;

        /// <summary>
        /// Index of the <see cref="CaelixBrickPool"/> page holding this sector's bricks. The kernel
        /// switches every brick load on it, because one buffer cannot hold a large scene.
        /// </summary>
        public uint page;

        /// <summary>Unique allocation lifetime used by sparse GI face keys. Matches HLSL pad1.</summary>
        public uint pad1;

        /// <summary>Size of one record in bytes; the structured buffer's stride.</summary>
        public const int Stride = 80;
    }

    /// <summary>
    /// The structured buffer of <see cref="CaelixRayQueryInstance"/> records bound as
    /// <c>g_Instances</c>, plus the slot allocator that keeps RTAS instance IDs stable.
    /// </summary>
    /// <remarks>
    /// A slot index is what the renderer passes to
    /// <see cref="UnityEngine.Rendering.RayTracingAccelerationStructure.AddInstance(UnityEngine.Rendering.RayTracingAABBsInstanceConfig, Matrix4x4, uint)"/>,
    /// so it must survive an instance being removed and re-added. Slots are therefore owned by the
    /// sector renderer for its whole life and only returned on removal.
    /// </remarks>
    public sealed class CaelixRayQueryInstanceTable : IDisposable
    {
        private const int InitialCapacity = 64;

        /// <summary>The GPU buffer bound as <c>g_Instances</c>. Always allocated.</summary>
        public GraphicsBuffer Buffer { get; private set; }

        /// <summary>Number of slots the current buffer holds.</summary>
        public int Capacity { get; private set; }

        /// <summary>Estimated VRAM usage of the table in bytes.</summary>
        public ulong VRAMUsage => (ulong)Capacity * CaelixRayQueryInstance.Stride;

        private CaelixRayQueryInstance[] host;
        private readonly Stack<int> freeSlots = new();
        private int highWater;
        private bool dirty;
        private static uint nextLifetimeToken;

        public uint GetLifetimeToken(int slot) => host[slot].pad1;

        public CaelixRayQueryInstanceTable()
        {
            Grow(InitialCapacity);
        }

        /// <summary>Reserves a slot, reusing a freed one when possible.</summary>
        public int Allocate()
        {
            if (freeSlots.Count == 0 && highWater >= Capacity)
            {
                Grow(Capacity * 2);
            }

            int slot = freeSlots.Count > 0 ? freeSlots.Pop() : highWater++;
            if (++nextLifetimeToken == 0) ++nextLifetimeToken;
            host[slot].pad1 = nextLifetimeToken;
            dirty = true;
            return slot;
        }

        /// <summary>Returns a slot and zeroes its record, so a stale transform cannot be read.</summary>
        public void Free(int slot)
        {
            if (slot < 0 || slot >= Capacity)
            {
                return;
            }

            host[slot] = default;
            freeSlots.Push(slot);
            dirty = true;
        }

        /// <summary>Writes one slot's record. Takes effect on the next <see cref="Flush"/>.</summary>
        /// <param name="brickBaseWords">Word offset of the sector's first brick inside <paramref name="page"/>.</param>
        /// <param name="page">Index of the brick pool page holding the sector's bricks.</param>
        public void Set(
            int slot, in Matrix4x4 prevObjectToWorld, int brickBaseWords, int page, uint hashSeed)
        {
            host[slot] = new CaelixRayQueryInstance
            {
                prevRow0 = prevObjectToWorld.GetRow(0),
                prevRow1 = prevObjectToWorld.GetRow(1),
                prevRow2 = prevObjectToWorld.GetRow(2),
                prevRow3 = prevObjectToWorld.GetRow(3),
                brickBase = (uint)brickBaseWords,
                hashSeed = hashSeed,
                page = (uint)page,
                pad1 = host[slot].pad1
            };

            dirty = true;
        }

        /// <summary>Uploads the host copy if anything changed since the last flush.</summary>
        public void Flush()
        {
            if (!dirty)
            {
                return;
            }

            Buffer.SetData(host, 0, 0, Capacity);
            dirty = false;
        }

        private void Grow(int capacity)
        {
            CaelixRayQueryInstance[] newHost = new CaelixRayQueryInstance[capacity];
            if (host != null)
            {
                Array.Copy(host, newHost, host.Length);
            }

            host = newHost;

            Buffer?.Dispose();
            Buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, capacity, CaelixRayQueryInstance.Stride);
            Capacity = capacity;

            // A fresh buffer holds garbage, so the whole host copy has to go back up.
            dirty = true;
        }

        public void Dispose()
        {
            Buffer?.Dispose();
            Buffer = null;
            host = null;
            freeSlots.Clear();
            highWater = 0;
            Capacity = 0;
        }
    }
}
