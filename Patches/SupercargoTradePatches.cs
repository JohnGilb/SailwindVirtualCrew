using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    [HarmonyPatch(typeof(GoPointer), "LateUpdate")]
    internal static class SupercargoSellInputPatch
    {
        private static void Postfix(GoPointer __instance)
        {
            if (__instance == null || __instance.GetHeldItem() != null)
                return;

            var item = __instance.GetPointedAtItem();
            if (!item)
                return;

            if (Plugin.SupercargoSellAtPortKey != null && Plugin.SupercargoSellAtPortKey.Value.IsDown())
                SupercargoTradeService.TryToggleSellAtPort(item);

            if (Plugin.SupercargoKeepCargoKey != null && Plugin.SupercargoKeepCargoKey.Value.IsDown())
                SupercargoTradeService.TryToggleKeep(item);
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
            if (!item)
                return;

            bool canSell = SupercargoTradeService.CanOfferSellAtPort(item);
            bool canKeep = SupercargoTradeService.CanMarkKeep(item);
            if (!canSell && !canKeep)
                return;

            var controlsText = ControlsTextField.GetValue(__instance) as TextMesh;
            if (controlsText == null)
                return;

            if (!string.IsNullOrEmpty(controlsText.text) && !controlsText.text.EndsWith("\n"))
                controlsText.text += "\n";

            if (canSell)
            {
                controlsText.text += GetSellKeyName()
                    + (SupercargoTradeService.IsMarkedForHaulSell(item) ? " cancel port sale" : " sell at port");
            }

            if (canKeep)
            {
                if (!controlsText.text.EndsWith("\n"))
                    controlsText.text += "\n";

                controlsText.text += GetKeepKeyName()
                    + (SupercargoTradeService.IsMarkedKeep(item) ? " unmark keep" : " mark keep");
            }
        }

        private static string GetSellKeyName()
        {
            if (Plugin.SupercargoSellAtPortKey == null
                || Plugin.SupercargoSellAtPortKey.Value.MainKey == KeyCode.None)
                return "X";

            return Plugin.SupercargoSellAtPortKey.Value.MainKey.ToString();
        }

        private static string GetKeepKeyName()
        {
            if (Plugin.SupercargoKeepCargoKey == null
                || Plugin.SupercargoKeepCargoKey.Value.MainKey == KeyCode.None)
                return "N";

            return Plugin.SupercargoKeepCargoKey.Value.MainKey.ToString();
        }
    }

    [HarmonyPatch(typeof(SaveablePrefab), "RegisterToSave")]
    internal static class SupercargoKeepMarkerRestorePatch
    {
        private static void Postfix(SaveablePrefab __instance)
        {
            var item = __instance != null ? __instance.GetComponent<ShipItem>() : null;
            if (item == null)
                return;

            SupercargoTradeService.RefreshPersistentKeepMarker(item);
        }
    }
}
