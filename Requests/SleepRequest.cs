using UnityEngine;

namespace SailwindVirtualCrew
{
    public class SleepRequest
    {
        public Crewman           AssignedCrewman { get; }
        public WorkRequestStatus Status          { get; set; } = WorkRequestStatus.Open;
        public UnityEngine.Component AssignedBed { get; private set; }

        private const float PositioningGraceSeconds = 6f;
        private float positioningStartTime;
        private float positioningTimeTotal;

        // Full rest in 8 in-game hours (480 in-game minutes), regardless of MaxStamina.
        private float RestoreRatePerMinute => AssignedCrewman.MaxStamina / 480f;

        public SleepRequest(Crewman crewman)
        {
            AssignedCrewman       = crewman;
            crewman.CurrentTask   = this;
        }

        public void BeginPositioning(UnityEngine.Component bed)
        {
            AssignedBed = bed;
            positioningTimeTotal = Mathf.Max(0f, 7f - AssignedCrewman.Dexterity);
            positioningStartTime = Time.time;
            Status = WorkRequestStatus.Positioning;
        }

        public float GetPositioningProgress() =>
            CrewNavigationCoordinator.Instance.GetPositioningProgress(this);

        public bool IsPositioningTimedOut() =>
            Status == WorkRequestStatus.Positioning
         && Time.time >= positioningStartTime + positioningTimeTotal + PositioningGraceSeconds;

        public void Begin()
        {
            Status = WorkRequestStatus.InProgress;
        }

        public void Tick(float deltaMinutes)
        {
            if (Status != WorkRequestStatus.InProgress) return;
            AssignedCrewman.RestoreStamina(RestoreRatePerMinute * deltaMinutes);
            if (AssignedCrewman.CurrentStamina >= AssignedCrewman.MaxStamina)
                Complete();
        }

        // 0 = exhausted, 100 = fully rested.
        public float GetProgress() =>
            (AssignedCrewman.CurrentStamina / AssignedCrewman.MaxStamina) * 100f;

        private void Complete()
        {
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
        }
    }
}
