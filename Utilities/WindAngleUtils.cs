using UnityEngine;

namespace SailwindVirtualCrew
{
    public enum StandingOrderWindState
    {
        None,
        PortClose,
        PortBeam,
        PortBroad,
        PortRun,
        StbdClose,
        StbdBeam,
        StbdBroad,
        StbdRun,
        // Special triggers. These are not wind directions, but share the same saved
        // standing-order storage; they fire once when their condition becomes true.
        HeelAbove20,
        HeelAbove40,
        WaterAbove30
    }

    internal static class WindAngleUtils
    {
        public static bool TryGetApparentWindAngle(out float angle)
        {
            angle = 0f;
            Transform boat = GetSailInfoBoatTransform();
            if (boat == null)
                return false;

            if (!TryGetSailInfoApparentWind(out Vector3 apparentWind))
                return false;

            angle = Vector3.SignedAngle(-boat.forward, apparentWind.normalized, Vector3.up);
            return true;
        }

        public static bool TryGetSailInfoApparentWind(out Vector3 apparentWind)
        {
            apparentWind = Vector3.zero;
            Transform boat = GetSailInfoBoatTransform();
            Rigidbody body = GetSailInfoBoatRigidbody(boat);
            if (boat == null || body == null)
                return false;

            apparentWind = Wind.currentWind - body.velocity;
            apparentWind.y = 0f;
            return apparentWind.sqrMagnitude >= 0.001f;
        }

        public static Transform GetSailInfoBoatTransform()
        {
            var worldBoat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (!worldBoat)
                return null;

            var purchasableBoat = worldBoat.GetComponentInParent<PurchasableBoat>();
            return purchasableBoat ? purchasableBoat.transform : worldBoat.transform;
        }

        public static Rigidbody GetSailInfoBoatRigidbody(Transform boat)
        {
            if (boat == null)
                return null;

            return boat.GetComponent<Rigidbody>() ?? boat.GetComponentInParent<Rigidbody>();
        }

        // Heel is the boat's roll from vertical, ignoring pitch, so bow-down waves don't count.
        public static bool TryGetHeelAngle(out float heel)
        {
            heel = 0f;
            Transform boat = GetSailInfoBoatTransform();
            if (boat == null)
                return false;

            heel = Mathf.Asin(Mathf.Clamp01(Mathf.Abs(boat.right.y))) * Mathf.Rad2Deg;
            return true;
        }

        public static bool IsSpecialTrigger(StandingOrderWindState state)
        {
            return state == StandingOrderWindState.HeelAbove20
                || state == StandingOrderWindState.HeelAbove40
                || state == StandingOrderWindState.WaterAbove30;
        }

        public static StandingOrderWindState ClassifyStandingOrderWindState(float angle)
        {
            float abs = Mathf.Abs(angle);
            if (abs < 10f)
                return StandingOrderWindState.None;

            bool starboard = angle >= 0f;
            if (abs < 60f)
                return starboard ? StandingOrderWindState.StbdClose : StandingOrderWindState.PortClose;
            if (abs < 115f)
                return starboard ? StandingOrderWindState.StbdBeam : StandingOrderWindState.PortBeam;
            if (abs < 160f)
                return starboard ? StandingOrderWindState.StbdBroad : StandingOrderWindState.PortBroad;

            return starboard ? StandingOrderWindState.StbdRun : StandingOrderWindState.PortRun;
        }

        public static string FormatWindAngleCoarse(float angle)
        {
            float abs = Mathf.Abs(angle);
            string side = angle >= 0f ? "Stbd" : "Port";
            if (abs < 10f) return "Ahead";
            if (abs < 60f) return side + " Close";
            if (abs < 115f) return side + " Beam";
            if (abs < 160f) return side + " Broad";
            return side + " Run";
        }

        public static string GetStateLabel(StandingOrderWindState state)
        {
            switch (state)
            {
                case StandingOrderWindState.PortClose: return "Port Close";
                case StandingOrderWindState.PortBeam: return "Port Beam";
                case StandingOrderWindState.PortBroad: return "Port Broad";
                case StandingOrderWindState.PortRun: return "Port Run";
                case StandingOrderWindState.StbdClose: return "Stbd Close";
                case StandingOrderWindState.StbdBeam: return "Stbd Beam";
                case StandingOrderWindState.StbdBroad: return "Stbd Broad";
                case StandingOrderWindState.StbdRun: return "Stbd Run";
                case StandingOrderWindState.HeelAbove20: return "Heel > 20°";
                case StandingOrderWindState.HeelAbove40: return "Heel > 40°";
                case StandingOrderWindState.WaterAbove30: return "Water > 30%";
                default: return "Ahead";
            }
        }

        public static bool TryGetMirroredStarboardState(StandingOrderWindState portState, out StandingOrderWindState starboardState)
        {
            switch (portState)
            {
                case StandingOrderWindState.PortClose:
                    starboardState = StandingOrderWindState.StbdClose;
                    return true;
                case StandingOrderWindState.PortBeam:
                    starboardState = StandingOrderWindState.StbdBeam;
                    return true;
                case StandingOrderWindState.PortBroad:
                    starboardState = StandingOrderWindState.StbdBroad;
                    return true;
                case StandingOrderWindState.PortRun:
                    starboardState = StandingOrderWindState.StbdRun;
                    return true;
                default:
                    starboardState = StandingOrderWindState.None;
                    return false;
            }
        }
    }
}
