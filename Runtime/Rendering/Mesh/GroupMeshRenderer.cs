using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Caelix.Rendering.RayQuery;
using Caelix.Utils;

namespace Caelix.Rendering.Meshing
{
    /// <summary>
    /// Manages mesh rendering for a single <see cref="RenderGroup"/> of one entity.
    /// Subdivides the group's block box into chunks and creates a GameObject hierarchy for them.
    /// </summary>
    /// <remarks>
    /// Work arrives as a slice of the entity's per-cycle change list, keyed by brick key: every
    /// entry marks the chunk that holds its brick dirty, and each dirty chunk is re-meshed from the
    /// bricks bound for it. Nothing here knows how storage groups bricks.
    /// </remarks>
    public class GroupMeshRenderer : IDisposable
    {
        private readonly int3 groupKey;
        private readonly int chunkSize;
        private readonly Material material;

        private const DirtyFlags MeshUpdateFlags =
            DirtyFlags.BlockBrickAdded |
            DirtyFlags.BlockBrickRemoved |
            DirtyFlags.GeometryWithLocalNeighbor;

        /// <summary>
        /// Blocks along one axis of a render group (16 bricks x 8 blocks = 128). The mesh renderer
        /// keeps the default grouping; only the ray query renderer's group shape is configurable.
        /// </summary>
        private static readonly int GroupSizeInBlocks =
            RenderGroup.Default.BricksPerAxis.x * BrickKey.BlocksPerAxis;

        // Chunk management
        private readonly int3 chunksPerAxis;
        private readonly int totalChunks;
        private readonly int bricksPerChunkAxis;
        private readonly GameObject[] chunkObjects;
        private readonly MeshFilter[] meshFilters;
        private readonly MeshRenderer[] meshRenderers;
        private readonly Mesh[] meshes;

        // Job tracking
        private readonly List<ChunkMeshData> meshDataList = new List<ChunkMeshData>();
        private readonly List<NativeArray<IntPtr>> brickTables = new List<NativeArray<IntPtr>>();
        private JobHandle jobHandle;
        private readonly List<int> chunkIndices = new List<int>();
        private readonly HashSet<int> dirtyChunks = new HashSet<int>();

        /// <summary>
        /// Parent GameObject containing all chunk renderers.
        /// </summary>
        public GameObject GroupObject { get; private set; }

        /// <summary>The render group this renderer draws, in group units.</summary>
        public int3 GroupKey => groupKey;

        public GroupMeshRenderer(
            int3 groupKey,
            int chunkSize,
            Material material,
            Transform worldTransform)
        {
            this.groupKey = groupKey;
            this.chunkSize = chunkSize;
            this.material = material;

            // Calculate chunk subdivision
            chunksPerAxis = new int3(
                GroupSizeInBlocks / chunkSize,
                GroupSizeInBlocks / chunkSize,
                GroupSizeInBlocks / chunkSize
            );
            totalChunks = chunksPerAxis.x * chunksPerAxis.y * chunksPerAxis.z;
            bricksPerChunkAxis = chunkSize / BrickKey.BlocksPerAxis;

            // Create parent GameObject
            GroupObject = new GameObject($"Group_{groupKey.x}_{groupKey.y}_{groupKey.z}");
            GroupObject.transform.SetParent(worldTransform, false);
            GroupObject.transform.localPosition = RenderGroup.Default.BlockOrigin(groupKey).ToVector3Int();

            // Allocate arrays
            chunkObjects = new GameObject[totalChunks];
            meshFilters = new MeshFilter[totalChunks];
            meshRenderers = new MeshRenderer[totalChunks];
            meshes = new Mesh[totalChunks];

            // Create chunk GameObjects
            InitializeChunks();

            // Mark all chunks as dirty for initial generation
            for (int i = 0; i < totalChunks; i++)
            {
                dirtyChunks.Add(i);
            }
        }

        /// <summary>
        /// Creates GameObject hierarchy for all chunks.
        /// </summary>
        private void InitializeChunks()
        {
            for (int z = 0; z < chunksPerAxis.z; z++)
            {
                for (int y = 0; y < chunksPerAxis.y; y++)
                {
                    for (int x = 0; x < chunksPerAxis.x; x++)
                    {
                        int chunkIdx = GetChunkIndex(x, y, z);
                        int3 chunkPos = new int3(x, y, z);

                        // Create GameObject
                        GameObject chunkObj = new GameObject($"Chunk_{x}_{y}_{z}");
                        chunkObj.transform.SetParent(GroupObject.transform, false);
                        chunkObj.transform.localPosition = (chunkPos * chunkSize).ToVector3Int();

                        // Add components
                        MeshFilter filter = chunkObj.AddComponent<MeshFilter>();
                        MeshRenderer renderer = chunkObj.AddComponent<MeshRenderer>();

                        // Create mesh
                        Mesh mesh = new Mesh();
                        mesh.name = $"ChunkMesh_{x}_{y}_{z}";
                        mesh.indexFormat = IndexFormat.UInt32; // Support large meshes

                        // Assign
                        filter.mesh = mesh;
                        renderer.sharedMaterial = material;
                        renderer.shadowCastingMode = ShadowCastingMode.On;
                        renderer.receiveShadows = true;

                        // Store references
                        chunkObjects[chunkIdx] = chunkObj;
                        meshFilters[chunkIdx] = filter;
                        meshRenderers[chunkIdx] = renderer;
                        meshes[chunkIdx] = mesh;
                    }
                }
            }
        }

