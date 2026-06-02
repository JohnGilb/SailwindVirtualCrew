using HarmonyLib;

namespace SailwindVirtualCrew
{
    [HarmonyPatch(typeof(PlayerEmbarkerNew), "PlayerEmbark")]
    internal static class PlayerEmbarkerNewPatches
    {
        private static void Postfix()
        {
            Plugin.Instance?.RequestEmbarkedVesselScan();
        }
    }
}
