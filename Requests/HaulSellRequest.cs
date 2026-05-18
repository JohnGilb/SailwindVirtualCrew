using UnityEngine;

namespace SailwindVirtualCrew
{
    public class HaulSellRequest
    {
        private const float PositioningGraceSeconds = 6f;
        private const float SellDistance = 2.5f;
        private const float HaulSpeed = 2.25f;
        private const float ReturnSpeed = 2.75f;
        private const float JumpHeight = 1.15f;

        private readonly ShipItem item;
        private readonly PortDude portDude;
        private readonly Port port;
        private readonly IslandMarket market;
        private readonly int goodIndex;

        private Transform originBoat;
        private float positioningStartTime;
        private float positioningTimeTotal;
        private bool concretePositioning;
        private HaulSegment[] routeSegments;
        private int routeSegmentIndex;
        private float routeSegmentStartTime;
        private bool routeCarriesCargo;
        private Vector3 haulStartPosition;
        private Quaternion haulStartRotation;
        private Vector3 haulEndPosition;
        private Quaternion haulEndRotation;
        private Vector3 returnLocalPosition;
        private Quaternion returnLocalRotation;
        private Collider itemCollider;
        private bool itemColliderWasEnabled;
        private ItemRigidbody itemRigidbody;
        private bool itemRigidbodyDisableColWasSet;
        private bool itemRigidbodyWasEnabled;
        private Rigidbody itemBody;
        private bool itemBodyWasKinematic;
        private bool itemBodyDetectCollisionsWasEnabled;
        private Collider[] itemRigidbodyColliders;
        private bool[] itemRigidbodyColliderStates;
        private Collider[] itemChildColliders;
        private bool[] itemChildColliderStates;
        private BoatMass boatMass;
        private bool removedFromBoatMass;
        private Phase phase = Phase.Waiting;
        private bool restoringCanceledCargo;
        private ActiveMooringRoute activeRoute;
        private bool navigatingToMooring;
        private float cargoCarryHeightOffset;

        private enum Phase
        {
            Waiting,
            Hauling,
            Returning
        }

        private sealed class HaulSegment
        {
            internal Vector3 Start;
            internal Quaternion StartRotation;
            internal Vector3 End;
            internal Quaternion EndRotation;
            internal float Duration;
            internal float ArcHeight;
        }

        public Crewman AssignedCrewman { get; set; }
        public WorkRequestStatus Status { get; private set; } = WorkRequestStatus.Open;
        public string CommandName => "Haul & Sell";
        public ShipItem Item => item;
        public string ItemName => item ? item.name : "cargo";
        public bool IsReturning => phase == Phase.Returning && Status == WorkRequestStatus.InProgress;
        public bool IsReturningCanceledCargo => restoringCanceledCargo && IsReturning;

        public bool AbortIfPlayerLeftOriginBoat()
        {
            if (Status == WorkRequestStatus.Complete || IsPlayerOnOriginBoat())
                return false;

            AbortAndUnmark();
            return true;
        }

        public HaulSellRequest(ShipItem item, PortDude portDude, int goodIndex)
        {
            this.item = item;
            this.portDude = portDude;
            this.port = portDude ? portDude.GetPort() : null;
            this.market = port ? port.GetComponent<IslandMarket>() : null;
            this.goodIndex = goodIndex;
            this.originBoat = item ? item.currentActualBoat : null;
        }

