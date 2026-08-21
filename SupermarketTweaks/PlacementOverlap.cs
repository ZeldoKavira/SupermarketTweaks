using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SupermarketTweaks
{
    // Stop snapped shelves refusing to place because they touch by a hair.
    //
    // Placement is gated on a PlayMaker trigger, read at the top of each build behaviour:
    //
    //   overlapping = pmakerFSM.FsmVariables.GetFsmBool("Overlapping").Value;
    //   ...
    //   if ((!correctSector || overlapping) && canPlace) { canPlace = false; ... red }
    //
    // So it is a physics trigger, not a distance rule, and grid snapping puts neighbours exactly
    // flush - where "exactly" is floating point, and a shared edge counts as an intersection.
    //
    // Two settings, in the order you would want to try them:
    //
    //   Tolerance   shrinks the placement ghost's trigger colliders by a fraction of a percent, so
    //               a shared edge no longer registers while a real overlap still does. This is the
    //               actual fix and keeps every other placement rule intact.
    //   Disable     forces the flag false outright, so nothing blocks placement. The escape hatch,
    //               for when tolerance is not enough - it will happily let you bury one shelf
    //               inside another.
    //
    // The game already does exactly this override itself for architectural pieces:
    //
    //   if (MainPlayer.GetButton("Drop Item") && currentTabIndex == 12 && ...) overlapping = false;
    public static class PlacementOverlapConfig
    {
        internal static ConfigEntry<float> Tolerance;
        internal static ConfigEntry<bool> Disable;
        internal static ConfigEntry<bool> Log;

        public static void Init(ConfigFile cfg)
        {
            Tolerance = cfg.Bind("Building", "OverlapTolerance", 0.02f,
                new ConfigDescription("Shrink the placement ghost's collision by this fraction so " +
                    "flush-snapped items stop blocking each other. 0.02 is 2%. Set 0 to disable.",
                    new AcceptableValueRange<float>(0f, 0.25f)));
            Disable = cfg.Bind("Building", "DisableOverlapCheck", false,
                "Ignore overlapping entirely while building. Lets you place items inside each " +
                "other, so use it only if the tolerance above is not enough.");
            Log = cfg.Bind("Building", "LogPlacement", false,
                "Log which placement colliders were shrunk.");
        }

        internal static bool ToleranceOn => Tolerance != null && Tolerance.Value > 0.0001f;
    }

    internal static class PlacementOverlap
    {
        internal static string Status = "default";

        // Originals per collider instance, so a ghost that is destroyed and rebuilt each time you
        // pick a new item cannot compound the shrink.
        private static readonly Dictionary<int, Vector3> _boxSizes = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, float> _radii = new Dictionary<int, float>();

        internal static void ShrinkGhost(GameObject dummy)
        {
            if (dummy == null || !PlacementOverlapConfig.ToleranceOn) return;

            float keep = 1f - Mathf.Clamp(PlacementOverlapConfig.Tolerance.Value, 0f, 0.25f);
            int changed = 0;

            foreach (var col in dummy.GetComponentsInChildren<Collider>(true))
            {
                if (col == null) continue;
                int id = col.GetInstanceID();

                var box = col as BoxCollider;
                if (box != null)
                {
                    if (!_boxSizes.ContainsKey(id)) _boxSizes[id] = box.size;

                    var target = _boxSizes[id] * keep;
                    // Only write when it actually differs, so this is safe to call every frame.
                    if ((box.size - target).sqrMagnitude > 1e-8f) { box.size = target; changed++; }
                    continue;
                }

                var sphere = col as SphereCollider;
                if (sphere != null)
                {
                    if (!_radii.ContainsKey(id)) _radii[id] = sphere.radius;

                    float target = _radii[id] * keep;
                    if (Mathf.Abs(sphere.radius - target) > 1e-5f) { sphere.radius = target; changed++; }
                    continue;
                }

                var capsule = col as CapsuleCollider;
                if (capsule != null)
                {
                    if (!_radii.ContainsKey(id)) _radii[id] = capsule.radius;

                    float target = _radii[id] * keep;
                    if (Mathf.Abs(capsule.radius - target) > 1e-5f) { capsule.radius = target; changed++; }
                }

                // MeshColliders are left alone deliberately: there is no size to scale, and
                // swapping the mesh for a shrunken copy would cost far more than it is worth.
            }

            if (changed > 0)
            {
                Status = $"ghost shrunk {(1f - keep) * 100f:0.#}%";
                if (PlacementOverlapConfig.Log.Value)
                    Plugin.Log.LogInfo($"[Placement] Shrunk {changed} collider(s) on the placement ghost by " +
                                       $"{(1f - keep) * 100f:0.#}%.");
            }
        }

        // Clearing the flag the behaviours are about to read is the least invasive way to disable
        // the check: the game's own code path runs unchanged, it simply sees no overlap.
        private static FieldInfo _fsmField;
        private static bool _looked;
        private static bool _warned;

        internal static void ClearOverlapFlag(Builder_Main builder)
        {
            if (!PlacementOverlapConfig.Disable.Value) return;

            try
            {
                if (!_looked)
                {
                    _looked = true;
                    _fsmField = typeof(Builder_Main).GetField("pmakerFSM",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                if (_fsmField == null) return;

                var fsm = _fsmField.GetValue(builder);
                if (fsm == null) return;

                // Reached by reflection so the mod needs no PlayMaker reference:
                //   fsm.FsmVariables.GetFsmBool("Overlapping").Value = false;
                var vars = fsm.GetType().GetProperty("FsmVariables")?.GetValue(fsm, null);
                if (vars == null) return;

                var get = vars.GetType().GetMethod("GetFsmBool", new[] { typeof(string) });
                var flag = get?.Invoke(vars, new object[] { "Overlapping" });
                if (flag == null) return;

                var valueProp = flag.GetType().GetProperty("Value");
                if (valueProp == null) return;

                valueProp.SetValue(flag, false, null);
                Status = "overlap check DISABLED";
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning($"[Placement] Couldn't clear the overlap flag: {e.Message}");
                }
            }
        }
    }

    // Every build mode reads the flag as its first statement, so a prefix that clears it lands
    // before the read. Patched per mode rather than once, because they are separate methods with
    // no shared entry point.
    [HarmonyPatch(typeof(Builder_Main), "BuildableBehaviour")]
    public static class Patch_Builder_BuildableBehaviour
    {
        private static void Prefix(Builder_Main __instance) => PlacementOverlap.ClearOverlapFlag(__instance);
    }

    [HarmonyPatch(typeof(Builder_Main), "DecorationBehaviour")]
    public static class Patch_Builder_DecorationBehaviour
    {
        private static void Prefix(Builder_Main __instance) => PlacementOverlap.ClearOverlapFlag(__instance);
    }

    [HarmonyPatch(typeof(Builder_Main), "ManufacturingBehaviour")]
    public static class Patch_Builder_ManufacturingBehaviour
    {
        private static void Prefix(Builder_Main __instance) => PlacementOverlap.ClearOverlapFlag(__instance);
    }

    public class PlacementOverlapDriver : MonoBehaviour
    {
        private static FieldInfo _dummyField;
        private static bool _looked;

        private void Update()
        {
            try
            {
                if (!PlacementOverlapConfig.ToleranceOn) return;

                var builder = UnityEngine.Object.FindObjectOfType<Builder_Main>();
                if (builder == null) return;

                if (!_looked)
                {
                    _looked = true;
                    _dummyField = typeof(Builder_Main).GetField("dummyOBJ",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (_dummyField == null)
                        Plugin.Log.LogWarning("[Placement] Builder_Main.dummyOBJ not found; the " +
                                              "tolerance setting will do nothing.");
                }
                if (_dummyField == null) return;

                // Applied every frame while a ghost exists: it is rebuilt whenever you pick a
                // different item, and the originals are keyed per collider so re-applying is free.
                var dummy = _dummyField.GetValue(builder) as GameObject;
                if (dummy != null) PlacementOverlap.ShrinkGhost(dummy);
            }
            catch (Exception e) { Plugin.Log.LogError($"[Placement] {e.Message}"); }
        }
    }
}
