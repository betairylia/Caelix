using Caelix.Rendering.GiPrototypes;
using Caelix.Rendering.RayQuery;
using NUnit.Framework;
using UnityEngine;

namespace Caelix.Tests
{
    public class GiPrototypeSettingsTests
    {
        [Test]
        public void ApproachValuesSurviveSwitchingAndSerialization()
        {
            var feature = ScriptableObject.CreateInstance<CaelixGiPrototypeFeature>();
            var restored = ScriptableObject.CreateInstance<CaelixGiPrototypeFeature>();
            try
            {
                var approaches = (CaelixGiApproach[])System.Enum.GetValues(typeof(CaelixGiApproach));
                var expected = new string[approaches.Length];
                foreach (var approach in approaches)
                {
                    feature.SelectApproach(approach);
                    int index = (int)approach;
                    feature.settings.samplesPerPixel = index + 1;
                    feature.settings.accumulationFrames = 20 + index;
                    feature.settings.maxBounces = index;
                    feature.settings.skyIntensity = 0.5f + index;
                    feature.settings.cacheCapacity = 1024 * (index + 1);
                    feature.settings.spatialRadius = 10 + index;
                    feature.settings.guidingStrength = index * 0.1f;
                    expected[index] = JsonUtility.ToJson(feature.settings);
                }
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(feature), restored);
                foreach (var approach in approaches)
                {
                    restored.SelectApproach(approach);
                    Assert.That(JsonUtility.ToJson(restored.settings), Is.EqualTo(expected[(int)approach]), approach.ToString());
                }
            }
            finally
            {
                feature.ResetSession(); restored.ResetSession();
                Object.DestroyImmediate(feature); Object.DestroyImmediate(restored);
            }
        }

        [Test]
        public void ExistingAssetValuesAreKeptAndResetAffectsOnlySelectedApproach()
        {
            var feature = ScriptableObject.CreateInstance<CaelixGiPrototypeFeature>();
            try
            {
                feature.settings.approach = CaelixGiApproach.BrickEmissionPathGuiding;
                feature.settings.cacheCapacity = 262144;
                feature.settings.accumulationFrames = 19;
                feature.SelectApproach(CaelixGiApproach.FaceRadianceCache);
                feature.settings.cacheCapacity = 524288;
                feature.ApplyRecommendedSettings();
                Assert.That(JsonUtility.ToJson(feature.settings),
                    Is.EqualTo(JsonUtility.ToJson(CaelixGiSettings.Recommended(CaelixGiApproach.FaceRadianceCache))));
                feature.SelectApproach(CaelixGiApproach.BrickEmissionPathGuiding);
                Assert.That(feature.settings.cacheCapacity, Is.EqualTo(262144));
                Assert.That(feature.settings.accumulationFrames, Is.EqualTo(19));
                var values = feature.settings;
                feature.SelectApproach(CaelixGiApproach.BrickEmissionPathGuiding);
                Assert.That(feature.settings, Is.SameAs(values));
            }
            finally { feature.ResetSession(); Object.DestroyImmediate(feature); }
        }

        [Test]
        public void ApproachSelectionSupportsUndoAndRedo()
        {
            var feature = ScriptableObject.CreateInstance<CaelixGiPrototypeFeature>();
            try
            {
                feature.settings.maxBounces = 9;
                UnityEditor.Undo.RecordObject(feature, "Test GI selection");
                feature.SelectApproach(CaelixGiApproach.FacePathGuiding);
                UnityEditor.Undo.FlushUndoRecordObjects();
                UnityEditor.Undo.PerformUndo();
                Assert.That(feature.settings.approach, Is.EqualTo(CaelixGiApproach.ReferencePathTracing));
                Assert.That(feature.settings.maxBounces, Is.EqualTo(9));
                UnityEditor.Undo.PerformRedo();
                Assert.That(feature.settings.approach, Is.EqualTo(CaelixGiApproach.FacePathGuiding));
                feature.SelectApproach(CaelixGiApproach.ReferencePathTracing);
                Assert.That(feature.settings.maxBounces, Is.EqualTo(9));
            }
            finally
            {
                UnityEditor.Undo.ClearUndo(feature);
                feature.ResetSession(); Object.DestroyImmediate(feature);
            }
        }

