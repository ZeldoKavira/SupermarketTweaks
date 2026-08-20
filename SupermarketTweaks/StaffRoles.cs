using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Mirror;
using UnityEngine;

namespace SupermarketTweaks
{
    // Move staff between jobs automatically, so nobody stands idle.
    //
    // Roles are NPC_Info.taskPriority, read off UpdateEmployeeStats' own switch:
    //
    //   1 cashier   2 restocker   3 storage   4 security
    //   5 technician   6 ordering   7 manufacturing
    //
    // Two rules, both about the same waste:
    //
    //   Cashiers have nothing to do while the shop is shut, which is most of the morning and all
    //   night. They become restockers until it opens.
    //
    //   Storage staff have nothing to do when no boxes are on the floor, at any hour. Same fix,
    //   different trigger.
    //
    // Changes go through NPC_Manager.CmdChangeEmployeePriority, the same command the in-game
    // employee menu uses. It is requiresAuthority: false, but this only ever runs on the host: the
    // command writes priorityArray (a SyncVar) and calls AssignEmployeesPriorities, so two machines
    // issuing it would fight exactly the way duplicate pricing did.
    public static class StaffRolesConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Cashiers;
        internal static ConfigEntry<bool> StorageFallback;
        internal static ConfigEntry<float> IdleSeconds;
        internal static ConfigEntry<bool> Log;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Staff", "AutoRoles", false,
                "Move staff between jobs automatically. Pick who your cashiers are in the F1 panel.");
            Cashiers = cfg.Bind("Staff", "CashierNames", "",
                "Names of the staff who work the tills during opening hours, comma separated. " +
                "Edit this from the F1 panel rather than by hand.");
            StorageFallback = cfg.Bind("Staff", "StorageHelpsRestock", true,
                "Storage staff switch to restocking whenever there are no boxes on the floor, and " +
                "switch back as soon as a delivery lands.");
            IdleSeconds = cfg.Bind("Staff", "SwapDelaySeconds", 8f,
                new ConfigDescription("How long a condition must hold before anyone is moved. Stops " +
                    "staff flip-flopping as the last box is picked up and put down.",
                    new AcceptableValueRange<float>(1f, 120f)));
            Log = cfg.Bind("Staff", "LogRoleChanges", true,
                "Log every automatic role change.");
        }

        internal static bool On => Enabled != null && Enabled.Value;

        internal static List<string> CashierList()
        {
            var list = new List<string>();
            if (Cashiers == null || string.IsNullOrEmpty(Cashiers.Value)) return list;
            foreach (var part in Cashiers.Value.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        internal static void SetCashiers(List<string> names)
            => Cashiers.Value = string.Join(",", names.ToArray());
    }


    internal static class StaffRoles
    {
        internal const int Cashier = 1;
        internal const int Restocker = 2;
        internal const int Storage = 3;

        internal static string Status = "off";

        // What each employee was doing before we moved them, so they can be put back rather than
        // being assumed to have started as a cashier.
        private static readonly Dictionary<int, int> _original = new Dictionary<int, int>();

        internal static string NameOf(NPC_Manager mgr, int index)
        {
            try
            {
                var go = mgr.employeesArray[index];
                if (go == null) return null;
                var info = go.GetComponent<NPC_Info>();
                return info != null ? info.NPCName : null;
            }
            catch { return null; }
        }

        internal static int RoleOf(NPC_Manager mgr, int index)
        {
            try
            {
                var go = mgr.employeesArray[index];
                if (go == null) return -1;
                var info = go.GetComponent<NPC_Info>();
                return info != null ? info.taskPriority : -1;
            }
            catch { return -1; }
        }

        internal static void SetRole(NPC_Manager mgr, int index, int role, string why)
        {
            if (RoleOf(mgr, index) == role) return;

            mgr.CmdChangeEmployeePriority(index, role);
            if (StaffRolesConfig.Log.Value)
                Plugin.Log.LogInfo($"[Staff] {NameOf(mgr, index) ?? ("#" + index)} -> {RoleName(role)} ({why}).");
        }

        internal static string RoleName(int r)
        {
            switch (r)
            {
                case 1: return "cashier";
                case 2: return "restocker";
                case 3: return "storage";
                case 4: return "security";
                case 5: return "technician";
                case 6: return "ordering";
                case 7: return "manufacturing";
                default: return "unassigned";
            }
        }

        internal static void Remember(int index, int role)
        {
            if (!_original.ContainsKey(index)) _original[index] = role;
        }

        internal static int Recall(int index, int fallback)
        {
            int r;
            return _original.TryGetValue(index, out r) ? r : fallback;
        }

        internal static void Forget(int index) => _original.Remove(index);
    }

    public class StaffRolesDriver : MonoBehaviour
    {
        private float _next;
        private float _boxesEmptySince = -1f;
        private float _boxesPresentSince = -1f;
        private bool _wasOpen;
        private bool _knowOpen;

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            try
            {
                if (!StaffRolesConfig.On) { StaffRoles.Status = "off"; return; }

                // Host only, for the same reason pricing is: this writes a SyncVar through a
                // command, and two machines doing it would fight.
                if (NetworkClient.active && !NetworkServer.active)
                {
                    StaffRoles.Status = "host manages roles";
                    return;
                }

                var mgr = NPC_Manager.Instance;
                var data = GameData.Instance;
                if (mgr == null || data == null || mgr.employeesArray == null) return;

                HandleCashiers(mgr, data);
                HandleStorage(mgr);
            }
            catch (Exception e) { Plugin.Log.LogError($"[Staff] {e.Message}"); }
        }

        // Cashiers restock while the shop is shut.
        private void HandleCashiers(NPC_Manager mgr, GameData data)
        {
            bool open = data.isSupermarketOpen;
            if (!_knowOpen) { _knowOpen = true; _wasOpen = open; }

            var names = StaffRolesConfig.CashierList();
            if (names.Count == 0) { StaffRoles.Status = "no cashiers picked"; return; }

            // The same container the spawner counts against for its own cap, so this is exactly
            // "how many customers are in the shop".
            int customersLeft = 0;
            if (mgr.customersnpcParentOBJ != null)
                customersLeft = mgr.customersnpcParentOBJ.transform.childCount;

            for (int i = 0; i < mgr.employeesArray.Length; i++)
            {
                string name = StaffRoles.NameOf(mgr, i);
                if (name == null || !names.Contains(name)) continue;

                int role = StaffRoles.RoleOf(mgr, i);
                if (role < 0) continue;

                if (open)
                {
                    // Opening: back to the till.
                    StaffRoles.Forget(i);
                    StaffRoles.SetRole(mgr, i, StaffRoles.Cashier, "shop open");
                }
                else if (customersLeft == 0)
                {
                    // Closed AND empty. Waiting for the last customer matters: "closed" only means
                    // the doors stopped admitting people, and anyone already inside still has to
                    // queue and pay. Pulling the cashiers the moment the sign flips would strand
                    // them - and a customer who cannot find a free checkout turns thief outright.
                    StaffRoles.Remember(i, role);
                    StaffRoles.SetRole(mgr, i, StaffRoles.Restocker, "shop closed and empty");
                }
            }

            _wasOpen = open;
            StaffRoles.Status = open
                ? $"open - {names.Count} on tills"
                : customersLeft > 0
                    ? $"closed - {names.Count} still on tills, {customersLeft} customer(s) inside"
                    : $"closed and empty - {names.Count} restocking";
        }

        // Storage staff restock when there is nothing to put away.
        //
        // boxesOBJ is the parent of every box on the floor; GetRandomGroundBox returns null the
        // moment its childCount hits zero, which is precisely when a storage worker has no job.
        // Both directions are debounced, because the count crosses zero constantly as the last box
        // is picked up.
        private void HandleStorage(NPC_Manager mgr)
        {
            if (!StaffRolesConfig.StorageFallback.Value) return;
            if (mgr.boxesOBJ == null) return;

            bool anyBoxes = mgr.boxesOBJ.transform.childCount > 0;
            float now = Time.unscaledTime;
            float delay = Mathf.Max(1f, StaffRolesConfig.IdleSeconds.Value);

            if (anyBoxes)
            {
                _boxesEmptySince = -1f;
                if (_boxesPresentSince < 0f) _boxesPresentSince = now;
            }
            else
            {
                _boxesPresentSince = -1f;
                if (_boxesEmptySince < 0f) _boxesEmptySince = now;
            }

            var cashiers = StaffRolesConfig.CashierList();

            for (int i = 0; i < mgr.employeesArray.Length; i++)
            {
                string name = StaffRoles.NameOf(mgr, i);
                if (name == null) continue;

                // Someone rostered as a cashier is the other rule's business, not this one.
                if (cashiers.Contains(name)) continue;

                int role = StaffRoles.RoleOf(mgr, i);

                if (!anyBoxes && role == StaffRoles.Storage && _boxesEmptySince > 0f
                    && now - _boxesEmptySince >= delay)
                {
                    StaffRoles.Remember(i, StaffRoles.Storage);
                    StaffRoles.SetRole(mgr, i, StaffRoles.Restocker, "no boxes on the floor");
                }
                else if (anyBoxes && role == StaffRoles.Restocker
                         && StaffRoles.Recall(i, -1) == StaffRoles.Storage)
                {
                    // Going BACK is not debounced, unlike leaving.
                    //
                    // Waiting here was the bug: boxes are often cleared inside the delay - by the
                    // player, or by a storage worker who wasn't converted - which reset
                    // _boxesPresentSince and meant the timer never elapsed, so nobody ever returned.
                    // Idling is worth debouncing; a delivery arriving is a real event, and there is
                    // no flapping risk in this direction.
                    //
                    // The memory is deliberately NOT cleared here. If the command doesn't land -
                    // the employee slot briefly null, the role rejected - this retries next tick
                    // instead of stranding them as a restocker forever, which is what forgetting
                    // before confirming used to do.
                    StaffRoles.SetRole(mgr, i, StaffRoles.Storage, "boxes waiting");
                }
                else if (role == StaffRoles.Storage && StaffRoles.Recall(i, -1) == StaffRoles.Storage)
                {
                    // Observed back in the job we moved them out of, so the note has served its
                    // purpose and can go.
                    StaffRoles.Forget(i);
                }
            }
        }
    }
}
