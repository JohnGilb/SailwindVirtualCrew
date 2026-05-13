using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SailwindVirtualCrew
{
    internal sealed class CrewNavigationCoordinator
    {
        private const string Phase = "RuntimeNav";
        private static readonly CrewNavigationCoordinator _instance = new CrewNavigationCoordinator();

        internal static CrewNavigationCoordinator Instance => _instance;

        private CrewBoatContext _context;
        private ProxyBoat _proxyBoat;
        private ProxyNavMeshNavigationProvider _navMeshProvider;
        private List<CrewStation> _stations = new List<CrewStation>();
        private readonly Dictionary<Crewman, RuntimeActor> _actorsByCrew = new Dictionary<Crewman, RuntimeActor>();
        private readonly Dictionary<object, RuntimeActor> _actorsByOwner = new Dictionary<object, RuntimeActor>();
        private readonly List<GameObject> _diagnosticMarkers = new List<GameObject>();
        private readonly System.Random _random = new System.Random();

        private float _lastBellRealTime      = float.MinValue;
        private float _lastLookoutStopReal   = float.MinValue;
        private bool  _landVisibleAtLastStop;
        private const float ShiftChangeWindow = 5f;

        private CrewNavigationCoordinator()
        {
        }

        private void TryRingLookoutBell()
        {
            if (UnityEngine.Time.realtimeSinceStartup - _lastBellRealTime < 60f) return;
            RingLookoutBell();
        }

        internal void ForceRingLookoutBell()
        {
            _lastBellRealTime = float.MinValue;
            RingLookoutBell();
        }

        internal IReadOnlyList<CrewStation> GetWorkstations()
        {
            EnsureRuntimeReady();
            return _stations.AsReadOnly();
        }

        internal void RebuildWorkstations()
        {
            _stations = new List<CrewStation>();
            EnsureRuntimeReady();
        }

        internal void ApplyCustomWorkstationLocation(CrewStation station, Vector3 localPosition, Quaternion localRotation)
        {
            if (station == null || string.IsNullOrEmpty(station.StableKey))
                return;

            if (EnsureRuntimeReady() && _navMeshProvider.TryGetWorldOnNavMeshQuiet(localPosition, 1.5f, out var navWorld))
                localPosition = _navMeshProvider.WorldToProxyLocal(navWorld);
            else
                CrewDebugLog.Warn(Phase, "Custom workstation location could not be sampled onto the NavMesh key='" + station.StableKey + "'.");

            VirtualCrewManager.Instance.SetCustomWorkstationLocation(station.StableKey, localPosition, localRotation);
            foreach (var cached in _stations.Where(s => s.StableKey == station.StableKey))
                ApplyCustomWorkstationLocation(cached, localPosition, localRotation, true);
        }

        internal void ClearCustomWorkstationLocation(CrewStation station)
        {
            if (station == null || string.IsNullOrEmpty(station.StableKey))
                return;

            VirtualCrewManager.Instance.ClearCustomWorkstationLocation(station.StableKey);
            RebuildWorkstations();
        }

        private void RingLookoutBell()
        {
            _lastBellRealTime = UnityEngine.Time.realtimeSinceStartup;
            CrewSoundPlayer.Instance?.Play("shipbell");
            if (GameState.sleeping && Sleep.instance != null)
                Sleep.instance.WakeUp();
        }

        internal void Tick()
        {
            EnsureRestActors();
            foreach (var actor in _actorsByCrew.Values.ToList())
                actor.Tick();
        }

        internal void OnRestLocationChanged(Crewman crewman)
        {
            if (crewman == null || !EnsureRuntimeReady())
                return;

            var actor = GetOrCreateActor(crewman);
            actor?.RefreshRestLocation();
        }

        internal bool TryProjectLocalToNavMesh(Vector3 localPosition, out Vector3 projectedLocal)
        {
            projectedLocal = localPosition;
            if (!EnsureRuntimeReady())
                return false;

            if (!_navMeshProvider.TryGetWorldOnNavMesh(localPosition, GetNavMeshSearchDistance(), out var worldPosition))
            {
                _navMeshProvider.DumpSampleDiagnostics(localPosition, GetNavMeshSearchDistance(), "project-local");
                return false;
            }

            projectedLocal = _navMeshProvider.WorldToProxyLocal(worldPosition);
            return true;
        }

        internal void DumpNavDiagnosticsAtPlayer()
        {
            if (!TryGetPlayerLocalPosition(out var localPosition))
                return;

            DumpRuntimeContext("player");
            ProxyBoatBuilder.LogProxy(_proxyBoat);
            ProxyBoatBuilder.LogProxyColliderDiagnostics(_proxyBoat, localPosition);
            ProxyBoatBuilder.LogMeshNormalDiagnostics(_proxyBoat, localPosition);
            _navMeshProvider.DumpSampleDiagnostics(localPosition, GetNavMeshSearchDistance(), "player");
        }

        internal void ShowNavDiagnosticsAtPlayer()
        {
            ClearNavDiagnostics();
            if (!TryGetPlayerLocalPosition(out var localPosition))
                return;

            CreateDiagnosticMarker("VC_RuntimeNav_PlayerLocal", localPosition, Color.red, 0.45f);

            if (_navMeshProvider.TryGetWorldOnNavMeshQuiet(localPosition, GetNavMeshSearchDistance(), out var hitWorld))
            {
                Vector3 hitLocal = _navMeshProvider.WorldToProxyLocal(hitWorld);
                CreateDiagnosticMarker("VC_RuntimeNav_SampledNavMesh", hitLocal, Color.green, 0.35f);
                CrewDebugLog.Ok(Phase, "Created nav diagnostic markers at player local and sampled NavMesh local.");
            }
            else if (_navMeshProvider.TryGetNearestNavMeshVertexLocal(localPosition, out var nearestLocal, out var distance))
            {
                CreateDiagnosticMarker("VC_RuntimeNav_NearestVertex", nearestLocal, Color.yellow, 0.35f);
                CrewDebugLog.Warn(Phase,
                    "Created nav diagnostic markers; SamplePosition failed, nearest vertex distance="
                    + distance.ToString("0.000"));
            }
            else
            {
                if (ProxyBoatBuilder.TryFindNearestColliderTop(_proxyBoat, localPosition, out var colliderTopLocal, out var description))
                {
                    CreateDiagnosticMarker("VC_RuntimeNav_NearestColliderTop", colliderTopLocal, Color.cyan, 0.35f);
                    CrewDebugLog.Warn(Phase, "Created player and nearest collider-top markers; " + description);
                }
                else
                {
                    CrewDebugLog.Warn(Phase, "Created player marker only; no proxy NavMesh vertices or collider tops found.");
                }
            }
        }

        internal void ClearNavDiagnostics()
        {
            foreach (var marker in _diagnosticMarkers)
                if (marker)
                    Object.Destroy(marker);

            _diagnosticMarkers.Clear();
        }

        internal bool TryBeginWinchPositioning(object owner, Crewman crewman, GPButtonRopeWinch winch)
        {
            if (owner == null || crewman == null || !winch)
                return false;

            if (_actorsByOwner.ContainsKey(owner))
                return true;

            if (!EnsureRuntimeReady())
                return false;

            var actor = GetOrCreateActor(crewman);
            if (actor == null)
            {
                CrewDebugLog.Warn(Phase, "No concrete actor available; falling back to simulated positioning.");
                return false;
            }

            if (actor.ActiveOwner != null)
            {
                CrewDebugLog.Warn(Phase, "Concrete actor is busy for crew='" + crewman.Name + "'; falling back to simulated positioning.");
                return false;
            }

            var station = FindStationForWinch(winch, actor);
            if (station == null)
            {
                CrewDebugLog.Warn(Phase, "No projected station found for winch='" + winch.name + "'; falling back to simulated positioning.");
                return false;
            }

            actor.Begin(owner, station, winch);
            _actorsByOwner[owner] = actor;
            return true;
        }

        internal void BeginPilot(PilotTask task)
        {
            if (task == null || task.AssignedCrewman == null || !EnsureRuntimeReady())
                return;

            var actor = GetOrCreateActor(task.AssignedCrewman);
            if (actor == null || actor.ActiveOwner != null)
                return;

            var station = FindClosestStation("helm", actor.CurrentLocalPosition);
            if (station == null)
            {
                CrewDebugLog.Warn(Phase, "No helm station found for pilot crew='" + task.AssignedCrewman.Name + "'.");
                return;
            }

            if (actor.BeginRole(task, station.ProjectedLocalStand, GetBowRotation(), "pilot helm='" + station.Id + "'"))
                _actorsByOwner[task] = actor;
        }

        internal void BeginLookout(LookoutTask task)
        {
            if (task == null || task.AssignedCrewman == null || !EnsureRuntimeReady())
                return;

            var actor = GetOrCreateActor(task.AssignedCrewman);
            if (actor == null || actor.ActiveOwner != null)
                return;

            Vector3 startLocal;
            Quaternion startRotation;
            if (!VirtualCrewManager.Instance.TryGetCrewRestLocation(task.AssignedCrewman, out startLocal, out startRotation))
            {
                startLocal = actor.CurrentLocalPosition;
                startRotation = actor.CurrentLocalRotation;
            }
            else if (TryProjectLocalToNavMesh(startLocal, out var projectedStartLocal))
            {
                startLocal = projectedStartLocal;
            }

            if (actor.BeginLookout(task, startLocal, startRotation, _random))
            {
                _actorsByOwner[task] = actor;
                bool isShiftChange = (UnityEngine.Time.realtimeSinceStartup - _lastLookoutStopReal) < ShiftChangeWindow;
                actor.SetLookoutSuppressFirst(isShiftChange && _landVisibleAtLastStop);
            }
        }

        internal void BeginSleep(SleepRequest request, Crewman crewman, Component bed)
        {
            if (request == null || crewman == null || !bed || !EnsureRuntimeReady())
                return;

            if (_actorsByOwner.ContainsKey(request))
                return;

            var actor = GetOrCreateActor(crewman);
            if (actor == null || actor.ActiveOwner != null)
                return;

            Vector3 bedWorld = GetBedSleepPosition(bed);
            Vector3 bedLocal = _context.WorldBoat.InverseTransformPoint(bedWorld);
            Quaternion bedRotation = Quaternion.Inverse(_context.WorldBoat.rotation) * bed.transform.rotation;

            if (!actor.BeginRole(request, bedLocal, bedRotation, "sleep bed='" + bed.name + "'", 1.5f, Vector3.up * 0.33f))
            {
                CrewDebugLog.Warn(Phase, "Bed off NavMesh, teleporting crew='" + crewman.Name + "' to bed='" + bed.name + "'");
                actor.TeleportToRole(request, bedLocal, bedRotation, Vector3.up * 0.33f);
            }
            _actorsByOwner[request] = actor;
        }

        private static Vector3 GetBedSleepPosition(Component bed)
        {
            if (bed is ShipItemBed shipBed && shipBed.sleepPos)
                return shipBed.sleepPos.position;
            if (bed is GPButtonBed buttonBed && buttonBed.sleepPos)
                return buttonBed.sleepPos.position;
            return bed.transform.position;
        }

        internal bool IsPositioningComplete(object owner)
        {
            return owner != null
                && _actorsByOwner.TryGetValue(owner, out var actor)
                && actor.IsPositioningComplete;
        }

        internal float GetPositioningProgress(object owner)
        {
            if (owner == null || !_actorsByOwner.TryGetValue(owner, out var actor))
                return 100f;

            return actor.GetPositioningProgress();
        }

        internal float EstimateDistanceToWinch(Crewman crewman, GPButtonRopeWinch winch)
        {
            if (crewman == null || !winch || !EnsureRuntimeReady())
                return float.MaxValue;

            Vector3 fromLocal;
            if (_actorsByCrew.TryGetValue(crewman, out var actor) && actor.IsValid)
                fromLocal = actor.CurrentLocalPosition;
            else if (VirtualCrewManager.Instance.TryGetCrewRestLocation(crewman, out var restLocal, out _))
            {
                if (TryProjectLocalToNavMesh(restLocal, out var projectedRestLocal))
                    restLocal = projectedRestLocal;
                fromLocal = restLocal;
            }
            else
                fromLocal = GetDefaultStartLocal();

            var station = _stations
                .Where(s => IsStationForWinch(s, winch))
                .OrderBy(s => Vector3.Distance(fromLocal, s.ProjectedLocalStand))
                .FirstOrDefault();

            return station == null ? float.MaxValue : Vector3.Distance(fromLocal, station.ProjectedLocalStand);
        }

        internal void Complete(object owner)
        {
            if (owner == null || !_actorsByOwner.TryGetValue(owner, out var actor))
                return;

            CrewDebugLog.Ok(Phase,
                "Concrete positioning complete crew='" + actor.Crew.Name
                + "' destination='" + (actor.ActiveStation != null ? actor.ActiveStation.Id : "role") + "'");
            actor.Complete();
            _actorsByOwner.Remove(owner);
        }

        internal void Cancel(object owner)
        {
            if (owner == null || !_actorsByOwner.TryGetValue(owner, out var actor))
                return;

            if (owner is LookoutTask)
            {
                _landVisibleAtLastStop = actor.LookoutSawLand;
                _lastLookoutStopReal   = UnityEngine.Time.realtimeSinceStartup;
            }

            actor.Cancel();
            _actorsByOwner.Remove(owner);
        }

        private bool EnsureRuntimeReady()
        {
            _context = CrewBoatContextResolver.Resolve();
            if (_context == null)
                return false;

            if (_proxyBoat == null || !_proxyBoat.IsValid)
                _proxyBoat = ProxyBoatBuilder.Create(_context);

            if (_navMeshProvider == null || !_navMeshProvider.IsBaked)
            {
                _navMeshProvider = new ProxyNavMeshNavigationProvider(_proxyBoat);
                if (!_navMeshProvider.Bake())
                    return false;
            }

            if (_stations == null || _stations.Count == 0)
                _stations = new CrewStationScanner(_context, _navMeshProvider).Scan();

            return true;
        }

        private bool TryGetPlayerLocalPosition(out Vector3 localPosition)
        {
            localPosition = Vector3.zero;
            if (!EnsureRuntimeReady())
                return false;

            if (Refs.observerMirror == null)
            {
                CrewDebugLog.Warn(Phase, "Cannot diagnose player NavMesh position; Refs.observerMirror is null.");
                return false;
            }

            localPosition = _context.WorldBoat.InverseTransformPoint(Refs.observerMirror.transform.position);
            return true;
        }

        private void DumpRuntimeContext(string label)
        {
            CrewDebugLog.Ok(Phase,
                "Runtime context label='" + label
                + "' worldBoat='" + GetPath(_context.WorldBoat)
                + "' walkCol='" + GetPath(_context.WalkCol)
                + "' playerControllerParent='" + GetPath(Refs.charController ? Refs.charController.transform.parent : null)
                + "' observerParent='" + GetPath(Refs.observerMirror ? Refs.observerMirror.transform.parent : null)
                + "' proxyRoot='" + (_proxyBoat != null && _proxyBoat.Root ? _proxyBoat.Root.name : "none") + "'");
        }

        private void CreateDiagnosticMarker(string name, Vector3 localPosition, Color color, float size)
        {
            if (_context == null || !_context.WorldBoat)
                return;

            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = name;
            marker.transform.SetParent(_context.WorldBoat, false);
            marker.transform.localPosition = localPosition;
            marker.transform.localRotation = Quaternion.identity;
            marker.transform.localScale = Vector3.one * size;

            var collider = marker.GetComponent<Collider>();
            if (collider)
                Object.Destroy(collider);

            var renderer = marker.GetComponent<Renderer>();
            if (renderer)
                renderer.material.color = color;

            _diagnosticMarkers.Add(marker);
        }

        private static string GetPath(Transform transform)
        {
            if (!transform)
                return "null";

            string path = transform.name;
            var current = transform.parent;
            while (current)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private static void ApplyCustomWorkstationLocation(CrewStation station, Vector3 localPosition, Quaternion localRotation, bool hasCustomLocation)
        {
            station.ProjectedLocalStand = localPosition;
            station.LocalRotation = localRotation;
            station.Projected = true;
            station.HasCustomLocation = hasCustomLocation;
        }

        private RuntimeActor GetOrCreateActor(Crewman crewman)
        {
            if (_actorsByCrew.TryGetValue(crewman, out var actor) && actor.IsValid)
                return actor;

            Vector3 startLocal = VirtualCrewManager.Instance.TryGetCrewRestLocation(crewman, out var restPosition, out _)
                ? restPosition
                : GetDefaultStartLocal();
            if (!_navMeshProvider.TryGetWorldOnNavMesh(startLocal, GetNavMeshSearchDistance(), out var startWorld))
                return null;

            actor = new RuntimeActor(crewman, _context, _navMeshProvider, startWorld);
            _actorsByCrew[crewman] = actor;
            CrewDebugLog.Ok(Phase, "Created concrete actor crew='" + crewman.Name + "' actors=" + _actorsByCrew.Count);
            return actor;
        }

        private void EnsureRestActors()
        {
            var mgr = VirtualCrewManager.Instance;
            bool anyRestCrew = false;
            foreach (var crewman in mgr.Crew)
            {
                if (mgr.TryGetCrewRestLocation(crewman, out _, out _))
                {
                    anyRestCrew = true;
                    break;
                }
            }

            if (!anyRestCrew)
                return;

            if (!EnsureRuntimeReady())
                return;

            var currentCrew = new HashSet<Crewman>(mgr.Crew);
            foreach (var pair in _actorsByCrew.ToList())
            {
                if (currentCrew.Contains(pair.Key))
                    continue;

                pair.Value.Destroy();
                _actorsByCrew.Remove(pair.Key);
            }

            foreach (var crewman in mgr.Crew)
            {
                if (mgr.TryGetCrewRestLocation(crewman, out _, out _))
                {
                    if (!_actorsByCrew.ContainsKey(crewman))
                        GetOrCreateActor(crewman);
                }
            }
        }

        private CrewStation FindStationForWinch(GPButtonRopeWinch winch, RuntimeActor actor)
        {
            return _stations
                .Where(s => IsStationForWinch(s, winch))
                .OrderBy(s => Vector3.Distance(actor.CurrentLocalPosition, s.ProjectedLocalStand))
                .FirstOrDefault();
        }

        private CrewStation FindClosestStation(string typeName, Vector3 fromLocal)
        {
            return _stations
                .Where(s => s.Projected && s.TypeName == typeName)
                .OrderBy(s => Vector3.Distance(fromLocal, s.ProjectedLocalStand))
                .FirstOrDefault();
        }

        private static Quaternion GetBowRotation()
        {
            return Quaternion.LookRotation(Vector3.right, Vector3.up);
        }

        private static bool IsStationForWinch(CrewStation station, GPButtonRopeWinch winch)
        {
            if (station == null || !station.Projected || !winch)
                return false;

            if (station.Control == winch)
                return true;

            var stationWinch = station.Control as GPButtonRopeWinch;
            return stationWinch && stationWinch.rope == winch.rope;
        }

        private Vector3 GetDefaultStartLocal()
        {
            if (_proxyBoat != null && _proxyBoat.IsValid)
                return _proxyBoat.Root.transform.InverseTransformPoint(_proxyBoat.Bounds.center);

            return Vector3.zero;
        }

        private float GetNavMeshSearchDistance()
        {
            if (_proxyBoat == null || !_proxyBoat.IsValid)
                return 12f;

            return Mathf.Max(12f, _proxyBoat.Bounds.size.y + 2f);
        }

        private static float DexterityToSpeed(int dexterity)
        {
            return 1.6f + (dexterity - 3) * 0.3f;
        }

        private static string SafeName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "crew";

            var chars = value.Where(char.IsLetterOrDigit).ToArray();
            return chars.Length == 0 ? "crew" : new string(chars);
        }

        private sealed class RuntimeActor
        {
            private readonly ProxyNavMeshNavigationProvider _navMeshProvider;
            private readonly CrewBoatContext _context;
            private readonly ProxyLogicAgent _logicAgent;
            private readonly CrewAgent _visualAgent;
            private readonly ProxyToBoatPoseSync _poseSync;
            private float _initialDistance;
            private bool _workingLogged;
            private bool _isTeleported;
            private string _activeLabel;
            private Quaternion _activeArrivalRotation;
            private bool _hasActiveArrivalRotation;
            private Vector3 _arrivalWorldOffset;
            private bool _lookoutActive;
            private Vector3 _lookoutStartLocal;
            private Quaternion _lookoutStartRotation;
            private System.Random _lookoutRandom;
            private float _nextLookoutDecisionTime;
            private bool _returningToRest;
            private Vector3 _restLocalPosition;
            private Vector3 _restStandLocalPosition;
            private Quaternion _restLocalRotation;
            private bool _hasRestLocation;
            private int  _lastAppliedDexterity    = -1;
            private bool _lookoutSawLand;
            private bool _suppressNextLandDetection;

            internal bool LookoutSawLand => _lookoutSawLand;
            internal void SetLookoutSuppressFirst(bool suppress) { _suppressNextLandDetection = suppress; }

            internal RuntimeActor(Crewman crew, CrewBoatContext context, ProxyNavMeshNavigationProvider navMeshProvider, Vector3 startWorld)
            {
                Crew = crew;
                _navMeshProvider = navMeshProvider;
                _context = context;

                string id = SafeName(crew.Name);
                _logicAgent = new ProxyLogicAgent(navMeshProvider.Proxy.Root.transform, startWorld, "VC_LogicAgent_" + id);
                _logicAgent.SetSpeed(DexterityToSpeed(crew.Dexterity));
                _visualAgent = CrewVisualFactory.SpawnTestCrewVisual(context, _logicAgent.CurrentLocalPosition, _logicAgent.CurrentLocalRotation, id, crew.ModelIndex);
                _poseSync = new ProxyToBoatPoseSync(_visualAgent, _logicAgent, context);
                RefreshRestLocation();
            }

            internal Crewman Crew { get; }
            internal object ActiveOwner { get; private set; }
            internal CrewStation ActiveStation { get; private set; }
            internal bool IsValid => _logicAgent != null && _logicAgent.IsValid && _visualAgent != null && _visualAgent.VisualRoot;
            internal bool IsPositioningComplete => _isTeleported || (ActiveOwner != null && _logicAgent.HasArrived);
            internal Vector3 CurrentLocalPosition => _logicAgent.CurrentLocalPosition;
            internal Quaternion CurrentLocalRotation => _logicAgent.CurrentLocalRotation;

            internal void Begin(object owner, CrewStation station, GPButtonRopeWinch winch)
            {
                ActiveOwner = owner;
                ActiveStation = station;
                _workingLogged = false;
                _activeLabel = "station='" + station.Id + "'";
                _activeArrivalRotation = station.LocalRotation;
                _hasActiveArrivalRotation = true;
                _lookoutActive = false;
                _returningToRest = false;
                _poseSync.ClearPoseOverride();
                _poseSync.ClearRotationOverride();

                var destinationWorld = _navMeshProvider.Proxy.Root.transform.TransformPoint(station.ProjectedLocalStand);
                _initialDistance = Mathf.Max(0.01f, Vector3.Distance(_logicAgent.CurrentLocalPosition, station.ProjectedLocalStand));

                CrewDebugLog.Ok(Phase,
                    "Concrete positioning started crew='" + Crew.Name
                    + "' station='" + station.Id
                    + "' winch='" + winch.name + "'");
                _logicAgent.SetDestination(destinationWorld, station.ProjectedLocalStand, teleportIfUnreachable: true, unreachableTeleportDelay: GetPositioningDelay());
            }

            internal bool BeginRole(object owner, Vector3 destinationLocal, Quaternion arrivalRotation, string label, float maxNavMeshDistance = 4f, Vector3 arrivalWorldOffset = default)
            {
                if (!_navMeshProvider.TryGetWorldOnNavMesh(destinationLocal, maxNavMeshDistance, out var destinationWorld))
                {
                    CrewDebugLog.Warn(Phase, "Could not project role destination crew='" + Crew.Name + "' " + label);
                    return false;
                }

                Vector3 projectedLocal = _navMeshProvider.WorldToProxyLocal(destinationWorld);
                ActiveOwner = owner;
                ActiveStation = null;
                _workingLogged = false;
                _activeLabel = label;
                _activeArrivalRotation = arrivalRotation;
                _hasActiveArrivalRotation = true;
                _arrivalWorldOffset = arrivalWorldOffset;
                _lookoutActive = false;
                _returningToRest = false;
                _poseSync.ClearPoseOverride();
                _poseSync.ClearRotationOverride();
                _initialDistance = Mathf.Max(0.01f, Vector3.Distance(_logicAgent.CurrentLocalPosition, projectedLocal));
                CrewDebugLog.Ok(Phase, "Role positioning started crew='" + Crew.Name + "' " + label);
                _logicAgent.SetDestination(destinationWorld, projectedLocal, teleportIfUnreachable: true, unreachableTeleportDelay: GetPositioningDelay());
                return true;
            }

            internal void TeleportToRole(object owner, Vector3 destinationLocal, Quaternion arrivalRotation, Vector3 arrivalWorldOffset)
            {
                ActiveOwner = owner;
                ActiveStation = null;
                _isTeleported = true;
                _workingLogged = true;
                _activeLabel = "teleport";
                _hasActiveArrivalRotation = true;
                _activeArrivalRotation = arrivalRotation;
                _arrivalWorldOffset = arrivalWorldOffset;
                _lookoutActive = false;
                _returningToRest = false;
                _poseSync.ClearPoseOverride();
                _poseSync.ClearRotationOverride();
                Vector3 localOffset = _context.WorldBoat.InverseTransformDirection(arrivalWorldOffset);
                _poseSync.SetPoseOverride(destinationLocal + localOffset, arrivalRotation);
                CrewDebugLog.Ok(Phase, "Teleported to role crew='" + Crew.Name + "' dest=" + destinationLocal);
            }

            internal bool BeginLookout(object owner, Vector3 startLocal, Quaternion startRotation, System.Random random)
            {
                _lookoutStartLocal = startLocal;
                _lookoutStartRotation = startRotation;
                _lookoutRandom = random;
                if (!BeginRole(owner, startLocal, startRotation, "lookout start"))
                    return false;

                _lookoutActive = true;
                _nextLookoutDecisionTime = 0f;
                return true;
            }

            internal void Tick()
            {
                int dex = Crew.Dexterity;
                if (dex != _lastAppliedDexterity)
                {
                    _logicAgent.SetSpeed(DexterityToSpeed(dex));
                    _lastAppliedDexterity = dex;
                }

                _logicAgent.Tick();

                if (ActiveOwner != null && _logicAgent.HasArrived && !_workingLogged)
                {
                    if (_hasActiveArrivalRotation)
                    {
                        if (_arrivalWorldOffset != Vector3.zero)
                        {
                            Vector3 localOffset = _context.WorldBoat.InverseTransformDirection(_arrivalWorldOffset);
                            _poseSync.SetPoseOverride(_logicAgent.CurrentLocalPosition + localOffset, _activeArrivalRotation);
                        }
                        else
                        {
                            _poseSync.SetRotationOverride(_activeArrivalRotation);
                        }
                    }
                    _workingLogged = true;
                    CrewDebugLog.Ok(Phase,
                        "Arrived crew='" + Crew.Name
                        + "' " + _activeLabel);
                }

                if (_lookoutActive && ActiveOwner != null)
                    TickLookout();

                if (ActiveOwner == null && !Crew.IsOccupied && _hasRestLocation)
                {
                    if (_returningToRest && _logicAgent.HasArrived)
                    {
                        _returningToRest = false;
                        _logicAgent.Stop();
                        _poseSync.SetPoseOverride(_logicAgent.CurrentLocalPosition, _restLocalRotation);
                    }
                    else if (!_returningToRest && LocalHorizontalDistance(_logicAgent.CurrentLocalPosition, _restStandLocalPosition) > 0.35f)
                    {
                        MoveToRest();
                    }
                }

                _poseSync.Tick();
            }

            internal float GetPositioningProgress()
            {
                if (ActiveOwner == null)
                    return 0f;

                if (_logicAgent.HasPendingUnreachableTeleport)
                    return _logicAgent.GetPendingUnreachableTeleportProgress();

                float distance = _logicAgent.HasDestination
                    ? Vector3.Distance(_logicAgent.CurrentLocalPosition, _logicAgent.LastDestinationLocal)
                    : 0f;
                return Mathf.Clamp01(distance / _initialDistance) * 100f;
            }

            internal void Complete()
            {
                ActiveOwner = null;
                ActiveStation = null;
                _initialDistance = 0f;
                _workingLogged = false;
                _isTeleported = false;
                _activeLabel = null;
                _hasActiveArrivalRotation = false;
                _arrivalWorldOffset = Vector3.zero;
                _lookoutActive = false;
                _lookoutSawLand = false;
                _suppressNextLandDetection = false;
            }

            internal void Cancel()
            {
                _logicAgent.Stop();
                _poseSync.ClearPoseOverride();
                _poseSync.ClearRotationOverride();
                CrewDebugLog.Ok(Phase, "Concrete positioning cancelled crew='" + Crew.Name + "'");
                Complete();
            }

            private void TickLookout()
            {
                if (Time.time < _nextLookoutDecisionTime)
                    return;

                _nextLookoutDecisionTime = Time.time + 4f;

                bool landVisible = TryGetVisibleLand(out var islandPosition);

                if (landVisible && !_lookoutSawLand)
                {
                    if (_suppressNextLandDetection)
                        _suppressNextLandDetection = false;
                    else
                        CrewNavigationCoordinator.Instance.TryRingLookoutBell();
                }
                _lookoutSawLand = landVisible;

                if (landVisible)
                {
                    Quaternion facing = GetLocalLookRotationTowardWorld(islandPosition);
                    _activeArrivalRotation = facing;
                    _hasActiveArrivalRotation = true;

                    if (LocalHorizontalDistance(_logicAgent.CurrentLocalPosition, _lookoutStartLocal) > 0.5f
                        && (!_logicAgent.HasActiveDestination || LocalHorizontalDistance(_logicAgent.LastDestinationLocal, _lookoutStartLocal) > 0.35f))
                    {
                        _workingLogged = false;
                        if (BeginRole(ActiveOwner, _lookoutStartLocal, facing, "lookout visible land"))
                            _lookoutActive = true;
                    }
                    else if (!_logicAgent.HasActiveDestination)
                    {
                        _poseSync.SetRotationOverride(facing);
                    }

                    return;
                }

                if (_logicAgent.HasActiveDestination)
                    return;

                if (TryGetRandomDeckLocal(out var randomLocal))
                {
                    _workingLogged = false;
                    if (BeginRole(ActiveOwner, randomLocal, _logicAgent.CurrentLocalRotation, "lookout patrol"))
                        _lookoutActive = true;
                }
            }

            private bool TryGetVisibleLand(out Vector3 islandPosition)
            {
                islandPosition = Vector3.zero;
                var tracker = IslandDistanceTracker.instance;
                if (tracker == null || tracker.islands == null || tracker.islands.Count == 0)
                    return false;

                Vector3 from = _context.WorldBoat.TransformPoint(_lookoutStartLocal);
                foreach (var item in tracker.islands
                    .Where(i => i != null)
                    .Select(i => new { Island = i, Distance = Vector3.Distance(i.GetPosition(), from) })
                    .OrderBy(x => x.Distance)
                    .Take(8))
                {
                    if (IsLandVisible(item.Island, item.Distance))
                    {
                        islandPosition = item.Island.GetPosition();
                        return true;
                    }
                }

                return false;
            }

            private bool IsLandVisible(IslandHorizon island, float distance)
            {
                if (island == null || distance <= 0f)
                    return false;

                float peak = GetPeakAboveRoot(island);
                if (peak <= 0f)
                    return false;

                float currentDrop = GetInitialHeight(island) - island.transform.localPosition.y;
                float visibleHeight = peak - currentDrop;
                float angleDeg = Mathf.Atan2(visibleHeight, distance) * Mathf.Rad2Deg;
                float threshold = 1f - Crew.Wisdom * 0.1f;
                if (IsNightwatch()) threshold *= 5f;
                return angleDeg >= threshold;
            }

            private static bool IsNightwatch()
            {
                float t = Sun.sun.localTime;
                return t >= 20f || t < 4f;
            }

            private float GetPeakAboveRoot(IslandHorizon island)
            {
                float maxY = ScanMaxWorldY(island);
                return maxY == float.MinValue ? 0f : maxY - island.transform.position.y;
            }

            private float ScanMaxWorldY(IslandHorizon island)
            {
                float maxY = float.MinValue;

                foreach (var renderer in island.GetComponentsInChildren<Renderer>())
                {
                    if (renderer.bounds.min.y < 250f && renderer.bounds.max.y > maxY)
                        maxY = renderer.bounds.max.y;
                }

                if (island.islandIndex > 0 && island.SceneLoaded())
                {
                    var scene = SceneManager.GetSceneByBuildIndex(island.islandIndex);
                    if (scene.isLoaded)
                    {
                        foreach (var root in scene.GetRootGameObjects())
                        {
                            foreach (var renderer in root.GetComponentsInChildren<Renderer>())
                            {
                                if (renderer.bounds.min.y < 250f && renderer.bounds.max.y > maxY)
                                    maxY = renderer.bounds.max.y;
                            }
                        }
                    }
                }

                return maxY;
            }

            private static float GetInitialHeight(IslandHorizon island)
            {
                try { return Traverse.Create(island).Field("initialHeight").GetValue<float>(); }
                catch { return 0f; }
            }

            private Quaternion GetLocalLookRotationTowardWorld(Vector3 worldPosition)
            {
                Vector3 localTarget = _context.WorldBoat.InverseTransformPoint(worldPosition);
                Vector3 direction = localTarget - _logicAgent.CurrentLocalPosition;
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.001f)
                    direction = Vector3.left;

                return Quaternion.LookRotation(direction.normalized, Vector3.up);
            }

            private bool TryGetRandomDeckLocal(out Vector3 localPosition)
            {
                localPosition = _lookoutStartLocal;
                Bounds bounds = _navMeshProvider.Proxy.Bounds;
                Transform proxyRoot = _navMeshProvider.Proxy.Root.transform;

                for (int i = 0; i < 12; i++)
                {
                    float x = Mathf.Lerp(bounds.min.x, bounds.max.x, (float)_lookoutRandom.NextDouble());
                    float z = Mathf.Lerp(bounds.min.z, bounds.max.z, (float)_lookoutRandom.NextDouble());
                    Vector3 world = new Vector3(x, bounds.center.y, z);
                    Vector3 candidateLocal = proxyRoot.InverseTransformPoint(world);

                    if (_navMeshProvider.TryGetWorldOnNavMeshQuiet(candidateLocal, 20f, out var hitWorld))
                    {
                        localPosition = _navMeshProvider.WorldToProxyLocal(hitWorld);
                        if (LocalHorizontalDistance(localPosition, _logicAgent.CurrentLocalPosition) > 2f)
                            return true;
                    }
                }

                return false;
            }

            internal void RefreshRestLocation()
            {
                _hasRestLocation = VirtualCrewManager.Instance.TryGetCrewRestLocation(Crew, out _restLocalPosition, out _restLocalRotation);
                if (!_hasRestLocation)
                    return;

                _restStandLocalPosition = GetRestStandLocalPosition();

                if (ActiveOwner == null && !Crew.IsOccupied)
                {
                    if (LocalHorizontalDistance(_logicAgent.CurrentLocalPosition, _restStandLocalPosition) <= 0.35f)
                    {
                        _logicAgent.Stop();
                        _poseSync.SetPoseOverride(_logicAgent.CurrentLocalPosition, _restLocalRotation);
                    }
                    else
                        MoveToRest();
                }
            }

            internal void Destroy()
            {
                _logicAgent.Destroy();
                _visualAgent.Destroy();
            }

            private void MoveToRest()
            {
                if (!_hasRestLocation || !_navMeshProvider.TryGetWorldOnNavMesh(_restLocalPosition, GetNavMeshSearchDistance(), out var restWorld))
                    return;

                _restStandLocalPosition = _navMeshProvider.WorldToProxyLocal(restWorld);
                _returningToRest = true;
                _poseSync.ClearPoseOverride();
                _poseSync.ClearRotationOverride();
                _logicAgent.SetDestination(restWorld, _restStandLocalPosition, teleportIfUnreachable: true, unreachableTeleportDelay: GetPositioningDelay());
                CrewDebugLog.Ok(Phase, "Returning crew='" + Crew.Name + "' to rest location.");
            }

            private Vector3 GetRestStandLocalPosition()
            {
                if (_navMeshProvider.TryGetWorldOnNavMeshQuiet(_restLocalPosition, GetNavMeshSearchDistance(), out var restWorld))
                    return _navMeshProvider.WorldToProxyLocal(restWorld);

                return _restLocalPosition;
            }

            private float GetNavMeshSearchDistance()
            {
                var proxy = _navMeshProvider.Proxy;
                if (proxy == null || !proxy.IsValid)
                    return 12f;

                return Mathf.Max(12f, proxy.Bounds.size.y + 2f);
            }

            private float GetPositioningDelay()
            {
                return Mathf.Max(0f, 7f - Crew.Dexterity);
            }

            private static float LocalHorizontalDistance(Vector3 a, Vector3 b)
            {
                a.y = 0f;
                b.y = 0f;
                return Vector3.Distance(a, b);
            }
        }
    }
}
