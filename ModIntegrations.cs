using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
                Debug.Log("[VirtualCrew] CargoController gated: awake Quartermaster required.");
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
                var showGoodPage = AccessTools.Method(typeof(EconomyUI), "ShowGoodPage");
                if (showGoodPage == null)
                {
                    Debug.LogWarning("[VirtualCrew] ProfitPercent found but EconomyUI.ShowGoodPage not located.");
                    return;
                }

                // Postfix at Low priority so we run after ProfitPercent's Normal-priority postfix,
                // then clear the columns it populated if no Supercargo is awake.
                harmony.Patch(showGoodPage, postfix: new HarmonyMethod(
                    typeof(ProfitPercentGate), nameof(ProfitPercentGate.ShowGoodPagePostfix))
                { priority = Priority.Low });
                Debug.Log("[VirtualCrew] ProfitPercent gated: awake Supercargo required.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VirtualCrew] ProfitPercent gate setup failed: {ex.Message}");
            }
        }
    }

    internal static class CargoControllerGate
    {
        // Blocks CargoControllerUI.SetVisible(true) unless a Quartermaster is awake.
        public static bool SetVisiblePrefix(bool visible)
        {
            if (!visible) return true; // always allow closing
            var mgr = VirtualCrewManager.Instance;
            if (mgr == null) return true; // before save loaded
            return CrewRoleAvailability.HasAwakeCrew(ShipRole.Quartermaster);
        }
    }

    internal static class ProfitPercentGate
    {
        private static readonly string[] ColumnNames =
        {
            "productionText", "percentText", "perPoundText",
            "bdBestDeals", "bdPercent", "bdPerPound", "bdAbsolute"
        };

        private static readonly FieldInfo textProfitField =
            AccessTools.Field(typeof(EconomyUI), "textProfit");

        // Runs after ProfitPercent's postfix on EconomyUI.ShowGoodPage.
        // Clears the mod columns and strips color tags from the base profit column.
        public static void ShowGoodPagePostfix()
        {
            var mgr = VirtualCrewManager.Instance;
            if (mgr == null) return;
            if (CrewRoleAvailability.HasAwakeCrew(ShipRole.Supercargo)) return;
            ClearProfitColumns();
            StripProfitColors();
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

        private static void StripProfitColors()
        {
            if (EconomyUI.instance == null) return;
            var tm = textProfitField?.GetValue(EconomyUI.instance) as TextMesh;
            if (tm == null) return;
            tm.text = Regex.Replace(tm.text, @"<[^>]+>", "");
        }
    }

    internal static class CrewRoleAvailability
    {
        internal static bool HasAwakeCrew(ShipRole role)
        {
            var mgr = VirtualCrewManager.Instance;
            return mgr != null && mgr.Crew.Any(c =>
                c.Role == role
                && !c.IsExhausted
                && !(c.CurrentTask is SleepRequest));
        }
    }
}
