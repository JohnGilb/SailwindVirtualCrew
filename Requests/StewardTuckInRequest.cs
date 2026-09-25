using UnityEngine;

namespace SailwindVirtualCrew
{
    // The steward walks to an exhausted player, picks them up, carries them to a bed and puts them in it. While
    // carried, the player's controller is disabled and moved along with the steward; the game's own bed logic takes
    // over once they are in the bed (falling asleep, waking, getting up where they were laid down).
    public class StewardTuckInRequest
    {
        // Generous, since this waits for a real arrival and a long ship can take a while to cross.
        private const float PositioningGraceSeconds = 30f;
        // Where the carried player's view sits relative to the steward: in front of them and a little lower than
        // the player's own standing height.
        private const float CarryForwardDistance = 0.5f;
        private const float CarryHeightDrop = 0.35f;
        private const float DefaultStandHeight = 1f;
        private const float MaxStandHeight = 2.5f;

        private readonly Component bed;
        private float positioningStartTime;
        private float positioningTimeTotal;
        private Phase phase = Phase.ToPlayer;
        // Height of the player's observer above the deck they stand on, measured against the steward's feet.
        private float standHeight = DefaultStandHeight;
        private bool playerControlTaken;

        private enum Phase
        {
            ToPlayer,
            Carrying
        }

        public Crewman AssignedCrewman { get; private set; }
        public WorkRequestStatus Status { get; private set; } = WorkRequestStatus.Open;
        public Component Bed => bed;
        public bool IsCarrying => Status == WorkRequestStatus.Positioning && phase == Phase.Carrying;

        public StewardTuckInRequest(Component bed)
        {
            this.bed = bed;
        }

        public void Begin(Crewman crewman)
        {
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            positioningTimeTotal = Mathf.Max(1f, 7f - crewman.Dexterity);
            positioningStartTime = Time.time;
            Status = WorkRequestStatus.Positioning;

            if (!StewardRequestNavigation.TryBeginNearPlayer(this, crewman, "steward tuck-in player"))
                Complete();
        }

        public void Tick()
        {
            if (Status != WorkRequestStatus.Positioning)
                return;

            if (!bed || !LocatorUtils.IsBedStillOnBoat(bed) || GameState.inBed)
            {
                Cancel();
                return;
            }

            // The player passed out on their own before the steward reached them.
            if (phase == Phase.ToPlayer && GameState.sleeping)
            {
                Cancel();
                return;
            }

            // Wait for the steward to actually arrive, so a carried player isn't dropped into a distant bed.
            if (!CrewNavigationCoordinator.Instance.IsPositioningComplete(this) && !IsPositioningTimedOut())
                return;

            if (phase == Phase.ToPlayer)
                PickUpPlayer();
            else
                PutPlayerInBed();
        }

        public void UpdateFrame()
        {
            if (!IsCarrying || !CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out var crewRotation))
                return;

            Vector3 up = GetBoatUp();
            PlacePlayer(crewPosition + crewRotation * Vector3.forward * CarryForwardDistance + up * (standHeight - CarryHeightDrop));
        }

        public void Cancel()
        {
            if (Status == WorkRequestStatus.Complete)
                return;

            // Set the player back down on their feet where the steward stands.
            if (playerControlTaken && CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out _))
                PlacePlayer(crewPosition + GetBoatUp() * standHeight);
            RestorePlayerControl();
            Complete();
        }

        private bool IsPositioningTimedOut()
        {
            return Time.time >= positioningStartTime + positioningTimeTotal + PositioningGraceSeconds;
        }

        private void PickUpPlayer()
        {
            if (Refs.observerMirror == null || Refs.charController == null
                || !CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out _))
            {
                Cancel();
                return;
            }

            float measured = Vector3.Dot(Refs.observerMirror.transform.position - crewPosition, GetBoatUp());
            standHeight = measured > 0f && measured < MaxStandHeight ? measured : DefaultStandHeight;

            DropHeldItems();
            Refs.SetPlayerControl(state: false);
            playerControlTaken = true;

            var context = CrewBoatContextResolver.Resolve();
            if (context == null || !context.WorldBoat)
            {
                Cancel();
                return;
            }

            CrewNavigationCoordinator.GetBedsideWorldPose(bed, context.WorldBoat.up, out var standWorld, out var standWorldRotation);
            Vector3 standLocal = context.WorldBoat.InverseTransformPoint(standWorld);
            Quaternion standLocalRotation = Quaternion.Inverse(context.WorldBoat.rotation) * standWorldRotation;

            phase = Phase.Carrying;
            positioningStartTime = Time.time;
            if (!CrewNavigationCoordinator.Instance.TryRetargetRolePositioning(this, standLocal, standLocalRotation, "steward tuck-in bed='" + bed.name + "'"))
                Cancel();
        }

        private void PutPlayerInBed()
        {
            // Leave the player standing beside the bed, where the steward is; the game gets them up there.
            if (CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out _))
                PlacePlayer(crewPosition + GetBoatUp() * standHeight);

            // Complete first: entering a bed can autosave, and settling requests for that save cancels this one.
            playerControlTaken = false;
            Complete();
            if (Sleep.instance != null && bed && !GameState.inBed)
                Sleep.instance.EnterBed(bed.transform);
            else
                Refs.SetPlayerControl(state: true);
        }

        // The controller lives in the walk-collider copy of the boat and the observer mirrors its local position
        // onto the real boat, so move the controller by the observer's local position.
        private static void PlacePlayer(Vector3 observerWorld)
        {
            if (Refs.observerMirror == null || Refs.charController == null)
                return;

            Transform observer = Refs.observerMirror.transform;
            Transform controller = Refs.charController.transform;
            if (observer.parent)
                controller.localPosition = observer.parent.InverseTransformPoint(observerWorld);
            else
                controller.position = observerWorld;
            observer.position = observerWorld;
        }

        private static void DropHeldItems()
        {
            foreach (var pointer in Object.FindObjectsOfType<GoPointer>())
            {
                var item = pointer.GetHeldItem();
                if (item == null)
                    continue;

                item.OnDrop();
                pointer.DropItem();
            }
        }

        private static Vector3 GetBoatUp()
        {
            var context = CrewBoatContextResolver.Resolve();
            return context != null && context.WorldBoat ? context.WorldBoat.up : Vector3.up;
        }

        private void RestorePlayerControl()
        {
            if (!playerControlTaken)
                return;

            playerControlTaken = false;
            if (!GameState.inBed && !GameState.sleeping)
                Refs.SetPlayerControl(state: true);
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
