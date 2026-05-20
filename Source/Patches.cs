using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Data.ScriptableObject;
using Game.UI.Windows.Elements.ChoseFacilityElements;
using Game.UI.Windows.Windows;
using HarmonyLib;
using Language;
using Manager;
using ScriptableObjectScripts;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TedditCategories
{
    /// <summary>
    /// State stamped onto each UIFacilityList we patch — tracks the cloned
    /// "category rows" inserted into the row list and which mode we're in.
    /// </summary>
    internal class CategoryListState : MonoBehaviour
    {
        public ChoseFacilityWindow window;
        public UIFacilityList list;
        public FacilityBaseDescriptor.EFacilityType currentType;
        public readonly List<CategoryRow> categoryRows = new List<CategoryRow>();
        public CategoryRow backRow;
        public string drilledIntoTag;        // null = in category-list mode
        public bool drillDownActive;          // true if there are 2+ tags worth showing

        public class CategoryRow
        {
            public string tag;                // null = "All"; "__back" = Back row
            public UIRowFacility row;         // cloned UIRowFacility being repurposed
        }
    }

    [HarmonyPatch(typeof(ChoseFacilityWindow), "SetData")]
    internal static class ChoseFacilityWindow_SetData_Patch
    {
        static void Postfix(ChoseFacilityWindow __instance)
        {
            try { CategoryDrill.OnSetData(__instance); }
            catch (Exception e) { Plugin.Log.LogError($"SetData postfix failed: {e}"); }
        }
    }

    [HarmonyPatch(typeof(UIFacilityList), nameof(UIFacilityList.FacilityType), MethodType.Setter)]
    internal static class UIFacilityList_SetFacilityType_Patch
    {
        static void Postfix(UIFacilityList __instance, FacilityBaseDescriptor.EFacilityType value)
        {
            try { CategoryDrill.OnFacilityTypeChanged(__instance, value); }
            catch (Exception e) { Plugin.Log.LogError($"set_FacilityType postfix failed: {e}"); }
        }
    }

    internal static class CategoryDrill
    {
        // Reflected accessors into UIRowFacility (private fields).
        private static readonly FieldInfo F_row_image                  = AccessTools.Field(typeof(UIRowFacility), "image");
        private static readonly FieldInfo F_row_imgType                = AccessTools.Field(typeof(UIRowFacility), "imgType");
        private static readonly FieldInfo F_row_textName               = AccessTools.Field(typeof(UIRowFacility), "textName");
        private static readonly FieldInfo F_row_textMass               = AccessTools.Field(typeof(UIRowFacility), "textMass");
        private static readonly FieldInfo F_row_textCost               = AccessTools.Field(typeof(UIRowFacility), "textCost");
        private static readonly FieldInfo F_row_textBuildTime          = AccessTools.Field(typeof(UIRowFacility), "textBuildTime");
        private static readonly FieldInfo F_row_textEnergyConsumption  = AccessTools.Field(typeof(UIRowFacility), "textEnergyConsumption");
        private static readonly FieldInfo F_row_textRequiredCrew       = AccessTools.Field(typeof(UIRowFacility), "textRequiredCrew");
        private static readonly FieldInfo F_row_btnInfo                = AccessTools.Field(typeof(UIRowFacility), "btnInfo");
        private static readonly FieldInfo F_row_disabledMask           = AccessTools.Field(typeof(UIRowFacility), "disabledMask");

        // UIList<,> private fields we read by walking up the class hierarchy.
        private static readonly FieldInfo F_list_parentPrefab          = AccessTools.Field(typeof(UIFacilityList), "parentPrefab");
        private static readonly FieldInfo F_list_toggleGroup           = AccessTools.Field(typeof(UIFacilityList), "toggleGroup");

        public static void OnSetData(ChoseFacilityWindow window)
        {
            var list = GetList(window);
            if (list == null) return;

            var state = list.gameObject.GetComponent<CategoryListState>() ?? list.gameObject.AddComponent<CategoryListState>();
            state.window = window;
            state.list = list;

            DestroyCategoryRows(state);
            state.currentType = list.FacilityType;
            state.drilledIntoTag = null;
            RebuildCategoryRows(state);
            ApplyView(state);
        }

        public static void OnFacilityTypeChanged(UIFacilityList list, FacilityBaseDescriptor.EFacilityType value)
        {
            var state = list.gameObject.GetComponent<CategoryListState>();
            if (state == null) return; // SetData not run yet
            state.currentType = value;
            state.drilledIntoTag = null;
            DestroyCategoryRows(state);
            RebuildCategoryRows(state);
            ApplyView(state);
        }

        // --- categorization ---

        private static UIFacilityList GetList(ChoseFacilityWindow window)
        {
            var f = AccessTools.Field(typeof(ChoseFacilityWindow), "uiFacilityList");
            return f?.GetValue(window) as UIFacilityList;
        }

        // Sentinel tag for the auto-generated catch-all subcategory. Not a real tag —
        // only handled in the drill-down filter to show rows that have zero tags.
        public const string OtherSentinel = "__other__";

        private static List<string> CollectTagsForType(UIFacilityList list, FacilityBaseDescriptor.EFacilityType type)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in list.CreateRows)
            {
                var desc = row?.facilityDescriptor;
                if (desc == null || desc.facilityType != type) continue;
                foreach (var t in TagRegistry.GetTags(desc)) set.Add(t);
            }
            var ordered = set.ToList();
            ordered.Sort(StringComparer.Ordinal);
            return ordered;
        }

        private static bool HasUntaggedRowOfType(UIFacilityList list, FacilityBaseDescriptor.EFacilityType type)
        {
            foreach (var row in list.CreateRows)
            {
                var desc = row?.facilityDescriptor;
                if (desc == null || desc.facilityType != type) continue;
                if (TagRegistry.GetTags(desc).Count == 0) return true;
            }
            return false;
        }

        // --- view rebuilds ---

        private static void DestroyCategoryRows(CategoryListState state)
        {
            foreach (var cr in state.categoryRows)
            {
                if (cr.row != null) UnityEngine.Object.Destroy(cr.row.gameObject);
            }
            state.categoryRows.Clear();
            if (state.backRow?.row != null) UnityEngine.Object.Destroy(state.backRow.row.gameObject);
            state.backRow = null;
        }

        private static UIRowFacility CloneRowTemplate(CategoryListState state)
        {
            // Find any existing real row to clone — its visuals (toggle, layout, text styling) are
            // exactly what we want for our category rows.
            var template = state.list.CreateRows.FirstOrDefault();
            if (template == null) return null;
            var parent = (Transform)F_list_parentPrefab.GetValue(state.list);
            var clone = UnityEngine.Object.Instantiate(template.gameObject, parent != null ? parent : template.transform.parent, false);
            return clone.GetComponent<UIRowFacility>();
        }

        private static void StyleAsCategory(UIRowFacility row, Sprite icon, string title)
        {
            if (row == null) return;

            // Drop the cloned row's underlying data so its tooltip/info path no-ops
            // (otherwise it inherits the donor facility's name + tooltip).
            row.curentRowFacilityData = null;
            row.facilityDescriptor = null;

            // Hide stat / info bits.
            var textMass = F_row_textMass.GetValue(row) as Component;
            var textCost = F_row_textCost.GetValue(row) as Component;
            var textBuildTime = F_row_textBuildTime.GetValue(row) as Component;
            var textEnergy = F_row_textEnergyConsumption.GetValue(row) as Component;
            var textCrew = F_row_textRequiredCrew.GetValue(row) as Component;
            var btnInfo = F_row_btnInfo.GetValue(row) as Component;
            var imgType = F_row_imgType.GetValue(row) as Component;
            var disabledMask = F_row_disabledMask.GetValue(row) as Component;

            if (textMass != null)       textMass.gameObject.SetActive(false);
            if (textCost != null)       textCost.gameObject.SetActive(false);
            if (textBuildTime != null)  textBuildTime.gameObject.SetActive(false);
            if (textEnergy != null)     textEnergy.gameObject.SetActive(false);
            if (textCrew != null)       textCrew.gameObject.SetActive(false);
            if (btnInfo != null)        btnInfo.gameObject.SetActive(false);
            if (imgType != null)        imgType.gameObject.SetActive(false);
            if (disabledMask != null)   disabledMask.gameObject.SetActive(false);

            var image = F_row_image.GetValue(row) as Image;
            if (image != null)
            {
                if (icon == null) image.gameObject.SetActive(false);
                else              image.sprite = icon;
            }

            var textName = F_row_textName.GetValue(row) as TMP_Text;
            if (textName != null) textName.text = title;

            row.IsDisabled = false;

            // Compact height — category rows are buttons, not facility listings.
            var rt = row.transform as RectTransform;
            float baseHeight = rt != null ? rt.rect.height : 0f;
            if (baseHeight <= 1f)
            {
                var templateLe = row.GetComponent<LayoutElement>();
                if (templateLe != null) baseHeight = Mathf.Max(templateLe.preferredHeight, templateLe.minHeight);
            }
            if (baseHeight > 1f)
            {
                var le = row.GetComponent<LayoutElement>() ?? row.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = baseHeight * 0.5f;
                le.minHeight       = baseHeight * 0.5f;
            }
        }

        private static void HookCategoryToggle(CategoryListState state, CategoryListState.CategoryRow cr)
        {
            // The cloned UIRowFacility came with a Toggle already registered to the list's
            // toggleGroup (via OnSetDataAfterFunction). Pull it out so it can't be selected
            // as a facility, then add our drill-in handler.
            var toggle = cr.row.Toggle;
            var toggleGroup = F_list_toggleGroup.GetValue(state.list) as ToggleGroup;
            if (toggleGroup != null) toggleGroup.UnregisterToggle(toggle);
            toggle.group = null;
            toggle.onValueChanged.RemoveAllListeners();

            string capturedTag = cr.tag;
            toggle.onValueChanged.AddListener(on =>
            {
                if (!on) return;
                toggle.SetIsOnWithoutNotify(false); // categories aren't a sticky selection
                if (capturedTag == "__back") DrillUp(state);
                else                          DrillInto(state, capturedTag);
            });
        }

        private static void RebuildCategoryRows(CategoryListState state)
        {
            // The Modules tab uses a different layout (gridded SpaceModuleDescriptor cards)
            // and project preference is to leave it alone — no drill-down on Modules.
            if (state.currentType == FacilityBaseDescriptor.EFacilityType.Module)
            {
                state.drillDownActive = false;
                return;
            }

            var tags = CollectTagsForType(state.list, state.currentType);

            // Synthetic "OTHER" subcategory: if at least one row of this type has no tags
            // at all, append the sentinel so those rows are still reachable in the
            // drill-down rather than silently disappearing.
            bool hasUntagged = HasUntaggedRowOfType(state.list, state.currentType);
            if (hasUntagged && tags.Count >= 1) tags.Add(OtherSentinel);

            Plugin.Log.LogInfo($"[Cat] type={state.currentType} tags=[{string.Join(", ", tags)}]");
            state.drillDownActive = tags.Count >= 2;
            if (!state.drillDownActive) return;

            foreach (var tag in tags)
            {
                var row = CloneRowTemplate(state);
                if (row == null) break;
                StyleAsCategory(row, ResolveIcon(state.list, tag), ResolveLabel(tag));
                var cr = new CategoryListState.CategoryRow { tag = tag, row = row };
                state.categoryRows.Add(cr);
                HookCategoryToggle(state, cr);
            }

            // Trailing "All" pseudo-tag — null tag means "show every facility of this type".
            {
                var row = CloneRowTemplate(state);
                if (row != null)
                {
                    StyleAsCategory(row, null, LEManager.Get("TedditCategories.All", "ALL"));
                    var cr = new CategoryListState.CategoryRow { tag = null, row = row };
                    state.categoryRows.Add(cr);
                    HookCategoryToggle(state, cr);
                }
            }

            // Back row (hidden until drilled in).
            {
                var row = CloneRowTemplate(state);
                if (row != null)
                {
                    StyleAsCategory(row, null, LEManager.Get("TedditCategories.Back", "← BACK"));
                    state.backRow = new CategoryListState.CategoryRow { tag = "__back", row = row };
                    HookCategoryToggle(state, state.backRow);
                }
            }
        }

        private static void ApplyView(CategoryListState state)
        {
            if (!state.drillDownActive)
            {
                // Nothing to drill into — let stock behavior show through unchanged.
                if (state.backRow?.row != null) state.backRow.row.gameObject.SetActive(false);
                return;
            }

            bool inCategoryList = state.drilledIntoTag == null;

            // Real facility rows: in category-list mode hide them all; in drill mode show those
            // matching FacilityType AND the drilled tag (null = "All" pseudo-category).
            foreach (var row in state.list.CreateRows)
            {
                var desc = row?.facilityDescriptor;
                if (desc == null) continue;
                bool typeMatch = desc.facilityType == state.currentType;
                if (!typeMatch) { row.gameObject.SetActive(false); continue; }

                if (inCategoryList) { row.gameObject.SetActive(false); continue; }

                if (state.drilledIntoTag == "__all")
                {
                    row.gameObject.SetActive(true);
                }
                else if (state.drilledIntoTag == OtherSentinel)
                {
                    // OTHER subcategory = rows with zero tags.
                    var t = TagRegistry.GetTags(desc);
                    row.gameObject.SetActive(t.Count == 0);
                }
                else
                {
                    var t = TagRegistry.GetTags(desc);
                    row.gameObject.SetActive(t.Contains(state.drilledIntoTag));
                }
            }

            // Category rows visible only in category-list mode.
            foreach (var cr in state.categoryRows)
            {
                if (cr.row != null) cr.row.gameObject.SetActive(inCategoryList);
            }
            if (state.backRow?.row != null) state.backRow.row.gameObject.SetActive(!inCategoryList);

            // Reorder so category rows / back row appear at the top.
            int idx = 0;
            if (state.backRow?.row != null && state.backRow.row.gameObject.activeSelf)
                state.backRow.row.transform.SetSiblingIndex(idx++);
            foreach (var cr in state.categoryRows)
                if (cr.row != null && cr.row.gameObject.activeSelf)
                    cr.row.transform.SetSiblingIndex(idx++);
        }

        private static void DrillInto(CategoryListState state, string tag)
        {
            // "All" pseudo-tag uses sentinel so we can distinguish from category-list mode.
            state.drilledIntoTag = tag ?? "__all";
            ApplyView(state);
        }

        private static void DrillUp(CategoryListState state)
        {
            state.drilledIntoTag = null;
            ApplyView(state);
        }

        // --- label/icon resolution ---

        private static string ResolveLabel(string tag)
        {
            if (tag == null) return LEManager.Get("TedditCategories.All", "ALL");
            if (tag == OtherSentinel) return LEManager.Get("TedditCategories.Other", "OTHER");

            // User-defined category from facility_categories.yaml.
            var customLabel = CategoryDefinitions.TryGetLabel(tag);
            if (!string.IsNullOrEmpty(customLabel)) return customLabel.ToUpper();

            if (tag.StartsWith(TagRegistry.ResourceTagPrefix, StringComparison.Ordinal))
            {
                var id = tag.Substring(TagRegistry.ResourceTagPrefix.Length);
                var def = ResolveResource(id);
                if (def != null) return LEManager.Get(def.ID, id).ToUpper();
                return id.ToUpper();
            }
            return LEManager.Get("TedditCategories.Category." + tag, tag).ToUpper();
        }

        private static Sprite ResolveIcon(UIFacilityList list, string tag)
        {
            if (tag == null) return null;
            if (tag == OtherSentinel) return null;

            // User-defined "icon: <facility_id>" override.
            var iconFacilityId = CategoryDefinitions.TryGetIconFacilityId(tag);
            if (!string.IsNullOrEmpty(iconFacilityId))
            {
                foreach (var row in list.CreateRows)
                {
                    var desc = row?.facilityDescriptor;
                    if (desc != null && desc.ID == iconFacilityId && desc.Sprite != null) return desc.Sprite;
                }
            }

            if (tag.StartsWith(TagRegistry.ResourceTagPrefix, StringComparison.Ordinal))
            {
                var id = tag.Substring(TagRegistry.ResourceTagPrefix.Length);
                var def = ResolveResource(id);
                if (def != null && def.Sprite != null) return def.Sprite;
            }
            // Fallback: first facility carrying this tag.
            foreach (var row in list.CreateRows)
            {
                var desc = row?.facilityDescriptor;
                if (desc == null) continue;
                if (TagRegistry.GetTags(desc).Contains(tag) && desc.Sprite != null) return desc.Sprite;
            }
            return null;
        }

        private static ResourceDefinition ResolveResource(string id)
        {
            try
            {
                var mgr = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
                return mgr?.AllResourceDefinitions?.GetByID(id);
            }
            catch { return null; }
        }
    }
}
