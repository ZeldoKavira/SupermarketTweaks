using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SupermarketTweaks
{
    // Let you walk through a prop that is blocking somewhere you want to get to - the giant
    // elephant being the one that prompted this.
    //
    // Only colliders are touched, never the object itself: hiding or deleting it would be a visible
    // change every other player could see and would have to agree with, whereas a collider is
    // purely local physics. Nothing here is networked, so it affects only the machine it runs on.
    //
    // Matching is by name substring rather than a hardcoded object, because the in-scene name is
    // not something the assembly tells us - the asset is called 115_GiantElephant, but what the
    // placed instance is called is a guess until we look. Every match is logged, so if it catches
    // nothing or the wrong thing the filter can be corrected without a new build.
    public static class NoClipPropsConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> NameFilter;
        internal static ConfigEntry<bool> IncludeTriggers;
        internal static ConfigEntry<KeyboardShortcut> ListKey;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("World", "DisablePropCollision", false,
                "Turn off collision on props whose name matches PropNameFilter, so you can walk " +
                "through them. Local only - nobody else is affected.");
            NameFilter = cfg.Bind("World", "PropNameFilter", "Elephant",
                "Substring of the object name to match, case-insensitive. Comma-separate for more " +
                "than one. Press the list key to see what actually matches in your shop.");
            IncludeTriggers = cfg.Bind("World", "AlsoDisableTriggers", false,
                "Also disable trigger colliders on matches. Off by default: triggers are usually " +
                "interaction volumes, and switching them off can break using the object.");
            ListKey = cfg.Bind("World", "ListPropsKey", new KeyboardShortcut(KeyCode.F11),
                "Log every object matching the filter, with its colliders. Use this to find the " +
                "right name.");
        }

        internal static bool On => Enabled != null && Enabled.Value;

        internal static List<string> Filters()
        {
            var list = new List<string>();
            if (NameFilter == null || string.IsNullOrEmpty(NameFilter.Value)) return list;
            foreach (var part in NameFilter.Value.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }
    }

    public class NoClipPropsDriver : MonoBehaviour
    {
        private float _next;
        private bool _lastState;
        private bool _known;

        // Colliders we switched off, so turning the setting back on restores exactly those and
        // nothing else - a collider that was already disabled stays disabled.
        private static readonly List<Collider> _disabled = new List<Collider>();

        internal static string Status = "off";

        private void Update()
        {
            try
            {
                if (NoClipPropsConfig.ListKey != null && NoClipPropsConfig.ListKey.Value.IsDown())
                    ListMatches();

                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + 2f;

                bool on = NoClipPropsConfig.On;
                bool changed = !_known || on != _lastState;
                _known = true;
                _lastState = on;

                if (on) Apply(changed);
                else if (changed) Restore();
            }
            catch (Exception e) { Plugin.Log.LogError($"[NoClip] {e.Message}"); }
        }

        private static bool Matches(string name, List<string> filters)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var f in filters)
                if (name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void Apply(bool announce)
        {
            var filters = NoClipPropsConfig.Filters();
            if (filters.Count == 0) { Status = "no filter set"; return; }

            int hit = 0;

            // Re-scanned periodically rather than once: props can be placed, bought or spawned at
            // any time, and a one-shot pass would miss anything that arrives later.
            foreach (var col in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (col == null || !col.enabled) continue;
                if (col.isTrigger && !NoClipPropsConfig.IncludeTriggers.Value) continue;

                // Check the whole parent chain: the collider usually sits on a child mesh whose own
                // name says nothing useful.
                if (!MatchesHierarchy(col.transform, filters)) continue;

                col.enabled = false;
                _disabled.Add(col);
                hit++;
            }

            if (hit > 0 || announce)
            {
                Status = $"{_disabled.Count} collider(s) disabled";
                if (hit > 0)
                    Plugin.Log.LogInfo($"[NoClip] Disabled {hit} collider(s) on props matching " +
                                       $"'{NoClipPropsConfig.NameFilter.Value}'.");
                else if (announce)
                    Plugin.Log.LogWarning($"[NoClip] Nothing matched '{NoClipPropsConfig.NameFilter.Value}'. " +
                                          $"Press {NoClipPropsConfig.ListKey.Value} to list what is actually there.");
            }
        }

        private static bool MatchesHierarchy(Transform t, List<string> filters)
        {
            // Bounded walk upward - a deep hierarchy should not cost us a long climb per collider.
            int depth = 0;
            while (t != null && depth++ < 6)
            {
                if (Matches(t.name, filters)) return true;
                t = t.parent;
            }
            return false;
        }

        private void Restore()
        {
            int n = 0;
            foreach (var col in _disabled)
            {
                if (col == null) continue;      // destroyed since
                col.enabled = true;
                n++;
            }
            _disabled.Clear();
            Status = "off";
            if (n > 0) Plugin.Log.LogInfo($"[NoClip] Restored {n} collider(s).");
        }

        // Finding the right name is the hard part, so make it answerable in game.
        private void ListMatches()
        {
            var filters = NoClipPropsConfig.Filters();
            int found = 0;

            Plugin.Log.LogInfo($"[NoClip] Objects matching '{NoClipPropsConfig.NameFilter.Value}':");

            foreach (var col in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (col == null || !MatchesHierarchy(col.transform, filters)) continue;
                found++;

                var path = col.name;
                var t = col.transform.parent;
                int depth = 0;
                while (t != null && depth++ < 4) { path = t.name + "/" + path; t = t.parent; }

                Plugin.Log.LogInfo($"    {path}   [{col.GetType().Name}" +
                                   $"{(col.isTrigger ? ", trigger" : "")}" +
                                   $"{(col.enabled ? "" : ", already disabled")}]");
            }

            if (found == 0)
                Plugin.Log.LogWarning("    nothing. Try a shorter filter, or check the object's real name.");
        }
    }
}
