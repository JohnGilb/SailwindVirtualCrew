using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    [HarmonyPatch(typeof(GoPointer), "LateUpdate")]
    internal static class SupercargoSellInputPatch
    {
        private static void Postfix(GoPointer __instance)
        {
            if (__instance == null || Plugin.SupercargoSellAtPortKey == null)
                return;

            if (!Plugin.SupercargoSellAtPortKey.Value.IsDown())
                return;

            if (__instance.GetHeldItem() != null)
                return;

            var item = __instance.GetPointedAtItem();
            if (!item)
                return;

            SupercargoTradeService.TryToggleSellAtPort(item);
        }
    }

    [HarmonyPatch(typeof(LookUI), "ShowLookText")]
    internal static class SupercargoSellLookTextPatch
    {
        private static readonly System.Reflection.FieldInfo ControlsTextField =
            AccessTools.Field(typeof(LookUI), "controlsText");

        private static void Postfix(LookUI __instance, GoPointerButton button)
        {
            if (!button)
                return;

            var item = button.GetComponent<ShipItem>();
            if (!item || !SupercargoTradeService.CanOfferSellAtPort(item))
                return;

            var controlsText = ControlsTextField.GetValue(__instance) as TextMesh;
            if (controlsText == null)
                return;

            if (!string.IsNullOrEmpty(controlsText.text) && !controlsText.text.EndsWith("\n"))
                controlsText.text += "\n";

            controlsText.text += GetKeyName()
                + (SupercargoTradeService.IsMarkedForHaulSell(item) ? " cancel port sale" : " sell at port");
        }

        private static string GetKeyName()
        {
            if (Plugin.SupercargoSellAtPortKey == null
                || Plugin.SupercargoSellAtPortKey.Value.MainKey == KeyCode.None)
                return "X";

            return Plugin.SupercargoSellAtPortKey.Value.MainKey.ToString();
        }
    }
}
