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
        public List<SleepRequest>    SleepRequests    { get; private set; }
        public Dictionary<GPButtonRopeWinch, WinchTarget> crewWinchInstructions;

        private readonly System.Random rng = new System.Random();

        public Dictionary<string, VesselSaveData> AllVesselsData { get; set; }
        public string CurrentVesselKey { get; private set; }

        public List<SailGroup> SailGroups { get; private set; }
        public SailGroup AllSailsGroup { get; private set; }
        public SailGroup SelectedGroup { get; set; }

        public Port CurrentPort { get; private set; }
        public List<Crewman> AvailableAtPort { get; private set; } = new List<Crewman>();
        public Dictionary<string, List<Crewman>> PortCrewPools { get; private set; } = new Dictionary<string, List<Crewman>>();
        private Dictionary<string, bool> portIsHub = new Dictionary<string, bool>();
        private float _lastGlobalTime = -1f;

        private const int SalaryCurrency = 0;
        private const int SalaryPerCrewPerDay = 10;

        public int TotalSalaryPay { get; private set; }
        public int[] TotalSharePayByCurrency { get; private set; }
        public Dictionary<int, CargoPaySaveData> CargoPayRecords { get; private set; }

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
            SailGroups = new List<SailGroup>();
            Crew = new List<Crewman>();
            TotalSharePayByCurrency = new int[4];
            CargoPayRecords = new Dictionary<int, CargoPaySaveData>();
            Reset();
            Sun.OnNewDay += OnNewDay;
        }

        private void OnNewDay()
        {
            PayDailySalaries();
            if (GameState.day % 7 == 0)
                RefreshPortCrewPools();
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
            foreach (var key in PortCrewPools.Keys)
            {
                bool hub = portIsHub.TryGetValue(key, out var h) && h;
                int count = hub ? 5 : 1;
                var pool = new List<Crewman>();
                for (int i = 0; i < count; i++)
                    pool.Add(GenerateRandomCrewman(hub));
                PortCrewPools[key] = pool;
            }
            if (CurrentPort != null && PortCrewPools.TryGetValue(CurrentPort.GetPortName(), out var current))
                AvailableAtPort = current;
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
            return vesselData;
        }

        private void EnsureCurrentVesselKey()
        {
            if (CurrentVesselKey != null || GameState.currentBoat == null)
                return;

            string vesselKey = GameState.currentBoat.name.Replace("(Clone)", "").Trim();
            SetCurrentVessel(vesselKey);
        }

        public void Reset()
        {
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
            SleepRequests    = new List<SleepRequest>();
            crewWinchInstructions = new Dictionary<GPButtonRopeWinch, WinchTarget>();
            AnchorWinches = new List<GPButtonRopeWinch>();
            _lastGlobalTime = -1f;

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

        // Returns the crew member of the given role with the highest stamina ratio.
        public Crewman FreshestCrewman(ShipRole role) =>
            Crew.Where(c => c.Role == role && !c.IsOccupied)
                .OrderByDescending(c => (float)c.CurrentStamina / c.MaxStamina)
                .FirstOrDefault();

        public void StartPilot(Crewman crewman)
        {
            if (crewman.IsOccupied) return;
            StopPilot();
            ActivePilotTask = new PilotTask(crewman);
        }

        public void StopPilot()
        {
            ActivePilotTask?.Cancel();
            ActivePilotTask = null;
        }

        public void StartLookout(Crewman crewman)
        {
            if (crewman.IsOccupied) return;
            StopLookout();
            ActiveLookoutTask = new LookoutTask(crewman);
        }

        public void StopLookout()
        {
            ActiveLookoutTask?.Cancel();
            ActiveLookoutTask = null;
        }

        public void AssignNavigator(Crewman crewman)
        {
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
            AvailableAtPort = PortCrewPools[key];
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

        public void HireCrew(Crewman c)
        {
            AvailableAtPort.Remove(c);
            Crew.Add(c);
        }

        public void FireCrew(Crewman c)
        {
            var navReq = NavigateRequests.FirstOrDefault(r => r.Navigator == c);
            if (navReq != null) CancelNavigateRequest(navReq);
            if (ActivePilotTask?.AssignedCrewman == c)   StopPilot();
            if (ActiveLookoutTask?.AssignedCrewman == c) StopLookout();
            if (_assignedNavigator == c) _assignedNavigator = null;
            var sleepReq = SleepRequests.FirstOrDefault(r => r.AssignedCrewman == c);
            if (sleepReq != null) CancelSleepRequest(sleepReq);
            c.CurrentTask = null;
            Crew.Remove(c);
            if (CurrentPort != null)
            {
                string key = CurrentPort.GetPortName();
                if (!PortCrewPools.ContainsKey(key))
                    PortCrewPools[key] = new List<Crewman>();
                PortCrewPools[key].Add(c);
                AvailableAtPort = PortCrewPools[key];
            }
        }

        public void RestoreShipCrew(List<CrewmanSaveData> saved)
        {
            _lastGlobalTime = -1f;
            Crew.Clear();
            if (saved == null || saved.Count == 0) { return; }
            foreach (var d in saved)
                Crew.Add(FromSaveData(d));
        }

        public void RestorePortPools(Dictionary<string, List<CrewmanSaveData>> saved)
        {
            PortCrewPools.Clear();
            if (saved == null) return;
            foreach (var kv in saved)
                PortCrewPools[kv.Key] = kv.Value.Select(FromSaveData).ToList();
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
                d.modelIndex);

        public void addSail(SimpleSail sail)
        {
            Console.WriteLine(string.Format("Finalizing Sail:{0}, with Halyard:{1}, Sheet:{2}",
                sail.getRealSail().name, sail.getHalyardWinch().name, sail.getSheetWinch().name));
            RestoreFriendlyName(sail);
            simpleSails.Add(sail);
            allSails.Add(sail);
        }

        public void addDualSheetSail(DualSheetSail sail)
        {
            Console.WriteLine(string.Format("Finalizing Sail:{0}, with Halyard:{1}, PortSheet:{2}, StarboardSheet:{3}",
                sail.getRealSail().name, sail.getHalyardWinch().name, sail.getPortSheetWinch().name, sail.getStarboardSheetWinch().name));
            RestoreFriendlyName(sail);
            dualSheetSails.Add(sail);
            allSails.Add(sail);
        }

        public void addSquareSail(DualSheetSail sail)
        {
            Console.WriteLine(string.Format("Finalizing Square Sail:{0}, with Halyard:{1}, PortSheet:{2}, StarboardSheet:{3}",
                sail.getRealSail().name, sail.getHalyardWinch().name, sail.getPortSheetWinch().name, sail.getStarboardSheetWinch().name));
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

        public void CancelBailRequest(BailRequest request)
        {
            if (request.AssignedCrewman != null)
                request.AssignedCrewman.CurrentTask = null;
            BailRequests.Remove(request);
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

        // Called once per second from Plugin.Update(). Assigns open requests to free
        // deckhands and marks completed tasks as done.
        public void Tick()
        {
            // Drain stamina at 1 unit per in-game minute (2 units/min while working).
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
                    c.DrainStamina(c.IsOccupied ? deltaMinutes * 2f : deltaMinutes);
                }
            }
            _lastGlobalTime = currentTime;

            // Auto-trigger sleep for exhausted, unoccupied crew, but only up to the number of
            // available beds. Crew with no bed to claim stay unoccupied so the player can still
            // use them. Both Open and InProgress requests count as claimed beds.
            int? autoTriggerBedCount = null;
            foreach (var c in Crew)
            {
                if (!c.IsExhausted || c.IsOccupied) continue;
                if (autoTriggerBedCount == null) autoTriggerBedCount = LocatorUtils.CountBeds();
                if (SleepRequests.Count >= autoTriggerBedCount.Value) break;
                SleepRequests.Add(new SleepRequest(c));
            }

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

            AssignOpenWorkRequestsByDeckhand();

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

                    var boat = GameState.currentBoat;
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
                            float maxErr = intel == 1 ? 5f : (6 - intel) * 0.25f;
                            float latErr = (float)(rng.NextDouble() * 2.0 - 1.0) * maxErr;
                            float lonErr = (float)(rng.NextDouble() * 2.0 - 1.0) * maxErr;

                            var result = new NavigationResult(
                                nav.Method,
                                GameState.day, Sun.sun.localTime,
                                nav.CanEstimateLatitude,  trueLat + (nav.CanEstimateLatitude  ? latErr : 0f),
                                nav.CanEstimateLongitude, trueLon + (nav.CanEstimateLongitude ? lonErr : 0f));
                            nav.OnComplete?.Invoke(result);
                        }
                    }
                }
            }
            NavigateRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);

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
                        navCoord.OnSleepCompleted(sleep.AssignedCrewman);
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
            if (crewman.IsOccupied) return;
            SleepRequests.Add(new SleepRequest(crewman));
        }

        private void AssignOpenWorkRequestsByDeckhand()
        {
            var openRequests = WorkRequests
                .Where(r => r.Status == WorkRequestStatus.Open)
                .ToList();
            if (openRequests.Count == 0)
                return;

            foreach (var crewman in Crew.Where(c => !c.IsOccupied && c.Role == ShipRole.Deckhand).ToList())
            {
                if (openRequests.Count == 0)
                    break;

                var request = FindClosestOpenWorkRequest(crewman, openRequests);
                if (request == null)
                    continue;

                crewman.CurrentTask = request;
                request.AssignedCrewman = crewman;
                request.BeginPositioning(crewman);
                openRequests.Remove(request);
            }
        }

        private WorkRequest FindClosestOpenWorkRequest(Crewman crewman, List<WorkRequest> openRequests)
        {
            if (crewman == null || openRequests == null || openRequests.Count == 0)
                return null;

            var ranked = openRequests
                .Select(r => new
                {
                    Request = r,
                    Distance = EstimateDistanceToWorkRequest(crewman, r)
                })
                .OrderBy(x => x.Distance)
                .ToList();

            CrewDebugLog.Ok("RuntimeNav",
                "Task distance ranking for crew='" + crewman.Name + "': "
                + string.Join(", ", ranked.Select(x => GetWorkRequestLabel(x.Request) + "=" + FormatDistance(x.Distance)).ToArray()));

            return ranked.FirstOrDefault()?.Request;
        }

        private static float EstimateDistanceToWorkRequest(Crewman crewman, WorkRequest request)
        {
            var winch = GetPrimaryWinch(request);
            return winch
                ? CrewNavigationCoordinator.Instance.EstimateDistanceToWinch(crewman, winch)
                : float.MaxValue;
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
                winchInstructions.Add(sail.getHalyardWinch(), sail.halyardWinchPower);
                winchInstructions.Add(sail.getSheetWinch(), sail.sheetWinchPower);
            }

            foreach (DualSheetSail sail in dualSheetSails)
            {
                winchInstructions.Add(sail.getHalyardWinch(), sail.halyardWinchPower);
                winchInstructions.Add(sail.getPortSheetWinch(), sail.portSheetWinchPower);
                winchInstructions.Add(sail.getStarboardSheetWinch(), sail.starboardSheetWinchPower);
            }

            foreach (DualSheetSail sail in squareSails)
            {
                winchInstructions.Add(sail.getHalyardWinch(), sail.halyardWinchPower);

                // Ganged square sails share sheet winches — avoid duplicating instructions.
                if (!winchInstructions.ContainsKey(sail.getPortSheetWinch()))
                    winchInstructions.Add(sail.getPortSheetWinch(), sail.portSheetWinchPower);
                if (!winchInstructions.ContainsKey(sail.getStarboardSheetWinch()))
                    winchInstructions.Add(sail.getStarboardSheetWinch(), sail.starboardSheetWinchPower);
            }
        }
    }
}
