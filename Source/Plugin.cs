using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace TedditCategories
{
    [BepInPlugin("com.teddit.categories", "TedditCategories", "0.1.1")]
    [BepInDependency("com.teddit.teddit", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static string PluginDir;

        void Awake()
        {
            Log = Logger;
            PluginDir = Path.GetDirectoryName(Info.Location);
            Log.LogInfo($"TedditCategories v{Info.Metadata.Version} loaded. Dir={PluginDir}");

            try
            {
                TagRegistry.LoadManualTagsFromYaml(Path.Combine(PluginDir, "facility_tags.yaml"));
            }
            catch (Exception e)
            {
                Log.LogError($"LoadManualTagsFromYaml threw: {e}");
            }
            try
            {
                CategoryDefinitions.LoadFromYaml(Path.Combine(PluginDir, "facility_categories.yaml"));
            }
            catch (Exception e)
            {
                Log.LogError($"LoadCategoriesYaml threw: {e}");
            }

            try
            {
                var harmony = new Harmony("com.teddit.categories");
                harmony.PatchAll();
                int count = 0;
                foreach (var m in harmony.GetPatchedMethods()) { count++; Log.LogInfo($"  patched: {m.DeclaringType?.FullName}.{m.Name}"); }
                Log.LogInfo($"Harmony PatchAll done — {count} method(s) patched.");
            }
            catch (Exception e)
            {
                Log.LogError($"Harmony PatchAll threw: {e}");
            }

        }
    }
}
