using UnityEngine;

namespace SailwindVirtualCrew
{
    public class StewardWaterRequest
    {
        private const float PositioningGraceSeconds = 6f;
        private const float WaterLiquidIndex = 1f;

        private readonly ShipItemBottle barrel;
        private float positioningStartTime;
        private float positioningTimeTotal;
        private Phase phase = Phase.ToSource;
        private bool carryingWater;

        private enum Phase
        {
            ToSource,
            ToPlayer
        }

        public Crewman AssignedCrewman { get; private set; }
        public WorkRequestStatus Status { get; private set; } = WorkRequestStatus.Open;
        public string SourceName => barrel ? barrel.name : "water barrel";

        public StewardWaterRequest(ShipItemBottle barrel)
        {
            this.barrel = barrel;
        }

        public void Begin(Crewman crewman)
        {
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            positioningTimeTotal = Mathf.Max(1f, 7f - crewman.Dexterity);
            positioningStartTime = Time.time;
            Status = WorkRequestStatus.Positioning;

            if (barrel && CrewBoatContextResolver.TryResolveBoatTransforms(out _, out var worldBoat))
            {
                Vector3 localPosition = worldBoat.InverseTransformPoint(barrel.transform.position);
                Quaternion localRotation = Quaternion.Inverse(worldBoat.rotation) * barrel.transform.rotation;
                CrewNavigationCoordinator.Instance.TryBeginRolePositioning(
                    this, crewman, localPosition, localRotation, "steward water source='" + barrel.name + "'");
            }
        }

        public void Tick()
        {
            if (Status != WorkRequestStatus.Positioning)
                return;

            if (!IsPositioningComplete() && !IsPositioningTimedOut())
                return;

            if (phase == Phase.ToSource)
                TakeWaterAndGoToPlayer();
            else
                FeedPlayer();
        }

        public float GetPositioningProgress()
        {
            if (CrewNavigationCoordinator.Instance.IsPositioningComplete(this))
                return 0f;

            return positioningTimeTotal <= 0f
                ? 0f
                : Mathf.Clamp01(1f - (Time.time - positioningStartTime) / positioningTimeTotal) * 100f;
        }

        private bool IsPositioningComplete()
        {
            return CrewNavigationCoordinator.Instance.IsPositioningComplete(this)
                || Time.time >= positioningStartTime + positioningTimeTotal;
        }

        private bool IsPositioningTimedOut()
        {
            return Time.time >= positioningStartTime + positioningTimeTotal + PositioningGraceSeconds;
        }

        private void TakeWaterAndGoToPlayer()
        {
            if (!barrel || barrel.health < 1f || Mathf.RoundToInt(barrel.amount) != Mathf.RoundToInt(WaterLiquidIndex))
            {
                Complete();
                return;
            }

            barrel.health -= 1f;
            carryingWater = true;
            if (barrel.health <= 0f)
                barrel.EmptyBottle();
            else
                barrel.UpdateLookText();
            if (barrel.itemRigidbodyC != null)
                barrel.itemRigidbodyC.UpdateMass();

            phase = Phase.ToPlayer;
            positioningStartTime = Time.time;
            if (!StewardRequestNavigation.TryRetargetNearPlayer(this, "steward water player"))
                Complete();
        }

        private void FeedPlayer()
        {
            PlayerNeeds.water += Liquids.GetLiquidHydration(WaterLiquidIndex);
            if (PlayerNeeds.instance != null)
                PlayerNeeds.instance.eatCooldown = 0.5f;
            PlayerNeedsUI.instance?.ShowFeedback();
            Refs.playerMouthCol?.PlayDrinkSound();
            Complete();
        }

        public void Cancel()
        {
            if (carryingWater && barrel)
            {
                if (barrel.health <= 0f)
                    barrel.amount = WaterLiquidIndex;
                barrel.health = Mathf.Min(barrel.GetCapacity(), barrel.health + 1f);
                barrel.UpdateLookText();
                if (barrel.itemRigidbodyC != null)
                    barrel.itemRigidbodyC.UpdateMass();
            }

            Complete();
        }

        private void Complete()
        {
            carryingWater = false;
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
            CrewNavigationCoordinator.Instance.Complete(this);
        }
    }
}
