using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// A deckhand loads one item from the dock into the painted cargo area: the reverse of <see cref="HaulSellRequest"/>.
    ///
    /// The item's spot is planned as soon as the request is made (<see cref="CargoLoadPlanner"/>, a slice per frame) and
    /// reserved, so it's usually ready long before the deckhand needs it. The deckhand walks to the boat's mooring, crosses
    /// to the dock, fetches the item and carries it back aboard. Only then is the reserved spot confirmed against the
    /// ship as it is now; the deckhand walks it there, confirms once more on arrival, and sets it down. When the spot no
    /// longer works (the player put something there, cargo shifted), it is planned again while the deckhand waits holding
    /// the item; when there's no room at all, the item goes back to the dock.
    /// </summary>
    public class HaulLoadRequest
    {
        private const float PositioningGraceSeconds = 6f;
        private const float HaulSpeed = 2.25f;
        private const float ReturnSpeed = 2.75f;
        private const float JumpHeight = 1.15f;
        // Walks run at constant speed, the top speed the eased movement used to reach (smoothstep peaks at 1.5x its
        // average), so a deckhand is at full pace from the moment they land on the dock. Jumps keep the easing.
        private const float WalkSpeedFactor = 1.5f;
        // Walking bobs once per this much ground covered.
        private const float StrideLength = 1.4f;
        private const float JumpArcPerMeter = 0.2f;
        private const float MaxJumpSeconds = 3f;
        // Where the crewman holds the item: this far ahead of their feet, with its origin this high.
        private const float CarryAhead = 0.75f;
        private const float CarryHeight = 0.35f;
        private const float SetDownSeconds = 0.6f;
        private const float SetDownArc = 0.12f;
        // How often a waiting deckhand checks whether their spot is ready.
        private const float ConfirmInterval = 0.5f;
        // Setting down, the crewman stands this far short of the spot, on the side they came from.
        private const float StowStandOff = 0.9f;

        private static int nextSequence;

        private readonly ShipItem item;
        private readonly CarriedCargo cargo;
        private readonly int sequence;
        private Transform originBoat;
        private Transform originTopBoat;
        private Transform originFrame;
        private Vector3 originLocalPosition;
        private Quaternion originLocalRotation;

        private Phase phase = Phase.Waiting;
        private bool concretePositioning;
        private float positioningStartTime;
        private float positioningTimeTotal;
        private ShoreRoute activeRoute;
        private Segment[] routeSegments;
        private int routeSegmentIndex;
        private float routeSegmentStartTime;
        private RouteMode routeMode;
        private Quaternion carryRotation;
        private float nextConfirmTime;
        private bool crewAwayFromAnchor;
        private bool suspendedForSave;

        private CargoLoadPlanner.Reservation reservation;
        private string planFailure;
        // Told once whether the first plan found a spot; a batch uses it for one summary instead of a notice per item.
        private System.Action<bool> onFirstPlan;

        private enum RouteMode
        {
            // The crewman moves along the route.
            CrewOnly,
            // Only the item moves (lifting it, setting it down); the crewman stays put.
            ItemOnly,
            // The item moves along the route with the crewman walking behind it.
            CarryWithCrew
        }

        private enum Phase
        {
            Waiting,
            Boarding,
            Fetching,
            Lifting,
            Carrying,
            AwaitingSpot,
            Stowing,
            SettingDown,
            WalkingBackToAnchor,
            ReturningCargo,
            ReturningEmpty,
            Done
        }

        private sealed class Segment
        {
            internal Vector3 Start;
            internal Quaternion StartRotation;
            internal Vector3 End;
            internal Quaternion EndRotation;
            internal float Duration;
            internal float ArcHeight;
            internal int Bobs;
        }

        public HaulLoadRequest(ShipItem item, System.Action<bool> onFirstPlan = null)
        {
            this.item = item;
            this.onFirstPlan = onFirstPlan;
            cargo = new CarriedCargo(item);
            sequence = nextSequence++;
            originBoat = CrewBoatContextResolver.GetActiveWorldBoat();
            originTopBoat = CrewBoatContextResolver.GetActiveTopBoat();
            RecordOrigin();
            RequestPlan();
        }

        public Crewman AssignedCrewman { get; set; }
        public WorkRequestStatus Status { get; private set; } = WorkRequestStatus.Open;
        public string CommandName => "Load";
        public ShipItem Item => item;
        public string ItemName => item ? item.name : "cargo";
        // Requests are handed out in the order they were made, which is also the order their spots were planned: bow
        // first. Deliveries then land bow to stern, and deckhands rarely walk through cargo they've already stowed.
        public int Sequence => sequence;

        /// <summary>Waiting for a deckhand, with a spot planned (or still being planned).</summary>
        public bool IsReadyToAssign => Status == WorkRequestStatus.Open && planFailure == null;

        /// <summary>Its spot is planned and reserved.</summary>
        public bool HasSpot => reservation != null;

        public string StatusLabel
        {
            get
            {
                switch (phase)
                {
                    case Phase.Waiting:
                        return reservation != null ? "Load " + ItemName : "Load " + ItemName + " (planning)";
                    case Phase.AwaitingSpot:
                        return "Waiting for a spot for " + ItemName;
                    case Phase.WalkingBackToAnchor:
                    case Phase.ReturningCargo:
                        return "Returning " + ItemName + " to the dock";
                    case Phase.ReturningEmpty:
                        return "Returning from loading";
                    default:
                        return "Load " + ItemName;
                }
            }
        }

        public bool AbortIfPlayerLeftOriginBoat()
        {
            if (Status == WorkRequestStatus.Complete || IsOriginBoatActive())
                return false;

            Abort("the boat is no longer the active boat");
            return true;
        }

        // ---------------------------------------------------------------- planning

        private void RequestPlan()
        {
            planFailure = null;
            reservation = null;
            // Planned again once a deckhand has the item (the first spot fell through): they're waiting on it, so it
            // goes ahead of the items still sitting on the dock.
            bool urgent = phase != Phase.Waiting && phase != Phase.Boarding;
            CargoLoadPlanner.Request(item, reserve: true, onDone: OnPlanned, urgent: urgent);
        }

        private void OnPlanned(CargoPackingSolver.SolveJob job, CargoLoadPlanner.Reservation planned)
        {
            if (Status == WorkRequestStatus.Complete)
            {
                CargoLoadPlanner.Release(item);
                return;
            }

            var report = onFirstPlan;
            onFirstPlan = null;
            report?.Invoke(planned != null);

            if (planned != null)
            {
                reservation = planned;
                return;
            }

            planFailure = job.Failure ?? "no room";
            if (report == null)
                NotificationUi.instance?.ShowNotification("No room aboard for " + ItemName);
            // Not yet picked up: just give up. Carrying it: take it back (see UpdateAwaitingSpot).
            if (phase == Phase.Waiting)
                AbortWithoutCargo();
        }

        // ---------------------------------------------------------------- positioning

        public void BeginPositioning(Crewman crewman)
        {
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            positioningTimeTotal = Mathf.Max(1f, 7f - crewman.Dexterity);
            positioningStartTime = Time.time;
            Status = WorkRequestStatus.Positioning;
            phase = Phase.Boarding;

            if (!item || !ShoreRoute.TryFind(item.transform.position, out activeRoute))
            {
                NotificationUi.instance?.ShowNotification("Can't reach the dock to load " + ItemName);
                Abort("no mooring route to the dock");
                return;
            }

            Quaternion facingDock = LookRotation(activeRoute.BoatAnchorWorld, activeRoute.DockWorld, Quaternion.identity);
            Quaternion localFacing = originBoat ? Quaternion.Inverse(originBoat.rotation) * facingDock : facingDock;
            concretePositioning = CrewNavigationCoordinator.Instance.TryBeginRolePositioning(
                this, crewman, activeRoute.BoatAnchorLocal, localFacing, "haul load via " + activeRoute.Label);
        }

        public bool IsPositioningComplete()
        {
            if (Status != WorkRequestStatus.Positioning)
                return false;

            return concretePositioning
                ? CrewNavigationCoordinator.Instance.IsPositioningComplete(this)
                : Time.time >= positioningStartTime + positioningTimeTotal;
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

        /// <summary>At the mooring: cross to the dock and fetch the item.</summary>
        public void BeginHaul()
        {
            if (Status != WorkRequestStatus.Positioning)
                return;

            if (!IsOriginBoatActive() || !IsItemAvailable())
            {
                Abort("the item or the boat is gone");
                return;
            }

            if (planFailure != null)
            {
                AbortWithoutCargo();
                return;
            }

            // Stand on the far side of the item, facing back towards the dock: then it's already in front on the way back.
            Vector3 start = GetBoatAnchorWorld();
            Vector3 dock = activeRoute.DockWorld;
            Vector3 itemPosition = item.transform.position;
            Vector3 stand = itemPosition + Flat(itemPosition - dock).normalized * CarryAhead;
            Quaternion toDock = LookRotation(start, dock, Quaternion.identity);
            Quaternion toItem = LookRotation(dock, itemPosition, toDock);
            Quaternion backToDock = LookRotation(stand, dock, toItem);

            Status = WorkRequestStatus.InProgress;
            phase = Phase.Fetching;
            var segments = new List<Segment> { CreateSegment(start, toDock, dock, toDock, JumpHeight, ReturnSpeed) };
            AddShoreWalk(segments, dock, toItem, stand, backToDock, ReturnSpeed, 0f);
            BeginRoute(segments.ToArray(), RouteMode.CrewOnly);
        }

        // ---------------------------------------------------------------- per frame

        public void UpdateFrame()
        {
            if (Status != WorkRequestStatus.InProgress)
                return;

            if (suspendedForSave)
            {
                // Fallback if the save never completed.
                if (VirtualCrewManager.Instance.IsHaulSellSuspendedForSave)
                    return;
                ResumeAfterSave();
            }

            if (!IsOriginBoatActive())
            {
                Abort("the boat is no longer the active boat");
                return;
            }

            if (!item || (item.held && !cargo.IsSuspended))
            {
                Abort("the item was taken");
                return;
            }

            switch (phase)
            {
                case Phase.Fetching:
                    if (UpdateRoute())
                    {
                        if (planFailure != null)
                            BeginReturnEmpty(item.transform.position);
                        else
                            PickUp();
                    }
                    break;
                case Phase.Lifting:
                    if (UpdateRoute())
                        BeginCarryAboard();
                    break;
                case Phase.Carrying:
                    if (UpdateRoute())
                        BeginAwaitingSpot();
                    break;
                case Phase.AwaitingSpot:
                    // Keep it where the carry left it: the crewman's visible pose may be one set by the carry, which
                    // isn't where their walking agent is.
                    CrewNavigationCoordinator.Instance.HoldItemThisFrame(this, item);
                    UpdateAwaitingSpot();
                    break;
                case Phase.Stowing:
                    HoldCargoAtCrew();
                    if (CrewNavigationCoordinator.Instance.IsPositioningComplete(this))
                        ArriveAtSpot();
                    break;
                case Phase.SettingDown:
                    if (UpdateRoute())
                        Place();
                    break;
                case Phase.WalkingBackToAnchor:
                    HoldCargoAtCrew();
                    if (CrewNavigationCoordinator.Instance.IsPositioningComplete(this))
                        BeginCarryBackToDock();
                    break;
                case Phase.ReturningCargo:
                    if (UpdateRoute())
                        PutBackOnDock();
                    break;
                case Phase.ReturningEmpty:
                    if (UpdateRoute())
                        Complete();
                    break;
            }
        }

        private void PickUp()
        {
            RecordOrigin();
            cargo.Suspend();
            Quaternion toDock = LookRotation(item.transform.position, activeRoute.DockWorld, Quaternion.identity);
            // The item keeps its heading relative to the crewman carrying it.
            carryRotation = Quaternion.Inverse(toDock) * item.transform.rotation;

            phase = Phase.Lifting;
            Vector3 from = item.transform.position;
            BeginRoute(new[]
            {
                CreateSegment(from, item.transform.rotation, from + Vector3.up * CarryHeight, toDock * carryRotation, 0f, 1f)
            }, RouteMode.ItemOnly);
        }

        private void BeginCarryAboard()
        {
            Vector3 start = item.transform.position;
            Vector3 dock = activeRoute.DockWorld + Vector3.up * CarryHeight;
            Vector3 anchor = GetBoatAnchorWorld() + Vector3.up * CarryHeight;
            Quaternion toDock = LookRotation(start, dock, Quaternion.identity);
            Quaternion toBoat = LookRotation(dock, anchor, toDock);

            phase = Phase.Carrying;
            var segments = new List<Segment>();
            AddShoreWalk(segments, start, toDock, dock, toDock, GetHaulSpeed(), CarryHeight);
            segments.Add(CreateSegment(dock, toBoat, anchor, toBoat, JumpHeight, GetHaulSpeed()));
            BeginRoute(segments.ToArray(), RouteMode.CarryWithCrew);
        }

        private void BeginAwaitingSpot()
        {
            phase = Phase.AwaitingSpot;
            nextConfirmTime = 0f;
            CrewNavigationCoordinator.Instance.TryStopOwnerMotion(this);
        }

        private void UpdateAwaitingSpot()
        {
            if (planFailure != null)
            {
                BeginReturnToDock();
                return;
            }

            if (Time.time < nextConfirmTime)
                return;
            nextConfirmTime = Time.time + ConfirmInterval;

            if (reservation == null)
            {
                // Still waiting on the first plan: it's needed now.
                if (CargoLoadPlanner.IsPlanning(item))
                    CargoLoadPlanner.Expedite(item);
                else
                    RequestPlan();
                return;
            }

            // The spot was planned a while ago; the ship may have changed since.
            if (!CargoLoadPlanner.Confirm(reservation, out _))
            {
                RequestPlan();
                return;
            }

            // Stand just short of the spot, on the side coming from the mooring, facing it.
            Vector3 anchorLocal = activeRoute.BoatAnchorLocal;
            Vector3 approach = Flat(reservation.Position - anchorLocal);
            Vector3 standLocal = approach.sqrMagnitude > StowStandOff * StowStandOff
                ? reservation.Position - approach.normalized * StowStandOff
                : anchorLocal;
            Quaternion facing = approach.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(approach.normalized, Vector3.up)
                : Quaternion.identity;
            if (!CrewNavigationCoordinator.Instance.TryRetargetRolePositioning(this, standLocal, facing, "haul load stow '" + ItemName + "'"))
            {
                // No way there on foot: set it down from where we stand.
                BeginSetDown();
                return;
            }

            crewAwayFromAnchor = true;
            phase = Phase.Stowing;
        }

        private void ArriveAtSpot()
        {
            if (reservation == null || !CargoLoadPlanner.Confirm(reservation, out _))
            {
                // Taken while we walked here: wait here for a new spot.
                phase = Phase.AwaitingSpot;
                nextConfirmTime = 0f;
                RequestPlan();
                return;
            }

            BeginSetDown();
        }

        private void BeginSetDown()
        {
            phase = Phase.SettingDown;
            BeginRoute(new[]
            {
                new Segment
                {
                    Start = item.transform.position,
                    StartRotation = item.transform.rotation,
                    End = GetReservedWorldPosition(),
                    EndRotation = originBoat.rotation * reservation.Rotation,
                    Duration = SetDownSeconds,
                    ArcHeight = SetDownArc
                }
            }, RouteMode.ItemOnly);
        }

        private void Place()
        {
            Vector3 target = GetReservedWorldPosition();
            Quaternion targetRotation = originBoat.rotation * reservation.Rotation;
            cargo.MoveTo(target, targetRotation);

            if (!CarriedCargo.EnterBoat(item, originBoat))
                CrewDebugLog.Warn("CargoLoad", "Could not put '" + ItemName + "' aboard directly; leaving it to the embark trigger.");
            cargo.MoveTo(target, targetRotation);
            // Frozen until loading is finished: what it's planned to rest on may not have arrived yet (see FrozenCargo).
            cargo.Restore(frozen: true);
            FrozenCargo.Add(item, cargo);

            CargoLoadPlanner.Release(item);
            CargoLoadService.RemoveLoadStamp(item);
            CrewDebugLog.Ok("CargoLoad", "Stowed '" + ItemName + "' (" + reservation.PoseLabel + ") at " + reservation.Position);
            Complete();
        }

        // ---------------------------------------------------------------- returning the item

        private void BeginReturnToDock()
        {
            CargoLoadPlanner.Cancel(item);
            if (crewAwayFromAnchor
                && CrewNavigationCoordinator.Instance.TryRetargetRolePositioning(this, activeRoute.BoatAnchorLocal,
                    Quaternion.Inverse(originBoat.rotation) * LookRotation(activeRoute.BoatAnchorWorld, activeRoute.DockWorld, carryRotation),
                    "haul load return '" + ItemName + "'"))
            {
                phase = Phase.WalkingBackToAnchor;
                return;
            }

            BeginCarryBackToDock();
        }

        private void BeginCarryBackToDock()
        {
            crewAwayFromAnchor = false;
            Vector3 start = item.transform.position;
            Vector3 anchor = GetBoatAnchorWorld() + Vector3.up * CarryHeight;
            Vector3 dock = activeRoute.DockWorld + Vector3.up * CarryHeight;
            Vector3 origin = GetOriginWorldPosition();
            Quaternion toDock = LookRotation(anchor, dock, carryRotation);
            Quaternion toOrigin = LookRotation(dock, origin, toDock);

            phase = Phase.ReturningCargo;
            var segments = new List<Segment>
            {
                CreateSegment(start, toDock, anchor, toDock, 0f, GetHaulSpeed()),
                CreateSegment(anchor, toDock, dock, toDock, JumpHeight, GetHaulSpeed())
            };
            AddShoreWalk(segments, dock, toOrigin, origin + Vector3.up * CarryHeight, toOrigin, GetHaulSpeed(), CarryHeight);
            BeginRoute(segments.ToArray(), RouteMode.CarryWithCrew);
        }

        private void PutBackOnDock()
        {
            cargo.MoveTo(GetOriginWorldPosition(), GetOriginWorldRotation());
            cargo.Restore();
            BeginReturnEmpty(GetOriginWorldPosition());
        }

        // From the dock back aboard, empty-handed.
        private void BeginReturnEmpty(Vector3 from)
        {
            CargoLoadPlanner.Cancel(item);
            CargoLoadService.RemoveLoadStamp(item);

            Vector3 dock = activeRoute.DockWorld;
            Vector3 anchor = GetBoatAnchorWorld();
            Quaternion toDock = LookRotation(from, dock, Quaternion.identity);
            Quaternion toBoat = LookRotation(dock, anchor, toDock);
            phase = Phase.ReturningEmpty;
            var segments = new List<Segment>();
            AddShoreWalk(segments, from, toDock, dock, toDock, ReturnSpeed, 0f);
            segments.Add(CreateSegment(dock, toBoat, anchor, toBoat, JumpHeight, ReturnSpeed));
            BeginRoute(segments.ToArray(), RouteMode.CrewOnly);
        }

        // ---------------------------------------------------------------- cancel, abort, save

        /// <summary>The player cancelled: an item in hand goes back to the dock; otherwise the request just ends.</summary>
        public void Cancel()
        {
            if (Status == WorkRequestStatus.InProgress && cargo.IsSuspended
                && (phase == Phase.Lifting || phase == Phase.Carrying || phase == Phase.AwaitingSpot || phase == Phase.Stowing))
            {
                CrewNavigationCoordinator.Instance.TryStopOwnerMotion(this);
                BeginReturnToDock();
                return;
            }

            if (phase == Phase.WalkingBackToAnchor || phase == Phase.ReturningCargo || phase == Phase.ReturningEmpty)
                return;

            Abort("cancelled");
        }

        /// <summary>Ends the request now: an item in hand is put straight back where it came from.</summary>
        public void Abort(string reason)
        {
            if (Status == WorkRequestStatus.Complete)
                return;

            CrewDebugLog.Ok("CargoLoad", "Loading '" + ItemName + "' stopped: " + reason);
            if (cargo.IsSuspended && item)
            {
                cargo.MoveTo(GetOriginWorldPosition(), GetOriginWorldRotation());
                cargo.Restore();
            }

            AbortWithoutCargo();
        }

        private void AbortWithoutCargo()
        {
            // Ended before its first plan came back: it won't be placed.
            var report = onFirstPlan;
            onFirstPlan = null;
            report?.Invoke(false);

            CargoLoadPlanner.Cancel(item);
            CargoLoadService.RemoveLoadStamp(item);
            if (concretePositioning || Status != WorkRequestStatus.Open)
                CrewNavigationCoordinator.Instance.Cancel(this);
            Finish();
        }

        private bool IsCarryingCargo => Status == WorkRequestStatus.InProgress && cargo.IsSuspended;

        // The game records item positions shortly after SaveGame; put carried cargo back on the dock with normal
        // physics so the save never captures it mid-carry. Carrying picks up again afterwards.
        public void SuspendForSave()
        {
            if (suspendedForSave || !IsCarryingCargo)
                return;

            cargo.MoveTo(GetOriginWorldPosition(), GetOriginWorldRotation());
            cargo.Restore();
            suspendedForSave = true;
        }

        public void ResumeAfterSave()
        {
            if (!suspendedForSave)
                return;

            suspendedForSave = false;
            if (Status == WorkRequestStatus.InProgress && item && phase != Phase.Fetching && phase != Phase.ReturningEmpty)
                cargo.Suspend();
        }

        private void Complete()
        {
            // The crewman has finished with the reservation's spot one way or another.
            Finish();
            CrewNavigationCoordinator.Instance.Complete(this);
        }

        private void Finish()
        {
            phase = Phase.Done;
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
        }

        // ---------------------------------------------------------------- movement

        public float GetProgress()
        {
            if (Status != WorkRequestStatus.InProgress)
                return 0f;

            switch (phase)
            {
                case Phase.Fetching: return RouteFraction() * 30f;
                case Phase.Lifting: return 30f;
                case Phase.Carrying: return 30f + RouteFraction() * 40f;
                case Phase.AwaitingSpot: return 70f;
                case Phase.Stowing: return 80f;
                case Phase.SettingDown: return 90f + RouteFraction() * 10f;
                default: return RouteFraction() * 100f;
            }
        }

        private float RouteFraction()
        {
            if (routeSegments == null || routeSegments.Length == 0)
                return 0f;

            var segment = routeSegments[Mathf.Clamp(routeSegmentIndex, 0, routeSegments.Length - 1)];
            float segmentProgress = segment.Duration <= 0f ? 1f : Mathf.Clamp01((Time.time - routeSegmentStartTime) / segment.Duration);
            return Mathf.Clamp01((routeSegmentIndex + segmentProgress) / routeSegments.Length);
        }

        private void BeginRoute(Segment[] segments, RouteMode mode)
        {
            CrewNavigationCoordinator.Instance.TryStopOwnerMotion(this);
            routeSegments = segments;
            routeSegmentIndex = 0;
            routeSegmentStartTime = Time.time;
            routeMode = mode;
        }

        // Advances the current route; true once it's finished. See RouteMode for what moves.
        private bool UpdateRoute()
        {
            if (routeSegments == null || routeSegmentIndex >= routeSegments.Length)
                return true;

            var segment = routeSegments[routeSegmentIndex];
            float rawT = segment.Duration <= 0f ? 1f : Mathf.Clamp01((Time.time - routeSegmentStartTime) / segment.Duration);
            // Eased for jumps and for lifting or setting down the item; walking is at constant speed.
            float t = segment.ArcHeight > 0f || routeMode == RouteMode.ItemOnly ? rawT * rawT * (3f - 2f * rawT) : rawT;
            Vector3 position = Vector3.Lerp(segment.Start, segment.End, t);
            if (segment.ArcHeight > 0f)
                position.y += Mathf.Sin(rawT * Mathf.PI) * segment.ArcHeight;
            else
                position.y += Mathf.Sin(rawT * Mathf.PI * 2f * segment.Bobs) * 0.035f;
            Quaternion rotation = Quaternion.Slerp(segment.StartRotation, segment.EndRotation, t);

            switch (routeMode)
            {
                case RouteMode.ItemOnly:
                    cargo.MoveTo(position, rotation);
                    // Held while lifting it; not while setting it down: a held item is posed into the crewman's hands
                    // after this, which would pull it off its spot (and a stowed item stays frozen wherever it's left).
                    if (phase == Phase.Lifting)
                        CrewNavigationCoordinator.Instance.HoldItemThisFrame(this, item);
                    break;
                case RouteMode.CarryWithCrew:
                    Quaternion heading = Flatten(rotation);
                    cargo.MoveTo(position, heading * carryRotation);
                    CrewNavigationCoordinator.Instance.TrySetPoseOverrideWorld(this,
                        position - Vector3.up * CarryHeight - heading * Vector3.forward * CarryAhead, heading);
                    CrewNavigationCoordinator.Instance.HoldItemThisFrame(this, item);
                    break;
                default:
                    CrewNavigationCoordinator.Instance.TrySetPoseOverrideWorld(this, position, rotation);
                    break;
            }

            if (rawT < 1f)
                return false;

            routeSegmentIndex++;
            routeSegmentStartTime = Time.time;
            return routeSegmentIndex >= routeSegments.Length;
        }

        // While the crewman walks the deck (or waits), the item is carried in front of them.
        private void HoldCargoAtCrew()
        {
            if (!CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out var crewRotation))
                return;

            Vector3 position = CarryPositionAtCrewPose(crewPosition, crewRotation, out Quaternion rotation);
            cargo.MoveTo(position, rotation);
            CrewNavigationCoordinator.Instance.HoldItemThisFrame(this, item);
        }

        // The carried item's pose for a crewman standing at the given pose.
        private Vector3 CarryPositionAtCrewPose(Vector3 crewPosition, Quaternion crewRotation, out Quaternion rotation)
        {
            Quaternion facing = Flatten(crewRotation);
            rotation = facing * carryRotation;
            return crewPosition + facing * Vector3.forward * CarryAhead + Vector3.up * CarryHeight;
        }

        private float GetHaulSpeed()
        {
            return HaulSpeed + (AssignedCrewman != null ? AssignedCrewman.Strength * 0.15f : 0f);
        }

        private static Segment CreateSegment(Vector3 start, Quaternion startRotation, Vector3 end, Quaternion endRotation, float arcHeight, float speed)
        {
            float distance = Vector3.Distance(start, end);
            float duration = distance / Mathf.Max(0.1f, arcHeight > 0f ? speed : speed * WalkSpeedFactor);
            if (arcHeight > 0f)
            {
                // Longer leaps (ashore from an anchored boat) arc higher and don't take forever.
                arcHeight = Mathf.Max(arcHeight, distance * JumpArcPerMeter);
                duration = Mathf.Min(duration, MaxJumpSeconds);
            }
            return new Segment
            {
                Start = start,
                StartRotation = startRotation,
                End = end,
                EndRotation = endRotation,
                // Walking goes at a steady top speed; see WalkSpeedFactor.
                Duration = Mathf.Max(0.2f, duration),
                ArcHeight = arcHeight,
                Bobs = Mathf.Max(1, Mathf.RoundToInt(distance / StrideLength))
            };
        }

        // A walk ashore: around buildings on the port's NavMesh when there's a path, straight otherwise. Carrying,
        // the points are the item's, lift above the ground; the crewman's pose is worked out from it (see UpdateRoute).
        private static void AddShoreWalk(List<Segment> segments, Vector3 start, Quaternion startRotation, Vector3 end, Quaternion endRotation, float speed, float lift)
        {
            if (!ShoreNavMesh.TryBuildWalk(start, end, lift, out var points))
            {
                segments.Add(CreateSegment(start, startRotation, end, endRotation, 0f, speed));
                return;
            }

            // Each piece turns towards the next one as it goes, so the heading is continuous through corners.
            Quaternion rotation = startRotation;
            for (int i = 1; i < points.Count; i++)
            {
                Quaternion next = i + 1 < points.Count ? LookRotation(points[i], points[i + 1], rotation) : endRotation;
                segments.Add(CreateSegment(points[i - 1], rotation, points[i], next, 0f, speed));
                rotation = next;
            }
        }

        private static Quaternion LookRotation(Vector3 from, Vector3 to, Quaternion fallback)
        {
            Vector3 direction = Flat(to - from);
            return direction.sqrMagnitude > 0.001f ? Quaternion.LookRotation(direction.normalized, Vector3.up) : fallback;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        // Just the heading of a rotation.
        private static Quaternion Flatten(Quaternion rotation)
        {
            Vector3 forward = Flat(rotation * Vector3.forward);
            return forward.sqrMagnitude > 0.001f ? Quaternion.LookRotation(forward.normalized, Vector3.up) : Quaternion.identity;
        }

        // ---------------------------------------------------------------- where things are

        // Where the item sat on the dock, kept relative to the shifting world (floating origin) so it stays put.
        private void RecordOrigin()
        {
            if (!item)
                return;

            originFrame = FloatingOriginManager.instance ? FloatingOriginManager.instance.transform : null;
            originLocalPosition = originFrame ? originFrame.InverseTransformPoint(item.transform.position) : item.transform.position;
            originLocalRotation = originFrame ? Quaternion.Inverse(originFrame.rotation) * item.transform.rotation : item.transform.rotation;
        }

        private Vector3 GetOriginWorldPosition()
        {
            return originFrame ? originFrame.TransformPoint(originLocalPosition) : originLocalPosition;
        }

        private Quaternion GetOriginWorldRotation()
        {
            return originFrame ? originFrame.rotation * originLocalRotation : originLocalRotation;
        }

        // Exactly the reserved pose, with no lift for settling: stowed items stay frozen until loading is done, so any
        // lift would stay too, and cargo planned on or against the reservation (at its exact pose) would collide with it.
        private Vector3 GetReservedWorldPosition()
        {
            return originBoat.TransformPoint(reservation.Position);
        }

        private Vector3 GetBoatAnchorWorld()
        {
            return originBoat ? originBoat.TransformPoint(activeRoute.BoatAnchorLocal) : activeRoute.BoatAnchorWorld;
        }

        private bool IsItemAvailable()
        {
            return item && !item.held && !item.currentActualBoat;
        }

        private bool IsOriginBoatActive()
        {
            if (!originBoat)
                return false;

            if (originTopBoat)
                return CrewBoatContextResolver.IsActiveTopBoat(originTopBoat);

            var activeWorldBoat = CrewBoatContextResolver.GetActiveWorldBoat();
            return activeWorldBoat && activeWorldBoat == originBoat;
        }
    }
}
