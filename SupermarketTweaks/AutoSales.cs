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
    // Two ways to decide what comes back. The default repeats yesterday's list; AutoSaleTopPriced
    // instead fills every slot with the dearest products the shop can actually sell, on the grounds
    // that a discount is worth most on the item with the most margin to give away.
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
        internal static ConfigEntry<bool> TopPriced;
        internal static ConfigEntry<int> TopPricedDiscount;

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
            TopPriced = cfg.Bind("Sales", "AutoSaleTopPriced", false,
                "Ignore the remembered list and instead put the most expensive products on sale " +
                "each morning - as many as the shop has slots for. Filling every slot is what " +
                "matters: an empty one is a wasted roll in ExtraProductsOnSaleToAdd, the only thing " +
                "in the game that ADDS an item to a customer's shopping list.");
            TopPricedDiscount = cfg.Bind("Sales", "TopPricedDiscount", 5,
                new ConfigDescription("Discount to apply, as a percentage. The terminal only offers " +
                    "multiples of 5 between 5 and 45, and this is clamped and rounded to match.",
                    new AcceptableValueRange<int>(5, 45)));
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

            // In top-priced mode tomorrow's list is recomputed from prices, so writing today's
            // choices over the remembered one would quietly destroy the list the player curated -
            // and they would find it gone the moment they turned the mode back off.
            if (AutoSalesConfig.TopPriced != null && AutoSalesConfig.TopPriced.Value)
            {
                Status = $"{ids.Count} sale(s) ended; top priced mode picks tomorrow's";
                return;
            }

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

        // The most expensive products the shop can actually sell, dearest first.
        //
        // Filtered to products that have a shelf row assigned, because a sale on something no
        // customer can pick up is a slot spent for nothing - and slots are the scarce resource here,
        // not discounts.
        //
        // Price comes from productPlayerPricing, which is what you charge, so this tracks whatever
        // the auto-pricer last set rather than some fixed notion of value.
        private static List<int> TopPricedProducts(ProductListing listing, int count)
        {
            var picks = new List<int>();
            if (listing == null || listing.availableProducts == null || count <= 0) return picks;

            var onShelves = new HashSet<int>();
            var mgr = NPC_Manager.Instance;
            if (mgr != null && mgr.shelvesOBJ != null)
            {
                foreach (Transform shelf in mgr.shelvesOBJ.transform)
                {
                    var data = shelf.GetComponent<Data_Container>();
                    if (data == null || data.productInfoArray == null) continue;

                    for (int j = 0; j < data.productInfoArray.Length / 2; j++)
                    {
                        int id = data.productInfoArray[j * 2];
                        if (id >= 0) onShelves.Add(id);
                    }
                }
            }

            var candidates = new List<int>();
            foreach (int id in listing.availableProducts)
            {
                if (id < 0 || id >= listing.productPlayerPricing.Length) continue;
                if (onShelves.Count > 0 && !onShelves.Contains(id)) continue;
                candidates.Add(id);
            }

            candidates.Sort((a, b) => listing.productPlayerPricing[b]
                                      .CompareTo(listing.productPlayerPricing[a]));

            for (int i = 0; i < candidates.Count && picks.Count < count; i++)
                picks.Add(candidates[i]);

            return picks;
        }

        // The terminal moves the discount in steps of five between 5 and 45, so anything else would
        // be a value the player could never have set by hand.
        private static int SnapDiscount(int raw)
        {
            int snapped = Mathf.RoundToInt(raw / 5f) * 5;
            return Mathf.Clamp(snapped, 5, 45);
        }

        internal static IEnumerator RestoreRoutine()
        {
            yield return new WaitForSeconds(Mathf.Max(0f, AutoSalesConfig.DelaySeconds.Value));

            var listing = ProductListing.Instance;
            if (listing == null) yield break;

            int cap = listing.allowedSimultaneousSales;
            int restored = 0, skipped = 0, refused = 0;

            // Either mode ends up as the same id:discount list, so the loop below is shared. Only
            // where the list comes from differs.
            var plan = new List<KeyValuePair<int, int>>();

            if (AutoSalesConfig.TopPriced.Value)
            {
                int discount = SnapDiscount(AutoSalesConfig.TopPricedDiscount.Value);

                // Exactly as many as the shop can hold. Asking for more would just be refused by
                // SetProductOnSale, and asking for fewer wastes a slot.
                foreach (int id in TopPricedProducts(listing, cap))
                    plan.Add(new KeyValuePair<int, int>(id, discount));

                if (plan.Count == 0) { Status = "no sellable products to discount"; yield break; }
            }
            else
            {
                string raw = AutoSalesConfig.Remembered.Value;
                if (string.IsNullOrEmpty(raw)) { Status = "nothing remembered"; yield break; }

                foreach (var pair in raw.Split(','))
                {
                    var kv = pair.Split(':');
                    if (kv.Length != 2) continue;
                    if (!int.TryParse(kv[0], out int rid) || !int.TryParse(kv[1], out int rdiscount)) continue;
                    plan.Add(new KeyValuePair<int, int>(rid, rdiscount));
                }
            }

            foreach (var entry in plan)
            {
                int id = entry.Key, discount = entry.Value;

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

            Status = (AutoSalesConfig.TopPriced.Value ? "top priced: " : "restored ")
                   + $"{restored} of {cap} slot(s)"
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
