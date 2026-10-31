using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Unity.Mathematics;
using UnityEngine.Serialization;

namespace Caelix
{
    /// <summary>
    /// Test implementation of VoxelEntity that generates procedural voxel terrain using noise functions.
    /// Includes a corruption test mode for testing dynamic voxel updates.
    /// </summary>
    public class TestWorld : VoxelEntity
    {
        /// <summary>
        /// Burst-compiled job that fills a region with procedurally generated voxel data.
        /// Uses layered Simplex noise to create organic terrain shapes.
        /// </summary>
        [BurstCompile]
        struct FillRegionJob : IJob
        {
            /// <summary>
            /// The region to fill with generated voxel data. Positions are entity-local.
            /// </summary>
            public VoxelRegion region;

            /// <summary>
            /// Executes the job, filling the region with noise-based voxel data.
            /// </summary>
            /// <remarks>
            /// Uses two octaves of Simplex noise at different scales to create terrain.
            /// Blocks are colored based on their height (Y position) with a gradient effect.
            /// </remarks>
            public void Execute()
            {
                int3 origin = region.Origin;
                for (int x = 0; x < VoxelRegion.SizeInBlocks; x++)
                {
                    for (int y = 0; y < VoxelRegion.SizeInBlocks; y++)
                    {
                        for (int z = 0; z < VoxelRegion.SizeInBlocks; z++)
                        {
                            int3 p = origin + new int3(x, y, z);
                            int wx = p.x;
                            int wy = p.y;
                            int wz = p.z;

                            // var n = Unity.Mathematics.noise.snoise(
                            //     new float3(wx / 32.0f, wy / 32.0f, wz / 32.0f));
                            var n = Unity.Mathematics.noise.snoise(
                                new float3(wx / 72.0f, wy / 72.0f, wz / 72.0f)) +
                                    noise.snoise(
                                        new float3(wx / 24.0f, wy / 24.0f, wz / 24.0f)) * 0.5f;
                            // float n = (float)(600 + 200 * math.sin(wx / 100.0) + 100 * math.cos(wz / 150.0)) - wy;
                            if (n > 0)
                            {
                                region.SetBlock(p, new Block(n, wy / 512.0f, 0.5f, 0f));
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Number of regions to generate in each dimension. Serialized field name kept: scene data
        /// depends on it.
        /// </summary>
        public int3 numSectors;

        /// <summary>
        /// Initializes the test world by generating all regions with procedural terrain.
        /// Can be called from the context menu in the Unity Editor.
        /// </summary>
        /// <remarks>
        /// Generates regions in parallel using Unity Jobs for performance.
        /// Logs total brick count and memory usage when complete.
        /// </remarks>
        [ContextMenu("Initialize")]
        public void Initialize()
        {
            // Dispose();

            NativeList<JobHandle> fillWorldJobs = new NativeList<JobHandle>(Allocator.Temp);

            for (int i = 0; i < numSectors.x; i++)
            {
                for (int j = 0; j < numSectors.z; j++)
                {
                    for (int k = 0; k < numSectors.y; k++)
                    {
                        var secPos = new int3(i, k, j);
                        EnsureRegion(secPos);
                        if (!TryOpenRegion(secPos, out VoxelRegion region))
                        {
                            continue;
                        }

                        var job = new FillRegionJob()
                        {
                            region = region
                        };

                        fillWorldJobs.Add(job.Schedule());
                    }
                }
            }

            JobHandle.CompleteAll(fillWorldJobs);
            fillWorldJobs.Dispose();
            Debug.Log("Done!");

            int totalBricks = AllocatedBrickCount;
            Debug.Log($"Total: {totalBricks} Bricks ({totalBricks * 2 / 1024} MiB)");
        }

        /// <summary>
        /// Initializes the test world on scene start.
        /// </summary>
        void Start()
        {
            Initialize();
        }

        /// <summary>
        /// When enabled, continuously modifies voxels every frame to test dynamic updates.
        /// </summary>
        [FormerlySerializedAs("Tick")] public bool CorruptionTick = false;

        /// <summary>
        /// Burst-compiled job that toggles blocks in a region for testing dynamic voxel updates.
        /// </summary>
        [BurstCompile]
        struct TestUpdate : IJob
        {
            /// <summary>
            /// The region to modify.
            /// </summary>
            public VoxelRegion region;

            /// <summary>
            /// Frame counter used to determine which blocks to toggle.
            /// </summary>
            public int p;

            /// <summary>
            /// Toggles a horizontal slice of blocks in the region.
            /// </summary>
            public void Execute()
            {
                const int Zs = VoxelRegion.SizeInBlocks;
                int3 origin = region.Origin;
                for (int x = 0; x < VoxelRegion.SizeInBlocks; x++)
                {
                    for (int i = 0; i < Zs; i++)
                    {
                        int z = ((p / VoxelRegion.SizeInBlocks) % (VoxelRegion.SizeInBlocks / Zs)) * Zs + i;
                        
                        int Ys = 4;
                        int y_start = p % (VoxelRegion.SizeInBlocks / Ys);
                        
                        for (int y = y_start * Ys; y < (y_start + 1) * Ys; y++)
                        {
                            int3 pos = origin + new int3(x, y, z);
                            region.SetBlock(
                                pos, new Block((ushort)(region.GetBlock(pos).isEmpty ? new Block(0.5f, 1.0f, 0.8f, 0.0f).data : 0)), BrickUpdateFlags.None);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// If CorruptionTick is enabled, modifies voxels every frame to test rendering updates.
        /// </summary>
        public void Update()
        {
            if (CorruptionTick)
            {
                NativeList<JobHandle> jobs = new NativeList<JobHandle>(Allocator.Temp);
                NativeArray<int3> regionPositions = GetRegionPositions(Allocator.Temp);

                foreach (int3 regionPos in regionPositions)
                {
                    if (math.any(regionPos >= numSectors) || math.any(regionPos < 0))
                    {
                        continue;
                    }

                    if (!TryOpenRegion(regionPos, out VoxelRegion region))
                    {
                        continue;
                    }

                    int p = Time.frameCount;

                    jobs.Add(new TestUpdate()
                    {
                        p = p,
                        region = region
                    }.Schedule());
                }

                JobHandle.CompleteAll(jobs);
                jobs.Dispose();
                regionPositions.Dispose();
            }
        }
    }
}