        /// <summary>
        /// Marks the chunks named by this group's slice of the change list dirty and schedules one
        /// mesh generation job per dirty chunk. Called during update phase 1.
        /// </summary>
        /// <remarks>
        /// Removed and Updated entries alike mark their chunk: a freed brick has to disappear from
        /// the mesh too. The require-update filter already happened while the changes were bucketed.
        /// Brick pointers are bound here, on the main thread, and every job that reads them is
        /// completed inside the same <see cref="VoxelMeshRenderer.Update"/> — the phase rule.
        /// </remarks>
        public unsafe void ScheduleJobs(
            in VoxelEntityData data, NativeArray<BrickChange> sorted, int start, int count)
        {
            for (int i = start; i < start + count; i++)
            {
                int3 chunkIdx = ChunkOf(sorted[i].Key);
                if (IsValidChunkIndex(chunkIdx))
                {
                    dirtyChunks.Add(GetChunkIndex(chunkIdx.x, chunkIdx.y, chunkIdx.z));
                }
            }

            // A local copy: VoxelEntityData is a struct whose members are not readonly, so calling
            // through the `in` parameter would make a defensive copy per call. Every copy shares the
            // same storage, so a local one reads exactly the same bricks.
            VoxelEntityData store = data;
            int bricksPerChunk = bricksPerChunkAxis * bricksPerChunkAxis * bricksPerChunkAxis;
            int3 groupFirstKey = RenderGroup.Default.FirstKey(groupKey);

            // Schedule jobs for dirty chunks
            foreach (int chunkIdx in dirtyChunks)
            {
                int3 chunkCoord = GetChunkCoord(chunkIdx);
                int3 chunkFirstKey = groupFirstKey + chunkCoord * bricksPerChunkAxis;
                int3 chunkSizeVec = new int3(chunkSize, chunkSize, chunkSize);

                // Bind the chunk's bricks on the main thread; an unallocated brick binds as zero.
                var bricks = new NativeArray<IntPtr>(bricksPerChunk, Allocator.TempJob);
                var cursor = new BrickCursor();
                for (int bz = 0; bz < bricksPerChunkAxis; bz++)
                for (int by = 0; by < bricksPerChunkAxis; by++)
                for (int bx = 0; bx < bricksPerChunkAxis; bx++)
                {
                    int3 key = chunkFirstKey + new int3(bx, by, bz);
                    int flat = bx + by * bricksPerChunkAxis + bz * bricksPerChunkAxis * bricksPerChunkAxis;
                    bricks[flat] = store.TryBindBrick(SectorSlotId.Block, key, ref cursor, out Block* blocks)
                        ? (IntPtr)blocks
                        : IntPtr.Zero;
                }

                // Create mesh data
                var meshData = new ChunkMeshData(
                    vertexCapacity: 16384,
                    indexCapacity: 32768,
                    Allocator.TempJob
                );

                // Create job
                var job = new MeshGenerationJob
                {
                    bricks = bricks,
                    bricksPerAxis = bricksPerChunkAxis,
                    chunkSize = chunkSizeVec,
                    vertices = meshData.vertices,
                    indices = meshData.indices
                };

                // Schedule
                JobHandle handle = job.Schedule();

                // Track
                meshDataList.Add(meshData);
                brickTables.Add(bricks);
                jobHandle = JobHandle.CombineDependencies(jobHandle, handle);
                chunkIndices.Add(chunkIdx);
            }

            dirtyChunks.Clear();
        }

        internal bool TryGetScheduledJobHandle(out JobHandle handle)
        {
            handle = jobHandle;
            return meshDataList.Count > 0;
        }

