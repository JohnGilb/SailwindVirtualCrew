using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public sealed class VirtualCrewManager
    {
        public bool isCrewActive { get; set; }
        private List<ICommonSailActions> allSails;
        private List<SimpleSail> simpleSails;
        private List<DualSheetSail> dualSheetSails;
        private List<DualSheetSail> squareSails;
        public Dictionary<GPButtonRopeWinch, float> winchInstructions;

        public List<Crewman> Crew { get; private set; }
        public List<WorkRequest> WorkRequests { get; private set; }
        public List<TrimRequest> TrimRequests { get; private set; }
        public List<JibTrimRequest> JibTrimRequests { get; private set; }
        public List<SquareTrimRequest> SquareTrimRequests { get; private set; }
        public List<NavigateRequest> NavigateRequests { get; private set; }
        public List<BailRequest>     BailRequests     { get; private set; }
        public List<MooringRequest>  MooringRequests  { get; private set; }
        public List<HaulSellRequest> HaulSellRequests { get; private set; }
        public List<SleepRequest>    SleepRequests    { get; private set; }
        public Dictionary<GPButtonRopeWinch, WinchTarget> crewWinchInstructions;

        private readonly System.Random rng = new System.Random();
        private const float NavigatorWakeStaminaRatio = 0.33f;
        private const float OffShiftSleepStaminaRatio = 0.8f;
        private const float FirstOfficerTrimIntervalHours = 2f;
        private const float DayShiftStartHour = 6f;
        private const float NightShiftStartHour = 18f;
        private const float ShiftSleepDelayHours = 5f / 60f;
        private const int MaxNavigationResults = 3;
        private float _lastFirstOfficerLocalTime = -1f;
        private float _lastFirstOfficerTrimGameHours = -1f;
        private float _lastShiftLocalTime = -1f;
        private PilotTask _pilotShiftHandoffTask;
        private readonly Dictionary<NavigationMethod, float> navigationToolCooldownEnd = new Dictionary<NavigationMethod, float>();
        private readonly Dictionary<NavigationMethod, float> navigationToolCooldownTotal = new Dictionary<NavigationMethod, float>();
        private readonly List<string> recentNavigationResults = new List<string>();

        public Dictionary<string, VesselSaveData> AllVesselsData { get; set; }
        public string CurrentVesselKey { get; private set; }
        public Dictionary<string, float> LookoutCertainties { get; private set; }
        public Dictionary<string, string> LookoutIdentifiedNames { get; private set; }
        public Dictionary<string, float> LookoutIgnoredUntil { get; private set; }
        public Dictionary<string, bool> VisitedPorts { get; private set; }
        public float LookoutSpyglassZoom { get; private set; } = 1f;
        public bool LookoutSpyglassScanned { get; private set; }
        public bool FirstOfficerAutoTrimEnabled { get; private set; } = true;

        public List<SailGroup> SailGroups { get; private set; }
        public SailGroup AllSailsGroup { get; private set; }
        public SailGroup SelectedGroup { get; set; }

        public Port CurrentPort { get; private set; }
        public List<Crewman> AvailableAtPort { get; private set; } = new List<Crewman>();
        public Dictionary<string, List<Crewman>> PortCrewPools { get; private set; } = new Dictionary<string, List<Crewman>>();
        private Dictionary<string, bool> portIsHub = new Dictionary<string, bool>();
        public int LastPortCrewRefreshDay { get; private set; } = -1;
        private float _lastGlobalTime = -1f;
        private float _lastLookoutPassiveDecayGameHours = -1f;

        private const int SalaryCurrency = 0;
        private const int SalaryPerCrewPerDay = 10;
        private const int QuartermasterWaterRefillCooldownDays = 7;
        private const float WaterLiquidIndex = 1f;
        private const float BarrelCapacityThreshold = 30f;
        private const float BailMugUnits = 3f;
        private const float BailBucketUnits = 10f;

        public int TotalSalaryPay { get; private set; }
        public int[] TotalSharePayByCurrency { get; private set; }
        public Dictionary<int, CargoPaySaveData> CargoPayRecords { get; private set; }
        private Dictionary<string, int> quartermasterWaterRefillNextAllowedDay;

        private static readonly string[] CrewNamePool =
        {
            "Tobias", "Margot", "Fletcher", "Isolde", "Crispin", "Rowena",
            "Aldric", "Sybil", "Oswin", "Heloise", "Gareth", "Mira",
            "Leofric", "Petra", "Hadwin", "Cecily", "Wulfric", "Aveline",
            "Godwin", "Elspeth", "Thurstan", "Mathilda", "Eadric", "Beatrix"
        };

        public string CurrentVesselFriendlyName
        {
            get
            {
                if (CurrentVesselKey == null) return null;
                return AllVesselsData.TryGetValue(CurrentVesselKey, out var d) ? d.friendlyName : null;
            }
        }

        public List<GPButtonRopeWinch> AnchorWinches { get; private set; }

        public IReadOnlyList<ICommonSailActions> AllSails => allSails.AsReadOnly();

        private VirtualCrewManager()
        {
            AllVesselsData = new Dictionary<string, VesselSaveData>();
            LookoutCertainties = new Dictionary<string, float>();
            LookoutIdentifiedNames = new Dictionary<string, string>();
            LookoutIgnoredUntil = new Dictionary<string, float>();
            VisitedPorts = new Dictionary<string, bool>();
            SailGroups = new List<SailGroup>();
            Crew = new List<Crewman>();
            TotalSharePayByCurrency = new int[4];
            CargoPayRecords = new Dictionary<int, CargoPaySaveData>();
            quartermasterWaterRefillNextAllowedDay = new Dictionary<string, int>();
            Reset();
            Sun.OnNewDay += OnNewDay;
        }

        private void OnNewDay()
        {
            PayDailySalaries();
            if (ShouldRefreshPortCrewPools())
                RefreshPortCrewPools();
        }

        private bool ShouldRefreshPortCrewPools()
        {
            if (LastPortCrewRefreshDay < 0)
            {
                LastPortCrewRefreshDay = GameState.day;
                return false;
            }

            return GameState.day - LastPortCrewRefreshDay >= 7;
        }

        private void PayDailySalaries()
        {
            if (Crew.Count == 0 || PlayerGold.currency == null || PlayerGold.currency.Length <= SalaryCurrency)
                return;

            int totalPaid = 0;
            for (int i = 0; i < Crew.Count; i++)
            {
                if (PlayerGold.currency[SalaryCurrency] <= 0)
                    break;

                int paid = Math.Min(SalaryPerCrewPerDay, PlayerGold.currency[SalaryCurrency]);
                PlayerGold.currency[SalaryCurrency] -= paid;
                totalPaid += paid;
            }

            if (totalPaid <= 0)
                return;

            TotalSalaryPay += totalPaid;
            LogCrewPayment(-totalPaid, SalaryCurrency);
        }

        public void RefreshPortCrewPools()
        {
            var ports = GetKnownPortHubFlags();
            if (ports.Count == 0)
                return;

            foreach (var kv in ports)
            {
                string key = kv.Key;
                bool hub = kv.Value;
                int count = hub ? 5 : 1;
                var pool = new List<Crewman>();
                for (int i = 0; i < count; i++)
                    pool.Add(GenerateRandomCrewman(hub));
                PortCrewPools[key] = pool;
                portIsHub[key] = hub;
                EnsureLegendaryCrewAtPort(key);
            }

            LastPortCrewRefreshDay = GameState.day;
            if (CurrentPort != null && PortCrewPools.TryGetValue(CurrentPort.GetPortName(), out var current))
                AvailableAtPort = current;
        }

        private Dictionary<string, bool> GetKnownPortHubFlags()
        {
            var ports = new Dictionary<string, bool>();

            if (Port.ports != null)
            {
                foreach (var port in Port.ports)
                {
                    if (port == null) continue;
                    string name = port.GetPortName();
                    if (!string.IsNullOrEmpty(name))
                        ports[name] = port.hubPort;
                }
            }

            if (CurrentPort != null)
                ports[CurrentPort.GetPortName()] = CurrentPort.hubPort;

            foreach (var kv in portIsHub)
                if (!ports.ContainsKey(kv.Key))
                    ports[kv.Key] = kv.Value;

            foreach (var key in PortCrewPools.Keys.ToList())
                if (!ports.ContainsKey(key))
                    ports[key] = false;

            return ports;
        }

        public void SetCurrentVessel(string key)
        {
            CurrentVesselKey = key;
            if (!AllVesselsData.ContainsKey(key))
                AllVesselsData[key] = new VesselSaveData();

            // Restore user-created groups for this vessel (AllSails is always rebuilt by Reset).
            SailGroups.RemoveAll(g => !g.IsAllSails);
            SelectedGroup = null;
            var vesselData = AllVesselsData[key];
            if (vesselData.sailGroups != null)
            {
                foreach (var gd in vesselData.sailGroups)
                {
                    var group = new SailGroup(gd.name, id: gd.id);
                    if (gd.memberIdentifiers != null)
                        foreach (var id in gd.memberIdentifiers)
                            group.AddIdentifier(id);
                    SailGroups.Add(group);
                }
            }
        }

        public SailGroup CreateSailGroup(string name)
        {
            var group = new SailGroup(name);
            SailGroups.Add(group);
            return group;
        }

        public void DeleteSailGroup(SailGroup group)
        {
            if (!group.IsAllSails)
            {
                if (SelectedGroup == group) SelectedGroup = null;
                SailGroups.Remove(group);
                RemoveFavoriteActionsForGroup(group.Id);
            }
        }

        public void SetVesselFriendlyName(string name)
        {
            if (CurrentVesselKey == null) return;
            if (!AllVesselsData.ContainsKey(CurrentVesselKey))
                AllVesselsData[CurrentVesselKey] = new VesselSaveData();
            AllVesselsData[CurrentVesselKey].friendlyName = string.IsNullOrEmpty(name) ? null : name;
        }

        public void SetCrewRestLocation(Crewman crewman, Vector3 localPosition, Quaternion localRotation)
        {
            if (crewman == null) return;
            EnsureCurrentVesselKey();
            if (CurrentVesselKey == null) return;
            if (!AllVesselsData.ContainsKey(CurrentVesselKey))
                AllVesselsData[CurrentVesselKey] = new VesselSaveData();

            var dict = AllVesselsData[CurrentVesselKey].crewRestLocations
                ?? (AllVesselsData[CurrentVesselKey].crewRestLocations = new Dictionary<string, CrewRestLocationSaveData>());
            dict[crewman.Id] = new CrewRestLocationSaveData
            {
                localPosition = new[] { localPosition.x, localPosition.y, localPosition.z },
                localEulerAngles = new[] { localRotation.eulerAngles.x, localRotation.eulerAngles.y, localRotation.eulerAngles.z }
            };
        }

        public bool TryGetCrewRestLocation(Crewman crewman, out Vector3 localPosition, out Quaternion localRotation)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            EnsureCurrentVesselKey();
            if (crewman == null || CurrentVesselKey == null)
                return false;

            if (!AllVesselsData.TryGetValue(CurrentVesselKey, out var vesselData)
                || vesselData.crewRestLocations == null
                || !vesselData.crewRestLocations.TryGetValue(crewman.Id, out var saved)
                || saved.localPosition == null
                || saved.localPosition.Length < 3)
                return false;

            localPosition = new Vector3(saved.localPosition[0], saved.localPosition[1], saved.localPosition[2]);
            if (saved.localEulerAngles != null && saved.localEulerAngles.Length >= 3)
                localRotation = Quaternion.Euler(saved.localEulerAngles[0], saved.localEulerAngles[1], saved.localEulerAngles[2]);
            return true;
        }

        public void ClearCrewRestLocation(Crewman crewman)
        {
            if (crewman == null || CurrentVesselKey == null) return;
            if (AllVesselsData.TryGetValue(CurrentVesselKey, out var vesselData) && vesselData.crewRestLocations != null)
                vesselData.crewRestLocations.Remove(crewman.Id);
        }

        public void SetCustomWorkstationLocation(string workstationKey, Vector3 localPosition, Quaternion localRotation)
        {
            if (string.IsNullOrEmpty(workstationKey)) return;
            EnsureCurrentVesselKey();
            if (CurrentVesselKey == null) return;
            if (!AllVesselsData.ContainsKey(CurrentVesselKey))
                AllVesselsData[CurrentVesselKey] = new VesselSaveData();

            var dict = AllVesselsData[CurrentVesselKey].customWorkstationLocations
                ?? (AllVesselsData[CurrentVesselKey].customWorkstationLocations = new Dictionary<string, WorkstationLocationSaveData>());
            dict[workstationKey] = new WorkstationLocationSaveData
            {
                localPosition = new[] { localPosition.x, localPosition.y, localPosition.z },
                localEulerAngles = new[] { localRotation.eulerAngles.x, localRotation.eulerAngles.y, localRotation.eulerAngles.z }
            };
        }

        public bool TryGetCustomWorkstationLocation(string workstationKey, out Vector3 localPosition, out Quaternion localRotation)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            EnsureCurrentVesselKey();
            if (string.IsNullOrEmpty(workstationKey) || CurrentVesselKey == null)
                return false;

            if (!AllVesselsData.TryGetValue(CurrentVesselKey, out var vesselData)
                || vesselData.customWorkstationLocations == null
                || !vesselData.customWorkstationLocations.TryGetValue(workstationKey, out var saved)
                || saved.localPosition == null
                || saved.localPosition.Length < 3)
                return false;

            localPosition = new Vector3(saved.localPosition[0], saved.localPosition[1], saved.localPosition[2]);
            if (saved.localEulerAngles != null && saved.localEulerAngles.Length >= 3)
                localRotation = Quaternion.Euler(saved.localEulerAngles[0], saved.localEulerAngles[1], saved.localEulerAngles[2]);
            return true;
        }

        public bool HasCustomWorkstationLocation(string workstationKey)
        {
            return TryGetCustomWorkstationLocation(workstationKey, out _, out _);
        }

        public void ClearCustomWorkstationLocation(string workstationKey)
        {
            if (string.IsNullOrEmpty(workstationKey)) return;
            EnsureCurrentVesselKey();
            if (CurrentVesselKey == null) return;

            if (AllVesselsData.TryGetValue(CurrentVesselKey, out var vesselData) && vesselData.customWorkstationLocations != null)
                vesselData.customWorkstationLocations.Remove(workstationKey);
        }

        public void SetLookoutStation(Vector3 localPosition, Quaternion localRotation, bool isCrowsNest, Vector3 approachLocalPosition)
        {
            EnsureCurrentVesselKey();
            if (CurrentVesselKey == null) return;
            if (!AllVesselsData.ContainsKey(CurrentVesselKey))
                AllVesselsData[CurrentVesselKey] = new VesselSaveData();

            AllVesselsData[CurrentVesselKey].lookoutStation = new LookoutStationSaveData
            {
                localPosition = new[] { localPosition.x, localPosition.y, localPosition.z },
                localEulerAngles = new[] { localRotation.eulerAngles.x, localRotation.eulerAngles.y, localRotation.eulerAngles.z },
                isCrowsNest = isCrowsNest,
                approachLocalPosition = new[] { approachLocalPosition.x, approachLocalPosition.y, approachLocalPosition.z }
            };
        }

        public bool TryGetLookoutStation(out LookoutStationSaveData station)
        {
            station = null;
            EnsureCurrentVesselKey();
            if (CurrentVesselKey == null)
                return false;

            return AllVesselsData.TryGetValue(CurrentVesselKey, out var vesselData)
                && vesselData.lookoutStation != null
                && vesselData.lookoutStation.localPosition != null
                && vesselData.lookoutStation.localPosition.Length >= 3
                && vesselData.lookoutStation.localEulerAngles != null
                && vesselData.lookoutStation.localEulerAngles.Length >= 3
                && (!vesselData.lookoutStation.isCrowsNest
                    || (vesselData.lookoutStation.approachLocalPosition != null
                        && vesselData.lookoutStation.approachLocalPosition.Length >= 3))
                && ((station = vesselData.lookoutStation) != null);
        }

        public void ClearLookoutStation()
        {
            EnsureCurrentVesselKey();
            if (CurrentVesselKey == null) return;

            if (AllVesselsData.TryGetValue(CurrentVesselKey, out var vesselData))
                vesselData.lookoutStation = null;
        }

        public IReadOnlyList<FavoriteAction> FavoriteActions
        {
            get
            {
                var vesselData = GetCurrentVesselData();
                return vesselData?.favoriteActions ?? new List<FavoriteAction>();
            }
        }

        public void AddFavoriteAction(FavoriteAction action)
        {
            if (action == null) return;
            var vesselData = GetCurrentVesselData();
            if (vesselData == null) return;
            var list = vesselData.favoriteActions ?? (vesselData.favoriteActions = new List<FavoriteAction>());
            RefreshFavoriteActionGroupName(action);
            list.Add(action);
            CrewDebugLog.Ok("Favorites", "Created favorite action '" + action.DisplayName + "'");
        }

        public void RemoveFavoriteAction(FavoriteAction action)
        {
            if (action == null) return;
            var vesselData = GetCurrentVesselData();
            if (vesselData?.favoriteActions == null) return;
            vesselData.favoriteActions.Remove(action);
        }

        public bool IsCargoMarkedKeep(ShipItem item)
        {
            if (!TryGetCargoInstanceId(item, out int instanceId))
                return false;

            var vesselData = GetCurrentVesselData();
            return vesselData != null
                && vesselData.keptCargoInstanceIds != null
                && vesselData.keptCargoInstanceIds.Contains(instanceId);
        }

        public void SetCargoKeepMark(ShipItem item, bool keepMarked)
        {
            if (!TryGetCargoInstanceId(item, out int instanceId))
                return;

            var vesselData = GetCurrentVesselData();
            if (vesselData == null)
                return;

            var keptCargo = vesselData.keptCargoInstanceIds ?? (vesselData.keptCargoInstanceIds = new List<int>());
            if (keepMarked)
            {
                if (!keptCargo.Contains(instanceId))
                    keptCargo.Add(instanceId);
            }
            else
            {
                keptCargo.Remove(instanceId);
            }
        }

        public void RemoveFavoriteActionsForGroup(string groupId)
        {
            if (string.IsNullOrEmpty(groupId)) return;
            var vesselData = GetCurrentVesselData();
            if (vesselData?.favoriteActions == null) return;
            vesselData.favoriteActions.RemoveAll(a => a.groupId == groupId);
        }

        public void SetFavoriteActionKey(FavoriteAction action, KeyCode key)
        {
            if (action == null) return;
            var vesselData = GetCurrentVesselData();
            if (vesselData?.favoriteActions == null) return;

            if (key != KeyCode.None)
            {
                foreach (var other in vesselData.favoriteActions)
                    if (other != action && other.keyCode == (int)key)
                        other.keyCode = (int)KeyCode.None;
            }

            action.keyCode = (int)key;
            CrewDebugLog.Ok("Favorites", "Set favorite action key '" + action.DisplayName + "' key=" + key);
        }

        public void InvokeFavoriteAction(FavoriteAction action)
        {
            if (action == null) return;
            RefreshFavoriteActionGroupName(action);
            var group = GetFavoriteActionGroup(action);
            if (group == null)
            {
                CrewDebugLog.Warn("Favorites", "Favorite action group not found id='" + action.groupId + "' name='" + action.groupName + "'");
                return;
            }

            InvokeFavoriteAction(group, action);
            CrewDebugLog.Ok("Favorites", "Invoked favorite action '" + action.DisplayName + "'");
        }

        private void InvokeFavoriteAction(SailGroup group, FavoriteAction action)
        {
            switch (action.kind)
            {
                case FavoriteActionKind.Halyard:
                    foreach (var sail in group.GetMembers(AllSails))
                        AddWorkRequest(new WorkRequest(sail, "Halyard " + action.label,
                            new WinchTarget(sail.getHalyardWinch(), action.target)));
                    break;

                case FavoriteActionKind.SimpleSheet:
                    foreach (var sail in group.GetMembers(AllSails).OfType<SimpleSail>())
                        AddWorkRequest(new WorkRequest(sail, "Sheet " + action.label,
                            new WinchTarget(sail.getSheetWinch(), action.target)));
                    break;

                case FavoriteActionKind.RelativeSheet:
                    foreach (var sail in group.GetMembers(AllSails).OfType<SimpleSail>())
                    {
                        var winch = sail.getSheetWinch();
                        float target = Mathf.Clamp01(winch.rope.currentLength + action.delta);
                        AddWorkRequest(new WorkRequest(sail, "Sheet " + action.label,
                            new WinchTarget(winch, target)));
                    }
                    break;

                case FavoriteActionKind.DualSheet:
                    foreach (var sail in group.GetMembers(AllSails).OfType<DualSheetSail>()
                                             .Where(s => s.getSubtype() == action.dualSheetSubtype))
                    {
                        AddWorkRequest(new WorkRequest(sail, "Port Sheet " + action.label,
                            new WinchTarget(sail.getPortSheetWinch(), action.portTarget)));
                        AddWorkRequest(new WorkRequest(sail, "Starboard Sheet " + action.label,
                            new WinchTarget(sail.getStarboardSheetWinch(), action.starboardTarget)));
                    }
                    break;

                case FavoriteActionKind.Trim:
                    foreach (var sail in group.GetMembers(AllSails))
                    {
                        if (sail is SimpleSail simple)
                            AddTrimRequest(new TrimRequest(simple));
                        else if (sail is DualSheetSail dual)
                        {
                            if (dual.getSubtype() == DualSheetSail.DualSheetSailSubtype.Jib)
                                AddJibTrimRequest(new JibTrimRequest(dual));
                            else if (dual.getSubtype() == DualSheetSail.DualSheetSailSubtype.Square)
                                AddSquareTrimRequest(new SquareTrimRequest(dual));
                        }
                    }
                    break;
            }
        }

        private SailGroup GetFavoriteActionGroup(FavoriteAction action)
        {
            var group = SailGroups.FirstOrDefault(g => g.Id == action.groupId);
            if (group != null)
                return group;

            return SailGroups.FirstOrDefault(g => g.Name == action.groupName);
        }

        private void RefreshFavoriteActionGroupName(FavoriteAction action)
        {
            var group = GetFavoriteActionGroup(action);
            if (group != null)
                action.groupName = group.Name;
        }

        private VesselSaveData GetCurrentVesselData()
        {
            EnsureCurrentVesselKey();
            if (CurrentVesselKey == null)
                return null;
            if (!AllVesselsData.ContainsKey(CurrentVesselKey))
                AllVesselsData[CurrentVesselKey] = new VesselSaveData();

            var vesselData = AllVesselsData[CurrentVesselKey];
            if (vesselData.favoriteActions == null)
                vesselData.favoriteActions = new List<FavoriteAction>();
            if (vesselData.keptCargoInstanceIds == null)
                vesselData.keptCargoInstanceIds = new List<int>();
            return vesselData;
        }

        private static bool TryGetCargoInstanceId(ShipItem item, out int instanceId)
        {
            instanceId = -1;
            var saveable = item != null ? item.GetComponent<SaveablePrefab>() : null;
            if (saveable == null || saveable.instanceId <= 0)
                return false;

            instanceId = saveable.instanceId;
            return true;
        }

        private void EnsureCurrentVesselKey()
        {
            if (CurrentVesselKey != null)
                return;

            string vesselKey = CrewBoatContextResolver.GetActiveVesselKey();
            if (!string.IsNullOrEmpty(vesselKey))
                SetCurrentVessel(vesselKey);
        }

        public void Reset()
        {
            CrewNavigationCoordinator.Instance.CancelAllActiveTasks();

            foreach (var c in Crew)
                c.CurrentTask = null;
            ActivePilotTask   = null;
            ActiveLookoutTask = null;
            _assignedNavigator = null;
            isCrewActive = false;
            simpleSails = new List<SimpleSail>();
            dualSheetSails = new List<DualSheetSail>();
            squareSails = new List<DualSheetSail>();
            allSails = new List<ICommonSailActions>();
            winchInstructions = new Dictionary<GPButtonRopeWinch, float>();
            WorkRequests = new List<WorkRequest>();
            TrimRequests = new List<TrimRequest>();
            JibTrimRequests = new List<JibTrimRequest>();
            SquareTrimRequests = new List<SquareTrimRequest>();
            NavigateRequests = new List<NavigateRequest>();
            BailRequests     = new List<BailRequest>();
            MooringRequests  = new List<MooringRequest>();
            HaulSellRequests = new List<HaulSellRequest>();
            SleepRequests    = new List<SleepRequest>();
            crewWinchInstructions = new Dictionary<GPButtonRopeWinch, WinchTarget>();
            AnchorWinches = new List<GPButtonRopeWinch>();
            _lastGlobalTime = -1f;
            _lastLookoutPassiveDecayGameHours = -1f;
            _lastFirstOfficerLocalTime = -1f;
            _lastFirstOfficerTrimGameHours = -1f;
            _lastShiftLocalTime = -1f;
            _pilotShiftHandoffTask = null;

            // Rebuild the AllSails group; keep user-created groups intact.
            AllSailsGroup = new SailGroup("All Sails", isAllSails: true);
            if (SailGroups.Count > 0 && SailGroups[0].IsAllSails)
                SailGroups[0] = AllSailsGroup;
            else
                SailGroups.Insert(0, AllSailsGroup);
        }

        public PilotTask   ActivePilotTask   { get; private set; }
        public LookoutTask ActiveLookoutTask { get; private set; }
        private Crewman _assignedNavigator;

        public Crewman Pilot     => ActivePilotTask?.AssignedCrewman;
        public Crewman Navigator => _assignedNavigator ?? Crew.FirstOrDefault(c => c.Role == ShipRole.Navigator);
        public Crewman Lookout   => ActiveLookoutTask?.AssignedCrewman;
        public Crewman FirstOfficer => Crew.FirstOrDefault(c => c.Role == ShipRole.ChiefOfficer);
        public bool HasFirstOfficer => Crew.Any(c => c.Role == ShipRole.ChiefOfficer);
        public IReadOnlyList<string> RecentNavigationResults => recentNavigationResults.AsReadOnly();

        // Returns the crew member of the given role with the highest stamina ratio.
        public Crewman FreshestCrewman(ShipRole role) =>
            Crew.Where(c => c.Role == role && IsCrewAssignable(c))
                .OrderByDescending(c => (float)c.CurrentStamina / c.MaxStamina)
                .FirstOrDefault();

        private Crewman FreshestContinuousDutyCrewman(ShipRole role) =>
            Crew.Where(c => c.Role == role && IsCrewAssignable(c) && IsCrewEligibleForContinuousDuty(c))
                .OrderByDescending(c => (float)c.CurrentStamina / c.MaxStamina)
                .FirstOrDefault();

        public bool IsCrewAvailable(Crewman crewman) =>
            crewman != null
            && Crew.Contains(crewman)
            && !crewman.IsExhausted
            && !(crewman.CurrentTask is SleepRequest);

        public bool IsCrewAssignable(Crewman crewman) =>
            IsCrewAvailable(crewman) && !crewman.IsOccupied;

        private Crewman BestAvailableQuartermaster() =>
            Crew.Where(c => c.Role == ShipRole.Quartermaster && IsCrewAvailable(c))
                .OrderByDescending(c => c.Charisma)
                .FirstOrDefault();

        public int GetFirstOfficerStatModifier(Crewman target)
        {
            if (target == null || target.Role == ShipRole.ChiefOfficer || !Crew.Contains(target))
                return 0;

            var fo = Crew.FirstOrDefault(c => c.Role == ShipRole.ChiefOfficer && IsCrewAvailable(c));
            return fo == null ? 0 : fo.BaseCharisma - 3;
        }

        public void StartPilot(Crewman crewman)
        {
            StartPilot(crewman, false);
        }

        private void StartPilot(Crewman crewman, bool shiftHandoff)
        {
            if (crewman == null || crewman.Role != ShipRole.Pilot || !IsCrewAssignable(crewman)) return;
            var previousTask = ActivePilotTask;
            StopPilot();
            ActivePilotTask = new PilotTask(crewman);
            if (shiftHandoff && previousTask != null)
                _pilotShiftHandoffTask = ActivePilotTask;
        }

        public void StopPilot()
        {
            ActivePilotTask?.Cancel();
            ActivePilotTask = null;
        }

        public void StartLookout(Crewman crewman)
        {
            StartLookout(crewman, false);
        }

        private void StartLookout(Crewman crewman, bool suppressFirstLandBell)
        {
            if (crewman == null || crewman.Role != ShipRole.Lookout || !IsCrewAssignable(crewman)) return;
            StopLookout();
            ScanLookoutSpyglass();
            ActiveLookoutTask = new LookoutTask(crewman, suppressFirstLandBell);
        }

        public void StopLookout()
        {
            ActiveLookoutTask?.Cancel();
            ActiveLookoutTask = null;
        }

        public void ScanLookoutSpyglass()
        {
            LookoutSpyglassZoom = LocatorUtils.FindBestLookoutSpyglassZoomOnCurrentVessel();
            LookoutSpyglassScanned = true;
        }

        public bool ShouldPreservePilotOrderForShiftHandoff(PilotTask task)
        {
            return task != null && task == _pilotShiftHandoffTask;
        }

        public void ClearPilotShiftHandoff(PilotTask task)
        {
            if (task != null && task == _pilotShiftHandoffTask)
                _pilotShiftHandoffTask = null;
        }

        public float GetLookoutSpyglassZoom()
        {
            return LookoutSpyglassScanned ? Mathf.Max(1f, LookoutSpyglassZoom) : 1f;
        }

        public void AssignNavigator(Crewman crewman)
        {
            if (crewman == null || crewman.Role != ShipRole.Navigator) return;
            _assignedNavigator = crewman;
        }

        /// <summary>
        ///  Deprecated for now, since we have developer commands to add randomized crew
        /// </summary>
        private void InitializeDefaultCrew()
        {
            Crew.Add(new Crewman("Silas",    ShipRole.Pilot,     rng));
            Crew.Add(new Crewman("Edmund",   ShipRole.Navigator, rng));
            Crew.Add(new Crewman("Barnabas", ShipRole.Deckhand,  rng));
            Crew.Add(new Crewman("Gideon",   ShipRole.Deckhand,  rng));
            Crew.Add(new Crewman("Margit",   ShipRole.Deckhand,  rng));
        }

        private static VirtualCrewManager instance = null;
        public static VirtualCrewManager Instance
        {
            get
            {
                if (instance == null)
                    instance = new VirtualCrewManager();
                return instance;
            }
        }

        public void SetCurrentPort(Port port)
        {
            if (port == null)
                return;

            CurrentPort = port;
            string key = port.GetPortName();
            portIsHub[key] = port.hubPort;
            if (!PortCrewPools.ContainsKey(key))
            {
                int count = port.hubPort ? 5 : 1;
                var pool = new List<Crewman>();
                for (int i = 0; i < count; i++)
                    pool.Add(GenerateRandomCrewman(port.hubPort));
                PortCrewPools[key] = pool;
            }
            EnsureLegendaryCrewAtPort(key);
            AvailableAtPort = PortCrewPools[key];
        }

        public void TryQuartermasterRefillWaterAtPort(Port port)
        {
            if (port == null)
                return;

            string portName = port.GetPortName();
            if (string.IsNullOrEmpty(portName) || !CanQuartermasterRefillWaterAtPort(portName))
                return;

            var quartermaster = BestAvailableQuartermaster();
            if (quartermaster == null)
                return;

            int refillLimit = Math.Max(0, quartermaster.Charisma);
            if (refillLimit <= 0)
                return;

            int refilled = RefillWaterBarrels(refillLimit);
            if (refilled <= 0)
                return;

            quartermasterWaterRefillNextAllowedDay[portName] =
                GameState.day + QuartermasterWaterRefillCooldownDays;

            NotificationUi.instance?.ShowNotification(
                $"{quartermaster.Name} refilled {refilled} water barrel{(refilled == 1 ? "" : "s")}.");
        }

        private bool CanQuartermasterRefillWaterAtPort(string portName)
        {
            if (string.IsNullOrEmpty(portName))
                return false;

            if (quartermasterWaterRefillNextAllowedDay == null)
                quartermasterWaterRefillNextAllowedDay = new Dictionary<string, int>();

            return !quartermasterWaterRefillNextAllowedDay.TryGetValue(portName, out int nextAllowedDay)
                || GameState.day >= nextAllowedDay;
        }

        private int RefillWaterBarrels(int limit)
        {
            int refilled = 0;
            foreach (var barrel in GameObject.FindObjectsOfType<ShipItemBottle>()
                .Where(IsRefillableWaterBarrelOnActiveVessel)
                .OrderByDescending(b => b.GetRemainingCapacity()))
            {
                barrel.amount = WaterLiquidIndex;
                barrel.health = barrel.GetCapacity();
                barrel.UpdateLookText();
                if (barrel.itemRigidbodyC != null)
                    barrel.itemRigidbodyC.UpdateMass();

                refilled++;
                if (refilled >= limit)
                    break;
            }

            return refilled;
        }

        private static bool IsRefillableWaterBarrelOnActiveVessel(ShipItemBottle bottle)
        {
            if (bottle == null || !bottle.sold || bottle.GetCapacity() < BarrelCapacityThreshold)
                return false;

            if (bottle.health >= bottle.GetCapacity())
                return false;

            if (bottle.amount != 0f && Mathf.RoundToInt(bottle.amount) != Mathf.RoundToInt(WaterLiquidIndex))
                return false;

            var good = bottle.GetComponent<Good>();
            if (good != null && good.GetMissionIndex() > -1)
                return false;

            return IsItemOnActiveVessel(bottle);
        }

        private static bool IsItemOnActiveVessel(ShipItem item)
        {
            if (item == null || !CrewBoatContextResolver.TryResolveBoatTransforms(out var topBoat, out var worldBoat))
                return false;

            if (item.currentActualBoat != null && item.currentActualBoat == worldBoat)
                return true;

            if (item.transform.IsChildOf(worldBoat) || item.transform.IsChildOf(topBoat))
                return true;

            var saveable = item.GetComponent<SaveablePrefab>();
            var vesselSaveable = topBoat != null ? topBoat.GetComponent<SaveableObject>() : null;
            return saveable != null
                && vesselSaveable != null
                && saveable.GetParentObject() == vesselSaveable.sceneIndex;
        }

        public void ClearCurrentPort()
        {
            AvailableAtPort = new List<Crewman>();
        }



        // Weights × 2 so 2.5 % entries become integers; total = 200.
        private static readonly int[] SimpleWeights = { 150, 10, 10, 5, 5, 5, 5, 10 };
        private static readonly int[] HubWeights    = { 110, 20, 20, 10, 10, 10, 10, 10 };
        private static readonly ShipRole[] WeightedRoles =
        {
            ShipRole.Deckhand, ShipRole.Navigator, ShipRole.Pilot,
            ShipRole.ChiefOfficer, ShipRole.Chef, ShipRole.Quartermaster, ShipRole.Supercargo,
            ShipRole.Lookout
        };

        private Crewman GenerateRandomCrewman(bool hub)
        {
            string name = CrewNamePool[rng.Next(CrewNamePool.Length)];
            int[] weights = hub ? HubWeights : SimpleWeights;
            int roll = rng.Next(200);
            int cumulative = 0;
            ShipRole role = WeightedRoles[0];
            for (int i = 0; i < WeightedRoles.Length; i++)
            {
                cumulative += weights[i];
                if (roll < cumulative) { role = WeightedRoles[i]; break; }
            }
            return new Crewman(name, role, rng);
        }

        public Crewman CreateRandomCrewman(ShipRole role)
        {
            string name = CrewNamePool[rng.Next(CrewNamePool.Length)];
            return new Crewman(name, role, rng);
        }

        public IReadOnlyList<LegendaryCrewDefinition> LegendaryCrewDefinitions => LegendaryCrewCatalog.All;

        public bool IsLegendaryCrew(Crewman c)
        {
            return c != null && LegendaryCrewCatalog.IsLegendaryId(c.Id);
        }

        public bool IsLegendaryCrewOnShip(string id)
        {
            return Crew.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public bool CanAddLegendaryCrewToRoster(LegendaryCrewDefinition definition, out string reason)
        {
            reason = null;
            if (definition == null)
            {
                reason = "No legendary crew selected.";
                return false;
            }

            if (IsLegendaryCrewOnShip(definition.Id))
            {
                reason = definition.Name + " is already aboard.";
                return false;
            }

            if (definition.Role == ShipRole.ChiefOfficer && Crew.Any(existing => existing.Role == ShipRole.ChiefOfficer))
            {
                reason = "A First Officer is already aboard.";
                return false;
            }

            return true;
        }

        public bool AddLegendaryCrewToRoster(string id, out string reason)
        {
            reason = null;
            if (!LegendaryCrewCatalog.TryGet(id, out var definition))
            {
                reason = "Unknown legendary crew.";
                return false;
            }

            if (!CanAddLegendaryCrewToRoster(definition, out reason))
                return false;

            RemoveLegendaryCrewFromPortPools(definition.Id);
            Crew.Add(LegendaryCrewCatalog.Create(definition));
            if (CurrentPort != null && PortCrewPools.TryGetValue(CurrentPort.GetPortName(), out var current))
                AvailableAtPort = current;
            return true;
        }

        private void EnsureLegendaryCrewAtPort(string portName)
        {
            if (string.IsNullOrEmpty(portName))
                return;

            if (!PortCrewPools.ContainsKey(portName))
                PortCrewPools[portName] = new List<Crewman>();

            foreach (var definition in LegendaryCrewCatalog.ForPort(portName))
            {
                RemoveLegendaryCrewFromNonHomePortPools(definition);
                if (IsLegendaryCrewOnShip(definition.Id) || IsLegendaryCrewWaitingAtHome(definition))
                    continue;

                PortCrewPools[portName].Add(LegendaryCrewCatalog.Create(definition));
            }
        }

        private bool IsLegendaryCrewWaitingAtHome(LegendaryCrewDefinition definition)
        {
            if (definition == null)
                return false;

            foreach (var kv in PortCrewPools)
            {
                if (!LegendaryCrewCatalog.IsPortMatch(definition.HomePort, kv.Key))
                    continue;

                return kv.Value.Any(c => string.Equals(c.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        private void RemoveLegendaryCrewFromNonHomePortPools(LegendaryCrewDefinition definition)
        {
            if (definition == null)
                return;

            foreach (var kv in PortCrewPools)
            {
                if (LegendaryCrewCatalog.IsPortMatch(definition.HomePort, kv.Key))
                    continue;

                kv.Value.RemoveAll(c => string.Equals(c.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void RemoveLegendaryCrewFromPortPools(string id)
        {
            foreach (var pool in PortCrewPools.Values)
                pool.RemoveAll(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private void AddCrewToPortPool(string portName, Crewman crewman)
        {
            if (string.IsNullOrEmpty(portName) || crewman == null)
                return;

            if (!PortCrewPools.ContainsKey(portName))
                PortCrewPools[portName] = new List<Crewman>();

            if (!PortCrewPools[portName].Any(c => c.Id == crewman.Id))
                PortCrewPools[portName].Add(crewman);

            if (CurrentPort != null && LegendaryCrewCatalog.IsPortMatch(CurrentPort.GetPortName(), portName))
                AvailableAtPort = PortCrewPools[portName];
        }

        public bool CanHireCrew(Crewman c, out string reason)
        {
            reason = null;
            if (c == null)
            {
                reason = "No crew selected.";
                return false;
            }

            if (c.Role == ShipRole.ChiefOfficer && Crew.Any(existing => existing.Role == ShipRole.ChiefOfficer))
            {
                reason = "A First Officer is already aboard.";
                return false;
            }

            return true;
        }

        public void HireCrew(Crewman c)
        {
            if (!CanHireCrew(c, out _)) return;
            AvailableAtPort.Remove(c);
            Crew.Add(c);
        }

        public void FireCrew(Crewman c)
        {
            bool wasFirstOfficer = c != null && c.Role == ShipRole.ChiefOfficer;
            var navReq = NavigateRequests.FirstOrDefault(r => r.Navigator == c);
            if (navReq != null) CancelNavigateRequest(navReq);
            if (ActivePilotTask?.AssignedCrewman == c)   StopPilot();
            if (ActiveLookoutTask?.AssignedCrewman == c) StopLookout();
            if (_assignedNavigator == c) _assignedNavigator = null;
            var sleepReq = SleepRequests.FirstOrDefault(r => r.AssignedCrewman == c);
            if (sleepReq != null) CancelSleepRequest(sleepReq);
            c.CurrentTask = null;
            Crew.Remove(c);
            if (IsLegendaryCrew(c) && LegendaryCrewCatalog.TryGet(c.Id, out var legendary))
            {
                AddCrewToPortPool(legendary.HomePort, c);
            }
            else if (CurrentPort != null)
            {
                string key = CurrentPort.GetPortName();
                AddCrewToPortPool(key, c);
            }

            if (wasFirstOfficer && !HasFirstOfficer)
                ResetAllCrewShiftsToAdHoc();
        }

        public void RestoreShipCrew(List<CrewmanSaveData> saved)
        {
            _lastGlobalTime = -1f;
            _lastShiftLocalTime = -1f;
            Crew.Clear();
            if (saved == null || saved.Count == 0) { return; }
            foreach (var d in saved)
                Crew.Add(FromSaveData(d));
            EnsureFirstOfficerShiftAuthority();
        }

        public void RestorePortPools(Dictionary<string, List<CrewmanSaveData>> saved)
        {
            PortCrewPools.Clear();
            if (saved == null) return;
            foreach (var kv in saved)
            {
                PortCrewPools[kv.Key] = kv.Value.Select(FromSaveData).ToList();
                EnsureLegendaryCrewAtPort(kv.Key);
            }
        }

        public void RestorePortCrewRefreshDay(int day)
        {
            LastPortCrewRefreshDay = day;
        }

        public void SetCrewShift(Crewman crewman, CrewShift shift)
        {
            if (crewman == null)
                return;

            if (shift != CrewShift.AdHoc && !HasFirstOfficer)
                return;

            crewman.SetShift(shift);
            if (shift == CrewShift.AdHoc)
                crewman.SetShiftSleepPending(false);
        }

        public bool CanSetCrewShift(CrewShift shift)
        {
            return shift == CrewShift.AdHoc || HasFirstOfficer;
        }

        public void SetFirstOfficerAutoTrimEnabled(bool enabled)
        {
            FirstOfficerAutoTrimEnabled = enabled;
            if (!enabled)
                _lastFirstOfficerTrimGameHours = GetCurrentGameHours();
        }

        public void RestoreFirstOfficerSettings(int settingsVersion, bool autoTrimEnabled)
        {
            FirstOfficerAutoTrimEnabled = settingsVersion <= 0 || autoTrimEnabled;
        }

        public void RestorePayData(int totalSalaryPay, int[] totalSharePayByCurrency, Dictionary<int, CargoPaySaveData> cargoPayRecords)
        {
            TotalSalaryPay = Math.Max(0, totalSalaryPay);
            TotalSharePayByCurrency = new int[4];
            if (totalSharePayByCurrency != null)
            {
                for (int i = 0; i < TotalSharePayByCurrency.Length && i < totalSharePayByCurrency.Length; i++)
                    TotalSharePayByCurrency[i] = Math.Max(0, totalSharePayByCurrency[i]);
            }

            CargoPayRecords = cargoPayRecords != null
                ? new Dictionary<int, CargoPaySaveData>(cargoPayRecords)
                : new Dictionary<int, CargoPaySaveData>();
        }

        public void StoreLookoutCertainties(Dictionary<string, float> certainties)
        {
            LookoutCertainties = new Dictionary<string, float>();
            if (certainties == null)
                return;

            foreach (var kv in certainties)
            {
                float certainty = Mathf.Clamp(kv.Value, 0f, 2f);
                if (certainty > 0f)
                    LookoutCertainties[kv.Key] = certainty;
            }

            PruneLookoutIdentifiedNames();
        }

        public Dictionary<string, float> GetLookoutCertaintySnapshot()
        {
            return new Dictionary<string, float>(LookoutCertainties ?? new Dictionary<string, float>());
        }

        public float GetLookoutCertainty(IslandHorizon island)
        {
            if (island == null || LookoutCertainties == null)
                return 0f;

            return LookoutCertainties.TryGetValue(LookoutVisibility.GetIslandKey(island), out float certainty)
                ? certainty
                : 0f;
        }

        public void SetLookoutCertainty(IslandHorizon island, float certainty)
        {
            if (island == null)
                return;

            if (LookoutCertainties == null)
                LookoutCertainties = new Dictionary<string, float>();

            string key = LookoutVisibility.GetIslandKey(island);
            certainty = Mathf.Clamp(certainty, 0f, 2f);
            if (certainty <= 0f)
                LookoutCertainties.Remove(key);
            else
                LookoutCertainties[key] = certainty;

            if (certainty < 1f)
                ForgetLookoutIslandName(key);
        }

        public void StoreLookoutIdentifiedNames(Dictionary<string, string> identifiedNames)
        {
            LookoutIdentifiedNames = new Dictionary<string, string>();
            if (identifiedNames == null)
                return;

            foreach (var kv in identifiedNames)
            {
                if (!string.IsNullOrEmpty(kv.Key)
                    && !string.IsNullOrEmpty(kv.Value)
                    && LookoutCertainties != null
                    && LookoutCertainties.TryGetValue(kv.Key, out float certainty)
                    && certainty >= 1f)
                    LookoutIdentifiedNames[kv.Key] = kv.Value;
            }
        }

        public Dictionary<string, string> GetLookoutIdentifiedNamesSnapshot()
        {
            PruneLookoutIdentifiedNames();
            return new Dictionary<string, string>(LookoutIdentifiedNames ?? new Dictionary<string, string>());
        }

        public void RememberLookoutIslandName(IslandHorizon island, string islandName)
        {
            if (island == null
                || string.IsNullOrEmpty(islandName)
                || GetLookoutCertainty(island) < 1f
                || !LookoutIslandKnowledge.HasPlayerVisitedIsland(island))
                return;

            if (LookoutIdentifiedNames == null)
                LookoutIdentifiedNames = new Dictionary<string, string>();

            LookoutIdentifiedNames[LookoutVisibility.GetIslandKey(island)] = islandName;
        }

        public bool TryGetRememberedLookoutIslandName(IslandHorizon island, out string islandName)
        {
            islandName = null;
            if (island == null || LookoutIdentifiedNames == null)
                return false;

            string key = LookoutVisibility.GetIslandKey(island);
            if (GetLookoutCertainty(island) < 1f)
            {
                ForgetLookoutIslandName(key);
                return false;
            }

            if (!LookoutIslandKnowledge.HasPlayerVisitedIsland(island))
            {
                ForgetLookoutIslandName(key);
                return false;
            }

            return LookoutIdentifiedNames.TryGetValue(key, out islandName)
                && !string.IsNullOrEmpty(islandName);
        }

        private void ForgetLookoutIslandName(string key)
        {
            if (!string.IsNullOrEmpty(key) && LookoutIdentifiedNames != null)
                LookoutIdentifiedNames.Remove(key);
        }

        private void PruneLookoutIdentifiedNames()
        {
            if (LookoutIdentifiedNames == null || LookoutIdentifiedNames.Count == 0)
                return;

            foreach (string key in LookoutIdentifiedNames.Keys.ToList())
            {
                if (LookoutCertainties == null
                    || !LookoutCertainties.TryGetValue(key, out float certainty)
                    || certainty < 1f)
                    LookoutIdentifiedNames.Remove(key);
            }
        }

        public void StoreLookoutIgnoredUntil(Dictionary<string, float> ignoredUntil)
        {
            LookoutIgnoredUntil = new Dictionary<string, float>();
            if (ignoredUntil == null)
                return;

            float now = GetCurrentGameHours();
            foreach (var kv in ignoredUntil)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value > now)
                    LookoutIgnoredUntil[kv.Key] = kv.Value;
        }

        public Dictionary<string, float> GetLookoutIgnoredUntilSnapshot()
        {
            PruneExpiredLookoutIgnores();
            return new Dictionary<string, float>(LookoutIgnoredUntil ?? new Dictionary<string, float>());
        }

        public void IgnoreLookoutIsland(IslandHorizon island, float gameHours)
        {
            if (island == null || gameHours <= 0f)
                return;

            if (LookoutIgnoredUntil == null)
                LookoutIgnoredUntil = new Dictionary<string, float>();

            LookoutIgnoredUntil[LookoutVisibility.GetIslandKey(island)] = GetCurrentGameHours() + gameHours;
        }

        public void ClearLookoutIgnore(IslandHorizon island)
        {
            if (island == null || LookoutIgnoredUntil == null)
                return;

            LookoutIgnoredUntil.Remove(LookoutVisibility.GetIslandKey(island));
        }

        public bool IsLookoutIgnored(IslandHorizon island)
        {
            return GetLookoutIgnoreRemainingHours(island) > 0f;
        }

        public float GetLookoutIgnoreRemainingHours(IslandHorizon island)
        {
            if (island == null || LookoutIgnoredUntil == null)
                return 0f;

            string key = LookoutVisibility.GetIslandKey(island);
            if (!LookoutIgnoredUntil.TryGetValue(key, out float until))
                return 0f;

            float remaining = until - GetCurrentGameHours();
            if (remaining > 0f)
                return remaining;

            LookoutIgnoredUntil.Remove(key);
            return 0f;
        }

        public bool HasIgnoredLookoutIslands()
        {
            PruneExpiredLookoutIgnores();
            return LookoutIgnoredUntil != null && LookoutIgnoredUntil.Count > 0;
        }

        private void PruneExpiredLookoutIgnores()
        {
            if (LookoutIgnoredUntil == null || LookoutIgnoredUntil.Count == 0)
                return;

            float now = GetCurrentGameHours();
            foreach (string key in LookoutIgnoredUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                LookoutIgnoredUntil.Remove(key);
        }

        private static float GetCurrentGameHours()
        {
            float time = Sun.sun != null ? Sun.sun.globalTime : 0f;
            return GameState.day * 24f + time;
        }

        private void TickLookoutPassiveCertaintyDecay()
        {
            float now = GetCurrentGameHours();
            if (_lastLookoutPassiveDecayGameHours < 0f)
            {
                _lastLookoutPassiveDecayGameHours = now;
                return;
            }

            float deltaHours = now - _lastLookoutPassiveDecayGameHours;
            if (deltaHours <= 0f)
                return;

            _lastLookoutPassiveDecayGameHours = now;

            if (ActiveLookoutTask != null || LookoutCertainties == null || LookoutCertainties.Count == 0)
                return;

            foreach (string key in LookoutCertainties.Keys.ToList())
            {
                float certainty = Mathf.Clamp(LookoutCertainties[key] - deltaHours, 0f, 2f);
                if (certainty <= 0f)
                    LookoutCertainties.Remove(key);
                else
                    LookoutCertainties[key] = certainty;

                if (certainty < 1f)
                    ForgetLookoutIslandName(key);
            }
        }

        public void StoreVisitedPorts(Dictionary<string, bool> visitedPorts)
        {
            VisitedPorts = new Dictionary<string, bool>();
            if (visitedPorts == null)
                return;

            foreach (var kv in visitedPorts)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value)
                    VisitedPorts[kv.Key] = true;
        }

        public Dictionary<string, bool> GetVisitedPortsSnapshot()
        {
            return new Dictionary<string, bool>(VisitedPorts ?? new Dictionary<string, bool>());
        }

        public void RegisterVisitedPort(Port port)
        {
            if (port == null)
                return;

            RegisterVisitedPort(port.GetPortName());
        }

        public void RegisterVisitedPort(string portName)
        {
            if (string.IsNullOrEmpty(portName))
                return;

            if (VisitedPorts == null)
                VisitedPorts = new Dictionary<string, bool>();

            VisitedPorts[portName] = true;
        }

        public bool HasVisitedPort(string portName)
        {
            if (string.IsNullOrEmpty(portName))
                return false;

            if (VisitedPorts != null
                && VisitedPorts.TryGetValue(portName, out bool visited)
                && visited)
                return true;

            return false;
        }

        public void StoreQuartermasterWaterRefills(Dictionary<string, int> nextAllowedDays)
        {
            quartermasterWaterRefillNextAllowedDay = new Dictionary<string, int>();
            if (nextAllowedDays == null)
                return;

            foreach (var kv in nextAllowedDays)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value > GameState.day)
                    quartermasterWaterRefillNextAllowedDay[kv.Key] = kv.Value;
        }

        public Dictionary<string, int> GetQuartermasterWaterRefillSnapshot()
        {
            PruneQuartermasterWaterRefillCooldowns();
            return new Dictionary<string, int>(
                quartermasterWaterRefillNextAllowedDay ?? new Dictionary<string, int>());
        }

        private void PruneQuartermasterWaterRefillCooldowns()
        {
            if (quartermasterWaterRefillNextAllowedDay == null || quartermasterWaterRefillNextAllowedDay.Count == 0)
                return;

            foreach (string key in quartermasterWaterRefillNextAllowedDay
                .Where(kv => kv.Value <= GameState.day)
                .Select(kv => kv.Key)
                .ToList())
                quartermasterWaterRefillNextAllowedDay.Remove(key);
        }

        public void RecordCargoPurchase(ShipItem item, int price, int currency)
        {
            var saveable = item != null ? item.GetComponent<SaveablePrefab>() : null;
            if (saveable == null || saveable.instanceId <= 0 || price <= 0 || !IsSupportedCurrency(currency))
                return;

            CargoPayRecords[saveable.instanceId] = new CargoPaySaveData
            {
                instanceId = saveable.instanceId,
                prefabIndex = saveable.prefabIndex,
                purchasePrice = price,
                purchaseCurrency = currency,
                purchaseDay = GameState.day,
                sold = false
            };
        }

        public void RecordCargoSale(ShipItem item, int salePrice, int saleCurrency)
        {
            var saveable = item != null ? item.GetComponent<SaveablePrefab>() : null;
            if (saveable == null || saveable.instanceId <= 0 || salePrice <= 0 || !IsSupportedCurrency(saleCurrency))
                return;

            if (!CargoPayRecords.TryGetValue(saveable.instanceId, out var record) || record.sold)
                return;

            int purchaseInSaleCurrency = ConvertCurrency(record.purchasePrice, record.purchaseCurrency, saleCurrency);
            int profit = salePrice - purchaseInSaleCurrency;
            int sharePaid = 0;
            if (profit > 0 && Crew.Count > 0)
            {
                int shareOwed = Crew.Sum(c => Mathf.CeilToInt(profit * GetProfitSharePercent(c.Role) * 0.01f));
                sharePaid = DeductCurrency(saleCurrency, shareOwed);
                if (sharePaid > 0)
                {
                    TotalSharePayByCurrency[saleCurrency] += sharePaid;
                    LogCrewPayment(-sharePaid, saleCurrency);
                }
            }

            record.salePrice = salePrice;
            record.saleCurrency = saleCurrency;
            record.saleDay = GameState.day;
            record.profit = profit;
            record.sharePaid = sharePaid;
            record.sold = true;
        }

        public void ForgetDestroyedCargo(ShipItem item)
        {
            var saveable = item != null ? item.GetComponent<SaveablePrefab>() : null;
            if (saveable == null || saveable.instanceId <= 0)
                return;

            if (CargoPayRecords.TryGetValue(saveable.instanceId, out var record) && !record.sold)
                CargoPayRecords.Remove(saveable.instanceId);
        }

        private static int GetProfitSharePercent(ShipRole role)
        {
            switch (role)
            {
                case ShipRole.Quartermaster:
                case ShipRole.Supercargo:
                    return 2;
                case ShipRole.Lookout:
                case ShipRole.Pilot:
                    return 3;
                case ShipRole.Navigator:
                    return 4;
                case ShipRole.ChiefOfficer:
                    return 7;
                default:
                    return 1;
            }
        }

        public string GetSharePaySummary()
        {
            if (TotalSharePayByCurrency == null || TotalSharePayByCurrency.All(v => v <= 0))
                return "0";

            string[] parts = new string[TotalSharePayByCurrency.Length];
            int count = 0;
            for (int i = 0; i < TotalSharePayByCurrency.Length; i++)
            {
                if (TotalSharePayByCurrency[i] <= 0)
                    continue;
                parts[count++] = TotalSharePayByCurrency[i] + " " + PlayerGold.GetCurrencySymbol(i);
            }
            return string.Join(" / ", parts.Take(count).ToArray());
        }

        private static bool IsSupportedCurrency(int currency)
        {
            return currency >= 0 && currency < 4;
        }

        private static int DeductCurrency(int currency, int requestedAmount)
        {
            if (requestedAmount <= 0 || PlayerGold.currency == null || PlayerGold.currency.Length <= currency)
                return 0;
            if (PlayerGold.currency[currency] <= 0)
                return 0;

            int paid = Math.Min(requestedAmount, PlayerGold.currency[currency]);
            PlayerGold.currency[currency] -= paid;
            return paid;
        }

        private static int ConvertCurrency(int amount, int fromCurrency, int toCurrency)
        {
            if (amount <= 0 || fromCurrency == toCurrency || CurrencyMarket.instance == null)
                return amount;
            if (!IsSupportedCurrency(fromCurrency) || !IsSupportedCurrency(toCurrency))
                return amount;

            var prices = CurrencyMarket.instance.currentPrices;
            if (prices == null || prices.Length <= Math.Max(fromCurrency, toCurrency) || prices[fromCurrency] <= 0f)
                return amount;

            float rawValue = amount / prices[fromCurrency];
            return Mathf.RoundToInt(rawValue * prices[toCurrency]);
        }

        private static void LogCrewPayment(int amount, int currency)
        {
            if (DayLogs.instance != null && DayLogs.instance.dayLogs != null && DayLogs.instance.dayLogs.Length > currency)
                DayLogs.instance.dayLogs[currency].LogTransaction(amount, TransactionCategory.other);
            if (MoneyNotification.instance != null)
                MoneyNotification.instance.PlayNotif(amount, currency);
        }

        private static Crewman FromSaveData(CrewmanSaveData d) =>
            new Crewman(d.name, d.role,
                d.strength, d.dexterity, d.constitution, d.intelligence, d.wisdom, d.charisma,
                d.advStrength, d.advDexterity, d.advConstitution, d.advIntelligence, d.advWisdom, d.advCharisma,
                d.currentStamina,
                d.id,
                d.modelIndex,
                d.shift);

        public void addSail(SimpleSail sail)
        {
            Console.WriteLine(string.Format("Finalizing Sail:{0}, with Halyard:{1}, Sheet:{2}",
                sail.getRealSail().name, sail.getHalyardWinch()?.name ?? "NULL", sail.getSheetWinch()?.name ?? "NULL"));
            RestoreFriendlyName(sail);
            simpleSails.Add(sail);
            allSails.Add(sail);
        }

        public void addDualSheetSail(DualSheetSail sail)
        {
            Console.WriteLine(string.Format("Finalizing Sail:{0}, with Halyard:{1}, PortSheet:{2}, StarboardSheet:{3}",
                sail.getRealSail().name, sail.getHalyardWinch()?.name ?? "NULL", sail.getPortSheetWinch()?.name ?? "NULL", sail.getStarboardSheetWinch()?.name ?? "NULL"));
            RestoreFriendlyName(sail);
            dualSheetSails.Add(sail);
            allSails.Add(sail);
        }

        public void addSquareSail(DualSheetSail sail)
        {
            Console.WriteLine(string.Format("Finalizing Square Sail:{0}, with Halyard:{1}, PortSheet:{2}, StarboardSheet:{3}",
                sail.getRealSail().name, sail.getHalyardWinch()?.name ?? "NULL", sail.getPortSheetWinch()?.name ?? "NULL", sail.getStarboardSheetWinch()?.name ?? "NULL"));
            RestoreFriendlyName(sail);
            squareSails.Add(sail);
            allSails.Add(sail);
        }

        private void RestoreFriendlyName(ICommonSailActions sail)
        {
            if (CurrentVesselKey == null) return;
            if (!AllVesselsData.TryGetValue(CurrentVesselKey, out var d)) return;
            if (d.sailFriendlyNames != null && d.sailFriendlyNames.TryGetValue(sail.getDefaultIdentifier(), out string name))
                sail.FriendlyName = name;
        }

        public void SetSailFriendlyName(ICommonSailActions sail, string name)
        {
            sail.FriendlyName = name;
            if (CurrentVesselKey == null) return;
            if (!AllVesselsData.ContainsKey(CurrentVesselKey))
                AllVesselsData[CurrentVesselKey] = new VesselSaveData();
            var dict = AllVesselsData[CurrentVesselKey].sailFriendlyNames
                    ?? (AllVesselsData[CurrentVesselKey].sailFriendlyNames = new Dictionary<string, string>());
            if (string.IsNullOrEmpty(name))
                dict.Remove(sail.getDefaultIdentifier());
            else
                dict[sail.getDefaultIdentifier()] = name;
        }

        public bool HasPendingRequestForWinch(GPButtonRopeWinch winch)
        {
            return WorkRequests.Any(r => r.Status != WorkRequestStatus.Complete
                                     && r.Targets.Any(t => t.Winch.rope == winch.rope))
                || TrimRequests.Any(r => r.Status != WorkRequestStatus.Complete
                                     && r.Sail.getSheetWinch().rope == winch.rope)
                || JibTrimRequests.Any(r => r.Status != WorkRequestStatus.Complete
                                     && (r.Sail.getPortSheetWinch().rope == winch.rope
                                      || r.Sail.getStarboardSheetWinch().rope == winch.rope))
                || SquareTrimRequests.Any(r => r.Status != WorkRequestStatus.Complete
                                     && (r.Sail.getPortSheetWinch().rope == winch.rope
                                      || r.Sail.getStarboardSheetWinch().rope == winch.rope));
        }

        public void AddWorkRequest(WorkRequest request)
        {
            // Reject if any of this request's target winches are already claimed.
            if (request.Targets.Any(t => HasPendingRequestForWinch(t.Winch)))
                return;
            WorkRequests.Add(request);
        }

        public void CancelWorkRequest(WorkRequest request)
        {
            request.CancelPositioning();
            if (request.AssignedCrewman != null)
                request.AssignedCrewman.CurrentTask = null;
            foreach (var t in request.Targets)
                crewWinchInstructions.Remove(t.Winch);
            WorkRequests.Remove(request);
        }

        public void AddTrimRequest(TrimRequest request)
        {
            if (HasPendingRequestForWinch(request.Sail.getSheetWinch()))
                return;
            TrimRequests.Add(request);
        }

        public void CancelTrimRequest(TrimRequest request)
        {
            if (request.AssignedCrewman != null)
                request.AssignedCrewman.CurrentTask = null;
            crewWinchInstructions.Remove(request.Sail.getSheetWinch());
            CrewNavigationCoordinator.Instance.Cancel(request);
            TrimRequests.Remove(request);
        }

        public void AddJibTrimRequest(JibTrimRequest request)
        {
            var port = request.Sail.getPortSheetWinch();
            var star = request.Sail.getStarboardSheetWinch();
            if (HasPendingRequestForWinch(port) || HasPendingRequestForWinch(star))
                return;
            JibTrimRequests.Add(request);
        }

        public void CancelJibTrimRequest(JibTrimRequest request)
        {
            if (request.AssignedCrewman != null)
                request.AssignedCrewman.CurrentTask = null;
            crewWinchInstructions.Remove(request.Sail.getPortSheetWinch());
            crewWinchInstructions.Remove(request.Sail.getStarboardSheetWinch());
            var nav = CrewNavigationCoordinator.Instance;
            nav.Cancel(request);
            nav.Cancel((request, 1));
            JibTrimRequests.Remove(request);
        }

        public void AddSquareTrimRequest(SquareTrimRequest request)
        {
            var port = request.Sail.getPortSheetWinch();
            var star = request.Sail.getStarboardSheetWinch();
            if (HasPendingRequestForWinch(port) || HasPendingRequestForWinch(star))
                return;
            SquareTrimRequests.Add(request);
        }

        public void AddNavigateRequest(NavigateRequest request)
        {
            NavigateRequests.Add(request);
        }

        public bool TryAddNavigateRequest(NavigationMethod method, out string reason, bool requireTool = true, bool allowQueue = false)
        {
            reason = null;
            if (!allowQueue && NavigateRequests.Any(r => r.Status != WorkRequestStatus.Complete))
            {
                reason = "Navigator is already plotting.";
                return false;
            }

            var navigator = Navigator;
            if (!IsCrewAssignable(navigator))
            {
                reason = "Navigator is not available.";
                return false;
            }

            if (IsNavigationToolOnCooldown(method))
            {
                reason = GetNavigationToolLabel(method) + " exhausted for now.";
                return false;
            }

            if (requireTool && !HasNavigationTool(method))
            {
                reason = "Can't find " + GetNavigationToolLabel(method).ToLowerInvariant() + " nearby!";
                return false;
            }

            AddNavigateRequest(new NavigateRequest(method, RecordNavigationResult));
            return true;
        }

        public bool HasNavigationTool(NavigationMethod method)
        {
            return LocatorUtils.findItem(new[] { GetNavigationToolItemName(method) })[0];
        }

        public bool IsNavigationToolOnCooldown(NavigationMethod method) =>
            navigationToolCooldownEnd.TryGetValue(method, out float end) && GetCurrentGameHours() < end;

        public float GetNavigationToolCooldownProgress(NavigationMethod method)
        {
            if (!navigationToolCooldownEnd.TryGetValue(method, out float end))
                return 1f;

            float remaining = end - GetCurrentGameHours();
            if (remaining <= 0f)
                return 1f;

            float total = navigationToolCooldownTotal.TryGetValue(method, out float value) ? value : remaining;
            return total <= 0f ? 1f : Mathf.Clamp01((total - remaining) / total);
        }

        public void RecordNavigationResult(NavigationResult result)
        {
            StartNavigationToolCooldown(result.Method);

            if (result.IsFailure)
            {
                AddNavigationResult(result.FailureMessage);
                return;
            }

            string coords = "";
            if (result.HasLatitude) coords += result.LatitudeText;
            if (result.HasLatitude && result.HasLongitude) coords += "  ";
            if (result.HasLongitude) coords += result.LongitudeText;
            AddNavigationResult(result.Header + "\n" + coords);
        }

        public void AddNavigationMessage(string text)
        {
            AddNavigationResult(text);
        }

        public static string GetNavigationToolLabel(NavigationMethod method)
        {
            switch (method)
            {
                case NavigationMethod.Quadrant:      return "Quadrant";
                case NavigationMethod.SunCompass:    return "Sun Compass";
                case NavigationMethod.Chronometer:   return "Chronometer";
                case NavigationMethod.Chronocompass: return "Chronocompass";
                default: return method.ToString();
            }
        }

        private void StartNavigationToolCooldown(NavigationMethod method)
        {
            float hours = method == NavigationMethod.Quadrant ? 8f : 2f;
            navigationToolCooldownEnd[method] = GetCurrentGameHours() + hours;
            navigationToolCooldownTotal[method] = hours;
        }

        private void AddNavigationResult(string text)
        {
            recentNavigationResults.Insert(0, text);
            if (recentNavigationResults.Count > MaxNavigationResults)
                recentNavigationResults.RemoveAt(recentNavigationResults.Count - 1);
        }

        private static string GetNavigationToolItemName(NavigationMethod method)
        {
            switch (method)
            {
                case NavigationMethod.Quadrant:      return "quadrant";
                case NavigationMethod.SunCompass:    return "sun compass";
                case NavigationMethod.Chronometer:   return "chronometer";
                case NavigationMethod.Chronocompass: return "chronocompass";
                default: return method.ToString().ToLowerInvariant();
            }
        }

        public void CancelNavigateRequest(NavigateRequest request)
        {
            if (request.Navigator != null)
                request.Navigator.CurrentTask = null;
            NavigateRequests.Remove(request);
        }

        public void AddBailRequest(BailRequest request)
        {
            BailRequests.Add(request);
        }

        public void AddHaulSellRequest(HaulSellRequest request)
        {
            if (request == null || HasPendingHaulSellRequest(request.Item))
                return;

            HaulSellRequests.Add(request);
        }

        public bool HasPendingHaulSellRequest(ShipItem item)
        {
            return item
                && HaulSellRequests.Any(r => r.Item == item && r.Status != WorkRequestStatus.Complete);
        }

        public bool TryCancelHaulSellRequestForItem(ShipItem item)
        {
            if (!item)
                return false;

            var request = HaulSellRequests.FirstOrDefault(r =>
                r.Item == item && r.Status != WorkRequestStatus.Complete);
            if (request == null)
                return false;

            CancelHaulSellRequest(request);
            return true;
        }

        public bool HasPendingMooringRequest(MooringSide side)
        {
            return MooringRequests.Any(r => r.Side == side && r.Status != WorkRequestStatus.Complete);
        }

        public bool CanAddMooringRequest(MooringSide side)
        {
            return !HasPendingMooringRequest(side) && MooringLocator.HasAvailableTargets(side);
        }

        public void AddMooringRequests(MooringSide side)
        {
            if (HasPendingMooringRequest(side))
                return;

            var excluded = MooringRequests
                .Where(r => r.Status != WorkRequestStatus.Complete && r.TargetRope != null)
                .Select(r => r.TargetRope)
                .ToList();

            if (!MooringLocator.TryFindAvailableRopes(side, excluded, out var ropes))
                return;

            foreach (var rope in ropes)
                MooringRequests.Add(new MooringRequest(side, rope.Rope));
        }

        public void CancelMooringRequest(MooringRequest request)
        {
            request.CancelPositioning();
            if (request.AssignedCrewman != null)
                request.AssignedCrewman.CurrentTask = null;
            MooringRequests.Remove(request);
        }

        public void CancelBailRequest(BailRequest request)
        {
            if (request.AssignedCrewman != null)
                request.AssignedCrewman.CurrentTask = null;
            BailRequests.Remove(request);
        }

        public void CancelHaulSellRequest(HaulSellRequest request)
        {
            if (request == null)
                return;

            request.Cancel();
            if (request.Status == WorkRequestStatus.Complete)
                HaulSellRequests.Remove(request);
        }

        public void SettleHaulSellRequestsForSave()
        {
            if (HaulSellRequests == null || HaulSellRequests.Count == 0)
                return;

            foreach (var request in HaulSellRequests.ToList())
                request.ForceCompleteForSave();

            HaulSellRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);
        }

        private void TickQuartermasterBailing()
        {
            if (!Crew.Any(c => c.Role == ShipRole.Quartermaster && IsCrewAvailable(c)))
                return;

            var damage = GetCurrentBoatDamage();
            if (damage == null)
                return;

            float waterLevel = damage.waterLevel;
            int deckhandCount = Crew.Count(c => c.Role == ShipRole.Deckhand);
            if (deckhandCount <= 0)
                return;

            int targetRequests = 0;
            if (waterLevel >= 0.66f)
            {
                WakeSleepingDeckhands();
                targetRequests = deckhandCount;
            }
            else if (waterLevel >= 0.35f)
            {
                targetRequests = deckhandCount >= 2 ? 2 : 1;
            }
            else if (waterLevel >= 0.15f)
            {
                targetRequests = 1;
            }

            if (targetRequests <= 0)
                return;

            int currentRequests = BailRequests.Count(r => r.Status != WorkRequestStatus.Complete);
            for (int i = currentRequests; i < targetRequests; i++)
                AddBailRequest(new BailRequest(damage, GetNextBailToolUnits()));
        }

        private void WakeSleepingDeckhands()
        {
            foreach (var sleep in SleepRequests
                .Where(r => r.AssignedCrewman != null && r.AssignedCrewman.Role == ShipRole.Deckhand)
                .ToList())
            {
                CancelSleepRequest(sleep);
            }
        }

        private float GetNextBailToolUnits()
        {
            int bucketCount = LocatorUtils.findItemCounts(new[] { "bucket" })[0];
            int bucketUsersQueued = BailRequests.Count(r =>
                r.Status != WorkRequestStatus.Complete
                && r.UnitsPerScoop >= BailBucketUnits);
            return bucketUsersQueued < bucketCount ? BailBucketUnits : BailMugUnits;
        }

        private static BoatDamage GetCurrentBoatDamage()
        {
            var topBoat = CrewBoatContextResolver.GetActiveTopBoat();
            return topBoat ? topBoat.GetComponent<BoatDamage>() : null;
        }

        public void CancelSquareTrimRequest(SquareTrimRequest request)
        {
            if (request.AssignedCrewman  != null) request.AssignedCrewman.CurrentTask  = null;
            if (request.AssignedCrewman2 != null) request.AssignedCrewman2.CurrentTask = null;
            crewWinchInstructions.Remove(request.Sail.getPortSheetWinch());
            crewWinchInstructions.Remove(request.Sail.getStarboardSheetWinch());
            var nav = CrewNavigationCoordinator.Instance;
            nav.Cancel((request, 0));
            nav.Cancel((request, 1));
            SquareTrimRequests.Remove(request);
        }

        private void TickFirstOfficer()
        {
            if (Sun.sun == null)
                return;

            float currentLocalTime = Sun.sun.localTime;
            float currentGameHours = GetCurrentGameHours();
            var firstOfficer = Crew.FirstOrDefault(c => c.Role == ShipRole.ChiefOfficer && IsCrewAvailable(c));
            if (firstOfficer == null)
            {
                _lastFirstOfficerLocalTime = currentLocalTime;
                return;
            }

            RotateWatchCrew();

            bool hasPreviousLocalTime = _lastFirstOfficerLocalTime >= 0f;
            if (hasPreviousLocalTime)
            {
                if (CrossedLocalHour(_lastFirstOfficerLocalTime, currentLocalTime, 0f))
                    CommandMidnightNavigation();

                if (CrossedLocalHour(_lastFirstOfficerLocalTime, currentLocalTime, 12f))
                    CommandNoonNavigation();

                if (CrossedLocalHour(_lastFirstOfficerLocalTime, currentLocalTime, 4f)
                 || CrossedLocalHour(_lastFirstOfficerLocalTime, currentLocalTime, 14f))
                    SendNavigatorToSleep();

                if (CrossedLocalHour(_lastFirstOfficerLocalTime, currentLocalTime, 10f)
                 || CrossedLocalHour(_lastFirstOfficerLocalTime, currentLocalTime, 20f))
                    WakeNavigatorIfRested();
            }

            if (_lastFirstOfficerTrimGameHours < 0f)
            {
                _lastFirstOfficerTrimGameHours = currentGameHours;
            }
            else if (!FirstOfficerAutoTrimEnabled)
            {
                _lastFirstOfficerTrimGameHours = currentGameHours;
            }
            else if (currentGameHours - _lastFirstOfficerTrimGameHours >= FirstOfficerTrimIntervalHours)
            {
                QueueAutoTrimAllSails();
                _lastFirstOfficerTrimGameHours = currentGameHours;
            }

            _lastFirstOfficerLocalTime = currentLocalTime;
        }

        private void RotateWatchCrew()
        {
            if (ActivePilotTask != null
                && ActivePilotTask.AssignedCrewman.IsExhausted)
                StopPilot();

            if (ActiveLookoutTask != null
                && ActiveLookoutTask.AssignedCrewman.IsExhausted)
                StopLookout();

            if (!Crew.Any(c => c.Role == ShipRole.ChiefOfficer))
                return;

            if (ActivePilotTask == null)
                StartPilot(FreshestContinuousDutyCrewman(ShipRole.Pilot));

            if (ActiveLookoutTask == null)
                StartLookout(FreshestContinuousDutyCrewman(ShipRole.Lookout));
        }

        private void CommandMidnightNavigation()
        {
            if (HasNavigationTool(NavigationMethod.Quadrant))
                TryAddNavigateRequest(NavigationMethod.Quadrant, out _, requireTool: true, allowQueue: true);
        }

        private void CommandNoonNavigation()
        {
            if (HasNavigationTool(NavigationMethod.Chronocompass))
            {
                TryAddNavigateRequest(NavigationMethod.Chronocompass, out _, requireTool: true, allowQueue: true);
            }

            if (HasNavigationTool(NavigationMethod.Chronometer))
            {
                TryAddNavigateRequest(NavigationMethod.Chronometer, out _, requireTool: true, allowQueue: true);
            }

            if (HasNavigationTool(NavigationMethod.SunCompass))
            {
                TryAddNavigateRequest(NavigationMethod.SunCompass, out _, requireTool: true, allowQueue: true);
            }
        }

        private void SendNavigatorToSleep()
        {
            var navigator = Navigator;
            if (IsCrewAssignable(navigator))
                AddSleepRequest(navigator);
        }

        private void WakeNavigatorIfRested()
        {
            var navigator = Navigator;
            if (navigator == null || navigator.CurrentStamina < navigator.MaxStamina * NavigatorWakeStaminaRatio)
                return;

            var sleep = SleepRequests.FirstOrDefault(r => r.AssignedCrewman == navigator);
            if (sleep != null)
                CancelSleepRequest(sleep);
        }

        private void QueueAutoTrimAllSails()
        {
            foreach (var sail in allSails)
            {
                if (sail is SimpleSail simple)
                {
                    AddTrimRequest(new TrimRequest(simple));
                }
                else if (sail is DualSheetSail dual)
                {
                    if (dual.getSubtype() == DualSheetSail.DualSheetSailSubtype.Jib)
                        AddJibTrimRequest(new JibTrimRequest(dual));
                    else if (dual.getSubtype() == DualSheetSail.DualSheetSailSubtype.Square)
                        AddSquareTrimRequest(new SquareTrimRequest(dual));
                }
            }
        }

        private void TickShiftSchedule()
        {
            if (Sun.sun == null)
                return;

            if (!EnsureFirstOfficerShiftAuthority())
                return;

            float currentLocalTime = NormalizeHour(Sun.sun.localTime);
            bool hasPreviousLocalTime = _lastShiftLocalTime >= 0f;
            if (hasPreviousLocalTime)
            {
                if (CrossedLocalHour(_lastShiftLocalTime, currentLocalTime, DayShiftStartHour))
                    BeginShiftChange(CrewShift.Day);

                if (CrossedLocalHour(_lastShiftLocalTime, currentLocalTime, DayShiftStartHour + ShiftSleepDelayHours))
                    SendShiftToSleep(CrewShift.Night);

                if (CrossedLocalHour(_lastShiftLocalTime, currentLocalTime, NightShiftStartHour))
                    BeginShiftChange(CrewShift.Night);

                if (CrossedLocalHour(_lastShiftLocalTime, currentLocalTime, NightShiftStartHour + ShiftSleepDelayHours))
                    SendShiftToSleep(CrewShift.Day);
            }

            _lastShiftLocalTime = currentLocalTime;
        }

        private bool EnsureFirstOfficerShiftAuthority()
        {
            if (HasFirstOfficer)
                return true;

            ResetAllCrewShiftsToAdHoc();
            _lastShiftLocalTime = -1f;
            return false;
        }

        private void ResetAllCrewShiftsToAdHoc()
        {
            foreach (var crewman in Crew)
            {
                crewman.SetShift(CrewShift.AdHoc);
                crewman.SetShiftSleepPending(false);
            }
        }

        private void BeginShiftChange(CrewShift newShift)
        {
            WakeCrewForShift(newShift);
            AssignShiftPilot(newShift);
            AssignShiftLookout(newShift);
        }

        private void AssignShiftPilot(CrewShift shift)
        {
            if (ActivePilotTask?.AssignedCrewman?.Shift == shift)
                return;

            var pilot = FreshestCrewmanInShift(ShipRole.Pilot, shift);
            if (pilot != null)
                StartPilot(pilot, shiftHandoff: true);
        }

        private void AssignShiftLookout(CrewShift shift)
        {
            if (ActiveLookoutTask?.AssignedCrewman?.Shift == shift)
                return;

            var lookout = FreshestCrewmanInShift(ShipRole.Lookout, shift);
            if (lookout != null)
                StartLookout(lookout, suppressFirstLandBell: true);
        }

        private Crewman FreshestCrewmanInShift(ShipRole role, CrewShift shift)
        {
            return Crew.Where(c => c.Role == role && c.Shift == shift && IsCrewAssignable(c))
                .OrderByDescending(c => (float)c.CurrentStamina / c.MaxStamina)
                .FirstOrDefault();
        }

        private void WakeCrewForShift(CrewShift shift)
        {
            foreach (var crewman in Crew.Where(c => c.Shift == shift))
            {
                crewman.SetShiftSleepPending(false);
                var sleep = SleepRequests.FirstOrDefault(r => r.AssignedCrewman == crewman);
                if (sleep != null)
                    CancelSleepRequest(sleep);
            }
        }

        private void SendShiftToSleep(CrewShift shift)
        {
            foreach (var crewman in Crew.Where(c => c.Shift == shift))
            {
                StopContinuousDutyForCrewman(crewman);

                if (!ShouldOffShiftCrewSleep(crewman))
                {
                    crewman.SetShiftSleepPending(false);
                    continue;
                }

                crewman.SetShiftSleepPending(true);
                if (!crewman.IsOccupied)
                    AddSleepRequest(crewman);
            }
        }

        private void StopContinuousDutyForCrewman(Crewman crewman)
        {
            if (crewman == null)
                return;

            if (ActivePilotTask?.AssignedCrewman == crewman)
                StopPilot();

            if (ActiveLookoutTask?.AssignedCrewman == crewman)
                StopLookout();
        }

        private void QueuePendingShiftSleepRequests()
        {
            foreach (var crewman in Crew)
            {
                if (!crewman.ShiftSleepPending)
                    continue;

                if (crewman.CurrentStamina >= crewman.MaxStamina)
                {
                    crewman.SetShiftSleepPending(false);
                    continue;
                }

                if (crewman.IsOccupied)
                    continue;

                AddSleepRequest(crewman);
            }
        }

        private void EvaluateOffShiftSleepNeeds()
        {
            foreach (var crewman in Crew)
            {
                if (!IsCrewOffShift(crewman))
                    continue;

                if (crewman.CurrentStamina >= crewman.MaxStamina)
                {
                    crewman.SetShiftSleepPending(false);
                    continue;
                }

                if (!crewman.ShiftSleepPending && ShouldOffShiftCrewSleep(crewman))
                    crewman.SetShiftSleepPending(true);
            }
        }

        private static bool ShouldOffShiftCrewSleep(Crewman crewman)
        {
            return crewman != null
                && crewman.CurrentStamina < crewman.MaxStamina * OffShiftSleepStaminaRatio;
        }

        private CrewShift GetActiveShift()
        {
            float currentLocalHour = GetCurrentLocalHour();
            return currentLocalHour >= DayShiftStartHour && currentLocalHour < NightShiftStartHour
                ? CrewShift.Day
                : CrewShift.Night;
        }

        private bool IsCrewOffShift(Crewman crewman)
        {
            return crewman != null
                && crewman.Shift != CrewShift.AdHoc
                && crewman.Shift != GetActiveShift();
        }

        private bool IsCrewEligibleForContinuousDuty(Crewman crewman)
        {
            return crewman != null && !IsCrewOffShift(crewman);
        }

        private static bool CrossedLocalHour(float previous, float current, float hour)
        {
            previous = NormalizeHour(previous);
            current = NormalizeHour(current);
            hour = NormalizeHour(hour);

            if (Mathf.Approximately(previous, current))
                return false;

            if (previous < current)
                return previous < hour && hour <= current;

            return previous < hour || hour <= current;
        }

        private static float NormalizeHour(float hour)
        {
            hour %= 24f;
            return hour < 0f ? hour + 24f : hour;
        }

        private bool HasNavigationTimekeepingDevice()
        {
            return HasNavigationTool(NavigationMethod.Chronometer)
                || HasNavigationTool(NavigationMethod.Chronocompass);
        }

        private static int GetCurrentLocalDay()
        {
            if (Sun.sun == null)
                return GameState.day;

            float global = NormalizeHour(Sun.sun.globalTime);
            float local = NormalizeHour(Sun.sun.localTime);
            float delta = local - global;

            if (delta < -12f)
                return GameState.day + 1;
            if (delta > 12f)
                return GameState.day - 1;

            return GameState.day;
        }

        private static float GetCurrentLocalHour()
        {
            return Sun.sun != null
                ? NormalizeHour(Sun.sun.localTime)
                : 0f;
        }

        // Called once per second from Plugin.Update(). Assigns open requests to free
        // deckhands and marks completed tasks as done.
        public void Tick()
        {
            // Drain stamina at 1 unit per in-game minute. Optional config restores the old
            // behavior where actively working crew drain twice as fast.
            // Sleeping crew are exempt from drain — their stamina is handled by SleepRequest.Tick().
            float currentTime = Sun.sun.globalTime;
            float deltaMinutes = 0f;
            if (_lastGlobalTime >= 0f)
            {
                float deltaHours = currentTime - _lastGlobalTime;
                if (deltaHours < 0f) deltaHours += 24f; // midnight rollover
                deltaMinutes = deltaHours * 60f;
                foreach (var c in Crew)
                {
                    if (c.CurrentTask is SleepRequest) continue;
                    float drain = deltaMinutes;
                    if (Plugin.ExtraWorkingStaminaDrain != null
                        && Plugin.ExtraWorkingStaminaDrain.Value
                        && c.IsOccupied)
                        drain *= 2f;
                    c.DrainStamina(drain);
                }
            }
            _lastGlobalTime = currentTime;

            TickShiftSchedule();
            TickFirstOfficer();
            TickLookoutPassiveCertaintyDecay();

            EvaluateOffShiftSleepNeeds();
            QueuePendingShiftSleepRequests();

            // Auto-trigger sleep for exhausted, unoccupied crew, but only up to the number of
            // available beds. Crew with no bed to claim stay unoccupied so the player can still
            // use them. Both Open and InProgress requests count as claimed beds.
            int? autoTriggerBedCount = null;
            foreach (var c in Crew)
            {
                if (!c.IsExhausted || c.IsOccupied) continue;
                if (autoTriggerBedCount == null) autoTriggerBedCount = LocatorUtils.CountBeds();
                if (SleepRequests.Count >= autoTriggerBedCount.Value) break;
                AddSleepRequest(c);
            }

            TickQuartermasterBailing();

            foreach (var req in WorkRequests)
            {
                if (req.Status == WorkRequestStatus.InProgress && req.IsComplete())
                {
                    req.Status = WorkRequestStatus.Complete;
                    if (req.AssignedCrewman != null)
                        req.AssignedCrewman.CurrentTask = null;
                    foreach (var t in req.Targets)
                        crewWinchInstructions.Remove(t.Winch);
                }
                else if (req.Status == WorkRequestStatus.Positioning)
                {
                    if (req.IsPositioningComplete())
                    {
                        foreach (var t in req.Targets)
                        {
                            t.MaxPower = req.AssignedCrewman.Strength * 5f;
                            crewWinchInstructions[t.Winch] = t;
                        }
                        req.Begin();
                    }
                }
            }

            WorkRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);

            var navCoord = CrewNavigationCoordinator.Instance;

            foreach (var trim in TrimRequests)
            {
                if (trim.Status == WorkRequestStatus.InProgress && trim.IsComplete())
                {
                    trim.Status = WorkRequestStatus.Complete;
                    if (trim.AssignedCrewman != null)
                        trim.AssignedCrewman.CurrentTask = null;
                    crewWinchInstructions.Remove(trim.Sail.getSheetWinch());
                }
                else if (trim.Status == WorkRequestStatus.Positioning &&
                         (navCoord.IsPositioningComplete(trim) || trim.IsPositioningComplete()))
                {
                    navCoord.Complete(trim);
                    trim.Begin(trim.AssignedCrewman);
                }
            }

            TrimRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);

            AssignOpenDeckhandTasksByDistance();

            foreach (var trim in TrimRequests)
            {
                if (trim.Status != WorkRequestStatus.Open) continue;
                var crewman = Crew.FirstOrDefault(c => !c.IsOccupied && c.Role == ShipRole.Deckhand);
                if (crewman == null) break;
                crewman.CurrentTask = trim;
                trim.AssignedCrewman = crewman;
                trim.BeginPositioning(crewman);
                navCoord.TryBeginWinchPositioning(trim, crewman, trim.Sail.getSheetWinch());
            }

            foreach (var jtrim in JibTrimRequests)
            {
                if (jtrim.Status == WorkRequestStatus.InProgress && jtrim.IsComplete())
                {
                    jtrim.Status = WorkRequestStatus.Complete;
                    if (jtrim.AssignedCrewman != null)
                        jtrim.AssignedCrewman.CurrentTask = null;
                    crewWinchInstructions.Remove(jtrim.Sail.getPortSheetWinch());
                    crewWinchInstructions.Remove(jtrim.Sail.getStarboardSheetWinch());
                }
                else if (jtrim.Status == WorkRequestStatus.InProgress && jtrim.IsRepositioning && jtrim.SecondWinch != null)
                {
                    navCoord.TryBeginWinchPositioning((jtrim, 1), jtrim.AssignedCrewman, jtrim.SecondWinch);
                    if (navCoord.IsPositioningComplete((jtrim, 1)))
                    {
                        navCoord.Complete((jtrim, 1));
                        jtrim.BeginSecondWinch();
                    }
                }
                else if (jtrim.Status == WorkRequestStatus.Positioning &&
                         (navCoord.IsPositioningComplete(jtrim) || jtrim.IsPositioningComplete()))
                {
                    navCoord.Complete(jtrim);
                    jtrim.Begin(jtrim.AssignedCrewman);
                }
            }

            JibTrimRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);

            foreach (var jtrim in JibTrimRequests)
            {
                if (jtrim.Status != WorkRequestStatus.Open) continue;
                var crewman = Crew.FirstOrDefault(c => !c.IsOccupied && c.Role == ShipRole.Deckhand);
                if (crewman == null) break;
                crewman.CurrentTask = jtrim;
                jtrim.AssignedCrewman = crewman;
                jtrim.BeginPositioning(crewman);
                navCoord.TryBeginWinchPositioning(jtrim, crewman, jtrim.Sail.getPortSheetWinch());
            }

            foreach (var strim in SquareTrimRequests)
            {
                if (strim.Status == WorkRequestStatus.InProgress && strim.IsComplete())
                {
                    strim.Status = WorkRequestStatus.Complete;
                    if (strim.AssignedCrewman  != null) strim.AssignedCrewman.CurrentTask  = null;
                    if (strim.AssignedCrewman2 != null) strim.AssignedCrewman2.CurrentTask = null;
                    crewWinchInstructions.Remove(strim.Sail.getPortSheetWinch());
                    crewWinchInstructions.Remove(strim.Sail.getStarboardSheetWinch());
                }
                else if (strim.Status == WorkRequestStatus.Positioning)
                {
                    bool c1 = navCoord.IsPositioningComplete((strim, 0)) || strim.IsPositioningComplete();
                    bool c2 = navCoord.IsPositioningComplete((strim, 1)) || strim.IsPositioningComplete();
                    if (c1 && c2)
                    {
                        navCoord.Complete((strim, 0));
                        navCoord.Complete((strim, 1));
                        strim.Begin();
                    }
                }
            }

            SquareTrimRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);

            // Square trim requires two simultaneous deckhands; only start when both are free.
            foreach (var strim in SquareTrimRequests)
            {
                if (strim.Status != WorkRequestStatus.Open) continue;
                var free = Crew.Where(c => !c.IsOccupied && c.Role == ShipRole.Deckhand).Take(2).ToList();
                if (free.Count < 2) continue;
                free[0].CurrentTask = strim;
                free[1].CurrentTask = strim;
                strim.AssignedCrewman  = free[0];
                strim.AssignedCrewman2 = free[1];
                strim.BeginPositioning();
                navCoord.TryBeginWinchPositioning((strim, 0), free[0], strim.Sail.getPortSheetWinch());
                navCoord.TryBeginWinchPositioning((strim, 1), free[1], strim.Sail.getStarboardSheetWinch());
            }

            // Navigate requests: assign navigator when free, complete when timer expires.
            foreach (var nav in NavigateRequests)
            {
                if (nav.Status == WorkRequestStatus.Open)
                {
                    var crewman = Navigator;
                    if (crewman != null && !crewman.IsOccupied)
                        nav.Begin(crewman);
                }
                else if (nav.Status == WorkRequestStatus.InProgress && nav.IsComplete())
                {
                    nav.Status = WorkRequestStatus.Complete;
                    if (nav.Navigator != null) nav.Navigator.CurrentTask = null;

                    var boat = CrewBoatContextResolver.GetActiveWorldBoat();
                    if (boat != null)
                    {
                        var coords  = FloatingOriginManager.instance.GetGlobeCoords(boat);
                        float trueLat = coords.z;
                        float trueLon = coords.x;

                        var weatherState = WeatherUtils.GetWeatherState();

                        if (weatherState >= WeatherState.Rain)
                        {
                            nav.OnComplete?.Invoke(NavigationResult.Failure(nav.Method, weatherState));
                        }
                        else
                        {
                            int intel    = nav.Navigator?.Intelligence ?? 3;
                            float maxErr = intel == 1 ? 5f : Mathf.Max(0f, (6 - intel) * 0.25f);
                            float latErr = (float)(rng.NextDouble() * 2.0 - 1.0) * maxErr;
                            float lonErr = (float)(rng.NextDouble() * 2.0 - 1.0) * maxErr;

                            var result = new NavigationResult(
                                nav.Method,
                                GetCurrentLocalDay(), GetCurrentLocalHour(),
                                nav.CanEstimateLatitude,  trueLat + (nav.CanEstimateLatitude  ? latErr : 0f),
                                nav.CanEstimateLongitude, trueLon + (nav.CanEstimateLongitude ? lonErr : 0f),
                                HasNavigationTimekeepingDevice());
                            nav.OnComplete?.Invoke(result);
                        }
                    }
                }
            }
            NavigateRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);

            // Mooring requests: walk to the selected side, tie available ropes to matching dock cleats, then complete.
            foreach (var mooring in MooringRequests)
            {
                if (mooring.Status == WorkRequestStatus.Positioning
                    && (mooring.IsPositioningComplete() || mooring.IsPositioningTimedOut()))
                {
                    mooring.Begin();
                }
                else if (mooring.Status == WorkRequestStatus.InProgress)
                {
                    mooring.Tick();
                }
            }

            MooringRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);

            // Haul & Sell requests: walk to the cargo, then hand off to the per-frame cargo haul.
            foreach (var haul in HaulSellRequests)
                haul.AbortIfPlayerLeftOriginBoat();

            foreach (var haul in HaulSellRequests)
            {
                if (haul.Status == WorkRequestStatus.Positioning
                    && (haul.IsPositioningComplete() || haul.IsPositioningTimedOut()))
                {
                    haul.BeginHaul();
                }
            }

            HaulSellRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);

            // Bail requests: tick active ones, then assign free deckhands to open ones.
            foreach (var bail in BailRequests)
            {
                if (bail.Status == WorkRequestStatus.InProgress)
                    bail.Tick();
            }

            BailRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);

            foreach (var bail in BailRequests)
            {
                if (bail.Status != WorkRequestStatus.Open) continue;
                var crewman = Crew.FirstOrDefault(c => !c.IsOccupied && c.Role == ShipRole.Deckhand);
                if (crewman == null) break;
                bail.Begin(crewman);
            }

            // Sleep requests: tick active ones, advance positioning completions, assign beds to waiting ones.
            foreach (var sleep in SleepRequests)
            {
                if (sleep.Status == WorkRequestStatus.InProgress)
                {
                    sleep.Tick(deltaMinutes);
                    if (sleep.Status == WorkRequestStatus.Complete)
                    {
                        sleep.AssignedCrewman.SetShiftSleepPending(false);
                        navCoord.OnSleepCompleted(sleep.AssignedCrewman);
                    }
                }
                else if (sleep.Status == WorkRequestStatus.Positioning
                      && (navCoord.IsPositioningComplete(sleep) || sleep.IsPositioningTimedOut()))
                {
                    navCoord.Complete(sleep);
                    sleep.Begin();
                }
            }

            SleepRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);

            int bedsInUse = SleepRequests.Count(r => r.Status == WorkRequestStatus.InProgress
                                                   || r.Status == WorkRequestStatus.Positioning);
            List<Component> availableBeds = null;
            foreach (var sleep in SleepRequests)
            {
                if (sleep.Status != WorkRequestStatus.Open) continue;
                if (sleep.AssignedCrewman.CurrentTask != sleep) continue;
                if (availableBeds == null) availableBeds = LocatorUtils.FindBedsOnBoat();
                var bed = availableBeds.FirstOrDefault(b => !SleepRequests.Any(s => s.AssignedBed == b));
                if (bed == null) break;
                if (navCoord.BeginSleep(sleep, sleep.AssignedCrewman, bed))
                {
                    sleep.BeginPositioning(bed);
                    bedsInUse++;
                }
            }
        }

        public void AddSleepRequest(Crewman crewman)
        {
            if (crewman == null || crewman.IsOccupied)
                return;
            if (SleepRequests.Any(r => r.AssignedCrewman == crewman))
                return;
            SleepRequests.Add(new SleepRequest(crewman));
        }

        private void AssignOpenDeckhandTasksByDistance()
        {
            foreach (var crewman in Crew.Where(c => !c.IsOccupied && c.Role == ShipRole.Deckhand).ToList())
            {
                var ranked = GetOpenDeckhandTaskCandidates(crewman)
                    .OrderBy(c => c.Distance)
                    .ToList();

                if (ranked.Count == 0)
                    break;

                CrewDebugLog.Ok("RuntimeNav",
                    "Task distance ranking for crew='" + crewman.Name + "': "
                    + string.Join(", ", ranked.Select(c => c.Label + "=" + FormatDistance(c.Distance)).ToArray()));

                ranked[0].Begin(crewman);
            }
        }

        private IEnumerable<DeckhandTaskCandidate> GetOpenDeckhandTaskCandidates(Crewman crewman)
        {
            foreach (var request in WorkRequests.Where(r => r.Status == WorkRequestStatus.Open))
            {
                yield return new DeckhandTaskCandidate(
                    GetWorkRequestLabel(request),
                    EstimateDistanceToWorkRequest(crewman, request),
                    c =>
                    {
                        c.CurrentTask = request;
                        request.AssignedCrewman = c;
                        request.BeginPositioning(c);
                    });
            }

            foreach (var request in MooringRequests.Where(r => r.Status == WorkRequestStatus.Open))
            {
                yield return new DeckhandTaskCandidate(
                    request.CommandName,
                    EstimateDistanceToMooringRequest(crewman, request),
                    c => request.BeginPositioning(c));
            }

            foreach (var request in HaulSellRequests.Where(r => r.Status == WorkRequestStatus.Open))
            {
                yield return new DeckhandTaskCandidate(
                    request.CommandName + " " + request.ItemName,
                    EstimateDistanceToHaulSellRequest(crewman, request),
                    c => request.BeginPositioning(c));
            }
        }

        private static float EstimateDistanceToWorkRequest(Crewman crewman, WorkRequest request)
        {
            var winch = GetPrimaryWinch(request);
            return winch
                ? CrewNavigationCoordinator.Instance.EstimateDistanceToWinch(crewman, winch)
                : float.MaxValue;
        }

        private static float EstimateDistanceToMooringRequest(Crewman crewman, MooringRequest request)
        {
            if (request == null || !request.TryGetWorkLocalPosition(out var localPosition))
                return float.MaxValue;

            return CrewNavigationCoordinator.Instance.EstimateDistanceToLocalPosition(crewman, localPosition);
        }

        private static float EstimateDistanceToHaulSellRequest(Crewman crewman, HaulSellRequest request)
        {
            if (request == null || !request.Item)
                return float.MaxValue;

            Transform worldBoat = request.Item.currentActualBoat
                ? request.Item.currentActualBoat
                : CrewBoatContextResolver.GetActiveWorldBoat();
            if (!worldBoat)
                return float.MaxValue;

            Vector3 localPosition = worldBoat.InverseTransformPoint(request.Item.transform.position);
            return CrewNavigationCoordinator.Instance.EstimateDistanceToLocalPosition(crewman, localPosition);
        }

        private static GPButtonRopeWinch GetPrimaryWinch(WorkRequest request)
        {
            return request?.Targets?.FirstOrDefault()?.Winch;
        }

        private static string GetWorkRequestLabel(WorkRequest request)
        {
            if (request == null)
                return "null";

            var winch = GetPrimaryWinch(request);
            string winchName = winch ? winch.name : "no-winch";
            return request.DisplayLabel + "@" + winchName;
        }

        private static string FormatDistance(float distance)
        {
            return float.IsInfinity(distance) || distance == float.MaxValue
                ? "unreachable"
                : distance.ToString("0.0") + "m";
        }

        private sealed class DeckhandTaskCandidate
        {
            internal string Label { get; }
            internal float Distance { get; }
            internal Action<Crewman> Begin { get; }

            internal DeckhandTaskCandidate(string label, float distance, Action<Crewman> begin)
            {
                Label = label;
                Distance = distance;
                Begin = begin;
            }
        }

        public void CancelSleepRequest(SleepRequest request)
        {
            if (request.AssignedCrewman != null)
                request.AssignedCrewman.CurrentTask = null;
            CrewNavigationCoordinator.Instance.Cancel(request);
            SleepRequests.Remove(request);
        }

        // Called every frame from Plugin.Update(). Drives the per-frame evaluation logic
        // for active trim operations.
        public void TrimTick()
        {
            foreach (var trim in TrimRequests)
            {
                if (trim.Status == WorkRequestStatus.InProgress)
                    trim.UpdateFrame();
            }

            foreach (var jtrim in JibTrimRequests)
            {
                if (jtrim.Status == WorkRequestStatus.InProgress)
                    jtrim.UpdateFrame();
            }

            foreach (var strim in SquareTrimRequests)
            {
                if (strim.Status == WorkRequestStatus.InProgress)
                    strim.UpdateFrame();
            }

            foreach (var haul in HaulSellRequests)
            {
                if (haul.Status == WorkRequestStatus.InProgress)
                    haul.UpdateFrame();
            }
        }

        public void deployAllSails()
        {
            foreach (ICommonSailActions sail in allSails)
                sail.deploySail();
            PrepareWinchInstructions();
        }

        public void reefAllSails()
        {
            foreach (ICommonSailActions sail in allSails)
                sail.reefSail();
            PrepareWinchInstructions();
        }

        public void easeAllSails()
        {
            foreach (ICommonSailActions sail in allSails)
                sail.easeSail();
            PrepareWinchInstructions();
        }

        public void trimAllSails()
        {
            foreach (ICommonSailActions sail in allSails)
                sail.trimSail();
            PrepareWinchInstructions();
        }

        public void stop()
        {
            foreach (ICommonSailActions sail in allSails)
                sail.stop();
            PrepareWinchInstructions();
        }

        public void bringToPort()
        {
            foreach (DualSheetSail sail in squareSails)
                sail.bringToPort();
            PrepareWinchInstructions();
        }

        public void bringToStarboard()
        {
            foreach (DualSheetSail sail in squareSails)
                sail.bringToStarboard();
            PrepareWinchInstructions();
        }

        public void deploySquares()
        {
            foreach (DualSheetSail sail in squareSails)
                sail.deploySail();
            PrepareWinchInstructions();
        }

        public void reefSquares()
        {
            foreach (DualSheetSail sail in squareSails)
                sail.reefSail();
            PrepareWinchInstructions();
        }

        public void deployOthers()
        {
            foreach (ICommonSailActions sail in allSails)
            {
                if (!squareSails.Contains(sail))
                    sail.deploySail();
            }
            PrepareWinchInstructions();
        }

        public void reefOthers()
        {
            foreach (ICommonSailActions sail in allSails)
            {
                if (!squareSails.Contains(sail))
                    sail.reefSail();
            }
            PrepareWinchInstructions();
        }

        private void PrepareWinchInstructions()
        {
            winchInstructions = new Dictionary<GPButtonRopeWinch, float>();
            foreach (SimpleSail sail in simpleSails)
            {
                if (sail.getHalyardWinch() != null)
                    winchInstructions.Add(sail.getHalyardWinch(), sail.halyardWinchPower);
                else if (sail.halyardWinchPower != 0)
                    Console.WriteLine($"WARNING: Null halyard winch for Simple sail {sail.getSailName()} despite non-zero power instruction.");

                if (sail.getSheetWinch() != null)
                    winchInstructions.Add(sail.getSheetWinch(), sail.sheetWinchPower);
                else if (sail.sheetWinchPower != 0)
                    Console.WriteLine($"WARNING: Null sheet winch for Simple sail {sail.getSailName()} despite non-zero power instruction.");
            }

            foreach (DualSheetSail sail in dualSheetSails)
            {
                if (sail.getHalyardWinch() != null)
                    winchInstructions.Add(sail.getHalyardWinch(), sail.halyardWinchPower);
                else if (sail.halyardWinchPower != 0)
                    Console.WriteLine($"WARNING: Null halyard winch for DualSheet sail {sail.getSailName()} despite non-zero power instruction.");

                if (sail.getPortSheetWinch() != null)
                    winchInstructions.Add(sail.getPortSheetWinch(), sail.portSheetWinchPower);
                else if (sail.portSheetWinchPower != 0)
                    Console.WriteLine($"WARNING: Null port sheet winch for DualSheet sail {sail.getSailName()} despite non-zero power instruction.");

                if (sail.getStarboardSheetWinch() != null)
                    winchInstructions.Add(sail.getStarboardSheetWinch(), sail.starboardSheetWinchPower);
                else if (sail.starboardSheetWinchPower != 0)
                    Console.WriteLine($"WARNING: Null starboard sheet winch for DualSheet sail {sail.getSailName()} despite non-zero power instruction.");
            }

            foreach (DualSheetSail sail in squareSails)
            {
                if (sail.getHalyardWinch() != null)
                    winchInstructions.Add(sail.getHalyardWinch(), sail.halyardWinchPower);
                else if (sail.halyardWinchPower != 0)
                    Console.WriteLine($"WARNING: Null halyard winch for Square sail {sail.getSailName()} despite non-zero power instruction.");

                // Ganged square sails share sheet winches — avoid duplicating instructions.
                if (sail.getPortSheetWinch() != null)
                {
                    if (!winchInstructions.ContainsKey(sail.getPortSheetWinch()))
                        winchInstructions.Add(sail.getPortSheetWinch(), sail.portSheetWinchPower);
                }
                else if (sail.portSheetWinchPower != 0)
                {
                    Console.WriteLine($"WARNING: Null port sheet winch for Square sail {sail.getSailName()} despite non-zero power instruction.");
                }

                if (sail.getStarboardSheetWinch() != null)
                {
                    if (!winchInstructions.ContainsKey(sail.getStarboardSheetWinch()))
                        winchInstructions.Add(sail.getStarboardSheetWinch(), sail.starboardSheetWinchPower);
                }
                else if (sail.starboardSheetWinchPower != 0)
                {
                    Console.WriteLine($"WARNING: Null starboard sheet winch for Square sail {sail.getSailName()} despite non-zero power instruction.");
                }
            }
        }
    }
}
