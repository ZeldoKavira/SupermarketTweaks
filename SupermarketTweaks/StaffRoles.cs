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
    // Every rule is about the same waste - an employee standing still - and each has its own test
    // for when that is true: the shop is shut and empty (cashiers, security), nothing is broken
    // (technicians), no boxes are on the floor (storage staff), no orders are queued (order
    // fillers).
    //
    // Where a borrowed employee GOES is a separate question from whether they are idle, and it is
    // answered fresh every tick by HelperRole: storage while boxes are on the floor, restocking
    // once it is clear. Sending everyone to restocking unconditionally was wrong whenever a
    // delivery had just landed - shelves are refilled from storage, so a restocker with an empty
    // back room has nothing to carry, while the boxes nobody is putting away are the real
    // bottleneck. Borrowed staff now follow the work between the two as it moves.
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
        internal static ConfigEntry<bool> SecurityHelps;
        internal static ConfigEntry<bool> TechHelps;
        internal static ConfigEntry<bool> TechHaulsBales;
        internal static ConfigEntry<bool> OrderingHelps;
        internal static ConfigEntry<float> IdleSeconds;
        internal static ConfigEntry<bool> Log;
        internal static ConfigEntry<string> RememberedRoles;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Staff", "AutoRoles", false,
                "Move staff between jobs automatically. Pick who your cashiers are in the F1 panel.");
            Cashiers = cfg.Bind("Staff", "CashierNames", "",
                "Names of the staff who work the tills during opening hours, comma separated. " +
                "Edit this from the F1 panel rather than by hand.");
            SecurityHelps = cfg.Bind("Staff", "SecurityHelpsRestock", true,
                "Security switch to restocking once the shop is closed AND empty, and back to " +
                "security when it opens - same rule as the cashiers, since there is nobody left " +
                "to steal from either.");
            TechHelps = cfg.Bind("Staff", "TechHelpsRestock", true,
                "Technicians switch to storage or restocking whenever they have nothing to do, and " +
                "back the moment work appears.");
            TechHaulsBales = cfg.Bind("Staff", "TechCountsBalesAsWork", true,
                "Count waiting cardboard bales as technician work, so they are not lent out with " +
                "bales stacking up. Hauling a bale pays 18x the recycle factor against 1.5x for a " +
                "loose box, and one bale is ten boxes, so it is the better job to leave them on.");
            OrderingHelps = cfg.Bind("Staff", "OrderingHelpsRestock", true,
                "Order fillers switch to storage or restocking whenever the packaging queue is " +
                "empty, and back the moment an order comes in. Anyone halfway through packing an " +
                "order is left alone.");
            StorageFallback = cfg.Bind("Staff", "StorageHelpsRestock", true,
                "Storage staff switch to restocking whenever there are no boxes on the floor, and " +
                "switch back as soon as a delivery lands.");
            IdleSeconds = cfg.Bind("Staff", "SwapDelaySeconds", 8f,
                new ConfigDescription("How long a condition must hold before anyone is moved. Stops " +
                    "staff flip-flopping as the last box is picked up and put down.",
                    new AcceptableValueRange<float>(1f, 120f)));
            RememberedRoles = cfg.Bind("Staff", "RememberedRoles", "",
                "Which job each auto-moved employee should be returned to, as name:role pairs. " +
                "Written automatically; kept in the config so a restart cannot strand someone in " +
                "a job they were only lent to.");
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
        internal const int Security = 4;
        internal const int Technician = 5;
        internal const int Ordering = 6;

        internal static string Status = "off";
        internal static string OrdersStatus = "not checked yet";

        // Where a borrowed employee is most useful right now.
        //
        // Restocking was the only answer before, and it was the wrong one whenever a delivery was
        // sitting on the floor: shelves are refilled FROM storage, so a restocker with an empty back
        // room has nothing to carry, while the boxes nobody is putting away are the actual
        // bottleneck. Emptying the floor first and refilling the shelves second is the order the
        // work genuinely happens in.
        //
        // boxesOBJ is the same parent GetRandomGroundBox draws from, so "has children" is exactly
        // "there is a box a storage worker could pick up".
        internal static int HelperRole(NPC_Manager mgr)
        {
            bool boxesWaiting = mgr != null && mgr.boxesOBJ != null
                                && mgr.boxesOBJ.transform.childCount > 0;
            return boxesWaiting ? Storage : Restocker;
        }

        // Is this employee currently standing in one of the two jobs we lend people to?
        //
        // Needed because the borrowed role is no longer a single fixed value: a cashier lent out
        // may be found as either a storage worker or a restocker, and the "put them back" checks
        // have to recognise both.
        internal static bool IsHelperRole(int role) => role == Storage || role == Restocker;

        // Cardboard bales waiting to be hauled away.
        //
        // Baling is the technician's OTHER job, and the more lucrative one. NPC_Manager case 5
        // state 0 asks GetFurnitureToFix first and falls through to state 10 when nothing is
        // broken, which is where it looks for a bale - so "nothing is broken" was never the same
        // thing as "no work", and lending technicians out on that test alone left bales stacking up
        // in the back room.
        //
        // Worth what it costs to check: a bale pays 18 * boxRecycleFactor against 1.5 for a single
        // loose box, and one bale is ten boxes - so it is better money for a tenth of the walking.
        //
        // Read from the same place GetClosestBale reads: levelPropsOBJ child 9 is the bale parent.
        internal static int BalesWaiting()
        {
            try
            {
                var data = GameData.Instance;
                var spawner = data != null ? data.GetComponent<NetworkSpawner>() : null;
                var props = spawner != null ? spawner.levelPropsOBJ : null;
                if (props == null || props.transform.childCount <= 9) return 0;

                return props.transform.GetChild(9).childCount;
            }
            catch { return 0; }
        }

        // What each employee was doing before we moved them, so they can be put back rather than
        // being assumed to have started as a cashier.
        //
        // Keyed by NAME and written to the config, for two separate reasons:
        //
        //   durability  the map used to live only in memory, so quitting while someone was lent to
        //               restocking stranded them there permanently - the note saying what they
        //               really were died with the process.
        //   correctness it used to be keyed by employeesArray index, and those shift when you hire
        //               or fire. A departure could hand one employee's remembered role to whoever
        //               slid into their slot.
        //
        // The cost is that two employees sharing a name share an entry. That is a far smaller
        // problem than either of the above, and the game generates from a large name pool.
        private static Dictionary<string, int> _original;

        private static Dictionary<string, int> Original
        {
            get
            {
                if (_original != null) return _original;

                _original = new Dictionary<string, int>();
                var raw = StaffRolesConfig.RememberedRoles != null
                    ? StaffRolesConfig.RememberedRoles.Value : "";
                if (string.IsNullOrEmpty(raw)) return _original;

                foreach (var pair in raw.Split(','))
                {
                    // Names can contain most things, so split on the LAST colon.
                    int cut = pair.LastIndexOf(':');
                    if (cut <= 0 || cut == pair.Length - 1) continue;

                    string name = pair.Substring(0, cut).Trim();
                    if (name.Length == 0) continue;

                    int role;
                    if (int.TryParse(pair.Substring(cut + 1), out role)) _original[name] = role;
                }
                return _original;
            }
        }

        private static void Save()
        {
            if (StaffRolesConfig.RememberedRoles == null) return;

            var parts = new List<string>();
            foreach (var kv in Original) parts.Add(kv.Key + ":" + kv.Value);
            StaffRolesConfig.RememberedRoles.Value = string.Join(",", parts.ToArray());
        }

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

        internal static NPC_Info InfoOf(NPC_Manager mgr, int index)
        {
            try
            {
                var go = mgr.employeesArray[index];
                return go != null ? go.GetComponent<NPC_Info>() : null;
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

        // The three accessors still take an index because that is what the callers have; the name
        // is resolved here so no call site has to care.
        internal static void Remember(NPC_Manager mgr, int index, int role)
        {
            string name = NameOf(mgr, index);
            if (string.IsNullOrEmpty(name) || Original.ContainsKey(name)) return;

            Original[name] = role;
            Save();
        }

        internal static int Recall(NPC_Manager mgr, int index, int fallback)
        {
            string name = NameOf(mgr, index);
            if (string.IsNullOrEmpty(name)) return fallback;

            int r;
            return Original.TryGetValue(name, out r) ? r : fallback;
        }

        internal static void Forget(NPC_Manager mgr, int index)
        {
            string name = NameOf(mgr, index);
            if (string.IsNullOrEmpty(name) || !Original.Remove(name)) return;
            Save();
        }
    }

    public class StaffRolesDriver : MonoBehaviour
    {
        private float _next;
        private float _boxesEmptySince = -1f;
        private float _boxesPresentSince = -1f;
        private float _ordersEmptySince = -1f;
        private float _techIdleSince = -1f;
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
                HandleSecurity(mgr, data);
                HandleTech(mgr);
                HandleOrdering(mgr);
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
                    StaffRoles.Forget(mgr, i);
                    StaffRoles.SetRole(mgr, i, StaffRoles.Cashier, "shop open");
                }
                else if (customersLeft == 0)
                {
                    // Closed AND empty. Waiting for the last customer matters: "closed" only means
                    // the doors stopped admitting people, and anyone already inside still has to
                    // queue and pay. Pulling the cashiers the moment the sign flips would strand
                    // them - and a customer who cannot find a free checkout turns thief outright.
                    //
                    // Remember() only records the first time, so re-running this as the helper role
                    // flips between storage and restocking cannot overwrite "was a cashier".
                    StaffRoles.Remember(mgr, i, role);

                    int want = StaffRoles.HelperRole(mgr);
                    if (role != want)
                        StaffRoles.SetRole(mgr, i, want, want == StaffRoles.Storage
                            ? "shop closed and empty, boxes waiting"
                            : "shop closed and empty, floor clear");
                }
            }

            _wasOpen = open;
            StaffRoles.Status = open
                ? $"open - {names.Count} on tills"
                : customersLeft > 0
                    ? $"closed - {names.Count} still on tills, {customersLeft} customer(s) inside"
                    : $"closed and empty - {names.Count} on " +
                      (StaffRoles.HelperRole(mgr) == StaffRoles.Storage ? "storage" : "restocking");
        }

        // Security follow the cashiers: no customers, nobody to steal.
        //
        // Same empty-store condition rather than just "closed", for the same reason - the people
        // still queueing are exactly the ones a thief would be among, and the checkout-starvation
        // path means a closing shop is when theft is most likely, not least.
        private void HandleSecurity(NPC_Manager mgr, GameData data)
        {
            if (!StaffRolesConfig.SecurityHelps.Value) return;

            bool open = data.isSupermarketOpen;
            int customersLeft = mgr.customersnpcParentOBJ != null
                ? mgr.customersnpcParentOBJ.transform.childCount : 0;

            var cashiers = StaffRolesConfig.CashierList();

            for (int i = 0; i < mgr.employeesArray.Length; i++)
            {
                string name = StaffRoles.NameOf(mgr, i);
                if (name == null || cashiers.Contains(name)) continue;   // the cashier rule owns those

                int role = StaffRoles.RoleOf(mgr, i);

                if (role == StaffRoles.Security && !open && customersLeft == 0)
                {
                    StaffRoles.Remember(mgr, i, StaffRoles.Security);
                    StaffRoles.SetRole(mgr, i, StaffRoles.HelperRole(mgr), "shop closed and empty");
                }
                else if (StaffRoles.IsHelperRole(role) && open
                         && StaffRoles.Recall(mgr, i, -1) == StaffRoles.Security)
                {
                    StaffRoles.SetRole(mgr, i, StaffRoles.Security, "shop open");
                }
                else if (StaffRoles.IsHelperRole(role) && !open && customersLeft == 0
                         && StaffRoles.Recall(mgr, i, -1) == StaffRoles.Security)
                {
                    // Still lent out - follow the work as it moves between the floor and the
                    // shelves rather than staying wherever they first landed.
                    int want = StaffRoles.HelperRole(mgr);
                    if (role != want) StaffRoles.SetRole(mgr, i, want, "following the work");
                }
                else if (role == StaffRoles.Security && StaffRoles.Recall(mgr, i, -1) == StaffRoles.Security)
                {
                    StaffRoles.Forget(mgr, i);      // observed back on duty
                }
            }
        }

        // Technicians help out only when nothing is broken AND no bales are waiting.
        //
        // Repairs are one half of the job; hauling cardboard bales to the recycler is the other,
        // and it is the half that pays. Testing brokenFurnitureList alone treated a technician with
        // a back room full of bales as idle, and lent them away from better-paid work.
        //
        // This one direction is debounced, unlike the original rule. Breakages are discrete events
        // and needed no delay, but bales are produced continuously - a baler emits one every ten
        // boxes - so the idle condition now flickers every time a technician clears the last bale
        // and the next one forms. Going back is still immediate: work appearing is a real event.
        private void HandleTech(NPC_Manager mgr)
        {
            if (!StaffRolesConfig.TechHelps.Value) return;

            bool anythingBroken = mgr.brokenFurnitureList != null && mgr.brokenFurnitureList.Count > 0;
            int bales = StaffRolesConfig.TechHaulsBales.Value ? StaffRoles.BalesWaiting() : 0;
            bool anyWork = anythingBroken || bales > 0;

            float now = Time.unscaledTime;
            float delay = Mathf.Max(1f, StaffRolesConfig.IdleSeconds.Value);

            if (anyWork) _techIdleSince = -1f;
            else if (_techIdleSince < 0f) _techIdleSince = now;

            bool quietLongEnough = !anyWork && _techIdleSince > 0f && now - _techIdleSince >= delay;

            var cashiers = StaffRolesConfig.CashierList();

            for (int i = 0; i < mgr.employeesArray.Length; i++)
            {
                string name = StaffRoles.NameOf(mgr, i);
                if (name == null || cashiers.Contains(name)) continue;

                int role = StaffRoles.RoleOf(mgr, i);

                if (role == StaffRoles.Technician && quietLongEnough)
                {
                    StaffRoles.Remember(mgr, i, StaffRoles.Technician);
                    StaffRoles.SetRole(mgr, i, StaffRoles.HelperRole(mgr),
                                       "nothing broken and no bales waiting");
                }
                else if (StaffRoles.IsHelperRole(role) && anyWork
                         && StaffRoles.Recall(mgr, i, -1) == StaffRoles.Technician)
                {
                    StaffRoles.SetRole(mgr, i, StaffRoles.Technician, anythingBroken
                        ? $"{mgr.brokenFurnitureList.Count} thing(s) broken"
                        : $"{bales} bale(s) waiting");
                }
                else if (StaffRoles.IsHelperRole(role) && !anyWork
                         && StaffRoles.Recall(mgr, i, -1) == StaffRoles.Technician)
                {
                    int want = StaffRoles.HelperRole(mgr);
                    if (role != want) StaffRoles.SetRole(mgr, i, want, "following the work");
                }
                else if (role == StaffRoles.Technician && StaffRoles.Recall(mgr, i, -1) == StaffRoles.Technician)
                {
                    StaffRoles.Forget(mgr, i);
                }
            }
        }

        // Order fillers help out when the packaging queue is empty.
        //
        // Their entire work queue is OrderPackaging.Instance.ordersData - NPC_Manager case 6 asks
        // RetrievePackagingFreeOrderIndex() for the first non-empty entry, and if there is none it
        // sends them to state 10, which walks to the rest spot and waits. So an empty array means
        // an employee who is provably doing nothing.
        //
        // The catch is that taking an order REMOVES it from that array
        // (OrderPackaging.RemoveOrderFromEmployee), so someone mid-pack looks idle by the queue
        // alone. packagingAssignedOrderProducts is what they are still carrying out, and it is
        // checked per employee before moving anyone - pulling them mid-order would drop the box on
        // the floor (case 6 state 0 does exactly that when equippedItem > 0) and lose the order.
        private void HandleOrdering(NPC_Manager mgr)
        {
            if (!StaffRolesConfig.OrderingHelps.Value) return;

            bool anyOrders = Diagnose(mgr);

            float now = Time.unscaledTime;
            float delay = Mathf.Max(1f, StaffRolesConfig.IdleSeconds.Value);

            if (anyOrders) _ordersEmptySince = -1f;
            else if (_ordersEmptySince < 0f) _ordersEmptySince = now;

            bool quietLongEnough = !anyOrders && _ordersEmptySince > 0f
                                   && now - _ordersEmptySince >= delay;

            var cashiers = StaffRolesConfig.CashierList();

            for (int i = 0; i < mgr.employeesArray.Length; i++)
            {
                string name = StaffRoles.NameOf(mgr, i);
                if (name == null || cashiers.Contains(name)) continue;

                int role = StaffRoles.RoleOf(mgr, i);

                if (role == StaffRoles.Ordering && quietLongEnough && !MidOrder(mgr, i))
                {
                    StaffRoles.Remember(mgr, i, StaffRoles.Ordering);
                    StaffRoles.SetRole(mgr, i, StaffRoles.HelperRole(mgr), "no orders waiting");
                }
                else if (StaffRoles.IsHelperRole(role) && anyOrders
                         && StaffRoles.Recall(mgr, i, -1) == StaffRoles.Ordering)
                {
                    // Not debounced going back, for the same reason the storage rule is not: an
                    // order arriving is a real event, and a customer waiting on one is the thing
                    // this whole rule exists to avoid.
                    StaffRoles.SetRole(mgr, i, StaffRoles.Ordering, "orders waiting");
                }
                else if (StaffRoles.IsHelperRole(role) && !anyOrders
                         && StaffRoles.Recall(mgr, i, -1) == StaffRoles.Ordering)
                {
                    int want = StaffRoles.HelperRole(mgr);
                    if (role != want) StaffRoles.SetRole(mgr, i, want, "following the work");
                }
                else if (role == StaffRoles.Ordering && StaffRoles.Recall(mgr, i, -1) == StaffRoles.Ordering)
                {
                    StaffRoles.Forget(mgr, i);
                }
            }
        }

        // Why the packer is standing still, in the words of the four things that can stop them.
        //
        // NPC_Manager case 6 state 0 checks all of these and, if ANY fails, silently sends them to
        // state 10 - walk to the rest spot and wait. No message above their head, no log, nothing to
        // look at. That silence is the whole reason this exists:
        //
        //   addonsBought[0]                          the order department was never bought
        //   isOrderDepartmentActivated               it is shut - and it is shut EVERY new day
        //                                            unless openAutomaticallyDepartment is set
        //   RetrieveAnOrderPickupPoint(false)        no pickup point has been placed
        //   RetrievePackagingFreeOrderIndex() >= 0   nothing in the queue yet
        //
        // Returns whether there is work, and leaves the reason in OrdersStatus either way.
        private bool Diagnose(NPC_Manager mgr)
        {
            var data = GameData.Instance;
            var upgrades = data != null ? data.GetComponent<UpgradesManager>() : null;
            var packaging = OrderPackaging.Instance;

            int packers = 0;
            for (int i = 0; i < mgr.employeesArray.Length; i++)
                if (StaffRoles.RoleOf(mgr, i) == StaffRoles.Ordering) packers++;

            string reason;
            bool anyOrders = false;

            if (upgrades == null || upgrades.addonsBought == null || upgrades.addonsBought.Length == 0
                || !upgrades.addonsBought[0])
            {
                reason = "the order department addon is not bought";
            }
            else if (packaging == null)
            {
                reason = "no OrderPackaging in the scene";
            }
            else if (!packaging.isOrderDepartmentActivated)
            {
                // Far and away the most likely answer, because it resets nightly.
                reason = packaging.openAutomaticallyDepartment
                    ? "the department is shut (auto-open is on, so it opens shortly after the day starts)"
                    : "the department is SHUT - open it at the desk, or turn on auto-open so it " +
                      "reopens itself; it closes every new day";
            }
            else if (mgr.orderPickupPointsList == null || mgr.orderPickupPointsList.Count == 0)
            {
                reason = "no order pickup point has been placed";
            }
            else
            {
                int queued = 0;
                if (packaging.ordersData != null)
                    foreach (var order in packaging.ordersData)
                        if (!string.IsNullOrEmpty(order)) queued++;

                anyOrders = queued > 0;
                reason = anyOrders
                    ? $"{queued} order(s) queued"
                    : $"open with no orders yet ({packaging.numberOfAssignedOrders}/" +
                      $"{packaging.maxNumberOfDailyOrders} today; the first cannot arrive before 09:00)";
            }

            string now = $"{packers} packer(s), {reason}";
            if (now != OrdersStatusPrev)
            {
                OrdersStatusPrev = now;
                StaffRoles.OrdersStatus = now;
                if (StaffRolesConfig.Log.Value)
                    Plugin.Log.LogInfo($"[Orders] {now}.");
            }

            return anyOrders;
        }

        private static string OrdersStatusPrev;

        // Halfway through packing an order, even though the queue looks empty.
        //
        // Only two of the four packaging fields are safe to read, and picking the wrong ones
        // wedged this rule shut: NOTHING clears packagingAssignedOrderData or
        // packagingPackedOrderProducts when an order completes. State 5 deposits the box and goes
        // straight back to state 0 - the packed list is only emptied at the START of the next order
        // (case 6 state 1), and the order string is only ever overwritten. So after the very first
        // order both stay populated forever, and testing them meant the packer was permanently
        // "mid-order" and never lent out again.
        //
        // equippedItem is the honest signal, and it happens to bracket the job exactly: state 1
        // calls EquipNPCItem(3) on taking the order, state 5 calls UnequipBox on depositing it.
        // It is also the game's own test for this hazard - case 6 state 0 opens with
        // "if (equippedItem > 0) { DropBoxOnGround; UnequipBox; }", which is precisely the box on
        // the floor we are trying not to cause.
        //
        // packagingAssignedOrderProducts is kept as a second check because it genuinely drains to
        // zero, one RemoveAt(0) per item fetched.
        private static bool MidOrder(NPC_Manager mgr, int index)
        {
            var info = StaffRoles.InfoOf(mgr, index);
            if (info == null) return false;

            if (info.equippedItem > 0) return true;

            return info.packagingAssignedOrderProducts != null
                && info.packagingAssignedOrderProducts.Count > 0;
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

                // Nor is anyone on loan from another rule. Now that borrowed staff can be parked in
                // the storage job, this rule would otherwise see a lent-out guard standing in
                // storage and start managing them - both rules issuing a command for the same
                // employee in the same tick, and this one having no idea they are owed back to
                // security when the shop opens.
                int remembered = StaffRoles.Recall(mgr, i, -1);
                if (remembered >= 0 && remembered != StaffRoles.Storage) continue;

                int role = StaffRoles.RoleOf(mgr, i);

                if (!anyBoxes && role == StaffRoles.Storage && _boxesEmptySince > 0f
                    && now - _boxesEmptySince >= delay)
                {
                    StaffRoles.Remember(mgr, i, StaffRoles.Storage);
                    StaffRoles.SetRole(mgr, i, StaffRoles.Restocker, "no boxes on the floor");
                }
                else if (anyBoxes && role == StaffRoles.Restocker
                         && StaffRoles.Recall(mgr, i, -1) == StaffRoles.Storage)
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
                else if (role == StaffRoles.Storage && StaffRoles.Recall(mgr, i, -1) == StaffRoles.Storage)
                {
                    // Observed back in the job we moved them out of, so the note has served its
                    // purpose and can go.
                    StaffRoles.Forget(mgr, i);
                }
            }
        }
    }
}
