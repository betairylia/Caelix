using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Caelix.Rendering
{
    /// <summary>
    /// Layout of one GPU brick record, and the helpers that fill it.
    /// </summary>
    /// <remarks>
    /// The record is what the trace reads: two info words, sixteen occupancy words and one packed
    /// pair of 16-bit block ids per two voxels. Its layout is mirrored in
    /// <c>CaelixBrickTrace.hlsl</c> and in <c>CaelixBrickPoolOps.compute</c>, so every constant here
    /// has a counterpart there. Backend-neutral on purpose: the render-data job, the brick pool and
    /// the record-moving kernels all size their buffers from these numbers.
    /// </remarks>
    public static class BrickRecordLayout
    {
        // Word 0: absolute brick index + coarse occupancy. Word 1: packed tight occupied
        // bounds (also keeps uint64 occupancy loads 8-byte aligned).
        public const int BRICK_INFO_WORDS = 2;
        public const int BRICK_OCCUPANCY_WORDS = 16;
        public const int BRICK_BLOCK_DATA_OFFSET = BRICK_INFO_WORDS + BRICK_OCCUPANCY_WORDS;
        public const int BRICK_BLOCK_DATA_WORDS = BrickKey.BlocksInBrick / 2;
        public const int BRICK_DATA_LENGTH = BRICK_BLOCK_DATA_OFFSET + BRICK_BLOCK_DATA_WORDS;

        /// <summary>
        /// Axis-aligned bounding box structure for ray tracing.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct BrickAABB
        {
            public Vector3 min;
            public Vector3 max;
        }

        public static int ToCoarseOccupancyBit(int bx, int by, int bz)
        {
            return (bx >> 2) | ((by >> 2) << 1) | ((bz >> 2) << 2);
        }

        public static int ToMicroOccupancyBit(int bx, int by, int bz)
        {
            return (bx & 3) | ((by & 3) << 2) | ((bz & 3) << 4);
        }

        public static int ToOccupancyWordOffset(int coarseBit, int microBit)
        {
            return BRICK_INFO_WORDS + coarseBit * 2 + (microBit >> 5);
        }

        /// <summary>
        /// Packs brick info word 0: bits 0..15 = group-local brick index
        /// (<see cref="Caelix.Rendering.RayQuery.RenderGroup.MaxIndexBits"/> wide at most),
        /// bits 16..23 = coarse occupancy.
        /// </summary>
        public static int PackBrickInfo(int brickIdxAbsolute, uint coarseOccupancy)
        {
            return unchecked((int)(((uint)brickIdxAbsolute & 0xFFFFu) | ((coarseOccupancy & 0xFFu) << 16)));
        }

        /// <summary>
        /// Packs the brick-local occupied block bounds (both inclusive, 0..7 per axis)
        /// into brick info word 1. Layout: [minX:0-2][minY:3-5][minZ:6-8][maxX:9-11][maxY:12-14][maxZ:15-17].
        /// </summary>
        public static int PackBrickTightBounds(int3 occupiedMin, int3 occupiedMax)
        {
            return occupiedMin.x | (occupiedMin.y << 3) | (occupiedMin.z << 6)
                 | (occupiedMax.x << 9) | (occupiedMax.y << 12) | (occupiedMax.z << 15);
        }

        /// <summary>
        /// The allocation size a buffer of <paramref name="requestedLength"/> bricks is rounded up
        /// to: the next power of two, at least 1.
        /// </summary>
        public static int GetCapacity(int requestedLength)
        {
            int result = 1;
            while (result < requestedLength)
            {
                result <<= 1;
            }

            // int result = requestedLength / 16 * 16 + 16;

            return result;
        }

        /// <summary>
        /// Writes the occupancy and payload of one brick. The caller supplies a separate scratch
        /// record and writes its two header words only if the returned coarse occupancy is nonzero.
        /// Source pointers are borrowed for this call; no storage mutation is allowed while reading.
        /// </summary>
        internal static unsafe uint GenerateBrickRecord(
            VoxelEntityData data, int3 key, Block* brick, [NoAlias] int* record,
            out int3 occupiedMin, out int3 occupiedMax)
        {
            // Face classification needs six adjacent bricks, never edges or corners. Binding them
            // once also removes coordinate conversion and pointer selection from each voxel read.
            BrickCursor cursor = default;
            data.TryBindBrick(SectorSlotId.Block, key + new int3(1, 0, 0), ref cursor, out Block* xp);
            data.TryBindBrick(SectorSlotId.Block, key + new int3(-1, 0, 0), ref cursor, out Block* xn);
            data.TryBindBrick(SectorSlotId.Block, key + new int3(0, 1, 0), ref cursor, out Block* yp);
            data.TryBindBrick(SectorSlotId.Block, key + new int3(0, -1, 0), ref cursor, out Block* yn);
            data.TryBindBrick(SectorSlotId.Block, key + new int3(0, 0, 1), ref cursor, out Block* zp);
            data.TryBindBrick(SectorSlotId.Block, key + new int3(0, 0, -1), ref cursor, out Block* zn);

            UnsafeUtility.MemClear(record + BRICK_INFO_WORDS, BRICK_OCCUPANCY_WORDS * sizeof(int));
            uint xMask = 0, yMask = 0, zMask = 0;
            for (int z = 0; z < BrickKey.BlocksPerAxis; z++)
            {
                for (int y = 0; y < BrickKey.BlocksPerAxis; y++)
                {
                    int row = BrickKey.ToBlockIdx(0, y, z);
                    // Each uint lane contains two adjacent ushort blocks. Keep them packed so
                    // each vector operation classifies all eight voxels of this row.
                    uint4 source = ReadRow(brick, row);
                    uint4 plusY = y < 7 ? ReadRow(brick, row + 8) : ReadRow(yp, row - 56);
                    uint4 minusY = y > 0 ? ReadRow(brick, row - 8) : ReadRow(yn, row + 56);
                    uint4 plusZ = z < 7 ? ReadRow(brick, row + 64) : ReadRow(zp, row - 448);
                    uint4 minusZ = z > 0 ? ReadRow(brick, row - 64) : ReadRow(zn, row + 448);
                    uint left = xn == null ? 0u : xn[row + 7].id;
                    uint right = xp == null ? 0u : xp[row].id;

                    uint4 plusX = (source >> 16) | (new uint4(source.yzw, right) << 16);
                    uint4 minusX = (source << 16) | (new uint4(left << 16, source.xyz) >> 16);
                    uint4 rowData = ClassifyRow(source, plusX, minusX, plusY, minusY, plusZ, minusZ);

                    // The GPU pair order is the reverse of little-endian source storage.
                    *(uint4*)(record + BRICK_BLOCK_DATA_OFFSET + (row >> 1)) = (rowData << 16) | (rowData >> 16);
                    uint rowMask = SpreadFourBits((uint)math.bitmask((rowData & 0xFFFFu) != 0))
                                 | (SpreadFourBits((uint)math.bitmask((rowData >> 16) != 0)) << 1);
                    xMask |= rowMask;
                    yMask |= math.select(0u, 1u << y, rowMask != 0);
                    zMask |= math.select(0u, 1u << z, rowMask != 0);

                    // One row contributes four bits to each of its two 4x4x4 micro masks.
                    int word = BRICK_INFO_WORDS + ((y >> 2) << 2) + ((z >> 2) << 3) + ((z >> 1) & 1);
                    int shift = ((y & 3) << 2) + ((z & 1) << 4);
                    record[word] |= (int)((rowMask & 15u) << shift);
                    record[word + 2] |= (int)((rowMask >> 4) << shift);
                }
            }

            uint coarseOccupancy = 0;
            for (int i = 0; i < 8; i++)
            {
                int offset = BRICK_INFO_WORDS + i * 2;
                coarseOccupancy |= math.select(0u, 1u << i, (record[offset] | record[offset + 1]) != 0);
            }

            // Axis projections preserve tight bounds without per-voxel min/max updates. Empty
            // records use the same sentinel bounds as the scalar path (and are retired by caller).
            occupiedMin = new int3(8);
            occupiedMax = new int3(-1);
            if (xMask != 0)
            {
                // Three scalar scans avoid the long vector emulation emitted for uint3 on AVX2.
                occupiedMin = new int3(math.tzcnt(xMask), math.tzcnt(yMask), math.tzcnt(zMask));
                occupiedMax = new int3(31 - math.lzcnt(xMask), 31 - math.lzcnt(yMask), 31 - math.lzcnt(zMask));
            }
            return coarseOccupancy;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe uint4 ReadRow(Block* brick, int row)
            => brick == null ? default : *(uint4*)(brick + row);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint SpreadFourBits(uint bits)
        {
            bits = (bits | (bits << 2)) & 0x33u;
            return (bits | (bits << 1)) & 0x55u;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint4 ClassifyRow(uint4 current, uint4 xp, uint4 xn,
            uint4 yp, uint4 yn, uint4 zp, uint4 zn)
        {
            // Common in sparse bricks; air with no material interfaces has no renderer data.
            if (math.all((current | xp | xn | yp | yn | zp | zn) == 0)) return 0;
#if !CAELIX_RENDER_DISABLE_TRANSPARENCY
            uint4 opaqueXp = OpaqueHighBits(xp), opaqueXn = OpaqueHighBits(xn);
            uint4 opaqueYp = OpaqueHighBits(yp), opaqueYn = OpaqueHighBits(yn);
            uint4 opaqueZp = OpaqueHighBits(zp), opaqueZn = OpaqueHighBits(zn);
            uint4 alive = ~(opaqueXp & opaqueXn & opaqueYp & opaqueYn & opaqueZp & opaqueZn) & 0x80008000u;
            if (math.all(alive == 0)) return 0;
            const uint idMask = BlockEncoding.TransparentMask | (BlockEncoding.TransparentMask << 16);
            const int faceShift = 15 - BlockEncoding.FaceShift;
            // Preserve face order +X,-X,+Y,-Y,+Z,-Z, including faces carried by air.
            uint4 transparent = (current & idMask)
                | (DifferentTransparentHighBits(current, xp, opaqueXp) >> faceShift)
                | (DifferentTransparentHighBits(current, xn, opaqueXn) >> (faceShift - 1))
                | (DifferentTransparentHighBits(current, yp, opaqueYp) >> (faceShift - 2))
                | (DifferentTransparentHighBits(current, yn, opaqueYn) >> (faceShift - 3))
                | (DifferentTransparentHighBits(current, zp, opaqueZp) >> (faceShift - 4))
                | (DifferentTransparentHighBits(current, zn, opaqueZn) >> (faceShift - 5));
            uint4 opaqueMask = ExpandHighBits(OpaqueHighBits(current));
            return ((current & opaqueMask) | (transparent & ~opaqueMask)) & ExpandHighBits(alive);
#else
            uint4 surrounded = NonzeroHighBits(xp) & NonzeroHighBits(xn)
                & NonzeroHighBits(yp) & NonzeroHighBits(yn) & NonzeroHighBits(zp) & NonzeroHighBits(zn);
            return current & ExpandHighBits(~surrounded & 0x80008000u);
#endif
        }

        // One flag in bit 15 of each ushort, carried in four uint lanes (eight voxels).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint4 OpaqueHighBits(uint4 value)
        {
            // Packed scene mode treats either of bits 14/15 as opaque; regular IDs use bit 15.
            uint4 opaque = BlockEncoding.PackedSceneColor ? value | (value << 1) : value;
            return opaque & 0x80008000u;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint4 DifferentTransparentHighBits(uint4 current, uint4 neighbor, uint4 neighborOpaque)
        {
            const uint mask = BlockEncoding.TransparentMask | (BlockEncoding.TransparentMask << 16);
            uint4 difference = (current ^ neighbor) & mask;
            // Each difference is at most 511. Adding 0x7FFF sets bit 15 iff it is nonzero,
            // and cannot carry into the next ushort. Neighbor opacity suppresses that face.
            return (difference + 0x7FFF7FFFu) & 0x80008000u & ~neighborOpaque;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint4 NonzeroHighBits(uint4 value)
            => (((value & 0x7FFF7FFFu) + 0x7FFF7FFFu) | value) & 0x80008000u;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint4 ExpandHighBits(uint4 bits) => (bits >> 15) * 0xFFFFu;

        /// <summary>
        /// The renderer's block word for one voxel: <see cref="Block.Empty"/>'s id when every face
        /// is hidden, the block id when it is opaque and visible, and the transparent id with its
        /// visible face mask otherwise. Scalar counterpart of <see cref="GenerateBrickRecord"/>,
        /// also used to check the packed implementation against the per-voxel contract.
        /// </summary>
        /// <typeparam name="TReader">
        /// How the six neighbours are read, for example a <see cref="VoxelNeighborhood"/> with
        /// entity-local positions. A generic constraint rather
        /// than an interface field, so the call stays direct under Burst.
        /// </typeparam>
        /// <param name="currentBlock">The voxel being classified.</param>
        /// <param name="blockPos">Its position, in whatever frame <paramref name="reader"/> reads.</param>
        /// <param name="reader">The neighbourhood window to read the six neighbours from.</param>
        internal static ushort GetRendererBlockData<TReader>(
            Block currentBlock, int3 blockPos, ref TReader reader)
            where TReader : struct, IBlockReader
        {
            bool alive = false;

#if !CAELIX_RENDER_DISABLE_TRANSPARENCY
            bool isOpaque = currentBlock.isOpaque;
            uint transparentId = currentBlock.transparentId;
            uint faceMask = 0;
#endif

            for (int ni = 0; ni < 6; ni++)
            {
                int3 nd = NeighborhoodSettings.Directions[ni];
                Block neighbor = reader.GetBlock(blockPos + nd);

#if !CAELIX_RENDER_DISABLE_TRANSPARENCY
                // Opaque face is always non-visible
                alive |= (!neighbor.isOpaque);

                // For transparent faces, visible only adjacent to different transparent blocks
                if ((!neighbor.isOpaque) && (!isOpaque))
                {
                    uint neighborTransparentId = neighbor.transparentId;
                    if (neighborTransparentId != transparentId)
                    {
                        alive = true;
                        faceMask |= (1u << ni);
                    }
                }

                if (alive && isOpaque) break;
#else
                alive |= (neighbor.isRendererEmpty);
                if (alive) break;
#endif
            }

            if (!alive)
            {
                return Block.Empty.id;
            }

            ushort result;
#if !CAELIX_RENDER_DISABLE_TRANSPARENCY
            if (!isOpaque)
            {
                result = (ushort)Block.MaskTransparency(faceMask, transparentId);
            }
            else
            {
                result = currentBlock.id;
            }
#else
            result = currentBlock.id;
#endif

            return result;
        }
    }
}
