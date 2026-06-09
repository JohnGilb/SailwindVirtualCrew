using UnityEngine;

namespace SailwindVirtualCrew
{
    public class StandingOrderTargets
    {
        public bool HasHalyard;
        public float Halyard;
        public bool HasSimpleSheet;
        public float SimpleSheet;
        public bool HasPortSheet;
        public float PortSheet;
        public bool HasStarboardSheet;
        public float StarboardSheet;
        public bool HasTrim;

        public bool HasAny =>
            HasHalyard || HasSimpleSheet || HasPortSheet || HasStarboardSheet || HasTrim;

        public static StandingOrderTargets FromSaveData(StandingOrderSailSaveData data)
        {
            if (data == null)
                return new StandingOrderTargets();

            return new StandingOrderTargets
            {
                HasHalyard = data.hasHalyard,
                Halyard = Mathf.Clamp01(data.halyard),
                HasSimpleSheet = data.hasSimpleSheet,
                SimpleSheet = Mathf.Clamp01(data.simpleSheet),
                HasPortSheet = data.hasPortSheet,
                PortSheet = Mathf.Clamp01(data.portSheet),
                HasStarboardSheet = data.hasStarboardSheet,
                StarboardSheet = Mathf.Clamp01(data.starboardSheet)
            };
        }

        public void ApplyTo(StandingOrderSailSaveData data)
        {
            data.hasHalyard = HasHalyard;
            data.halyard = Mathf.Clamp01(Halyard);
            data.hasSimpleSheet = HasSimpleSheet;
            data.simpleSheet = Mathf.Clamp01(SimpleSheet);
            data.hasPortSheet = HasPortSheet;
            data.portSheet = Mathf.Clamp01(PortSheet);
            data.hasStarboardSheet = HasStarboardSheet;
            data.starboardSheet = Mathf.Clamp01(StarboardSheet);
        }

        public StandingOrderTargets Clone()
        {
            return new StandingOrderTargets
            {
                HasHalyard = HasHalyard,
                Halyard = Halyard,
                HasSimpleSheet = HasSimpleSheet,
                SimpleSheet = SimpleSheet,
                HasPortSheet = HasPortSheet,
                PortSheet = PortSheet,
                HasStarboardSheet = HasStarboardSheet,
                StarboardSheet = StarboardSheet,
                HasTrim = HasTrim
            };
        }

        public StandingOrderTargets MirroredFor(ICommonSailActions sail)
        {
            var copy = Clone();
            if (sail is DualSheetSail)
            {
                bool hasPort = copy.HasPortSheet;
                float port = copy.PortSheet;
                copy.HasPortSheet = copy.HasStarboardSheet;
                copy.PortSheet = copy.StarboardSheet;
                copy.HasStarboardSheet = hasPort;
                copy.StarboardSheet = port;
            }

            return copy;
        }
    }
}
