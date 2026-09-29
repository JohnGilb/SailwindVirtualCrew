using HarmonyLib;

namespace SailwindVirtualCrew
{
    // While the steward accelerates time, scale down any rise in BoatDamage.waterLevel so leaks, rain and
    // waves over the rail can't swamp the ship between frames. Overflow only ever adds water; UpdateWaterAndDrag
    // both drains and adds, so only a net rise is scaled.
    [HarmonyPatch(typeof(BoatDamage), "Overflow")]
    internal static class WaitingOverflowPatches
    {
        private static void Prefix(BoatDamage __instance, out float __state)
        {
            __state = __instance.waterLevel;
        }

        private static void Postfix(BoatDamage __instance, float __state)
        {
            WaitingWaterIntake.Scale(__instance, __state);
        }
    }

    [HarmonyPatch(typeof(BoatDamage), "UpdateWaterAndDrag")]
    internal static class WaitingUpdateWaterPatches
    {
        private static void Prefix(BoatDamage __instance, out float __state)
        {
            __state = __instance.waterLevel;
        }

        private static void Postfix(BoatDamage __instance, float __state)
        {
            WaitingWaterIntake.Scale(__instance, __state);
        }
    }

    internal static class WaitingWaterIntake
    {
        internal static void Scale(BoatDamage damage, float before)
        {
            if (!PlayerWaitingState.IsActive || damage.sunk)
                return;

            float rise = damage.waterLevel - before;
            if (rise > 0f)
                damage.waterLevel = before + rise * PlayerWaitingState.WaterIntakeMultiplier;
        }
    }
}