        /// <summary>
        /// Applies mesh data after the renderer coordinator has completed the combined job handle.
        /// </summary>
        internal void ApplyCompletedJobs()
        {
            if (meshDataList.Count == 0)
                return;

            // Apply meshes
            for (int i = 0; i < meshDataList.Count; i++)
            {
                int chunkIdx = chunkIndices[i];
                ChunkMeshData meshData = meshDataList[i];

                ApplyMeshData(chunkIdx, meshData);

                // Dispose mesh data
                meshData.Dispose();
                brickTables[i].Dispose();
            }

            // Clear tracking
            jobHandle = default;
            meshDataList.Clear();
            brickTables.Clear();
            chunkIndices.Clear();
        }

        /// <summary>
        /// Applies mesh data to a chunk's MeshFilter.
        /// </summary>
        private void ApplyMeshData(int chunkIdx, ChunkMeshData meshData)
        {
            Mesh mesh = meshes[chunkIdx];
            mesh.Clear();

            if (meshData.IsEmpty)
            {
                // Hide empty chunks
                chunkObjects[chunkIdx].SetActive(false);
                return;
            }

            // Show chunk
            chunkObjects[chunkIdx].SetActive(true);

            // Convert NativeList to arrays
            var vertices = new Vector3[meshData.vertices.Length];
            var normals = new Vector3[meshData.vertices.Length];
            var blockIds = new Vector2[meshData.vertices.Length];

            for (int i = 0; i < meshData.vertices.Length; i++)
            {
                VoxelVertex v = meshData.vertices[i];
                vertices[i] = v.position;
                normals[i] = v.normal;
                blockIds[i] = new Vector2(v.blockID, 0f);
            }

            var indices = meshData.indices.AsArray().ToArray();

            // Assign to mesh
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = blockIds;
            mesh.triangles = indices;

            // Calculate bounds
            mesh.RecalculateBounds();

            // Optimize
            mesh.Optimize();
        }

        /// <summary>
        /// Forces regeneration of all chunks.
        /// </summary>
        public void MarkAllDirty()
        {
            for (int i = 0; i < totalChunks; i++)
            {
                dirtyChunks.Add(i);
            }
        }

        /// <summary>True when the entity holds no allocated brick inside this group any more.</summary>
        public bool IsEmpty(in VoxelEntityData data)
        {
            VoxelEntityData store = data;
            foreach (int3 unused in store.EnumerateBricks(
                         RenderGroup.Default.FirstKey(groupKey), RenderGroup.Default.LastKey(groupKey)))
            {
                return false;
            }

            return true;
        }

        internal static bool RequiresRemesh(ushort requireUpdateFlags)
        {
            return (requireUpdateFlags & (ushort)MeshUpdateFlags) != 0;
        }

        /// <summary>Chunk coordinate, inside this group, of the chunk that holds a brick key.</summary>
        private int3 ChunkOf(int3 key)
        {
            return (BrickKey.ToBlockOrigin(RenderGroup.Default.LocalBrick(key))) / chunkSize;
        }

        /// <summary>
        /// Converts chunk coordinates to flat index.
        /// </summary>
        private int GetChunkIndex(int x, int y, int z)
        {
            return x + y * chunksPerAxis.x + z * chunksPerAxis.x * chunksPerAxis.y;
        }

        /// <summary>
        /// Converts flat index to chunk coordinates.
        /// </summary>
        private int3 GetChunkCoord(int index)
        {
            int z = index / (chunksPerAxis.x * chunksPerAxis.y);
            int rem = index % (chunksPerAxis.x * chunksPerAxis.y);
            int y = rem / chunksPerAxis.x;
            int x = rem % chunksPerAxis.x;
            return new int3(x, y, z);
        }

        /// <summary>
        /// Checks if chunk index is valid.
        /// </summary>
        private bool IsValidChunkIndex(int3 chunkIdx)
        {
            return chunkIdx.x >= 0 && chunkIdx.x < chunksPerAxis.x &&
                   chunkIdx.y >= 0 && chunkIdx.y < chunksPerAxis.y &&
                   chunkIdx.z >= 0 && chunkIdx.z < chunksPerAxis.z;
        }

        /// <summary>
        /// Cleanup resources.
        /// </summary>
        public void Dispose()
        {
            // Complete any pending jobs
            if (meshDataList.Count > 0)
            {
                jobHandle.Complete();
                foreach (var meshData in meshDataList)
                {
                    meshData.Dispose();
                }

                for (int i = 0; i < brickTables.Count; i++)
                {
                    brickTables[i].Dispose();
                }

                jobHandle = default;
                meshDataList.Clear();
                brickTables.Clear();
                chunkIndices.Clear();
            }

            // Destroy meshes
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] != null)
                {
                    DestroyObject(meshes[i]);
                }
            }

            // Destroy GameObjects
            if (GroupObject != null)
            {
                DestroyObject(GroupObject);
                GroupObject = null;
            }
        }

        private static void DestroyObject(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
