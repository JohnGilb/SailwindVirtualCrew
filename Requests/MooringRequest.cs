using UnityEngine;

namespace SailwindVirtualCrew
{
    public class MooringRequest
    {
        public MooringSide Side { get; }
        public PickupableBoatMooringRope TargetRope { get; }
        public Crewman AssignedCrewman { get; set; }
        public WorkRequestStatus Status { get; set; } = WorkRequestStatus.Open;

        public string CommandName =>
            "Moor " + Side + " Line" + (TargetRope ? " (" + TargetRope.name + ")" : "");

        private const float PositioningGraceSeconds = 6f;
        private MooringRopeInfo ropeInfo;
        private bool concretePositioning;
        private float positioningStartTime;
        private float positioningTimeTotal;
        private float workStartTime;
        private float workDuration;

        public MooringRequest(MooringSide side, PickupableBoatMooringRope targetRope)
        {
            Side = side;
            TargetRope = targetRope;
        }

        internal bool RefreshTarget()
        {
            if (!TargetRope || TargetRope.IsMoored())
                return false;

            if (!MooringLocator.TryFindRope(TargetRope, out ropeInfo))
                return false;

            return ropeInfo.Side == Side && !ropeInfo.IsMoored;
        }

        public void BeginPositioning(Crewman crewman)
        {
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            if (!RefreshTarget())
            {
                Complete();
                return;
            }

            positioningTimeTotal = Mathf.Max(0f, 7f - crewman.Dexterity);
            positioningStartTime = Time.time;
            Status = WorkRequestStatus.Positioning;

            Vector3 destinationLocal = ropeInfo.AnchorLocal;
            Quaternion rotation = GetWorkLocalRotation(destinationLocal);
            concretePositioning = CrewNavigationCoordinator.Instance.TryBeginRolePositioning(
                this,
                crewman,
                destinationLocal,
                rotation,
                CommandName.ToLowerInvariant());
        }

        public bool IsPositioningComplete()
        {
            if (Status != WorkRequestStatus.Positioning)
                return false;

            if (concretePositioning)
                return CrewNavigationCoordinator.Instance.IsPositioningComplete(this);

            return Time.time >= positioningStartTime + positioningTimeTotal;
        }

        public bool IsPositioningTimedOut()
        {
            return Status == WorkRequestStatus.Positioning
                && Time.time >= positioningStartTime + positioningTimeTotal + PositioningGraceSeconds;
        }

        public float GetPositioningProgress()
        {
            if (concretePositioning)
                return CrewNavigationCoordinator.Instance.GetPositioningProgress(this);

            return positioningTimeTotal <= 0f
                ? 0f
                : Mathf.Clamp01(1f - (Time.time - positioningStartTime) / positioningTimeTotal) * 100f;
        }

        public void Begin()
        {
            if (concretePositioning)
            {
                CrewNavigationCoordinator.Instance.Complete(this);
                concretePositioning = false;
            }

            if (!RefreshTarget())
            {
                Complete();
                return;
            }

            Status = WorkRequestStatus.InProgress;
            workStartTime = Time.time;
            workDuration = Mathf.Max(1.5f, 5.5f - AssignedCrewman.Dexterity * 0.55f);
        }

        public void Tick()
        {
            if (Status != WorkRequestStatus.InProgress)
                return;

            if (Time.time < workStartTime + workDuration)
                return;

            bool moored = false;
            if (RefreshTarget() && MooringLocator.TryFindClosestDock(TargetRope, Side, out var dock))
            {
                var dockButton = dock.Mooring;
                if (dockButton && dockButton.spring != null && dockButton.spring.connectedBody == null)
                {
                    TargetRope.MoorTo(dockButton);
                    moored = true;
                }
            }

            CrewDebugLog.Ok("Mooring",
                "Completed " + CommandName
                + " crew='" + (AssignedCrewman != null ? AssignedCrewman.Name : "none")
                + "' moored=" + moored);
            Complete();
        }

        public float GetProgress()
        {
            return workDuration <= 0f
                ? 100f
                : Mathf.Clamp01((Time.time - workStartTime) / workDuration) * 100f;
        }

        public void CancelPositioning()
        {
            if (!concretePositioning)
                return;

            CrewNavigationCoordinator.Instance.Cancel(this);
            concretePositioning = false;
        }

        private Quaternion GetWorkLocalRotation(Vector3 destinationLocal)
        {
            if (MooringLocator.TryFindClosestDock(TargetRope, Side, out var dock))
            {
                Vector3 direction = dock.LocalPosition - destinationLocal;
                direction.y = 0f;
                if (direction.sqrMagnitude >= 0.001f)
                    return Quaternion.LookRotation(direction.normalized, Vector3.up);
            }

            return Quaternion.LookRotation(Side == MooringSide.Port ? Vector3.forward : Vector3.back, Vector3.up);
        }

        private void Complete()
        {
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
        }
    }
}
