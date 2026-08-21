using System;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace SupermarketTweaks
{
    // Dump the easter-egg trigger phrases (F10).
    //
    // Typing one of these in the in-game chat spawns something: a PlayMaker FSM on the chat object
    // calls EasterChecker.StringChecker(message), which does
    //
    //   for (int i = 0; i < checkersArray.Length; i++)
    //       if (!alreadySpawned[i] && message.Contains(checkersArray[i])) { CmdSpawnEaster(i); ... }
    //
    // so each phrase fires once per save, with a 5s cooldown between spawns.
    //
    // The phrases themselves are serialized MonoBehaviour data, and this build ships no
    // MonoBehaviour typetrees - 0 of 9,772 in level1 are readable - so they cannot be pulled out of
    // the files with the usual asset tools. Reading them from the live object sidesteps that
    // entirely: the array is a public field, already loaded, already correct.
    public static class EasterDumpConfig
    {
        internal static ConfigEntry<KeyboardShortcut> Key;

        public static void Init(ConfigFile cfg)
        {
            Key = cfg.Bind("UI", "DumpEasterEggsKey", new KeyboardShortcut(KeyCode.F10),
                "Log the chat phrases that trigger easter eggs, and which have already been used " +
                "on this save.");
        }
    }

    public class EasterDumper : MonoBehaviour
    {
        private void Update()
        {
            try
            {
                if (EasterDumpConfig.Key == null || !EasterDumpConfig.Key.Value.IsDown()) return;
                Dump();
            }
            catch (Exception e) { Plugin.Log.LogError($"[Easter] {e.Message}"); }
        }

        private static void Dump()
        {
            // FindObjectsOfType rather than a manager lookup: nothing exposes this component, and
            // there is no cost to a one-shot scan on a keypress.
            var checkers = UnityEngine.Object.FindObjectsOfType<EasterChecker>();
            if (checkers == null || checkers.Length == 0)
            {
                Plugin.Log.LogWarning("[Easter] No EasterChecker in the scene - load a save first.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== EASTER EGG CHAT PHRASES ===");
            sb.AppendLine("Type one of these in the in-game chat. Each fires once per save.");
            sb.AppendLine();

            foreach (var c in checkers)
            {
                if (c == null || c.checkersArray == null) continue;

                for (int i = 0; i < c.checkersArray.Length; i++)
                {
                    bool used = c.alreadySpawned != null
                                && i < c.alreadySpawned.Length
                                && c.alreadySpawned[i];

                    sb.AppendLine($"  [{i,2}] {c.checkersArray[i]}{(used ? "   (already used on this save)" : "")}");
                }
            }

            // Not in checkersArray - hardcoded in StringChecker, and not an easter egg: it moves
            // corner shelves back to the entrance. Worth listing since it is typed the same way.
            sb.AppendLine();
            sb.AppendLine("  Also accepted (a fix, not an egg):  corner_shelf_fix");

            string text = sb.ToString();
            Plugin.Log.LogInfo("\n" + text);

            try
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath,
                                                     "SupermarketTweaks-easter.txt");
                System.IO.File.WriteAllText(path, text);
                Plugin.Log.LogInfo($"[Easter] Also written to {path}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Easter] Couldn't write the file: {e.Message}"); }
        }
    }
}
