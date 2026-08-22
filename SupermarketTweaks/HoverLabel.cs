using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SupermarketTweaks
{
    // Everything this mod adds to the product name you see when looking at a shelf.
    //
    // One patch, not two, because there is only one label. PlayerNetwork.Update writes
    // "Container/ProductName" from either of two branches and two different tables:
    //
    //   Data_Container           GetLocalizationString("product" + id)
    //   ManufacturingContainer   GetLocalizationString("mfactureproduct" + id)
    //
    // Both branches store their id in the SAME oldCanvasProductID field, which is the trap that
    // renamed every manufactured product on the shelf when the research marker read the field
    // without asking which branch had written it. Two separate features writing the same label from
    // two patches would be a worse version of the same problem, so they share this one.
    //
    // What gets added depends on which kind of container is under the crosshair:
    //
    //   a normal shelf         "*" if the product has been researched and no locked recipe wants it
    //   a manufactured shelf   where that good stands - shelf space, storage, loose boxes
    //
    // The wanted string is rebuilt only when the hovered product changes, because this runs every
    // frame and the work behind it is not free.
    public static class HoverLabelConfig
    {
        internal static ConfigEntry<bool> ShowManufacturedStanding;

        public static void Init(ConfigFile cfg)
        {
            ShowManufacturedStanding = cfg.Bind("Manufacturing", "ShowStandingOnHover", true,
                "When looking at a manufactured product - on a display shelf or a storage rack - " +
                "add where that good stands: whether a store shelf is assigned to it at all, how " +
                "full that shelf is, and how much is behind it. Without this there is no way to " +
                "tell a box you can sell from one with nowhere to go.");
        }
    }

    [HarmonyPatch(typeof(PlayerNetwork), "Update")]
    public static class Patch_PlayerNetwork_Update
    {
        private static FieldInfo _panelField;
        private static FieldInfo _idField;
        private static FieldInfo _maskField;
        private static bool _looked;
        private static bool _warned;

        private static Component _label;
        private static PropertyInfo _labelText;
        private static GameObject _labelOwner;

        private static int _cachedId = int.MinValue;
        private static int _cachedVersion = -1;
        private static string _cachedCombinables;

        // null means "leave this label alone" - the safe answer, and the common one.
        private static string _cachedWant;

        private static void Postfix(PlayerNetwork __instance)
        {
            try
            {
                if (__instance == null) return;

                if (!_looked)
                {
                    _looked = true;
                    _panelField = AccessTools.Field(typeof(PlayerNetwork), "gameCanvasProductOBJ");
                    _idField    = AccessTools.Field(typeof(PlayerNetwork), "oldCanvasProductID");
                    _maskField  = AccessTools.Field(typeof(PlayerNetwork), "interactableMask");

                    if ((_panelField == null || _idField == null || _maskField == null) && !_warned)
                    {
                        _warned = true;
                        Plugin.Log.LogWarning("[Hover] PlayerNetwork's hover-panel fields moved; " +
                                              "nothing will be added to product names.");
                    }
                }
                if (_panelField == null || _idField == null || _maskField == null) return;

                var panel = _panelField.GetValue(__instance) as GameObject;
                if (panel == null || !panel.activeSelf) return;

                // Negative values are the game's own sentinels for "nothing" - -2, -4, -5, -6, -7
                // are all used - never product ids.
                int id = (int)_idField.GetValue(__instance);
                if (id < 0) return;

                if (_labelOwner != panel)
                {
                    _labelOwner = panel;
                    _label = null;
                    _labelText = null;

                    var found = panel.transform.Find("Container/ProductName");
                    if (found != null)
                    {
                        foreach (var c in found.GetComponents<Component>())
                        {
                            if (c == null || c.GetType().Name != "TextMeshProUGUI") continue;
                            _label = c;
                            _labelText = c.GetType().GetProperty("text");
                            break;
                        }
                    }
                }
                if (_label == null || _labelText == null) return;

                Rebuild(__instance, id);
                if (_cachedWant == null) return;

                if ((_labelText.GetValue(_label, null) as string) != _cachedWant)
                    _labelText.SetValue(_label, _cachedWant, null);
            }
            catch (Exception e) { Plugin.Log.LogError($"[Hover] {e.Message}"); }
        }

        private static void Rebuild(PlayerNetwork instance, int id)
        {
            // The row under the crosshair, which is what tells the two branches apart AND carries
            // the combination a manufactured row is labelled for. One raycast, and only when
            // something has changed - the same ray the game itself just cast, with its own mask and
            // range.
            var cam = Camera.main;
            if (cam == null) { _cachedWant = null; return; }

            RaycastHit hit;
            var mask = (LayerMask)_maskField.GetValue(instance);
            if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out hit, 4f, mask))
            {
                _cachedWant = null;
                return;
            }

            var owner = hit.transform.parent != null ? hit.transform.parent.parent : null;
            if (owner == null) { _cachedWant = null; return; }

            var mfg = owner.GetComponent<ManufacturingContainer>();
            if (mfg != null)
            {
                if (!HoverLabelConfig.ShowManufacturedStanding.Value) { _cachedWant = null; return; }

                int row = hit.transform.GetSiblingIndex();
                string combinables = (mfg.combinableInfoArray != null && row < mfg.combinableInfoArray.Length)
                    ? mfg.combinableInfoArray[row] : "";

                // The combination is part of the identity, so it has to be part of the cache key -
                // two rows of the same base product with different combinations are different goods
                // and stand in different places.
                if (id == _cachedId && combinables == _cachedCombinables) return;
                _cachedId = id;
                _cachedCombinables = combinables;

                _cachedWant = ManufacturedName(id) + StandingSuffix(id, combinables);
                return;
            }

            if (owner.GetComponent<Data_Container>() == null) { _cachedWant = null; return; }

            if (id == _cachedId && _cachedCombinables == null
                && ResearchTracker.Version == _cachedVersion) return;

            _cachedId = id;
            _cachedCombinables = null;
            _cachedVersion = ResearchTracker.Version;

            _cachedWant = ResearchTracker.NameOf(id)
                        + (ResearchTracker.IsExhausted(id) ? ResearchTrackerConfig.Marker.Value : "");
        }

        // Manufactured goods have their own localization table. Resolving one through "product"
        // gives an unrelated grocery, which is precisely how this went wrong the first time.
        internal static string ManufacturedName(int productID)
        {
            try
            {
                var lm = LocalizationManager.instance;
                return lm != null
                    ? lm.GetLocalizationString("mfactureproduct" + productID)
                    : productID.ToString();
            }
            catch { return productID.ToString(); }
        }

        // "  [no shelf - 50 stored]" or "  [shelf 12/40, 25 stored]".
        //
        // The no-shelf case is the one worth spotting: a manufactured good with no display row
        // assigned can never be put out, so the boxes behind it are money sitting still.
        internal static string StandingSuffix(int productID, string combinables)
        {
            var s = ManufactureOrder.Where(productID, combinables);

            string behind = s.InStorage + s.InBoxes > 0
                ? $", {s.InStorage + s.InBoxes} behind"
                : "";

            return s.HasShelf
                ? $"   [shelf {s.OnShelf}/{s.ShelfCapacity}{behind}]"
                : $"   [NO SHELF{behind}]";
        }
    }
}
