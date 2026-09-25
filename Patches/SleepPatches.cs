using HarmonyLib;

namespace SailwindVirtualCrew
{
    [HarmonyPatch(typeof(Sleep), "FallAsleep")]
    internal static class SleepPatches
    {
        private static void Postfix()
        {
            CrewNavigationCoordinator.Instance.ResetLookoutBellCooldown();
        }
    }

    // WakeUp clears GameState.sleepingInTavern, so capture it beforehand. WakeUp also bails out early while
    // the player is still falling asleep, which eyesFullyClosed guards against.
    [HarmonyPatch(typeof(Sleep), "WakeUp")]
    internal static class InnWakeUpPatches
    {
        private static void Prefix(out bool __state)
        {
            __state = GameState.sleepingInTavern && GameState.eyesFullyClosed;
        }

        private static void Postfix(bool __state)
        {
            if (__state)
                VirtualCrewManager.Instance.RestAllCrewAtInn();
        }
    }
}
