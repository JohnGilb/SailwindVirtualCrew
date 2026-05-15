using UnityEngine;

namespace SailwindVirtualCrew
{
    public class BailRequest
    {
        public Crewman          AssignedCrewman { get; set; }
        public WorkRequestStatus Status          { get; set; } = WorkRequestStatus.Open;

        private readonly BoatDamage boatDamage;
        private float phaseStart;
        private float phaseDuration;
        private bool  inPickupPhase;

        public float  UnitsPerScoop { get; }
        public string ToolName      => UnitsPerScoop >= 10f ? "bucket" : "mug";
        private const float DoneThreshold   = 0.05f;

        public BailRequest(BoatDamage damage, float unitsPerScoop)
        {
            boatDamage   = damage;
            UnitsPerScoop = unitsPerScoop;
        }

        public void Begin(Crewman crewman)
        {
            AssignedCrewman       = crewman;
            crewman.CurrentTask   = this;
            Status                = WorkRequestStatus.InProgress;
            StartPickup();
        }

        public void Tick()
        {
            if (Status != WorkRequestStatus.InProgress) return;

            if (boatDamage == null || boatDamage.waterLevel <= DoneThreshold)
            {
                Complete();
                return;
            }

            if (Time.time < phaseStart + phaseDuration) return;

            if (inPickupPhase)
            {
                float removal = boatDamage.waterUnitsCapacity > 0f
                    ? UnitsPerScoop / boatDamage.waterUnitsCapacity
                    : 0f;
                boatDamage.waterLevel = Mathf.Max(0f, boatDamage.waterLevel - removal);
                if (boatDamage.waterLevel <= DoneThreshold)
                    Complete();
                else
                    StartThrow();
            }
            else
            {
                StartPickup();
            }
        }

        // 0 = just started phase, 100 = phase complete.
        public float GetProgress() =>
            phaseDuration <= 0f ? 100f
                : Mathf.Clamp01((Time.time - phaseStart) / phaseDuration) * 100f;

        public bool IsPickingUp => inPickupPhase && Status == WorkRequestStatus.InProgress;

        private void StartPickup()
        {
            inPickupPhase  = true;
            phaseStart     = Time.time;
            phaseDuration  = Mathf.Max(1f, 8f - AssignedCrewman.Dexterity);
        }

        private void StartThrow()
        {
            inPickupPhase  = false;
            phaseStart     = Time.time;
            phaseDuration  = Mathf.Max(1f, 6f - AssignedCrewman.Dexterity);
        }

        private void Complete()
        {
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
        }
    }
}
