using System;

namespace SailwindVirtualCrew
{
    public class Crewman
    {
        public string   Id         { get; private set; }
        public string   Name       { get; private set; }
        public ShipRole Role       { get; }
        public int      ModelIndex { get; private set; }

        // True stat backing fields — always hold the real value regardless of exhaustion.
        private readonly int _strength;
        private readonly int _dexterity;
        private readonly int _intelligence;
        private readonly int _wisdom;
        private readonly int _charisma;

        // True stats — return 1 when exhausted (except Constitution, which is never impaired).
        public int Strength     => IsExhausted ? 1 : _strength;
        public int Dexterity    => IsExhausted ? 1 : _dexterity;
        public int Constitution { get; }
        public int Intelligence => IsExhausted ? 1 : _intelligence;
        public int Wisdom       => IsExhausted ? 1 : _wisdom;
        public int Charisma     => IsExhausted ? 1 : _charisma;

        // Advertised stats — shown in UI (offset -1..+3 from true, minimum 1)
        public int AdvStrength     { get; }
        public int AdvDexterity    { get; }
        public int AdvConstitution { get; }
        public int AdvIntelligence { get; }
        public int AdvWisdom       { get; }
        public int AdvCharisma     { get; }

        public object CurrentTask { get; set; }
        public bool IsOccupied => CurrentTask != null;

        // ── Stamina ─────────────────────────────────────────────────────────
        // MaxStamina in minutes; baseline 960 min (16 h) at Constitution 3.
        public int   MaxStamina     => 960 + (Constitution - 3) * 120;
        public float CurrentStamina { get; private set; }
        public bool  IsExhausted    => CurrentStamina <= 0f;

        public void DrainStamina(float amount)
        {
            CurrentStamina = Math.Max(0f, CurrentStamina - amount);
        }

        public void RestoreStamina(float amount)
        {
            CurrentStamina = Math.Min(MaxStamina, CurrentStamina + amount);
        }

        public string FatigueTag
        {
            get
            {
                if (IsExhausted)              return "Exhausted";
                if (CurrentStamina <= 120f)   return "Tired";
                if (CurrentStamina <= 360f)   return "Flagging";
                if (CurrentStamina <= 600f)   return "Well";
                return "Fresh";
            }
        }

        public Crewman(string name, ShipRole role, Random rng)
        {
            Id           = Guid.NewGuid().ToString("N");
            Name         = name;
            Role         = role;
            ModelIndex   = rng.Next(64);
            _strength    = rng.Next(1, 6);
            _dexterity   = rng.Next(1, 6);
            Constitution = rng.Next(1, 6);
            _intelligence = rng.Next(1, 6);
            _wisdom      = rng.Next(1, 6);
            _charisma    = rng.Next(1, 6);

            AdvStrength     = Advertise(_strength,     rng);
            AdvDexterity    = Advertise(_dexterity,    rng);
            AdvConstitution = Advertise(Constitution,  rng);
            AdvIntelligence = Advertise(_intelligence, rng);
            AdvWisdom       = Advertise(_wisdom,       rng);
            AdvCharisma     = Advertise(_charisma,     rng);

            CurrentStamina = MaxStamina;
        }

        // Used for save/load restoration. Pass currentStamina < 0 to default to MaxStamina
        // (handles old saves that predate the stamina system).
        public Crewman(string name, ShipRole role,
            int strength, int dexterity, int constitution, int intelligence, int wisdom, int charisma,
            int advStrength, int advDexterity, int advConstitution, int advIntelligence, int advWisdom, int advCharisma,
            float currentStamina = -1f,
            string id = null,
            int modelIndex = -1)
        {
            Id           = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
            Name         = name;
            Role         = role;
            ModelIndex   = modelIndex >= 0 ? modelIndex : 0;
            _strength    = strength;
            _dexterity   = dexterity;
            Constitution = constitution;
            _intelligence = intelligence;
            _wisdom      = wisdom;
            _charisma    = charisma;
            AdvStrength     = advStrength;
            AdvDexterity    = advDexterity;
            AdvConstitution = advConstitution;
            AdvIntelligence = advIntelligence;
            AdvWisdom       = advWisdom;
            AdvCharisma     = advCharisma;

            CurrentStamina = currentStamina >= 0f ? currentStamina : MaxStamina;
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
        // Uses backing fields so exhaustion doesn't distort the displayed values.
        public string TrueStatLine()
        {
            switch (Role)
            {
                case ShipRole.Deckhand:      return $"S{_strength}  D{_dexterity}";
                case ShipRole.Navigator:     return $"D{_dexterity}  I{_intelligence}";
                case ShipRole.Pilot:         return $"I{_intelligence}  Co{Constitution}";
                case ShipRole.ChiefOfficer:  return $"W{_wisdom}  Ch{_charisma}";
                case ShipRole.Chef:          return $"D{_dexterity}  W{_wisdom}";
                case ShipRole.Quartermaster: return $"S{_strength}  W{_wisdom}";
                case ShipRole.Supercargo:    return $"I{_intelligence}  Ch{_charisma}";
                case ShipRole.Lookout:       return $"D{_dexterity}  W{_wisdom}";
                default:                     return $"S{_strength}  D{_dexterity}";
            }
        }

        public void Rename(string newName) { Name = newName; }

        public CrewmanSaveData ToSaveData() => new CrewmanSaveData
        {
            id = Id,
            name = Name, role = Role,
            strength = _strength, dexterity = _dexterity, constitution = Constitution,
            intelligence = _intelligence, wisdom = _wisdom, charisma = _charisma,
            advStrength = AdvStrength, advDexterity = AdvDexterity, advConstitution = AdvConstitution,
            advIntelligence = AdvIntelligence, advWisdom = AdvWisdom, advCharisma = AdvCharisma,
            currentStamina = CurrentStamina,
            modelIndex = ModelIndex
        };
    }
}
