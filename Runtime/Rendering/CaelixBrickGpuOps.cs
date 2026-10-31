using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Caelix.Rendering
{
    /// <summary>
    /// The three GPU-side movements of brick records: copying ranges between two buffers, moving
    /// ranges inside one buffer, and scattering staged records into the bricks they belong in.
    /// </summary>
    /// <remarks>
    /// Sector renderers keep no host copy of their brick records. A record therefore only ever
    /// exists on the GPU once the render-data job has staged it, and every event that used to be
    /// answered with a full re-upload — a pool page that grew, a sector range that moved, a
    /// per-sector buffer that was resized — is answered by one of these kernels instead.
    /// <para>
    /// <b>Scatters are batched, and that is load-bearing.</b> A frame's sectors call
    /// <see cref="StageScatter"/>, which writes each sector's records into its own region of one
    /// frame staging buffer; nothing is dispatched until <see cref="FlushScatter"/>. Writing a
    /// buffer the GPU is still reading makes the D3D12 backend rename or stage the WHOLE buffer per
    /// call, so a per-sector "write then dispatch" asked for one staging copy per sector: on a world
    /// load, about 6800 sectors x 4.5 MB in a single frame, which exhausted the graphics ring buffer,
    /// reserved 25 GB of system memory in the driver and hung the device. Batched, one frame costs
    /// one buffer's worth of upload memory no matter how many sectors moved.
    /// </para>
    /// <para>
    /// Two frame staging buffers alternate, so a flush never writes the buffer the previous flush's
    /// dispatches are still reading. <see cref="CopyRanges"/> and <see cref="MoveRanges"/> stay
    /// immediate: there are a handful per frame, and their range table is a few bytes.
    /// </para>
    /// <para>
    /// One instance is shared: the brick pool owns it (<see cref="RayQuery.CaelixBrickPool.Ops"/>).
    /// Its owner must call <see cref="FlushScatter"/> once per frame, after every group has staged,
    /// and once more before disposing it.
    /// </para>
    /// </remarks>
    public sealed class CaelixBrickGpuOps : IDisposable
    {
        /// <summary>Name of the compute shader, loaded from any <c>Resources</c> folder.</summary>
        public const string ShaderResourceName = "CaelixBrickPoolOps";

        /// <summary>
        /// Default cap on one batch, in bricks: 2^18 records is about 287 MB of staging buffer.
        /// Staging past it forces an intermediate flush rather than a larger upload.
        /// </summary>
        public const int MaxBatchBricks = 1 << 18;

        /// <summary>
        /// Flushes that found nothing staged before the staging buffers are released. Long enough
        /// that a paused world does not thrash them, short enough that a renderer left idle gives
        /// the memory back.
        /// </summary>
        private const int IdleFlushesBeforeTrim = 300;

        /// <summary>
        /// Smallest frame staging buffer, in bricks: 2^14 records is about 18 MB.
        /// </summary>
        /// <remarks>
        /// Growing a staging buffer has to carry the batch's records into the larger one, which
        /// leaves a dispatch reading the buffer the next <see cref="StageScatter"/> writes — the very
        /// pattern the batch exists to avoid. It is bounded rather than eliminated: starting well
        /// above one sector's worth keeps a cold frame to a handful of growths, and the buffer never
        /// shrinks, so a settled renderer grows none at all.
        /// </remarks>
        private const int MinFrameStagingBricks = 1 << 14;

        // Must match the numthreads of every kernel in the shader.
        private const int ThreadsPerGroup = 64;

        // D3D11/D3D12 cap one dispatch at 65535 groups per axis. A whole-page copy on a large world
        // is well past that, so a dispatch is split and _CaelixThreadBase carries the offset.
        private const int MaxGroupsPerDispatch = 65535;

        private static readonly int s_SrcId = Shader.PropertyToID("_CaelixSrc");
        private static readonly int s_DstId = Shader.PropertyToID("_CaelixDst");
        private static readonly int s_RangesId = Shader.PropertyToID("_CaelixRanges");
        private static readonly int s_RangeCountId = Shader.PropertyToID("_CaelixRangeCount");
        private static readonly int s_BrickCountId = Shader.PropertyToID("_CaelixBrickCount");
        private static readonly int s_ThreadBaseId = Shader.PropertyToID("_CaelixThreadBase");
        private static readonly int s_StagingId = Shader.PropertyToID("_CaelixStaging");
        private static readonly int s_PairsId = Shader.PropertyToID("_CaelixPairs");
        private static readonly int s_PairBaseId = Shader.PropertyToID("_CaelixPairBase");

        /// <summary>One destination's slice of the pair buffer, resolved at flush time.</summary>
        private struct ScatterGroup
        {
            public GraphicsBuffer Destination;

            /// <summary>Index into <see cref="pairLists"/>.</summary>
            public int List;

            /// <summary>First entry of this group inside the pair buffer.</summary>
            public int Start;

            public int Count;
        }

        private readonly ComputeShader shader;
        private readonly int copyKernel;
        private readonly int moveKernel;
        private readonly int scatterKernel;

        /// <summary>Bricks one batch may stage before <see cref="StageScatter"/> forces a flush.</summary>
        private readonly int batchLimitBricks;

        private GraphicsBuffer rangeBuffer;

        // Two, alternating: a flush leaves its dispatches reading the buffer they were staged in, so
        // the next batch of the same frame has to write the other one.
        private readonly GraphicsBuffer[] frameStaging = new GraphicsBuffer[2];
        private int frameStagingIndex;

        /// <summary>Records staged into <see cref="frameStaging"/> since the last flush.</summary>
        private int batchBricks;

        // Alternating with frameStaging, and for the same reason: a second flush in one frame must
        // not rewrite the pair buffer the first flush's dispatches are still reading.
        private readonly GraphicsBuffer[] pairBuffers = new GraphicsBuffer[2];

        /// <summary>
        /// Pair lists, one per destination buffer in the current batch. Kept and cleared rather than
        /// disposed: per-sector storage has thousands of destinations, so the lists are recycled.
        /// </summary>
        private readonly List<NativeList<uint2>> pairLists = new();

        /// <summary>Entries of <see cref="pairLists"/> claimed by the current batch.</summary>
        private int activePairLists;

        /// <summary>Destination buffer to its index in <see cref="pairLists"/>, for this batch.</summary>
        private readonly Dictionary<GraphicsBuffer, int> pairListByDestination = new();

        private readonly List<ScatterGroup> flushGroups = new();

        private int idleFlushes;

        /// <param name="maxBatchBricks">
        /// Cap on one batch, in bricks. The default is <see cref="MaxBatchBricks"/>; lower it to
        /// trade upload memory for more intermediate flushes.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The compute shader is missing. It has to sit in a <c>Resources</c> folder — the package
        /// ships it at <c>Runtime/Resources/CaelixBrickPoolOps.compute</c>.
        /// </exception>
        public CaelixBrickGpuOps(int maxBatchBricks = MaxBatchBricks)
        {
            batchLimitBricks = Math.Max(1, maxBatchBricks);

            shader = Resources.Load<ComputeShader>(ShaderResourceName);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    $"CaelixBrickGpuOps: could not load the compute shader '{ShaderResourceName}'. " +
                    "It must live in a Resources folder; the package ships it at " +
                    "Runtime/Resources/CaelixBrickPoolOps.compute.");
            }

            copyKernel = shader.FindKernel("CaelixCopyBrickRanges");
            moveKernel = shader.FindKernel("CaelixMoveBrickRanges");
            scatterKernel = shader.FindKernel("CaelixScatterBricks");
        }

        /// <summary>
        /// Copies whole brick ranges from one buffer into another. Dispatched immediately.
        /// </summary>
        /// <param name="src">Buffer the records come from. Must not be <paramref name="dst"/>.</param>
        /// <param name="dst">Buffer the records go to.</param>
        /// <param name="ranges">
        /// One entry per range: x = first source brick, y = first destination brick, z = bricks in
        /// the range, w = the sum of every preceding entry's z. Must be sorted by w ascending.
        /// </param>
        /// <param name="rangeCount">Entries of <paramref name="ranges"/> to read.</param>
        /// <param name="totalBricks">Sum of the z of those entries: one thread per brick.</param>
        public void CopyRanges(
            GraphicsBuffer src, GraphicsBuffer dst, NativeArray<uint4> ranges, int rangeCount, int totalBricks)
        {
            if (src == null || dst == null || rangeCount <= 0 || totalBricks <= 0)
            {
                return;
            }

            UploadRanges(ranges, rangeCount);

            shader.SetBuffer(copyKernel, s_SrcId, src);
            shader.SetBuffer(copyKernel, s_DstId, dst);
            shader.SetBuffer(copyKernel, s_RangesId, rangeBuffer);
            shader.SetInt(s_RangeCountId, rangeCount);
            shader.SetInt(s_BrickCountId, totalBricks);

            Dispatch(copyKernel, totalBricks);
        }

        /// <summary>
        /// Moves whole brick ranges inside one buffer. Dispatched immediately.
        /// </summary>
        /// <param name="buffer">The buffer read and written by the same dispatch.</param>
        /// <param name="ranges">Laid out exactly as <see cref="CopyRanges"/>' ranges.</param>
        /// <param name="rangeCount">Entries of <paramref name="ranges"/> to read.</param>
        /// <param name="totalBricks">Sum of the z of those entries: one thread per brick.</param>
        /// <remarks>
        /// The caller must guarantee that no source range of this dispatch overlaps a destination
        /// range of it: threads run in no particular order, so an overlap would read records that
        /// another thread has already overwritten.
        /// </remarks>
        public void MoveRanges(GraphicsBuffer buffer, NativeArray<uint4> ranges, int rangeCount, int totalBricks)
        {
            if (buffer == null || rangeCount <= 0 || totalBricks <= 0)
            {
                return;
            }

            UploadRanges(ranges, rangeCount);

            shader.SetBuffer(moveKernel, s_DstId, buffer);
            shader.SetBuffer(moveKernel, s_RangesId, rangeBuffer);
            shader.SetInt(s_RangeCountId, rangeCount);
            shader.SetInt(s_BrickCountId, totalBricks);

            Dispatch(moveKernel, totalBricks);
        }

        /// <summary>
        /// Adds one sector's brick records to the current batch. Nothing is dispatched until
        /// <see cref="FlushScatter"/>.
        /// </summary>
        /// <param name="dst">Buffer holding the destination range.</param>
        /// <param name="slotBase">The range's first brick inside <paramref name="dst"/>.</param>
        /// <param name="words">
        /// The records back to back, <see cref="BrickRecordLayout.BRICK_DATA_LENGTH"/> words each. Only
        /// the first <paramref name="brickCount"/> records are read, and they are copied out before
        /// this returns, so the caller may free them straight away.
        /// </param>
        /// <param name="slots">
        /// Brick index inside the range for each record. Across everything staged for one
        /// destination in one batch, every <c>slotBase + slot</c> must be distinct: two records
        /// naming one brick would race.
        /// </param>
        /// <param name="brickCount">Number of staged records.</param>
        public void StageScatter(
            GraphicsBuffer dst, int slotBase, NativeArray<int> words, NativeArray<int> slots, int brickCount)
        {
            if (dst == null || brickCount <= 0)
            {
                return;
            }

            if (batchBricks > 0 && batchBricks + brickCount > batchLimitBricks)
            {
                FlushScatter();
            }

            // One sector is at most Sector.BRICKS_IN_SECTOR bricks, far under the cap, so a single
            // stage always fits an empty batch; the Max only keeps a caller that asks for more from
            // writing past the end of the buffer.
            EnsureFrameStaging(Math.Max(brickCount, Math.Min(batchLimitBricks, batchBricks + brickCount)));

            int brickWords = BrickRecordLayout.BRICK_DATA_LENGTH;
            frameStaging[frameStagingIndex].SetData(
                words, 0, batchBricks * brickWords, brickCount * brickWords);

            NativeList<uint2> pairs = PairsFor(dst);
            for (int i = 0; i < brickCount; i++)
            {
                pairs.Add(new uint2((uint)(batchBricks + i), (uint)(slotBase + slots[i])));
            }

            batchBricks += brickCount;
            idleFlushes = 0;
        }

        /// <summary>
        /// Dispatches everything staged since the last flush: one dispatch per destination buffer.
        /// </summary>
        /// <remarks>
        /// Every write to the pair buffer happens before the first dispatch reads it, for the same
        /// reason the record staging is batched at all — see the class remarks.
        /// </remarks>
        public void FlushScatter()
        {
            if (batchBricks == 0)
            {
                idleFlushes++;
                if (idleFlushes >= IdleFlushesBeforeTrim)
                {
                    TrimStagingBuffers();
                }

                return;
            }

            flushGroups.Clear();
            int totalPairs = 0;
            foreach (KeyValuePair<GraphicsBuffer, int> entry in pairListByDestination)
            {
                int count = pairLists[entry.Value].Length;
                if (count == 0)
                {
                    continue;
                }

                flushGroups.Add(new ScatterGroup
                {
                    Destination = entry.Key,
                    List = entry.Value,
                    Start = totalPairs,
                    Count = count
                });

                totalPairs += count;
            }

            if (totalPairs > 0)
            {
                GraphicsBuffer pairs = EnsurePairBuffer(totalPairs);

                for (int i = 0; i < flushGroups.Count; i++)
                {
                    ScatterGroup group = flushGroups[i];
                    pairs.SetData(pairLists[group.List].AsArray(), 0, group.Start, group.Count);
                }

                shader.SetBuffer(scatterKernel, s_StagingId, frameStaging[frameStagingIndex]);
                shader.SetBuffer(scatterKernel, s_PairsId, pairs);

                for (int i = 0; i < flushGroups.Count; i++)
                {
                    ScatterGroup group = flushGroups[i];
                    shader.SetBuffer(scatterKernel, s_DstId, group.Destination);
                    shader.SetInt(s_PairBaseId, group.Start);
                    shader.SetInt(s_BrickCountId, group.Count);
                    Dispatch(scatterKernel, group.Count);
                }
            }

            ResetBatch();

            // The dispatches above read the buffer that was just staged, so the next batch of this
            // frame writes the other one.
            frameStagingIndex ^= 1;
            idleFlushes = 0;
        }

        /// <summary>Drops every staged record without dispatching it.</summary>
        private void ResetBatch()
        {
            for (int i = 0; i < activePairLists; i++)
            {
                pairLists[i].Clear();
            }

            activePairLists = 0;
            pairListByDestination.Clear();
            flushGroups.Clear();
            batchBricks = 0;
        }

        /// <summary>The current batch's pair list for one destination, claimed on first use.</summary>
        private NativeList<uint2> PairsFor(GraphicsBuffer dst)
        {
            if (!pairListByDestination.TryGetValue(dst, out int index))
            {
                if (activePairLists == pairLists.Count)
                {
                    pairLists.Add(new NativeList<uint2>(256, Allocator.Persistent));
                }

                index = activePairLists++;
                pairLists[index].Clear();
                pairListByDestination.Add(dst, index);
            }

            return pairLists[index];
        }

        /// <summary>
        /// Makes sure the current frame staging buffer holds <paramref name="bricks"/> records,
        /// carrying what this batch has already staged into the larger buffer.
        /// </summary>
        private void EnsureFrameStaging(int bricks)
        {
            ref GraphicsBuffer buffer = ref frameStaging[frameStagingIndex];

            int brickWords = BrickRecordLayout.BRICK_DATA_LENGTH;
            int capacityBricks = NextPow2(Math.Max(bricks, Math.Min(MinFrameStagingBricks, batchLimitBricks)));
            if (buffer != null && buffer.IsValid() && buffer.count >= capacityBricks * brickWords)
            {
                return;
            }

            GraphicsBuffer grown = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw, capacityBricks * brickWords, 4);

            if (buffer != null && batchBricks > 0)
            {
                // Nothing has read the old buffer yet, but its records are the only copy there is,
                // so they move across rather than being staged again.
                NativeArray<uint4> ranges = new NativeArray<uint4>(1, Allocator.Temp);
                ranges[0] = new uint4(0u, 0u, (uint)batchBricks, 0u);
                CopyRanges(buffer, grown, ranges, 1, batchBricks);
                ranges.Dispose();
            }

            buffer?.Dispose();
            buffer = grown;
        }

        /// <summary>
        /// The current pair buffer, grown to hold <paramref name="entries"/> pairs. Its contents are
        /// rewritten in full by every flush, so a growth has nothing to preserve.
        /// </summary>
        private GraphicsBuffer EnsurePairBuffer(int entries)
        {
            ref GraphicsBuffer buffer = ref pairBuffers[frameStagingIndex];
            if (buffer == null || !buffer.IsValid() || buffer.count < entries)
            {
                buffer?.Dispose();
                buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, NextPow2(entries), 8);
            }

            return buffer;
        }

        /// <summary>Releases the batch's GPU buffers after a long idle stretch; they come back on demand.</summary>
        private void TrimStagingBuffers()
        {
            for (int i = 0; i < frameStaging.Length; i++)
            {
                frameStaging[i]?.Dispose();
                frameStaging[i] = null;

                pairBuffers[i]?.Dispose();
                pairBuffers[i] = null;
            }

            idleFlushes = 0;
        }

        /// <summary>Uploads the first <paramref name="rangeCount"/> range entries.</summary>
        private void UploadRanges(NativeArray<uint4> ranges, int rangeCount)
        {
            if (rangeBuffer == null || !rangeBuffer.IsValid() || rangeBuffer.count < rangeCount)
            {
                rangeBuffer?.Dispose();
                rangeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, NextPow2(rangeCount), 16);
            }

            rangeBuffer.SetData(ranges, 0, 0, rangeCount);
        }

        /// <summary>
        /// Runs <paramref name="threadCount"/> threads of a kernel, split into as many dispatches as
        /// the group limit needs.
        /// </summary>
        private void Dispatch(int kernel, int threadCount)
        {
            int totalGroups = (threadCount + ThreadsPerGroup - 1) / ThreadsPerGroup;
            int dispatchedGroups = 0;

            while (dispatchedGroups < totalGroups)
            {
                int groups = Math.Min(MaxGroupsPerDispatch, totalGroups - dispatchedGroups);
                shader.SetInt(s_ThreadBaseId, dispatchedGroups * ThreadsPerGroup);
                shader.Dispatch(kernel, groups, 1, 1);
                dispatchedGroups += groups;
            }
        }

        private static int NextPow2(int value)
        {
            int result = 1;
            while (result < value && result < (1 << 30))
            {
                result <<= 1;
            }

            return result;
        }

        public void Dispose()
        {
            ResetBatch();

            for (int i = 0; i < pairLists.Count; i++)
            {
                if (pairLists[i].IsCreated)
                {
                    pairLists[i].Dispose();
                }
            }

            pairLists.Clear();

            rangeBuffer?.Dispose();
            rangeBuffer = null;

            for (int i = 0; i < frameStaging.Length; i++)
            {
                frameStaging[i]?.Dispose();
                frameStaging[i] = null;

                pairBuffers[i]?.Dispose();
                pairBuffers[i] = null;
            }
        }
    }
}
