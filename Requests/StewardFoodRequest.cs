using UnityEngine;

namespace SailwindVirtualCrew
{
    public class StewardFoodRequest
    {
        private const float PositioningGraceSeconds = 6f;

        private readonly ShipItemFood food;
        private Transform originBoat;
        private Vector3 returnLocalPosition;
        private Quaternion returnLocalRotation;
        private float positioningStartTime;
        private float positioningTimeTotal;
        private Phase phase = Phase.ToSource;

        private Collider itemCollider;
        private bool itemColliderWasEnabled;
        private Collider[] itemChildColliders;
        private bool[] itemChildColliderStates;
        private ItemRigidbody itemRigidbody;
        private bool itemRigidbodyWasEnabled;
        private bool itemRigidbodyDisableColWasSet;
        private Rigidbody itemBody;
        private bool itemBodyWasKinematic;
        private bool itemBodyDetectCollisionsWasEnabled;
        private Collider[] itemRigidbodyColliders;
        private bool[] itemRigidbodyColliderStates;
        private BoatMass boatMass;
        private bool removedFromBoatMass;
        private float carryHeightOffset;

        private enum Phase
        {
            ToSource,
            ToPlayer
        }

        public Crewman AssignedCrewman { get; private set; }
        public WorkRequestStatus Status { get; private set; } = WorkRequestStatus.Open;
        public ShipItemFood Food => food;
        public string SourceName => food ? food.name : "food";

        public StewardFoodRequest(ShipItemFood food)
        {
            this.food = food;
            originBoat = food ? food.currentActualBoat : null;
        }

        public void Begin(Crewman crewman)
        {
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            positioningTimeTotal = Mathf.Max(1f, 7f - crewman.Dexterity);
            positioningStartTime = Time.time;
            Status = WorkRequestStatus.Positioning;

            ResolveOriginBoat();
            if (food && originBoat)
            {
                Vector3 localPosition = originBoat.InverseTransformPoint(food.transform.position);
                Quaternion localRotation = Quaternion.Inverse(originBoat.rotation) * food.transform.rotation;
                CrewNavigationCoordinator.Instance.TryBeginRolePositioning(
                    this, crewman, localPosition, localRotation, "steward food source='" + food.name + "'");
            }
        }

        public void Tick()
        {
            if (Status != WorkRequestStatus.Positioning)
                return;

            if (!IsPositioningComplete() && !IsPositioningTimedOut())
                return;

            if (phase == Phase.ToSource)
                PickUpFoodAndGoToPlayer();
            else
                FeedPlayer();
        }

        public void UpdateFrame()
        {
            if (Status != WorkRequestStatus.Positioning || phase != Phase.ToPlayer)
                return;

            UpdateCarriedFoodPose();
        }

        public void Cancel()
        {
            if (phase == Phase.ToPlayer && food)
                ReturnFoodToOrigin();
            RestoreFoodCollision();
            RestoreFoodToBoatMass();
            Complete();
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

        private void PickUpFoodAndGoToPlayer()
        {
            if (!IsFoodStillUsable())
            {
                Complete();
                return;
            }

            ResolveOriginBoat();
            returnLocalPosition = originBoat ? originBoat.InverseTransformPoint(food.transform.position) : Vector3.zero;
            returnLocalRotation = originBoat ? Quaternion.Inverse(originBoat.rotation) * food.transform.rotation : food.transform.rotation;
            if (VirtualCrewManager.TryGetUnsealedFoodCrate(food, out var crate))
            {
                crate.WithdrawItem(food);
                food.transform.position = crate.transform.position;
                food.transform.rotation = crate.transform.rotation;
            }
            DisableFoodCollision();
            RemoveFoodFromBoatMass();

            if (CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out _))
                carryHeightOffset = food.transform.position.y - crewPosition.y;
            else
                carryHeightOffset = 0.6f;

            phase = Phase.ToPlayer;
            positioningStartTime = Time.time;
            if (!StewardRequestNavigation.TryRetargetNearPlayer(this, "steward food player"))
                Cancel();
        }

        private void FeedPlayer()
        {
            if (!food)
            {
                Complete();
                return;
            }

            RestoreFoodToBoatMass();
            RestoreFoodCollision();
            if (PlayerNeeds.instance != null)
                PlayerNeeds.instance.eatCooldown = 0f;
            food.EatFood();
            Complete();
        }

        private void UpdateCarriedFoodPose()
        {
            if (!food || !CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out var crewRotation))
                return;

            Vector3 position = crewPosition + crewRotation * Vector3.forward * 0.65f + Vector3.up * carryHeightOffset;
            food.transform.position = position;
            food.transform.rotation = crewRotation;
            SyncSuspendedItemRigidbody(position, crewRotation);
        }

        private bool IsFoodStillUsable()
        {
            if (!food || !food.sold || food.held != null)
                return false;

            var good = food.GetComponent<Good>();
            return good == null || good.GetMissionIndex() == -1;
        }

        private void DisableFoodCollision()
        {
            itemCollider = food ? food.GetComponent<Collider>() : null;
            if (itemCollider != null)
            {
                itemColliderWasEnabled = itemCollider.enabled;
                itemCollider.enabled = false;
            }

            CaptureAndDisableItemChildColliders();

            itemRigidbody = food ? food.GetItemRigidbody() : null;
            if (itemRigidbody != null)
            {
                itemRigidbodyWasEnabled = itemRigidbody.enabled;
                itemRigidbodyDisableColWasSet = itemRigidbody.disableCol;
                itemRigidbody.disableCol = true;
                itemRigidbody.ToggleCollider(false);
                CaptureAndDisableItemRigidbodyPhysics();
                itemRigidbody.enabled = false;
            }
        }

