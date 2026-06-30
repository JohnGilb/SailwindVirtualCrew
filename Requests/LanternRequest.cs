using UnityEngine;

namespace SailwindVirtualCrew
{
    public class LanternRequest
    {
        private const float PositioningGraceSeconds = 6f;

        private readonly ShipItemLight lantern;
        private readonly bool lightState;
        private float positioningStartTime;
        private float positioningTimeTotal;
        private float workStartTime;
        private float workDuration;
        private bool concretePositioning;

        public Crewman AssignedCrewman { get; private set; }
        public WorkRequestStatus Status { get; private set; } = WorkRequestStatus.Open;
        public ShipItemLight Lantern => lantern;
        public string CommandName => lightState ? "Light Lantern" : "Extinguish Lantern";
        public string LanternName => lantern ? lantern.name : "lantern";

        public LanternRequest(ShipItemLight lantern, bool lightState)
        {
            this.lantern = lantern;
            this.lightState = lightState;
        }

        public bool IsDone()
        {
            return !lantern
                || !lantern.sold
                || !CrewLanternService.IsServiceableLanternTarget(lantern)
                || (lightState && lantern.health <= 0f)
                || CrewLanternService.IsLanternLit(lantern) == lightState;
        }

        public void BeginPositioning(Crewman crewman)
        {
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            positioningTimeTotal = Mathf.Max(1f, 7f - crewman.Dexterity);
            positioningStartTime = Time.time;
            Status = WorkRequestStatus.Positioning;

            concretePositioning = false;
            if (CrewLanternService.TryGetLanternServicePose(lantern, out var localPosition, out var localRotation))
            {
                concretePositioning = CrewNavigationCoordinator.Instance.TryBeginRolePositioning(
                    this, crewman, localPosition, localRotation, CommandName.ToLowerInvariant());
            }
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
            if (IsDone())
            {
                Complete();
                return;
            }

            Status = WorkRequestStatus.InProgress;
            workStartTime = Time.time;
            workDuration = Mathf.Max(1f, 4f - AssignedCrewman.Dexterity * 0.35f);
        }

        public void Tick()
        {
            if (Status != WorkRequestStatus.InProgress)
                return;

            if (Time.time < workStartTime + workDuration)
                return;

            CrewLanternService.SetLight(lantern, lightState);
            Complete();
        }

        public float GetProgress()
        {
            return workDuration <= 0f
                ? 100f
                : Mathf.Clamp01((Time.time - workStartTime) / workDuration) * 100f;
        }

        public void Cancel()
        {
            Complete();
        }

        private void Complete()
        {
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
            CrewNavigationCoordinator.Instance.Cancel(this);
            CrewNavigationCoordinator.Instance.Complete(this);
        }
    }
}
