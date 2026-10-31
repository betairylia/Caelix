using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Caelix.Rendering.Meshing
{
    /// <summary>
    /// Burst-compiled job for generating voxel meshes using greedy face merging.
    /// Processes a chunk of voxel data and outputs optimized mesh geometry.
    /// </summary>
    [BurstCompile]
    public unsafe struct MeshGenerationJob : IJob
    {
        // Input data

        /// <summary>
        /// Block storage of every brick the chunk covers, x-fastest, bound by the caller. A brick
        /// with no storage is <see cref="IntPtr.Zero"/> and reads as empty.
        /// </summary>
        [ReadOnly] public NativeArray<IntPtr> bricks;

        /// <summary>Bricks along one axis of the chunk: <c>chunkSize / 8</c>.</summary>
        [ReadOnly] public int bricksPerAxis;

        [ReadOnly] public int3 chunkSize;       // Size of chunk to mesh, in blocks

        /// <summary>
        /// One block at a CHUNK-LOCAL position, from the bound brick table.
        /// </summary>
        private Block GetBlock(int3 chunkLocal)
        {
            int3 brick = chunkLocal >> BrickKey.Shift;
            IntPtr ptr = bricks[brick.x + brick.y * bricksPerAxis + brick.z * bricksPerAxis * bricksPerAxis];
            if (ptr == IntPtr.Zero)
            {
                return Block.Empty;
            }

            int3 local = chunkLocal & BrickKey.Mask;
            return ((Block*)(void*)ptr)[BrickKey.ToBlockIdx(local.x, local.y, local.z)];
        }

        // Output data
        public NativeList<VoxelVertex> vertices;
        public NativeList<int> indices;

        public void Execute()
        {
            int size = math.max(math.max(chunkSize.x, chunkSize.y), chunkSize.z);

            // Allocate temporary data structures
            var quads = new NativeArray<MeshQuad>(6 * size * size * size, Allocator.Temp);
            var heads = new NativeArray<int>(6 * size * size, Allocator.Temp);
            var tails = new NativeArray<int>(6 * size * size, Allocator.Temp);
            var mergedQuads = new NativeList<MergedQuad>(size * size, Allocator.Temp);

            // Initialize heads/tails to -1 (empty)
            for (int i = 0; i < heads.Length; i++)
            {
                heads[i] = -1;
                tails[i] = -1;
            }

            // Phase 1: Emit visible faces
            EmitFaces(quads, heads, tails);

            // Phase 2: Greedy merge faces for each direction
            for (int faceDir = 0; faceDir < 6; faceDir++)
            {
                MergeFaces(faceDir, quads, heads, tails, mergedQuads);
                mergedQuads.Clear();
            }

            // Cleanup
            quads.Dispose();
            heads.Dispose();
            tails.Dispose();
            mergedQuads.Dispose();
        }

        /// <summary>
        /// Phase 1: Emit faces for all visible blocks.
        /// Checks each block's 6 neighbors and adds faces where neighbors are empty/transparent.
        /// </summary>
        private void EmitFaces(NativeArray<MeshQuad> quads, NativeArray<int> heads, NativeArray<int> tails)
        {
            int size = math.max(math.max(chunkSize.x, chunkSize.y), chunkSize.z);

            for (int z = 0; z < chunkSize.z; z++)
            {
                for (int y = 0; y < chunkSize.y; y++)
                {
                    for (int x = 0; x < chunkSize.x; x++)
                    {
                        int3 localPos = new int3(x, y, z);

                        Block block = GetBlock(localPos);
                        if (block.isEmpty) continue;

                        ushort blockID = block.id;

                        // Check all 6 faces
                        CheckAndEmitFace(0, localPos, blockID, new int3(1, 0, 0), quads, heads, tails, size);   // X+
                        CheckAndEmitFace(1, localPos, blockID, new int3(-1, 0, 0), quads, heads, tails, size);  // X-
                        CheckAndEmitFace(2, localPos, blockID, new int3(0, 1, 0), quads, heads, tails, size);   // Y+
                        CheckAndEmitFace(3, localPos, blockID, new int3(0, -1, 0), quads, heads, tails, size);  // Y-
                        CheckAndEmitFace(4, localPos, blockID, new int3(0, 0, 1), quads, heads, tails, size);   // Z+
                        CheckAndEmitFace(5, localPos, blockID, new int3(0, 0, -1), quads, heads, tails, size);  // Z-
                    }
                }
            }
        }

        /// <summary>
        /// Checks if a face should be emitted and adds it to the quad list.
        /// </summary>
        private void CheckAndEmitFace(
            int faceDir,
            int3 localPos,
            ushort blockID,
            int3 offset,
            NativeArray<MeshQuad> quads,
            NativeArray<int> heads,
            NativeArray<int> tails,
            int size)
        {
            // Check if neighbor is within chunk bounds
            int3 neighborLocal = localPos + offset;
            bool inBounds = neighborLocal.x >= 0 && neighborLocal.x < chunkSize.x &&
                           neighborLocal.y >= 0 && neighborLocal.y < chunkSize.y &&
                           neighborLocal.z >= 0 && neighborLocal.z < chunkSize.z;

            // If out of chunk bounds, always emit face (no cross-chunk culling)
            bool shouldEmit = !inBounds;

            // If in bounds, check if neighbor is empty
            if (inBounds)
            {
                Block neighbor = GetBlock(neighborLocal);
                shouldEmit = neighbor.isEmpty;
            }

            if (shouldEmit)
            {
                EmitFaceAt(faceDir, localPos, blockID, quads, heads, tails, size);
            }
        }

        /// <summary>
        /// Adds a face to the linked list for the given face direction and position.
        /// </summary>
        private void EmitFaceAt(
            int faceDir,
            int3 pos,
            ushort blockID,
            NativeArray<MeshQuad> quads,
            NativeArray<int> heads,
            NativeArray<int> tails,
            int size)
        {
            int primaryIdx = FaceLookup.PrimaryIndex[faceDir];
            int secondaryIdx = FaceLookup.SecondaryIndex[faceDir];
            int tertiaryIdx = FaceLookup.TertiaryIndex[faceDir];

            int primary = pos[primaryIdx];
            int secondary = pos[secondaryIdx];
            int tertiary = pos[tertiaryIdx];

            // Calculate indices
            int headTailIdx = faceDir * size * size + secondary * size + tertiary;
            int quadIdx = faceDir * size * size * size + primary * size * size + secondary * size + tertiary;

            // Create quad
            quads[quadIdx] = new MeshQuad(-1, blockID);

            // Link to existing chain
            if (heads[headTailIdx] == -1)
            {
                // First quad in this chain
                heads[headTailIdx] = quadIdx;
            }
            else
            {
                // Link previous tail to this quad
                var prevQuad = quads[tails[headTailIdx]];
                prevQuad.next = quadIdx;
                quads[tails[headTailIdx]] = prevQuad;
            }

            tails[headTailIdx] = quadIdx;
        }

        /// <summary>
        /// Phase 2: Greedy merge faces for a given direction.
        /// Processes linked lists of quads and merges adjacent faces with same material.
        /// </summary>
        private void MergeFaces(
            int faceDir,
            NativeArray<MeshQuad> quads,
            NativeArray<int> heads,
            NativeArray<int> tails,
            NativeList<MergedQuad> mergedQuads)
        {
            int size = math.max(math.max(chunkSize.x, chunkSize.y), chunkSize.z);
            int primaryIdx = FaceLookup.PrimaryIndex[faceDir];
            int secondaryIdx = FaceLookup.SecondaryIndex[faceDir];
            int tertiaryIdx = FaceLookup.TertiaryIndex[faceDir];

            int primarySize = chunkSize[primaryIdx];

            // Process each slice along primary axis
            for (int primary = 0; primary < primarySize; primary++)
            {
                ProcessSlice(faceDir, primary, quads, heads, tails, mergedQuads, size);
            }
        }

        /// <summary>
        /// Processes a 2D slice and merges quads greedily.
        /// </summary>
        private void ProcessSlice(
            int faceDir,
            int primary,
            NativeArray<MeshQuad> quads,
            NativeArray<int> heads,
            NativeArray<int> tails,
            NativeList<MergedQuad> mergedQuads,
            int size)
        {
            int secondaryIdx = FaceLookup.SecondaryIndex[faceDir];
            int tertiaryIdx = FaceLookup.TertiaryIndex[faceDir];

            int secondarySize = chunkSize[secondaryIdx];
            int tertiarySize = chunkSize[tertiaryIdx];

            var mergedQuadPtr = new NativeArray<int>(size * size, Allocator.Temp);
            for (int i = 0; i < mergedQuadPtr.Length; i++)
                mergedQuadPtr[i] = 0;

            mergedQuads.Clear();

            // Scan through secondary axis
            for (int secondary = 0; secondary < secondarySize; secondary++)
            {
                for (int tertiary = 0; tertiary < tertiarySize; tertiary++)
                {
                    int headTailIdx = faceDir * size * size + secondary * size + tertiary;
                    int currentQuadIdx = heads[headTailIdx];

                    if (currentQuadIdx == -1) continue;

                    // Find the quad at this primary position
                    int quadIdx = FindQuadAtPrimary(currentQuadIdx, primary, faceDir, quads, size);
                    if (quadIdx == -1) continue;

                    MeshQuad quad = quads[quadIdx];
                    ushort blockID = quad.blockID;

                    // Try to merge with left neighbor
                    int2 minPt = new int2(secondary, tertiary);
                    int2 maxPt = new int2(secondary, tertiary);

                    if (secondary > 0 && mergedQuadPtr[secondary - 1 + tertiary * size] > 0)
                    {
                        int leftIdx = mergedQuadPtr[secondary - 1 + tertiary * size] - 1;
                        MergedQuad left = mergedQuads[leftIdx];

                        if (left.blockID == blockID && left.min.y == minPt.y && left.max.y == maxPt.y)
                        {
                            // Can merge - extend left quad
                            minPt = left.min;
                            mergedQuadPtr[secondary + tertiary * size] = mergedQuadPtr[secondary - 1 + tertiary * size];
                            mergedQuads[leftIdx] = new MergedQuad(minPt, maxPt, blockID);
                            continue;
                        }
                    }

                    // Create new merged quad
                    mergedQuads.Add(new MergedQuad(minPt, maxPt, blockID));
                    mergedQuadPtr[secondary + tertiary * size] = mergedQuads.Length;
                }
            }

            // Emit all merged quads as mesh geometry
            for (int i = 0; i < mergedQuads.Length; i++)
            {
                EmitMergedQuad(faceDir, primary, mergedQuads[i]);
            }

            mergedQuadPtr.Dispose();
        }

        /// <summary>
        /// Finds the quad at a specific primary position in the linked list.
        /// </summary>
        private int FindQuadAtPrimary(int headIdx, int primary, int faceDir, NativeArray<MeshQuad> quads, int size)
        {
            int primaryIdx = FaceLookup.PrimaryIndex[faceDir];
            int secondaryIdx = FaceLookup.SecondaryIndex[faceDir];
            int tertiaryIdx = FaceLookup.TertiaryIndex[faceDir];

            int currentIdx = headIdx;

            while (currentIdx != -1)
            {
                // Extract primary coordinate from index
                int offset = currentIdx - (faceDir * size * size * size);
                int p = offset / (size * size);

                if (p == primary)
                    return currentIdx;

                currentIdx = quads[currentIdx].next;
            }

            return -1;
        }

        /// <summary>
        /// Emits a merged quad as 4 vertices and 6 indices (2 triangles).
        /// </summary>
        private void EmitMergedQuad(int faceDir, int primary, MergedQuad mq)
        {
            int primaryIdx = FaceLookup.PrimaryIndex[faceDir];
            int secondaryIdx = FaceLookup.SecondaryIndex[faceDir];
            int tertiaryIdx = FaceLookup.TertiaryIndex[faceDir];
            int direction = FaceLookup.Direction[faceDir];

            // Calculate vertex positions (no managed arrays for Burst compatibility)
            float3 v0 = new float3(0, 0, 0);
            float3 v1 = new float3(0, 0, 0);
            float3 v2 = new float3(0, 0, 0);
            float3 v3 = new float3(0, 0, 0);

            // Vertex 0
            v0[primaryIdx] = primary + (direction > 0 ? 1 : 0);
            v0[secondaryIdx] = mq.min.x;
            v0[tertiaryIdx] = mq.min.y;

            // Vertex 1
            v1[primaryIdx] = primary + (direction > 0 ? 1 : 0);
            v1[secondaryIdx] = mq.max.x + 1;
            v1[tertiaryIdx] = mq.min.y;

            // Vertex 2
            v2[primaryIdx] = primary + (direction > 0 ? 1 : 0);
            v2[secondaryIdx] = mq.max.x + 1;
            v2[tertiaryIdx] = mq.max.y + 1;

            // Vertex 3
            v3[primaryIdx] = primary + (direction > 0 ? 1 : 0);
            v3[secondaryIdx] = mq.min.x;
            v3[tertiaryIdx] = mq.max.y + 1;

            // Add vertices with proper winding order
            int baseIdx = vertices.Length;
            float3 normal = FaceLookup.Normals[faceDir];

            // Add vertices in winding order (avoid array indexing)
            int w0 = FaceLookup.GetWindingOrder(faceDir, 0);
            int w1 = FaceLookup.GetWindingOrder(faceDir, 1);
            int w2 = FaceLookup.GetWindingOrder(faceDir, 2);
            int w3 = FaceLookup.GetWindingOrder(faceDir, 3);

            vertices.Add(new VoxelVertex(w0 == 0 ? v0 : w0 == 1 ? v1 : w0 == 2 ? v2 : v3, normal, mq.blockID));
            vertices.Add(new VoxelVertex(w1 == 0 ? v0 : w1 == 1 ? v1 : w1 == 2 ? v2 : v3, normal, mq.blockID));
            vertices.Add(new VoxelVertex(w2 == 0 ? v0 : w2 == 1 ? v1 : w2 == 2 ? v2 : v3, normal, mq.blockID));
            vertices.Add(new VoxelVertex(w3 == 0 ? v0 : w3 == 1 ? v1 : w3 == 2 ? v2 : v3, normal, mq.blockID));

            // Add indices (2 triangles) - clockwise winding for Unity
            indices.Add(baseIdx + 0);
            indices.Add(baseIdx + 2);
            indices.Add(baseIdx + 1);

            indices.Add(baseIdx + 0);
            indices.Add(baseIdx + 3);
            indices.Add(baseIdx + 2);
        }
    }
}
