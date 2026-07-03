using UnityEngine;

namespace SailwindVirtualCrew
{
    public enum MooringRequestKind
    {
        Moor,
        Unmoor
    }

    public class MooringRequest
    {
        public MooringSide Side { get; }
        public PickupableBoatMooringRope TargetRope { get; }
        public MooringRequestKind Kind { get; }
        public Crewman AssignedCrewman { get; set; }
        public WorkRequestStatus Status { get; set; } = WorkRequestStatus.Open;

        public string CommandName =>
            (Kind == MooringRequestKind.Unmoor ? "Unmoor " : "Moor ")
            + Side + " Line" + (TargetRope ? " (" + TargetRope.name + ")" : "");

        private const float PositioningGraceSeconds = 6f;
        private const float JumpHeight = 1.15f;
        private const float JumpSpeed = 2.75f;
        private MooringRopeInfo ropeInfo;
        private ActiveMooringRoute activeRoute;
        private bool concretePositioning;
        private float positioningStartTime;
        private float positioningTimeTotal;
        private float workStartTime;
        private float workDuration;
        private bool cancelled;
        private bool throwStarted;
        private bool throwComplete;
        private bool throwSucceeded;
        private float throwStartTime;
        private float throwDuration;
        private TravelSegment[] routeSegments;
        private int routeSegmentIndex;
        private float routeSegmentStartTime;
        private bool returningFromDock;

        private sealed class TravelSegment
        {
            internal Vector3 Start;
            internal Quaternion StartRotation;
            internal Vector3 End;
            internal Quaternion EndRotation;
            internal float Duration;
            internal float ArcHeight;
        }

        public MooringRequest(MooringSide side, PickupableBoatMooringRope targetRope, MooringRequestKind kind = MooringRequestKind.Moor)
        {
            Side = side;
            TargetRope = targetRope;
            Kind = kind;
        }

        internal bool RefreshTarget()
        {
            if (!TargetRope)
                return false;

            if (!MooringLocator.TryFindRope(TargetRope, out ropeInfo))
                return false;

            return ropeInfo.Side == Side
                && (Kind == MooringRequestKind.Unmoor ? ropeInfo.IsMoored : !ropeInfo.IsMoored);
        }

        internal bool TryGetWorkLocalPosition(out Vector3 localPosition)
        {
            localPosition = Vector3.zero;
            if (!RefreshTarget())
                return false;

            localPosition = ropeInfo.AnchorLocal;
            return true;
        }

        public void BeginPositioning(Crewman crewman)
        {
            cancelled = false;
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            if (!RefreshTarget())
            {
                Complete();
                return;
            }

            positioningTimeTotal = Mathf.Max(0f, 7f - crewman.Dexterity);
            positioningStartTime = Time.time;
            Status = WorkRequestStatus.Positioning;

            Vector3 destinationLocal = ropeInfo.AnchorLocal;
            Quaternion rotation = GetWorkLocalRotation(destinationLocal);
            concretePositioning = CrewNavigationCoordinator.Instance.TryBeginRolePositioning(
                this,
                crewman,
                destinationLocal,
                rotation,
                CommandName.ToLowerInvariant());
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
            if (concretePositioning)
            {
                if (Kind == MooringRequestKind.Moor)
                {
                    CrewNavigationCoordinator.Instance.Complete(this);
                    concretePositioning = false;
                }
            }

            if (!RefreshTarget())
            {
                Complete();
                return;
            }

            Status = WorkRequestStatus.InProgress;
            workStartTime = Time.time;
            workDuration = Kind == MooringRequestKind.Unmoor
                ? Mathf.Max(0.4f, 1.2f - AssignedCrewman.Dexterity * 0.08f)
                : Mathf.Max(1.5f, 5.5f - AssignedCrewman.Dexterity * 0.55f);
            throwStarted = false;
            throwComplete = false;
            throwSucceeded = false;
            throwStartTime = 0f;
            throwDuration = 0f;
            routeSegments = null;
            routeSegmentIndex = 0;
            routeSegmentStartTime = 0f;
            returningFromDock = false;
        }

        public void Tick()
        {
            if (Status != WorkRequestStatus.InProgress)
                return;

            if (Time.time < workStartTime + workDuration)
                return;

            if (Kind == MooringRequestKind.Unmoor)
            {
                return;
            }

            if (!throwStarted)
                BeginThrow();

            if (!throwComplete)
                return;

            CrewDebugLog.Ok("Mooring",
                "Completed " + CommandName
                + " crew='" + (AssignedCrewman != null ? AssignedCrewman.Name : "none")
                + "' moored=" + throwSucceeded);
            Complete();
        }

