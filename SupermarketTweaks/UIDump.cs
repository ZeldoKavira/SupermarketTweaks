using System;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace SupermarketTweaks
{
    // Dump a UI hierarchy so a real in-canvas button can be built against real names.
    //
    // Placing something next to the game's own "buy empty box" button means cloning that object,
    // and nothing about it is visible from the assembly: the canvas is prefab data, the button is
    // almost certainly driven by a PlayMaker FSM rather than a Unity Button, and the object names
    // are whatever the developer typed. Guessing produces a clone that either does nothing or
    // fires the original's FSM.
    //
    // Component type names are read generically rather than by referencing PlayMaker, so this
    // reports FSMs without the mod needing to link against them.
    public static class UIDumpConfig
    {
        internal static ConfigEntry<KeyboardShortcut> Key;
        internal static ConfigEntry<bool> ActiveOnly;

        public static void Init(ConfigFile cfg)
        {
            Key = cfg.Bind("UI", "DumpUIKey", new KeyboardShortcut(KeyCode.F12),
                "Dump the ordering terminal's UI hierarchy to BepInEx/SupermarketTweaks-ui.txt. " +
                "Open the terminal first.");
            ActiveOnly = cfg.Bind("UI", "DumpActiveOnly", false,
                "Only include objects that are currently active. Off by default - the button we " +
                "are looking for may live on an inactive tab.");
        }
    }

    public class UIDumper : MonoBehaviour
    {
        private void Update()
        {
            try
            {
                if (UIDumpConfig.Key == null || !UIDumpConfig.Key.Value.IsDown()) return;
                Dump();
            }
            catch (Exception e) { Plugin.Log.LogError($"[UIDump] {e.Message}"); }
        }

        private static void Dump()
        {
            var sb = new StringBuilder();
            int roots = 0;

            var devices = UnityEngine.Object.FindObjectsOfType<OrderingDevice>(true);
            if (devices != null && devices.Length > 0)
            {
                foreach (var d in devices)
                {
                    sb.AppendLine($"=== OrderingDevice on '{d.gameObject.name}' " +
                                  $"(active={d.gameObject.activeInHierarchy}) ===");
                    Walk(d.transform, 0, sb);
                    sb.AppendLine();
                    roots++;
                }
            }

            // The empty-box button may not be parented under the device itself, so also dump the
            // blackboard's shopping-list canvas - the other half of the same screen.
            var gd = GameData.Instance;
            var bb = gd != null ? gd.GetComponent<ManagerBlackboard>() : null;
            if (bb != null && bb.shoppingListParent != null)
            {
                var top = bb.shoppingListParent.transform;
                // Climb to the canvas root so siblings of the button are included. Detected by
                // component type NAME rather than the Canvas type, which lives in UIModule - not
                // worth a new assembly reference (and a matching CI reference assembly) just to
                // stop climbing one level early.
                while (top.parent != null && !HasComponentNamed(top, "Canvas")) top = top.parent;

                sb.AppendLine($"=== Shopping list canvas root '{top.name}' ===");
                Walk(top, 0, sb);
                roots++;
            }

            if (roots == 0)
            {
                Plugin.Log.LogWarning("[UIDump] Found no ordering UI. Open the ordering terminal first.");
                return;
            }

            string text = sb.ToString();
            try
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath,
                                                     "SupermarketTweaks-ui.txt");
                System.IO.File.WriteAllText(path, text);
                Plugin.Log.LogInfo($"[UIDump] {roots} root(s), {text.Length} chars -> {path}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[UIDump] Couldn't write the file ({e.Message}); logging instead.");
                Plugin.Log.LogInfo("\n" + text);
            }
        }

        private static bool HasComponentNamed(Transform t, string typeName)
        {
            foreach (var c in t.GetComponents<Component>())
                if (c != null && c.GetType().Name == typeName) return true;
            return false;
        }

        private static void Walk(Transform t, int depth, StringBuilder sb)
        {
            if (t == null || depth > 12) return;                 // guard against absurd nesting
            if (UIDumpConfig.ActiveOnly.Value && !t.gameObject.activeInHierarchy) return;

            var pad = new string(' ', depth * 2);

            // Component types are what identify a clickable thing - a Button, or an FSM standing in
            // for one - and the text is what identifies WHICH button.
            var parts = new StringBuilder();
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) { parts.Append("<missing> "); continue; }
                parts.Append(c.GetType().Name).Append(' ');
            }

            string label = "";
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                var type = c.GetType();
                if (type.Name != "TextMeshProUGUI" && type.Name != "Text") continue;

                // Read .text generically so this needs no TextMeshPro reference.
                var prop = type.GetProperty("text");
                if (prop == null) continue;
                var val = prop.GetValue(c, null) as string;
                if (!string.IsNullOrEmpty(val)) { label = "  \"" + val.Trim() + "\""; break; }
            }

            var rect = t as RectTransform;
            string geom = rect != null
                ? $"  pos={rect.anchoredPosition} size={rect.sizeDelta}"
                : "";

            sb.AppendLine($"{pad}{t.name}{(t.gameObject.activeSelf ? "" : " [inactive]")}{label}" +
                          $"\n{pad}   [{parts.ToString().Trim()}]{geom}");

            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb);
        }
    }
}
