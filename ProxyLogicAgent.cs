using UnityEngine;
using UnityEngine.AI;

namespace SailwindVirtualCrew
{
    internal sealed class ProxyLogicAgent
    {
        private const string Phase = "Phase06";
        private readonly GameObject _root;
        private readonly NavMeshAgent _agent;
        private readonly Transform _proxyRoot;
        private Vector3 _lastDestinationLocal;
        private Vector3 _lastDestinationWorld;
        private bool _hasDestination;
        private bool _arrivalLogged;
        private bool _pathReadyLogged;
        private bool _teleportIfUnreachable;
        private bool _pendingUnreachableTeleport;
        private string _pendingTeleportReason;
        private float _unreachableTeleportDelay;
        private float _unreachableTeleportStartTime;
        private float _unreachableTeleportAtTime;
        private float _nextProgressLogTime;

        internal ProxyLogicAgent(Transform proxyRoot, Vector3 startWorld, string objectName = "VC_LogicAgent_Test")
        {
            _proxyRoot = proxyRoot;
            _root = new GameObject(objectName);
            _root.transform.position = startWorld;
            _root.transform.rotation = Quaternion.identity;
            _root.layer = 2;

            _agent = _root.AddComponent<NavMeshAgent>();
            _agent.radius = 0.18f;
            _agent.height = 1.7f;
            _agent.speed = 1.6f;
            _agent.acceleration = 6f;
            _agent.angularSpeed = 360f;
            _agent.stoppingDistance = 0.15f;
            _agent.autoBraking = true;
            _agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
            _agent.Warp(startWorld);

            CrewDebugLog.Ok(Phase, "Agent placed on navmesh=" + _agent.isOnNavMesh);
            DumpState();
        }

        internal bool IsValid => _root && _agent;

        internal Vector3 CurrentLocalPosition => CurrentLocalPositionInternal();
        internal bool HasArrived => _hasDestination && _arrivalLogged;
        internal bool HasDestination => _hasDestination;
        internal bool HasActiveDestination => _hasDestination && !_arrivalLogged;
        internal Vector3 LastDestinationLocal => _lastDestinationLocal;
        internal bool HasPendingUnreachableTeleport => _pendingUnreachableTeleport;

        internal Quaternion CurrentLocalRotation
        {
            get
            {
                Vector3 localForward = _proxyRoot ? _proxyRoot.InverseTransformDirection(_root.transform.forward) : _root.transform.forward;
                if (_agent.velocity.sqrMagnitude > 0.01f)
                    localForward = _proxyRoot ? _proxyRoot.InverseTransformDirection(_agent.velocity.normalized) : _agent.velocity.normalized;

                localForward.y = 0f;
                if (localForward.sqrMagnitude < 0.001f)
                    localForward = Vector3.forward;

                return Quaternion.LookRotation(localForward.normalized, Vector3.up);
            }
        }

        internal void SetDestination(Vector3 destinationWorld, Vector3 destinationLocal, bool teleportIfUnreachable = false, float unreachableTeleportDelay = 0f)
        {
            if (!IsValid)
            {
                CrewDebugLog.Warn(Phase, "Logic agent does not exist.");
                return;
            }

            _lastDestinationLocal = destinationLocal;
            _lastDestinationWorld = destinationWorld;
            _hasDestination = true;
            _arrivalLogged = false;
            _pathReadyLogged = false;
            _teleportIfUnreachable = teleportIfUnreachable;
            _pendingUnreachableTeleport = false;
            _pendingTeleportReason = null;
            _unreachableTeleportDelay = Mathf.Max(0f, unreachableTeleportDelay);
            _unreachableTeleportStartTime = 0f;
            _unreachableTeleportAtTime = 0f;
            _nextProgressLogTime = Time.time + 2f;

            bool destinationSet = _agent.SetDestination(destinationWorld);
            CrewDebugLog.Ok(Phase,
                "Destination set local=" + Format(destinationLocal)
                + " success=" + destinationSet
                + " teleportIfUnreachable=" + teleportIfUnreachable
                + " unreachableTeleportDelay=" + _unreachableTeleportDelay.ToString("0.000"));
            if (!destinationSet && _teleportIfUnreachable)
                ScheduleUnreachableTeleport("SetDestination failed");
            DumpState();
        }

