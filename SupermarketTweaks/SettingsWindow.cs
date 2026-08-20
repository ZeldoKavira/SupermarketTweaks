using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SupermarketTweaks
{
    // In-game settings panel (F1), same idea as the Bookshop and Returns Outlet mods.
    //
    // Driven off the config entries themselves rather than a hand-written list of rows, so every
    // setting appears automatically and a new feature needs no UI work.
    public class SettingsWindow : MonoBehaviour
    {
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<float> Opacity;

        public static void Init(ConfigFile cfg)
        {
            ToggleKey = cfg.Bind("UI", "SettingsKey", new KeyboardShortcut(KeyCode.F1),
                "Opens and closes the settings panel.");
            Opacity = cfg.Bind("UI", "WindowOpacity", 0.95f,
                new ConfigDescription("How solid the panel is. 1 is fully opaque.",
                    new AcceptableValueRange<float>(0.3f, 1f)));
        }

        private bool _open;
        private Vector2 _scroll;
        private Rect _rect = new Rect(60f, 60f, 620f, 560f);
        private readonly Dictionary<string, string> _editing = new Dictionary<string, string>();

        private void Update()
        {
            try { if (ToggleKey != null && ToggleKey.Value.IsDown()) Toggle(); }
            catch { }
        }

        private void Toggle()
        {
            _open = !_open;
            Cursor.lockState = _open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = _open;
        }

        private void OnGUI()
        {
            if (!_open) return;
            _rect = GUI.Window(GetInstanceID(), _rect, (GUI.WindowFunction)Draw,
                               "Supermarket Tweaks", WindowStyle());
        }

        private void Draw(int id)
        {
            var cfg = Plugin.Instance != null ? Plugin.Instance.Config : null;
            if (cfg == null) { GUI.DragWindow(); return; }

            if (GUI.Button(new Rect(_rect.width - 28f, 2f, 26f, 20f), "X")) Toggle();

            GUILayout.Space(4f);
            _scroll = GUILayout.BeginScrollView(_scroll);

            // Live status first - the two features are both "did it actually fire" questions.
            GUILayout.Label($"<b>Status</b>   speed: {GameSpeedDriver.Status}   |   pricing: {AutoPrice.LastResult}", Rich());
            GUILayout.Label($"   sync: {NetSync.Status}" +
                            (AutoPriceConfig.IsAuthority
                                ? "   |   <b>this machine prices automatically</b>"
                                : "   |   the host prices automatically; these settings come from them"), Rich());

            // Manual sweeps stay available to a client on purpose - CmdUpdateProductPrice accepts
            // them, and it is useful to be able to force one without asking the host.
            GUILayout.Label($"   sales: {AutoSales.Status}   |   theft: {AntiTheft.Status}", Rich());

            int spawned = ThiefWatchDriver.SpawnedThieves, checkout = ThiefWatchDriver.CheckoutThieves;
            if (spawned + checkout > 0)
                GUILayout.Label($"   thieves this session: <b>{spawned} spawned</b> (bad luck, cameras help) " +
                                $"| <b>{checkout} turned away from a till</b> (add checkouts)", Rich());

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reprice everything now", GUILayout.Width(220f))
                && AutoPriceDriver.Instance != null)
                AutoPriceDriver.Instance.SweepNow("manual");
            if (GUILayout.Button("Restore sales now", GUILayout.Width(180f))
                && AutoSalesDriver.Instance != null)
                AutoSalesDriver.Instance.RestoreNow();
            GUILayout.EndHorizontal();

            foreach (var section in cfg.Keys.Select(k => k.Section).Distinct().OrderBy(s => s))
            {
                GUILayout.Space(6f);
                GUILayout.Label($"<b>{section}</b>", Rich());

                foreach (var key in cfg.Keys.Where(k => k.Section == section).OrderBy(k => k.Key))
                {
                    var entry = cfg[key];
                    // CashierNames is a comma-separated list of names, which is no way to pick
                    // people. The roster below replaces it.
                    if (entry != null && key.Key != "CashierNames") DrawEntry(entry, key);
                }

                if (section == "Staff") DrawStaffPicker();
            }

            GUILayout.Space(8f);
            GUILayout.Label("Changes apply immediately and are written to the config file.");
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width, 22f));
        }

        private void DrawEntry(ConfigEntryBase entry, ConfigDefinition key)
        {
            var type = entry.SettingType;

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(key.Key, entry.Description?.Description ?? ""),
                GUILayout.Width(240f));

            if (type == typeof(bool))
            {
                bool cur = (bool)entry.BoxedValue;
                bool now = GUILayout.Toggle(cur, cur ? " on" : " off");
                if (now != cur) entry.BoxedValue = now;
            }
            else if (type == typeof(float) || type == typeof(int))
            {
                DrawNumber(entry, key);
            }
            else
            {
                // Keybinds and anything else: show it, don't pretend to edit it here.
                GUILayout.Label(entry.BoxedValue?.ToString() ?? "");
            }

            GUILayout.EndHorizontal();
        }

        // Slider plus a typed box: the slider for a quick sweep, the box for an exact value.
        private void DrawNumber(ConfigEntryBase entry, ConfigDefinition key)
        {
            bool isInt = entry.SettingType == typeof(int);
            float value = isInt ? (int)entry.BoxedValue : (float)entry.BoxedValue;

            float min = 0f, max = 10f;
            if (entry.Description?.AcceptableValues is AcceptableValueBase range)
            {
                // AcceptableValueRange<T> exposes its bounds as properties; read them generically
                // so this works for int and float without separate cases.
                var t = range.GetType();
                var minProp = t.GetProperty("MinValue");
                var maxProp = t.GetProperty("MaxValue");
                if (minProp != null && maxProp != null)
                {
                    min = Convert.ToSingle(minProp.GetValue(range, null));
                    max = Convert.ToSingle(maxProp.GetValue(range, null));
                }
            }

            float slid = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(160f));
            if (isInt) slid = Mathf.Round(slid);
            if (!Mathf.Approximately(slid, value))
            {
                entry.BoxedValue = isInt ? (object)(int)slid : slid;
                _editing.Remove(Id(key));
                value = slid;
            }

            // Free text so exact values are possible; only committed when it parses.
            string id = Id(key);
            string shown = _editing.ContainsKey(id) ? _editing[id] : value.ToString(isInt ? "0" : "0.##");
            string typed = GUILayout.TextField(shown, GUILayout.Width(60f));
            if (typed != shown)
            {
                _editing[id] = typed;
                if (float.TryParse(typed, out float parsed))
                {
                    parsed = Mathf.Clamp(parsed, min, max);
                    entry.BoxedValue = isInt ? (object)(int)parsed : parsed;
                }
            }
        }

        // Who works the tills. Listed by the game's own NPCName so it matches the employee menu.
        private void DrawStaffPicker()
        {
            var mgr = NPC_Manager.Instance;
            if (mgr == null || mgr.employeesArray == null)
            {
                GUILayout.Label("   (load a save to pick staff)");
                return;
            }

            var cashiers = StaffRolesConfig.CashierList();
            int shown = 0;

            GUILayout.Space(4f);
            GUILayout.Label($"   <b>Your staff</b>   {StaffRoles.Status}", Rich());

            for (int i = 0; i < mgr.employeesArray.Length; i++)
            {
                string name = StaffRoles.NameOf(mgr, i);
                if (string.IsNullOrEmpty(name)) continue;
                shown++;

                int role = StaffRoles.RoleOf(mgr, i);
                bool isCashier = cashiers.Contains(name);

                GUILayout.BeginHorizontal();
                GUILayout.Label($"   {name}", GUILayout.Width(200f));
                GUILayout.Label(StaffRoles.RoleName(role), GUILayout.Width(110f));

                bool now = GUILayout.Toggle(isCashier, isCashier ? " day cashier" : " not a cashier");
                if (now != isCashier)
                {
                    if (now) cashiers.Add(name); else cashiers.Remove(name);
                    StaffRolesConfig.SetCashiers(cashiers);
                }
                GUILayout.EndHorizontal();
            }

            if (shown == 0) GUILayout.Label("   No staff hired yet.");
            else GUILayout.Label("   Day cashiers work the tills while open and restock while shut. " +
                                 "Storage staff restock whenever the floor is clear of boxes.", Rich());
        }

        private static string Id(ConfigDefinition key) => key.Section + "/" + key.Key;

        // Unity's built-in window skin is heavily translucent, which is unreadable over a bright
        // shop floor. A solid background texture is handed to GUI.Window rather than painted inside
        // the callback, because the callback cannot cover the title bar.
        private static GUIStyle _window, _rich;
        private static Texture2D _bg;
        private static float _builtFor = -1f;

        private static float Alpha => Opacity == null ? 0.95f : Mathf.Clamp(Opacity.Value, 0.3f, 1f);

        private static void Ensure()
        {
            if (_window != null && Mathf.Approximately(_builtFor, Alpha)) return;
            _builtFor = Alpha;

            if (_bg == null)
                _bg = new Texture2D(4, 4, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };

            var c = new Color(0.09f, 0.10f, 0.13f, Alpha);
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            _bg.SetPixels(px);
            _bg.Apply();

            _window = new GUIStyle(GUI.skin.window)
            {
                border = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(10, 10, 24, 10),
                richText = true,
            };
            _window.normal.background = _bg;
            _window.onNormal.background = _bg;
            _window.normal.textColor = Color.white;
            _window.onNormal.textColor = Color.white;

            _rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            _rich.normal.textColor = new Color(0.90f, 0.92f, 0.95f);
        }

        private static GUIStyle WindowStyle() { Ensure(); return _window; }
        private static GUIStyle Rich() { Ensure(); return _rich; }
    }
}
