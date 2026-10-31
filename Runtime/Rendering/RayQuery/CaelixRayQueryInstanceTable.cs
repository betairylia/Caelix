using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Caelix.Rendering.RayQuery
{
    /// <summary>
    /// Per-RTAS-instance record read by the ray query kernel, indexed by <c>InstanceID()</c>.
    /// Mirrors the HLSL <c>CaelixRayQueryInstance</c> struct in <c>CaelixInstanceRecord.hlsl</c>;
    /// the field order here must equal the HLSL field order.
    /// </summary>
    /// <remarks>
    /// The transforms are stored as rows rather than a <see cref="Matrix4x4"/> so the layout does
    /// not depend on HLSL matrix packing rules.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct CaelixRayQueryInstance
    {
        public Vector4 prevRow0;
        public Vector4 prevRow1;
        public Vector4 prevRow2;
        public Vector4 prevRow3;

        /// <summary>Row 0 of the current object-to-world matrix.</summary>
        public Vector4 curRow0;
        /// <summary>Row 1 of the current object-to-world matrix.</summary>
        public Vector4 curRow1;
        /// <summary>Row 2 of the current object-to-world matrix. Row 3 is always (0, 0, 0, 1).</summary>
        public Vector4 curRow2;

        /// <summary>Word offset of this sector's first brick inside its brick pool page.</summary>
        public uint brickBase;

        /// <summary>Per-sector seed for the voxel face hash.</summary>
        public uint hashSeed;

        /// <summary>
        /// Index of the <see cref="CaelixBrickPool"/> page holding this sector's bricks. The kernel
        /// switches every brick load on it, because one buffer cannot hold a large scene.
        /// </summary>
        public uint page;

        /// <summary>
        /// Non-zero while the slot is allocated, fresh on every <see cref="CaelixRayQueryInstanceTable.Allocate"/>.
        /// GI samples store it, so a sample whose slot was freed and reused is rejected.
        /// </summary>
        public uint lifetimeToken;

        /// <summary>Size of one record in bytes; the structured buffer's stride.</summary>
        public const int Stride = 128;
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

        /// <summary>
        /// Source of <see cref="CaelixRayQueryInstance.lifetimeToken"/>. Static, so a token is never
        /// reused while a table is recreated within one session either.
        /// </summary>
        private static uint s_NextLifetimeToken;

        public CaelixRayQueryInstanceTable()
        {
            Grow(InitialCapacity);
        }

        /// <summary>
        /// Reserves a slot, reusing a freed one when possible, and gives it a fresh non-zero
        /// lifetime token.
        /// </summary>
        public int Allocate()
        {
            int slot;
            if (freeSlots.Count > 0)
            {
                slot = freeSlots.Pop();
            }
            else
            {
                if (highWater >= Capacity)
                {
                    Grow(Capacity * 2);
                }

                slot = highWater++;
            }

            host[slot] = new CaelixRayQueryInstance { lifetimeToken = NextLifetimeToken() };
            dirty = true;
            return slot;
        }

        /// <summary>The lifetime token of <paramref name="slot"/>; 0 when the slot is not allocated.</summary>
        public uint GetLifetimeToken(int slot)
        {
            if (slot < 0 || slot >= Capacity)
            {
                return 0u;
            }

            return host[slot].lifetimeToken;
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

        /// <summary>
        /// Writes one slot's record, keeping its lifetime token. Takes effect on the next <see cref="Flush"/>.
        /// </summary>
        /// <param name="prevObjectToWorld">The group's object-to-world matrix on the previous renderer tick.</param>
        /// <param name="objectToWorld">The group's current object-to-world matrix (rigid, scale 1).</param>
        /// <param name="brickBaseWords">Word offset of the sector's first brick inside <paramref name="page"/>.</param>
        /// <param name="page">Index of the brick pool page holding the sector's bricks.</param>
        public void Set(
            int slot,
            in Matrix4x4 prevObjectToWorld,
            in Matrix4x4 objectToWorld,
            int brickBaseWords,
            int page,
            uint hashSeed)
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
                lifetimeToken = host[slot].lifetimeToken,
                curRow0 = objectToWorld.GetRow(0),
                curRow1 = objectToWorld.GetRow(1),
                curRow2 = objectToWorld.GetRow(2)
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

        /// <summary>Next non-zero token. 0 is reserved for "slot not allocated", so the counter skips it on wrap.</summary>
        private static uint NextLifetimeToken()
        {
            s_NextLifetimeToken++;
            if (s_NextLifetimeToken == 0u)
            {
                s_NextLifetimeToken = 1u;
            }

            return s_NextLifetimeToken;
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
