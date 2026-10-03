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
        BrickEmissionPathGuiding
    }

    [Serializable]
    public sealed class CaelixGiSettings
    {
        public CaelixGiApproach approach = CaelixGiApproach.ReferencePathTracing;
        [Range(0.25f, 1f)] public float resolutionScale = 0.5f;
        [Range(1, 16)] public int samplesPerPixel = 1;
        [Range(0, 12)] public int maxBounces = 4;
        [Min(0)] public float skyIntensity = 1f;
        [Range(1, 256)] public int accumulationFrames = 32;
        [Range(1, 16)] public int spatialSamples = 4;
        [Range(1, 64)] public int spatialRadius = 24;
        [Range(1, 32)] public int reservoirHistoryFrames = 8;
        [Range(1, 256)] public int maxReservoirCount = 32;
        [Range(1024, 1048576)] public int cacheCapacity = 65536;
        [Range(1, 1024)] public int cacheHistorySamples = 64;
        [Range(1, 64)] public int cacheMinSamples = 4;
        [Range(1, 1024)] public int cacheMaxAge = 120;
        [Range(0f, 0.95f)] public float guidingStrength = 0.5f;

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
            return copy;
        }

        public bool UsesCache => approach >= CaelixGiApproach.FaceRadianceCache;
        public bool UsesReservoirs => approach == CaelixGiApproach.RestirGi || approach == CaelixGiApproach.NaadfInspired;
    }
}
