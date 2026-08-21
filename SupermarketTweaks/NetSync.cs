using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Mirror;
using UnityEngine;

namespace SupermarketTweaks
{
    // Settings sync between the host's copy of the mod and the clients'.
    //
    // ONE Mirror message type, forever
    // -------------------------------
    // Mirror derives a message id from the type name, and an id nobody registered is fatal in this
    // build:
    //
    //   Debug.LogWarning($"Unknown message id: {messageId} for connection: {connection}...");
    //   return false;                       // -> caller disconnects, exceptionsDisconnect = true
    //
    // So if a newer version added a second message type, every older client would be DISCONNECTED
    // the first time the host used it - and it would look like a random network drop, not a mod
    // problem. Hence a single envelope: new features become new Kind strings inside the same
    // message, the id never changes, and an out-of-date client silently ignores what it doesn't
    // recognise. Never add a second NetworkMessage type to this mod.
    //
    // Kinds are prefixed "SMT/" so anything unrecognised is obviously ours and can be dropped
    // without guessing.
    //
    // Who speaks first
    // ----------------
    // Neither side can safely send blind: whoever lacks the mod is the one that gets disconnected,
    // and we cannot protect a vanilla player from our own packet. The client therefore opens, so
    // that a mod-vs-vanilla mismatch costs the modded player their own connection rather than
    // kicking an innocent one. Both players are expected to have the mod - joining a host without
    // it will disconnect you.
    public struct SmtMessage : NetworkMessage
    {
        public string Kind;
        public string Payload;
    }

    public static class NetSyncConfig
    {
        internal static ConfigEntry<bool> Tolerant;

        public static void Init(ConfigFile cfg)
        {
            Tolerant = cfg.Bind("Multiplayer", "TolerateUnknownPackets", true,
                "Stop Mirror disconnecting us over a message we don't recognise. Without this, an " +
                "older client drops out the moment a newer host sends something it has never seen.");
        }

        // Deliberately NOT a setting.
        //
        // It was one, and that was a mistake: host-side pricing, staff roles and the alarm hold all
        // depend on this channel, so switching it off does not disable a feature - it silently
        // breaks several, with no error on either side. A requirement dressed up as a preference is
        // just a way to configure the mod into being broken.
        //
        // The one case it used to guard - a modded client joining a host WITHOUT the mod, which
        // disconnects the client - is documented instead. Both players are expected to have the mod.
        internal static bool On => true;
    }

    internal static class NetSync
    {
        private const string Hello    = "SMT/hello";
        private const string Settings = "SMT/settings";
        private const string Alarm    = "SMT/alarm";

        internal static string Status = "not connected";

        private static bool _clientRegistered, _serverRegistered;

        // Handshake state. _acked flips as soon as the host says anything back, which is the only
        // proof the two sides can actually talk.
        private static bool _acked;
        private static int _helloCount;
        private static float _nextHello;
        private const float HelloRetrySeconds = 5f;

        // Clients that have answered, so the host never sends to someone who might be vanilla.
        private static readonly HashSet<int> _moddedClients = new HashSet<int>();


