using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace SupermarketTweaks
{
    // Put yesterday's sales back on today.
    //
    // Sales do not survive the night. RpcEndDay calls DailySaleReset, which empties both lists
    // outright:
    //
    //   productsIDOnSale.Clear();
    //   productsSaleDiscount.Clear();
    //   NetworkassembledSalesList = "";
    //
    // so every morning starts with zero slots filled - and an empty sale slot is a wasted roll in
    // ExtraProductsOnSaleToAdd, which is the only mechanism in the game that ADDS items to a
    // customer's shopping list. Re-applying is worth real money.
    //
    // What was on sale is captured in a prefix on DailySaleReset rather than polled, because by the
    // time anything else notices the day changed the lists are already gone.
    //
    // How many can come back is not up to this mod. ProductListing.allowedSimultaneousSales starts
    // at 1 and is raised only by upgrades 35, 36 and 37, +2 each, and both SalesDevice and
    // SetProductOnSale refuse anything past it. One remembered sale in an un-upgraded shop is the
    // correct answer, so the count is logged next to the cap to tell the two cases apart.
    public static class AutoSalesConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Remembered;
        internal static ConfigEntry<float> DelaySeconds;
        internal static ConfigEntry<bool> Log;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Sales", "AutoRestartSales", true,
                "Put the same products back on sale each morning, at the same discounts, instead of " +
                "the day starting with every sale slot empty.");
            Remembered = cfg.Bind("Sales", "RememberedSales", "",
                "What to restore, as id:discount pairs. Written automatically when the day ends, so " +
                "the sales survive quitting and reloading too.");
            DelaySeconds = cfg.Bind("Sales", "RestartDelaySeconds", 5f,
                new ConfigDescription("How long after the new day starts to re-apply. A little delay " +
                    "lets the game finish its own end-of-day clearing first.",
                    new AcceptableValueRange<float>(0f, 60f)));
            Log = cfg.Bind("Sales", "LogSales", true,
                "Log what was remembered and what was restored.");
        }

        internal static bool On => Enabled != null && Enabled.Value;
    }

    internal static class AutoSales
    {
        internal static string Status = "nothing remembered";

        internal static void Remember(List<int> ids, List<int> discounts)
        {
            if (ids == null || ids.Count == 0) return;

            var parts = new List<string>();
            for (int i = 0; i < ids.Count; i++)
            {
                int d = (discounts != null && i < discounts.Count) ? discounts[i] : 5;
                parts.Add(ids[i] + ":" + d);
            }

            AutoSalesConfig.Remembered.Value = string.Join(",", parts.ToArray());

            // The cap is reported alongside the count because it is the usual explanation for a
            // disappointing number here. allowedSimultaneousSales starts at 1 and only upgrades 35,
            // 36 and 37 raise it, +2 each - so an un-upgraded shop can hold exactly one sale, and
            // remembering one is the whole truth rather than a bug.
            int cap = ProductListing.Instance != null
                ? ProductListing.Instance.allowedSimultaneousSales : -1;

            Status = $"{ids.Count} of {cap} slot(s) remembered";

            if (AutoSalesConfig.Log.Value)
                Plugin.Log.LogInfo($"[Sales] Remembered for tomorrow: {AutoSalesConfig.Remembered.Value} " +
                                   $"({ids.Count} sale(s), shop allows {cap}).");
        }

        internal static IEnumerator RestoreRoutine()
        {
            yield return new WaitForSeconds(Mathf.Max(0f, AutoSalesConfig.DelaySeconds.Value));

            var listing = ProductListing.Instance;
            if (listing == null) yield break;

            string raw = AutoSalesConfig.Remembered.Value;
            if (string.IsNullOrEmpty(raw)) { Status = "nothing remembered"; yield break; }

            int cap = listing.allowedSimultaneousSales;
            int restored = 0, skipped = 0, refused = 0;

            foreach (var pair in raw.Split(','))
            {
                var kv = pair.Split(':');
                if (kv.Length != 2) continue;
                if (!int.TryParse(kv[0], out int id) || !int.TryParse(kv[1], out int discount)) continue;

                // A product can be unlearned between sessions; putting an unavailable one on sale
                // would burn a slot on something no customer can be offered.
                if (listing.availableProducts == null || !listing.availableProducts.Contains(id))
                {
                    skipped++;
                    continue;
                }

                // SetProductOnSale enforces allowedSimultaneousSales and rejects duplicates itself,
                // so restoring more than the current cap allows simply stops rather than breaking -
                // which matters if the upgrade that raised the cap has not been bought on this save.
                listing.SetProductOnSale(id, discount);

                // A frame for the command to land. SetProductOnSale does not report back - it fires
                // a Command and returns - so success is read from the list afterwards rather than
                // assumed. Counting the calls instead of the results was reporting sales that the
                // cap had silently refused.
                yield return null;

                if (listing.productsIDOnSale != null && listing.productsIDOnSale.Contains(id)) restored++;
                else refused++;
            }

            Status = $"restored {restored} of {cap} slot(s)"
                   + (skipped > 0 ? $", {skipped} unavailable" : "")
                   + (refused > 0 ? $", {refused} refused" : "");

            if (AutoSalesConfig.Log.Value && (restored > 0 || skipped > 0 || refused > 0))
                Plugin.Log.LogInfo($"[Sales] {Status}." + (refused > 0
                    ? " Refused ones did not fit; allowedSimultaneousSales is raised by upgrades 35, 36 and 37."
                    : ""));
        }
    }

    public class AutoSalesDriver : MonoBehaviour
    {
        internal static AutoSalesDriver Instance;

        private int _lastDay = int.MinValue;
        private float _next;

        private void Awake() { Instance = this; }

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            try
            {
                if (!AutoSalesConfig.On) return;

                // Host only: CmdSetProductOnSale is requiresAuthority false, so both machines would
                // otherwise race to fill the same limited slots.
                if (NetworkClient.active && !NetworkServer.active) return;

                var data = GameData.Instance;
                if (data == null || ProductListing.Instance == null) return;

                if (_lastDay == int.MinValue) { _lastDay = data.gameDay; return; }
                if (data.gameDay == _lastDay) return;

                _lastDay = data.gameDay;
                StartCoroutine(AutoSales.RestoreRoutine());
            }
            catch (Exception e) { Plugin.Log.LogError($"[Sales] {e.Message}"); }
        }

        internal void RestoreNow() => StartCoroutine(AutoSales.RestoreRoutine());
    }

    // Prefix, not postfix: this method is where the lists are emptied, so afterwards there is
    // nothing left to read.
    [HarmonyPatch(typeof(ProductListing), "DailySaleReset")]
    public static class Patch_ProductListing_DailySaleReset
    {
        private static void Prefix(ProductListing __instance)
        {
            try
            {
                if (!AutoSalesConfig.On) return;
                AutoSales.Remember(__instance.productsIDOnSale, __instance.productsSaleDiscount);
            }
            catch (Exception e) { Plugin.Log.LogError($"[Sales] remember: {e.Message}"); }
        }
    }
}
