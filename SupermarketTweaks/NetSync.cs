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
    // kicking an innocent one. Both players are expected to have the mod; SyncSettings turns it off
    // for joining a vanilla host.
    public struct SmtMessage : NetworkMessage
    {
        public string Kind;
        public string Payload;
    }

    public static class NetSyncConfig
    {
        internal static ConfigEntry<bool> SyncSettings;
        internal static ConfigEntry<bool> Tolerant;

        public static void Init(ConfigFile cfg)
        {
            SyncSettings = cfg.Bind("Multiplayer", "SyncSettings", true,
                "Share pricing settings between host and clients. Both players need the mod - turn " +
                "this OFF before joining a host who doesn't have it, or you will be disconnected.");
            Tolerant = cfg.Bind("Multiplayer", "TolerateUnknownPackets", true,
                "Stop Mirror disconnecting us over a message we don't recognise. Without this, an " +
                "older client drops out the moment a newer host sends something it has never seen.");
        }

        internal static bool On => SyncSettings != null && SyncSettings.Value;
    }

    internal static class NetSync
    {
        private const string Hello    = "SMT/hello";
        private const string Settings = "SMT/settings";
        private const string Alarm    = "SMT/alarm";

        internal static string Status = "not connected";

        private static bool _clientRegistered, _serverRegistered;
        private static bool _saidHello;

        // Clients that have answered, so the host never sends to someone who might be vanilla.
        private static readonly HashSet<int> _moddedClients = new HashSet<int>();

        internal static void Tick()
        {
            try
            {
                if (NetSyncConfig.Tolerant != null && NetSyncConfig.Tolerant.Value) MakeTolerant();

                if (!NetSyncConfig.On) { Status = "off"; return; }

                if (NetworkServer.active) EnsureServerHandlers();
                if (NetworkClient.active) EnsureClientHandlers();

                // A pure client introduces itself once per connection. The host is its own client
                // in host mode, and has nothing to tell itself.
                if (NetworkClient.isConnected && !NetworkServer.active)
                {
                    if (!_saidHello)
                    {
                        _saidHello = true;
                        NetworkClient.Send(new SmtMessage
                        {
                            Kind = Hello,
                            Payload = typeof(NetSync).Assembly.GetName().Version.ToString()
                        }, 0);
                        Status = "said hello, waiting for host settings";
                    }
                }
                else if (!NetworkClient.isConnected)
                {
                    _saidHello = false;
                    _moddedClients.Clear();
                    if (Status != "off") Status = "not connected";
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[NetSync] {e.Message}"); }
        }

        // Only protects US. A vanilla player still disconnects on our packets, which is exactly why
        // the client has to speak first.
        private static bool _tolerantDone;

        private static void MakeTolerant()
        {
            if (_tolerantDone) return;
            _tolerantDone = true;
            NetworkClient.exceptionsDisconnect = false;
            NetworkServer.exceptionsDisconnect = false;
            Plugin.Log.LogInfo("[NetSync] Unknown-packet disconnects disabled for this side.");
        }

        private static void EnsureClientHandlers()
        {
            if (_clientRegistered) return;
            _clientRegistered = true;
            // requireAuthentication: false - this arrives before the game considers us settled.
            NetworkClient.RegisterHandler<SmtMessage>(OnClientMessage, false);
        }

        private static void EnsureServerHandlers()
        {
            if (_serverRegistered) return;
            _serverRegistered = true;
            NetworkServer.RegisterHandler<SmtMessage>(OnServerMessage, false);
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
                             $"{AutoPriceConfig.Enabled.Value}";
                if (_lastSent == null) { _lastSent = now; return; }
                if (now == _lastSent) return;

                _lastSent = now;
                NetSync.BroadcastSettings();
            }
            catch (Exception e) { Plugin.Log.LogError($"[NetSync] {e.Message}"); }
        }
    }
}
