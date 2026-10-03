using Caelix.Rendering.GiPrototypes;
using NUnit.Framework;
using UnityEngine;

namespace Caelix.Tests
{
    public class GiPrototypeSettingsTests
    {
        [Test]
        public void SelectionAndSettingsRemainFrozenWithinSession()
        {
            var feature = ScriptableObject.CreateInstance<CaelixGiPrototypeFeature>();
            try
            {
                feature.settings.approach = CaelixGiApproach.RestirGi;
                feature.settings.maxBounces = 3;
                var first = feature.CaptureSessionSettings();
                feature.settings.approach = CaelixGiApproach.FaceRadianceCache;
                feature.settings.maxBounces = 7;
                feature.Create();
                var same = feature.CaptureSessionSettings();
                Assert.That(same, Is.SameAs(first));
                Assert.That(same.approach, Is.EqualTo(CaelixGiApproach.RestirGi));
                Assert.That(same.maxBounces, Is.EqualTo(3));
                feature.ResetSession();
                var next = feature.CaptureSessionSettings();
                Assert.That(next.approach, Is.EqualTo(CaelixGiApproach.FaceRadianceCache));
                Assert.That(next.maxBounces, Is.EqualTo(7));
            }
            finally { feature.ReleaseResources(); Object.DestroyImmediate(feature); }
        }

        [TestCase(CaelixGiApproach.ReferencePathTracing)]
        [TestCase(CaelixGiApproach.RestirGi)]
        [TestCase(CaelixGiApproach.NaadfInspired)]
        [TestCase(CaelixGiApproach.FaceRadianceCache)]
        [TestCase(CaelixGiApproach.FacePathGuiding)]
        [TestCase(CaelixGiApproach.BrickEmissionPathGuiding)]
        public void SessionResetAcceptsNewSelectionOnSameFeature(CaelixGiApproach approach)
        {
            var feature = ScriptableObject.CreateInstance<CaelixGiPrototypeFeature>();
            try
            {
                feature.settings.approach = CaelixGiApproach.RestirGi;
                var previous = feature.CaptureSessionSettings();
                feature.ResetSession();
                feature.settings.approach = approach;
                Assert.That(feature.ActiveApproach, Is.EqualTo(approach));
                feature.Create();
                var next = feature.CaptureSessionSettings();
                Assert.That(next, Is.Not.SameAs(previous));
                Assert.That(next.approach, Is.EqualTo(approach));
                Assert.That(feature.AllocatedBytes, Is.Zero);
            }
            finally { feature.ResetSession(); Object.DestroyImmediate(feature); }
        }

        [Test]
        public void RuntimeInitializationResetsSelectionWithoutDomainReload()
        {
            var feature = ScriptableObject.CreateInstance<CaelixGiPrototypeFeature>();
            try
            {
                feature.settings.approach = CaelixGiApproach.RestirGi;
                feature.CaptureSessionSettings();
                feature.settings.approach = CaelixGiApproach.FacePathGuiding;
                CaelixGiPrototypeFeature.BeginSession();
                Assert.That(feature.CaptureSessionSettings().approach, Is.EqualTo(CaelixGiApproach.FacePathGuiding));
                feature.settings.approach = CaelixGiApproach.NaadfInspired;
                CaelixGiPrototypeFeature.BeginSession();
                Assert.That(feature.CaptureSessionSettings().approach, Is.EqualTo(CaelixGiApproach.NaadfInspired));
            }
            finally { feature.ResetSession(); Object.DestroyImmediate(feature); }
        }

        [Test]
        public void CapturedSettingsCannotBeSerializedAcrossReloads()
        {
            var field = typeof(CaelixGiPrototypeFeature).GetField("capturedSettings",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            Assert.That(field.IsDefined(typeof(System.NonSerializedAttribute), false), Is.True);
        }

        [Test]
        public void ValidationBoundsStorageAndReachableConfidence()
        {
            var settings = new CaelixGiSettings
            {
                cacheCapacity = int.MaxValue, cacheHistorySamples = 1, cacheMinSamples = 60,
                resolutionScale = 0, maxBounces = -2, guidingStrength = 1
            };
            var copy = settings.ValidatedCopy();
            Assert.That(copy.cacheCapacity, Is.EqualTo(1048576));
            Assert.That(copy.cacheMinSamples, Is.EqualTo(1));
            Assert.That(copy.resolutionScale, Is.EqualTo(0.25f));
            Assert.That(copy.maxBounces, Is.Zero);
            Assert.That(copy.guidingStrength, Is.EqualTo(0.95f));
            Assert.That(settings.cacheCapacity, Is.EqualTo(int.MaxValue));
        }

        [TestCase(CaelixGiApproach.ReferencePathTracing, 0)]
        [TestCase(CaelixGiApproach.RestirGi, 64 * 64 * 3 + 64 * 16)]
        [TestCase(CaelixGiApproach.NaadfInspired, 64 * 64 + 64 * 16 + 2 * (16 + 8 * 32))]
        [TestCase(CaelixGiApproach.FaceRadianceCache, 1024 * 192)]
        [TestCase(CaelixGiApproach.FacePathGuiding, 1024 * 192)]
        [TestCase(CaelixGiApproach.BrickEmissionPathGuiding, 1024 * 192)]
        public void AllocatesOnlySelectedApproach(CaelixGiApproach approach, int extraBytes)
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Compute shaders unavailable.");
            var settings = new CaelixGiSettings { approach = approach, cacheCapacity = 1024 };
            using var resources = new CaelixGiResources(8, 8, settings);
            Assert.That(resources.EstimatedBytes, Is.EqualTo(64L * (2 * 64 + 3 * 16 + 4) + extraBytes));
            Assert.That(resources.CacheKeys != null, Is.EqualTo(settings.UsesCache));
            Assert.That(resources.Candidates != null, Is.EqualTo(settings.UsesReservoirs));
        }
    }
}
