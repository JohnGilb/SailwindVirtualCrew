using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

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
        public Dictionary<GPButtonRopeWinch, WinchTarget> crewWinchInstructions;

        private readonly Random rng = new Random();

        public Dictionary<string, VesselSaveData> AllVesselsData { get; set; }
        public string CurrentVesselKey { get; private set; }

        public List<SailGroup> SailGroups { get; private set; }
        public SailGroup AllSailsGroup { get; private set; }
        public SailGroup SelectedGroup { get; set; }

        public Port CurrentPort { get; private set; }
        public List<Crewman> AvailableAtPort { get; private set; } = new List<Crewman>();
        public Dictionary<string, List<Crewman>> PortCrewPools { get; private set; } = new Dictionary<string, List<Crewman>>();
        private Dictionary<string, bool> portIsHub = new Dictionary<string, bool>();

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

        public IReadOnlyList<ICommonSailActions> AllSails => allSails.AsReadOnly();

        private VirtualCrewManager()
        {
            AllVesselsData = new Dictionary<string, VesselSaveData>();
            SailGroups = new List<SailGroup>();
            Crew = new List<Crewman>();
            Reset();
            Sun.OnNewDay += OnNewDay;
        }

        private void OnNewDay()
        {
            if (GameState.day % 7 == 0)
                RefreshPortCrewPools();
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
                    var group = new SailGroup(gd.name);
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
            }
        }

        public void SetVesselFriendlyName(string name)
        {
            if (CurrentVesselKey == null) return;
            if (!AllVesselsData.ContainsKey(CurrentVesselKey))
                AllVesselsData[CurrentVesselKey] = new VesselSaveData();
            AllVesselsData[CurrentVesselKey].friendlyName = string.IsNullOrEmpty(name) ? null : name;
        }

        public void Reset()
        {
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
            crewWinchInstructions = new Dictionary<GPButtonRopeWinch, WinchTarget>();

            // Rebuild the AllSails group; keep user-created groups intact.
            AllSailsGroup = new SailGroup("All Sails", isAllSails: true);
            if (SailGroups.Count > 0 && SailGroups[0].IsAllSails)
                SailGroups[0] = AllSailsGroup;
            else
                SailGroups.Insert(0, AllSailsGroup);
        }

        public Crewman Pilot     => Crew.FirstOrDefault(c => c.Role == ShipRole.Pilot);
        public Crewman Navigator => Crew.FirstOrDefault(c => c.Role == ShipRole.Navigator);
        public Crewman Lookout   => Crew.FirstOrDefault(c => c.Role == ShipRole.Lookout);

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
            c.CurrentTask = null;
            Crew.Remove(c);
        }

        public void RestoreShipCrew(List<CrewmanSaveData> saved)
        {
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

        private static Crewman FromSaveData(CrewmanSaveData d) =>
            new Crewman(d.name, d.role,
                d.strength, d.dexterity, d.constitution, d.intelligence, d.wisdom, d.charisma,
                d.advStrength, d.advDexterity, d.advConstitution, d.advIntelligence, d.advWisdom, d.advCharisma);

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
            SquareTrimRequests.Remove(request);
        }

        // Called once per second from Plugin.Update(). Assigns open requests to free
        // deckhands and marks completed tasks as done.
        public void Tick()
        {
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

            foreach (var trim in TrimRequests)
            {
                if (trim.Status == WorkRequestStatus.InProgress && trim.IsComplete())
                {
                    trim.Status = WorkRequestStatus.Complete;
                    if (trim.AssignedCrewman != null)
                        trim.AssignedCrewman.CurrentTask = null;
                    crewWinchInstructions.Remove(trim.Sail.getSheetWinch());
                }
                else if (trim.Status == WorkRequestStatus.Positioning && trim.IsPositioningComplete())
                {
                    trim.Begin(trim.AssignedCrewman);
                }
            }

            TrimRequests.RemoveAll(r => r.Status == WorkRequestStatus.Complete);

            // Assign open requests to unoccupied deckhands.
            foreach (var req in WorkRequests)
            {
                if (req.Status != WorkRequestStatus.Open) continue;
                var crewman = Crew.FirstOrDefault(c => !c.IsOccupied && c.Role == ShipRole.Deckhand);
                if (crewman == null) break;
                crewman.CurrentTask = req;
                req.AssignedCrewman = crewman;
                req.BeginPositioning(crewman);
            }

            foreach (var trim in TrimRequests)
            {
                if (trim.Status != WorkRequestStatus.Open) continue;
                var crewman = Crew.FirstOrDefault(c => !c.IsOccupied && c.Role == ShipRole.Deckhand);
                if (crewman == null) break;
                crewman.CurrentTask = trim;
                trim.AssignedCrewman = crewman;
                trim.BeginPositioning(crewman);
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
                else if (jtrim.Status == WorkRequestStatus.Positioning && jtrim.IsPositioningComplete())
                {
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
                else if (strim.Status == WorkRequestStatus.Positioning && strim.IsPositioningComplete())
                {
                    strim.Begin();
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
