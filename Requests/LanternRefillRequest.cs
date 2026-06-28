using UnityEngine;

namespace SailwindVirtualCrew
{
    public class LanternRefillRequest
    {
        private const float PositioningGraceSeconds = 6f;

        private readonly ShipItemLight lantern;
        private readonly ShipItemLanternFuel fuel;
        private float positioningStartTime;
        private float positioningTimeTotal;
        private Phase phase = Phase.ToFuel;
        private bool concretePositioning;
        private bool carryingFuel;
        private float carryHeightOffset;

        private enum Phase
        {
            ToFuel,
            ToLantern
        }

        public Crewman AssignedCrewman { get; private set; }
        public WorkRequestStatus Status { get; private set; } = WorkRequestStatus.Open;
        public ShipItemLight Lantern => lantern;
        public ShipItemLanternFuel Fuel => fuel;
        public string CommandName => "Refill Lantern";
        public string LanternName => lantern ? lantern.name : "lantern";
        public string FuelName => fuel ? fuel.name : "fuel";

        public LanternRefillRequest(ShipItemLight lantern, ShipItemLanternFuel fuel)
        {
            this.lantern = lantern;
            this.fuel = fuel;
        }

        public bool IsDone()
        {
            return !CrewLanternService.NeedsRefill(lantern)
                || !CrewLanternService.CanAcceptFuel(lantern, fuel);
        }

        public void Begin(Crewman crewman)
        {
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            Status = WorkRequestStatus.Positioning;
            BeginPositionToFuel();
        }

        public void Tick()
        {
            if (Status != WorkRequestStatus.Positioning)
                return;

            if (!IsPositioningComplete() && !IsPositioningTimedOut())
                return;

            if (phase == Phase.ToFuel)
                PickUpFuelAndGoToLantern();
            else
                RefillLantern();
        }

        public void UpdateFrame()
        {
            if (!carryingFuel || !fuel)
                return;

            if (!CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out var crewRotation))
                return;

            fuel.transform.position = crewPosition + crewRotation * Vector3.forward * 0.65f + Vector3.up * carryHeightOffset;
            fuel.transform.rotation = crewRotation;
        }

        public float GetPositioningProgress()
        {
            if (concretePositioning)
                return CrewNavigationCoordinator.Instance.GetPositioningProgress(this);

            return positioningTimeTotal <= 0f
                ? 0f
                : Mathf.Clamp01(1f - (Time.time - positioningStartTime) / positioningTimeTotal) * 100f;
        }

        public void Cancel()
        {
            Complete();
        }

        private void BeginPositionToFuel()
        {
            positioningTimeTotal = Mathf.Max(1f, 7f - AssignedCrewman.Dexterity);
            positioningStartTime = Time.time;
            concretePositioning = false;

            var boat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (fuel && boat)
            {
                Vector3 localPosition = boat.InverseTransformPoint(fuel.transform.position);
                Quaternion localRotation = Quaternion.Inverse(boat.rotation) * fuel.transform.rotation;
                concretePositioning = CrewNavigationCoordinator.Instance.TryBeginRolePositioning(
                    this, AssignedCrewman, localPosition, localRotation, "lantern fuel source='" + fuel.name + "'");
            }
        }

        private bool IsPositioningComplete()
        {
            if (concretePositioning)
                return CrewNavigationCoordinator.Instance.IsPositioningComplete(this);

            return Time.time >= positioningStartTime + positioningTimeTotal;
        }

        private bool IsPositioningTimedOut()
        {
            return Time.time >= positioningStartTime + positioningTimeTotal + PositioningGraceSeconds;
        }

        private void PickUpFuelAndGoToLantern()
        {
            if (IsDone())
            {
                Complete();
                return;
            }

            CrewLanternService.DetachFuelFromCrate(fuel);
            carryingFuel = true;
            if (CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out _))
                carryHeightOffset = fuel.transform.position.y - crewPosition.y;
            else
                carryHeightOffset = 0.6f;

            phase = Phase.ToLantern;
            positioningTimeTotal = Mathf.Max(1f, 7f - AssignedCrewman.Dexterity);
            positioningStartTime = Time.time;
            concretePositioning = false;
            if (CrewLanternService.TryGetLanternServicePose(lantern, out var localPosition, out var localRotation))
            {
                concretePositioning = CrewNavigationCoordinator.Instance.TryRetargetRolePositioning(
                    this, localPosition, localRotation, "lantern refill target='" + LanternName + "'");
            }
        }

        private void RefillLantern()
        {
            carryingFuel = false;
            CrewLanternService.LoadFuel(lantern, fuel);
            Complete();
        }

        private void Complete()
        {
            carryingFuel = false;
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
            CrewNavigationCoordinator.Instance.Cancel(this);
            CrewNavigationCoordinator.Instance.Complete(this);
        }
    }
}