        public void UpdateFrame()
        {
            if (Status != WorkRequestStatus.InProgress || Kind != MooringRequestKind.Unmoor)
                return;

            if (Time.time < workStartTime + workDuration)
                return;

            TickUnmoor();
        }

        private void TickUnmoor()
        {
            if (routeSegments == null)
            {
                BeginUnmoorRouteToDock();
                return;
            }

            if (!UpdateRoute())
                return;

            if (!throwStarted)
            {
                BeginThrowBack();
                return;
            }

            if (!throwComplete)
                return;

            if (!returningFromDock)
            {
                BeginUnmoorRouteToBoat();
                return;
            }

            CrewDebugLog.Ok("Mooring",
                "Completed " + CommandName
                + " crew='" + (AssignedCrewman != null ? AssignedCrewman.Name : "none")
                + "' unmoored=" + throwSucceeded);
            Complete();
        }

        private void BeginThrow()
        {
            throwStarted = true;
            throwComplete = true;
            throwSucceeded = false;

            if (cancelled)
                return;

            if (RefreshTarget() && MooringLocator.TryFindClosestDock(TargetRope, Side, out var dock))
            {
                var dockButton = dock.Mooring;
                if (dockButton && dockButton.spring != null && dockButton.spring.connectedBody == null)
                {
                    throwComplete = false;
                    throwStartTime = Time.time;
                    throwDuration = MooringRopeThrowAnimator.EstimateDuration(
                        TargetRope.transform.position,
                        dockButton.transform.position);

                    CrewDebugLog.Info("Mooring",
                        "Throwing " + CommandName
                        + " to '" + dockButton.name + "'"
                        + " duration=" + throwDuration.ToString("0.00") + "s");

                    MooringRopeThrowAnimator.ThrowTo(
                        TargetRope,
                        dockButton,
                        () => cancelled,
                        moored =>
                        {
                            throwSucceeded = moored;
                            throwComplete = true;
                        });
                }
            }
        }

        private void BeginThrowBack()
        {
            throwStarted = true;
            throwComplete = true;
            throwSucceeded = false;

            if (cancelled || !RefreshTarget())
                return;

            throwComplete = false;
            throwStartTime = Time.time;
            throwDuration = MooringRopeThrowAnimator.EstimateDuration(
                TargetRope.transform.position,
                MooringLocator.GetRopeAnchorWorld(TargetRope));

            CrewDebugLog.Info("Mooring",
                "Throwing " + CommandName
                + " back to vessel"
                + " duration=" + throwDuration.ToString("0.00") + "s");

            MooringRopeThrowAnimator.ThrowBackToVessel(
                TargetRope,
                () => cancelled,
                unmoored =>
                {
                    throwSucceeded = unmoored;
                    throwComplete = true;
                });
        }

        private void BeginUnmoorRouteToDock()
        {
            if (!RefreshTarget() || !MooringLocator.TryFindActiveRoute(TargetRope, out activeRoute))
            {
                Complete();
                return;
            }

            Vector3 start;
            Quaternion startRotation;
            if (!CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out start, out startRotation))
            {
                start = activeRoute.BoatAnchorWorld;
                startRotation = GetLookRotation(start, activeRoute.DockWorld, Quaternion.identity);
            }

            Vector3 boatAnchor = activeRoute.BoatAnchorWorld;
            Vector3 dock = activeRoute.DockWorld;
            BeginRoute(new[]
            {
                CreateSegment(start, startRotation, boatAnchor, GetLookRotation(start, dock, startRotation), 0f, JumpSpeed),
                CreateSegment(boatAnchor, GetLookRotation(boatAnchor, dock, startRotation), dock, GetLookRotation(boatAnchor, dock, startRotation), JumpHeight, JumpSpeed)
            });
        }

        private void BeginUnmoorRouteToBoat()
        {
            returningFromDock = true;
            Vector3 start;
            Quaternion startRotation;
            if (!CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out start, out startRotation))
            {
                start = activeRoute != null ? activeRoute.DockWorld : TargetRope.transform.position;
                startRotation = Quaternion.identity;
            }

