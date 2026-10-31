using System;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Caelix.Rendering;
using Caelix.Rendering.RayQuery;

namespace Caelix.Tests
{
    /// <summary>
    /// Round-trip tests for the GPU-only brick storage: the batched scatter that replaces a host
    /// upload, and the two record copies that keep a range's contents alive when it is resized or
    /// when its page grows.
    /// </summary>
    /// <remarks>
    /// Every pool is built with a small page limit (8192 bricks) so that a handful of allocations
    /// is enough to fill a page and force both a growth and a second page.
    /// </remarks>
    public class BrickPoolGpuTests
    {
        private const int Words = BrickRecordLayout.BRICK_DATA_LENGTH;
        private const int PageLimitBricks = 8192;

        [SetUp]
        public void RequireComputeShaders()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Compute shaders unavailable.");
            }
        }

        /// <summary>The value word <paramref name="word"/> of brick <paramref name="slot"/> carries.</summary>
        /// <param name="tag">Distinguishes the ranges of a test that fills more than one.</param>
        private static int Pattern(int tag, int slot, int word) => tag * 1000000 + slot * 1000 + word;

        /// <summary>Builds the staging words and slots for one range's records.</summary>
        private static void BuildRecords(int tag, int[] slots, out NativeArray<int> words, out NativeArray<int> slotIds)
        {
            words = new NativeArray<int>(slots.Length * Words, Allocator.Temp);
            slotIds = new NativeArray<int>(slots.Length, Allocator.Temp);

            for (int k = 0; k < slots.Length; k++)
            {
                slotIds[k] = slots[k];
                for (int w = 0; w < Words; w++)
                {
                    words[k * Words + w] = Pattern(tag, slots[k], w);
                }
            }
        }

        /// <summary>Stages one record per entry of <paramref name="slots"/>, then flushes the batch.</summary>
        private static void ScatterPattern(CaelixBrickPool pool, CaelixBrickPool.Handle handle, int tag, params int[] slots)
        {
            BuildRecords(tag, slots, out NativeArray<int> words, out NativeArray<int> slotIds);

            pool.Ops.StageScatter(pool.GetPageBuffer(handle.Page), handle.OffsetBricks, words, slotIds, slots.Length);
            pool.Ops.FlushScatter();

            words.Dispose();
            slotIds.Dispose();
        }

        /// <summary>Reads a range back and asserts that every named slot still holds its pattern.</summary>
        private static void AssertPattern(CaelixBrickPool pool, CaelixBrickPool.Handle handle, int tag, params int[] slots)
        {
            AssertPattern(pool.GetPageBuffer(handle.Page), handle.OffsetBricks, tag, slots);
        }

        /// <summary>Reads a range of any buffer back and asserts every named slot's pattern.</summary>
        private static void AssertPattern(GraphicsBuffer buffer, int offsetBricks, int tag, int[] slots)
        {
            int highest = 0;
            for (int k = 0; k < slots.Length; k++)
            {
                highest = Math.Max(highest, slots[k]);
            }

            int[] readback = new int[(highest + 1) * Words];
            buffer.GetData(readback, 0, offsetBricks * Words, readback.Length);

            for (int k = 0; k < slots.Length; k++)
            {
                int slot = slots[k];
                for (int w = 0; w < Words; w++)
                {
                    Assert.That(
                        readback[slot * Words + w], Is.EqualTo(Pattern(tag, slot, w)),
                        $"tag {tag}, slot {slot}, word {w}");
                }
            }
        }

        [Test]
        public void ScatterWritesEveryStagedRecordToItsOwnSlot()
        {
            using CaelixBrickPool pool = new CaelixBrickPool(4096, PageLimitBricks);

            CaelixBrickPool.Handle handle = pool.Allocate(16);
            Assert.That(handle.IsValid, Is.True);

            // Deliberately sparse and out of order: the kernel addresses each record by its slot,
            // not by its position in the staging list.
            ScatterPattern(pool, handle, 1, 0, 5, 15);

            // A fresh page buffer is undefined, so only the slots that were written are asserted.
            AssertPattern(pool, handle, 1, 0, 5, 15);
        }

        [Test]
        public void ReallocateOnTheSamePageKeepsTheRecords()
        {
            using CaelixBrickPool pool = new CaelixBrickPool(4096, PageLimitBricks);

            CaelixBrickPool.Handle handle = pool.Allocate(4);
            ScatterPattern(pool, handle, 2, 0, 1, 2, 3);

            int oldOffset = handle.OffsetBricks;
            CaelixBrickPool.Handle grown = pool.Reallocate(handle, 8);

            Assert.That(grown.IsValid, Is.True);
            Assert.That(grown.CapacityBricks, Is.EqualTo(8));
            Assert.That(grown.Page, Is.EqualTo(0), "the page has room, so the new range stays on it");
            Assert.That(grown.OffsetBricks, Is.Not.EqualTo(oldOffset), "a resize hands out a different range");

            AssertPattern(pool, grown, 2, 0, 1, 2, 3);
        }

        [Test]
        public void GrowingAPageCarriesEveryLiveRangeOver()
        {
            using CaelixBrickPool pool = new CaelixBrickPool(4096, PageLimitBricks);

            // Mixed sizes, so compaction has to reorder them (largest first) and every range really
            // does land at a new offset.
            int[] capacities = { 512, 1024, 512, 1024, 512 };
            CaelixBrickPool.Handle[] handles = new CaelixBrickPool.Handle[capacities.Length];
            for (int i = 0; i < capacities.Length; i++)
            {
                handles[i] = pool.Allocate(capacities[i]);
                Assert.That(handles[i].IsValid, Is.True);
                ScatterPattern(pool, handles[i], i, 0, 1, 2);
            }

            Assert.That(pool.TotalCapacityBricks, Is.EqualTo(4096));

            // 3584 bricks are live and the page holds 4096, so this one no longer fits and grows it.
            CaelixBrickPool.Handle overflow = pool.Allocate(1024);
            Assert.That(overflow.IsValid, Is.True);
            Assert.That(pool.TotalCapacityBricks, Is.EqualTo(8192), "the page should have grown");

            for (int i = 0; i < handles.Length; i++)
            {
                AssertPattern(pool, handles[i], i, 0, 1, 2);
            }
        }

        [Test]
        public void ReallocateOntoAnotherPageKeepsTheRecords()
        {
            using CaelixBrickPool pool = new CaelixBrickPool(4096, PageLimitBricks);

            // Eight 1024-brick ranges fill page 0 to its 8192-brick limit (growing it once on the
            // way), so the ninth allocation has to open page 1.
            CaelixBrickPool.Handle first = pool.Allocate(1024);
            ScatterPattern(pool, first, 7, 0, 1, 2);

            for (int i = 1; i < 8; i++)
            {
                Assert.That(pool.Allocate(1024).Page, Is.EqualTo(0));
            }

            Assert.That(pool.Allocate(1024).Page, Is.EqualTo(1), "page 0 is at its limit");
            Assert.That(pool.PageCount, Is.EqualTo(2));

            CaelixBrickPool.Handle moved = pool.Reallocate(first, 2048);

            Assert.That(moved.IsValid, Is.True);
            Assert.That(moved.Page, Is.EqualTo(1), "page 0 has no room for the larger range");

            AssertPattern(pool, moved, 7, 0, 1, 2);
        }

        /// <summary>
        /// Stages far more than one batch may hold, across several destinations, and asserts that
        /// every record survives — including the ones an intermediate flush already dispatched.
        /// </summary>
        /// <remarks>
        /// The batch cap is lowered through the constructor rather than exercised at its default of
        /// 2^18 bricks, which would mean staging about 287 MB of records to prove the same thing.
        /// Destinations are plain buffers: this is a test of the batch, not of the pool.
        /// </remarks>
        [Test]
        public void StagingPastTheBatchCapFlushesAndKeepsEveryRecord()
        {
            const int capBricks = 512;
            const int destinations = 3;
            const int stagesPerDestination = 5;
            const int recordsPerStage = 64;
            const int bricksPerDestination = stagesPerDestination * recordsPerStage;

            // 3 x 5 x 64 = 960 records against a 512-brick cap, so at least one intermediate flush
            // happens before the final one.
            using CaelixBrickGpuOps ops = new CaelixBrickGpuOps(capBricks);

            GraphicsBuffer[] buffers = new GraphicsBuffer[destinations];
            for (int d = 0; d < destinations; d++)
            {
                buffers[d] = new GraphicsBuffer(GraphicsBuffer.Target.Raw, bricksPerDestination * Words, 4);
            }

            try
            {
                // Interleaved, so one destination's records land in several batches.
                for (int stage = 0; stage < stagesPerDestination; stage++)
                {
                    for (int d = 0; d < destinations; d++)
                    {
                        int[] slots = new int[recordsPerStage];
                        for (int k = 0; k < recordsPerStage; k++)
                        {
                            slots[k] = stage * recordsPerStage + k;
                        }

                        BuildRecords(d, slots, out NativeArray<int> words, out NativeArray<int> slotIds);
                        ops.StageScatter(buffers[d], 0, words, slotIds, recordsPerStage);
                        words.Dispose();
                        slotIds.Dispose();
                    }
                }

                ops.FlushScatter();

                int[] allSlots = new int[bricksPerDestination];
                for (int k = 0; k < bricksPerDestination; k++)
                {
                    allSlots[k] = k;
                }

                for (int d = 0; d < destinations; d++)
                {
                    AssertPattern(buffers[d], 0, d, allSlots);
                }
            }
            finally
            {
                for (int d = 0; d < destinations; d++)
                {
                    buffers[d].Dispose();
                }
            }
        }
    }
}
