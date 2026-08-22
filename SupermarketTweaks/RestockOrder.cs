using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace SupermarketTweaks
{
    // One press to order everything the shop has run out of.
    //
    // A gap is expensive: an out-of-stock item is deleted from a customer's shopping list outright
    // rather than deferred (WhichShelfHasItem returns -1, the id is removed, and they carry on
    // without it), so the shop loses that sale rather than delaying it.
    //
    // How many boxes
    // --------------
    //   target    = max(totalShelfCapacityForThatProduct + 1, MinimumStock)
    //   shortfall = target - everythingTheShopAlreadyOwns
    //   boxes     = ceil(shortfall / maxItemsPerBox)
    //
    // The target is whichever is larger of "fill the shelves and keep a spare" and the configured
    // minimum, because the minimum is a floor on the total rather than a bonus on top - a product
    // with 300 slots of shelf is already past a minimum of 50 and does not need padding.
    //
    // The +1 on capacity is what guarantees back stock. A target of exactly capacity is satisfied
    // by a full shelf and an empty back room, and that is the shop one sale away from a gap; asking
    // for one unit more means a box gets ordered and the remainder of it lands in storage.
    //
    // What counts as "already have"
    // -----------------------------
    // Storage stock, boxes still sitting on the floor from a delivery, boxes being carried by an
    // employee or a player, and boxes already in the order cart - the same four places the
    // terminal's own "in storage" figure looks, plus the cart.
    //
    // Counting only storage made pressing twice order everything twice: ordering moves nothing into
    // storage, so the second press saw the same empty shop and the same need.
    public static class RestockOrderConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyboardShortcut> Key;
        internal static ConfigEntry<bool> ShowButton;
        internal static ConfigEntry<bool> IncludeShelvedButEmpty;
        internal static ConfigEntry<int> MinimumStock;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Ordering", "QuickRestockOrder", true,
                "Adds a button and hotkey that fills the order cart with every product that has no " +
                "stock left in storage.");
            Key = cfg.Bind("Ordering", "QuickRestockKey", new KeyboardShortcut(KeyCode.F8),
                "Fills the cart with everything out of stock in storage.");
            ShowButton = cfg.Bind("Ordering", "ShowOrderButton", false,
                "Draw a fallback overlay button while the terminal is open. Off by default now " +
                "that a real button sits in the terminal's own UI - turn it on if that one fails " +
                "to appear.");
            MinimumStock = cfg.Bind("Ordering", "MinimumStock", 0,
                new ConfigDescription("Keep at least this many units of every product in the shop, " +
                    "counting shelves, storage, ground boxes and anything being carried. It is a " +
                    "floor on the total, not a bonus on top: a product whose shelves already hold " +
                    "more than this is unaffected. 0 means fill the shelves and keep one spare, " +
                    "which is the least that still leaves something in the back room.",
                    new AcceptableValueRange<int>(0, 2000)));
            IncludeShelvedButEmpty = cfg.Bind("Ordering", "OnlyProductsWithShelves", true,
                "Only order products that actually have a shelf row assigned. Ordering anything " +
                "else just fills the back room with stock no worker can ever put out.");
        }

        internal static bool On => Enabled != null && Enabled.Value;
    }

    internal static class RestockOrder
    {
        internal static string LastResult = "not run yet";

        private class Need
        {
            public int ProductID;
            public int Boxes;
            public int Target;      // what we are topping up to
            public int Have;        // everything already in the shop, wherever it is
            public int Shortfall;   // Target - Have
            public int PerBox;
        }

        // Everything sitting in the back room, by product.
        private static Dictionary<int, int> StorageCounts(NPC_Manager mgr)
        {
            var counts = new Dictionary<int, int>();
            if (mgr.storageOBJ == null) return counts;

            foreach (Transform box in mgr.storageOBJ.transform)
            {
                var data = box.GetComponent<Data_Container>();
                if (data == null || data.productInfoArray == null) continue;

                // Same (id, count) pairing the shelves use.
                for (int j = 0; j < data.productInfoArray.Length / 2; j++)
                {
                    int id = data.productInfoArray[j * 2];
                    int n = data.productInfoArray[j * 2 + 1];
                    if (id < 0 || n <= 0) continue;

                    int prev;
                    counts[id] = counts.TryGetValue(id, out prev) ? prev + n : n;
                }
            }
            return counts;
        }

        // Boxes already sitting in the order cart, by product.
        //
        // The cart is one UI child per box, each tagged with its product id. Ignoring it was the
        // bug: ordering changes neither storage nor shelf space, so a second press recalculated the
        // identical need and added the whole order again.
        private static Dictionary<int, int> CartBoxes(ManagerBlackboard blackboard)
        {
            var counts = new Dictionary<int, int>();
            var parent = blackboard != null ? blackboard.shoppingListParent : null;
            if (parent == null) return counts;

            foreach (Transform entry in parent.transform)
            {
                var data = entry.GetComponent<InteractableData>();
                if (data == null) continue;

                int id = data.thisSkillIndex;
                int prev;
                counts[id] = counts.TryGetValue(id, out prev) ? prev + 1 : 1;
            }
            return counts;
        }

        // Units in boxes already delivered and sitting on the floor.
        //
        // These count as incoming too. A delivery lands on the floor, not in storage, so between
        // the van arriving and a storage worker putting it away the product still reads as zero
        // stock - and pressing again would order a second delivery for something already here.
        private static Dictionary<int, int> FloorBoxUnits(NPC_Manager mgr)
        {
            var counts = new Dictionary<int, int>();
            if (mgr.boxesOBJ == null) return counts;

            foreach (Transform box in mgr.boxesOBJ.transform)
            {
                var data = box.GetComponent<BoxData>();
                if (data == null || data.productID < 0 || data.numberOfProducts <= 0) continue;

                int prev;
                counts[data.productID] = counts.TryGetValue(data.productID, out prev)
                    ? prev + data.numberOfProducts : data.numberOfProducts;
            }
            return counts;
        }

        // Units in a box someone is currently holding.
        //
        // A restocker who has picked a box up out of storage is carrying stock that is on its way
        // to a shelf, and a player walking one across the shop is no different. Neither is in
        // storage and neither is on the floor, so without this they read as gone - and the shop
        // orders a replacement for a box that is three metres from the shelf it belongs on.
        //
        // This is exactly what the terminal's own "in storage" figure counts. OrderingDevice
        // .UpdateProductExistences sums four sources into one number: storage containers, ground
        // boxes, employees via NPC_Info.boxProductID/boxNumberOfProducts, and players via
        // PlayerSyncCharacter.syncedProductID/syncedNumberOfProducts. We were reading the first two.
        private static Dictionary<int, int> CarriedUnits(NPC_Manager mgr)
        {
            var counts = new Dictionary<int, int>();

            var employees = mgr != null ? mgr.employeeParentOBJ : null;
            if (employees != null)
            {
                foreach (Transform employee in employees.transform)
                {
                    var info = employee.GetComponent<NPC_Info>();
                    if (info == null || info.boxProductID < 0 || info.boxNumberOfProducts <= 0) continue;

                    int prev;
                    counts[info.boxProductID] = counts.TryGetValue(info.boxProductID, out prev)
                        ? prev + info.boxNumberOfProducts : info.boxNumberOfProducts;
                }
            }

            // Players are reached through the network manager rather than the scene, because in
            // multiplayer the other player's character is only ever a synced object - and their
            // box counts towards the shop's stock just as much as the host's does.
            var manager = Mirror.NetworkManager.singleton as CustomNetworkManager;
            if (manager != null)
            {
                foreach (var player in manager.GamePlayers)
                {
                    if (player == null) continue;
                    var sync = player.GetComponent<PlayerSyncCharacter>();
                    if (sync == null || sync.syncedProductID < 0 || sync.syncedNumberOfProducts <= 0) continue;

                    int prev;
                    counts[sync.syncedProductID] = counts.TryGetValue(sync.syncedProductID, out prev)
                        ? prev + sync.syncedNumberOfProducts : sync.syncedNumberOfProducts;
                }
            }

            return counts;
        }

        // Capacity and current contents per product, across every row assigned to it.
        //
        // Both halves are needed now rather than just the difference between them: the target is
        // measured against total capacity, and what is already sitting on the shelf is stock like
        // any other.
        private struct Shelf
        {
            public int Capacity;
            public int OnShelf;
        }

        private static Dictionary<int, Shelf> ShelfStats(NPC_Manager mgr)
        {
            var stats = new Dictionary<int, Shelf>();
            if (mgr.shelvesOBJ == null) return stats;

            for (int i = 0; i < mgr.shelvesOBJ.transform.childCount; i++)
            {
                var data = mgr.shelvesOBJ.transform.GetChild(i).GetComponent<Data_Container>();
                if (data == null || data.productInfoArray == null) continue;

                for (int j = 0; j < data.productInfoArray.Length / 2; j++)
                {
                    int id = data.productInfoArray[j * 2];
                    if (id < 0) continue;                       // unassigned row
                    int have = Mathf.Max(0, data.productInfoArray[j * 2 + 1]);

                    // The game's own capacity rule, rather than a guess from shelf dimensions.
                    int cap = mgr.GetMaxProductsPerRow(i, id);

                    Shelf prev;
                    stats.TryGetValue(id, out prev);
                    stats[id] = new Shelf
                    {
                        Capacity = prev.Capacity + cap,
                        OnShelf  = prev.OnShelf + have,
                    };
                }
            }
            return stats;
        }

        private static List<Need> Calculate(ManagerBlackboard blackboard, out int skippedNoShelf)
        {
            skippedNoShelf = 0;
            var needs = new List<Need>();

            var mgr = NPC_Manager.Instance;
            var listing = ProductListing.Instance;
            if (mgr == null || listing == null || listing.availableProducts == null) return needs;

            int minimum = RestockOrderConfig.MinimumStock != null
                ? Mathf.Max(0, RestockOrderConfig.MinimumStock.Value) : 0;

            var storage = StorageCounts(mgr);
            var shelves = ShelfStats(mgr);
            var cart = CartBoxes(blackboard);
            var floor = FloorBoxUnits(mgr);
            var carried = CarriedUnits(mgr);

            foreach (int id in listing.availableProducts)
            {
                Shelf shelf;
                bool hasShelf = shelves.TryGetValue(id, out shelf);

                if (!hasShelf && RestockOrderConfig.IncludeShelvedButEmpty.Value)
                {
                    // No row names this product, so a restocker could never put it out - ordering it
                    // would only fill the back room with stock that cannot move. Checked before the
                    // minimum is applied, so raising the minimum does not start stockpiling every
                    // product the shop has nowhere to sell from.
                    skippedNoShelf++;
                    continue;
                }

                int inStorage, onFloor, inHand;
                storage.TryGetValue(id, out inStorage);
                floor.TryGetValue(id, out onFloor);
                carried.TryGetValue(id, out inHand);

                // Everything the shop already owns, wherever it is standing: on the shelf, in the
                // back room, in a box on the floor, or in someone's arms.
                int have = shelf.OnShelf + inStorage + onFloor + inHand;

                // Fill the shelves and keep one spare, or hold the minimum, whichever asks for
                // more. The +1 on capacity is what guarantees back stock: a target of exactly
                // capacity is met by a full shelf and an empty back room, which is the shop one
                // sale away from a gap.
                int target = Mathf.Max(shelf.Capacity + 1, minimum);

                int shortfall = target - have;
                if (shortfall <= 0) continue;

                int perBox = listing.productsData[id].maxItemsPerBox;
                if (perBox <= 0) continue;

                // Round up, not floor-plus-one. The leftover is guaranteed by the target now, so
                // the old unconditional extra box would be a second helping of the same guarantee -
                // and on a shortfall that divides exactly by the box size it ordered a whole box
                // nobody needed.
                int boxes = (shortfall + perBox - 1) / perBox;

                // Subtract what is already on the order, which is what makes pressing twice a
                // no-op rather than a double order.
                int already;
                cart.TryGetValue(id, out already);
                boxes -= already;
                if (boxes <= 0) continue;

                needs.Add(new Need
                {
                    ProductID = id, Boxes = boxes, Target = target,
                    Have = have, Shortfall = shortfall, PerBox = perBox,
                });
            }

            return needs;
        }

        internal static void Run()
        {
            try
            {
                var blackboard = GameData.Instance != null
                    ? GameData.Instance.GetComponent<ManagerBlackboard>() : null;
                var listing = ProductListing.Instance;
                if (blackboard == null || listing == null)
                {
                    LastResult = "not ready";
                    return;
                }

                int skipped;
                var needs = Calculate(blackboard, out skipped);

                if (needs.Count == 0)
                {
                    LastResult = skipped > 0
                        ? $"nothing to add ({skipped} out of stock but shelfless)"
                        : "nothing to add - already stocked or on the order";
                    Plugin.Log.LogInfo($"[Order] {LastResult}.");
                    return;
                }

                int boxes = 0;
                float total = 0f;
                var sb = new StringBuilder();

                foreach (var need in needs)
                {
                    // The cart is one UI child per box, so a quantity is that many calls - the same
                    // thing the terminal does when you click a product repeatedly.
                    float boxPrice = PricePerBox(listing, need.ProductID);

                    for (int i = 0; i < need.Boxes; i++)
                    {
                        blackboard.AddShoppingListProduct(need.ProductID, boxPrice);
                        boxes++;
                        total += boxPrice;
                    }

                    sb.AppendLine($"    product {need.ProductID}: have {need.Have}, target {need.Target} " +
                                  $"-> short {need.Shortfall}; {need.Boxes} box(es) of {need.PerBox}, " +
                                  $"{need.Boxes * need.PerBox - need.Shortfall} left over");
                }

                LastResult = $"{needs.Count} product(s), {boxes} box(es), ${total:0.00}";
                Plugin.Log.LogInfo($"[Order] Added {LastResult}" +
                                   (skipped > 0 ? $"; skipped {skipped} with no shelf" : "") + ":\n" + sb);
            }
            catch (Exception e)
            {
                LastResult = "failed: " + e.Message;
                Plugin.Log.LogError($"[Order] {e}");
            }
        }

        // Mirrors ManagerBlackboard.PricePerBoxRetrieve, which is private.
        private static float PricePerBox(ProductListing listing, int productID)
        {
            float inflation = listing.tierInflation[listing.productsData[productID].productTier];
            int perBox = listing.productsData[productID].maxItemsPerBox;
            return Mathf.Round(Mathf.Round(listing.productsData[productID].basePricePerUnit * inflation * 100f)
                               / 100f * perBox * 100f) / 100f;
        }
    }

    public class RestockOrderDriver : MonoBehaviour
    {
        private Rect _rect = new Rect(20f, 20f, 260f, 58f);

        // Looked up on a timer here rather than inside OnGUI.
        //
        // OnGUI runs several times a frame - once per event, layout and repaint at minimum - so a
        // FindObjectOfType in there costs several full scene walks per frame for a button that is
        // usually not even drawn.
        private OrderingDevice _device;
        private float _nextLook;

        private void Update()
        {
            try
            {
                if (!RestockOrderConfig.On) return;
                if (RestockOrderConfig.Key != null && RestockOrderConfig.Key.Value.IsDown())
                    RestockOrder.Run();

                if (!RestockOrderConfig.ShowButton.Value) { _device = null; return; }
                if (Time.unscaledTime < _nextLook) return;
                _nextLook = Time.unscaledTime + 0.5f;

                _device = UnityEngine.Object.FindObjectOfType<OrderingDevice>();
            }
            catch (Exception e) { Plugin.Log.LogError($"[Order] {e.Message}"); }
        }

        // Drawn only while the ordering terminal is actually open, so it is not floating over the
        // shop the rest of the time. Placing it inside the game's own canvas would mean cloning a
        // PlayMaker-driven button whose hierarchy I cannot inspect from the assembly - this is the
        // honest version until that hierarchy can be looked at in game.
        private void OnGUI()
        {
            if (!RestockOrderConfig.On || !RestockOrderConfig.ShowButton.Value) return;

            var device = _device;
            if (device == null || !device.isActiveAndEnabled) return;

            GUI.Box(_rect, GUIContent.none);
            if (GUI.Button(new Rect(_rect.x + 8f, _rect.y + 6f, _rect.width - 16f, 24f),
                           "Order everything out of stock"))
                RestockOrder.Run();

            GUI.Label(new Rect(_rect.x + 8f, _rect.y + 32f, _rect.width - 16f, 20f),
                      RestockOrder.LastResult);
        }
    }
}
