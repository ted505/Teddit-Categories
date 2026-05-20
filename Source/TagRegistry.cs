using System;
using System.Collections.Generic;
using System.IO;
using Data.ScriptableObject;
using ScriptableObjectScripts;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TedditCategories
{
    /// <summary>
    /// Resolves the set of tags for a given facility. Tags come from two sources:
    ///   1. Auto-derivation from descriptor data (mining resources, refiner output, power source).
    ///   2. Manual YAML overrides loaded at startup (union with auto unless disableAutoTags is set).
    /// </summary>
    internal static class TagRegistry
    {
        public const string ResourceTagPrefix = "resource:";
        public const string PowerTagPrefix = "power:";

        public class ManualEntry
        {
            public List<string> Tags { get; set; } = new List<string>();
            public bool DisableAutoTags { get; set; }
        }

        private static readonly Dictionary<string, ManualEntry> ManualById =
            new Dictionary<string, ManualEntry>(StringComparer.Ordinal);

        private static readonly Dictionary<FacilityBaseDescriptor, List<string>> Cache =
            new Dictionary<FacilityBaseDescriptor, List<string>>();

        public static void LoadManualTagsFromYaml(string path)
        {
            if (!File.Exists(path))
            {
                Plugin.Log.LogInfo($"facility_tags.yaml not found at {path}; using auto-tags only.");
                return;
            }

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                using (var reader = new StreamReader(path))
                {
                    var root = deserializer.Deserialize<Dictionary<string, ManualEntry>>(reader);
                    if (root == null) return;
                    foreach (var kv in root)
                    {
                        if (kv.Value == null) continue;
                        ManualById[kv.Key] = kv.Value;
                    }
                }
                Plugin.Log.LogInfo($"Loaded {ManualById.Count} manual tag entries from {path}.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Failed to parse facility_tags.yaml: {e}");
            }
        }

        public static void ClearCache()
        {
            Cache.Clear();
        }

        public static IReadOnlyList<string> GetTags(FacilityBaseDescriptor desc)
        {
            if (desc == null) return Array.Empty<string>();
            if (Cache.TryGetValue(desc, out var cached)) return cached;

            var set = new HashSet<string>(StringComparer.Ordinal);
            ManualEntry manual = null;
            if (!string.IsNullOrEmpty(desc.ID))
            {
                ManualById.TryGetValue(desc.ID, out manual);
            }

            if (manual == null || !manual.DisableAutoTags)
            {
                AddAutoTags(desc, set);
            }
            if (manual != null && manual.Tags != null)
            {
                foreach (var t in manual.Tags)
                {
                    if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());
                }
            }

            // User-defined categories from facility_categories.yaml. These are explicit so
            // they're always merged in, even when disableAutoTags is set on this facility.
            foreach (var t in CategoryDefinitions.GetTagsFor(desc.ID))
                set.Add(t);

            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            Cache[desc] = list;
            return list;
        }

        private static void AddAutoTags(FacilityBaseDescriptor desc, HashSet<string> set)
        {
            var ability = desc.specialAbilityFacilityNew;

            // Mining: per resource in the base resourcesToMine set. In the 2026-05-15 game
            // data many mining facilities have specialAbility: None (e.g. vanilla
            // build_metalmine) — the only consistent marker for a mine is facilityType
            // == Mining. The HasFlag(Mining) gate that used to work is now useless because
            // the special-ability flag isn't set on most mines.
            if (desc.facilityType == FacilityBaseDescriptor.EFacilityType.Mining)
            {
                var mined = desc.GetResourcesToMine(null);
                if (mined != null)
                {
                    foreach (var res in mined)
                    {
                        if (res != null && !string.IsNullOrEmpty(res.ID))
                            set.Add(ResourceTagPrefix + res.ID);
                    }
                }
            }

            // Refiner: union of input + output resources, all as resource:<id> tags. Tagging
            // by both sides lets modded resources surface as native categories — e.g. a
            // Waste mod's Biowaste recycler shows up under "Biowaste" (input) AND "Water" /
            // "Volatile" (outputs), so the player can find it whether they're searching for
            // "what processes my waste?" or "what produces water?".
            if (ability.HasFlag(ESpecialAbilityFacilityNew.Refiner) && desc.refinerData != null)
            {
                var ins = desc.refinerData.Input;
                if (ins != null)
                {
                    foreach (var item in ins)
                    {
                        if (item != null && item.resource != null && !string.IsNullOrEmpty(item.resource.ID))
                            set.Add(ResourceTagPrefix + item.resource.ID);
                    }
                }
                var outs = desc.refinerData.GetOutput(null, desc);
                if (outs != null)
                {
                    foreach (var item in outs)
                    {
                        if (item != null && item.resource != null && !string.IsNullOrEmpty(item.resource.ID))
                            set.Add(ResourceTagPrefix + item.resource.ID);
                    }
                }
            }

            // Power: source-based
            if (desc.facilityType == FacilityBaseDescriptor.EFacilityType.Power)
            {
                var ep = desc.energyProductionData;
                bool tagged = false;
                if (ep != null)
                {
                    if (ep.solarPanels)        { set.Add(PowerTagPrefix + "solar");         tagged = true; }
                    if (ep.windPower)          { set.Add(PowerTagPrefix + "wind");          tagged = true; }
                    if (ep.geothermalPower)    { set.Add(PowerTagPrefix + "geothermal");    tagged = true; }
                    if (ep.thermoelectricPower){ set.Add(PowerTagPrefix + "thermoelectric");tagged = true; }
                    if (!tagged && ep.input != null && ep.input.Length > 0)
                    {
                        foreach (var consumed in ep.input)
                        {
                            if (consumed != null && consumed.resource != null && !string.IsNullOrEmpty(consumed.resource.ID))
                            {
                                set.Add(PowerTagPrefix + consumed.resource.ID);
                                tagged = true;
                            }
                        }
                    }
                }
                if (!tagged) set.Add(PowerTagPrefix + "other");
            }
        }
    }
}
