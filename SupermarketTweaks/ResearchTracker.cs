using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace SupermarketTweaks
{
    // Mark products that have been through the researcher and have nothing left to give.
    //
    // The game keeps no record of what you have scanned. SaveBehaviour persists exactly two
    // manufacturing things - unlockedBaseProducts (which RECIPES are open) and playerSavedRecipes -
    // and ManufacturingResearch.assignedProductID holds only the item currently in the machine,
    // cleared to -1 the moment a run ends. So the only feedback a ten-minute run gives you is a
    // "no recipes found" label that is gone by the time you walk back to the shelf.
    //
    // Two facts have to be true before a product is worth marking, and they are different
    // questions:
    //
    //   scanned         did we ever put this in the machine
    //   nothing left    does any STILL-LOCKED recipe contain this product id
    //
    // Both matter. A product with no recipes left that you have never scanned is not something you
    // know is useless, so marking it would be handing over information the game never gave you.
    // And a scanned product can still be worth scanning again - CheckIfRecipeExists unlocks the
    // FIRST locked recipe containing the id and returns, so a product appearing in eight recipes
    // pays out eight times. Marking on "scanned" alone would send you away from the best item in
    // the shop.
    public static class ResearchTrackerConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Scanned;
        internal static ConfigEntry<string> Marker;
        internal static ConfigEntry<bool> Log;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Manufacturing", "MarkExhaustedProducts", true,
                "Put a marker beside a product's name on the shelf once you have researched it AND " +
                "no locked recipe still contains it - so a second look tells you not to bother.");
            Scanned = cfg.Bind("Manufacturing", "ScannedProducts", "",
                "Product ids that have been through the researcher, comma separated. Written " +
                "automatically. On a client this is replaced by the host's copy.");
            Marker = cfg.Bind("Manufacturing", "ExhaustedMarker", " *",
                "What to append to the name. Leading space included on purpose.");
            Log = cfg.Bind("Manufacturing", "LogResearch", true,
                "Log each product as it goes into the researcher.");
        }

        internal static bool On => Enabled != null && Enabled.Value;
    }

    internal static class ResearchTracker
    {
        internal static string Status = "nothing scanned";

        private static HashSet<int> _scanned;

        private static HashSet<int> Scanned
        {
            get
            {
                if (_scanned != null) return _scanned;

                _scanned = new HashSet<int>();
                var raw = ResearchTrackerConfig.Scanned != null
                    ? ResearchTrackerConfig.Scanned.Value : null;
                if (string.IsNullOrEmpty(raw)) return _scanned;

                foreach (var part in raw.Split(','))
                {
                    int id;
                    if (int.TryParse(part.Trim(), out id) && id >= 0) _scanned.Add(id);
                }
                return _scanned;
            }
        }

        private static void Save()
        {
            var ids = new List<int>(Scanned);
            ids.Sort();
            ResearchTrackerConfig.Scanned.Value = string.Join(",", ids.ConvertAll(i => i.ToString()).ToArray());
            Status = $"{ids.Count} product(s) scanned";
        }

        internal static string Serialize()
        {
            var ids = new List<int>(Scanned);
            ids.Sort();
            return string.Join(",", ids.ConvertAll(i => i.ToString()).ToArray());
        }

        // The host's copy replaces ours rather than merging into it.
        //
        // The host is the only machine that has seen every session of this save, so its list is the
        // record and ours is at best a subset. Merging would also make a stale local list immortal:
        // nothing would ever remove an id that the host does not have, and a wrong marker is worse
        // than a missing one because it tells you to skip a product that still pays.
        internal static void ApplyFromHost(string payload)
        {
            var incoming = new HashSet<int>();
            if (!string.IsNullOrEmpty(payload))
            {
                foreach (var part in payload.Split(','))
                {
                    int id;
                    if (int.TryParse(part.Trim(), out id) && id >= 0) incoming.Add(id);
                }
            }

            if (_scanned != null && incoming.Count == _scanned.Count && incoming.SetEquals(_scanned)) return;

            _scanned = incoming;
            Save();
            Plugin.Log.LogInfo($"[Research] Host's scanned list applied ({incoming.Count} product(s)).");
        }

        internal static void Record(int productID)
        {
            if (productID < 0 || !Scanned.Add(productID)) return;

            Save();

            if (ResearchTrackerConfig.Log.Value)
                Plugin.Log.LogInfo($"[Research] {NameOf(productID)} (id {productID}) scanned; " +
                                   $"{RecipesLeftFor(productID)} locked recipe(s) still contain it.");

            // Every machine watches the same Rpc, so both sides record this independently. The
            // broadcast is for the case that is not covered: a client who was not connected when it
            // happened, or who joined this save late.
            NetSync.BroadcastScanned();
        }

        // How many still-locked recipes contain this product.
        //
        // A read-only re-implementation of ManufacturingBase.CheckIfRecipeExists, and it has to be:
        // the real one UNLOCKS the first recipe it matches and fires an Rpc. Calling it to ask a
        // question would answer it by changing it.
        //
        // Both members it needs are public - unlockedBaseProducts and RetrieveBaseRecipe(i) - so no
        // reflection is involved. Recipe format is slot|slot|slot, each slot a '-' separated list
        // of acceptable product ids.
        internal static int RecipesLeftFor(int productID)
        {
            var mb = ManufacturingBase.Instance;
            if (mb == null || mb.unlockedBaseProducts == null) return 0;

            int found = 0;
            for (int i = 0; i < mb.unlockedBaseProducts.Length; i++)
            {
                if (mb.unlockedBaseProducts[i]) continue;

                string recipe;
                try { recipe = mb.RetrieveBaseRecipe(i); }
                catch { continue; }
                if (string.IsNullOrEmpty(recipe)) continue;

                foreach (var slot in recipe.Split('|'))
                {
                    bool matched = false;
                    foreach (var idText in slot.Split('-'))
                    {
                        int id;
                        if (!int.TryParse(idText, out id) || id != productID) continue;
                        matched = true;
                        break;
                    }
                    if (!matched) continue;
                    found++;
                    break;                              // one recipe counts once
                }
            }
            return found;
        }

        internal static bool IsExhausted(int productID)
            => Scanned.Contains(productID) && RecipesLeftFor(productID) == 0;

        internal static string NameOf(int productID)
        {
            try
            {
                var lm = LocalizationManager.instance;
                return lm != null ? lm.GetLocalizationString("product" + productID) : productID.ToString();
            }
            catch { return productID.ToString(); }
        }
    }

    // Recorded here rather than at completion, and on purpose.
    //
    // AssignProductForResearching runs on every machine - it is what UserCode_RpcAssignProduct-
    // ForResearching calls - whereas the completion path (ServerResearchThread) is server only. It
    // also fires the moment the item goes in, so a run abandoned by quitting still counts as
    // scanned, which matches what a player would remember doing.
    [HarmonyPatch(typeof(ManufacturingResearch), "AssignProductForResearching")]
    public static class Patch_ManufacturingResearch_AssignProductForResearching
    {
        private static void Postfix(int __0)
        {
            try
            {
                if (!ResearchTrackerConfig.On) return;
                ResearchTracker.Record(__0);
            }
            catch (Exception e) { Plugin.Log.LogError($"[Research] record: {e.Message}"); }
        }
    }

    // The hover panel is written inline in PlayerNetwork.Update:
    //
    //   gameCanvasProductOBJ = GameCanvas.Instance.transform.Find("ProductShow").gameObject;
    //   ...
    //   gameCanvasProductOBJ.transform.Find("Container/ProductName").GetComponent<TextMeshProUGUI>()
    //       .text = LocalizationManager.instance.GetLocalizationString("product" + num);
    //
    // and only when the id under the crosshair CHANGES - oldCanvasProductID guards it. That guard is
    // why this is a postfix on Update rather than a one-shot: the game will not rewrite the label
    // while you keep looking at the same product, so appending once inside the change would work,
    // but any other code touching the label would silently undo us with no second chance.
    //
    // Instead the desired string is computed each frame and written only when it differs. That is
    // idempotent, so it cannot stack up markers the way a blind append would, and it repairs itself
    // if anything else rewrites the label.
    [HarmonyPatch(typeof(PlayerNetwork), "Update")]
    public static class Patch_PlayerNetwork_Update
    {
        private static FieldInfo _panelField;
        private static FieldInfo _idField;
        private static bool _looked;
        private static bool _warned;

        // Reached by reflection, exactly as the order button's label is, so the mod still needs no
        // TextMeshPro reference (and no matching stripped copy in refs/Managed for CI) just to read
        // and write one string.
        private static Component _label;
        private static PropertyInfo _labelText;
        private static GameObject _labelOwner;

        private static void Postfix(PlayerNetwork __instance)
        {
            try
            {
                if (!ResearchTrackerConfig.On || __instance == null) return;

                if (!_looked)
                {
                    _looked = true;
                    _panelField = AccessTools.Field(typeof(PlayerNetwork), "gameCanvasProductOBJ");
                    _idField    = AccessTools.Field(typeof(PlayerNetwork), "oldCanvasProductID");

                    if ((_panelField == null || _idField == null) && !_warned)
                    {
                        _warned = true;
                        Plugin.Log.LogWarning("[Research] PlayerNetwork's hover-panel fields moved; " +
                                              "products will not be marked on the shelf.");
                    }
                }
                if (_panelField == null || _idField == null) return;

                var panel = _panelField.GetValue(__instance) as GameObject;
                if (panel == null || !panel.activeSelf) return;

                // Negative values are the game's own sentinels for "nothing" (-2 here, -7 for the
                // manufacturing shelf), not product ids.
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

                string want = ResearchTracker.NameOf(id);
                if (ResearchTracker.IsExhausted(id)) want += ResearchTrackerConfig.Marker.Value;

                if ((_labelText.GetValue(_label, null) as string) != want)
                    _labelText.SetValue(_label, want, null);
            }
            catch (Exception e) { Plugin.Log.LogError($"[Research] label: {e.Message}"); }
        }
    }
}
