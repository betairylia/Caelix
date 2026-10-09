using System;
using UnityEngine;

namespace Caelix.Rendering.GiPrototypes
{
    public enum CaelixGiApproach
    {
        ReferencePathTracing,
        RestirGi,
        NaadfInspired,
        FaceRadianceCache,
        FacePathGuiding,
        BrickEmissionPathGuiding,
        BrickSkinIrradiance
    }

    [Serializable]
    public sealed class CaelixGiSettings
    {
        public CaelixGiApproach approach = CaelixGiApproach.ReferencePathTracing;
        [Range(0.25f, 1f)] public float resolutionScale = 0.5f;
        [Range(1, 16)] public int samplesPerPixel = 1;
        [Range(0, 12)] public int maxBounces = 4;
        [Min(0)] public float skyIntensity = 1f;
        [Range(1, 256)] public int accumulationFrames = 64;
        [Range(1, 16)] public int spatialSamples = 4;
        [Range(1, 64)] public int spatialRadius = 8;
        [Range(1, 32)] public int reservoirHistoryFrames = 2;
        [Range(1, 256)] public int maxReservoirCount = 8;
        [Range(1024, 1048576)] public int cacheCapacity = 65536;
        [Range(1, 1024)] public int cacheHistorySamples = 64;
        [Range(1, 64)] public int cacheMinSamples = 4;
        [Range(1, 1024)] public int cacheMaxAge = 120;
        [Range(0f, 0.95f)] public float guidingStrength = 0.5f;
        [Range(256, 262144)] public int skinBrickCapacity = 32768;
        [Range(1, 64)] public int skinWalksPerPixel = 16;
        [Range(1, 8)] public int skinWalkBounces = 4;
        [Range(1, 16)] public int skinTrainingRays = 4;
        [Range(1, 64)] public int skinTrainingWalks = 4;
        [Range(0, 12)] public int skinColdBounces = 1;
        [Range(1024, 1048576)] public int skinTrainingBudget = 262144;
        public bool skinEmitterSampling = true;

        public const int SkinTexelsPerBrick = 384;
        public const int SkinMarkWordsPerBrick = 12;
        public const int SkinControlWords = 8;
        /// <summary>Frames without a lighting reset before the emitter proposal is (re)built.</summary>
        public const int EmitterSettleFrames = 30;

        public static CaelixGiSettings Recommended(CaelixGiApproach approach)
        {
            if (!Enum.IsDefined(typeof(CaelixGiApproach), approach))
                throw new ArgumentOutOfRangeException(nameof(approach));
            return new CaelixGiSettings
            {
                approach = approach,
                cacheCapacity = approach == CaelixGiApproach.FaceRadianceCache ? 1048576 : 65536
            };
        }

        public CaelixGiSettings ValidatedCopy()
        {
            var copy = (CaelixGiSettings)MemberwiseClone();
            if (!Enum.IsDefined(typeof(CaelixGiApproach), copy.approach))
                copy.approach = CaelixGiApproach.ReferencePathTracing;
            copy.resolutionScale = Mathf.Clamp(copy.resolutionScale, 0.25f, 1f);
            copy.samplesPerPixel = Mathf.Clamp(copy.samplesPerPixel, 1, 16);
            copy.maxBounces = Mathf.Clamp(copy.maxBounces, 0, 12);
            copy.skyIntensity = Mathf.Max(0, copy.skyIntensity);
            copy.accumulationFrames = Mathf.Clamp(copy.accumulationFrames, 1, 256);
            copy.spatialSamples = Mathf.Clamp(copy.spatialSamples, 1, 16);
            copy.spatialRadius = Mathf.Clamp(copy.spatialRadius, 1, 64);
            copy.reservoirHistoryFrames = Mathf.Clamp(copy.reservoirHistoryFrames, 1, 32);
            copy.maxReservoirCount = Mathf.Clamp(copy.maxReservoirCount, 1, 256);
            copy.cacheCapacity = Mathf.Clamp(copy.cacheCapacity, 1024, 1048576);
            copy.cacheHistorySamples = Mathf.Clamp(copy.cacheHistorySamples, 1, 1024);
            copy.cacheMinSamples = Mathf.Clamp(copy.cacheMinSamples, 1, Mathf.Min(64, copy.cacheHistorySamples));
            copy.cacheMaxAge = Mathf.Clamp(copy.cacheMaxAge, 1, 1024);
            copy.guidingStrength = Mathf.Clamp(copy.guidingStrength, 0f, 0.95f);
            copy.skinBrickCapacity = Mathf.Clamp(copy.skinBrickCapacity, 256, 262144);
            copy.skinWalksPerPixel = Mathf.Clamp(copy.skinWalksPerPixel, 1, 64);
            copy.skinWalkBounces = Mathf.Clamp(copy.skinWalkBounces, 1, 8);
            copy.skinTrainingRays = Mathf.Clamp(copy.skinTrainingRays, 1, 16);
            copy.skinTrainingWalks = Mathf.Clamp(copy.skinTrainingWalks, 1, 64);
            copy.skinColdBounces = Mathf.Clamp(copy.skinColdBounces, 0, 12);
            copy.skinTrainingBudget = Mathf.Clamp(copy.skinTrainingBudget, 1024, 1048576);
            return copy;
        }

        public bool UsesCache => approach == CaelixGiApproach.FaceRadianceCache
            || approach == CaelixGiApproach.FacePathGuiding || approach == CaelixGiApproach.BrickEmissionPathGuiding;
        public bool UsesReservoirs => approach == CaelixGiApproach.RestirGi || approach == CaelixGiApproach.NaadfInspired;
        public bool UsesGuiding => approach == CaelixGiApproach.FacePathGuiding || approach == CaelixGiApproach.BrickEmissionPathGuiding;
        public bool UsesSkin => approach == CaelixGiApproach.BrickSkinIrradiance;
        /// <summary>The group descriptor table is needed by emitter sampling and by brick skin lookups.</summary>
        public bool UsesGroupTable => approach == CaelixGiApproach.BrickEmissionPathGuiding || UsesSkin;
        public bool UsesEmitterSampling => approach == CaelixGiApproach.BrickEmissionPathGuiding || (UsesSkin && skinEmitterSampling);

        /// <summary>Bytes of the brick skin tables these settings allocate, independent of resolution.</summary>
        public long SkinBytes => !UsesSkin ? 0 :
            (long)skinBrickCapacity * (16 + SkinTexelsPerBrick * 8 + SkinMarkWordsPerBrick * 4)
            + (long)skinTrainingBudget * 4 + SkinControlWords * 4;
    }
}
