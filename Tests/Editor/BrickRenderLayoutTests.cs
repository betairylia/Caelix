using NUnit.Framework;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Caelix.Rendering;
using Caelix.Tests.TestSupport;

namespace Caelix.Tests
{
    [Category("RenderData")]
    public unsafe class BrickRenderLayoutTests
    {
        // Scalar reference retains the original neighborhood lookup, face classification,
        // pair packing and per-voxel occupancy/bounds accumulation.
        internal static uint GenerateScalarRecord(VoxelEntityData data, int3 key, int* record,
            out int3 min, out int3 max)
        {
            UnsafeUtility.MemClear(record, BrickRecordLayout.BRICK_DATA_LENGTH * sizeof(int));
            data.TryBindBrick(SectorSlotId.Block, key, out Block* brick);
            var neighborhood = data.OpenNeighborhood(key);
            int3 origin = BrickKey.ToBlockOrigin(key);
            uint coarse = 0;
            min = new int3(8);
            max = new int3(-1);
            for (int z = 0; z < 8; z++)
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x += 2)
            {
                int index = BrickKey.ToBlockIdx(x, y, z);
                uint a = BrickRecordLayout.GetRendererBlockData(brick[index], origin + new int3(x, y, z), ref neighborhood);
                uint b = BrickRecordLayout.GetRendererBlockData(brick[index + 1], origin + new int3(x + 1, y, z), ref neighborhood);
                record[BrickRecordLayout.BRICK_BLOCK_DATA_OFFSET + index / 2] = unchecked((int)((a << 16) | b));
                for (int pair = 0; pair < 2; pair++)
                {
                    if ((pair == 0 ? a : b) == 0) continue;
                    int3 p = new int3(x + pair, y, z);
                    int c = BrickRecordLayout.ToCoarseOccupancyBit(p.x, p.y, p.z);
                    int m = BrickRecordLayout.ToMicroOccupancyBit(p.x, p.y, p.z);
                    record[BrickRecordLayout.ToOccupancyWordOffset(c, m)] |= unchecked((int)(1u << (m & 31)));
                    coarse |= 1u << c;
                    min = math.min(min, p);
                    max = math.max(max, p);
                }
            }
            return coarse;
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct RecordJob : IJob
        {
            [ReadOnly] public VoxelEntityData Data;
            [ReadOnly] public NativeArray<int3> Keys;
            public NativeArray<int> Records;
            public bool Scalar;
            public int Repetitions;

            public void Execute()
            {
                for (int repeat = 0; repeat < Repetitions; repeat++)
                for (int i = 0; i < Keys.Length; i++)
                {
                    int* record = (int*)Records.GetUnsafePtr() + i * BrickRecordLayout.BRICK_DATA_LENGTH;
                    uint coarse;
                    int3 min, max;
                    if (Scalar)
                        coarse = GenerateScalarRecord(Data, Keys[i], record, out min, out max);
                    else
                    {
                        Data.TryBindBrick(SectorSlotId.Block, Keys[i], out Block* brick);
                        coarse = BrickRecordLayout.GenerateBrickRecord(Data, Keys[i], brick, record, out min, out max);
                    }
                    record[0] = BrickRecordLayout.PackBrickInfo(i, coarse);
                    record[1] = coarse == 0 ? 0 : BrickRecordLayout.PackBrickTightBounds(min, max);
                }
            }
        }

        private static Block* AllocateBrick(ref VoxelEntityData data, int3 key)
        {
            data.EnsureRegion(VoxelRegion.OfKey(key));
            data.SetBlock(BrickKey.ToBlockOrigin(key), new Block(0x8001));
            data.TryBindBrick(SectorSlotId.Block, key, out Block* brick);
            return brick;
        }

