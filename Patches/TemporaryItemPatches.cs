using HarmonyLib;

namespace SailwindVirtualCrew
{
    // A crew-held item copy is visual only: skip the physics body, collision checker and save hooks that
    // ShipItem sets up when it wakes, and the matching teardown. See CrewTemporaryItems.
    [HarmonyPatch(typeof(ShipItem), "Awake")]
    class ShipItemAwakeTemporaryItemPatch
    {
        [HarmonyPrefix]
        static bool Prefix(ShipItem __instance)
        {
            return !CrewTemporaryItems.IsTemporary(__instance);
        }
    }

    [HarmonyPatch(typeof(ShipItem), "OnDestroy")]
    class ShipItemOnDestroyTemporaryItemPatch
    {
        [HarmonyPrefix]
        static bool Prefix(ShipItem __instance)
        {
            return !CrewTemporaryItems.IsTemporary(__instance);
        }
    }
}
