using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace SupermarketTweaks
{
    // One press to order everything the shop has run out of.
    //
    // "Run out of" means no stock in the BACK ROOM - a product can look fine on the shelf and still
    // be one restock away from a gap, and that gap is expensive: an out-of-stock item is deleted
    // from a customer's shopping list outright rather than deferred (WhichShelfHasItem returns -1,
    // the id is removed, and they carry on without it).
    //
    // How many boxes
    // --------------
    // Enough to fill every empty slot on that product's shelves AND leave at least one unit over,
    // so there is something in storage afterwards rather than the shop being bare again the moment
    // the shelves are filled:
    //
    //   boxes = floor(openShelfSpace / maxItemsPerBox) + 1
    //
    // The +1 is unconditional and that is the point - it is what guarantees the leftover, including
    // when the space divides exactly by the box size.
    public static class RestockOrderConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyboardShortcut> Key;
        internal static ConfigEntry<bool> ShowButton;
        internal static ConfigEntry<bool> IncludeShelvedButEmpty;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Ordering", "QuickRestockOrder", true,
                "Adds a button and hotkey that fills the order cart with every product that has no " +
                "stock left in storage.");
            Key = cfg.Bind("Ordering", "QuickRestockKey", new KeyboardShortcut(KeyCode.F8),
                "Fills the cart with everything out of stock in storage.");
            ShowButton = cfg.Bind("Ordering", "ShowOrderButton", true,
                "Draw the button on screen while the ordering terminal is open.");
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
            public int OpenSpace;
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

        // Empty slots per product across every row assigned to it.
        private static Dictionary<int, int> OpenShelfSpace(NPC_Manager mgr)
        {
            var space = new Dictionary<int, int>();
            if (mgr.shelvesOBJ == null) return space;

            for (int i = 0; i < mgr.shelvesOBJ.transform.childCount; i++)
            {
                var data = mgr.shelvesOBJ.transform.GetChild(i).GetComponent<Data_Container>();
                if (data == null || data.productInfoArray == null) continue;

                for (int j = 0; j < data.productInfoArray.Length / 2; j++)
                {
                    int id = data.productInfoArray[j * 2];
                    if (id < 0) continue;                       // unassigned row
                    int have = data.productInfoArray[j * 2 + 1];

                    // The game's own capacity rule, rather than a guess from shelf dimensions.
                    int cap = mgr.GetMaxProductsPerRow(i, id);
                    int free = Mathf.Max(0, cap - have);

                    int prev;
                    space[id] = space.TryGetValue(id, out prev) ? prev + free : free;
                }
            }
            return space;
        }

        private static List<Need> Calculate(out int skippedNoShelf)
        {
            skippedNoShelf = 0;
            var needs = new List<Need>();

            var mgr = NPC_Manager.Instance;
            var listing = ProductListing.Instance;
            if (mgr == null || listing == null || listing.availableProducts == null) return needs;

            var storage = StorageCounts(mgr);
            var space = OpenShelfSpace(mgr);

            foreach (int id in listing.availableProducts)
            {
                int inStorage;
                storage.TryGetValue(id, out inStorage);
                if (inStorage > 0) continue;                    // still has back-room stock

                int open;
                bool hasShelf = space.TryGetValue(id, out open);

                if (!hasShelf)
                {
                    // No row names this product, so a restocker could never put it out - ordering it
                    // would only fill the back room with stock that cannot move.
                    if (RestockOrderConfig.IncludeShelvedButEmpty.Value) { skippedNoShelf++; continue; }
                    open = 0;
                }

                int perBox = listing.productsData[id].maxItemsPerBox;
                if (perBox <= 0) continue;

                // +1 guarantees the leftover, including when open space divides exactly.
                int boxes = (open / perBox) + 1;

                needs.Add(new Need { ProductID = id, Boxes = boxes, OpenSpace = open, PerBox = perBox });
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
                var needs = Calculate(out skipped);

                if (needs.Count == 0)
                {
                    LastResult = skipped > 0
                        ? $"nothing to order ({skipped} out of stock but shelfless)"
                        : "nothing out of stock";
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

                    sb.AppendLine($"    product {need.ProductID}: {need.Boxes} box(es) of {need.PerBox} " +
                                  $"-> fills {need.OpenSpace} empty slot(s), " +
                                  $"{need.Boxes * need.PerBox - need.OpenSpace} left for storage");
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

        private void Update()
        {
            try
            {
                if (!RestockOrderConfig.On) return;
                if (RestockOrderConfig.Key != null && RestockOrderConfig.Key.Value.IsDown())
                    RestockOrder.Run();
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

            var device = UnityEngine.Object.FindObjectOfType<OrderingDevice>();
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
