using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Caelix.Tests
{
    public class GiBsdfTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Result
        {
            public Vector4 density;
            public Vector4 sampledEnergy;
            public Vector4 integratedEnergy;
            public Vector4 errors;
        }

        private Result[] results;

        [OneTimeSetUp]
        public void DispatchActualShaderMath()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Compute shaders unavailable.");
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/ink.irylia.caelix/Tests/Editor/GiBsdfValidation.compute");
            Assert.That(shader, Is.Not.Null);
            using var buffer = new ComputeBuffer(9 * 64, Marshal.SizeOf<Result>());
            int kernel = shader.FindKernel("ValidateBsdf");
            shader.SetBuffer(kernel, "Results", buffer);
            shader.Dispatch(kernel, 9, 1, 1);
            results = new Result[9 * 64];
            buffer.GetData(results);
        }

        private Result Aggregate(int index)
        {
            var aggregate = new Result();
            for (int lane = 0; lane < 64; ++lane)
            {
                Result value = results[index * 64 + lane];
                aggregate.density += value.density / 64;
                aggregate.sampledEnergy += value.sampledEnergy / 64;
                aggregate.integratedEnergy += value.integratedEnergy / 64;
                aggregate.errors = Vector4.Max(aggregate.errors, value.errors);
            }
            return aggregate;
        }

        [TestCase(0, TestName = "RoughPlasticMatchesItsSolidAngleDensity")]
        [TestCase(1, TestName = "RoughMetalMatchesItsSolidAngleDensity")]
        [TestCase(2, TestName = "RoughGlassEntryMatchesItsRefractiveDensity")]
        [TestCase(3, TestName = "RoughGlassExitMatchesItsRefractiveDensity")]
        public void ContinuousSamplingMatchesIndependentIntegration(int index)
        {
            Result result = Aggregate(index);
            Assert.That(result.sampledEnergy.w, Is.Zero, "Invalid weights");
            Assert.That(result.errors.x, Is.LessThan(0.0002f), "Sample / evaluation disagreement");
            Assert.That(result.errors.y, Is.LessThan(0.0002f), "Reflection reciprocity");
            Assert.That(result.errors.w, Is.LessThan(0.0002f), "Direction normalization");
            Assert.That(result.density.x, Is.EqualTo(result.density.z).Within(0.06f), "PDF integral must equal the accepted sample probability, including rejected microfacets");
            Assert.That(result.density.y, Is.EqualTo(result.density.w).Within(0.06f), "Reflection probability");
            for (int channel = 0; channel < 3; ++channel)
                Assert.That(result.sampledEnergy[channel], Is.EqualTo(result.integratedEnergy[channel]).Within(0.07f), $"Integrated channel {channel}");
        }

        [TestCase(4, TestName = "PerfectWhiteMirrorPreservesEnergy")]
        [TestCase(6, TestName = "TotalInternalReflectionPreservesEnergy")]
        [TestCase(8, TestName = "EqualIndicesTransmitWithoutScattering")]
        public void DiscreteUnitEventsPreserveEnergy(int index)
        {
            Result result = Aggregate(index);
            Assert.That(result.density.x, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(result.errors.z, Is.LessThan(0.00001f));
            Assert.That(result.sampledEnergy.x, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(result.sampledEnergy.w, Is.Zero);
        }

        [Test]
        public void SmoothGlassUsesFresnelAndRadianceEtaSquared()
        {
            Result result = Aggregate(5);
            Assert.That(result.density.x, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(result.density.y, Is.EqualTo(0.04f).Within(0.006f));
            Assert.That(result.sampledEnergy.x, Is.EqualTo(0.04f + 0.96f / 2.25f).Within(0.006f));
            Assert.That(result.density.z, Is.Zero, "Discrete events have no continuous density");
        }

        [Test]
        public void SmoothPlasticRetainsItsContinuousDiffuseLobe()
        {
            Result result = Aggregate(7);
            Assert.That(result.density.x, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(result.density.z, Is.InRange(0.8f, 0.99f));
            Assert.That(result.sampledEnergy.w, Is.Zero);
            Assert.That(result.errors.x, Is.LessThan(0.0002f));
        }
    }
}
