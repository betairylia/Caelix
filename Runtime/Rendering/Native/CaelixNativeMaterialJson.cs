using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// Parses the Rust host's material JSON (<c>--materials</c>: an array of id, transparent,
    /// albedo, emission, smoothness, metallic, ior, extinction) into block-ID material entries.
    /// </summary>
    internal static class CaelixNativeMaterialJson
    {
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
