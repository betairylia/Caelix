using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using Caelix.Net;

namespace Caelix.Client
{
    /// <summary>
    /// A bounded run of <see cref="NetMessageType.BrickBatch"/> messages. Owns one
    /// <see cref="BrickBatchApplier"/> and the pins that keep the channel-owned arrays alive until
    /// its jobs complete. Only the main thread enqueues, flushes and disposes this object.
    /// </summary>
    /// <remarks>
    /// Everything about grouping, ordering, allocation, dirty marking and removal lives in the
    /// applier, inside Caelix-Core. This class only bounds how much input one flush retains: the
    /// channel hands over ownership of each received array, and pinning them is cheaper than
    /// staging another copy.
    /// </remarks>
    internal sealed unsafe class BrickReceiveBatch : IDisposable
    {
        private const int MaxPinnedBytes = 64 * 1024 * 1024;
        private const int MaxMessages = 1024;
        private static readonly ProfilerMarker ApplyMarker = new("Client.ApplyBrickBatch");

        private readonly BrickBatchApplier applier = new();
        private readonly List<GCHandle> pins = new();
        private readonly List<ReceivedMessage> messages = new();
        private long pinnedBytes;

        /// <summary>
        /// Queues one message for <paramref name="data"/>. <paramref name="payloadOffset"/> is the
        /// first record byte inside <paramref name="message"/>; the array is pinned until the next
        /// <see cref="Flush"/> returns.
        /// </summary>
        public void Add(in VoxelEntityData data, ref ReceivedMessage message, int payloadOffset, int brickCount)
        {
            if (pins.Count > 0 && (pinnedBytes + message.Buffer.Length > MaxPinnedBytes || pins.Count >= MaxMessages))
            {
                Flush();
            }

            var pin = GCHandle.Alloc(message.Buffer, GCHandleType.Pinned);
            pins.Add(pin);
            pinnedBytes += message.Buffer.Length;
            int payloadLength = message.Length - payloadOffset;
            messages.Add(message);
            message = default; // this batch now owns the lease, even if decoding throws
            applier.Add(
                in data,
                (byte*)pin.AddrOfPinnedObject() + payloadOffset,
                payloadLength,
                brickCount);
        }

        public void Flush()
        {
            if (pins.Count == 0) return;
            using var marker = ApplyMarker.Auto();
            try
            {
                applier.Flush();

                NativeArray<ReplicatedBrickBatchError>.ReadOnly errors = applier.Errors;
                for (int i = 0; i < errors.Length; i++)
                {
                    if (errors[i] != ReplicatedBrickBatchError.None)
                    {
                        Debug.LogException(new InvalidDataException($"Invalid replicated brick batch: {errors[i]}."));
                    }
                }
            }
            finally
            {
                // Flush is the lifetime fence for the pinned input.
                foreach (GCHandle pin in pins) pin.Free();
                pins.Clear();
                foreach (ReceivedMessage message in messages) message.Dispose();
                messages.Clear();
                pinnedBytes = 0;
            }
        }

        public void Dispose()
        {
            Flush();
            applier.Dispose();
        }
    }
}
