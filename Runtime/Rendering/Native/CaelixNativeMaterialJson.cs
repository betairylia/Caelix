using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// Parses the Rust host's material JSON (<c>--materials</c>: an array of id, transparent,
    /// albedo, emission, smoothness, metallic, ior, extinction, and the optional sss_model,
    /// sss_probability, sss_albedo, sss_anisotropy, sss_steps, sss_params, texture_set,
    /// connect_group; docs/MATERIALS.md of Caelix-Renderer) into block-ID material entries. An
    /// absent optional key is 0; the values are clamped to the ranges of <c>CaelixMaterial</c>.
    /// </summary>
    internal static class CaelixNativeMaterialJson
    {
        /// <summary>Length of <c>CaelixMaterial::sss_params</c>.</summary>
        private const int SssParamCount = 8;

        // The field names are the JSON keys (JsonUtility matches them exactly).
        [Serializable]
        private class Entry
        {
            public int id = -1;
            public bool transparent;
            public float[] albedo;
            public float[] emission;
            public float smoothness;
            public float metallic;
            public float ior = 1.5f;
            public float extinction;
            public int sss_model;
            public float sss_probability;
            public float[] sss_albedo;
            public float sss_anisotropy;
            public int sss_steps;
            public float[] sss_params;
            public int texture_set;
            public int connect_group;
        }

        [Serializable]
        private class EntryList
        {
            public Entry[] items;
        }

        /// <summary>
        /// The material entries of <paramref name="asset"/>; null without an asset or on a parse
        /// error. Entries with an id out of range are skipped with a warning.
        /// </summary>
        public static unsafe CaelixNativeMaterialEntry[] Parse(TextAsset asset, bool verbose, Object context)
        {
            if (asset == null) return null;
            EntryList list;
            try
            {
                list = JsonUtility.FromJson<EntryList>("{\"items\":" + asset.text + "}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Caelix native: {asset.name} is not a material JSON array: {ex.Message}", context);
                return null;
            }
            if (list?.items == null) return null;
            var entries = new List<CaelixNativeMaterialEntry>(list.items.Length);
            bool warnedSssParams = false;
            for (int i = 0; i < list.items.Length; i++)
            {
                Entry item = list.items[i];
                if (item == null) continue;
                int limit = item.transparent ? 0x1FF : 0x7FFF;
                if (item.id < 0 || item.id > limit)
                {
                    Debug.LogWarning($"Caelix native: {asset.name} entry {i}: id {item.id} out of range (0..{limit}); skipped.", context);
                    continue;
                }
                var entry = new CaelixNativeMaterialEntry
                {
                    code = item.transparent ? (uint)item.id : 0x8000u | (uint)item.id,
                };
                entry.material.albedo[0] = Element(item.albedo, 0, 1f);
                entry.material.albedo[1] = Element(item.albedo, 1, 1f);
                entry.material.albedo[2] = Element(item.albedo, 2, 1f);
                entry.material.emission[0] = Element(item.emission, 0, 0f);
                entry.material.emission[1] = Element(item.emission, 1, 0f);
                entry.material.emission[2] = Element(item.emission, 2, 0f);
                entry.material.smoothness = item.smoothness;
                entry.material.metallic = item.metallic;
                entry.material.ior = item.ior > 0f ? item.ior : 1.5f;
                entry.material.extinction = Mathf.Max(0f, item.extinction);
                entry.material.sssModel = (uint)Mathf.Clamp(item.sss_model, 0, 255);
                entry.material.sssProbability = Mathf.Clamp01(item.sss_probability);
                // The library rejects the whole entry list for one value outside [0, 1].
                entry.material.sssAlbedo[0] = Mathf.Clamp01(Element(item.sss_albedo, 0, 0f));
                entry.material.sssAlbedo[1] = Mathf.Clamp01(Element(item.sss_albedo, 1, 0f));
                entry.material.sssAlbedo[2] = Mathf.Clamp01(Element(item.sss_albedo, 2, 0f));
                entry.material.sssAnisotropy = Mathf.Clamp(item.sss_anisotropy, -1f, 1f);
                entry.material.sssSteps = (uint)Mathf.Clamp(item.sss_steps, 0, 255);
                if (item.sss_params != null)
                {
                    int count = Math.Min(SssParamCount, item.sss_params.Length);
                    for (int p = 0; p < count; p++) entry.material.sssParams[p] = item.sss_params[p];
                    if (item.sss_params.Length > SssParamCount && !warnedSssParams)
                    {
                        warnedSssParams = true;
                        Debug.LogWarning($"Caelix native: {asset.name} entry {i}: sss_params has {item.sss_params.Length} " +
                                         $"values; only the first {SssParamCount} are used (warned once per asset).", context);
                    }
                }
                entry.material.textureSet = (uint)Mathf.Clamp(item.texture_set, 0, 65535);
                entry.material.connectGroup = (uint)Mathf.Clamp(item.connect_group, 0, 65535);
                entry.material.reserved = 0;
                entries.Add(entry);
            }
            CaelixNativeMaterialEntry[] parsed = entries.ToArray();
            if (verbose)
            {
                Debug.Log($"Caelix native: {parsed.Length} material entries from {asset.name}.", context);
            }
            return parsed;
        }

        private static float Element(float[] values, int index, float fallback)
        {
            return values != null && values.Length == 3 ? values[index] : fallback;
        }
    }
}
