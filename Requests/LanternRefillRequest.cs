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
        private CrateInventory originCrate;
        private Vector3 originLocalPosition;
        private Quaternion originLocalRotation;
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
                || !CrewLanternService.IsServiceableLanternTarget(lantern)
                || !CrewLanternService.CanAcceptFuel(lantern, fuel);
        }

        public void Begin(Crewman crewman)
        {
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            Status = WorkRequestStatus.Positioning;
            CrewLanternService.TraceRefill("Begin request crew='" + crewman.Name + "' lantern='" + LanternName + "' fuel='" + FuelName + "'.");
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
            {
                CrewLanternService.TraceRefill("Carry pose skipped: no owner world pose for fuel='" + FuelName + "' phase=" + phase + ".");
                return;
            }

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
            CrewLanternService.TraceRefill("Cancel request lantern='" + LanternName + "' fuel='" + FuelName + "' phase=" + phase + " carryingFuel=" + carryingFuel + ".");
            if (carryingFuel)
                ReturnFuelToOrigin();
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
                CrewLanternService.TraceRefill("Begin position to fuel='" + FuelName + "' concrete=" + concretePositioning + " local=" + FormatVector(localPosition) + ".");
            }
            else
            {
                CrewLanternService.TraceRefill("Begin position to fuel fallback: fuel='" + FuelName + "' boat=" + (boat ? boat.name : "<null>") + ".");
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
                CrewLanternService.TraceRefill("PickUpFuel aborted: request already done lantern='" + LanternName + "' fuel='" + FuelName + "'.");
                Complete();
                return;
            }

            if (!CrewLanternService.TryGetLanternServicePose(lantern, out var localPosition, out var localRotation))
            {
                CrewLanternService.TraceRefill("PickUpFuel aborted: no lantern service pose lantern='" + LanternName + "'.");
                Complete();
                return;
            }

            CaptureFuelOrigin();
            CrewLanternService.DetachFuelFromCrate(fuel);
            carryingFuel = true;
            CrewLanternService.TraceRefill("Picked up fuel='" + FuelName + "' originCrate=" + (originCrate ? originCrate.name : "<none>") + " lantern='" + LanternName + "'.");
            if (CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out _))
                carryHeightOffset = fuel.transform.position.y - crewPosition.y;
            else
                carryHeightOffset = 0.6f;

            phase = Phase.ToLantern;
            positioningTimeTotal = Mathf.Max(1f, 7f - AssignedCrewman.Dexterity);
            positioningStartTime = Time.time;
            concretePositioning = CrewNavigationCoordinator.Instance.TryRetargetRolePositioning(
                this, localPosition, localRotation, "lantern refill target='" + LanternName + "'");
            CrewLanternService.TraceRefill("Retarget to lantern='" + LanternName + "' concrete=" + concretePositioning + " local=" + FormatVector(localPosition) + ".");
            if (!concretePositioning)
                Cancel();
        }

        private void RefillLantern()
        {
            if (!CrewLanternService.NeedsRefill(lantern)
                || !CrewLanternService.IsServiceableLanternTarget(lantern)
                || !CrewLanternService.IsCompatibleFuel(lantern, fuel))
            {
                CrewLanternService.TraceRefill("Refill aborted before loading lantern='" + LanternName + "' fuel='" + FuelName
                    + "' needsRefill=" + CrewLanternService.NeedsRefill(lantern)
                    + " serviceable=" + CrewLanternService.IsServiceableLanternTarget(lantern)
                    + " compatibleFuel=" + CrewLanternService.IsCompatibleFuel(lantern, fuel) + ".");
                ReturnFuelToOrigin();
                Complete();
                return;
            }

            carryingFuel = false;
            bool loaded = CrewLanternService.LoadFuel(lantern, fuel);
            CrewLanternService.TraceRefill("Refill load result lantern='" + LanternName + "' fuel='" + FuelName + "' loaded=" + loaded + ".");
            if (!loaded)
                ReturnFuelToOrigin();
            Complete();
        }

        private void CaptureFuelOrigin()
        {
            originCrate = CrewLanternService.FindContainingCrate(fuel);
            var boat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (fuel && boat)
            {
                originLocalPosition = boat.InverseTransformPoint(fuel.transform.position);
                originLocalRotation = Quaternion.Inverse(boat.rotation) * fuel.transform.rotation;
                CrewLanternService.TraceRefill("Captured fuel origin fuel='" + FuelName + "' crate=" + (originCrate ? originCrate.name : "<none>") + " local=" + FormatVector(originLocalPosition) + ".");
            }
            else
            {
                originLocalPosition = Vector3.zero;
                originLocalRotation = Quaternion.identity;
                CrewLanternService.TraceRefill("Captured fuel origin fallback fuel='" + FuelName + "' crate=" + (originCrate ? originCrate.name : "<none>") + ".");
            }
        }

        private void ReturnFuelToOrigin()
        {
            carryingFuel = false;
            if (!fuel)
                return;

            if (originCrate)
            {
                originCrate.InsertItem(fuel);
                CrewLanternService.TraceRefill("Returned fuel='" + FuelName + "' to origin crate='" + originCrate.name + "'.");
                if (CrateInventoryUI.instance
                    && CrateInventoryUI.instance.showingUI
                    && CrateInventoryUI.instance.currentCrate == originCrate)
                    CrateInventoryUI.instance.RefreshButtons();
                return;
            }

            var boat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (boat)
            {
                fuel.transform.position = boat.TransformPoint(originLocalPosition);
                fuel.transform.rotation = boat.rotation * originLocalRotation;
                CrewLanternService.TraceRefill("Returned fuel='" + FuelName + "' to origin local pose=" + FormatVector(originLocalPosition) + ".");
            }
            else
            {
                CrewLanternService.TraceRefill("Could not return fuel='" + FuelName + "': no active boat and no origin crate.");
            }
        }

        private void Complete()
        {
            CrewLanternService.TraceRefill("Complete request lantern='" + LanternName + "' fuel='" + FuelName + "' phase=" + phase + ".");
            carryingFuel = false;
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
            CrewNavigationCoordinator.Instance.Cancel(this);
            CrewNavigationCoordinator.Instance.Complete(this);
        }

        private static string FormatVector(Vector3 value)
        {
            return "(" + value.x.ToString("0.##") + "," + value.y.ToString("0.##") + "," + value.z.ToString("0.##") + ")";
        }
    }
}
