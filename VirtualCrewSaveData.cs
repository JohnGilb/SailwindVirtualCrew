using System;
using System.Collections.Generic;

namespace SailwindVirtualCrew
{
    [Serializable]
    public class CrewmanSaveData
    {
        public string id;
        public string   name;
        public ShipRole role;
        public int strength, dexterity, constitution, intelligence, wisdom, charisma;
        public int advStrength, advDexterity, advConstitution, advIntelligence, advWisdom, advCharisma;
        // Negative sentinel means "use MaxStamina on load" (handles saves from before this field existed).
        public float currentStamina = -1f;
        public int modelIndex = -1;
    }

    [Serializable]
    public class SailGroupSaveData
    {
        public string name;
        public List<string> memberIdentifiers = new List<string>();
    }

    [Serializable]
    public class CrewRestLocationSaveData
    {
        public float[] localPosition;
        public float[] localEulerAngles;
    }

    [Serializable]
    public class VesselSaveData
    {
        public string friendlyName;
        public Dictionary<string, string> sailFriendlyNames = new Dictionary<string, string>();
        public List<SailGroupSaveData> sailGroups = new List<SailGroupSaveData>();
        public Dictionary<string, CrewRestLocationSaveData> crewRestLocations = new Dictionary<string, CrewRestLocationSaveData>();
    }

    [Serializable]
    public class VirtualCrewSaveData
    {
        public Dictionary<string, VesselSaveData> vessels = new Dictionary<string, VesselSaveData>();
        public List<CrewmanSaveData> shipCrew;
        public Dictionary<string, List<CrewmanSaveData>> portCrewPools;
        public Dictionary<string, float[]> windowPositions;
    }
}
