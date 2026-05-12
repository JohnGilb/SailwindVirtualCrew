using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class ModIntegrations
    {
        internal static void Initialize(Harmony harmony)
        {
            TryGateCargoController(harmony);
            TryGateProfitPercent(harmony);
        }

        private static void TryGateCargoController(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.ContainsKey("com.jakeinaboat.cargocontroller"))
                return;

            try
            {
                var uiType = AccessTools.TypeByName("CargoController.CargoControllerUI");
                if (uiType == null)
                {
                    Debug.LogWarning("[VirtualCrew] CargoController found but CargoControllerUI type not located.");
                    return;
                }

                var setVisible = uiType.GetMethod("SetVisible", new[] { typeof(bool) });
                if (setVisible == null)
                {
                    Debug.LogWarning("[VirtualCrew] CargoControllerUI.SetVisible not found.");
                    return;
                }

                harmony.Patch(setVisible, prefix: new HarmonyMethod(
                    typeof(CargoControllerGate), nameof(CargoControllerGate.SetVisiblePrefix)));
                Debug.Log("[VirtualCrew] CargoController gated: Quartermaster required.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VirtualCrew] CargoController gate setup failed: {ex.Message}");
            }
        }

        private static void TryGateProfitPercent(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.ContainsKey("pr0skynesis.profitpercent"))
                return;

            try
            {
                var patchesType = AccessTools.TypeByName("ProfitPercent.ProfitPercentPatches");
                if (patchesType == null)
                {
                    Debug.LogWarning("[VirtualCrew] ProfitPercent found but ProfitPercentPatches type not located.");
                    return;
                }

                var mainPatch = patchesType.GetMethod("MainPatch", BindingFlags.Public | BindingFlags.Static);
                if (mainPatch == null)
                {
                    Debug.LogWarning("[VirtualCrew] ProfitPercentPatches.MainPatch not found.");
                    return;
                }

                harmony.Patch(mainPatch, prefix: new HarmonyMethod(
                    typeof(ProfitPercentGate), nameof(ProfitPercentGate.MainPatchPrefix)));
                Debug.Log("[VirtualCrew] ProfitPercent gated: Supercargo required.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VirtualCrew] ProfitPercent gate setup failed: {ex.Message}");
            }
        }
    }

    internal static class CargoControllerGate
    {
        // Blocks CargoControllerUI.SetVisible(true) unless a Quartermaster is hired.
        public static bool SetVisiblePrefix(bool visible)
        {
            if (!visible) return true; // always allow closing
            var mgr = VirtualCrewManager.Instance;
            if (mgr == null) return true; // before save loaded
            return mgr.Crew.Any(c => c.Role == ShipRole.Quartermaster);
        }
    }

    internal static class ProfitPercentGate
    {
        private static readonly string[] ColumnNames =
        {
            "productionText", "percentText", "perPoundText",
            "bdBestDeals", "bdPercent", "bdPerPound", "bdAbsolute"
        };

        // Blocks ProfitPercentPatches.MainPatch (EconomyUI.ShowGoodPage postfix)
        // unless a Supercargo is hired. Clears columns so no stale data shows.
        public static bool MainPatchPrefix()
        {
            var mgr = VirtualCrewManager.Instance;
            if (mgr == null) return true; // before save loaded
            if (mgr.Crew.Any(c => c.Role == ShipRole.Supercargo)) return true;

            ClearProfitColumns();
            return false;
        }

        private static void ClearProfitColumns()
        {
            if (EconomyUI.instance == null) return;
            var detailsUI = EconomyUI.instance.transform
                .Find("good details (right panel)")
                ?.Find("details UI");
            if (detailsUI == null) return;

            foreach (var name in ColumnNames)
            {
                var tm = detailsUI.Find(name)?.GetComponent<TextMesh>();
                if (tm != null) tm.text = "";
            }
            detailsUI.Find("highlightBar")?.gameObject.SetActive(false);
        }
    }
}
