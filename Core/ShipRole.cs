namespace SailwindVirtualCrew
{
    public enum ShipRole
    {
        Deckhand,
        Navigator,
        Pilot,
        ChiefOfficer,
        Chef,
        Quartermaster,
        Supercargo,
        Lookout
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
