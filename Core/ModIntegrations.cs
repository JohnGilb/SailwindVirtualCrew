using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Collections.Generic;
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
            if (!ShouldManageExternalModFeatures())
            {
                Debug.Log("[VirtualCrew] Profit Percent and Cargo Controller integration gates disabled by config.");
                return;
            }

            TryGateCargoController(harmony);
            TryGateProfitPercent(harmony);
        }

        internal static bool ShouldManageExternalModFeatures()
        {
            return Plugin.RequireCrewForExternalModFeatures == null
                || Plugin.RequireCrewForExternalModFeatures.Value;
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
            if (!ModIntegrations.ShouldManageExternalModFeatures()) return true;
            var mgr = VirtualCrewManager.Instance;
            if (mgr == null) return true; // before save loaded
            return CanUseCargoController();
        }

        public static bool CanUseCargoController()
        {
            if (!ModIntegrations.ShouldManageExternalModFeatures()) return true;
            return CrewRoleAvailability.HasAwakeCrew(ShipRole.Quartermaster);
        }
    }

    internal static class CargoControllerPortCargoHotkey
    {
        private static Type cargoControllerType;
        private static Type cargoControllerUiType;
        private static Type warehouseTrackerType;
        private static FieldInfo uiInstanceField;
        private static FieldInfo pointerField;
        private static FieldInfo nearestPortDistanceField;
        private static FieldInfo maximumPortDistanceField;
        private static FieldInfo missionGoodsField;
        private static FieldInfo nonMissionGoodsField;
        private static MethodInfo pickupGoodMethod;
        private static PropertyInfo enabledProperty;
        private static bool initialized;
        private static bool failed;

        public static void Tick()
        {
            if (!ModIntegrations.ShouldManageExternalModFeatures())
                return;

            if (Plugin.CargoControllerGrabPortCargoKey == null
                || Plugin.CargoControllerGrabPortCargoKey.Value.MainKey == KeyCode.None
                || !Plugin.CargoControllerGrabPortCargoKey.Value.IsDown())
                return;

            TryPickupTopPortCargo();
        }

        private static void TryPickupTopPortCargo()
        {
            if (!CargoControllerGate.CanUseCargoController())
                return;
            if (!EnsureInitialized())
                return;
            if (!IsCargoControllerEnabled())
                return;

            var ui = uiInstanceField.GetValue(null);
            if (ui == null)
                return;

            var pointer = pointerField.GetValue(null) as GoPointer;
            if (pointer == null || pointer.GetHeldItem() != null)
                return;
            if (!IsPortCargoListAvailable(ui))
                return;

            var good = GetTopPortGood();
            if (good == null)
                return;

            try
            {
                pickupGoodMethod.Invoke(ui, new object[] { good });
            }
            catch (TargetInvocationException ex)
            {
                Debug.LogWarning($"[VirtualCrew] CargoController port pickup failed: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VirtualCrew] CargoController port pickup failed: {ex.Message}");
            }
        }

        private static bool EnsureInitialized()
        {
            if (initialized)
                return true;
            if (failed)
                return false;
            if (!Chainloader.PluginInfos.ContainsKey("com.jakeinaboat.cargocontroller"))
                return FailQuietly();

            try
            {
                cargoControllerType = AccessTools.TypeByName("CargoController.CargoController");
                cargoControllerUiType = AccessTools.TypeByName("CargoController.CargoControllerUI");
                warehouseTrackerType = AccessTools.TypeByName("CargoController.IslandMarketWarehouseAreaTracker");

                uiInstanceField = AccessTools.Field(cargoControllerUiType, "Instance");
                pointerField = AccessTools.Field(cargoControllerUiType, "pointer");
                nearestPortDistanceField = AccessTools.Field(cargoControllerUiType, "nearestPortDistance");
                maximumPortDistanceField = AccessTools.Field(cargoControllerUiType, "maximumPortDistance");
                pickupGoodMethod = AccessTools.Method(cargoControllerUiType, "PickupGood");
                enabledProperty = AccessTools.Property(cargoControllerType, "Enabled");
                missionGoodsField = AccessTools.Field(warehouseTrackerType, "missionGoodsInArea");
                nonMissionGoodsField = AccessTools.Field(warehouseTrackerType, "nonMissionGoodsInArea");

                if (cargoControllerType == null
                    || cargoControllerUiType == null
                    || warehouseTrackerType == null
                    || uiInstanceField == null
                    || pointerField == null
                    || nearestPortDistanceField == null
                    || maximumPortDistanceField == null
                    || pickupGoodMethod == null
                    || enabledProperty == null
                    || missionGoodsField == null
                    || nonMissionGoodsField == null)
                {
                    Debug.LogWarning("[VirtualCrew] CargoController hotkey setup failed: expected CargoController members were not found.");
                    failed = true;
                    return false;
                }

                initialized = true;
                Debug.Log("[VirtualCrew] CargoController port cargo hotkey enabled.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VirtualCrew] CargoController hotkey setup failed: {ex.Message}");
                failed = true;
                return false;
            }
        }

        private static bool FailQuietly()
        {
            failed = true;
            return false;
        }

        private static bool IsCargoControllerEnabled()
        {
            try
            {
                return (bool)enabledProperty.GetValue(null, null);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPortCargoListAvailable(object ui)
        {
            var distance = (float)nearestPortDistanceField.GetValue(ui);
            var maximumDistance = (float)maximumPortDistanceField.GetValue(ui);
            return distance <= maximumDistance;
        }

        private static Good GetTopPortGood()
        {
            var missionGoods = GetGoods(missionGoodsField);
            var nonMissionGoods = GetGoods(nonMissionGoodsField);

            if (PlayerMissions.missions != null)
            {
                foreach (var mission in PlayerMissions.missions)
                {
                    if (mission == null)
                        continue;

                    var missionGood = missionGoods.FirstOrDefault(good =>
                        good != null && good.GetMissionIndex() == mission.missionIndex);
                    if (missionGood != null)
                        return missionGood;
                }
            }

            var firstNonMission = nonMissionGoods.FirstOrDefault(good =>
                good != null && good.GetMissionIndex() == -1);
            return firstNonMission;
        }

        private static List<Good> GetGoods(FieldInfo field)
        {
            var goods = field.GetValue(null) as IEnumerable<Good>;
            if (goods == null)
                return new List<Good>();
            return goods.Where(good => good != null).ToList();
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
            if (!ModIntegrations.ShouldManageExternalModFeatures()) return;
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