        private static void Fill(Block* brick, int pattern, ref Random random)
        {
            for (int i = 0; i < BrickKey.BlocksInBrick; i++)
            {
                uint value = random.NextUInt();
                brick[i] = new Block(pattern switch
                {
                    0 => (ushort)0,
                    1 => (ushort)0x8001,
                    2 => (ushort)5,
                    3 => (ushort)(value % 32 == 0 ? 0x8001 : 0),
                    4 => (ushort)(value % 4 == 0 ? value & BlockEncoding.TransparentMask : value | 0x8000),
                    5 => (ushort)value, // Includes pre-existing face bits, emissive IDs and all material bits.
                    _ => (ushort)(value % 3 == 0 ? 0 : 5)
                });
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        public void VectorRecordsMatchScalarForEveryFaceNeighborCombination(int pattern)
        {
            using var scope = new EntityDataTestScope();
            using var keyStorage = new NativeArray<int3>(64, Allocator.TempJob);
            var keys = keyStorage;
            using var expected = new NativeArray<int>(64 * BrickRecordLayout.BRICK_DATA_LENGTH, Allocator.TempJob);
            using var actual = new NativeArray<int>(expected.Length, Allocator.TempJob);
            var random = new Random(0x1F234567u + (uint)pattern);
            for (int mask = 0; mask < 64; mask++)
            {
                // Space windows apart and cross positive/negative region boundaries on all axes.
                int3 key = new int3((mask % 4) * 16 - 32, ((mask / 4) % 4) * 16 - 16, (mask / 16) * 16 - 32);
                keys[mask] = key;
                Fill(AllocateBrick(ref scope.Data, key), pattern, ref random);
                for (int face = 0; face < 6; face++)
                    if ((mask & (1 << face)) != 0)
                        Fill(AllocateBrick(ref scope.Data, key + NeighborhoodSettings.Directions[face]), pattern, ref random);
            }

            new RecordJob { Data = scope.Data, Keys = keys, Records = expected, Scalar = true, Repetitions = 1 }.Schedule().Complete();
            new RecordJob { Data = scope.Data, Keys = keys, Records = actual, Repetitions = 1 }.Schedule().Complete();
            for (int i = 0; i < actual.Length; i++)
                Assert.That(actual[i], Is.EqualTo(expected[i]), $"pattern {pattern}, neighbor mask {i / 274}, word {i % 274}");
        }

        [Test]
        public void VectorOccupancyAndBoundsMatchEverySingleVoxelPosition()
        {
            using var scope = new EntityDataTestScope();
            Block* brick = AllocateBrick(ref scope.Data, new int3(-1));
            UnsafeUtility.MemClear(brick, BrickKey.BlocksInBrick * sizeof(Block));
            int* expected = stackalloc int[BrickRecordLayout.BRICK_DATA_LENGTH];
            int* actual = stackalloc int[BrickRecordLayout.BRICK_DATA_LENGTH];
            for (int i = 0; i < BrickKey.BlocksInBrick; i++)
            {
                brick[i] = new Block(0x8001);
                uint reference = GenerateScalarRecord(scope.Data, new int3(-1), expected, out int3 min, out int3 max);
                uint coarse = BrickRecordLayout.GenerateBrickRecord(scope.Data, new int3(-1), brick, actual, out int3 actualMin, out int3 actualMax);
                Assert.That(coarse, Is.EqualTo(reference), $"voxel {i}");
                Assert.That(actualMin, Is.EqualTo(min), $"min at voxel {i}");
                Assert.That(actualMax, Is.EqualTo(max), $"max at voxel {i}");
                for (int word = 2; word < BrickRecordLayout.BRICK_DATA_LENGTH; word++)
                    Assert.That(actual[word], Is.EqualTo(expected[word]), $"voxel {i}, word {word}");
                brick[i] = default;
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [Explicit("CPU record microbenchmark; excludes renderer allocation, staging, GPU work and frame timing.")]
        public void BenchmarkScalarAndVectorRecords(int pattern)
        {
            Assert.That(BurstCompiler.IsEnabled, Is.True, "Enable Burst before measuring.");
            using var scope = new EntityDataTestScope();
            using var keyStorage = new NativeArray<int3>(512, Allocator.TempJob);
            var keys = keyStorage;
            using var records = new NativeArray<int>(keys.Length * BrickRecordLayout.BRICK_DATA_LENGTH, Allocator.TempJob);
            var random = new Random(0x728AF391);
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = new int3(i & 7, (i >> 3) & 7, i >> 6);
                Fill(AllocateBrick(ref scope.Data, keys[i]), pattern, ref random);
            }
            var scalar = new RecordJob { Data = scope.Data, Keys = keys, Records = records, Scalar = true, Repetitions = 16 };
            var vector = scalar;
            vector.Scalar = false;
            scalar.Run();
            vector.Run();
            var oldTimes = new double[9];
            var newTimes = new double[9];
            var timer = new Stopwatch();
            for (int sample = 0; sample < oldTimes.Length; sample++)
            {
                // Alternate ordering to reduce bias from CPU clock and temperature changes.
                for (int pass = 0; pass < 2; pass++)
                {
                    bool old = ((sample + pass) & 1) == 0;
                    timer.Restart();
                    if (old) scalar.Run(); else vector.Run();
                    timer.Stop();
                    (old ? oldTimes : newTimes)[sample] = timer.Elapsed.TotalMilliseconds / scalar.Repetitions;
                }
            }
            System.Array.Sort(oldTimes);
            System.Array.Sort(newTimes);
            UnityEngine.Debug.Log($"Record benchmark pattern={pattern}, bricks={keys.Length}, Burst={BurstCompiler.IsEnabled}, " +
                $"scalar median ms={oldTimes[4]:F4}, vector median ms={newTimes[4]:F4}, speedup={oldTimes[4] / newTimes[4]:F2}x");
        }

        [Test]
        public void BrickRenderRecordUsesHeaderOccupancyAndBlockPayload()
        {
            Assert.That(BrickRecordLayout.BRICK_INFO_WORDS, Is.EqualTo(2));
            Assert.That(BrickRecordLayout.BRICK_OCCUPANCY_WORDS, Is.EqualTo(16));
            Assert.That(BrickRecordLayout.BRICK_BLOCK_DATA_OFFSET, Is.EqualTo(18));
            Assert.That(BrickRecordLayout.BRICK_DATA_LENGTH, Is.EqualTo(274));
        }

        [TestCase(0, 0, 0, 0, 0, 2)]
        [TestCase(3, 3, 3, 0, 63, 3)]
        [TestCase(4, 0, 0, 1, 0, 4)]
        [TestCase(7, 7, 7, 7, 63, 17)]
        public void OccupancyIndicesMatchBrickQuadrants(int x, int y, int z, int coarseBit, int microBit, int wordOffset)
        {
            Assert.That(BrickRecordLayout.ToCoarseOccupancyBit(x, y, z), Is.EqualTo(coarseBit));
            Assert.That(BrickRecordLayout.ToMicroOccupancyBit(x, y, z), Is.EqualTo(microBit));
            Assert.That(BrickRecordLayout.ToOccupancyWordOffset(coarseBit, microBit), Is.EqualTo(wordOffset));
        }

        [Test]
        public void PackBrickInfoStoresAbsoluteIndexAndCoarseOccupancy()
        {
            int packed = BrickRecordLayout.PackBrickInfo(0xABC, 0b1010_0101u);

            Assert.That(packed & 0xFFF, Is.EqualTo(0xABC));
            Assert.That((packed >> 12) & 0xF, Is.EqualTo(0));
            Assert.That((packed >> 16) & 0xFF, Is.EqualTo(0b1010_0101));
            Assert.That((packed >> 24) & 0xFF, Is.EqualTo(0));
        }

        // Mirrors the unpack in CaelixBrickTrace.hlsl (CaelixTraceBrickPrimitive):
        // [minX:0-2][minY:3-5][minZ:6-8][maxX:9-11][maxY:12-14][maxZ:15-17], bounds inclusive.
        [TestCase(0, 0, 0, 7, 7, 7)]
        [TestCase(0, 0, 0, 0, 0, 0)]
        [TestCase(1, 2, 3, 4, 5, 6)]
        [TestCase(7, 7, 7, 7, 7, 7)]
        public void PackBrickTightBoundsRoundTripsPerAxis(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            int packed = BrickRecordLayout.PackBrickTightBounds(new int3(minX, minY, minZ), new int3(maxX, maxY, maxZ));

            Assert.That(packed & 7, Is.EqualTo(minX));
            Assert.That((packed >> 3) & 7, Is.EqualTo(minY));
            Assert.That((packed >> 6) & 7, Is.EqualTo(minZ));
            Assert.That((packed >> 9) & 7, Is.EqualTo(maxX));
            Assert.That((packed >> 12) & 7, Is.EqualTo(maxY));
            Assert.That((packed >> 15) & 7, Is.EqualTo(maxZ));
            Assert.That(packed >> 18, Is.EqualTo(0));
        }
    }
}
