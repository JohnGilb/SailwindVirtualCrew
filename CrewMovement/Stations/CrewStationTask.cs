namespace SailwindVirtualCrew
{
    internal enum CrewStationTaskState
    {
        None,
        Moving,
        Working,
        Cancelled
    }

    internal sealed class CrewStationTask
    {
        private const string Phase = "Phase09";
        private readonly CrewStation _station;
        private readonly ProxyLogicAgent _logicAgent;
        private readonly ProxyNavMeshNavigationProvider _navMesh;
        private readonly ProxyToBoatPoseSync _poseSync;
        private bool _arrivalLogged;

        internal CrewStationTaskState State { get; private set; }
        internal string ReservedStationId => _station?.Id;

        internal CrewStationTask(CrewStation station, ProxyLogicAgent logicAgent, ProxyNavMeshNavigationProvider navMesh, ProxyToBoatPoseSync poseSync)
        {
            _station = station;
            _logicAgent = logicAgent;
            _navMesh = navMesh;
            _poseSync = poseSync;
            State = CrewStationTaskState.None;
        }

        internal void Begin()
        {
            if (_station == null || !_station.Projected || _logicAgent == null || !_logicAgent.IsValid || _navMesh == null || !_navMesh.IsBaked)
            {
                CrewDebugLog.Fail(Phase, "Cannot assign station task; station, agent, or navmesh is unavailable.");
                return;
            }

            State = CrewStationTaskState.Moving;
            _arrivalLogged = false;
            _poseSync?.ClearRotationOverride();
            CrewDebugLog.Ok(Phase, "Task assigned crew='test-crew-001' station='" + _station.Id + "'");
            CrewDebugLog.Ok(Phase, "Station reserved station='" + _station.Id + "'");

            var destinationWorld = _navMesh.Proxy.Root.transform.TransformPoint(_station.ProjectedLocalStand);
            _logicAgent.SetDestination(destinationWorld, _station.ProjectedLocalStand);
        }

        internal void Tick()
        {
            if (State != CrewStationTaskState.Moving || _logicAgent == null || !_logicAgent.IsValid)
                return;

            if (_logicAgent.HasArrived)
            {
                State = CrewStationTaskState.Working;
                _poseSync?.SetRotationOverride(_station.LocalRotation);
                if (!_arrivalLogged)
                {
                    _arrivalLogged = true;
                    CrewDebugLog.Ok(Phase, "Arrived at station='" + _station.Id + "'");
                    CrewDebugLog.Ok(Phase, "Task state=Working");
                }
            }
        }

        internal void Cancel()
        {
            if (State == CrewStationTaskState.Cancelled)
                return;

            State = CrewStationTaskState.Cancelled;
            _poseSync?.ClearRotationOverride();
            _logicAgent?.Stop();
            CrewDebugLog.Ok(Phase, "Task cancelled station='" + ReservedStationId + "'");
        }

        internal void Dump()
        {
            CrewDebugLog.Ok(Phase,
                "Task state=" + State
                + " reservedStation='" + (ReservedStationId ?? "none") + "'");
        }
    }
}