        [TestCase(CaelixGiApproach.ReferencePathTracing, false, false, false)]
        [TestCase(CaelixGiApproach.RestirGi, true, false, false)]
        [TestCase(CaelixGiApproach.NaadfInspired, true, false, false)]
        [TestCase(CaelixGiApproach.FaceRadianceCache, false, true, false)]
        [TestCase(CaelixGiApproach.FacePathGuiding, false, true, true)]
        [TestCase(CaelixGiApproach.BrickEmissionPathGuiding, false, true, true)]
        [TestCase(CaelixGiApproach.BrickSkinIrradiance, false, false, false)]
        public void RecommendedDefaultsUseOneSampleAndRelevantControls(CaelixGiApproach approach,
            bool reservoirs, bool cache, bool guiding)
        {
            var settings = CaelixGiSettings.Recommended(approach);
            Assert.That(settings.approach, Is.EqualTo(approach));
            Assert.That(settings.samplesPerPixel, Is.EqualTo(1));
            Assert.That(settings.accumulationFrames, Is.EqualTo(64));
            Assert.That(settings.cacheCapacity,
                Is.EqualTo(approach == CaelixGiApproach.FaceRadianceCache ? 1048576 : 65536));
            Assert.That(settings.UsesReservoirs, Is.EqualTo(reservoirs));
            Assert.That(settings.UsesCache, Is.EqualTo(cache));
            Assert.That(settings.UsesGuiding, Is.EqualTo(guiding));
            Assert.That(JsonUtility.ToJson(settings.ValidatedCopy()), Is.EqualTo(JsonUtility.ToJson(settings)));
            Assert.That(CaelixGiSettings.Recommended(approach), Is.Not.SameAs(settings));
        }

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
        [TestCase(CaelixGiApproach.BrickSkinIrradiance)]
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
        [TestCase(CaelixGiApproach.BrickSkinIrradiance, 256 * (16 + 384 * 8 + 12 * 4) + 1024 * 4 + 8 * 4)]
        public void AllocatesOnlySelectedApproach(CaelixGiApproach approach, int extraBytes)
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Compute shaders unavailable.");
            var settings = new CaelixGiSettings
            {
                approach = approach, cacheCapacity = 1024, skinBrickCapacity = 256, skinTrainingBudget = 1024
            };
            using var resources = new CaelixGiResources(8, 8, settings);
            Assert.That(resources.EstimatedBytes, Is.EqualTo(64L * (2 * 64 + 3 * 16 + 4) + extraBytes));
            Assert.That(resources.CacheKeys != null, Is.EqualTo(settings.UsesCache));
            Assert.That(resources.Candidates != null, Is.EqualTo(settings.UsesReservoirs));
            Assert.That(resources.SkinTexels != null, Is.EqualTo(settings.UsesSkin));
            Assert.That(settings.SkinBytes, Is.EqualTo(settings.UsesSkin ? extraBytes : 0));
        }

        [TestCase(CaelixGiApproach.ReferencePathTracing)]
        [TestCase(CaelixGiApproach.RestirGi)]
        [TestCase(CaelixGiApproach.NaadfInspired)]
        [TestCase(CaelixGiApproach.FaceRadianceCache)]
        [TestCase(CaelixGiApproach.FacePathGuiding)]
        [TestCase(CaelixGiApproach.BrickEmissionPathGuiding)]
        [TestCase(CaelixGiApproach.BrickSkinIrradiance)]
        public void ResizeRetainsLightingAndOnlyRebuildsPixelStorage(CaelixGiApproach approach)
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Compute shaders unavailable.");
            var settings = new CaelixGiSettings
            {
                approach = approach, cacheCapacity = 1024, skinBrickCapacity = 256, skinTrainingBudget = 1024
            };
            using var resources = new CaelixGiResources(8, 8, settings);
            long initialBytes = resources.EstimatedBytes;
            int initialBuffers = resources.Buffers.Count;
            int initialTextures = resources.Textures.Count;
            var keys = resources.CacheKeys;
            var history = resources.CacheHistory;
            var nextHistory = resources.NextCacheHistory;
            var accumulation = resources.CacheAccum;
            var skinTexels = resources.SkinTexels;
            var surfaces = resources.Surfaces;
            var color = resources.RawColor;
            resources.ValidHistory = resources.ValidLightingHistory = true;
            resources.Frame = 41;
            resources.Resize(8, 8);
            Assert.That(resources.Surfaces, Is.SameAs(surfaces));
            Assert.That(resources.ValidHistory, Is.True);
            resources.Resize(16, 8);
            Assert.That(resources.Surfaces, Is.Not.SameAs(surfaces));
            Assert.That(resources.RawColor, Is.Not.SameAs(color));
            Assert.That(resources.Surfaces.count, Is.EqualTo(128));
            Assert.That(resources.ValidHistory, Is.False);
            Assert.That(resources.ValidLightingHistory, Is.True);
            Assert.That(resources.Frame, Is.EqualTo(41), "Cache ages must survive a resize.");
            Assert.That(resources.CacheKeys, Is.SameAs(keys));
            Assert.That(resources.CacheHistory, Is.SameAs(history));
            Assert.That(resources.NextCacheHistory, Is.SameAs(nextHistory));
            Assert.That(resources.CacheAccum, Is.SameAs(accumulation));
            Assert.That(resources.SkinTexels, Is.SameAs(skinTexels), "Brick skins must survive a resize.");
            long persistentBytes = settings.UsesCache ? 1024 * 192 : settings.SkinBytes;
            Assert.That(resources.EstimatedBytes, Is.EqualTo((initialBytes - persistentBytes) * 2 + persistentBytes));
            resources.Resize(8, 8);
            Assert.That(resources.EstimatedBytes, Is.EqualTo(initialBytes));
            Assert.That(resources.Buffers.Count, Is.EqualTo(initialBuffers));
            Assert.That(resources.Textures.Count, Is.EqualTo(initialTextures));
        }

        [Test]
        public void LightingInvalidationIgnoresCameraButTracksSceneSkyAndMaterials()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Compute shaders unavailable.");
            var gameObject = new GameObject("GI invalidation test");
            gameObject.SetActive(false);
            var renderer = gameObject.AddComponent<CaelixRayQueryRenderer>();
            var sky = new Texture2D(1, 1);
            var otherSky = new Texture2D(1, 1);
            using var resources = new CaelixGiResources(8, 8,
                new CaelixGiSettings { approach = CaelixGiApproach.FaceRadianceCache, cacheCapacity = 1024 });
            try
            {
                renderer.MaterialsBaked = true;
                Assert.That(resources.NeedsLightingReset(renderer, sky), Is.True);
                resources.Scene = renderer;
                resources.SceneRevision = renderer.GiSceneRevision;
                resources.Sky = sky;
                resources.SkyUpdateCount = sky.updateCount;
                resources.FinishFrame(Matrix4x4.identity, 1, Vector2.zero);
                Assert.That(resources.NeedsLightingReset(renderer, sky), Is.False);
                resources.FinishFrame(Matrix4x4.Translate(Vector3.one), 0.5f, Vector2.one);
                resources.Resize(16, 8);
                Assert.That(resources.NeedsLightingReset(renderer, sky), Is.False, "Movement, FOV and size do not change lighting.");
                Assert.That(resources.NeedsLightingReset(renderer, otherSky), Is.True);
                sky.IncrementUpdateCount();
                Assert.That(resources.NeedsLightingReset(renderer, sky), Is.True);
                resources.SkyUpdateCount = sky.updateCount;
                renderer.MaterialsBaked = false;
                Assert.That(resources.NeedsLightingReset(renderer, sky), Is.True);
                renderer.MaterialsBaked = true;
                renderer.InvalidateGiHistory();
                Assert.That(resources.NeedsLightingReset(renderer, sky), Is.True);
            }
            finally { Object.DestroyImmediate(gameObject); Object.DestroyImmediate(sky); Object.DestroyImmediate(otherSky); }
        }
    }
}