        public void BeginPositioning(Crewman crewman)
        {
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            positioningTimeTotal = Mathf.Max(1f, 7f - crewman.Dexterity);
            positioningStartTime = Time.time;
            Status = WorkRequestStatus.Positioning;

            concretePositioning = false;
            if (item && !originBoat)
                originBoat = item.currentActualBoat ? item.currentActualBoat : GameState.currentBoat;

            if (item && IsPlayerOnOriginBoat())
            {
                Vector3 localPosition = originBoat.InverseTransformPoint(item.transform.position);
                Quaternion localRotation = Quaternion.Inverse(originBoat.rotation) * item.transform.rotation;
                concretePositioning = CrewNavigationCoordinator.Instance.TryBeginRolePositioning(
                    this, crewman, localPosition, localRotation, "haul sell cargo='" + item.name + "'");
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

        public void BeginHaul()
        {
            if (Status != WorkRequestStatus.Positioning)
                return;

            if (!IsPlayerOnOriginBoat())
            {
                AbortAndUnmark();
                return;
            }

            if (concretePositioning)
            {
                concretePositioning = false;
            }

            if (!item || !portDude || market == null)
            {
                AbortAndUnmark();
                return;
            }

            DisableCargoCollision();
            RemoveCargoFromBoatMass();
            haulStartPosition = item.transform.position;
            haulStartRotation = item.transform.rotation;
            if (!originBoat)
                originBoat = item.currentActualBoat ? item.currentActualBoat : GameState.currentBoat;
            returnLocalPosition = originBoat
                ? originBoat.InverseTransformPoint(haulStartPosition)
                : Vector3.zero;
            returnLocalRotation = originBoat
                ? Quaternion.Inverse(originBoat.rotation) * haulStartRotation
                : haulStartRotation;
            if (!MooringLocator.TryFindActiveRoute(haulStartPosition, out activeRoute))
            {
                ReturnCargoToOrigin();
                RestoreCargoCollision();
                RestoreCargoToBoatMass();
                SupercargoTradeService.RemoveStamp(item);
                Complete();
                return;
            }

            haulEndPosition = portDude.transform.position + Vector3.up * Mathf.Max(0.25f, GetCargoHalfHeight());
            haulEndRotation = GetLookRotation(haulStartPosition, haulEndPosition, haulStartRotation);
            if (!BeginNavigateToMooring())
                BeginRoute(BuildRoute(haulStartPosition, haulStartRotation, haulEndPosition, haulEndRotation, activeRoute, toPort: true), carriesCargo: true);
            phase = Phase.Hauling;
            Status = WorkRequestStatus.InProgress;
        }

        public void UpdateFrame()
        {
            if (Status != WorkRequestStatus.InProgress)
                return;

            if (!IsPlayerOnOriginBoat())
            {
                AbortAndUnmark();
                return;
            }

            if (phase == Phase.Returning)
            {
                UpdateReturnFrame();
                return;
            }

            if (!item || !portDude || market == null)
            {
                BeginReturn();
                return;
            }

            if (navigatingToMooring)
            {
                UpdateCargoDuringMooringNavigation();
                if (!CrewNavigationCoordinator.Instance.IsPositioningComplete(this))
                    return;

                navigatingToMooring = false;
                Vector3 start = item.transform.position;
                Quaternion startRotation = item.transform.rotation;
                BeginRoute(BuildPortRouteFromBoatAnchor(start, startRotation, haulEndPosition, haulEndRotation, activeRoute), carriesCargo: true);
                return;
            }

            if (!UpdateRoute())
                return;

            if (Vector3.Distance(item.transform.position, portDude.transform.position) <= SellDistance)
            {
                if (!SupercargoTradeService.TrySellCargoAtPort(item, market, goodIndex))
                {
                    BeginCanceledCargoReturn();
                    return;
                }
                BeginReturn();
            }
        }

        public float GetProgress()
        {
            if (Status != WorkRequestStatus.InProgress)
                return 0f;

            if (routeSegments != null && routeSegments.Length > 0)
            {
                float completed = routeSegmentIndex;
                var segment = routeSegments[Mathf.Clamp(routeSegmentIndex, 0, routeSegments.Length - 1)];
                float segmentProgress = segment.Duration <= 0f
                    ? 1f
                    : Mathf.Clamp01((Time.time - routeSegmentStartTime) / segment.Duration);
                return Mathf.Clamp01((completed + segmentProgress) / routeSegments.Length) * 100f;
            }

            return 0f;
        }

        public void Cancel()
        {
            if (concretePositioning)
            {
                CrewNavigationCoordinator.Instance.Cancel(this);
                concretePositioning = false;
            }

            if (Status == WorkRequestStatus.InProgress && item && phase == Phase.Hauling)
            {
                SupercargoTradeService.RemoveStamp(item);
                BeginCanceledCargoReturn();
                return;
            }

            if (phase != Phase.Waiting)
                ReturnCargoToOrigin();
            RestoreCargoCollision();
            RestoreCargoToBoatMass();
            SupercargoTradeService.RemoveStamp(item);
            Complete();
        }

        private float GetHaulSpeed()
        {
            float statBonus = AssignedCrewman != null ? AssignedCrewman.Strength * 0.15f : 0f;
            return HaulSpeed + statBonus;
        }

        private float GetCargoHalfHeight()
        {
            var renderer = item ? item.GetComponent<Renderer>() : null;
            return renderer != null ? renderer.bounds.extents.y : 0.5f;
        }

        private void DisableCargoCollision()
        {
            itemCollider = item ? item.GetComponent<Collider>() : null;
            if (itemCollider != null)
            {
                itemColliderWasEnabled = itemCollider.enabled;
                itemCollider.enabled = false;
            }

            CaptureAndDisableItemChildColliders();

            itemRigidbody = item ? item.GetItemRigidbody() : null;
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

        private void RemoveCargoFromBoatMass()
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

        private void RestoreCargoToBoatMass()
        {
            if (!removedFromBoatMass || boatMass == null || itemRigidbody == null || !item)
                return;

            if (!originBoat)
                originBoat = item.currentActualBoat ? item.currentActualBoat : GameState.currentBoat;

            if (item.currentActualBoat == originBoat)
                boatMass.AddItem(itemRigidbody);

            removedFromBoatMass = false;
        }

        private void RestoreCargoCollision()
        {
            if (itemCollider != null)
                itemCollider.enabled = itemColliderWasEnabled;

            if (itemRigidbody != null)
            {
                SyncSuspendedItemRigidbody(item.transform.position, item.transform.rotation);
                RestoreItemRigidbodyPhysics();
                itemRigidbody.disableCol = itemRigidbodyDisableColWasSet;
                itemRigidbody.enabled = itemRigidbodyWasEnabled;
                itemRigidbody.ToggleCollider(!itemRigidbody.disableCol);
            }

            RestoreItemChildColliders();
        }

        private void CaptureAndDisableItemChildColliders()
        {
            if (!item)
                return;

            itemChildColliders = item.GetComponentsInChildren<Collider>(true);
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

            if (item && item.currentActualBoat && item.currentWalkCol)
            {
                Vector3 boatLocalPosition = item.currentActualBoat.InverseTransformPoint(position);
                Quaternion boatLocalRotation = Quaternion.Inverse(item.currentActualBoat.rotation) * rotation;
                itemBody.transform.position = item.currentWalkCol.TransformPoint(boatLocalPosition);
                itemBody.transform.rotation = item.currentWalkCol.rotation * boatLocalRotation;
            }
            else
            {
                itemBody.transform.position = position;
                itemBody.transform.rotation = rotation;
            }

            itemBody.velocity = Vector3.zero;
            itemBody.angularVelocity = Vector3.zero;
        }

        private void BeginReturn()
        {
            phase = Phase.Returning;
            restoringCanceledCargo = false;
            Vector3 start = portDude ? portDude.transform.position : haulEndPosition;
            Quaternion startRotation = haulEndRotation;
            if (MooringLocator.TryFindActiveRoute(GetReturnWorldPosition(), out var activeRoute))
                BeginRoute(BuildRoute(start, startRotation, GetReturnWorldPosition(), GetReturnWorldRotation(), activeRoute, toPort: false), carriesCargo: false);
            else
                BeginRoute(new[] { CreateSegment(start, startRotation, GetReturnWorldPosition(), GetReturnWorldRotation(), 0f, ReturnSpeed) }, carriesCargo: false);
        }

        private void BeginCanceledCargoReturn()
        {
            phase = Phase.Returning;
            restoringCanceledCargo = true;
            Vector3 start = item ? item.transform.position : haulEndPosition;
            Quaternion startRotation = item ? item.transform.rotation : haulEndRotation;

            if (routeSegmentIndex <= 0)
                BeginRoute(new[] { CreateSegment(start, startRotation, GetReturnWorldPosition(), GetReturnWorldRotation(), 0f, ReturnSpeed) }, carriesCargo: true);
            else if (MooringLocator.TryFindActiveRoute(GetReturnWorldPosition(), out var activeRoute))
                BeginRoute(BuildRoute(start, startRotation, GetReturnWorldPosition(), GetReturnWorldRotation(), activeRoute, toPort: false), carriesCargo: true);
            else
                BeginRoute(new[] { CreateSegment(start, startRotation, GetReturnWorldPosition(), GetReturnWorldRotation(), 0f, ReturnSpeed) }, carriesCargo: true);
        }

        private void UpdateReturnFrame()
        {
            if (!UpdateRoute())
                return;

            if (restoringCanceledCargo)
            {
                ReturnCargoToOrigin();
                RestoreCargoCollision();
                RestoreCargoToBoatMass();
                SupercargoTradeService.RemoveStamp(item);
            }

            Complete();
        }

        private void BeginRoute(HaulSegment[] segments, bool carriesCargo)
        {
            CrewNavigationCoordinator.Instance.TryStopOwnerMotion(this);
            routeSegments = segments;
            routeSegmentIndex = 0;
            routeSegmentStartTime = Time.time;
            routeCarriesCargo = carriesCargo;
            navigatingToMooring = false;
        }

        private bool BeginNavigateToMooring()
        {
            if (activeRoute == null || AssignedCrewman == null)
                return false;

            Quaternion arrivalRotation = GetLookRotation(activeRoute.BoatAnchorWorld, activeRoute.DockWorld, haulStartRotation);
            if (!CrewNavigationCoordinator.Instance.TryRetargetRolePositioning(
                    this,
                    activeRoute.BoatAnchorLocal,
                    arrivalRotation,
                    "haul sell mooring='" + activeRoute.Dock.Mooring.name + "'"))
                return false;

            navigatingToMooring = true;
            routeSegments = null;
            routeSegmentIndex = 0;

            if (CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out _))
                cargoCarryHeightOffset = haulStartPosition.y - crewPosition.y;
            else
                cargoCarryHeightOffset = 0f;

            return true;
        }

        private void UpdateCargoDuringMooringNavigation()
        {
            if (!item || !CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(this, out var crewPosition, out var crewRotation))
                return;

            Vector3 position = crewPosition + crewRotation * Vector3.forward * 0.75f + Vector3.up * cargoCarryHeightOffset;
            item.transform.position = position;
            item.transform.rotation = crewRotation;
            SyncSuspendedItemRigidbody(position, crewRotation);
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

            if (routeCarriesCargo && item)
            {
                item.transform.position = position;
                item.transform.rotation = rotation;
                SyncSuspendedItemRigidbody(position, rotation);
                MoveDeckhandWithCargo(position, rotation);
            }
            else
            {
                CrewNavigationCoordinator.Instance.TrySetPoseOverrideWorld(this, position, rotation);
            }

            if (rawT < 1f)
                return false;

            routeSegmentIndex++;
            routeSegmentStartTime = Time.time;
            return routeSegmentIndex >= routeSegments.Length;
        }

        private HaulSegment[] BuildRoute(
            Vector3 start,
            Quaternion startRotation,
            Vector3 end,
            Quaternion endRotation,
            ActiveMooringRoute activeRoute,
            bool toPort)
        {
            Vector3 boatAnchor = originBoat
                ? originBoat.TransformPoint(activeRoute.BoatAnchorLocal)
                : activeRoute.BoatAnchorWorld;
            Vector3 dock = activeRoute.DockWorld;

            if (toPort)
            {
                return new[]
                {
                    CreateSegment(start, startRotation, boatAnchor, GetLookRotation(start, boatAnchor, startRotation), 0f, GetHaulSpeed()),
                    CreateSegment(boatAnchor, GetLookRotation(start, dock, startRotation), dock, GetLookRotation(boatAnchor, dock, startRotation), JumpHeight, GetHaulSpeed()),
                    CreateSegment(dock, GetLookRotation(dock, end, endRotation), end, endRotation, 0f, GetHaulSpeed())
                };
            }

            return new[]
            {
                CreateSegment(start, startRotation, dock, GetLookRotation(start, dock, startRotation), 0f, ReturnSpeed),
                CreateSegment(dock, GetLookRotation(dock, boatAnchor, startRotation), boatAnchor, GetLookRotation(dock, boatAnchor, startRotation), JumpHeight, ReturnSpeed),
                CreateSegment(boatAnchor, GetLookRotation(boatAnchor, end, endRotation), end, endRotation, 0f, ReturnSpeed)
            };
        }

        private HaulSegment[] BuildPortRouteFromBoatAnchor(
            Vector3 start,
            Quaternion startRotation,
            Vector3 end,
            Quaternion endRotation,
            ActiveMooringRoute route)
        {
            Vector3 dock = route.DockWorld;
            return new[]
            {
                CreateSegment(start, startRotation, dock, GetLookRotation(start, dock, startRotation), JumpHeight, GetHaulSpeed()),
                CreateSegment(dock, GetLookRotation(dock, end, endRotation), end, endRotation, 0f, GetHaulSpeed())
            };
        }

        private HaulSegment CreateSegment(Vector3 start, Quaternion startRotation, Vector3 end, Quaternion endRotation, float arcHeight, float speed)
        {
            return new HaulSegment
            {
                Start = start,
                StartRotation = startRotation,
                End = end,
                EndRotation = endRotation,
                Duration = Mathf.Max(0.2f, Vector3.Distance(start, end) / Mathf.Max(0.1f, speed)),
                ArcHeight = arcHeight
            };
        }

        private static Quaternion GetLookRotation(Vector3 from, Vector3 to, Quaternion fallback)
        {
            Vector3 direction = to - from;
            direction.y = 0f;
            return direction.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(direction.normalized, Vector3.up)
                : fallback;
        }

        private void ReturnCargoToOrigin()
        {
            if (!item)
                return;

            Vector3 position = GetReturnWorldPosition();
            Quaternion rotation = GetReturnWorldRotation();
            item.transform.position = position;
            item.transform.rotation = rotation;
            SyncSuspendedItemRigidbody(position, rotation);
        }

        public void ForceCompleteForSave()
        {
            if (concretePositioning)
            {
                CrewNavigationCoordinator.Instance.Cancel(this);
                concretePositioning = false;
            }

            if (phase != Phase.Waiting)
            {
                ReturnCargoToOrigin();
                RestoreCargoCollision();
                RestoreCargoToBoatMass();
            }

            SupercargoTradeService.RemoveStamp(item);
            Complete();
        }

        private bool IsPlayerOnOriginBoat()
        {
            if (!originBoat && item)
                originBoat = item.currentActualBoat ? item.currentActualBoat : GameState.currentBoat;

            return originBoat && GameState.currentBoat == originBoat;
        }

        private void AbortAndUnmark()
        {
            if (concretePositioning)
            {
                CrewNavigationCoordinator.Instance.Cancel(this);
                concretePositioning = false;
            }

            if (phase != Phase.Waiting)
            {
                ReturnCargoToOrigin();
                RestoreCargoCollision();
                RestoreCargoToBoatMass();
            }
            SupercargoTradeService.RemoveStamp(item);
            Complete();
        }

        private void MoveDeckhandWithCargo(Vector3 cargoPosition, Quaternion cargoRotation)
        {
            if (AssignedCrewman == null)
                return;

            Vector3 crewPosition = cargoPosition - (cargoRotation * Vector3.forward * 0.75f);
            CrewNavigationCoordinator.Instance.TrySetPoseOverrideWorld(this, crewPosition, cargoRotation);
        }

        private Vector3 GetReturnWorldPosition()
        {
            return originBoat
                ? originBoat.TransformPoint(returnLocalPosition)
                : haulStartPosition;
        }

        private Quaternion GetReturnWorldRotation()
        {
            return originBoat
                ? originBoat.rotation * returnLocalRotation
                : haulStartRotation;
        }

        private void Complete()
        {
            RestoreCargoToBoatMass();
            phase = Phase.Waiting;
            restoringCanceledCargo = false;
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
            CrewNavigationCoordinator.Instance.Complete(this);
        }
    }
}
