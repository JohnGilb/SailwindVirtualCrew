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
        private const float CrowsNestVerticalThreshold = 3f;

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

        internal void ResetLookoutBellCooldown()
        {
            _lastBellRealTime = float.MinValue;
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

        internal bool SetLookoutStationAtPlayer()
        {
            if (!EnsureRuntimeReady() || Refs.observerMirror == null)
                return false;

            Transform player = Refs.observerMirror.transform;
            Vector3 localPosition = _context.WorldBoat.InverseTransformPoint(player.position);
            Vector3 localForward = _context.WorldBoat.InverseTransformDirection(player.forward);
            localForward.y = 0f;
            if (localForward.sqrMagnitude < 0.001f)
                localForward = Vector3.forward;

            Quaternion localRotation = Quaternion.LookRotation(localForward.normalized, Vector3.up);
            if (!_navMeshProvider.TryGetWorldOnNavMeshQuiet(localPosition, GetNavMeshSearchDistance(), out var approachWorld))
            {
                CrewDebugLog.Warn(Phase, "Could not project lookout station to navmesh.");
                return false;
            }

            Vector3 approachLocal = _navMeshProvider.WorldToProxyLocal(approachWorld);
            bool isCrowsNest = localPosition.y - approachLocal.y >= CrowsNestVerticalThreshold;
            Vector3 stationLocal = isCrowsNest ? localPosition : approachLocal;
            VirtualCrewManager.Instance.SetLookoutStation(stationLocal, localRotation, isCrowsNest, approachLocal);
            CrewDebugLog.Ok(Phase,
                "Set lookout station local=" + Format(stationLocal)
                + " approach=" + Format(approachLocal)
                + " crowsNest=" + isCrowsNest);
            return true;
        }

        internal void ClearLookoutStation()
        {
            VirtualCrewManager.Instance.ClearLookoutStation();
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

        internal void OnSleepCompleted(Crewman crewman)
        {
            if (crewman == null || !EnsureRuntimeReady())
                return;

            var actor = GetOrCreateActor(crewman);
            actor?.StartReturnToRest();
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
            LookoutStationSaveData lookoutStation = null;
            if (!VirtualCrewManager.Instance.TryGetCrewRestLocation(task.AssignedCrewman, out startLocal, out startRotation))
            {
                startLocal = actor.CurrentLocalPosition;
                startRotation = actor.CurrentLocalRotation;
            }
            else if (TryProjectLocalToNavMesh(startLocal, out var projectedStartLocal))
            {
                startLocal = projectedStartLocal;
            }

            if (VirtualCrewManager.Instance.TryGetLookoutStation(out var savedStation))
            {
                lookoutStation = savedStation;
                startLocal = ToVector3(savedStation.isCrowsNest ? savedStation.approachLocalPosition : savedStation.localPosition);
                startRotation = ToQuaternion(savedStation.localEulerAngles);
            }

            if (actor.BeginLookout(task, startLocal, startRotation, _random, lookoutStation))
            {
                _actorsByOwner[task] = actor;
                bool isShiftChange = (UnityEngine.Time.realtimeSinceStartup - _lastLookoutStopReal) < ShiftChangeWindow;
                actor.SetLookoutSuppressFirst(task.SuppressFirstLandBell || (isShiftChange && _landVisibleAtLastStop));
            }
        }

        internal bool TryBeginRolePositioning(object owner, Crewman crewman, Vector3 destinationLocal, Quaternion arrivalRotation, string label)
        {
            if (owner == null || crewman == null)
                return false;

            if (_actorsByOwner.ContainsKey(owner))
                return true;

            if (!EnsureRuntimeReady())
                return false;

            var actor = GetOrCreateActor(crewman);
            if (actor == null || actor.ActiveOwner != null)
            {
                CrewDebugLog.Warn(Phase, "Could not start role positioning crew='" + crewman.Name + "' label='" + label + "'");
                return false;
            }

            if (!actor.BeginRole(owner, destinationLocal, arrivalRotation, label))
                return false;

            _actorsByOwner[owner] = actor;
            return true;
        }

        internal bool TryRetargetRolePositioning(object owner, Vector3 destinationLocal, Quaternion arrivalRotation, string label)
        {
            if (owner == null || !_actorsByOwner.TryGetValue(owner, out var actor))
                return false;

            return actor.BeginRole(owner, destinationLocal, arrivalRotation, label);
        }

        internal bool BeginSleep(SleepRequest request, Crewman crewman, Component bed)
        {
            if (request == null || crewman == null || !bed || !EnsureRuntimeReady())
                return false;

            if (_actorsByOwner.ContainsKey(request))
                return true;

            var actor = GetOrCreateActor(crewman);
            if (actor == null || actor.ActiveOwner != null)
            {
                CrewDebugLog.Warn(Phase, "Could not start sleep positioning crew='" + crewman.Name + "' bed='" + bed.name + "'");
                return false;
            }

            Vector3 bedWorld = GetBedSleepPosition(bed);
            Vector3 bedLocal = _context.WorldBoat.InverseTransformPoint(bedWorld);
            Quaternion bedRotation = Quaternion.Inverse(_context.WorldBoat.rotation) * bed.transform.rotation;

            if (!actor.BeginRole(request, bedLocal, bedRotation, "sleep bed='" + bed.name + "'", 1.5f, Vector3.up * 0.33f))
            {
                CrewDebugLog.Warn(Phase, "Bed off NavMesh, teleporting crew='" + crewman.Name + "' to bed='" + bed.name + "'");
                actor.TeleportToRole(request, bedLocal, bedRotation, Vector3.up * 0.33f);
            }
            _actorsByOwner[request] = actor;
            return true;
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

        internal bool TryGetLookoutEyeWorldPosition(Crewman crewman, out Vector3 eyeWorldPosition)
        {
            eyeWorldPosition = Vector3.zero;
            if (crewman == null)
                return false;

            return _actorsByCrew.TryGetValue(crewman, out var actor)
                && actor.IsValid
                && actor.TryGetLookoutEyeWorldPosition(out eyeWorldPosition);
        }

        internal float EstimateDistanceToWinch(Crewman crewman, GPButtonRopeWinch winch)
        {
            if (crewman == null || !winch || !EnsureRuntimeReady())
                return float.MaxValue;

            Vector3 fromLocal = GetCrewReferenceLocal(crewman);

            var station = _stations
                .Where(s => IsStationForWinch(s, winch))
                .OrderBy(s => Vector3.Distance(fromLocal, s.ProjectedLocalStand))
                .FirstOrDefault();

            return station == null ? float.MaxValue : Vector3.Distance(fromLocal, station.ProjectedLocalStand);
        }

        internal float EstimateDistanceToLocalPosition(Crewman crewman, Vector3 destinationLocal, float maxNavMeshDistance = 4f)
        {
            if (crewman == null || !EnsureRuntimeReady())
                return float.MaxValue;

            Vector3 fromLocal = GetCrewReferenceLocal(crewman);
            if (_navMeshProvider.TryGetWorldOnNavMeshQuiet(destinationLocal, maxNavMeshDistance, out var destinationWorld))
                destinationLocal = _navMeshProvider.WorldToProxyLocal(destinationWorld);

            return Vector3.Distance(fromLocal, destinationLocal);
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

        internal bool TrySetPoseOverrideWorld(object owner, Vector3 worldPosition, Quaternion worldRotation)
        {
            if (owner == null || !_actorsByOwner.TryGetValue(owner, out var actor))
                return false;

            actor.SetPoseOverrideWorld(worldPosition, worldRotation);
            return true;
        }

        internal bool TryGetOwnerWorldPose(object owner, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            worldPosition = Vector3.zero;
            worldRotation = Quaternion.identity;
            if (owner == null || !_actorsByOwner.TryGetValue(owner, out var actor))
                return false;

            actor.GetWorldPose(out worldPosition, out worldRotation);
            return true;
        }

        internal bool TryStopOwnerMotion(object owner)
        {
            if (owner == null || !_actorsByOwner.TryGetValue(owner, out var actor))
                return false;

            actor.StopMotion();
            return true;
        }

        internal void CancelAllActiveTasks()
        {
            foreach (object owner in _actorsByOwner.Keys.ToList())
                Cancel(owner);

            foreach (var actor in _actorsByCrew.Values.Where(a => a.ActiveOwner != null).ToList())
                actor.Cancel();

            _actorsByOwner.Clear();
        }

        private bool EnsureRuntimeReady()
        {
            var context = CrewBoatContextResolver.Resolve();
            if (context == null)
                return false;

            if (_context != null
                && (_context.TopBoat != context.TopBoat || _context.WorldBoat != context.WorldBoat || _context.WalkCol != context.WalkCol))
            {
                CancelAllActiveTasks();
                foreach (var actor in _actorsByCrew.Values.ToList())
                    actor.Destroy();
                _actorsByCrew.Clear();
                _actorsByOwner.Clear();
                _proxyBoat = null;
                _navMeshProvider = null;
                _stations = new List<CrewStation>();
            }

            _context = context;

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

        private Vector3 GetCrewReferenceLocal(Crewman crewman)
        {
            if (crewman != null && _actorsByCrew.TryGetValue(crewman, out var actor) && actor.IsValid)
                return actor.CurrentLocalPosition;

            if (crewman != null && VirtualCrewManager.Instance.TryGetCrewRestLocation(crewman, out var restLocal, out _))
            {
                if (_navMeshProvider != null && _navMeshProvider.TryGetWorldOnNavMeshQuiet(restLocal, GetNavMeshSearchDistance(), out var restWorld))
                    return _navMeshProvider.WorldToProxyLocal(restWorld);

                return restLocal;
            }

            return GetDefaultStartLocal();
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

        private static Vector3 ToVector3(float[] values)
        {
            return values != null && values.Length >= 3
                ? new Vector3(values[0], values[1], values[2])
                : Vector3.zero;
        }

        private static Quaternion ToQuaternion(float[] euler)
        {
            return euler != null && euler.Length >= 3
                ? Quaternion.Euler(euler[0], euler[1], euler[2])
                : Quaternion.identity;
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
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
            private bool _lookoutHasStation;
            private bool _lookoutStationIsCrowsNest;
            private bool _lookoutAtCrowsNest;
            private Vector3 _lookoutStationLocal;
            private Vector3 _lookoutStationApproachLocal;
            private Quaternion _lookoutStationRotation;
            private float _nextLookoutStationScanTurnTime;
            private bool _hasLookoutScanRotation;
            private Quaternion _lookoutScanRotation;
            private Quaternion _lookoutScanTargetRotation;
            private bool _crowsNestAscentActive;
            private bool _crowsNestDescentActive;
            private Vector3 _crowsNestLerpFromLocal;
            private Vector3 _crowsNestLerpToLocal;
            private Quaternion _crowsNestLerpFromRotation;
            private Quaternion _crowsNestLerpToRotation;
            private float _crowsNestLerpStartTime;
            private float _crowsNestLerpDuration;
            private bool _returningToRest;
            private Vector3 _restLocalPosition;
            private Vector3 _restStandLocalPosition;
            private Quaternion _restLocalRotation;
            private bool _hasRestLocation;
            private int  _lastAppliedDexterity    = -1;
            private bool _lookoutSawLand;
            private bool _suppressNextLandDetection;
            private float _lastLookoutCertaintyGameHour;
            private bool _hasLastLookoutCertaintyGameHour;

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

            internal void SetPoseOverrideWorld(Vector3 worldPosition, Quaternion worldRotation)
            {
                Vector3 localPosition = _context.WorldBoat.InverseTransformPoint(worldPosition);
                Quaternion localRotation = Quaternion.Inverse(_context.WorldBoat.rotation) * worldRotation;
                _poseSync.SetPoseOverride(localPosition, localRotation);
            }

            internal void GetWorldPose(out Vector3 worldPosition, out Quaternion worldRotation)
            {
                worldPosition = _context.WorldBoat.TransformPoint(_logicAgent.CurrentLocalPosition);
                worldRotation = _context.WorldBoat.rotation * _logicAgent.CurrentLocalRotation;
            }

            internal void StopMotion()
            {
                _logicAgent.Stop();
            }

            internal bool BeginLookout(object owner, Vector3 startLocal, Quaternion startRotation, System.Random random, LookoutStationSaveData station)
            {
                _lookoutStartLocal = startLocal;
                _lookoutStartRotation = startRotation;
                _lookoutRandom = random;
                ConfigureLookoutStation(station);
                if (!BeginRole(owner, startLocal, startRotation, "lookout start"))
                    return false;

                _lookoutActive = true;
                _nextLookoutDecisionTime = 0f;
                _nextLookoutStationScanTurnTime = 0f;
                _hasLookoutScanRotation = false;
                _lastLookoutCertaintyGameHour = GetCurrentGameHour();
                _hasLastLookoutCertaintyGameHour = true;
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

                if (TickCrowsNestTransition())
                {
                    _poseSync.Tick();
                    return;
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
                bool shouldDescendFromCrowsNest = _lookoutStationIsCrowsNest
                    && (_lookoutAtCrowsNest || _crowsNestAscentActive)
                    && _visualAgent != null
                    && _visualAgent.VisualRoot;
                if (shouldDescendFromCrowsNest)
                    BeginCrowsNestDescent();
                else
                    ResetLookoutStationState();
                _hasLastLookoutCertaintyGameHour = false;
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

                    if (_lookoutHasStation)
                    {
                        ApplyStationLookRotation(facing);
                        return;
                    }

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

                if (_lookoutHasStation)
                {
                    TickStationScanTurn();
                    return;
                }

                if (TryGetRandomDeckLocal(out var randomLocal))
                {
                    _workingLogged = false;
                    if (BeginRole(ActiveOwner, randomLocal, _logicAgent.CurrentLocalRotation, "lookout patrol"))
                        _lookoutActive = true;
                }
            }

            private void ConfigureLookoutStation(LookoutStationSaveData station)
            {
                ResetLookoutStationState();
                if (station == null)
                    return;

                _lookoutHasStation = true;
                _lookoutStationIsCrowsNest = station.isCrowsNest;
                _lookoutStationLocal = ToVector3(station.localPosition);
                _lookoutStationApproachLocal = station.isCrowsNest
                    ? ToVector3(station.approachLocalPosition)
                    : _lookoutStationLocal;
                _lookoutStationRotation = ToQuaternion(station.localEulerAngles);
                _lookoutStartLocal = station.isCrowsNest ? _lookoutStationApproachLocal : _lookoutStationLocal;
                _lookoutStartRotation = _lookoutStationRotation;
                _activeArrivalRotation = _lookoutStationRotation;
                _hasActiveArrivalRotation = true;
            }

            private void ResetLookoutStationState()
            {
                _lookoutHasStation = false;
                _lookoutStationIsCrowsNest = false;
                _lookoutAtCrowsNest = false;
                _crowsNestAscentActive = false;
                _crowsNestDescentActive = false;
                _hasLookoutScanRotation = false;
                _nextLookoutStationScanTurnTime = 0f;
            }

            private bool TickCrowsNestTransition()
            {
                if (_crowsNestDescentActive)
                {
                    UpdateCrowsNestLerp();
                    return true;
                }

                if (!_lookoutActive || !_lookoutStationIsCrowsNest || ActiveOwner == null)
                    return false;

                if (!_lookoutAtCrowsNest && !_crowsNestAscentActive && _logicAgent.HasArrived)
                    BeginCrowsNestAscent();

                if (_crowsNestAscentActive)
                {
                    UpdateCrowsNestLerp();
                    return true;
                }

                return false;
            }

            private void BeginCrowsNestAscent()
            {
                BeginCrowsNestLerp(
                    GetVisualLocalPosition(),
                    _lookoutStationLocal,
                    _lookoutStationRotation,
                    _lookoutStationRotation,
                    ascending: true);
            }

            private void BeginCrowsNestDescent()
            {
                BeginCrowsNestLerp(
                    GetVisualLocalPosition(),
                    _lookoutStationApproachLocal,
                    GetVisualLocalRotation(),
                    _lookoutStationRotation,
                    ascending: false);
            }

            private void BeginCrowsNestLerp(Vector3 fromLocal, Vector3 toLocal, Quaternion fromRotation, Quaternion toRotation, bool ascending)
            {
                _logicAgent.Stop();
                _crowsNestLerpFromLocal = fromLocal;
                _crowsNestLerpToLocal = toLocal;
                _crowsNestLerpFromRotation = fromRotation;
                _crowsNestLerpToRotation = toRotation;
                _crowsNestLerpStartTime = Time.time;
                _crowsNestLerpDuration = Mathf.Max(0.1f, Vector3.Distance(fromLocal, toLocal) / GetCrowsNestClimbSpeed());
                _crowsNestAscentActive = ascending;
                _crowsNestDescentActive = !ascending;
            }

            private void UpdateCrowsNestLerp()
            {
                float t = Mathf.Clamp01((Time.time - _crowsNestLerpStartTime) / _crowsNestLerpDuration);
                t = t * t * (3f - 2f * t);
                Vector3 localPosition = Vector3.Lerp(_crowsNestLerpFromLocal, _crowsNestLerpToLocal, t);
                Quaternion localRotation = Quaternion.Slerp(_crowsNestLerpFromRotation, _crowsNestLerpToRotation, t);
                _poseSync.SetPoseOverride(localPosition, localRotation);

                if (t < 1f)
                    return;

                if (_crowsNestAscentActive)
                {
                    _lookoutAtCrowsNest = true;
                    _hasLookoutScanRotation = false;
                }
                else
                {
                    _lookoutAtCrowsNest = false;
                    _poseSync.ClearPoseOverride();
                    _poseSync.ClearRotationOverride();
                }

                _crowsNestAscentActive = false;
                _crowsNestDescentActive = false;
            }

            private void TickStationScanTurn()
            {
                if (!_hasLookoutScanRotation)
                {
                    _lookoutScanRotation = GetVisualLocalRotation();
                    _lookoutScanTargetRotation = _lookoutScanRotation;
                    _hasLookoutScanRotation = true;
                }

                if (Time.time >= _nextLookoutStationScanTurnTime)
                {
                    _nextLookoutStationScanTurnTime = Time.time + 10f;
                    float yaw = (float)_lookoutRandom.NextDouble() * 360f;
                    _lookoutScanTargetRotation = Quaternion.Euler(0f, yaw, 0f);
                }

                _lookoutScanRotation = Quaternion.Slerp(_lookoutScanRotation, _lookoutScanTargetRotation, Time.deltaTime * 1.5f);
                ApplyStationLookRotation(_lookoutScanRotation);
            }

            private void ApplyStationLookRotation(Quaternion localRotation)
            {
                _activeArrivalRotation = localRotation;
                _hasActiveArrivalRotation = true;

                if (_lookoutStationIsCrowsNest && _lookoutAtCrowsNest)
                    _poseSync.SetPoseOverride(_lookoutStationLocal, localRotation);
                else
                    _poseSync.SetRotationOverride(localRotation);
            }

            private Vector3 GetVisualLocalPosition()
            {
                return _visualAgent != null && _visualAgent.VisualRoot
                    ? _visualAgent.VisualRoot.transform.localPosition
                    : _logicAgent.CurrentLocalPosition;
            }

            private Quaternion GetVisualLocalRotation()
            {
                return _visualAgent != null && _visualAgent.VisualRoot
                    ? _visualAgent.VisualRoot.transform.localRotation
                    : _logicAgent.CurrentLocalRotation;
            }

            private float GetCrowsNestClimbSpeed()
            {
                return Mathf.Max(0.8f, DexterityToSpeed(Crew.Dexterity) * 0.65f);
            }

            private bool TryGetVisibleLand(out Vector3 islandPosition)
            {
                islandPosition = Vector3.zero;
                var tracker = IslandDistanceTracker.instance;
                if (tracker == null || tracker.islands == null || tracker.islands.Count == 0)
                    return false;

                Vector3 from = GetLookoutEyeWorldPosition();
                float deltaGameHours = GetLookoutDeltaGameHours();
                float zoom = VirtualCrewManager.Instance.GetLookoutSpyglassZoom();

                var nearby = tracker.islands
                    .Where(i => i != null)
                    .Select(i => new { Island = i, Distance = Vector3.Distance(i.GetPosition(), from) })
                    .OrderBy(x => x.Distance)
                    .Take(8)
                    .ToList();

                foreach (var item in nearby)
                {
                    bool clearView = IsLandVisible(item.Island, item.Distance, from, zoom);
                    UpdateLookoutCertainty(item.Island, clearView, deltaGameHours);
                }

                foreach (var item in nearby)
                {
                    if (GetLookoutCertainty(item.Island) >= 1f)
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

                float zoom = VirtualCrewManager.Instance.GetLookoutSpyglassZoom();
                return LookoutVisibility.TryEvaluate(island, GetLookoutEyeWorldPosition(), Crew, zoom, out var result)
                    && result.IsVisible;
            }

            private bool IsLandVisible(IslandHorizon island, float distance, Vector3 observerWorld, float zoom)
            {
                if (island == null || distance <= 0f)
                    return false;

                return LookoutVisibility.TryEvaluate(island, observerWorld, Crew, zoom, out var result)
                    && result.IsVisible;
            }

            internal float GetLookoutCertainty(IslandHorizon island)
            {
                return VirtualCrewManager.Instance.GetLookoutCertainty(island);
            }

            private void UpdateLookoutCertainty(IslandHorizon island, bool clearView, float deltaGameHours)
            {
                if (island == null || deltaGameHours <= 0f)
                    return;

                float certainty = VirtualCrewManager.Instance.GetLookoutCertainty(island);
                float delta = clearView
                    ? deltaGameHours * (Crew.Dexterity / 3f)
                    : -deltaGameHours;

                VirtualCrewManager.Instance.SetLookoutCertainty(island, certainty + delta);
            }

            private float GetLookoutDeltaGameHours()
            {
                float now = GetCurrentGameHour();
                if (!_hasLastLookoutCertaintyGameHour)
                {
                    _lastLookoutCertaintyGameHour = now;
                    _hasLastLookoutCertaintyGameHour = true;
                    return 0f;
                }

                float delta = now - _lastLookoutCertaintyGameHour;
                if (delta < 0f)
                    delta += 24f;

                _lastLookoutCertaintyGameHour = now;
                return Mathf.Clamp(delta, 0f, 24f);
            }

            private static float GetCurrentGameHour()
            {
                return Sun.sun != null
                    ? Sun.sun.globalTime
                    : Time.time / 3600f;
            }

            private Vector3 GetLookoutEyeWorldPosition()
            {
                if (TryGetLookoutEyeWorldPosition(out var eyeWorldPosition))
                    return eyeWorldPosition;

                return _context.WorldBoat.TransformPoint(_lookoutStartLocal + Vector3.up * 1.7f);
            }

            internal bool TryGetLookoutEyeWorldPosition(out Vector3 eyeWorldPosition)
            {
                eyeWorldPosition = Vector3.zero;
                if (_visualAgent == null || !_visualAgent.VisualRoot)
                    return false;

                Transform root = _visualAgent.VisualRoot.transform;
                float topY = root.position.y + 1.7f;
                foreach (var renderer in _visualAgent.VisualRoot.GetComponentsInChildren<Renderer>())
                    if (renderer != null && renderer.bounds.max.y > topY)
                        topY = renderer.bounds.max.y;

                eyeWorldPosition = new Vector3(root.position.x, topY, root.position.z);
                return true;
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

            internal void StartReturnToRest()
            {
                RefreshRestLocation();
                if (!_hasRestLocation || ActiveOwner != null || Crew.IsOccupied)
                    return;

                _returningToRest = false;
                if (LocalHorizontalDistance(_logicAgent.CurrentLocalPosition, _restStandLocalPosition) <= 0.35f)
                {
                    _logicAgent.Stop();
                    _poseSync.SetPoseOverride(_logicAgent.CurrentLocalPosition, _restLocalRotation);
                }
                else
                {
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
