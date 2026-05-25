using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    [HarmonyPatch(typeof(PortDude), "OnTriggerEnter")]
    class PortDudeEnterPatches
    {
        [HarmonyPostfix]
        static void Postfix(PortDude __instance, Collider other)
        {
            if (!other.CompareTag("Player")) return;
            var manager = VirtualCrewManager.Instance;
            var port = __instance.GetPort();
            manager.SetCurrentPort(port);
            manager.TryQuartermasterRefillWaterAtPort(port);
        }
    }

    [HarmonyPatch(typeof(PortDude), "OnTriggerExit")]
    class PortDudeExitPatches
    {
        [HarmonyPostfix]
        static void Postfix(PortDude __instance, Collider other)
        {
            if (!other.CompareTag("Player")) return;
            VirtualCrewManager.Instance.ClearCurrentPort();
        }
    }
}
