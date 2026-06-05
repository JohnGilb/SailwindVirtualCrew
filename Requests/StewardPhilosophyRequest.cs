using UnityEngine;

namespace SailwindVirtualCrew
{
    public class StewardPhilosophyRequest
    {
        private const float PositioningGraceSeconds = 6f;

        private float positioningStartTime;
        private float positioningTimeTotal;

        public Crewman AssignedCrewman { get; private set; }
        public WorkRequestStatus Status { get; private set; } = WorkRequestStatus.Open;

        public void Begin(Crewman crewman)
        {
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            positioningTimeTotal = Mathf.Max(1f, 7f - crewman.Dexterity);
            positioningStartTime = Time.time;
            Status = WorkRequestStatus.Positioning;

            if (!StewardRequestNavigation.TryBeginNearPlayer(this, crewman, "steward philosophy player"))
                Complete();
        }

        public void Tick()
        {
            if (Status == WorkRequestStatus.Positioning)
            {
                if (CrewNavigationCoordinator.Instance.IsPositioningComplete(this)
                    || Time.time >= positioningStartTime + positioningTimeTotal + PositioningGraceSeconds)
                    BeginDiscussion();
            }
            else if (Status == WorkRequestStatus.InProgress && !PlayerWaitingState.IsOwner(this))
            {
                Complete();
            }
        }

        public void UpdateFrame()
        {
            if (Status != WorkRequestStatus.InProgress)
                return;

            StewardRequestNavigation.FacePlayer(this);
        }

        public void Cancel()
        {
            PlayerWaitingState.End(this);
            Complete();
        }

        private void BeginDiscussion()
        {
            CrewNavigationCoordinator.Instance.TryStopOwnerMotion(this);
            Status = WorkRequestStatus.InProgress;
            if (!PlayerWaitingState.Begin(this))
                Complete();
        }

        private void Complete()
        {
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
            CrewNavigationCoordinator.Instance.Complete(this);
        }
    }
}
