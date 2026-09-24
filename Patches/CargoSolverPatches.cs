using HarmonyLib;

namespace SailwindVirtualCrew
{
    // Developer spike input: the solve key previews (then places) the item the player is holding or looking at; the
    // claim key records that the looked-at item's current spot works, for tuning the solver.
    [HarmonyPatch(typeof(GoPointer), "LateUpdate")]
    internal static class CargoSolverInputPatch
    {
        private static void Postfix(GoPointer __instance)
        {
            if (!DeveloperMode.IsEnabled
                || __instance == null
                || TextInputHotkeySuppressor.ShouldSuppressFavoriteActionHotkeys)
                return;

            if (Plugin.CargoSolverClaimKey != null && Plugin.CargoSolverClaimKey.Value.IsDown())
            {
                var claimed = __instance.GetPointedAtItem();
                if (claimed)
                    CargoPackingSolver.ClaimSpot(claimed);
                return;
            }

            if (Plugin.CargoSolverKey == null || !Plugin.CargoSolverKey.Value.IsDown())
                return;

            var held = __instance.GetHeldItem() as ShipItem;
            if (held)
            {
                CargoPackingSolver.OnSolveKey(held, held: true);
                return;
            }

            var pointed = __instance.GetPointedAtItem();
            if (pointed)
                CargoPackingSolver.OnSolveKey(pointed, held: false);
        }
    }
}
