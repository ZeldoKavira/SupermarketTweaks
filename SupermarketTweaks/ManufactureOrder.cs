using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using Mirror;
using UnityEngine;

namespace SupermarketTweaks
{
    // One press to queue every production run the manufactured shelves are short of.
    //
    // The ordering terminal's Restock All cannot touch any of this. Manufactured goods live in a
    // parallel world with its own product table (ManufacturingBase.productsData, not
    // ProductListing.productsData) and its own containers - manufacturingShelvesOBJ,
    // manufacturingStorageShelvesOBJ, manufacturingBoxesOBJ - none of which the ordering code
    // walks, and none of which you can buy from a supplier anyway. The only way to refill them is
    // to run the machine.
    //
    // A run is identified by a PAIR, not an id
    // ---------------------------------------
    // CmdAddToProductionQueue takes (productID, combinableString), and the combinables are what
    // make two boxes of the same base product different goods. A shelf row records both -
    // productInfoArray for the id, combinableInfoArray for the combination - so the run queued for
    // a row has to carry that row's exact combination. Queueing the bare base product would produce
    // something the row will not accept, which is the same dead end as ordering a shelfless
    // product.
    //
    // How many runs
    //   target    = row capacity, summed over every row assigned to that (id, combination)
    //   shortfall = target - everything already made, wherever it is standing
    //   runs      = ceil(shortfall / itemsPerBox)
    //
    // No +1 here, unlike the ordering button. Back stock exists there to absorb the delay of a
    // delivery; a production run has no such delay to hide, and an over-run of a manufactured good
    // costs ingredients that came off your own shelves.
    public static class ManufactureOrderConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyboardShortcut> Key;
        internal static ConfigEntry<bool> ShowButton;
        internal static ConfigEntry<int> MaxRuns;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Manufacturing", "QuickProductionQueue", true,
                "Adds a hotkey that fills the manufacturing machine's queue with every run needed " +
                "to refill the manufactured shelves.");
            Key = cfg.Bind("Manufacturing", "QuickProductionKey", new KeyboardShortcut(KeyCode.F9),
                "Queues the runs needed to refill the manufactured shelves.");
            ShowButton = cfg.Bind("Manufacturing", "ShowProductionButton", false,
                "Draw a fallback overlay button while a machine's interface is open. Off by " +
                "default now that a real button sits in the machine's own panel - turn it on if " +
                "that one fails to appear.");
            MaxRuns = cfg.Bind("Manufacturing", "MaxQueuedRuns", 20,
                new ConfigDescription("Never add more than this many runs in one press. Each run " +
                    "consumes ingredients off your own shelves, so a full refill of an empty shop " +
                    "can be an expensive thing to trigger by accident.",
                    new AcceptableValueRange<int>(1, 100)));
        }

        internal static bool On => Enabled != null && Enabled.Value;
    }

    internal static class ManufactureOrder
    {
        internal static string LastResult = "not run yet";

        // A manufactured good is a base id plus a combination, so that pair is the key.
        private struct Recipe : IEquatable<Recipe>
        {
            public int ProductID;
            public string Combinables;

            public bool Equals(Recipe other)
                => ProductID == other.ProductID
                   && string.Equals(Combinables ?? "", other.Combinables ?? "", StringComparison.Ordinal);

            public override bool Equals(object o) => o is Recipe && Equals((Recipe)o);
            public override int GetHashCode()
                => ProductID * 397 ^ (Combinables ?? "").GetHashCode();
        }

        private class Need
        {
            public Recipe What;
            public int Runs;
            public int Have;
            public int Target;
            public int PerBox;
        }

        private static void Add(Dictionary<Recipe, int> into, Recipe key, int amount)
        {
            if (amount <= 0) return;
            int prev;
            into[key] = into.TryGetValue(key, out prev) ? prev + amount : amount;
        }

        // Capacity and contents of every assigned row on the manufactured shelves.
        private static void ShelfStats(NPC_Manager mgr,
                                       Dictionary<Recipe, int> capacity,
                                       Dictionary<Recipe, int> onShelf)
        {
            if (mgr.manufacturingShelvesOBJ == null) return;

            for (int i = 0; i < mgr.manufacturingShelvesOBJ.transform.childCount; i++)
            {
                var c = mgr.manufacturingShelvesOBJ.transform.GetChild(i).GetComponent<ManufacturingContainer>();
                if (c == null || c.productInfoArray == null) continue;

                for (int j = 0; j < c.productInfoArray.Length / 2; j++)
                {
                    int id = c.productInfoArray[j * 2];
                    if (id < 0) continue;                       // unassigned row

                    var key = new Recipe
                    {
                        ProductID = id,
                        Combinables = (c.combinableInfoArray != null && j < c.combinableInfoArray.Length)
                            ? c.combinableInfoArray[j] : "",
                    };

                    Add(capacity, key, mgr.GetMaxManufacturingProductsPerRow(i, id));
                    Add(onShelf, key, Mathf.Max(0, c.productInfoArray[j * 2 + 1]));
                }
            }
        }

        // Everything already made, wherever it is standing.
        private static Dictionary<Recipe, int> Stock(NPC_Manager mgr)
        {
            var counts = StorageStock(mgr);
            FloorStock(mgr, counts);
            return counts;
        }

        // What the manufacturing storage racks hold. Kept separate from the floor boxes because
        // they feed different jobs: a restocker pulls from the racks, while a box on the floor is
        // a put-away task.
        private static Dictionary<Recipe, int> StorageStock(NPC_Manager mgr)
        {
            var counts = new Dictionary<Recipe, int>();

            if (mgr.manufacturingStorageShelvesOBJ != null)
            {
                foreach (Transform rack in mgr.manufacturingStorageShelvesOBJ.transform)
                {
                    var c = rack.GetComponent<ManufacturingContainer>();
                    if (c == null || c.productInfoArray == null) continue;

                    for (int j = 0; j < c.productInfoArray.Length / 2; j++)
                    {
                        int id = c.productInfoArray[j * 2];
                        if (id < 0) continue;

                        Add(counts, new Recipe
                        {
                            ProductID = id,
                            Combinables = (c.combinableInfoArray != null && j < c.combinableInfoArray.Length)
                                ? c.combinableInfoArray[j] : "",
                        }, c.productInfoArray[j * 2 + 1]);
                    }
                }
            }

            return counts;
        }

        // Boxes waiting on the floor are stock too - the same lesson the ordering button learned by
        // ordering replacements for a delivery that had already arrived.
        private static void FloorStock(NPC_Manager mgr, Dictionary<Recipe, int> counts)
        {
            if (mgr.manufacturingBoxesOBJ != null)
            {
                foreach (Transform box in mgr.manufacturingBoxesOBJ.transform)
                {
                    var d = box.GetComponent<ManufacturingBoxData>();
                    if (d == null || d.manufacturedProductIndex < 0 || d.numberOfProducts <= 0) continue;

                    Add(counts, new Recipe
                    {
                        ProductID = d.manufacturedProductIndex,
                        Combinables = d.combinablesData ?? "",
                    }, d.numberOfProducts);
                }
            }
        }

        // Runs already queued on this machine, so a second press is a no-op rather than a second
        // helping - exactly the bug the ordering button had before the cart was counted.
        private static Dictionary<Recipe, int> Queued(ManufacturingProduction machine)
        {
            var counts = new Dictionary<Recipe, int>();
            if (machine == null || machine.productQueue == null) return counts;

            for (int i = 0; i < machine.productQueue.Count; i++)
            {
                Add(counts, new Recipe
                {
                    ProductID = machine.productQueue[i],
                    Combinables = (machine.combinableQueue != null && i < machine.combinableQueue.Count)
                        ? machine.combinableQueue[i] : "",
                }, 1);
            }
            return counts;
        }

        // Would a manufacturing employee find restocking work right now?
        //
        // Approximates ReturnWeightedManufacturerTask, which is private: it moves stock from the
        // storage racks onto a shelf row with room, so a row below capacity with matching stock
        // behind it is exactly a job waiting. Matching means the (id, combination) PAIR - a rack
        // full of the plain version cannot fill a row labelled for the combined one.
        internal static bool RestockPending(NPC_Manager mgr)
        {
            if (mgr == null) return false;

            var capacity = new Dictionary<Recipe, int>();
            var onShelf = new Dictionary<Recipe, int>();
            ShelfStats(mgr, capacity, onShelf);
            if (capacity.Count == 0) return false;

            var racks = StorageStock(mgr);
            if (racks.Count == 0) return false;

            foreach (var pair in capacity)
            {
                int have, stored;
                onShelf.TryGetValue(pair.Key, out have);
                if (have >= pair.Value) continue;               // row is full

                racks.TryGetValue(pair.Key, out stored);
                if (stored > 0) return true;
            }
            return false;
        }

        // Any machine with something still to make.
        //
        // The same test GetManufacturingProducerWithQueue uses, minus the assignedToEmployee half:
        // a machine already being worked is still work, and excluding it would report "idle" for
        // the one employee who is busiest. The queue entry survives production - Produce() only
        // pops it at the end - so a worker mid-run keeps this true.
        internal static bool ProductionPending()
        {
            foreach (var m in UnityEngine.Object.FindObjectsOfType<ManufacturingProduction>())
                if (m != null && m.productQueue != null && m.productQueue.Count > 0) return true;
            return false;
        }

        private static List<Need> Calculate(NPC_Manager mgr, ManufacturingProduction machine)
        {
            var needs = new List<Need>();

            var mb = ManufacturingBase.Instance;
            if (mb == null || mb.productsData == null) return needs;

            var capacity = new Dictionary<Recipe, int>();
            var onShelf = new Dictionary<Recipe, int>();
            ShelfStats(mgr, capacity, onShelf);

            var stock = Stock(mgr);
            var queued = Queued(machine);

            foreach (var pair in capacity)
            {
                var key = pair.Key;
                if (key.ProductID < 0 || key.ProductID >= mb.productsData.Length) continue;

                // Only what has actually been discovered. A locked recipe cannot be produced, and
                // the machine would refuse the run anyway.
                if (mb.unlockedBaseProducts != null && key.ProductID < mb.unlockedBaseProducts.Length
                    && !mb.unlockedBaseProducts[key.ProductID]) continue;

                int have, made;
                onShelf.TryGetValue(key, out have);
                stock.TryGetValue(key, out made);

                int shortfall = pair.Value - (have + made);
                if (shortfall <= 0) continue;

                int perBox = mb.productsData[key.ProductID].itemsPerBox;
                if (perBox <= 0) continue;

                int runs = (shortfall + perBox - 1) / perBox;

                int already;
                queued.TryGetValue(key, out already);
                runs -= already;
                if (runs <= 0) continue;

                needs.Add(new Need
                {
                    What = key, Runs = runs, Have = have + made,
                    Target = pair.Value, PerBox = perBox,
                });
            }

            return needs;
        }

        // The machine the player is standing at, or the only one there is.
        internal static ManufacturingProduction Target()
        {
            var machines = UnityEngine.Object.FindObjectsOfType<ManufacturingProduction>();
            if (machines == null || machines.Length == 0) return null;
            if (machines.Length == 1) return machines[0];

            var cam = Camera.main;
            if (cam == null) return machines[0];

            ManufacturingProduction best = null;
            float bestDistance = float.MaxValue;
            foreach (var m in machines)
            {
                float d = (m.transform.position - cam.transform.position).sqrMagnitude;
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = m;
            }
            return best;
        }

        internal static void Run() => Run(null);

        // machine == null means "whichever one you are standing at", which is what the hotkey and
        // the overlay want. The in-panel button passes its own instead: nearest would nearly always
        // agree, and nearly is how you queue into the wrong machine when two stand side by side.
        internal static void Run(ManufacturingProduction machine)
        {
            try
            {
                var mgr = NPC_Manager.Instance;
                if (machine == null) machine = Target();
                if (mgr == null || machine == null)
                {
                    LastResult = "no manufacturing machine found";
                    Plugin.Log.LogInfo($"[Produce] {LastResult}.");
                    return;
                }

                var needs = Calculate(mgr, machine);
                if (needs.Count == 0)
                {
                    LastResult = "nothing to make - shelves full or already queued";
                    Plugin.Log.LogInfo($"[Produce] {LastResult}.");
                    return;
                }

                int cap = Mathf.Max(1, ManufactureOrderConfig.MaxRuns.Value);
                int added = 0, dropped = 0;
                var sb = new StringBuilder();

                foreach (var need in needs)
                {
                    for (int i = 0; i < need.Runs; i++)
                    {
                        if (added >= cap) { dropped++; continue; }

                        // Public, and it does nothing but forward to CmdAddToProductionQueue, which
                        // is requiresAuthority: false - so this works from a client as well as the
                        // host, exactly like clicking the machine's own button.
                        machine.AddItemFromManufacturingDesk(need.What.ProductID, need.What.Combinables ?? "");
                        added++;
                    }

                    sb.AppendLine($"    {ResearchTracker.NameOf(need.What.ProductID)} " +
                                  $"[{(string.IsNullOrEmpty(need.What.Combinables) ? "plain" : need.What.Combinables)}]: " +
                                  $"have {need.Have}, target {need.Target}, {need.Runs} run(s) of {need.PerBox}");
                }

                LastResult = $"{added} run(s) queued across {needs.Count} product(s)"
                           + (dropped > 0 ? $"; {dropped} not added, MaxQueuedRuns is {cap}" : "");
                Plugin.Log.LogInfo($"[Produce] {LastResult}:\n" + sb);
            }
            catch (Exception e)
            {
                LastResult = "failed: " + e.Message;
                Plugin.Log.LogError($"[Produce] {e}");
            }
        }
    }

    public class ManufactureOrderDriver : MonoBehaviour
    {
        private Rect _rect = new Rect(20f, 90f, 300f, 58f);

        // Same fix as the ordering overlay: Target() calls FindObjectsOfType, and OnGUI runs
        // several times a frame, so resolving the machine in there was several scene walks per
        // frame for a fallback button that is off by default.
        private ManufacturingProduction _machine;
        private float _nextLook;

        private void Update()
        {
            try
            {
                if (!ManufactureOrderConfig.On) return;
                if (ManufactureOrderConfig.Key != null && ManufactureOrderConfig.Key.Value.IsDown())
                    ManufactureOrder.Run();

                if (!ManufactureOrderConfig.ShowButton.Value) { _machine = null; return; }
                if (Time.unscaledTime < _nextLook) return;
                _nextLook = Time.unscaledTime + 0.5f;

                _machine = ManufactureOrder.Target();
            }
            catch (Exception e) { Plugin.Log.LogError($"[Produce] {e.Message}"); }
        }

        // An overlay rather than a button cloned into the machine's own UI, unlike the ordering
        // terminal's. That one was only possible because an F12 dump had already told us where
        // BuyEmptyBoxButton lives; the manufacturing interface has not been dumped, and guessing at
        // a hierarchy is what made the first version of the order button search the wrong root and
        // silently find nothing. Dump it with F12 while the machine is open and this can become a
        // real button.
        private void OnGUI()
        {
            if (!ManufactureOrderConfig.On || !ManufactureOrderConfig.ShowButton.Value) return;

            var machine = _machine;
            if (machine == null || machine.selectionCanvasOBJ == null
                || !machine.selectionCanvasOBJ.activeInHierarchy) return;

            GUI.Box(_rect, GUIContent.none);
            if (GUI.Button(new Rect(_rect.x + 8f, _rect.y + 6f, _rect.width - 16f, 24f),
                           "Queue runs to refill shelves"))
                ManufactureOrder.Run();

            GUI.Label(new Rect(_rect.x + 8f, _rect.y + 32f, _rect.width - 16f, 20f),
                      ManufactureOrder.LastResult);
        }
    }
}
