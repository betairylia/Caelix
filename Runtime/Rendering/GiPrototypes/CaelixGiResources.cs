using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Caelix.Rendering.RayQuery;
using UnityEngine;
using UnityEngine.Rendering;

namespace Caelix.Rendering.GiPrototypes
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct CaelixGiEmissionGroup
    {
        public Vector4 row0, row1, row2;
        public uint page, brickBase, brickCount, firstBrick;
        public uint groupToken, reserved0, reserved1, reserved2;
    }

    /// <summary>Only the selected approach allocates its auxiliary storage. Owned by one camera.</summary>
    internal sealed class CaelixGiResources : IDisposable
    {
        public readonly int Width, Height;
        public readonly CaelixGiSettings Settings;
        public readonly List<GraphicsBuffer> Buffers = new();
        public readonly List<RTHandle> Textures = new();
        public GraphicsBuffer Surfaces, PreviousSurfaces;
        public GraphicsBuffer Candidates, TemporalReservoirs, ReservoirHistory;
        public GraphicsBuffer Buckets, PreviousBuckets, BucketSamples, PreviousBucketSamples;
        public GraphicsBuffer CacheKeys, CacheHistory, NextCacheHistory, CacheAccum;
        public GraphicsBuffer EmissionGroups, EmissionWeights, EmissionTree;
        public RTHandle RawColor, Depth, ResolvedColor, PreviousColor, Fallback;
        public int EmissionGroupCount, EmissionBrickCount, EmissionLeafCount;
        public uint SceneRevision;
        public int Frame, LastUsedFrame;
        public CaelixRayQueryRenderer Scene;
        public Texture Sky;
        public uint SkyUpdateCount;
        public bool ValidHistory;
        public Matrix4x4 PreviousView;
        public float PreviousZoom;
        public Vector2 PreviousJitter;
        public long EstimatedBytes { get; private set; }

        public CaelixGiResources(int width, int height, CaelixGiSettings settings)
        {
            Width = width;
            Height = height;
            Settings = settings;
            int pixels = checked(width * height);
            Surfaces = Buffer(pixels, 64, "GI surfaces");
            PreviousSurfaces = Buffer(pixels, 64, "GI previous surfaces");
            RawColor = Texture(RenderTextureFormat.ARGBFloat, "GI raw color");
            Depth = Texture(RenderTextureFormat.RFloat, "GI view depth");
            ResolvedColor = Texture(RenderTextureFormat.ARGBFloat, "GI resolved color");
            PreviousColor = Texture(RenderTextureFormat.ARGBFloat, "GI previous color");
            if (settings.UsesReservoirs)
            {
                Candidates = Buffer(pixels, 64, "GI candidates");
                Fallback = Texture(RenderTextureFormat.ARGBFloat, "GI directional remainder");
                if (settings.approach == CaelixGiApproach.RestirGi)
                {
                    TemporalReservoirs = Buffer(pixels, 64, "GI temporal reservoirs");
                    ReservoirHistory = Buffer(pixels, 64, "GI reservoir history");
                }
                else
                {
                    int tiles = ((width + 7) / 8) * ((height + 7) / 8);
                    Buckets = Buffer(tiles, 16, "GI buckets");
                    PreviousBuckets = Buffer(tiles, 16, "GI previous buckets");
                    BucketSamples = Buffer(tiles * 8, 32, "GI bucket samples");
                    PreviousBucketSamples = Buffer(tiles * 8, 32, "GI previous bucket samples");
                }
            }
            if (settings.UsesCache)
            {
                CacheKeys = Buffer(settings.cacheCapacity, 12, "GI face keys");
                CacheHistory = Buffer(settings.cacheCapacity, 64, "GI cache history");
                NextCacheHistory = Buffer(settings.cacheCapacity, 64, "GI next cache history");
                CacheAccum = Buffer(settings.cacheCapacity, 52, "GI cache accumulation");
            }
        }

        private GraphicsBuffer Buffer(int count, int stride, string name)
        {
            var result = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), stride) { name = name };
            Buffers.Add(result);
            EstimatedBytes += (long)result.count * result.stride;
            return result;
        }

        private RTHandle Texture(RenderTextureFormat format, string name)
        {
            var texture = new RenderTexture(Width, Height, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name, enableRandomWrite = true, filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
            var handle = RTHandles.Alloc(texture);
            Textures.Add(handle);
            EstimatedBytes += (long)Width * Height * (format == RenderTextureFormat.RFloat ? 4 : 16);
            return handle;
        }

        private void ReleaseBuffer(ref GraphicsBuffer buffer)
        {
            if (buffer == null) return;
            EstimatedBytes -= (long)buffer.count * buffer.stride;
            Buffers.Remove(buffer);
            buffer.Dispose();
            buffer = null;
        }

        public void UpdateEmissionGroups(CaelixRayQueryRenderer renderer)
        {
            var descriptors = new List<CaelixGiEmissionGroup>();
            int total = 0;
            foreach (var group in renderer.AllGroups())
            {
                if (!group.TryGetGiGroup(renderer.Instances, out var transform, out int page,
                    out int brickBase, out int brickCount, out uint token)) continue;
                descriptors.Add(new CaelixGiEmissionGroup
                {
                    row0 = transform.GetRow(0), row1 = transform.GetRow(1), row2 = transform.GetRow(2),
                    page = (uint)page, brickBase = (uint)brickBase, brickCount = (uint)brickCount,
                    firstBrick = (uint)total, groupToken = token
                });
                total = checked(total + brickCount);
            }
            descriptors.Sort((a, b) => a.groupToken.CompareTo(b.groupToken));
            int firstBrick = 0;
            for (int index = 0; index < descriptors.Count; index++)
            {
                var descriptor = descriptors[index];
                descriptor.firstBrick = (uint)firstBrick;
                firstBrick = checked(firstBrick + (int)descriptor.brickCount);
                descriptors[index] = descriptor;
            }
            EmissionGroupCount = descriptors.Count;
            EmissionBrickCount = total;
            EmissionLeafCount = Mathf.NextPowerOfTwo(Mathf.Max(1, total));
            EnsureBuffer(ref EmissionGroups, descriptors.Count, 80, "GI emission groups");
            EnsureBuffer(ref EmissionWeights, total, 32, "GI emission voxel weights");
            EnsureBuffer(ref EmissionTree, checked(2 * EmissionLeafCount), 4, "GI emission tree");
            if (descriptors.Count > 0) EmissionGroups.SetData(descriptors);
        }

        private void EnsureBuffer(ref GraphicsBuffer buffer, int count, int stride, string name)
        {
            count = Mathf.Max(1, count);
            if (buffer != null && buffer.count == count) return;
            ReleaseBuffer(ref buffer);
            buffer = Buffer(count, stride, name);
        }

        public void FinishFrame(Matrix4x4 view, float zoom, Vector2 jitter)
        {
            (Surfaces, PreviousSurfaces) = (PreviousSurfaces, Surfaces);
            (ResolvedColor, PreviousColor) = (PreviousColor, ResolvedColor);
            if (Settings.approach == CaelixGiApproach.RestirGi)
                (Candidates, ReservoirHistory) = (ReservoirHistory, Candidates);
            if (Settings.approach == CaelixGiApproach.NaadfInspired)
            {
                (Buckets, PreviousBuckets) = (PreviousBuckets, Buckets);
                (BucketSamples, PreviousBucketSamples) = (PreviousBucketSamples, BucketSamples);
            }
            if (Settings.UsesCache) (CacheHistory, NextCacheHistory) = (NextCacheHistory, CacheHistory);
            PreviousView = view;
            PreviousZoom = zoom;
            PreviousJitter = jitter;
            ValidHistory = true;
            Frame++;
        }

        public void Dispose()
        {
            foreach (var buffer in Buffers) buffer.Dispose();
            foreach (var handle in Textures)
            {
                var texture = handle.rt;
                handle.Release();
                CoreUtils.Destroy(texture);
            }
            Buffers.Clear();
            Textures.Clear();
        }
    }
}
