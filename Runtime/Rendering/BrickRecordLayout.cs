using System.Runtime.InteropServices;
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
        /// The renderer's block word for one voxel: <see cref="Block.Empty"/>'s id when every face
        /// is hidden, the block id when it is opaque and visible, and the transparent id with its
        /// visible face mask otherwise.
        /// </summary>
        /// <typeparam name="TReader">
        /// How the six neighbours are read. The ray query group job passes a
        /// <see cref="VoxelNeighborhood"/> and entity-local positions. A generic constraint rather
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
