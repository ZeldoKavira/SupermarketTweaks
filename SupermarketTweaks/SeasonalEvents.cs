using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SupermarketTweaks
{
    // Let the alien night run without owning the antenna.
    //
    // Of the seasonal events, this is the only one written in C#. SeasonalAlienEvent.Update gates
    // everything behind one ownership check:
    //
    //   if (!CheckIfAlienAntennaExists()) return;
    //   if (isSupermarketOpen && timeOfDay > 18f && currentState == 0) { ...StartCoroutine(CreateAliens())... }
    //
    // and the check is simply "is a decoration with decorationID 237 standing in the shop":
    //
    //   foreach (Transform item in nSpawner.levelPropsOBJ.transform.GetChild(7))
    //       if (item.GetComponent<BuildableInfo>().decorationID == 237) return true;
    //
    // A postfix that forces it true is the whole feature. The rest of the trigger is left alone -
    // the shop still has to be open and it still has to be past 18:00 - so this changes what you
    // need to own, not how the event behaves.
    //
    // Aliens are also the better token source: a ghost drops exactly one token of a fixed type,
    // while Seasonal_Alien.HitsCheck drops alienID + 1 of them, each rolled from the whole set.
    //
    // The other events are out of reach from here, and it is worth writing down why rather than
    // rediscovering it: SeasonalBehaviour, which owns the Halloween ghosts, has fields and not one
    // single method - it is driven entirely by PlayMaker. SeasonalChristmas and SeasonalCageEvent
    // are empty shells for the same reason. EasterChecker is not on a timer at all; it watches chat
    // through StringChecker(string message) and spawns eggs when someone types the right word.
    public static class SeasonalEventsConfig
    {
        internal static ConfigEntry<bool> ForceAlienEvent;
        internal static ConfigEntry<bool> Log;

        public static void Init(ConfigFile cfg)
        {
            ForceAlienEvent = cfg.Bind("Seasonal", "ForceAlienEvent", false,
                "Run the alien night without owning the alien antenna. The rest of the trigger is " +
                "unchanged: the shop still has to be open and it still has to be past 18:00. " +
                "Aliens drop several gachapon tokens each, so this is the fast way to farm them.");
            Log = cfg.Bind("Seasonal", "LogSeasonal", true,
                "Log when the antenna check is overridden.");
        }

        internal static bool On => ForceAlienEvent != null && ForceAlienEvent.Value;
    }

    internal static class SeasonalEvents
    {
        internal static string Status = "off";

        private const int AlienAntennaDecorationID = 237;

        private static bool _reported;
        private static bool _refused;

        // Never bypass a paid prop.
        //
        // BuildableInfo.isCool is the flag the builder reads to stamp a DLC badge on a decoration
        // (Builder_Main: "if (activateDLCSigns && component3.isCool)"). If the antenna turns out to
        // carry it, forcing the event would be handing over content that was not bought, so the
        // override refuses and says so. On everything else this is an ordinary cheat, no different
        // in kind from the speed boost.
        //
        // Checked lazily and cached: the prop array is not populated until the level has loaded.
        internal static bool Allowed()
        {
            if (_refused) return false;
            if (_reported) return true;

            var data = GameData.Instance;
            var spawner = data != null ? data.GetComponent<NetworkSpawner>() : null;
            var props = spawner != null ? spawner.decorationProps : null;
            if (props == null || props.Length == 0) return false;   // not loaded yet; try again later

            foreach (var prop in props)
            {
                if (prop == null) continue;
                var info = prop.GetComponent<BuildableInfo>();
                if (info == null || info.decorationID != AlienAntennaDecorationID) continue;

                if (info.isCool)
                {
                    _refused = true;
                    Status = "refused - the antenna is DLC content";
                    Plugin.Log.LogWarning("[Seasonal] The alien antenna is flagged as DLC content " +
                                          "(BuildableInfo.isCool), so ForceAlienEvent will not " +
                                          "override its check. Buy the antenna in game instead.");
                    return false;
                }

                _reported = true;
                if (SeasonalEventsConfig.Log.Value)
                    Plugin.Log.LogInfo($"[Seasonal] Alien antenna (decoration {AlienAntennaDecorationID}) " +
                                       $"costs ${info.cost} and is not DLC; the check can be overridden.");
                return true;
            }

            // Not in the decoration list at all. Say so once rather than silently doing nothing.
            if (!_refused)
            {
                _refused = true;
                Status = "refused - antenna not found in the decoration list";
                Plugin.Log.LogWarning($"[Seasonal] No decoration with ID {AlienAntennaDecorationID} " +
                                      "in this build; ForceAlienEvent will do nothing.");
            }
            return false;
        }
    }

    // Postfix rather than prefix: letting the game's own check run first means an antenna that IS
    // present still reports true through the normal path, and the override only ever turns a false
    // into a true.
    [HarmonyPatch(typeof(SeasonalAlienEvent), "CheckIfAlienAntennaExists")]
    public static class Patch_SeasonalAlienEvent_CheckIfAlienAntennaExists
    {
        private static void Postfix(ref bool __result)
        {
            try
            {
                if (__result)
                {
                    SeasonalEvents.Status = "antenna owned";
                    return;
                }
                if (!SeasonalEventsConfig.On) { SeasonalEvents.Status = "off"; return; }
                if (!SeasonalEvents.Allowed()) return;

                __result = true;
                SeasonalEvents.Status = "forced on";
            }
            catch (Exception e) { Plugin.Log.LogError($"[Seasonal] {e.Message}"); }
        }
    }
}
