using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace SupermarketTweaks
{
    // Two fixes for the same problem: fast-forwarding makes theft unmanageable.
    //
    // The game has no health and no damage. The only thing a hit does to a thief is knock stolen
    // goods loose, in NPC_Info.AuxiliarAnimationPlay:
    //
    //   int value = (int)Mathf.Floor((float)thiefProductsNumber / 4f);
    //   value = Mathf.Clamp(value, 1, 5);
    //   for (int i = 0; i < value + 1 && i < productsIDCarrying.Count; i++)
    //       // spawn a dropped product at 80% value, remove it from the thief
    //
    // Everything in the chase runs on scaled time, so at 3x the guard hits three times as often per
    // real second - but so does the thief run, and more importantly YOU only get a third of the
    // real time to notice and react. These two features give that time back.
    public static class AntiTheftConfig
    {
        internal static ConfigEntry<bool> ScaleDrops;
        internal static ConfigEntry<bool> SlowOnAlarm;
        internal static ConfigEntry<bool> Log;

        public static void Init(ConfigFile cfg)
        {
            ScaleDrops = cfg.Bind("Theft", "ScaleDropsWithGameSpeed", true,
                "Knock proportionally more goods loose per hit when the game is sped up, so a theft " +
                "takes about the same amount of REAL time to break up however fast you are running.");
            SlowOnAlarm = cfg.Bind("Theft", "NormalSpeedOnAlarm", true,
                "Drop back to normal speed while the anti-theft door alarm is going, and stay there " +
                "until the thief is caught, empty-handed or gone. Needs the anti-theft door.");
            Log = cfg.Bind("Theft", "LogTheft", true,
                "Log alarms and extra drops.");
        }

        internal static bool DropsOn  => ScaleDrops  != null && ScaleDrops.Value;
        internal static bool SlowOn   => SlowOnAlarm != null && SlowOnAlarm.Value;
    }

    internal static class AntiTheft
    {
        internal static string Status = "quiet";

        // Thieves the door has actually alarmed on. Tracked rather than scanning every NPC, because
        // the point is specifically "the door you paid for went off".
        private static readonly List<NPC_Info> _alarmed = new List<NPC_Info>();

        internal static void NoteAlarm(NPC_Info thief)
        {
            if (thief == null || _alarmed.Contains(thief)) return;
            _alarmed.Add(thief);

            if (AntiTheftConfig.Log.Value)
                Plugin.Log.LogInfo($"[Theft] Alarm: thief carrying {thief.productsIDCarrying?.Count ?? 0} item(s).");
        }

        // True while any alarmed thief is still around AND still holding something. Both endings
        // the request asked for fall out of this: stripped bare, or despawned.
        internal static bool AlarmActive
        {
            get
            {
                try
                {
                    for (int i = _alarmed.Count - 1; i >= 0; i--)
                    {
                        var t = _alarmed[i];
                        // A despawned NPC compares equal to null through Unity's operator.
                        if (t == null || t.productsIDCarrying == null || t.productsIDCarrying.Count == 0)
                        {
                            _alarmed.RemoveAt(i);
                            continue;
                        }
                    }

                    bool active = _alarmed.Count > 0;
                    Status = active ? $"ALARM - {_alarmed.Count} thief/thieves with goods" : "quiet";
                    return active;
                }
                catch { return false; }
            }
        }

        internal static void Clear() => _alarmed.Clear();

        // ---- client side ----
        //
        // A client is told the alarm state by the host rather than working it out, and the flag
        // expires on its own. Without that expiry a single dropped "off" would leave someone stuck
        // at 1x for the rest of the session with no way to tell why.
        private static bool _remoteAlarm;
        private static float _remoteAlarmUntil;
        private const float RemoteAlarmTimeout = 90f;

        internal static void SetRemoteAlarm(bool active)
        {
            _remoteAlarm = active;
            _remoteAlarmUntil = active ? Time.unscaledTime + RemoteAlarmTimeout : 0f;

            if (AntiTheftConfig.Log.Value)
                Plugin.Log.LogInfo($"[Theft] Host says alarm {(active ? "ON" : "off")}.");
        }

        internal static bool RemoteAlarmActive
        {
            get
            {
                if (!_remoteAlarm) return false;
                if (Time.unscaledTime > _remoteAlarmUntil)
                {
                    _remoteAlarm = false;
                    Plugin.Log.LogWarning("[Theft] Alarm state expired without an all-clear; " +
                                          "releasing the speed hold.");
                    return false;
                }
                return true;
            }
        }

        // The authority differs by side, so ask the right one.
        internal static bool AnyAlarm
        {
            get
            {
                bool isClient = NetworkClient.active && !NetworkServer.active;
                if (isClient) { Status = RemoteAlarmActive ? "ALARM (host)" : "quiet"; return RemoteAlarmActive; }
                return AlarmActive;
            }
        }
    }

    // The door decides a thief is fair game here, and sounds the alarm in the same breath.
    [HarmonyPatch(typeof(AntiTheftBehaviour), "CheckThief")]
    public static class Patch_AntiTheftBehaviour_CheckThief
    {
        private static void Postfix(GameObject __0)
        {
            try
            {
                if (!AntiTheftConfig.SlowOn || __0 == null) return;

                // Same shape the method itself checks: the HitTrigger's parent holds the NPC.
                var parent = __0.transform != null ? __0.transform.parent : null;
                var info = parent != null ? parent.GetComponent<NPC_Info>() : null;
                if (info == null) return;

                // thiefCanBeHitBySecurity is what CheckThief sets when it alarms, so this is how we
                // tell "the door fired on this one" from "some thief walked past".
                if (info.isAThief && info.thiefCanBeHitBySecurity) AntiTheft.NoteAlarm(info);
            }
            catch (Exception e) { Plugin.Log.LogError($"[Theft] alarm hook: {e.Message}"); }
        }
    }

    // Scale the goods knocked loose by the game speed.
    //
    // Rather than reimplementing the drop loop - which spawns networked objects and mutates two
    // parallel lists that must stay in step - this simply runs the game's own method again. Each
    // extra pass drops another batch, using the game's own arithmetic, so nothing here can get the
    // spawn, the 80% price or the list bookkeeping subtly wrong.
    [HarmonyPatch(typeof(NPC_Info), "AuxiliarAnimationPlay")]
    public static class Patch_NPC_Info_AuxiliarAnimationPlay
    {
        private static bool _reentering;

        private static void Postfix(NPC_Info __instance, int __0)
        {
            try
            {
                if (_reentering) return;                       // our own extra passes
                if (!AntiTheftConfig.DropsOn) return;
                if (__instance == null || !__instance.isAThief) return;
                if (__instance.productsIDCarrying == null || __instance.productsIDCarrying.Count == 0) return;

                // Server owns the dropped-product spawns.
                if (NetworkClient.active && !NetworkServer.active) return;

                float speed = GameSpeedConfig.Target;
                int extra = Mathf.RoundToInt(speed) - 1;
                if (extra <= 0) return;

                _reentering = true;
                try
                {
                    for (int i = 0; i < extra; i++)
                    {
                        if (__instance == null || __instance.productsIDCarrying == null
                            || __instance.productsIDCarrying.Count == 0) break;
                        __instance.AuxiliarAnimationPlay(__0);
                    }
                }
                finally { _reentering = false; }

                if (AntiTheftConfig.Log.Value)
                    Plugin.Log.LogInfo($"[Theft] {extra} extra drop pass(es) at {speed:0.##}x speed.");
            }
            catch (Exception e)
            {
                _reentering = false;
                Plugin.Log.LogError($"[Theft] drop scaling: {e.Message}");
            }
        }
    }
}
