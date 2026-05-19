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
        public string id;
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
    public class WorkstationLocationSaveData
    {
        public float[] localPosition;
        public float[] localEulerAngles;
    }

    [Serializable]
    public class LookoutStationSaveData
    {
        public float[] localPosition;
        public float[] localEulerAngles;
        public bool isCrowsNest;
        public float[] approachLocalPosition;
    }

    [Serializable]
    public class CargoPaySaveData
    {
        public int instanceId;
        public int prefabIndex;
        public int purchasePrice;
        public int purchaseCurrency;
        public int purchaseDay;
        public int salePrice;
        public int saleCurrency;
        public int saleDay;
        public int profit;
        public int sharePaid;
        public bool sold;
    }

    [Serializable]
    public class NavigatorToolScanSaveData
    {
        public bool hasChronocompass;
        public bool hasChronometer;
        public bool hasCompass;
        public bool hasQuadrant;
        public bool hasSunCompass;
        public bool hasChipLog;
    }

    [Serializable]
    public class VesselSaveData
    {
        public string friendlyName;
        public Dictionary<string, string> sailFriendlyNames = new Dictionary<string, string>();
        public List<SailGroupSaveData> sailGroups = new List<SailGroupSaveData>();
        public Dictionary<string, CrewRestLocationSaveData> crewRestLocations = new Dictionary<string, CrewRestLocationSaveData>();
        public Dictionary<string, WorkstationLocationSaveData> customWorkstationLocations = new Dictionary<string, WorkstationLocationSaveData>();
        public LookoutStationSaveData lookoutStation;
        public List<FavoriteAction> favoriteActions = new List<FavoriteAction>();
        public List<int> keptCargoInstanceIds = new List<int>();
    }

    [Serializable]
    public class VirtualCrewSaveData
    {
        public Dictionary<string, VesselSaveData> vessels = new Dictionary<string, VesselSaveData>();
        public List<CrewmanSaveData> shipCrew;
        public Dictionary<string, List<CrewmanSaveData>> portCrewPools;
        public Dictionary<string, float[]> windowPositions;
        public int totalSalaryPay;
        public int[] totalSharePayByCurrency;
        public Dictionary<int, CargoPaySaveData> cargoPayRecords;
        public int lastPortCrewRefreshDay;
        public Dictionary<string, float> lookoutCertainties;
        public Dictionary<string, string> lookoutIdentifiedNames;
        public Dictionary<string, float> lookoutIgnoredUntil;
        public Dictionary<string, bool> visitedPorts;
        public NavigatorToolScanSaveData navigatorToolScan;
    }
}
