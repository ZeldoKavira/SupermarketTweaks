using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SupermarketTweaks
{
    // Game speed that survives the end of the day.
    //
    // The game forces the clock back to normal in two places - RpcEndDay on every client, and
    // CmdEndDayFromButton on the server:
    //
    //   if (Time.timeScale > 1.1f) { Time.timeScale = 1f; }
    //
    // which is why a plain "set timeScale" mod loses its setting every single night. This holds the
    // value instead of setting it once, so the reset is undone as soon as the new day starts.
    //
    // It also does the thing the simple version misses: Unity runs FixedUpdate every
    // fixedDeltaTime of SCALED time, so raising timeScale alone multiplies the number of physics
    // steps per real second - 3x speed means 3x the physics work, on top of a busy shop. The game's
    // own speed control compensates, in AdjustTimeScale:
    //
    //   Time.fixedDeltaTime = 0.02f * Time.timeScale;
    //
    // and this matches that. The day clock advances by fixedDeltaTime (NetworktimeOfDay += 1f /
    // timeFactor * Time.fixedDeltaTime / 60f), so scaling both keeps the day moving at the speed
    // you asked for while the physics load stays flat.
    public static class GameSpeedConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Speed;
        internal static ConfigEntry<bool> ReapplyOnNewDay;
        internal static ConfigEntry<bool> ScaleFixedTimestep;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Speed", "GameSpeedEnabled", false,
                "Run the game faster than normal.");
            Speed = cfg.Bind("Speed", "GameSpeed", 3f,
                new ConfigDescription("How much faster to run. 1 is normal.",
                    new AcceptableValueRange<float>(0.25f, 10f)));
            ReapplyOnNewDay = cfg.Bind("Speed", "ReapplyOnNewDay", true,
                "Put the speed back after the game resets it at the end of each day. This is the " +
                "whole point - without it the setting lasts exactly one day.");
            ScaleFixedTimestep = cfg.Bind("Speed", "ScaleFixedTimestep", true,
                "Also scale the physics timestep, the way the game's own speed control does. " +
                "Without this, running at 3x does 3x the physics work per second and will cost you " +
                "frames in a busy shop.");
            ToggleKey = cfg.Bind("Speed", "ToggleKey", new KeyboardShortcut(KeyCode.F5),
                "Turns the speed boost on and off.");
        }

        internal static bool On => Enabled != null && Enabled.Value;

        // What the player asked for, ignoring any temporary override.
        internal static float Target => On ? Mathf.Clamp(Speed.Value, 0.25f, 10f) : 1f;

        // What we actually apply. The anti-theft alarm forces normal speed so a robbery can be
        // dealt with at a human pace - it is the one event where fast-forward actively costs you
        // money, and you cannot react to it in a third of the time.
        internal static float Effective =>
            (AntiTheftConfig.SlowOn && AntiTheft.AnyAlarm) ? 1f : Target;
    }

    public class GameSpeedDriver : MonoBehaviour
    {
        // Unity's default. Captured rather than assumed, so restoring cannot drift if the game
        // ships a different project setting.
        private static float _baseFixedDelta = -1f;

        private float _next;
        private float _lastApplied = -1f;
        private int _lastDay = int.MinValue;
        private bool _lastAlarm;

        internal static string Status = "off";

        private void Update()
        {
            try
            {
                if (GameSpeedConfig.ToggleKey != null && GameSpeedConfig.ToggleKey.Value.IsDown())
                {
                    GameSpeedConfig.Enabled.Value = !GameSpeedConfig.Enabled.Value;
                    Apply(force: true);
                }

                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + 0.5f;

                if (_baseFixedDelta < 0f) _baseFixedDelta = Time.fixedDeltaTime;

                // The end-of-day reset writes timeScale directly, so the only reliable way to catch
                // it is to notice the value is no longer what we set.
                bool drifted = !Mathf.Approximately(Time.timeScale, GameSpeedConfig.Effective);

                var data = GameData.Instance;
                bool newDay = false;
                if (data != null)
                {
                    if (_lastDay == int.MinValue) _lastDay = data.gameDay;
                    else if (data.gameDay != _lastDay) { _lastDay = data.gameDay; newDay = true; }
                }

                // The alarm starting or ending has to take effect whatever ReapplyOnNewDay says -
                // that setting is about the end-of-day reset, not about this.
                bool alarm = AntiTheftConfig.SlowOn && AntiTheft.AnyAlarm;
                bool alarmChanged = alarm != _lastAlarm;
                _lastAlarm = alarm;

                if (drifted && (alarmChanged || GameSpeedConfig.ReapplyOnNewDay.Value
                                || newDay || _lastApplied < 0f))
                    Apply(force: alarmChanged);
            }
            catch (Exception e) { Plugin.Log.LogError($"[GameSpeed] {e.Message}"); }
        }

        private void Apply(bool force)
        {
            float target = GameSpeedConfig.Effective;

            // Never fight a real pause. The game stops time for menus and events, and stamping our
            // value over a zero timeScale would resume the world underneath a paused player.
            if (!force && Time.timeScale < 0.05f) return;

            Time.timeScale = target;

            if (_baseFixedDelta > 0f)
                Time.fixedDeltaTime = GameSpeedConfig.ScaleFixedTimestep.Value
                    ? _baseFixedDelta * target
                    : _baseFixedDelta;

            _lastApplied = target;

            bool held = AntiTheftConfig.SlowOn && AntiTheft.AnyAlarm;
            Status = held
                ? $"1x - held by alarm (want {GameSpeedConfig.Target:0.##}x)"
                : GameSpeedConfig.On
                    ? $"{target:0.##}x (timestep {Time.fixedDeltaTime:0.###})"
                    : "off";
        }

        private void OnDestroy()
        {
            // Leaving a modified timestep behind would follow the player into their next session.
            try
            {
                Time.timeScale = 1f;
                if (_baseFixedDelta > 0f) Time.fixedDeltaTime = _baseFixedDelta;
            }
            catch { }
        }
    }
}
