using System;
using UnityEngine;
using UnityEngine.UI;

namespace SupermarketTweaks
{
    // A real button in the manufacturing machine's own interface.
    //
    // From the F12 dump, taken with a machine open:
    //
    //   MainContainer
    //     FinalPanelContainer                 pos=(742, 0)
    //       AssembledRecipeText   "*"         pos=(-7, 107)   size=(551.9, 200)
    //       ItemsPerBoxBCK                    pos=(0, -98)    size=(560, 120)
    //       Manufacture_Button                pos=(0, -223)   size=(560, 120)
    //       BuyEmptyBox_Button                pos=(88, 279)   size=(230, 42.9)
    //         [RectTransform CanvasRenderer PlayMakerFSM Image Button Shadow]
    //         BuyEmptyBox_Text  "Buy empty box"
    //           [RectTransform CanvasRenderer TextMeshProUGUI SetLocalizationString]
    //
    // BuyEmptyBox_Button is the template rather than Manufacture_Button: it is the small one, and
    // cloning the 560x120 primary would put a second thing the size of the Manufacture button on
    // the panel, which is asking to be misclicked.
    //
    // The same two components have to go from the copy as on the ordering terminal, and for the
    // same reasons - PlayMakerFSM is what actually buys the empty box, and SetLocalizationString
    // rewrites the label from a key on enable. Both are removed by type NAME, so this still needs
    // no PlayMaker or game-UI reference.
    //
    // No name search for the root here: ManufacturingProduction.selectionCanvasOBJ is a direct
    // reference to the canvas, which is what the ordering button lacked when its first version
    // walked out from the wrong object and silently found nothing.
    public class ManufactureButtonUI : MonoBehaviour
    {
        private const string PanelName    = "FinalPanelContainer";
        private const string TemplateName = "BuyEmptyBox_Button";
        private const string CloneName    = "SMT_QueueRefillButton";

        private float _next;
        private int _complaints;

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            try
            {
                if (!ManufactureOrderConfig.On) return;
                Ensure();
            }
            catch (Exception e) { Plugin.Log.LogError($"[ProduceButton] {e.Message}"); }
        }

        private void Ensure()
        {
            // Every machine gets its own, the same way every ordering terminal does.
            foreach (var machine in UnityEngine.Object.FindObjectsOfType<ManufacturingProduction>(true))
            {
                var canvas = machine.selectionCanvasOBJ;
                if (canvas == null) continue;

                var panel = FindDeep(canvas.transform, PanelName);
                if (panel == null)
                {
                    Complain($"No {PanelName} under the machine's canvas.");
                    continue;
                }

                if (panel.Find(CloneName) != null) continue;        // already added

                var template = panel.Find(TemplateName) as RectTransform;
                if (template == null)
                {
                    Complain($"Found {PanelName} but no {TemplateName} under it.");
                    continue;
                }

                Build(panel, template, machine);
            }
        }

        private void Build(Transform panel, RectTransform template, ManufacturingProduction machine)
        {
            var clone = UnityEngine.Object.Instantiate(template.gameObject, panel);
            clone.name = CloneName;

            StripByName(clone, "PlayMakerFSM");
            foreach (var t in clone.GetComponentsInChildren<Transform>(true))
                StripByName(t.gameObject, "SetLocalizationString");

            var rect = clone.GetComponent<RectTransform>();
            if (rect != null)
            {
                // Directly under the template with a small gap. The template sits at y=279 and the
                // next thing down is AssembledRecipeText, whose 200-high box tops out around y=207,
                // so this lands in clear space.
                rect.anchoredPosition = template.anchoredPosition + new Vector2(0f, -(template.sizeDelta.y + 6f));
                rect.sizeDelta = template.sizeDelta;
            }

            SetLabel(clone, "Queue refill runs");

            var button = clone.GetComponent<Button>();
            if (button != null)
            {
                button.onClick.RemoveAllListeners();

                // Bound to THIS machine rather than letting Run() pick the nearest. You are standing
                // at the one whose panel you just clicked, so nearest would almost always agree -
                // but "almost always" is how you end up queueing into the wrong machine in a shop
                // with two of them side by side.
                var target = machine;
                button.onClick.AddListener(() => ManufactureOrder.Run(target));
            }
            else
            {
                Plugin.Log.LogWarning("[ProduceButton] The clone has no Button; it will do nothing.");
            }

            Plugin.Log.LogInfo($"[ProduceButton] Added 'Queue refill runs' to {machine.gameObject.name}.");
        }

        private static void StripByName(GameObject go, string typeName)
        {
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != typeName) continue;
                UnityEngine.Object.DestroyImmediate(c);
            }
        }

        private static void SetLabel(GameObject clone, string text)
        {
            foreach (var c in clone.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.GetType().Name != "TextMeshProUGUI") continue;

                var prop = c.GetType().GetProperty("text");
                if (prop != null) prop.SetValue(c, text, null);

                c.gameObject.name = "SMT_QueueRefill_Text";
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

        private void Complain(string message)
        {
            if (_complaints >= 3) return;
            _complaints++;
            Plugin.Log.LogWarning($"[ProduceButton] {message}");
        }
    }
}