        private void RestoreFoodCollision()
        {
            if (itemCollider != null)
                itemCollider.enabled = itemColliderWasEnabled;

            if (itemRigidbody != null)
            {
                if (food)
                    SyncSuspendedItemRigidbody(food.transform.position, food.transform.rotation);
                RestoreItemRigidbodyPhysics();
                itemRigidbody.disableCol = itemRigidbodyDisableColWasSet;
                itemRigidbody.enabled = itemRigidbodyWasEnabled;
                itemRigidbody.ToggleCollider(!itemRigidbody.disableCol);
            }

            RestoreItemChildColliders();
        }

        private void CaptureAndDisableItemChildColliders()
        {
            if (!food)
                return;

            itemChildColliders = food.GetComponentsInChildren<Collider>(true);
            itemChildColliderStates = new bool[itemChildColliders.Length];
            for (int i = 0; i < itemChildColliders.Length; i++)
            {
                if (itemChildColliders[i] == null)
                    continue;

                itemChildColliderStates[i] = itemChildColliders[i].enabled;
                itemChildColliders[i].enabled = false;
            }
        }

        private void RestoreItemChildColliders()
        {
            if (itemChildColliders == null || itemChildColliderStates == null)
                return;

            for (int i = 0; i < itemChildColliders.Length && i < itemChildColliderStates.Length; i++)
                if (itemChildColliders[i] != null)
                    itemChildColliders[i].enabled = itemChildColliderStates[i];

            itemChildColliders = null;
            itemChildColliderStates = null;
        }

        private void CaptureAndDisableItemRigidbodyPhysics()
        {
            itemBody = itemRigidbody.GetBody();
            if (itemBody != null)
            {
                itemBodyWasKinematic = itemBody.isKinematic;
                itemBodyDetectCollisionsWasEnabled = itemBody.detectCollisions;
                itemBody.isKinematic = true;
                itemBody.detectCollisions = false;
                itemBody.velocity = Vector3.zero;
                itemBody.angularVelocity = Vector3.zero;
            }

            itemRigidbodyColliders = itemRigidbody.GetComponentsInChildren<Collider>(true);
            itemRigidbodyColliderStates = new bool[itemRigidbodyColliders.Length];
            for (int i = 0; i < itemRigidbodyColliders.Length; i++)
            {
                if (itemRigidbodyColliders[i] == null)
                    continue;

                itemRigidbodyColliderStates[i] = itemRigidbodyColliders[i].enabled;
                itemRigidbodyColliders[i].enabled = false;
            }
        }

        private void RestoreItemRigidbodyPhysics()
        {
            if (itemRigidbodyColliders != null && itemRigidbodyColliderStates != null)
            {
                for (int i = 0; i < itemRigidbodyColliders.Length && i < itemRigidbodyColliderStates.Length; i++)
                    if (itemRigidbodyColliders[i] != null)
                        itemRigidbodyColliders[i].enabled = itemRigidbodyColliderStates[i];
            }

            if (itemBody != null)
            {
                itemBody.isKinematic = itemBodyWasKinematic;
                itemBody.detectCollisions = itemBodyDetectCollisionsWasEnabled;
                itemBody.velocity = Vector3.zero;
                itemBody.angularVelocity = Vector3.zero;
            }

            itemRigidbodyColliders = null;
            itemRigidbodyColliderStates = null;
            itemBody = null;
        }

        private void SyncSuspendedItemRigidbody(Vector3 position, Quaternion rotation)
        {
            if (itemBody == null)
                return;

            if (food && food.currentActualBoat && food.currentWalkCol)
            {
                Vector3 boatLocalPosition = food.currentActualBoat.InverseTransformPoint(position);
                Quaternion boatLocalRotation = Quaternion.Inverse(food.currentActualBoat.rotation) * rotation;
                itemBody.transform.position = food.currentWalkCol.TransformPoint(boatLocalPosition);
                itemBody.transform.rotation = food.currentWalkCol.rotation * boatLocalRotation;
            }
            else
            {
                itemBody.transform.position = position;
                itemBody.transform.rotation = rotation;
            }

            itemBody.velocity = Vector3.zero;
            itemBody.angularVelocity = Vector3.zero;
        }

        private void RemoveFoodFromBoatMass()
        {
            if (removedFromBoatMass || itemRigidbody == null || !itemRigidbody.GetBody())
                return;

            var context = CrewBoatContextResolver.Resolve();
            boatMass = context?.BoatMass;
            if (boatMass == null)
                return;

            boatMass.RemoveItem(itemRigidbody);
            removedFromBoatMass = true;
        }

        private void RestoreFoodToBoatMass()
        {
            if (!removedFromBoatMass || boatMass == null || itemRigidbody == null || !food)
                return;

            ResolveOriginBoat();
            if (food.currentActualBoat == originBoat)
                boatMass.AddItem(itemRigidbody);

            removedFromBoatMass = false;
        }

        private void ReturnFoodToOrigin()
        {
            ResolveOriginBoat();
            if (!food || !originBoat)
                return;

            Vector3 position = originBoat.TransformPoint(returnLocalPosition);
            Quaternion rotation = originBoat.rotation * returnLocalRotation;
            food.transform.position = position;
            food.transform.rotation = rotation;
            SyncSuspendedItemRigidbody(position, rotation);
        }

        private void ResolveOriginBoat()
        {
            if (!originBoat && food)
                originBoat = food.currentActualBoat ? food.currentActualBoat : CrewBoatContextResolver.GetActiveWorldBoat();
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
