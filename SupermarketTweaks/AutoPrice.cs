using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace SupermarketTweaks
{
    // Keep every product priced at a fixed multiple of its market price.
    //
    // Customers decide at the shelf, in NPC_Manager:
    //
    //   float yours     = productPlayerPricing[id];
    //   float threshold = basePricePerUnit * tierInflation[tier] * Random.Range(2.01f, 2.5f);
    //   if (yours > threshold) -> refuses, "too expensive"
    //
    // and the market price the game shows you is exactly basePricePerUnit * tierInflation[tier].
    // So 200% is the highest multiple EVERY customer still accepts; the 2.01 floor of that random
    // range is why the default stops there rather than at 250%.
    //
    // Repricing matters because tierInflation only ever rises - ServerCalculateNewInflation adds to
    // it every 7 days - so a price set once silently becomes a discount. That is what makes the
    // new-day trigger the important one.
    public static class AutoPriceConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> Percent;
        internal static ConfigEntry<bool> RoundDown;
        internal static ConfigEntry<bool> OnNewDay;
        internal static ConfigEntry<bool> OnNewProduct;
        internal static ConfigEntry<bool> OnlyUnpriced;
        internal static ConfigEntry<float> SecondsBetween;
        internal static ConfigEntry<bool> Log;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Pricing", "AutoPrice", true,
                "Automatically set product prices to a multiple of their market price.");
            Percent = cfg.Bind("Pricing", "PricePercent", 200,
                new ConfigDescription("Percentage of market price to charge. Every customer accepts " +
                    "up to 201%; above 250% nobody buys, and in between it is a per-customer roll.",
                    new AcceptableValueRange<int>(50, 250)));
            RoundDown = cfg.Bind("Pricing", "RoundToTenCents", false,
                "Round prices down to 10c instead of 1c, matching the in-game pricing machine's " +
                "round-down option.");
            OnNewDay = cfg.Bind("Pricing", "RepriceOnNewDay", true,
                "Reprice everything when a new day starts. This is the one that keeps up with inflation.");
            OnNewProduct = cfg.Bind("Pricing", "PriceNewProducts", true,
                "Price a product as soon as it becomes available, so newly unlocked stock is never " +
                "left sitting at zero.");
            OnlyUnpriced = cfg.Bind("Pricing", "OnlyPriceUnpriced", false,
                "Only ever set prices that are currently zero, leaving anything you priced by hand " +
                "alone. Turning this on largely defeats the inflation tracking.");
            SecondsBetween = cfg.Bind("Pricing", "SecondsBetweenUpdates", 0.05f,
                new ConfigDescription("Delay between individual price updates. Each one is a network " +
                    "command, so a full sweep is spread out rather than sent in one burst.",
                    new AcceptableValueRange<float>(0f, 1f)));
            Log = cfg.Bind("Pricing", "LogRepricing", true,
                "Log a summary each time prices are swept.");
        }

        internal static bool On => Enabled != null && Enabled.Value;

        // Automatic sweeps run on the host only.
        //
        // CmdUpdateProductPrice has requiresAuthority: false, so a client CAN price - which is
        // exactly the problem. Two machines sweeping the same product list on the same triggers
        // send two commands per product, and if their settings differ at all they fight, each
        // overwriting the other on every new day. One authority removes the whole class of bug,
        // and the host is the natural one: it owns the day rollover that drives repricing.
        //
        // NetworkServer.active covers host and singleplayer alike; a pure client is the only case
        // this excludes, and NetSync gives it the host's settings so its panel still reads true.
        internal static bool IsAuthority => !NetworkClient.active || NetworkServer.active;
    }

    internal static class AutoPrice
    {
        internal static string LastResult = "not run yet";

        // Mirrors PricingMachineAutomatic.SetPricingRoutine's arithmetic, including which way it
        // rounds, so a sweep produces exactly what the in-game machine would.
        internal static float TargetPrice(ProductListing listing, int productID)
        {
            float inflation = listing.tierInflation[listing.productsData[productID].productTier];
            float price = listing.productsData[productID].basePricePerUnit * inflation
                          * (AutoPriceConfig.Percent.Value / 100f);

            return AutoPriceConfig.RoundDown.Value
                ? Mathf.Floor(price * 10f) / 10f
                : Mathf.Floor(price * 100f) / 100f;
        }

        internal static IEnumerator SweepRoutine(string reason, List<int> only = null)
        {
            var listing = ProductListing.Instance;
            if (listing == null || listing.availableProducts == null) yield break;

            float delay = Mathf.Max(0f, AutoPriceConfig.SecondsBetween.Value);
            int changed = 0, skipped = 0;

            // Snapshotted first: the sweep yields between items, and availableProducts can be
            // rebuilt underneath us by updateProductList while we are walking it.
            var snapshot = new List<int>(only ?? listing.availableProducts);

            foreach (int id in snapshot)
            {
                if (!AutoPriceConfig.On) break;
                if (id < 0 || id >= listing.productPlayerPricing.Length) continue;

                float current = listing.productPlayerPricing[id];
                if (AutoPriceConfig.OnlyUnpriced.Value && current > 0f) { skipped++; continue; }

                float target = TargetPrice(listing, id);
                if (Mathf.Approximately(current, target)) { skipped++; continue; }

                // requiresAuthority is false on this command, so a client may send it too - the
                // server applies it and re-broadcasts the result.
                listing.CmdUpdateProductPrice(id, target);
                changed++;

                if (delay > 0f) yield return new WaitForSeconds(delay);
            }

            LastResult = $"{reason}: {changed} repriced, {skipped} already correct";
            if (AutoPriceConfig.Log.Value && changed > 0)
                Plugin.Log.LogInfo($"[AutoPrice] {LastResult} (at {AutoPriceConfig.Percent.Value}% of market)");
        }
    }

    public class AutoPriceDriver : MonoBehaviour
    {
        internal static AutoPriceDriver Instance;

        private int _lastDay = int.MinValue;
        private float _next;
        private readonly List<int> _pending = new List<int>();

        private void Awake() { Instance = this; }

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            try
            {
                if (!AutoPriceConfig.On) return;

                // Clients watch the day roll over too, but the host does the repricing.
                if (!AutoPriceConfig.IsAuthority)
                {
                    var d = GameData.Instance;
                    if (d != null) _lastDay = d.gameDay;      // stay in step, do nothing
                    _pending.Clear();
                    return;
                }

                var data = GameData.Instance;
                if (data == null || ProductListing.Instance == null) return;

                // Watching the day counter rather than patching the end-of-day routine: gameDay is
                // a SyncVar, so this fires on host and client alike and cannot miss a rollover that
                // happens through a path we did not think to hook.
                if (_lastDay == int.MinValue)
                {
                    _lastDay = data.gameDay;                 // first sight is not a new day
                }
                else if (data.gameDay != _lastDay)
                {
                    _lastDay = data.gameDay;
                    if (AutoPriceConfig.OnNewDay.Value)
                        StartCoroutine(AutoPrice.SweepRoutine($"day {data.gameDay}"));
                }

                if (_pending.Count > 0)
                {
                    var batch = new List<int>(_pending);
                    _pending.Clear();
                    StartCoroutine(AutoPrice.SweepRoutine("new product", batch));
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoPrice] {e.Message}"); }
        }

        // Queued rather than priced on the spot: updateProductList runs inside unlock handling, and
        // a coroutine that yields while the game is still rebuilding that list would sweep a
        // half-built collection.
        internal void QueueNewProducts(List<int> ids)
        {
            foreach (int id in ids) if (!_pending.Contains(id)) _pending.Add(id);
        }

        internal void SweepNow(string reason)
        {
            StartCoroutine(AutoPrice.SweepRoutine(reason));
        }
    }

    // updateProductList is the only place availableProducts is rebuilt, so it is where a newly
    // unlocked tier first appears - and those products arrive priced at zero.
    [HarmonyPatch(typeof(ProductListing), "updateProductList")]
    public static class Patch_ProductListing_updateProductList
    {
        private static void Prefix(ProductListing __instance, out List<int> __state)
        {
            __state = __instance.availableProducts != null
                ? new List<int>(__instance.availableProducts)
                : new List<int>();
        }

        private static void Postfix(ProductListing __instance, List<int> __state)
        {
            try
            {
                if (!AutoPriceConfig.On || !AutoPriceConfig.OnNewProduct.Value) return;
                if (!AutoPriceConfig.IsAuthority) return;
                if (AutoPriceDriver.Instance == null || __instance.availableProducts == null) return;

                var fresh = new List<int>();
                foreach (int id in __instance.availableProducts)
                    if (!__state.Contains(id)) fresh.Add(id);

                if (fresh.Count > 0) AutoPriceDriver.Instance.QueueNewProducts(fresh);
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoPrice] unlock hook: {e.Message}"); }
        }
    }
}
