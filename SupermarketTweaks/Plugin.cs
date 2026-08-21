using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SupermarketTweaks
{
    // Supermarket Together tweaks.
    //
    // Two features, both of which exist because the game keeps undoing something you asked for:
    // prices drift out of line every time inflation moves, and Time.timeScale is forced back to 1
    // at the end of every day.
    [BepInPlugin(Guid, "Supermarket Tweaks", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "net.zeldo.supermarkettweaks";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            AutoPriceConfig.Init(Config);
            GameSpeedConfig.Init(Config);
            ShelflessBoxConfig.Init(Config);
            AntiTheftConfig.Init(Config);
            AutoSalesConfig.Init(Config);
            StaffRolesConfig.Init(Config);
            NetSyncConfig.Init(Config);
            UIDumpConfig.Init(Config);
            RestockOrderConfig.Init(Config);
            NoClipPropsConfig.Init(Config);
            EasterDumpConfig.Init(Config);
            SettingsWindow.Init(Config);

            ApplyPatches();

            var go = new GameObject("SupermarketTweaks");
            go.transform.SetParent(transform);
            go.AddComponent<AutoPriceDriver>();
            go.AddComponent<GameSpeedDriver>();
            go.AddComponent<AutoSalesDriver>();
            go.AddComponent<ThiefWatchDriver>();
            go.AddComponent<StaffRolesDriver>();
            go.AddComponent<NetSyncDriver>();
            go.AddComponent<EasterDumper>();
            go.AddComponent<NoClipPropsDriver>();
            go.AddComponent<RestockOrderDriver>();
            go.AddComponent<UIDumper>();
            go.AddComponent<SettingsWindow>();

            ForwardUnityErrors();
            WatchConfigFile();
            Log.LogInfo("Supermarket Tweaks loaded.");
        }

        // Patched per class rather than with PatchAll: one bad signature - an overload that moved,
        // a renamed method after a game update - would otherwise take the whole plugin down with
        // it, and a mod that silently does nothing is far worse to diagnose than one that logs the
        // single patch it lost.
        private void ApplyPatches()
        {
            var harmony = new Harmony(Guid);
            int ok = 0, failed = 0;

            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try { harmony.CreateClassProcessor(type).Patch(); ok++; }
                catch (Exception e) { failed++; Log.LogError($"Patch failed: {type.Name} - {e.Message}"); }
            }

            Log.LogInfo($"{ok} patch class(es) applied{(failed > 0 ? $", {failed} FAILED" : "")}.");
        }

        // Forward Unity's own errors into the BepInEx log.
        //
        // This log opens with "Unable to start Unity log writer", so BepInEx captured none of
        // Unity's output - and that blindness cost real time: Mirror was reporting
        // "No writer found for SmtMessage" on every send, and neither side could see it. Anything
        // the engine complains about should be visible where we are already looking.
        //
        // Errors and exceptions only; forwarding every Debug.Log would drown the log in the game's
        // own chatter.
        private void ForwardUnityErrors()
        {
            try
            {
                Application.logMessageReceived += (condition, stackTrace, type) =>
                {
                    if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;

                    // Only what looks like ours or Mirror's, otherwise this becomes the game's
                    // error log rather than the mod's.
                    if (condition == null) return;
                    if (condition.IndexOf("Mirror", StringComparison.OrdinalIgnoreCase) < 0 &&
                        condition.IndexOf("Smt", StringComparison.Ordinal) < 0 &&
                        condition.IndexOf("SupermarketTweaks", StringComparison.Ordinal) < 0 &&
                        condition.IndexOf("No writer found", StringComparison.OrdinalIgnoreCase) < 0 &&
                        condition.IndexOf("No reader found", StringComparison.OrdinalIgnoreCase) < 0)
                        return;

                    Log.LogWarning($"[Unity/{type}] {condition}");
                };
            }
            catch (Exception e) { Log.LogWarning($"Could not hook Unity's log: {e.Message}"); }
        }

        // BepInEx re-reads the file on its own only for entries you never touch in code, so editing
        // the .cfg while the game runs otherwise appears to do nothing. This makes the file the
        // live source of truth, which matters because the F1 panel and the file can disagree.
        private void WatchConfigFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(Config.ConfigFilePath);
                var file = Path.GetFileName(Config.ConfigFilePath);
                if (dir == null || !Directory.Exists(dir)) return;

                var watcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += (_, __) =>
                {
                    try { Config.Reload(); }
                    catch (Exception e) { Log.LogWarning($"Config reload failed: {e.Message}"); }
                };
            }
            catch (Exception e) { Log.LogWarning($"Config watcher unavailable: {e.Message}"); }
        }
    }
}
