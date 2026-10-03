using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Caelix.Tests
{
    public class GiResamplingTests
    {
        private ComputeShader production;
        private ComputeShader validation;
        private ComputeBuffer candidates;
        private ComputeBuffer surfaces;
        private ComputeBuffer previousSurfaces;
        private ComputeBuffer buckets;
        private ComputeBuffer previousBuckets;
        private ComputeBuffer samples;
        private ComputeBuffer previousSamples;
        private ComputeBuffer results;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsRayTracing)
                Assert.Ignore("GI prototype compute shaders require ray tracing support.");
            production = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/ink.irylia.caelix/Runtime/Rendering/Shaders/GiPrototypes/GiResampling.compute");
            validation = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/ink.irylia.caelix/Tests/Editor/GiResamplingValidation.compute");
            Assert.That(production, Is.Not.Null);
            Assert.That(validation, Is.Not.Null);
            candidates = new ComputeBuffer(64, 64);
            surfaces = new ComputeBuffer(64, 64);
            previousSurfaces = new ComputeBuffer(64, 64);
            buckets = new ComputeBuffer(1, 16);
            previousBuckets = new ComputeBuffer(1, 16);
            samples = new ComputeBuffer(8, 32);
            previousSamples = new ComputeBuffer(8, 32);
            results = new ComputeBuffer(6, 16);
            buckets.SetData(new Vector4[1]);
            previousBuckets.SetData(new Vector4[1]);
            foreach (var shader in new[] { production, validation })
            {
                shader.SetInts("g_GiResolution", 8, 8);
                shader.SetInt("g_GiFrameIndex", 10);
                shader.SetInt("g_GiResetHistory", 1);
                shader.SetInt("g_GiNaadfHistoryValid", 0);
                shader.SetInt("g_GiMaxHistoryFrames", 8);
                shader.SetFloat("g_GiAspect", 1);
                shader.SetFloat("g_GiZoom", 1);
                shader.SetVector("g_GiPreviousJitter", Vector4.zero);
                shader.SetMatrix("g_GiPreviousWorldToCamera", Matrix4x4.identity);
            }
            validation.SetInt("g_TestPreviousFrame", 9);
            validation.SetInt("g_TestHistoryCase", 0);
        }

        [TearDown]
        public void TearDown()
        {
            candidates?.Dispose();
            surfaces?.Dispose();
            previousSurfaces?.Dispose();
            buckets?.Dispose();
            previousBuckets?.Dispose();
            samples?.Dispose();
            previousSamples?.Dispose();
            results?.Dispose();
        }

        [TestCase(0, 0, 9, true, TestName = "ReprojectionAcceptsMatchingRecentVoxelFace")]
        [TestCase(1, 0, 9, false, TestName = "ExplicitResetRejectsOtherwiseValidHistory")]
        [TestCase(0, 1, 9, false, TestName = "ReprojectionRejectsDifferentFaceLifetime")]
        [TestCase(0, 2, 9, false, TestName = "ReprojectionRejectsReversedSurfaceNormal")]
        [TestCase(0, 3, 9, false, TestName = "ReprojectionRejectsChangedMaterial")]
        [TestCase(0, 4, 9, false, TestName = "ReprojectionRejectsOffscreenSurface")]
        [TestCase(0, 5, 9, false, TestName = "ReprojectionRejectsSurfaceBehindCamera")]
        [TestCase(0, 0, 1, false, TestName = "ReprojectionRejectsExpiredReservoir")]
        public void TemporalValidityUsesResetGeometryAndAge(int reset, int geometryCase, int oldFrame, bool expected)
        {
            validation.SetInt("g_GiResetHistory", reset);
            validation.SetInt("g_TestHistoryCase", geometryCase);
            validation.SetInt("g_TestPreviousFrame", oldFrame);
            Initialize(16);
            int kernel = validation.FindKernel("InspectResamplingHistory");
            validation.SetBuffer(kernel, "g_GiSurfaces", surfaces);
            validation.SetBuffer(kernel, "g_GiPreviousSurfaces", previousSurfaces);
            validation.SetBuffer(kernel, "g_GiReservoirHistory", candidates);
            validation.SetBuffer(kernel, "g_TestResamplingResults", results);
            validation.Dispatch(kernel, 1, 1, 1);
            Assert.That(Read()[0].x, Is.EqualTo(expected ? 1f : 0f));
        }

        [Test]
        public void CompressedLitSamplesRetainUnlitProbability()
        {
            Initialize(16);
            BuildBuckets();
            Vector4[] value = InspectBuckets();
            Assert.That(value[1].x, Is.EqualTo(64));
            Assert.That(value[1].y, Is.EqualTo(16));
            Assert.That(value[0].w, Is.EqualTo(8));
            // Each lit observation is (4,2,1) / 0.5, and only 16/64 are lit.
            AssertColor(value[0], new Vector3(2, 1, 0.5f));
        }

        [Test]
        public void BlackFramesDiluteBucketHistoryAndHistoryRemainsBounded()
        {
            production.SetInt("g_GiMaxHistoryFrames", 2);
            Initialize(16);
            BuildBuckets();
            SwapBuckets();
            production.SetInt("g_GiResetHistory", 0);
            production.SetInt("g_GiNaadfHistoryValid", 1);
            Initialize(0);
            BuildBuckets();
            Vector4[] value = InspectBuckets();
            Assert.That(value[1].x, Is.EqualTo(128));
            Assert.That(value[1].y, Is.EqualTo(16));
            AssertColor(value[0], new Vector3(1, 0.5f, 0.25f));

            SwapBuckets();
            BuildBuckets();
            value = InspectBuckets();
            Assert.That(value[1].x, Is.EqualTo(128), "History must stay within two frames of observations.");
            Assert.That(value[1].y, Is.EqualTo(8));
            Assert.That(value[1].z, Is.EqualTo(2));
            AssertColor(value[0], new Vector3(0.5f, 0.25f, 0.125f));
        }

        [TestCase(1, 1, TestName = "SceneResetDropsNaadfBucketHistory")]
        [TestCase(0, 0, TestName = "CameraMotionDropsNaadfBucketHistory")]
        public void InvalidBucketHistoryCannotSupplyOldLighting(int reset, int stationary)
        {
            Initialize(64);
            BuildBuckets();
            SwapBuckets();
            production.SetInt("g_GiResetHistory", reset);
            production.SetInt("g_GiNaadfHistoryValid", stationary);
            Initialize(0);
            BuildBuckets();
            Vector4[] value = InspectBuckets();
            Assert.That(value[1].x, Is.EqualTo(64));
            Assert.That(value[1].y, Is.Zero);
            Assert.That(value[0].w, Is.Zero);
            AssertColor(value[0], Vector3.zero);
        }

        [Test]
        public void ReconnectionUsesSolidAngleJacobianAndRejectsBackside()
        {
            Vector4[] value = ValidateMath();
            Assert.That(value[0].x, Is.EqualTo(1).Within(0.00001));
            Assert.That(value[0].y, Is.EqualTo(4).Within(0.00001));
            Assert.That(value[0].z, Is.EqualTo(0.25).Within(0.00001));
            Assert.That(value[0].w, Is.EqualTo(8f / (5f * Mathf.Sqrt(5f))).Within(0.00001));
            Assert.That(value[1].x, Is.Zero);
            Assert.That(value[1].y, Is.Zero);
            Assert.That(value[1].z, Is.EqualTo(1));
            Assert.That(value[1].w, Is.EqualTo(1));
        }

        [Test]
        public void ZeroWeightObservationsStillNormalizeReservoirEstimate()
        {
            Vector4[] value = ValidateMath();
            Assert.That(value[2].x, Is.EqualTo(4));
            Assert.That(value[2].y, Is.EqualTo(0.5));
            Assert.That(value[2].z, Is.EqualTo(2));
            Assert.That(value[2].w, Is.EqualTo(8));
            Assert.That(value[3].x, Is.EqualTo(4));
            Assert.That(value[3].y, Is.Zero);
            Assert.That(value[3].z, Is.Zero);
            Assert.That(value[3].w, Is.Zero);
        }

        [Test]
        public void RadianceCompressionPreservesPowersOfTwoAndBoundsOverflow()
        {
            Vector4[] value = ValidateMath();
            AssertColor(value[4], new Vector3(8, 4, 2));
            AssertColor(value[5], new Vector3(65408, 65408, 65408));
        }

        private void Initialize(int litCount)
        {
            validation.SetInt("g_TestLitCount", litCount);
            int kernel = validation.FindKernel("InitializeResamplingInputs");
            validation.SetBuffer(kernel, "g_GiSurfacesOut", surfaces);
            validation.SetBuffer(kernel, "g_TestPreviousSurfaces", previousSurfaces);
            validation.SetBuffer(kernel, "g_GiCandidates", candidates);
            validation.Dispatch(kernel, 1, 1, 1);
        }

        private void BuildBuckets()
        {
            int kernel = production.FindKernel("GiNaadfBuildBuckets");
            production.SetBuffer(kernel, "g_GiCandidates", candidates);
            production.SetBuffer(kernel, "g_GiBuckets", buckets);
            production.SetBuffer(kernel, "g_GiPreviousBuckets", previousBuckets);
            production.SetBuffer(kernel, "g_GiBucketSamples", samples);
            production.SetBuffer(kernel, "g_GiPreviousBucketSamples", previousSamples);
            production.Dispatch(kernel, 1, 1, 1);
        }

        private Vector4[] InspectBuckets()
        {
            int kernel = validation.FindKernel("InspectResamplingBucket");
            validation.SetBuffer(kernel, "g_GiBuckets", buckets);
            validation.SetBuffer(kernel, "g_GiBucketSamples", samples);
            validation.SetBuffer(kernel, "g_TestResamplingResults", results);
            validation.Dispatch(kernel, 1, 1, 1);
            return Read();
        }

        private Vector4[] ValidateMath()
        {
            int kernel = validation.FindKernel("ValidateResamplingMath");
            validation.SetBuffer(kernel, "g_TestResamplingResults", results);
            validation.Dispatch(kernel, 1, 1, 1);
            return Read();
        }

        private void SwapBuckets()
        {
            (buckets, previousBuckets) = (previousBuckets, buckets);
            (samples, previousSamples) = (previousSamples, samples);
        }

        private Vector4[] Read()
        {
            var value = new Vector4[6];
            results.GetData(value);
            return value;
        }

        private static void AssertColor(Vector4 value, Vector3 expected)
        {
            Assert.That(value.x, Is.EqualTo(expected.x).Within(0.00001));
            Assert.That(value.y, Is.EqualTo(expected.y).Within(0.00001));
            Assert.That(value.z, Is.EqualTo(expected.z).Within(0.00001));
        }
    }
}
