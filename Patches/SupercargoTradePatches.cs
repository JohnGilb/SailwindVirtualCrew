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

            // The same key loads an item on the dock aboard, or sells one that's aboard.
            if (Plugin.SupercargoSellAtPortKey != null && Plugin.SupercargoSellAtPortKey.Value.IsDown())
            {
                if (!item.currentActualBoat && (CargoLoadService.IsMarkedForLoad(item) || CargoLoadService.CanQueueLoad(item, out _)))
                    CargoLoadService.TryToggleLoad(item);
                else
                    SupercargoTradeService.TryToggleSellAtPort(item);
            }

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

            bool canLoad = !item.currentActualBoat && CargoLoadService.CanOfferLoad(item);
            bool canSell = !canLoad && SupercargoTradeService.CanOfferSellAtPort(item);
            bool canKeep = SupercargoTradeService.CanMarkKeep(item);
            if (!canLoad && !canSell && !canKeep)
                return;

            var controlsText = ControlsTextField.GetValue(__instance) as TextMesh;
            if (controlsText == null)
                return;

            controlsText.text = RemoveSupercargoPrompts(controlsText.text);

            if (canLoad)
                AppendControlLine(controlsText, GetSellKeyName()
                    + (CargoLoadService.IsMarkedForLoad(item) ? " cancel loading" : " load aboard"));

            if (canSell)
                AppendControlLine(controlsText, GetSellKeyName()
                    + (SupercargoTradeService.IsMarkedForHaulSell(item) ? " cancel port sale" : " sell at port"));

            if (canKeep)
                AppendControlLine(controlsText, GetKeepKeyName()
                    + (SupercargoTradeService.IsMarkedKeep(item) ? " unmark keep" : " mark keep"));
        }

        private static void AppendControlLine(TextMesh controlsText, string line)
        {
            if (!string.IsNullOrEmpty(controlsText.text) && !controlsText.text.EndsWith("\n"))
                controlsText.text += "\n";

            controlsText.text += line;
        }

        private static string RemoveSupercargoPrompts(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var lines = text.Split('\n');
            var keptLines = new System.Collections.Generic.List<string>();
            foreach (string line in lines)
            {
                if (!IsSupercargoPromptLine(line))
                    keptLines.Add(line);
            }

            return string.Join("\n", keptLines.ToArray()).TrimEnd('\n');
        }

        private static bool IsSupercargoPromptLine(string line)
        {
            string trimmed = line.Trim();
            return trimmed.EndsWith(" sell at port", System.StringComparison.Ordinal)
                || trimmed.EndsWith(" load aboard", System.StringComparison.Ordinal)
                || trimmed.EndsWith(" cancel loading", System.StringComparison.Ordinal)
                || trimmed.EndsWith(" cancel port sale", System.StringComparison.Ordinal)
                || trimmed.EndsWith(" mark keep", System.StringComparison.Ordinal)
                || trimmed.EndsWith(" unmark keep", System.StringComparison.Ordinal);
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
