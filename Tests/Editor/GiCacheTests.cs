using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Caelix.Tests
{
    public class GiCacheTests
    {
        const int Capacity = 32;
        ComputeShader cache;
        ComputeShader validation;
        ComputeBuffer keys;
        ComputeBuffer history;
        ComputeBuffer nextHistory;
        ComputeBuffer accumulation;
        ComputeBuffer results;
        uint keyB;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Compute shaders unavailable.");
            cache = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/ink.irylia.caelix/Runtime/Rendering/Shaders/GiPrototypes/GiCache.compute");
            validation = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/ink.irylia.caelix/Tests/Editor/GiCacheValidation.compute");
            Assert.That(cache, Is.Not.Null);
            Assert.That(validation, Is.Not.Null);
            keys = new ComputeBuffer(Capacity, 12);
            history = new ComputeBuffer(Capacity, 64);
            nextHistory = new ComputeBuffer(Capacity, 64);
            accumulation = new ComputeBuffer(Capacity, 52);
            results = new ComputeBuffer(3, 16);
            keyB = 2;
            while (Hash(1, keyB) % Capacity != Hash(1, 1) % Capacity) ++keyB;
            validation.SetInt("g_TestKeyAX", 1);
            validation.SetInt("g_TestKeyAY", 1);
            validation.SetInt("g_TestKeyBX", 1);
            validation.SetInt("g_TestKeyBY", unchecked((int)keyB));
            Configure(cache, 1);
            Configure(validation, 1);
            Dispatch(cache, "ClearGiCache");
            SwapHistory();
            Dispatch(cache, "ClearGiCache");
        }

        [TearDown]
        public void TearDown()
        {
            keys?.Dispose();
            history?.Dispose();
            nextHistory?.Dispose();
            accumulation?.Dispose();
            results?.Dispose();
        }

        [Test]
        public void CollidingFacesKeepExactIdentityUnderConcurrentInsertion()
        {
            Dispatch(validation, "InsertKeysConcurrent");
            Dispatch(validation, "InsertKeys");
            Dispatch(validation, "InspectCache");
            Vector4[] values = ReadResults();
            Assert.That(values[1].x, Is.LessThan(Capacity));
            Assert.That(values[1].y, Is.LessThan(Capacity));
            Assert.That(values[1].x, Is.Not.EqualTo(values[1].y));
            Assert.That(values[1].z, Is.EqualTo(1), "A different group lifetime must miss.");
            var metadata = new uint[Capacity * 3];
            keys.GetData(metadata);
            int matchingA = 0, matchingB = 0;
            for (int slot = 0; slot < Capacity; ++slot)
            {
                if (metadata[slot * 3] != 2 || metadata[slot * 3 + 1] != 1) continue;
                if (metadata[slot * 3 + 2] == 1) ++matchingA;
                if (metadata[slot * 3 + 2] == keyB) ++matchingB;
            }
            Assert.That(matchingA, Is.EqualTo(1));
            Assert.That(matchingB, Is.EqualTo(1));
        }

        [Test]
        public void ResolvePublishesMeanAndNormalizedGuideAfterFrameBoundary()
        {
            Dispatch(validation, "InsertKeys");
            Dispatch(validation, "TrainCache");
            Dispatch(validation, "InspectCache");
            Assert.That(ReadResults()[0].w, Is.Zero, "Readers must see only the previous frame.");
            Dispatch(cache, "ResolveGiCache");
            SwapHistory();
            Dispatch(validation, "InspectCache");
            Vector4[] values = ReadResults();
            Assert.That(values[0].w, Is.EqualTo(1));
            Assert.That(values[0].x, Is.EqualTo(32.5f).Within(0.001));
            Assert.That(values[0].y, Is.EqualTo(2f).Within(0.001));
            Assert.That(values[0].z, Is.EqualTo(4f).Within(0.001));
            Assert.That(values[1].w, Is.EqualTo(1));
            Assert.That(values[2].x, Is.EqualTo(1f).Within(0.00001));
            Assert.That(values[2].y, Is.LessThan(0.00001), "Sampling and evaluation must report the same PDF.");
            Assert.That(values[2].z, Is.GreaterThan(0), "The guide must retain exploration in every bin.");
        }

        [Test]
        public void OldEntriesExpireAndCannotReturnStaleLighting()
        {
            Dispatch(validation, "InsertKeys");
            Dispatch(validation, "TrainCache");
            Dispatch(cache, "ResolveGiCache");
            SwapHistory();
            Configure(cache, 100);
            Configure(validation, 100);
            Dispatch(cache, "ResolveGiCache");
            SwapHistory();
            Dispatch(validation, "InspectCache");
            Vector4[] values = ReadResults();
            Assert.That(values[0].w, Is.Zero);
            Assert.That(values[1].w, Is.Zero);
            Assert.That(values[1].x, Is.GreaterThan(Capacity));
        }

        void Configure(ComputeShader shader, int frame)
        {
            shader.SetInt("g_GiCacheCapacity", Capacity);
            shader.SetInt("g_GiCacheFrame", frame);
            shader.SetInt("g_GiCacheMaxAge", 16);
            shader.SetInt("g_GiCacheHistoryLimit", 64);
            shader.SetInt("g_GiCacheMinSamples", 4);
        }

        void Dispatch(ComputeShader shader, string kernelName)
        {
            int kernel = shader.FindKernel(kernelName);
            shader.SetBuffer(kernel, "g_GiCacheKeys", keys);
            shader.SetBuffer(kernel, "g_GiCacheHistory", history);
            shader.SetBuffer(kernel, "g_GiCacheNextHistory", nextHistory);
            shader.SetBuffer(kernel, "g_GiCacheAccum", accumulation);
            if (shader == validation) shader.SetBuffer(kernel, "g_TestResults", results);
            shader.Dispatch(kernel, 1, 1, 1);
        }

        void SwapHistory()
        {
            (history, nextHistory) = (nextHistory, history);
        }

        Vector4[] ReadResults()
        {
            var values = new Vector4[3];
            results.GetData(values);
            return values;
        }

        static uint Hash(uint x, uint y)
        {
            unchecked
            {
                uint h = x * 0x9e3779b9u + y;
                h = (h ^ (h >> 16)) * 0x7feb352du;
                h = (h ^ (h >> 15)) * 0x846ca68bu;
                return h ^ (h >> 16);
            }
        }
    }
}
