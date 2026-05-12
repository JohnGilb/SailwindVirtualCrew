namespace SailwindVirtualCrew
{
    public class SleepRequest
    {
        public Crewman           AssignedCrewman { get; }
        public WorkRequestStatus Status          { get; set; } = WorkRequestStatus.Open;

        // Full rest in 8 in-game hours (480 in-game minutes), regardless of MaxStamina.
        private float RestoreRatePerMinute => AssignedCrewman.MaxStamina / 480f;

        public SleepRequest(Crewman crewman)
        {
            AssignedCrewman       = crewman;
            crewman.CurrentTask   = this;
        }

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
            AssignedCrewman.CurrentTask = null;
        }
    }
}
