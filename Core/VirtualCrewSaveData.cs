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
        public CrewShift shift = CrewShift.AdHoc;
    }

    [Serializable]
    public class SailGroupSaveData
    {
        public string id;
        public string name;
        public List<string> memberIdentifiers = new List<string>();
    }

    [Serializable]
    public class StowedSailSaveData
    {
        public string sailIdentifier;
        public string mastName;
        public int mastIndex;
        public int prefabIndex;
        public float installHeight;
        public float minAngle;
        public float maxAngle;
        public int sailColor;
        public float scaleY;
        public float scaleZ;
        public string sailName;
        public string friendlyName;
    }

    [Serializable]
    public class StandingOrderSailSaveData
    {
        public string sailIdentifier;
        public bool hasHalyard;
        public float halyard;
        public bool hasSimpleSheet;
        public float simpleSheet;
        public bool hasPortSheet;
        public float portSheet;
        public bool hasStarboardSheet;
        public float starboardSheet;
    }

    [Serializable]
    public class StandingOrderConditionSaveData
    {
        public StandingOrderWindState windState;
        public List<StandingOrderSailSaveData> sails = new List<StandingOrderSailSaveData>();
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
    public class NavigatorMapCoordinateAverageSaveData
    {
        public float latitudeSum;
        public int latitudeCount;
        public float longitudeSum;
        public int longitudeCount;

        internal bool HasLatitude => latitudeCount > 0;
        internal bool HasLongitude => longitudeCount > 0;
        internal bool HasPosition => HasLatitude && HasLongitude;
        internal float Latitude => HasLatitude ? latitudeSum / latitudeCount : 0f;
        internal float Longitude => HasLongitude ? longitudeSum / longitudeCount : 0f;
    }

    [Serializable]
    public class NavigatorShipLogEntrySaveData : NavigatorMapCoordinateAverageSaveData
    {
        public int localDay;
    }

    [Serializable]
    public class NavigatorIslandMapEntrySaveData : NavigatorMapCoordinateAverageSaveData
    {
        public string key;
        public string name;
    }

    [Serializable]
    public class PilotingSaveData
    {
        public bool autopilotEngaged;
        public bool hasPlayerSelection;
        public bool holdWindAngle;
        public float playerSelectedHeading;
        public float playerSelectedWindAngle;
    }

    [Serializable]
    public class VesselSaveData
    {
        public string friendlyName;
        public Dictionary<string, string> sailFriendlyNames = new Dictionary<string, string>();
        public List<StowedSailSaveData> stowedSails = new List<StowedSailSaveData>();
        public List<SailGroupSaveData> sailGroups = new List<SailGroupSaveData>();
        public List<StandingOrderConditionSaveData> standingOrders = new List<StandingOrderConditionSaveData>();
        public Dictionary<string, CrewRestLocationSaveData> crewRestLocations = new Dictionary<string, CrewRestLocationSaveData>();
        public Dictionary<string, WorkstationLocationSaveData> customWorkstationLocations = new Dictionary<string, WorkstationLocationSaveData>();
        public LookoutStationSaveData lookoutStation;
        public List<FavoriteAction> favoriteActions = new List<FavoriteAction>();
        public List<int> keptCargoInstanceIds = new List<int>();
        public List<NavigatorShipLogEntrySaveData> navigatorShipLog = new List<NavigatorShipLogEntrySaveData>();
    }

    [Serializable]
    public class VirtualCrewSaveData
    {
        public int firstOfficerSettingsVersion;
        public bool firstOfficerAutoTrimEnabled = true;
        public bool firstOfficerStandingOrdersEnabled;
        public int stewardSettingsVersion;
        public float stewardThirstLimitPercent = 50f;
        public float stewardHungerLimitPercent = 50f;
        public int maintenanceSettingsVersion;
        public float maintenanceBailOneDeckhandThresholdPercent = 15f;
        public float maintenanceBailTwoDeckhandsThresholdPercent = 35f;
        public float maintenanceBailAllDeckhandsThresholdPercent = 66f;
        public bool maintenanceLanternAutoEnabled = true;
        public bool maintenanceLanternRefillEnabled = true;
        public Dictionary<string, VesselSaveData> vessels = new Dictionary<string, VesselSaveData>();
        public List<CrewmanSaveData> shipCrew;
        public Dictionary<string, List<CrewmanSaveData>> portCrewPools;
        public Dictionary<string, float[]> windowPositions;
        public Dictionary<string, bool> windowVisibility;
        public int totalSalaryPay;
        public int[] totalSharePayByCurrency;
        public Dictionary<int, CargoPaySaveData> cargoPayRecords;
        public int lastPortCrewRefreshDay;
        public Dictionary<string, float> lookoutCertainties;
        public Dictionary<string, string> lookoutIdentifiedNames;
        public Dictionary<string, float> lookoutIgnoredUntil;
        public Dictionary<string, bool> visitedPorts;
        public Dictionary<string, int> quartermasterWaterRefillNextAllowedDay;
        public NavigatorToolScanSaveData navigatorToolScan;
        public Dictionary<string, NavigatorIslandMapEntrySaveData> navigatorIslandMap;
        public PilotingSaveData piloting;
    }
}
