using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SupermarketTweaks
{
    // Stop storage staff hauling away boxes of products that have nowhere to go.
    //
    // A box whose product has no shelf row assigned to it can never be restocked: MainRestockUpdate
    // only ever builds tasks from rows that already name a product,
    //
    //   int num2 = productInfoArray[j * 2];
    //   if (num2 < 0) continue;
    //
    // so moving that box into storage just fills the back room with stock the shop cannot sell,
    // while the worker who moved it was not restocking something that could be.
    //
    // Leaving the box on the floor is deliberate: it stays visible, which is the signal that you
    // have stock with no home. Silently filing it away is what hides the problem.
    public static class ShelflessBoxConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Log;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Staff", "IgnoreShelflessBoxes", true,
                "Storage staff leave boxes alone when no shelf row is assigned to that product. " +
                "Such a box can never be restocked, so storing it only clogs the back room - and " +
                "left on the floor it shows you which product still needs a shelf.");
            Log = cfg.Bind("Staff", "LogShelflessBoxes", true,
                "Log which products are being skipped for having no shelf.");
        }

        internal static bool On => Enabled != null && Enabled.Value;
    }

    internal static class ShelflessBoxes
    {
        internal static string Status = "";

        // Recomputed rather than cached per-frame, but only once per call into the filter, since
        // shelf assignments change whenever the player relabels a row.
        private static readonly HashSet<int> _shelved = new HashSet<int>();
        private static float _builtAt = -1f;
        private const float CacheSeconds = 2f;

        internal static bool HasShelfSpot(NPC_Manager mgr, int productID)
        {
            if (productID < 0) return true;      // unknown: never our business to block
            Rebuild(mgr);
            return _shelved.Contains(productID);
        }

        private static void Rebuild(NPC_Manager mgr)
        {
            if (Time.unscaledTime - _builtAt < CacheSeconds) return;
            _builtAt = Time.unscaledTime;
            _shelved.Clear();

            try
            {
                var shelves = mgr.shelvesOBJ;
                if (shelves == null) return;

                foreach (Transform shelf in shelves.transform)
                {
                    var data = shelf.GetComponent<Data_Container>();
                    if (data == null || data.productInfoArray == null) continue;

                    // productInfoArray is (productId, count) pairs; -1 means the row is unassigned.
                    // Only the id matters here - a row labelled but empty is still a home.
                    for (int j = 0; j < data.productInfoArray.Length / 2; j++)
                    {
                        int id = data.productInfoArray[j * 2];
                        if (id >= 0) _shelved.Add(id);
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[Shelfless] {e.Message}"); }
        }

        private static readonly HashSet<int> _reported = new HashSet<int>();

        internal static void NoteSkipped(int productID, int count)
        {
            Status = $"{count} box(es) skipped - no shelf";
            if (!ShelflessBoxConfig.Log.Value) return;

            // Once per product, not once per attempt: the filter runs every time a worker looks for
            // a box, which is constantly.
            if (_reported.Add(productID))
                Plugin.Log.LogInfo($"[Shelfless] Leaving product {productID} on the floor - " +
                                   "no shelf row is assigned to it.");
        }

        internal static void Reset() => _reported.Clear();

        // Shared by both choosers.
        internal static void FilterChoice(NPC_Manager mgr, ref GameObject chosen)
        {
            try
            {
                if (!ShelflessBoxConfig.On || chosen == null) return;

                var data = chosen.GetComponent<BoxData>();
                if (data == null) return;
                if (HasShelfSpot(mgr, data.productID)) return;

                // Refusing this one box rather than hunting for a substitute: the game's chooser
                // already applied a per-worker priority index and a navmesh reachability test, and
                // reproducing that to pick a replacement would duplicate logic that can drift out
                // of step. Returning null makes the worker idle a tick and ask again - exactly what
                // already happens when the floor is empty.
                NoteSkipped(data.productID, 1);
                chosen = null;
            }
            catch (Exception e) { Plugin.Log.LogError($"[Shelfless] pick: {e.Message}"); }
        }
    }

    // There are TWO box choosers, not one - the storage branch picks between them depending on
    // whether the box is allowed in storage - so both need the filter or the rule leaks.
    [HarmonyPatch(typeof(NPC_Manager), "GetRandomGroundBox")]
    public static class Patch_NPC_Manager_GetRandomGroundBox
    {
        private static void Postfix(NPC_Manager __instance, ref GameObject __result)
            => ShelflessBoxes.FilterChoice(__instance, ref __result);
    }

    [HarmonyPatch(typeof(NPC_Manager), "GetRandomGroundBoxAllowedInStorage")]
    public static class Patch_NPC_Manager_GetRandomGroundBoxAllowedInStorage
    {
        private static void Postfix(NPC_Manager __instance, ref GameObject __result)
            => ShelflessBoxes.FilterChoice(__instance, ref __result);
    }

}
