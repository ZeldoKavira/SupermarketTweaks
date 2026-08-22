using System;
using System.Reflection;
using BepInEx.Configuration;
using Mirror;
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
        internal static ConfigEntry<bool> SuperActive;
        internal static ConfigEntry<float> SuperSpeed;
        internal static ConfigEntry<KeyboardShortcut> SuperKey;

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
                "Turns the normal speed boost on and off.");
            SuperActive = cfg.Bind("Speed", "SuperSpeedActive", false,
                "Whether super speed is currently on. Toggled with the super speed key; kept here " +
                "so the F1 panel shows it and it survives a reload.");
            SuperSpeed = cfg.Bind("Speed", "SuperSpeed", 10f,
                new ConfigDescription("The second, faster tier. Overrides the normal speed while " +
                    "active. High values cost frames and make theft very hard to react to.",
                    new AcceptableValueRange<float>(1f, 20f)));
            SuperKey = cfg.Bind("Speed", "SuperSpeedKey", new KeyboardShortcut(KeyCode.F6),
                "Toggles super speed. Turning it off returns to whatever the normal boost was, " +
                "rather than to 1x.");
        }

        internal static bool On => Enabled != null && Enabled.Value;
        internal static bool Super => SuperActive != null && SuperActive.Value;

        // What the player asked for, ignoring any temporary override.
        //
        // Super wins over the normal boost rather than multiplying with it, so the two keys are
        // independent: F6 off always lands back on whatever F5 was set to, without having to
        // remember which order they were pressed in.
        internal static float Target
        {
            get
            {
                if (Super) return Mathf.Clamp(SuperSpeed.Value, 1f, 20f);
                return On ? Mathf.Clamp(Speed.Value, 0.25f, 10f) : 1f;
            }
        }

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
        private static FieldInfo _readyField;
        private static bool _lookedForReady;
        private static bool _warnedNoReady;
        private bool _wasReady;
        private int _lastDay = int.MinValue;
        private bool _lastAlarm;

        internal static string Status = "off";

        // Do not touch the clock until the level has finished setting itself up.
        //
        // This cost an afternoon. Builder_Main.RetrieveInitialBehaviours waits two seconds before
        // caching Camera.main, FirstPersonController.Instance and GameData.Instance, and only then
        // sets initialConfiguration = true. WaitForSeconds is SCALED time, so at 3x that two second
        // grace period is over in two thirds of a second - well before those objects exist. The
        // coroutine then dereferences a null, dies, and initialConfiguration stays false forever:
        //
        //   NullReferenceException
        //     at Builder_Main+<RetrieveInitialBehaviours>d__89.MoveNext ()
        //
        // and Builder_Main.Update opens with "if (!initialConfiguration) return;", so Tab silently
        // stops opening the build menu for the rest of the session. Nothing in our own log, because
        // the exception is the game's.
        //
        // That same flag is the exact readiness signal we need, so it is read directly rather than
        // approximated with a timer. No Builder_Main in the scene at all means the main menu, where
        // there is nothing worth speeding up anyway.
        // The Builder_Main reference is cached because this is called up to three times a FRAME -
        // once per hotkey guard and once in the throttled body - and FindObjectOfType walks every
        // object in the scene. Written without a second thought, it was the single most expensive
        // thing this mod did.
        //
        // Only the search is cached, not the answer: the flag is re-read every call, so readiness
        // is still current. A destroyed builder (level change) makes the reference null and the
        // search runs again, at most once a second.
        private static Builder_Main _builder;
        private static float _nextBuilderSearch;

        private static bool LevelReady()
        {
            if (_builder == null)
            {
                if (Time.unscaledTime < _nextBuilderSearch) return false;
                _nextBuilderSearch = Time.unscaledTime + 1f;

                _builder = UnityEngine.Object.FindObjectOfType<Builder_Main>();
            }
            var builder = _builder;
            if (builder == null) return false;

            if (!_lookedForReady)
            {
                _lookedForReady = true;
                _readyField = typeof(Builder_Main).GetField("initialConfiguration",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            if (_readyField == null)
            {
                // Renamed by a game update. Falling back to "the objects it needs all exist" keeps
                // the guard rather than silently dropping it - the point is not to run early.
                if (!_warnedNoReady)
                {
                    _warnedNoReady = true;
                    Plugin.Log.LogWarning("[GameSpeed] Builder_Main.initialConfiguration not found; " +
                                          "falling back to a looser readiness check.");
                }
                // FirstPersonController lives outside Assembly-CSharp, so it is not part of this
                // check - GameData and the camera are enough to say the level exists.
                return GameData.Instance != null && Camera.main != null;
            }

            return (bool)_readyField.GetValue(builder);
        }

        private void Update()
        {
            try
            {
                // Both toggles apply immediately, so both need the readiness gate too.
                if (GameSpeedConfig.SuperKey != null && GameSpeedConfig.SuperKey.Value.IsDown()
                    && LevelReady())
                {
                    if (NetworkClient.active && !NetworkServer.active)
                    {
                        Plugin.Log.LogInfo("[GameSpeed] The host sets the speed; ask them to change it.");
                    }
                    else
                    {
                        GameSpeedConfig.SuperActive.Value = !GameSpeedConfig.SuperActive.Value;
                        Apply(force: true);
                    }
                }

                if (GameSpeedConfig.ToggleKey != null && GameSpeedConfig.ToggleKey.Value.IsDown()
                    && LevelReady())
                {
                    // On a client the host owns the clock: it drives the shared simulation, and it
                    // rebroadcasts on every change, so a local toggle would be silently undone the
                    // next time the host touched anything. Refusing outright is at least honest.
                    if (NetworkClient.active && !NetworkServer.active && NetSyncConfig.On)
                    {
                        Plugin.Log.LogInfo("[GameSpeed] The host sets the speed; ask them to change it.");
                    }
                    else
                    {
                        GameSpeedConfig.Enabled.Value = !GameSpeedConfig.Enabled.Value;
                        Apply(force: true);
                    }
                }

                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + 0.5f;

                if (_baseFixedDelta < 0f) _baseFixedDelta = Time.fixedDeltaTime;

                if (!LevelReady())
                {
                    // Hand the clock back if we are mid-transition, so a level that starts loading
                    // while the boost is on does not inherit it.
                    if (_wasReady || _lastApplied > 1f)
                    {
                        Time.timeScale = 1f;
                        if (_baseFixedDelta > 0f) Time.fixedDeltaTime = _baseFixedDelta;
                        _lastApplied = -1f;
                        _lastDay = int.MinValue;
                    }
                    _wasReady = false;
                    Status = "waiting for the level to finish loading";
                    return;
                }
                _wasReady = true;

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
                : GameSpeedConfig.Super
                    ? $"SUPER {target:0.##}x (timestep {Time.fixedDeltaTime:0.###})"
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
