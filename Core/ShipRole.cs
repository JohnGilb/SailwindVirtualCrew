namespace SailwindVirtualCrew
{
    public enum CrewShift
    {
        AdHoc,
        Day,
        Night
    }

    public enum ShipRole
    {
        Deckhand,
        Navigator,
        Pilot,
        ChiefOfficer,
        Chef,
        Quartermaster,
        Supercargo,
        Lookout,
        Steward
    }

    public static class CrewShiftExtensions
    {
        public static string DisplayName(this CrewShift shift)
        {
            switch (shift)
            {
                case CrewShift.Day:   return "Day";
                case CrewShift.Night: return "Night";
                default:              return "Ad-Hoc";
            }
        }

        public static string DisplayTag(this CrewShift shift)
        {
            switch (shift)
            {
                case CrewShift.Day:   return " [D]";
                case CrewShift.Night: return " [N]";
                default:              return string.Empty;
            }
        }
    }

    public static class ShipRoleExtensions
    {
        public static string DisplayName(this ShipRole role)
        {
            switch (role)
            {
                case ShipRole.ChiefOfficer: return "First Officer";
                default:                    return role.ToString();
            }
        }
    }
}
