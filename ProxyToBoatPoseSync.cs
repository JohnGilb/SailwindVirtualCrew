using UnityEngine;

namespace SailwindVirtualCrew
{
    internal sealed class ProxyToBoatPoseSync
    {
        private const string Phase = "Phase07";
        private readonly CrewAgent _visualAgent;
        private readonly ProxyLogicAgent _logicAgent;
        private readonly CrewBoatContext _context;
        private bool _paused;
        private float _nextLogTime;
        private bool _hasRotationOverride;
        private Quaternion _rotationOverride;
        private bool _hasPoseOverride;
        private Vector3 _positionOverride;

        internal ProxyToBoatPoseSync(CrewAgent visualAgent, ProxyLogicAgent logicAgent, CrewBoatContext context)
        {
            _visualAgent = visualAgent;
            _logicAgent = logicAgent;
            _context = context;
        }

        internal bool IsPaused => _paused;

        internal void SetPaused(bool paused)
        {
            _paused = paused;
            CrewDebugLog.Ok(Phase, paused ? "Paused visual sync." : "Resumed visual sync.");
        }

        internal void Tick()
        {
            if (_paused || _visualAgent == null || !_visualAgent.VisualRoot || _logicAgent == null || !_logicAgent.IsValid)
                return;

            Vector3 proxyLocal = _hasPoseOverride ? _positionOverride : _logicAgent.CurrentLocalPosition;
            Quaternion proxyLocalRotation = _hasRotationOverride ? _rotationOverride : _logicAgent.CurrentLocalRotation;

            var visualTransform = _visualAgent.VisualRoot.transform;
            if (visualTransform.parent != _context.WorldBoat)
                visualTransform.SetParent(_context.WorldBoat, false);

            visualTransform.localPosition = proxyLocal;
            visualTransform.localRotation = proxyLocalRotation;

            if (Time.time >= _nextLogTime)
            {
                _nextLogTime = Time.time + 4f;
                float syncError = Vector3.Distance(visualTransform.localPosition, proxyLocal);
                // Phase 7 stuff is pretty spammmy, disabling this for now.
                /*CrewDebugLog.Ok(Phase,
                    "proxyLocal=" + Format(proxyLocal)
                    + ", realMapped=" + Format(_context.WorldBoat.TransformPoint(proxyLocal))
                    + ", syncError=" + syncError.ToString("0.0000"));
                CrewDebugLog.Ok(Phase, "visual parent='" + visualTransform.parent.name + "'");
                */
            }
        }

        internal void Dump()
        {
            if (_visualAgent == null || !_visualAgent.VisualRoot || _logicAgent == null || !_logicAgent.IsValid)
            {
                CrewDebugLog.Warn(Phase, "Cannot dump sync state; visual or logic agent is missing.");
                return;
            }

            Vector3 proxyLocal = _logicAgent.CurrentLocalPosition;
            Vector3 visualLocal = _visualAgent.VisualRoot.transform.localPosition;
            CrewDebugLog.Ok(Phase,
                "proxyLocal=" + Format(proxyLocal)
                + ", visualLocal=" + Format(visualLocal)
                + ", syncError=" + Vector3.Distance(proxyLocal, visualLocal).ToString("0.0000"));
        }

        internal void SetRotationOverride(Quaternion localRotation)
        {
            _rotationOverride = localRotation;
            _hasRotationOverride = true;
            CrewDebugLog.Ok(Phase, "Applied visual rotation override.");
        }

        internal void SetPoseOverride(Vector3 localPosition, Quaternion localRotation)
        {
            _positionOverride = localPosition;
            _rotationOverride = localRotation;
            _hasPoseOverride = true;
            _hasRotationOverride = true;
            CrewDebugLog.Ok(Phase, "Applied visual pose override.");
        }

        internal void ClearPoseOverride()
        {
            if (!_hasPoseOverride)
                return;

            _hasPoseOverride = false;
            CrewDebugLog.Ok(Phase, "Cleared visual pose override.");
        }

        internal void ClearRotationOverride()
        {
            if (!_hasRotationOverride)
                return;

            _hasRotationOverride = false;
            CrewDebugLog.Ok(Phase, "Cleared visual rotation override.");
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }
    }
}
