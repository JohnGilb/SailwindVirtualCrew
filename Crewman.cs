using System;

namespace SailwindVirtualCrew
{
    public class Crewman
    {
        public string   Name { get; }
        public ShipRole Role { get; }

        // True stats — used for all calculations
        public int Strength     { get; }
        public int Dexterity    { get; }
        public int Constitution { get; }
        public int Intelligence { get; }
        public int Wisdom       { get; }
        public int Charisma     { get; }

        // Advertised stats — shown in UI (offset -1..+3 from true, minimum 1)
        public int AdvStrength     { get; }
        public int AdvDexterity    { get; }
        public int AdvConstitution { get; }
        public int AdvIntelligence { get; }
        public int AdvWisdom       { get; }
        public int AdvCharisma     { get; }

        public object CurrentTask { get; set; }
        public bool IsOccupied => CurrentTask != null;

        public Crewman(string name, ShipRole role, Random rng)
        {
            Name         = name;
            Role         = role;
            Strength     = rng.Next(1, 6);
            Dexterity    = rng.Next(1, 6);
            Constitution = rng.Next(1, 6);
            Intelligence = rng.Next(1, 6);
            Wisdom       = rng.Next(1, 6);
            Charisma     = rng.Next(1, 6);

            AdvStrength     = Advertise(Strength,     rng);
            AdvDexterity    = Advertise(Dexterity,    rng);
            AdvConstitution = Advertise(Constitution, rng);
            AdvIntelligence = Advertise(Intelligence, rng);
            AdvWisdom       = Advertise(Wisdom,       rng);
            AdvCharisma     = Advertise(Charisma,     rng);
        }

        public Crewman(string name, ShipRole role,
            int strength, int dexterity, int constitution, int intelligence, int wisdom, int charisma,
            int advStrength, int advDexterity, int advConstitution, int advIntelligence, int advWisdom, int advCharisma)
        {
            Name         = name;
            Role         = role;
            Strength     = strength;
            Dexterity    = dexterity;
            Constitution = constitution;
            Intelligence = intelligence;
            Wisdom       = wisdom;
            Charisma     = charisma;
            AdvStrength     = advStrength;
            AdvDexterity    = advDexterity;
            AdvConstitution = advConstitution;
            AdvIntelligence = advIntelligence;
            AdvWisdom       = advWisdom;
            AdvCharisma     = advCharisma;
        }

        private static int Advertise(int trueStat, Random rng) =>
            Math.Max(1, trueStat + rng.Next(-1, 4));

        // Returns the two role-specific advertised stats shown in the UI.
        public string AdvertisedStatLine()
        {
            switch (Role)
            {
                case ShipRole.Deckhand:      return $"S{AdvStrength}  D{AdvDexterity}";
                case ShipRole.Navigator:     return $"D{AdvDexterity}  I{AdvIntelligence}";
                case ShipRole.Pilot:         return $"I{AdvIntelligence}  Co{AdvConstitution}";
                case ShipRole.ChiefOfficer:  return $"W{AdvWisdom}  Ch{AdvCharisma}";
                case ShipRole.Chef:          return $"D{AdvDexterity}  W{AdvWisdom}";
                case ShipRole.Quartermaster: return $"S{AdvStrength}  W{AdvWisdom}";
                case ShipRole.Supercargo:    return $"I{AdvIntelligence}  Ch{AdvCharisma}";
                case ShipRole.Lookout:       return $"D{AdvDexterity}  W{AdvWisdom}";
                default:                     return $"S{AdvStrength}  D{AdvDexterity}";
            }
        }

        // Returns the two role-specific true stats (developer mode only).
        public string TrueStatLine()
        {
            switch (Role)
            {
                case ShipRole.Deckhand:      return $"S{Strength}  D{Dexterity}";
                case ShipRole.Navigator:     return $"D{Dexterity}  I{Intelligence}";
                case ShipRole.Pilot:         return $"I{Intelligence}  Co{Constitution}";
                case ShipRole.ChiefOfficer:  return $"W{Wisdom}  Ch{Charisma}";
                case ShipRole.Chef:          return $"D{Dexterity}  W{Wisdom}";
                case ShipRole.Quartermaster: return $"S{Strength}  W{Wisdom}";
                case ShipRole.Supercargo:    return $"I{Intelligence}  Ch{Charisma}";
                case ShipRole.Lookout:       return $"D{Dexterity}  W{Wisdom}";
                default:                     return $"S{Strength}  D{Dexterity}";
            }
        }

        public CrewmanSaveData ToSaveData() => new CrewmanSaveData
        {
            name = Name, role = Role,
            strength = Strength, dexterity = Dexterity, constitution = Constitution,
            intelligence = Intelligence, wisdom = Wisdom, charisma = Charisma,
            advStrength = AdvStrength, advDexterity = AdvDexterity, advConstitution = AdvConstitution,
            advIntelligence = AdvIntelligence, advWisdom = AdvWisdom, advCharisma = AdvCharisma
        };
    }
}
