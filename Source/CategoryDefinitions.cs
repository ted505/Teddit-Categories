using System;
using System.Collections.Generic;
using System.IO;
using Data.ScriptableObject;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TedditCategories
{
    /// <summary>
    /// Loads facility_categories.yaml — user-defined subcategories scoped to a top-level
    /// facility-type tab. Each entry creates a category that drives one row in the
    /// drill-down for the named tab. Custom-tagged facilities still get their auto-tags
    /// (e.g. resource:id_resource_metal) on top, so a single facility can appear under
    /// multiple categories.
    ///
    /// YAML schema:
    ///
    ///   <FacilityType>:
    ///     - name: <display name>
    ///       icon: <optional facility ID whose sprite to reuse>
    ///       facilities: [<facility_id>, ...]
    ///
    /// FacilityType matches FacilityBaseDescriptor.EFacilityType (Module / Habitation /
    /// Power / Mining / Production / LaunchFacility / Terraformation / Other /
    /// FacilitySegment). Names are case-insensitive on parse.
    /// </summary>
    internal static class CategoryDefinitions
    {
        public const string CategoryTagPrefix = "cat:";

        public class CategoryEntry
        {
            public string Name { get; set; }
            public string IconRef { get; set; }
            public List<string> Facilities { get; set; } = new List<string>();
        }

        // Facility ID -> list of cat:<Type>:<Name> tags the facility belongs to.
        private static readonly Dictionary<string, List<string>> TagsByFacility =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Tag -> display label (user's "name" field) for non-resource categories.
        private static readonly Dictionary<string, string> LabelByTag =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Tag -> facility ID whose Sprite to reuse for the category icon. Optional;
        // falls back to "first facility carrying the tag" when missing.
        private static readonly Dictionary<string, string> IconFacilityIdByTag =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public static void LoadFromYaml(string path)
        {
            TagsByFacility.Clear();
            LabelByTag.Clear();
            IconFacilityIdByTag.Clear();

            if (!File.Exists(path))
            {
                Plugin.Log.LogInfo($"facility_categories.yaml not found at {path}; no custom categories.");
                return;
            }

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                Dictionary<string, List<CategoryEntry>> root;
                using (var reader = new StreamReader(path))
                {
                    root = deserializer.Deserialize<Dictionary<string, List<CategoryEntry>>>(reader);
                }
                if (root == null) return;

                int catCount = 0;
                foreach (var kv in root)
                {
                    if (!Enum.TryParse<FacilityBaseDescriptor.EFacilityType>(kv.Key, ignoreCase: true, out var type))
                    {
                        Plugin.Log.LogWarning($"facility_categories.yaml: unknown facility type '{kv.Key}', skipping.");
                        continue;
                    }
                    if (kv.Value == null) continue;
                    foreach (var entry in kv.Value)
                    {
                        if (entry == null || string.IsNullOrWhiteSpace(entry.Name) || entry.Facilities == null) continue;
                        string tag = CategoryTagPrefix + type + ":" + entry.Name.Trim();
                        LabelByTag[tag] = entry.Name.Trim();
                        if (!string.IsNullOrWhiteSpace(entry.IconRef))
                            IconFacilityIdByTag[tag] = entry.IconRef.Trim();

                        foreach (var f in entry.Facilities)
                        {
                            if (string.IsNullOrWhiteSpace(f)) continue;
                            string fid = f.Trim();
                            if (!TagsByFacility.TryGetValue(fid, out var list))
                            {
                                list = new List<string>();
                                TagsByFacility[fid] = list;
                            }
                            if (!list.Contains(tag)) list.Add(tag);
                        }
                        catCount++;
                    }
                }
                Plugin.Log.LogInfo($"Loaded {catCount} custom categor{(catCount == 1 ? "y" : "ies")} from {path}.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Failed to parse facility_categories.yaml: {e}");
            }
        }

        /// <summary>Returns the cat:&lt;Type&gt;:&lt;Name&gt; tags the given facility ID is mapped to, or empty.</summary>
        public static IReadOnlyList<string> GetTagsFor(string facilityId)
        {
            if (string.IsNullOrEmpty(facilityId)) return Array.Empty<string>();
            return TagsByFacility.TryGetValue(facilityId, out var list) ? list : (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <summary>User-supplied display name for a cat:* tag, or null.</summary>
        public static string TryGetLabel(string tag)
        {
            return LabelByTag.TryGetValue(tag, out var label) ? label : null;
        }

        /// <summary>User-supplied "iconRef: &lt;facility_id&gt;" for the category, or null.</summary>
        public static string TryGetIconFacilityId(string tag)
        {
            return IconFacilityIdByTag.TryGetValue(tag, out var id) ? id : null;
        }
    }
}
