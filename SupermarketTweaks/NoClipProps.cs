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
        internal static ConfigEntry<float> ListRadius;
        internal static ConfigEntry<string> ListIgnore;

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
                "Log every collider near you, closest first. Stand against whatever is blocking " +
                "you and press this to find out what it is actually called.");
            ListIgnore = cfg.Bind("World", "ListIgnoreNames",
                "PaintableModule,PaintablesParent,UModeler,Umodeler,BasePlane,Demo_Beams,Beam ,LOD0",
                "Substrings to leave out of the F11 listing, comma separated. The room itself is " +
                "made of hundreds of wall, floor and ceiling panels that are never what you are " +
                "looking for, and they crowd out the thing that is. Clear it to see everything.");
            ListRadius = cfg.Bind("World", "ListRadiusMetres", 5f,
                new ConfigDescription("How far around you to look when listing colliders.",
                    new AcceptableValueRange<float>(1f, 30f)));
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

        // Finding the right name is the hard part, so answer it by proximity instead of by guess.
        //
        // Filtering by name assumed the placed object is called something like its asset
        // (115_GiantElephant); it is not, and a filter that matches nothing tells you nothing about
        // what IS there. Standing against the obstruction and listing what is within arm's reach
        // cannot miss in the same way.
        private void ListMatches()
        {
            var cam = Camera.main;
            Vector3 origin = cam != null ? cam.transform.position : transform.position;
            float radius = NoClipPropsConfig.ListRadius != null
                ? Mathf.Clamp(NoClipPropsConfig.ListRadius.Value, 1f, 30f) : 5f;

            // The room's own fabric - paintable wall/floor/ceiling modules, UModeler meshes,
            // structural beams - is never the answer and there are hundreds of them. Listing it
            // buried the one line that mattered: an Expansions/Addons entry sat at 0.00m while
            // forty PaintableModules filled the rest of the page.
            var ignore = new List<string>();
            var rawIgnore = NoClipPropsConfig.ListIgnore != null ? NoClipPropsConfig.ListIgnore.Value : null;
            if (!string.IsNullOrEmpty(rawIgnore))
                foreach (var part in rawIgnore.Split(','))
                {
                    var t = part.Trim();
                    if (t.Length > 0) ignore.Add(t);
                }

            var hits = new List<KeyValuePair<float, string>>();
            int hidden = 0;

            // OverlapSphere rather than FindObjectsOfType: it asks physics directly, so it returns
            // exactly the things that could be blocking you, and nothing from the rest of the map.
            foreach (var col in Physics.OverlapSphere(origin, radius, ~0, QueryTriggerInteraction.Collide))
            {
                if (col == null) continue;

                float d = Vector3.Distance(origin, col.ClosestPoint(origin));

                string path = col.name;
                var t = col.transform.parent;
                int depth = 0;
                while (t != null && depth++ < 5) { path = t.name + "/" + path; t = t.parent; }

                bool skip = false;
                foreach (var word in ignore)
                {
                    if (path.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    skip = true;
                    break;
                }
                if (skip) { hidden++; continue; }

                hits.Add(new KeyValuePair<float, string>(d,
                    $"{d,5:0.00}m  {path}   [{col.GetType().Name}" +
                    $"{(col.isTrigger ? ", trigger" : "")}" +
                    $"{(col.enabled ? "" : ", disabled")}, layer {LayerMask.LayerToName(col.gameObject.layer)}]"));
            }

            hits.Sort((a, b) => a.Key.CompareTo(b.Key));

            Plugin.Log.LogInfo($"[NoClip] {hits.Count} collider(s) within {radius:0.#}m " +
                               (hidden > 0 ? $" ({hidden} room panel(s) hidden by ListIgnoreNames)" : "") +
                               " (closest first) - the blocker is usually near the top:");
            for (int i = 0; i < hits.Count && i < 60; i++)
                Plugin.Log.LogInfo("    " + hits[i].Value);

            if (hits.Count == 0)
                Plugin.Log.LogWarning("    nothing at all - are you actually standing next to it?");
            else
                Plugin.Log.LogInfo("[NoClip] Put a distinctive part of the name into " +
                                   "World/PropNameFilter, then turn DisablePropCollision on.");
        }
    }
}