        internal static void Tick()
        {
            try
            {
                EnsureSerializers();
                if (NetSyncConfig.Tolerant != null && NetSyncConfig.Tolerant.Value) MakeTolerant();

                // Registered before anything else can return early. This used to sit behind a
                // config check, and a host with that check off had no handler at all - so a
                // client's hellos landed on an id nobody listened for and vanished, with both logs
                // looking healthy. Nothing may come between being active and being able to listen.
                if (NetworkServer.active) EnsureServerHandlers();
                if (NetworkClient.active) EnsureClientHandlers();

                if (NetworkServer.active)
                {
                    int others = NetworkServer.connections.Count - 1;   // the host is one of them
                    Status = _moddedClients.Count > 0
                        ? $"host - {_moddedClients.Count} modded client(s)"
                        : others > 0
                            ? $"host - {others} client(s), NONE with the mod yet"
                            : "host - no clients";
                }

                // A pure client introduces itself. The host is its own client in host mode and has
                // nothing to tell itself.
                //
                // This RETRIES until the host answers, and that is the whole point. Sending once
                // and hoping was the bug: the hello can be thrown away - the host's handler is
                // registered from its own Tick, so a client that connects in the wrong second sends
                // into a socket with no handler for that id - and losing it once killed the entire
                // sync for the session, silently, with the host never knowing a modded client was
                // there. Nothing else recovers it, because the host only ever talks to clients that
                // have said hello.
                if (NetworkClient.isConnected && !NetworkServer.active)
                {
                    if (!_acked && Time.unscaledTime >= _nextHello)
                    {
                        _nextHello = Time.unscaledTime + HelloRetrySeconds;
                        _helloCount++;

                        NetworkClient.Send(new SmtMessage
                        {
                            Kind = Hello,
                            Payload = typeof(NetSync).Assembly.GetName().Version.ToString()
                        }, 0);

                        Status = $"waiting for host (hello x{_helloCount})";
                        if (_helloCount == 1 || _helloCount % 5 == 0)
                            Plugin.Log.LogInfo($"[NetSync] Told the host we have the mod " +
                                               $"(attempt {_helloCount}); no reply yet.");
                    }
                }
                else if (!NetworkClient.isConnected)
                {
                    _acked = false;
                    _helloCount = 0;
                    _nextHello = 0f;
                    _moddedClients.Clear();
                    Status = "not connected";
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[NetSync] {e.Message}"); }
        }

        // Teach Mirror how to put SmtMessage on the wire.
        //
        // This is the piece whose absence broke everything, and it fails silently. Mirror's Weaver
        // generates a Writer<T>.write and Reader<T>.read for every NetworkMessage at COMPILE time -
        // but a BepInEx plugin is never weaved, so for our struct both fields are null:
        //
        //   Action<NetworkWriter, T> write = Writer<T>.write;   // null
        //   if (write == null) Debug.LogError($"No writer found for {typeof(T)}...");
        //
        // The message then goes out as a bare 2-byte id with no payload, the far side cannot read
        // it, and the only complaint is a Unity Debug.LogError - which BepInEx never captured here
        // ("Unable to start Unity log writer"). Hence: client sending happily, host receiving
        // nothing, and not one error on either side.
        //
        // Both fields are public statics, so they can simply be filled in. The order of writes must
        // match the order of reads exactly; there is no length prefix to save us.
        private static bool _serializersDone;

        private static void EnsureSerializers()
        {
            if (_serializersDone) return;
            _serializersDone = true;

            Writer<SmtMessage>.write = (writer, msg) =>
            {
                writer.WriteString(msg.Kind ?? "");
                writer.WriteString(msg.Payload ?? "");
            };

            Reader<SmtMessage>.read = reader => new SmtMessage
            {
                Kind = reader.ReadString(),
                Payload = reader.ReadString(),
            };

            Plugin.Log.LogInfo("[NetSync] Registered SmtMessage serializers (the mod is not weaved, " +
                               "so Mirror has none of its own).");
        }

        // Only protects US. A vanilla player still disconnects on our packets, which is exactly why
        // the client has to speak first.
        private static bool _tolerantLogged;

        private static void MakeTolerant()
        {
            // Set every tick, not once. These are statics Mirror owns, and the handler wrapper
            // captures the value at REGISTRATION time - so a latch here could leave a handler
            // registered with the old, disconnect-happy behaviour after a session restart.
            NetworkClient.exceptionsDisconnect = false;
            NetworkServer.exceptionsDisconnect = false;

            if (_tolerantLogged) return;
            _tolerantLogged = true;
            Plugin.Log.LogInfo("[NetSync] Unknown-packet disconnects disabled for this side.");
        }

        // Re-registered every tick, deliberately, and with ReplaceHandler rather than
        // RegisterHandler.
        //
        // Registering once was the bug that broke the whole feature. Mirror wipes the table on
        // shutdown -
        //
        //   connections.Clear(); connectionsCopy.Clear(); handlers.Clear();   // NetworkServer
        //
        // - so the moment a session ends the handler is gone, while a latched "already registered"
        // flag says otherwise. The host then silently ignores every hello forever, which is exactly
        // what the client log showed: 300 attempts, no reply.
        //
        // ReplaceHandler because RegisterHandler logs a warning when the id is already present, and
        // this runs once a second. It also matters that the wrapper captures exceptionsDisconnect
        // AT REGISTRATION TIME - re-registering after MakeTolerant is what makes the tolerance
        // actually apply.
        private static void EnsureClientHandlers()
        {
            // requireAuthentication: false - this arrives before the game considers us settled.
            NetworkClient.ReplaceHandler<SmtMessage>(OnClientMessage, false);
            _clientRegistered = true;
        }

        private static void EnsureServerHandlers()
        {
            NetworkServer.ReplaceHandler<SmtMessage>(OnServerMessage, false);
            _serverRegistered = true;
        }

        private static void OnServerMessage(NetworkConnectionToClient conn, SmtMessage msg)
        {
            try
            {
                switch (msg.Kind)
                {
                    case Hello:
                        // Now proven to have the mod, so it is safe to send to them.
                        _moddedClients.Add(conn.connectionId);
                        Plugin.Log.LogInfo($"[NetSync] Client {conn.connectionId} has the mod (v{msg.Payload}).");
                        SendSettingsTo(conn);
                        // Catch them up if a robbery is already in progress.
                        if (AntiTheftConfig.SlowOn && AntiTheft.AlarmActive)
                            conn.Send(new SmtMessage { Kind = Alarm, Payload = "1" }, 0);
                        break;

                    default:
                        // A newer client asking for something this host has never heard of. Ignore.
                        if (msg.Kind != null && msg.Kind.StartsWith("SMT/"))
                            Plugin.Log.LogDebug($"[NetSync] Ignoring unknown kind from client: {msg.Kind}");
                        break;
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[NetSync] server: {e.Message}"); }
        }

        private static void OnClientMessage(SmtMessage msg)
        {
            try
            {
                if (!_acked)
                {
                    _acked = true;
                    Plugin.Log.LogInfo("[NetSync] Host answered; settings and speed will follow it.");
                }

                switch (msg.Kind)
                {
                    case Settings:
                        ApplySettings(msg.Payload);
                        break;

                    case Alarm:
                        // Clients cannot work this out for themselves: CheckThief only runs on the
                        // server (its trigger is enabled in OnStartServer), and productsIDCarrying
                        // is not a SyncVar, so a client can see neither the alarm nor the thief
                        // being emptied.
                        AntiTheft.SetRemoteAlarm(msg.Payload == "1");
                        break;

                    default:
                        // Older client, newer host. Dropping this is the entire point.
                        if (msg.Kind != null && msg.Kind.StartsWith("SMT/"))
                            Plugin.Log.LogDebug($"[NetSync] Ignoring unknown kind from host: {msg.Kind}");
                        break;
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[NetSync] client: {e.Message}"); }
        }

        // Host settings are the ones that matter, because the host is the only side that prices
        // automatically. Clients get them so the F1 panel tells the truth and a manual sweep uses
        // the same numbers.
        private static string Serialize()
        {
            return string.Join(";", new[]
            {
                "percent=" + AutoPriceConfig.Percent.Value,
                "round="   + (AutoPriceConfig.RoundDown.Value ? 1 : 0),
                "newday="  + (AutoPriceConfig.OnNewDay.Value ? 1 : 0),
                "newprod=" + (AutoPriceConfig.OnNewProduct.Value ? 1 : 0),
                "enabled=" + (AutoPriceConfig.Enabled.Value ? 1 : 0),

                // Speed has to travel too. timeScale is local, but the HOST's copy drives the
                // shared simulation while a client's drives only its own view - so a host at 3x
                // and a client at 1x means the world moves at triple speed around a player whose
                // own character does not. Matching clocks is the only coherent state.
                "speedon=" + (GameSpeedConfig.Enabled.Value ? 1 : 0),
                "speed="   + GameSpeedConfig.Speed.Value.ToString("0.###",
                                 System.Globalization.CultureInfo.InvariantCulture),

                // The super tier travels as its own pair rather than folded into "speed", so a
                // client's panel shows the same state the host is in - and so an older client that
                // has never heard of super still tracks the normal speed instead of nothing.
                "super="      + (GameSpeedConfig.SuperActive.Value ? 1 : 0),
                "superspeed=" + GameSpeedConfig.SuperSpeed.Value.ToString("0.###",
                                    System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        private static void ApplySettings(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            foreach (var part in payload.Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;

                // Unknown keys are skipped for the same reason unknown kinds are: a newer host will
                // send fields this build has never heard of.
                switch (kv[0])
                {
                    case "percent": if (int.TryParse(kv[1], out var p)) AutoPriceConfig.Percent.Value = Mathf.Clamp(p, 50, 250); break;
                    case "round":   AutoPriceConfig.RoundDown.Value    = kv[1] == "1"; break;
                    case "newday":  AutoPriceConfig.OnNewDay.Value     = kv[1] == "1"; break;
                    case "newprod": AutoPriceConfig.OnNewProduct.Value = kv[1] == "1"; break;
                    case "enabled": AutoPriceConfig.Enabled.Value      = kv[1] == "1"; break;
                    case "speedon": GameSpeedConfig.Enabled.Value      = kv[1] == "1"; break;
                    case "speed":
                        // InvariantCulture both ways: a host on a comma-decimal locale would
                        // otherwise send "3,5" and every other client would fail to parse it.
                        if (float.TryParse(kv[1], System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out var sp))
                            GameSpeedConfig.Speed.Value = Mathf.Clamp(sp, 0.25f, 10f);
                        break;
                    case "super": GameSpeedConfig.SuperActive.Value = kv[1] == "1"; break;
                    case "superspeed":
                        if (float.TryParse(kv[1], System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out var ss))
                            GameSpeedConfig.SuperSpeed.Value = Mathf.Clamp(ss, 1f, 20f);
                        break;
                }
            }

            Status = "using host settings";
            Plugin.Log.LogInfo($"[NetSync] Applied host pricing settings ({payload}).");
        }

        private static void SendSettingsTo(NetworkConnectionToClient conn)
        {
            conn.Send(new SmtMessage { Kind = Settings, Payload = Serialize() }, 0);
        }

        internal static void BroadcastAlarm(bool active)
        {
            try
            {
                if (!NetSyncConfig.On || !NetworkServer.active || _moddedClients.Count == 0) return;

                var msg = new SmtMessage { Kind = Alarm, Payload = active ? "1" : "0" };
                foreach (var kv in NetworkServer.connections)
                    if (_moddedClients.Contains(kv.Key)) kv.Value.Send(msg, 0);
            }
            catch (Exception e) { Plugin.Log.LogError($"[NetSync] alarm: {e.Message}"); }
        }

        // Called when the host changes a pricing setting, so clients don't sit on a stale copy.
        internal static void BroadcastSettings()
        {
            try
            {
                if (!NetSyncConfig.On || !NetworkServer.active || _moddedClients.Count == 0) return;

                var msg = new SmtMessage { Kind = Settings, Payload = Serialize() };
                foreach (var kv in NetworkServer.connections)
                {
                    // Only to clients that said hello. Anyone else may be vanilla, and sending to
                    // them would disconnect them.
                    if (_moddedClients.Contains(kv.Key)) kv.Value.Send(msg, 0);
                }
                Status = $"host, {_moddedClients.Count} modded client(s)";
            }
            catch (Exception e) { Plugin.Log.LogError($"[NetSync] broadcast: {e.Message}"); }
        }
    }

    public class NetSyncDriver : MonoBehaviour
    {
        private float _next;
        private string _lastSent;
        private bool _lastAlarm;
        private bool _knowAlarm;

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            NetSync.Tick();

            try
            {
                // Push changes rather than polling from the client side: cheap, and it means a
                // client's view updates the moment the host touches a slider.
                if (!NetworkServer.active || !NetSyncConfig.On) return;

                // Alarm transitions go out immediately - a client held at 1x needs the all-clear
                // promptly, and this is the only way it can learn of either edge.
                bool alarm = AntiTheftConfig.SlowOn && AntiTheft.AlarmActive;
                if (!_knowAlarm) { _knowAlarm = true; _lastAlarm = alarm; }
                else if (alarm != _lastAlarm)
                {
                    _lastAlarm = alarm;
                    NetSync.BroadcastAlarm(alarm);
                }

                string now = $"{AutoPriceConfig.Percent.Value}|{AutoPriceConfig.RoundDown.Value}|" +
                             $"{AutoPriceConfig.OnNewDay.Value}|{AutoPriceConfig.OnNewProduct.Value}|" +
                             $"{AutoPriceConfig.Enabled.Value}|" +
                             $"{GameSpeedConfig.Enabled.Value}|{GameSpeedConfig.Speed.Value}|" +
                             $"{GameSpeedConfig.SuperActive.Value}|{GameSpeedConfig.SuperSpeed.Value}";
                if (_lastSent == null) { _lastSent = now; return; }
                if (now == _lastSent) return;

                _lastSent = now;
                NetSync.BroadcastSettings();
            }
            catch (Exception e) { Plugin.Log.LogError($"[NetSync] {e.Message}"); }
        }
    }
}