        internal void Tick()
        {
            if (!IsValid || !_hasDestination || _arrivalLogged)
                return;

            if (_pendingUnreachableTeleport)
            {
                if (Time.time >= _unreachableTeleportAtTime)
                    TeleportToDestination(_pendingTeleportReason);
                else if (Time.time >= _nextProgressLogTime)
                {
                    _nextProgressLogTime = Time.time + 3f;
                    CrewDebugLog.Ok(Phase,
                        "Waiting to teleport unreachable agent; remaining="
                        + Mathf.Max(0f, _unreachableTeleportAtTime - Time.time).ToString("0.000")
                        + " local=" + Format(CurrentLocalPosition));
                }
                return;
            }

            float directDistance = Vector3.Distance(_root.transform.position, _lastDestinationWorld);

            if (!_agent.pathPending && !_pathReadyLogged)
            {
                _pathReadyLogged = true;
                CrewDebugLog.Ok(Phase,
                    "Agent path ready; status=" + _agent.pathStatus
                    + " hasPath=" + _agent.hasPath
                    + " directDistance=" + directDistance.ToString("0.000")
                    + " local=" + Format(CurrentLocalPosition));

                if (_teleportIfUnreachable && _agent.pathStatus != NavMeshPathStatus.PathComplete)
                {
                    ScheduleUnreachableTeleport("pathStatus=" + _agent.pathStatus + " hasPath=" + _agent.hasPath);
                    return;
                }
            }

            if (!_agent.pathPending && directDistance <= _agent.stoppingDistance + 0.12f)
            {
                _arrivalLogged = true;
                CrewDebugLog.Ok(Phase,
                    "Agent arrived; final local=" + Format(CurrentLocalPosition)
                    + " directDistance=" + directDistance.ToString("0.000"));
                return;
            }

            if (!_agent.pathPending && Time.time >= _nextProgressLogTime)
            {
                _nextProgressLogTime = Time.time + 3f;
                CrewDebugLog.Ok(Phase,
                    "Agent moving; directDistance=" + directDistance.ToString("0.000")
                    + " velocity=" + Format(_agent.velocity)
                    + " local=" + Format(CurrentLocalPosition));
            }
        }

        internal void DumpState()
        {
            if (!IsValid)
            {
                CrewDebugLog.Warn(Phase, "Logic agent does not exist.");
                return;
            }

            CrewDebugLog.Ok(Phase,
                "Agent state isOnNavMesh=" + _agent.isOnNavMesh
                + " hasPath=" + _agent.hasPath
                + " pathPending=" + _agent.pathPending
                + " status=" + _agent.pathStatus
                + " remainingDistance=" + _agent.remainingDistance.ToString("0.000")
                + " directDistance=" + GetDirectDistanceText()
                + " velocity=" + Format(_agent.velocity)
                + " local=" + Format(CurrentLocalPositionInternal())
                + (_pendingUnreachableTeleport ? " pendingTeleportRemaining=" + Mathf.Max(0f, _unreachableTeleportAtTime - Time.time).ToString("0.000") : "")
                + (_hasDestination ? " destinationLocal=" + Format(_lastDestinationLocal) : ""));
        }

        internal float GetPendingUnreachableTeleportProgress()
        {
            if (!_pendingUnreachableTeleport)
                return 0f;

            if (_unreachableTeleportDelay <= 0f)
                return 0f;

            return Mathf.Clamp01(1f - (Time.time - _unreachableTeleportStartTime) / _unreachableTeleportDelay) * 100f;
        }

        internal void SetSpeed(float speed)
        {
            if (IsValid)
                _agent.speed = speed;
        }

        internal void Stop()
        {
            if (!IsValid)
                return;

            _agent.ResetPath();
            _hasDestination = false;
            _arrivalLogged = false;
            _pathReadyLogged = false;
            _teleportIfUnreachable = false;
            _pendingUnreachableTeleport = false;
            _pendingTeleportReason = null;
            CrewDebugLog.Ok(Phase, "Agent stopped.");
        }

        internal void WarpToLocal(Vector3 localPosition, Quaternion localRotation)
        {
            if (!IsValid || !_proxyRoot)
                return;

            Vector3 worldPosition = _proxyRoot.TransformPoint(localPosition);
            _agent.Warp(worldPosition);
            _root.transform.rotation = _proxyRoot.rotation * localRotation;
            _hasDestination = false;
            _arrivalLogged = false;
            _pathReadyLogged = false;
            _teleportIfUnreachable = false;
            _pendingUnreachableTeleport = false;
            _pendingTeleportReason = null;
        }

        internal void Destroy()
        {
            if (_root)
                Object.Destroy(_root);
        }

        private Vector3 CurrentLocalPositionInternal()
        {
            return _proxyRoot ? _proxyRoot.InverseTransformPoint(_root.transform.position) : _root.transform.position;
        }

        private void ScheduleUnreachableTeleport(string reason)
        {
            _agent.ResetPath();
            _pendingUnreachableTeleport = true;
            _pendingTeleportReason = reason;
            _unreachableTeleportStartTime = Time.time;
            _unreachableTeleportAtTime = Time.time + _unreachableTeleportDelay;
            _pathReadyLogged = true;

            CrewDebugLog.Warn(Phase,
                "Scheduled teleport to unreachable destination reason='" + reason
                + "' delay=" + _unreachableTeleportDelay.ToString("0.000")
                + " destinationLocal=" + Format(_lastDestinationLocal));

            if (_unreachableTeleportDelay <= 0f)
                TeleportToDestination(reason);
        }

        private void TeleportToDestination(string reason)
        {
            if (!IsValid)
                return;

            _agent.ResetPath();
            _agent.Warp(_lastDestinationWorld);
            _hasDestination = true;
            _arrivalLogged = true;
            _pathReadyLogged = true;
            _teleportIfUnreachable = false;
            _pendingUnreachableTeleport = false;
            _pendingTeleportReason = null;
            CrewDebugLog.Warn(Phase,
                "Teleported agent to unreachable destination reason='" + reason
                + "' destinationLocal=" + Format(_lastDestinationLocal)
                + " finalLocal=" + Format(CurrentLocalPositionInternal()));
        }

        private string GetDirectDistanceText()
        {
            if (!_hasDestination)
                return "n/a";

            return Vector3.Distance(_root.transform.position, _lastDestinationWorld).ToString("0.000");
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }
    }
}
