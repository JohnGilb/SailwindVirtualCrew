using HarmonyLib;
using ModSaveBackups;
using System.Collections.Generic;
using System.Linq;

namespace SailwindVirtualCrew
{
    [HarmonyPatch(typeof(SaveLoadManager))]
    class SaveLoadPatches
    {
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
                        name = g.Name,
                        memberIdentifiers = g.MemberIdentifiers.ToList()
                    })
                    .ToList();
            }

            var windowPositions = new Dictionary<string, float[]>();
            foreach (var w in Plugin.Instance.GetComponents<IWindowPosition>())
                windowPositions[w.WindowKey] = w.GetPosition();

            var container = new VirtualCrewSaveData
            {
                vessels      = new Dictionary<string, VesselSaveData>(mgr.AllVesselsData),
                shipCrew     = mgr.Crew.Select(c => c.ToSaveData()).ToList(),
                portCrewPools = mgr.PortCrewPools.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(c => c.ToSaveData()).ToList()),
                windowPositions = windowPositions
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
            if (data.windowPositions != null)
                foreach (var w in Plugin.Instance.GetComponents<IWindowPosition>())
                    if (data.windowPositions.TryGetValue(w.WindowKey, out var pos) && pos.Length >= 2)
                        w.SetPosition(pos[0], pos[1], pos.Length >= 3 ? pos[2] : 0f);
        }
    }
}
