using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public enum SkullingCommand
    {
        Stop,
        Ahead,
        Aback,
        Port,
        Starboard,
        TurnPort,
        TurnStarboard
    }

    public enum SkullingStationId
    {
        ForePort,
        ForeStarboard,
        AftPort,
        AftStarboard,
        Fore,
        Aft,
        Port,
        Starboard
    }

    public sealed class SkullingRequest
    {
        private const int MaxStations = 8;
        private const float ProjectionDistance = 8f;
        private const float PositioningGraceSeconds = 6f;
        private const float ForceBurstSeconds = 1f;
        private const float ForceRestSeconds = 1f;

        private readonly List<Crewman> crew;
        private readonly Dictionary<SkullingStationId, SkullingStation> stations;
        private readonly List<SkullingAssignment> assignments = new List<SkullingAssignment>();
        private readonly Transform worldBoat;
        private readonly Rigidbody boatRigidbody;
        private readonly Vector3 forwardLocal;
        private readonly Vector3 starboardLocal;
        private readonly float rowForce;
        private readonly float maxBoatSpeed;

        private bool waiting;
        private float burstStartTime;
        private string statusMessage = "";

        public SkullingCommand Command { get; private set; }
        public WorkRequestStatus Status { get; private set; } = WorkRequestStatus.Positioning;
        public IReadOnlyList<Crewman> Crew => crew.AsReadOnly();
        public int AssignedCrewCount => crew.Count;
        public int ActiveStationCount => assignments.Count;
        public bool IsWaiting => waiting;
        public bool IsReady => Status == WorkRequestStatus.InProgress && !waiting;
        public bool IsForceBurstActive => IsReady && IsForceBurstWindow();
        public string StatusMessage => statusMessage;

        private SkullingRequest(
            SkullingCommand command,
            List<Crewman> selectedCrew,
            Dictionary<SkullingStationId, SkullingStation> stations,
            Transform worldBoat,
            Rigidbody boatRigidbody,
            Vector3 forwardLocal,
            Vector3 starboardLocal,
            float rowForce,
            float maxBoatSpeed)
        {
            Command = command;
            crew = selectedCrew;
            this.stations = stations;
            this.worldBoat = worldBoat;
            this.boatRigidbody = boatRigidbody;
            this.forwardLocal = forwardLocal.normalized;
            this.starboardLocal = starboardLocal.normalized;
            this.rowForce = rowForce;
            this.maxBoatSpeed = maxBoatSpeed;

            foreach (var crewman in crew)
                crewman.CurrentTask = this;

            ChangeCommand(command);
        }

        public static bool TryCreate(SkullingCommand command, IEnumerable<Crewman> candidates, out SkullingRequest request, out string reason)
        {
            request = null;
            reason = "";

            if (command == SkullingCommand.Stop)
            {
                reason = "Stop is not a starting skulling command.";
                return false;
            }

            var oars = LocatorUtils.FindOarsOnCurrentVessel();
            int oarCount = oars.Count;
            if (oarCount <= 0)
            {
                reason = "No oars found on the current vessel.";
                return false;
            }

            var forceOars = oars.Where(o => o.rowForce > 0f && o.maxBoatSpeed > 0f).ToList();
            if (forceOars.Count == 0)
            {
                reason = "No usable oar force values found.";
                return false;
            }

            if (!TryBuildStations(out var stations, out var worldBoat, out var rigidbody, out var forwardLocal, out var starboardLocal, out reason))
                return false;

            int capacity = Mathf.Min(MaxStations, oarCount, stations.Count);
            if (capacity <= 0)
            {
                reason = "No reachable skulling stations found.";
                return false;
            }

            var selectedCrew = (candidates ?? Enumerable.Empty<Crewman>())
                .Where(c => c != null && c.Role == ShipRole.Deckhand && VirtualCrewManager.Instance.IsCrewAssignable(c))
                .OrderByDescending(c => (float)c.CurrentStamina / c.MaxStamina)
                .ThenByDescending(c => c.Strength)
                .Take(capacity)
                .ToList();

            if (selectedCrew.Count == 0)
            {
                reason = "No awake idle deckhands available.";
                return false;
            }

            request = new SkullingRequest(
                command,
                selectedCrew,
                stations,
                worldBoat,
                rigidbody,
                forwardLocal,
                starboardLocal,
                forceOars.Average(o => o.rowForce),
                forceOars.Average(o => o.maxBoatSpeed));
            reason = "Skulling started.";
            return true;
        }

        public bool HasCrew(Crewman crewman)
        {
            return crewman != null && crew.Contains(crewman);
        }

        public void ChangeCommand(SkullingCommand command)
        {
            if (command == SkullingCommand.Stop)
            {
                Wait();
                return;
            }

            Command = command;
            waiting = false;
            Status = WorkRequestStatus.Positioning;
            burstStartTime = Time.time;
            ReassignStations();
        }

        public void Wait()
        {
            waiting = true;
            Command = SkullingCommand.Stop;
            Status = WorkRequestStatus.InProgress;
            burstStartTime = Time.time;
            statusMessage = "Waiting at skulling stations.";
            foreach (var assignment in assignments)
                CrewNavigationCoordinator.Instance.TryStopOwnerMotion(assignment);
        }

        public void Tick()
        {
            if (waiting || Status == WorkRequestStatus.Complete)
                return;

            if (Status == WorkRequestStatus.Positioning)
            {
                bool allReady = assignments.Count > 0;
                foreach (var assignment in assignments)
                {
                    if (!assignment.IsPositioningComplete())
                    {
                        allReady = false;
                        continue;
                    }

                    assignment.CompletePositioning();
                }

                if (allReady)
                {
                    Status = WorkRequestStatus.InProgress;
                    burstStartTime = Time.time;
                    statusMessage = "Skulling " + GetCommandLabel(Command) + ".";
                }
                else
                {
                    statusMessage = "Moving to skulling stations.";
                }
            }
        }

        public void UpdateFrame()
        {
            if (!IsForceBurstActive || boatRigidbody == null || worldBoat == null)
                return;

            float speedFactor = Mathf.InverseLerp(maxBoatSpeed, 0f, boatRigidbody.velocity.magnitude);
            if (speedFactor <= 0f)
                return;

            foreach (var assignment in assignments)
            {
                if (!assignment.Ready || assignment.Crewman == null)
                    continue;

                Vector3 stationWorld = worldBoat.TransformPoint(assignment.Station.ForceLocal);
                Vector3 direction = GetForceDirectionWorld(assignment.Station, stationWorld);
                if (direction.sqrMagnitude < 0.001f)
                    continue;

                float strengthScale = GetStrengthScale(assignment.Crewman);
                Vector3 force = direction.normalized * rowForce * strengthScale * Time.deltaTime * speedFactor;
                boatRigidbody.AddForceAtPosition(force, stationWorld);
            }
        }

        public float GetPositioningProgress()
        {
            if (assignments.Count == 0)
                return 100f;

            return assignments.Average(a => a.GetPositioningProgress());
        }

        public void Cancel()
        {
            foreach (var assignment in assignments)
                assignment.CancelPositioning();

            foreach (var crewman in crew)
                if (crewman != null && crewman.CurrentTask == this)
                    crewman.CurrentTask = null;

            assignments.Clear();
            Status = WorkRequestStatus.Complete;
            statusMessage = "Skulling dismissed.";
        }

        private void ReassignStations()
        {
            foreach (var assignment in assignments)
                assignment.CancelPositioning();
            assignments.Clear();

            var stationOrder = GetStationPreferenceForCurrentCommand()
                .Where(id => stations.ContainsKey(id))
                .Select(id => stations[id])
                .Take(crew.Count)
                .ToList();

            var unassignedCrew = crew.ToList();
            foreach (var station in stationOrder)
            {
                var best = unassignedCrew
                    .OrderBy(c => CrewNavigationCoordinator.Instance.EstimateDistanceToLocalPosition(c, station.StandLocal, ProjectionDistance))
                    .FirstOrDefault();
                if (best == null)
                    break;

                unassignedCrew.Remove(best);
                var assignment = new SkullingAssignment(this, best, station);
                assignments.Add(assignment);
                assignment.BeginPositioning();
            }

            statusMessage = assignments.Count == 0
                ? "No reachable skulling stations for command."
                : "Moving to skulling stations.";
        }

        private IEnumerable<SkullingStationId> GetStationPreferenceForCurrentCommand()
        {
            if (Command != SkullingCommand.TurnPort && Command != SkullingCommand.TurnStarboard)
                return GetStationPreference(Command, crew.Count);

            var mooringStations = Command == SkullingCommand.TurnPort
                ? new[]
                {
                    SkullingStationId.ForeStarboard,
                    SkullingStationId.AftPort,
                    SkullingStationId.AftStarboard,
                    SkullingStationId.ForePort
                }
                : new[]
                {
                    SkullingStationId.ForePort,
                    SkullingStationId.AftStarboard,
                    SkullingStationId.AftPort,
                    SkullingStationId.ForeStarboard
                };

            var centerStations = new[]
                {
                    SkullingStationId.Fore,
                    SkullingStationId.Aft,
                    SkullingStationId.Port,
                    SkullingStationId.Starboard
                }
                .Where(id => stations.ContainsKey(id))
                .OrderByDescending(id => GetHorizontalLeverArm(stations[id].ForceLocal));

            return mooringStations.Concat(centerStations);
        }

        private static float GetHorizontalLeverArm(Vector3 local)
        {
            local.y = 0f;
            return local.magnitude;
        }

        private Vector3 GetForceDirectionWorld(SkullingStation station, Vector3 stationWorld)
        {
            if (Command == SkullingCommand.TurnPort || Command == SkullingCommand.TurnStarboard)
            {
                Vector3 leverWorld = stationWorld - boatRigidbody.worldCenterOfMass;
                leverWorld.y = 0f;
                if (leverWorld.sqrMagnitude < 0.001f)
                    return Vector3.zero;

                Vector3 tangent = Vector3.Cross(boatRigidbody.transform.up, leverWorld);
                if (Command == SkullingCommand.TurnPort)
                    tangent = -tangent;
                return tangent;
            }

            Vector3 localDirection;
            switch (Command)
            {
                case SkullingCommand.Aback:
                    localDirection = -forwardLocal;
                    break;
                case SkullingCommand.Port:
                    localDirection = -starboardLocal;
                    break;
                case SkullingCommand.Starboard:
                    localDirection = starboardLocal;
                    break;
                default:
                    localDirection = forwardLocal;
                    break;
            }

            return worldBoat.TransformDirection(localDirection);
        }

        private bool IsForceBurstWindow()
        {
            float cycle = ForceBurstSeconds + ForceRestSeconds;
            if (cycle <= 0f)
                return true;

            float elapsed = Time.time - burstStartTime;
            return elapsed - Mathf.Floor(elapsed / cycle) * cycle < ForceBurstSeconds;
        }

        private static float GetStrengthScale(Crewman crewman)
        {
            int strength = crewman != null ? crewman.Strength : 3;
            return Mathf.Lerp(0.5f, 1f, Mathf.InverseLerp(1f, 7f, strength));
        }

        private static bool TryBuildStations(
            out Dictionary<SkullingStationId, SkullingStation> stations,
            out Transform worldBoat,
            out Rigidbody rigidbody,
            out Vector3 forwardLocal,
            out Vector3 starboardLocal,
            out string reason)
        {
            stations = new Dictionary<SkullingStationId, SkullingStation>();
            worldBoat = null;
            rigidbody = null;
            forwardLocal = Vector3.forward;
            starboardLocal = Vector3.right;
            reason = "";

            if (!MooringLocator.TryScan(out var scan) || scan == null)
            {
                reason = "Could not scan mooring ropes.";
                CrewDebugLog.Warn("Skulling", reason);
                return false;
            }

            if (scan.Ropes.Count < 4)
            {
                reason = "Skulling needs at least four mooring rope anchors.";
                CrewDebugLog.Warn("Skulling", reason);
                return false;
            }

            worldBoat = scan.Context.WorldBoat;
            rigidbody = scan.Context.Rigidbody;
            if (!worldBoat || rigidbody == null)
            {
                reason = "Could not resolve current vessel rigidbody.";
                CrewDebugLog.Warn("Skulling", reason);
                return false;
            }

            Vector3 beamLocal = scan.SideMap.Axis == MooringBeamAxis.LocalX ? Vector3.right : Vector3.forward;
            forwardLocal = scan.SideMap.Axis == MooringBeamAxis.LocalX ? Vector3.forward : Vector3.right;
            Vector3 portLocal = scan.SideMap.PortPositive ? beamLocal : -beamLocal;
            starboardLocal = -portLocal;

            var portRopes = scan.Ropes.Where(r => r.Side == MooringSide.Port).ToList();
            var starboardRopes = scan.Ropes.Where(r => r.Side == MooringSide.Starboard).ToList();
            if (portRopes.Count < 2 || starboardRopes.Count < 2)
            {
                reason = "Skulling needs two port and two starboard mooring rope anchors.";
                CrewDebugLog.Warn("Skulling", reason);
                return false;
            }

            var forePort = portRopes.OrderByDescending(r => GetLongitudinalValue(scan.SideMap, r.AnchorLocal)).First();
            var aftPort = portRopes.OrderBy(r => GetLongitudinalValue(scan.SideMap, r.AnchorLocal)).First();
            var foreStarboard = starboardRopes.OrderByDescending(r => GetLongitudinalValue(scan.SideMap, r.AnchorLocal)).First();
            var aftStarboard = starboardRopes.OrderBy(r => GetLongitudinalValue(scan.SideMap, r.AnchorLocal)).First();

            AddProjectedStation(stations, SkullingStationId.ForePort, forePort.AnchorLocal);
            AddProjectedStation(stations, SkullingStationId.ForeStarboard, foreStarboard.AnchorLocal);
            AddProjectedStation(stations, SkullingStationId.AftPort, aftPort.AnchorLocal);
            AddProjectedStation(stations, SkullingStationId.AftStarboard, aftStarboard.AnchorLocal);
            AddProjectedStation(stations, SkullingStationId.Fore, (forePort.AnchorLocal + foreStarboard.AnchorLocal) * 0.5f);
            AddProjectedStation(stations, SkullingStationId.Aft, (aftPort.AnchorLocal + aftStarboard.AnchorLocal) * 0.5f);
            AddProjectedStation(stations, SkullingStationId.Port, (forePort.AnchorLocal + aftPort.AnchorLocal) * 0.5f);
            AddProjectedStation(stations, SkullingStationId.Starboard, (foreStarboard.AnchorLocal + aftStarboard.AnchorLocal) * 0.5f);

            if (stations.Count == 0)
            {
                reason = "No skulling stations could be projected onto the crew navmesh.";
                CrewDebugLog.Warn("Skulling", reason);
                return false;
            }

            return true;
        }

        private static float GetLongitudinalValue(MooringSideMap sideMap, Vector3 local)
        {
            return sideMap.Axis == MooringBeamAxis.LocalX ? local.z : local.x;
        }

        private static void AddProjectedStation(Dictionary<SkullingStationId, SkullingStation> stations, SkullingStationId id, Vector3 forceLocal)
        {
            if (CrewNavigationCoordinator.Instance.TryProjectLocalToNavMeshQuiet(forceLocal, ProjectionDistance, out var standLocal))
                stations[id] = new SkullingStation(id, standLocal, forceLocal);
            else
                CrewDebugLog.Warn("Skulling", "Skipping unreachable station '" + id + "'.");
        }

        private static IEnumerable<SkullingStationId> GetStationPreference(SkullingCommand command, int crewCount)
        {
            SkullingStationId[] primary;
            switch (command)
            {
                case SkullingCommand.Aback:
                    primary = LinearForeAftPreference(crewCount, foreFirst: true);
                    break;
                case SkullingCommand.Port:
                    primary = LinearSidePreference(crewCount, starboardFirst: true);
                    break;
                case SkullingCommand.Starboard:
                    primary = LinearSidePreference(crewCount, starboardFirst: false);
                    break;
                case SkullingCommand.TurnPort:
                    primary = new[]
                    {
                        SkullingStationId.ForeStarboard,
                        SkullingStationId.AftPort,
                        SkullingStationId.AftStarboard,
                        SkullingStationId.ForePort,
                        SkullingStationId.Fore,
                        SkullingStationId.Aft,
                        SkullingStationId.Port,
                        SkullingStationId.Starboard
                    };
                    break;
                case SkullingCommand.TurnStarboard:
                    primary = new[]
                    {
                        SkullingStationId.ForePort,
                        SkullingStationId.AftStarboard,
                        SkullingStationId.AftPort,
                        SkullingStationId.ForeStarboard,
                        SkullingStationId.Fore,
                        SkullingStationId.Aft,
                        SkullingStationId.Port,
                        SkullingStationId.Starboard
                    };
                    break;
                default:
                    primary = LinearForeAftPreference(crewCount, foreFirst: false);
                    break;
            }

            foreach (var id in primary)
                yield return id;

            foreach (SkullingStationId id in Enum.GetValues(typeof(SkullingStationId)))
                if (!primary.Contains(id))
                    yield return id;
        }

        private static SkullingStationId[] LinearForeAftPreference(int crewCount, bool foreFirst)
        {
            var center = foreFirst ? SkullingStationId.Fore : SkullingStationId.Aft;
            var farCenter = foreFirst ? SkullingStationId.Aft : SkullingStationId.Fore;
            var nearPort = foreFirst ? SkullingStationId.ForePort : SkullingStationId.AftPort;
            var nearStarboard = foreFirst ? SkullingStationId.ForeStarboard : SkullingStationId.AftStarboard;
            var farPort = foreFirst ? SkullingStationId.AftPort : SkullingStationId.ForePort;
            var farStarboard = foreFirst ? SkullingStationId.AftStarboard : SkullingStationId.ForeStarboard;

            if (crewCount <= 1)
                return new[] { center };
            if (crewCount == 2)
                return new[] { nearPort, nearStarboard };
            if (crewCount == 3)
                return new[] { nearPort, nearStarboard, center };
            if (crewCount == 4)
                return new[] { nearPort, nearStarboard, farPort, farStarboard };
            if (crewCount == 5)
                return new[] { nearPort, nearStarboard, farPort, farStarboard, center };
            if (crewCount == 6)
                return new[] { nearPort, nearStarboard, farPort, farStarboard, SkullingStationId.Port, SkullingStationId.Starboard };
            if (crewCount == 7)
                return new[] { nearPort, nearStarboard, farPort, farStarboard, SkullingStationId.Port, SkullingStationId.Starboard, center };

            return new[] { nearPort, nearStarboard, farPort, farStarboard, SkullingStationId.Port, SkullingStationId.Starboard, center, farCenter };
        }

        private static SkullingStationId[] LinearSidePreference(int crewCount, bool starboardFirst)
        {
            var center = starboardFirst ? SkullingStationId.Starboard : SkullingStationId.Port;
            var farCenter = starboardFirst ? SkullingStationId.Port : SkullingStationId.Starboard;
            var nearFore = starboardFirst ? SkullingStationId.ForeStarboard : SkullingStationId.ForePort;
            var nearAft = starboardFirst ? SkullingStationId.AftStarboard : SkullingStationId.AftPort;
            var farFore = starboardFirst ? SkullingStationId.ForePort : SkullingStationId.ForeStarboard;
            var farAft = starboardFirst ? SkullingStationId.AftPort : SkullingStationId.AftStarboard;

            if (crewCount <= 1)
                return new[] { center };
            if (crewCount == 2)
                return new[] { nearFore, nearAft };
            if (crewCount == 3)
                return new[] { nearFore, nearAft, center };
            if (crewCount == 4)
                return new[] { nearFore, nearAft, farFore, farAft };
            if (crewCount == 5)
                return new[] { nearFore, nearAft, farFore, farAft, center };
            if (crewCount == 6)
                return new[] { nearFore, nearAft, farFore, farAft, SkullingStationId.Fore, SkullingStationId.Aft };
            if (crewCount == 7)
                return new[] { nearFore, nearAft, farFore, farAft, SkullingStationId.Fore, SkullingStationId.Aft, center };

            return new[] { nearFore, nearAft, farFore, farAft, SkullingStationId.Fore, SkullingStationId.Aft, center, farCenter };
        }

        public static string GetCommandLabel(SkullingCommand command)
        {
            switch (command)
            {
                case SkullingCommand.Aback: return "Aback";
                case SkullingCommand.Port: return "Port";
                case SkullingCommand.Starboard: return "Stbd";
                case SkullingCommand.TurnPort: return "Turn Port";
                case SkullingCommand.TurnStarboard: return "Turn Stbd";
                case SkullingCommand.Stop: return "Stop";
                default: return "Ahead";
            }
        }

        private sealed class SkullingStation
        {
            internal SkullingStationId Id { get; }
            internal Vector3 StandLocal { get; }
            internal Vector3 ForceLocal { get; }

            internal SkullingStation(SkullingStationId id, Vector3 standLocal, Vector3 forceLocal)
            {
                Id = id;
                StandLocal = standLocal;
                ForceLocal = forceLocal;
            }
        }

        private sealed class SkullingAssignment
        {
            private readonly SkullingRequest request;
            private bool concretePositioning;
            private float positioningStartTime;
            private float positioningDuration;

            internal Crewman Crewman { get; }
            internal SkullingStation Station { get; }
            internal bool Ready { get; private set; }

            internal SkullingAssignment(SkullingRequest request, Crewman crewman, SkullingStation station)
            {
                this.request = request;
                Crewman = crewman;
                Station = station;
            }

            internal void BeginPositioning()
            {
                Ready = false;
                positioningStartTime = Time.time;
                positioningDuration = Mathf.Max(0f, 7f - Crewman.Dexterity);
                Vector3 faceLocal = request.GetFacingLocal(Station);
                Quaternion rotation = Quaternion.LookRotation(faceLocal, Vector3.up);
                concretePositioning = CrewNavigationCoordinator.Instance.TryBeginRolePositioning(
                    this,
                    Crewman,
                    Station.StandLocal,
                    rotation,
                    "skulling " + Station.Id.ToString().ToLowerInvariant());
            }

            internal bool IsPositioningComplete()
            {
                if (Ready)
                    return true;
                if (concretePositioning && CrewNavigationCoordinator.Instance.IsPositioningComplete(this))
                    return true;
                return Time.time >= positioningStartTime + positioningDuration + PositioningGraceSeconds;
            }

            internal float GetPositioningProgress()
            {
                if (Ready)
                    return 0f;
                if (concretePositioning)
                    return CrewNavigationCoordinator.Instance.GetPositioningProgress(this);
                if (positioningDuration <= 0f)
                    return 0f;
                return Mathf.Clamp01(1f - (Time.time - positioningStartTime) / positioningDuration) * 100f;
            }

            internal void CompletePositioning()
            {
                if (Ready)
                    return;

                if (concretePositioning)
                    CrewNavigationCoordinator.Instance.Complete(this);
                concretePositioning = false;
                Ready = true;
            }

            internal void CancelPositioning()
            {
                if (concretePositioning)
                    CrewNavigationCoordinator.Instance.Cancel(this);
                concretePositioning = false;
                Ready = false;
            }
        }

        private Vector3 GetFacingLocal(SkullingStation station)
        {
            Vector3 center = Vector3.zero;
            Vector3 outward = station.StandLocal - center;
            outward.y = 0f;
            if (outward.sqrMagnitude >= 0.001f)
                return outward.normalized;

            return forwardLocal;
        }
    }
}