            Vector3 boatAnchor = activeRoute != null ? activeRoute.BoatAnchorWorld : MooringLocator.GetRopeAnchorWorld(TargetRope);
            Quaternion boatRotation = GetLookRotation(start, boatAnchor, startRotation);
            BeginRoute(new[]
            {
                CreateSegment(start, startRotation, boatAnchor, boatRotation, JumpHeight, JumpSpeed)
            });
        }

        private void BeginRoute(TravelSegment[] segments)
        {
            CrewNavigationCoordinator.Instance.TryStopOwnerMotion(this);
            routeSegments = segments;
            routeSegmentIndex = 0;
            routeSegmentStartTime = Time.time;
        }

        private bool UpdateRoute()
        {
            if (routeSegments == null || routeSegments.Length == 0)
                return true;

            if (routeSegmentIndex >= routeSegments.Length)
                return true;

            var segment = routeSegments[routeSegmentIndex];
            float rawT = segment.Duration <= 0f ? 1f : Mathf.Clamp01((Time.time - routeSegmentStartTime) / segment.Duration);
            float t = rawT * rawT * (3f - 2f * rawT);
            Vector3 position = Vector3.Lerp(segment.Start, segment.End, t);
            if (segment.ArcHeight > 0f)
                position.y += Mathf.Sin(rawT * Mathf.PI) * segment.ArcHeight;
            else
                position.y += Mathf.Sin(rawT * Mathf.PI * 4f) * 0.035f;

            Quaternion rotation = Quaternion.Slerp(segment.StartRotation, segment.EndRotation, t);
            CrewNavigationCoordinator.Instance.TrySetPoseOverrideWorld(this, position, rotation);

            if (rawT < 1f)
                return false;

            routeSegmentIndex++;
            routeSegmentStartTime = Time.time;
            return routeSegmentIndex >= routeSegments.Length;
        }

        private TravelSegment CreateSegment(Vector3 start, Quaternion startRotation, Vector3 end, Quaternion endRotation, float arcHeight, float speed)
        {
            return new TravelSegment
            {
                Start = start,
                StartRotation = startRotation,
                End = end,
                EndRotation = endRotation,
                Duration = Mathf.Max(0.2f, Vector3.Distance(start, end) / Mathf.Max(0.1f, speed)),
                ArcHeight = arcHeight
            };
        }

        public float GetProgress()
        {
            if (throwStarted && !throwComplete)
            {
                float throwProgress = throwDuration <= 0f
                    ? 1f
                    : Mathf.Clamp01((Time.time - throwStartTime) / throwDuration);
                return Kind == MooringRequestKind.Unmoor
                    ? 55f + throwProgress * 25f
                    : 85f + throwProgress * 15f;
            }

            if (Kind == MooringRequestKind.Unmoor && routeSegments != null && routeSegments.Length > 0)
            {
                float completed = routeSegmentIndex;
                var segment = routeSegments[Mathf.Clamp(routeSegmentIndex, 0, routeSegments.Length - 1)];
                float segmentProgress = segment.Duration <= 0f
                    ? 1f
                    : Mathf.Clamp01((Time.time - routeSegmentStartTime) / segment.Duration);
                float routeProgress = Mathf.Clamp01((completed + segmentProgress) / routeSegments.Length);
                return returningFromDock ? 80f + routeProgress * 20f : 15f + routeProgress * 40f;
            }

            return workDuration <= 0f
                ? (Kind == MooringRequestKind.Unmoor ? 15f : 100f)
                : Mathf.Clamp01((Time.time - workStartTime) / workDuration)
                    * (Kind == MooringRequestKind.Unmoor ? 15f : 85f);
        }

        public void CancelPositioning()
        {
            cancelled = true;
            if (!concretePositioning)
                return;

            CrewNavigationCoordinator.Instance.Cancel(this);
            concretePositioning = false;
        }

        private Quaternion GetWorkLocalRotation(Vector3 destinationLocal)
        {
            if (MooringLocator.TryFindClosestDock(TargetRope, Side, out var dock))
            {
                Vector3 direction = dock.LocalPosition - destinationLocal;
                direction.y = 0f;
                if (direction.sqrMagnitude >= 0.001f)
                    return Quaternion.LookRotation(direction.normalized, Vector3.up);
            }

            return Quaternion.LookRotation(Side == MooringSide.Port ? Vector3.forward : Vector3.back, Vector3.up);
        }

        private static Quaternion GetLookRotation(Vector3 from, Vector3 to, Quaternion fallback)
        {
            Vector3 direction = to - from;
            direction.y = 0f;
            return direction.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(direction.normalized, Vector3.up)
                : fallback;
        }

        private void Complete()
        {
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
            CrewNavigationCoordinator.Instance.Complete(this);
            concretePositioning = false;
        }
    }
}
