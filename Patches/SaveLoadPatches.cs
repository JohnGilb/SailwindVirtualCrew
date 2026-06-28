using HarmonyLib;
using ModSaveBackups;
using System.Collections.Generic;
using System.Linq;

namespace SailwindVirtualCrew
{
    [HarmonyPatch(typeof(SaveLoadManager))]
    class SaveLoadPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("SaveGame")]
        static void BeforeSaveGame()
        {
            VirtualCrewManager.Instance.SettleHaulSellRequestsForSave();
        }

        [HarmonyPostfix]
        [HarmonyPatch("SaveModData")]
        static void DoSave()
        {
            var mgr = VirtualCrewManager.Instance;

            // Sync the current vessel's user-created groups into AllVesselsData before serialising.
            if (mgr.CurrentVesselKey != null)
            {
                if (!mgr.AllVesselsData.ContainsKey(mgr.CurrentVesselKey))
                    mgr.AllVesselsData[mgr.CurrentVesselKey] = new VesselSaveData();

                mgr.AllVesselsData[mgr.CurrentVesselKey].sailGroups = mgr.SailGroups
                    .Where(g => !g.IsAllSails)
                    .Select(g => new SailGroupSaveData
                    {
                        id = g.Id,
                        name = g.Name,
                        memberIdentifiers = g.MemberIdentifiers.ToList()
                    })
                    .ToList();
            }

            var windowPositions = new Dictionary<string, float[]>();
            foreach (var w in Plugin.Instance.GetComponents<IWindowPosition>())
                windowPositions[w.WindowKey] = w.GetPosition();
            var windowVisibility = new Dictionary<string, bool>();
            foreach (var w in Plugin.Instance.GetComponents<IWindowPosition>())
                if (w is UnityEngine.Component component && WindowVisibilityUtility.TryGetVisible(component, out var visible))
                    windowVisibility[w.WindowKey] = visible;
            var navigatorWindow = Plugin.Instance.GetComponent<NavigatorWindow>();
            var pilotingWindow = Plugin.Instance.GetComponent<PilotingWindow>();

            var container = new VirtualCrewSaveData
            {
                firstOfficerSettingsVersion = 1,
                firstOfficerAutoTrimEnabled = mgr.FirstOfficerAutoTrimEnabled,
                firstOfficerStandingOrdersEnabled = mgr.FirstOfficerStandingOrdersEnabled,
                stewardSettingsVersion = 1,
                stewardThirstLimitPercent = mgr.StewardThirstLimitPercent,
                stewardHungerLimitPercent = mgr.StewardHungerLimitPercent,
                maintenanceSettingsVersion = 2,
                maintenanceBailOneDeckhandThresholdPercent = mgr.MaintenanceBailOneDeckhandThresholdPercent,
                maintenanceBailTwoDeckhandsThresholdPercent = mgr.MaintenanceBailTwoDeckhandsThresholdPercent,
                maintenanceBailAllDeckhandsThresholdPercent = mgr.MaintenanceBailAllDeckhandsThresholdPercent,
                maintenanceLanternAutoEnabled = mgr.MaintenanceLanternAutoEnabled,
                maintenanceLanternRefillEnabled = mgr.MaintenanceLanternRefillEnabled,
                vessels      = new Dictionary<string, VesselSaveData>(mgr.AllVesselsData),
                shipCrew     = mgr.Crew.Select(c => c.ToSaveData()).ToList(),
                portCrewPools = mgr.PortCrewPools.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(c => c.ToSaveData()).ToList()),
                windowPositions = windowPositions,
                windowVisibility = windowVisibility,
                totalSalaryPay = mgr.TotalSalaryPay,
                totalSharePayByCurrency = mgr.TotalSharePayByCurrency != null
                    ? mgr.TotalSharePayByCurrency.ToArray()
                    : new int[4],
                cargoPayRecords = mgr.CargoPayRecords != null
                    ? new Dictionary<int, CargoPaySaveData>(mgr.CargoPayRecords)
                    : new Dictionary<int, CargoPaySaveData>(),
                lastPortCrewRefreshDay = mgr.LastPortCrewRefreshDay,
                lookoutCertainties = mgr.GetLookoutCertaintySnapshot(),
                lookoutIdentifiedNames = mgr.GetLookoutIdentifiedNamesSnapshot(),
                lookoutIgnoredUntil = mgr.GetLookoutIgnoredUntilSnapshot(),
                visitedPorts = mgr.GetVisitedPortsSnapshot(),
                quartermasterWaterRefillNextAllowedDay = mgr.GetQuartermasterWaterRefillSnapshot(),
                navigatorToolScan = navigatorWindow != null ? navigatorWindow.GetToolScanSaveData() : null,
                navigatorIslandMap = mgr.GetNavigatorIslandMapSnapshot(),
                piloting = pilotingWindow != null ? pilotingWindow.GetPilotingSaveData() : null
            };
            ModSave.Save(Plugin.Instance.Info, container);
        }

        [HarmonyPostfix]
        [HarmonyPatch("LoadModData")]
        static void DoLoad()
        {
            if (!ModSave.Load(Plugin.Instance.Info, out VirtualCrewSaveData data))
                return;
            if (data.vessels != null)
                VirtualCrewManager.Instance.AllVesselsData = data.vessels;
            VirtualCrewManager.Instance.RestoreShipCrew(data.shipCrew);
            VirtualCrewManager.Instance.RestorePortPools(data.portCrewPools);
            VirtualCrewManager.Instance.RestorePortCrewRefreshDay(data.lastPortCrewRefreshDay);
            VirtualCrewManager.Instance.RestorePayData(data.totalSalaryPay, data.totalSharePayByCurrency, data.cargoPayRecords);
            VirtualCrewManager.Instance.RestoreFirstOfficerSettings(
                data.firstOfficerSettingsVersion,
                data.firstOfficerAutoTrimEnabled,
                data.firstOfficerStandingOrdersEnabled);
            VirtualCrewManager.Instance.RestoreStewardSettings(data.stewardSettingsVersion, data.stewardThirstLimitPercent, data.stewardHungerLimitPercent);
            VirtualCrewManager.Instance.RestoreMaintenanceSettings(
                data.maintenanceSettingsVersion,
                data.maintenanceBailOneDeckhandThresholdPercent,
                data.maintenanceBailTwoDeckhandsThresholdPercent,
                data.maintenanceBailAllDeckhandsThresholdPercent,
                data.maintenanceLanternAutoEnabled,
                data.maintenanceLanternRefillEnabled);
            VirtualCrewManager.Instance.StoreLookoutCertainties(data.lookoutCertainties);
            VirtualCrewManager.Instance.StoreLookoutIdentifiedNames(data.lookoutIdentifiedNames);
            VirtualCrewManager.Instance.StoreLookoutIgnoredUntil(data.lookoutIgnoredUntil);
            VirtualCrewManager.Instance.StoreVisitedPorts(data.visitedPorts);
            VirtualCrewManager.Instance.StoreQuartermasterWaterRefills(data.quartermasterWaterRefillNextAllowedDay);
            VirtualCrewManager.Instance.StoreNavigatorIslandMap(data.navigatorIslandMap);
            var navigatorWindow = Plugin.Instance.GetComponent<NavigatorWindow>();
            if (navigatorWindow != null)
                navigatorWindow.RestoreToolScanSaveData(data.navigatorToolScan);
            var pilotingWindow = Plugin.Instance.GetComponent<PilotingWindow>();
            if (pilotingWindow != null)
                pilotingWindow.RestorePilotingSaveData(data.piloting);
            if (data.windowPositions != null)
                foreach (var w in Plugin.Instance.GetComponents<IWindowPosition>())
                    if (data.windowPositions.TryGetValue(w.WindowKey, out var pos) && pos.Length >= 2)
                        w.SetPosition(pos[0], pos[1], pos.Length >= 3 ? pos[2] : 0f);
            if (data.windowVisibility != null)
                foreach (var w in Plugin.Instance.GetComponents<IWindowPosition>())
                    if (data.windowVisibility.TryGetValue(w.WindowKey, out var visible) && w is UnityEngine.Component component)
                        WindowVisibilityUtility.TrySetVisible(component, visible);
        }
    }
}
