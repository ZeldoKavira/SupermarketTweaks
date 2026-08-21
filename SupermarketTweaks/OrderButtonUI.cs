using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace SupermarketTweaks
{
    // A real button in the game's own ordering UI, beside "Buy Empty Box".
    //
    // Built from the hierarchy the F12 dump reported rather than guessed:
    //
    //   ExtraButtons                        pos=(0, -238)
    //     BuyEmptyBoxButton                 pos=(-298, 0)  size=(140, 25)
    //       [RectTransform CanvasRenderer Image Button PlayMakerFSM]
    //       BuyEmptyBox_Text  "Buy Empty Box"
    //         [RectTransform CanvasRenderer TextMeshProUGUI SetLocalizationString]
    //
    // Cloning it gives the right font, colours and scale for free. Two components have to go from
    // the copy, and both would be easy to miss:
    //
    //   PlayMakerFSM           this is what actually buys the empty box. Left on, our button would
    //                          order a box every time it was pressed, on top of doing our work.
    //   SetLocalizationString  it rewrites the label from a localisation key on enable, so our
    //                          caption would be replaced by "Buy Empty Box" the moment it showed.
    //
    // Both are removed by type NAME, so the mod still needs no PlayMaker or game-UI-script
    // reference.
    public static class OrderButtonConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Label;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Ordering", "AddOrderButtonToUI", true,
                "Add a button to the ordering terminal, next to Buy Empty Box, that fills the cart " +
                "with everything out of stock in storage.");
            Label = cfg.Bind("Ordering", "OrderButtonLabel", "Restock All",
                "Caption for that button.");
        }

        internal static bool On => Enabled != null && Enabled.Value;
    }

    public class OrderButtonUI : MonoBehaviour
    {
        private const string ContainerName = "ExtraButtons";
        private const string TemplateName  = "BuyEmptyBoxButton";
        private const string CloneName     = "SMT_RestockAllButton";

        private float _next;

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            try
            {
                if (!OrderButtonConfig.On || !RestockOrderConfig.On) return;
                Ensure();
            }
            catch (Exception e) { Plugin.Log.LogError($"[OrderButton] {e.Message}"); }
        }

        private void Ensure()
        {
            // The template is found by walking from the live device rather than by a global name
            // search: there can be more than one ordering terminal, and each needs its own button.
            foreach (var device in UnityEngine.Object.FindObjectsOfType<OrderingDevice>(true))
            {
                var root = device.transform.root;
                var container = FindDeep(root, ContainerName);
                if (container == null) continue;

                if (container.Find(CloneName) != null) continue;      // already added

                var template = container.Find(TemplateName);
                if (template == null) continue;

                Build(container, template as RectTransform);
            }
        }

        private void Build(Transform container, RectTransform template)
        {
            if (template == null) return;

            var clone = UnityEngine.Object.Instantiate(template.gameObject, container);
            clone.name = CloneName;

            // Strip anything that would make the copy behave like the original.
            StripByName(clone, "PlayMakerFSM");
            foreach (var t in clone.GetComponentsInChildren<Transform>(true))
                StripByName(t.gameObject, "SetLocalizationString");

            var rect = clone.GetComponent<RectTransform>();
            if (rect != null)
            {
                // Directly right of the template, with a small gap: it is 140 wide at x=-298, and
                // nothing else in this container occupies that space.
                rect.anchoredPosition = template.anchoredPosition + new Vector2(template.sizeDelta.x + 6f, 0f);
                rect.sizeDelta = template.sizeDelta;
            }

            SetLabel(clone, OrderButtonConfig.Label.Value);

            var button = clone.GetComponent<Button>();
            if (button != null)
            {
                // The clone carries the template's listeners; ours must be the only one.
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => RestockOrder.Run());
            }
            else
            {
                Plugin.Log.LogWarning("[OrderButton] The clone has no Button; it will do nothing.");
            }

            Plugin.Log.LogInfo($"[OrderButton] Added '{OrderButtonConfig.Label.Value}' next to {TemplateName}.");
        }

        private static void StripByName(GameObject go, string typeName)
        {
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != typeName) continue;
                UnityEngine.Object.DestroyImmediate(c);
            }
        }

        // The label is a TextMeshProUGUI, reached by reflection so no TextMeshPro reference is
        // needed just to set a string.
        private static void SetLabel(GameObject clone, string text)
        {
            foreach (var c in clone.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.GetType().Name != "TextMeshProUGUI") continue;

                var prop = c.GetType().GetProperty("text");
                if (prop != null) prop.SetValue(c, text, null);

                c.gameObject.name = "SMT_RestockAll_Text";
                return;
            }
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
