using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>How long a crewman has had nothing to do, and so how they pass the time.</summary>
    internal enum CrewDowntimeLevel
    {
        // Recently busy: stays at their saved rest location, where the player placed them near the work.
        AtRest,
        // A couple of minutes idle: shifts about near the rest location.
        LightWander,
        // Longer idle: wanders further, sits, lies down, and joins other idle crew.
        MajorWander,
    }

    /// <summary>
    /// Downtime: what idle crew do between tasks. Only crew with a rest location take part, and never while they
    /// have a task: wandering claims nothing, so any task takes a wandering crewman at once, resets their idle
    /// clock, and sends them back to their rest location afterwards as before.
    /// </summary>
    internal sealed partial class CrewNavigationCoordinator
    {
        private static readonly System.Random DowntimeRandom = new System.Random();

        /// <summary>
        /// Developer: put every idle crewman with a rest location into <paramref name="level"/> and have them act
        /// on it at once, each <paramref name="staggerSeconds"/> after the one before. Returns how many were set.
        /// </summary>
        internal int ForceDowntime(CrewDowntimeLevel level, float staggerSeconds)
        {
            int count = 0;
            foreach (var actor in _actorsByCrew.Values)
            {
                if (actor.IsValid && actor.ForceDowntime(level, count * staggerSeconds))
                    count++;
            }
            CrewDebugLog.Ok(Phase, "Forced downtime level=" + level + " crew=" + count);
            return count;
        }

        private RuntimeActor FindSocialPartner(RuntimeActor seeker)
        {
            var candidates = new List<RuntimeActor>();
            foreach (var actor in _actorsByCrew.Values)
            {
                if (actor != seeker && actor.IsAvailableForSocial)
                    candidates.Add(actor);
            }
            return candidates.Count == 0 ? null : candidates[DowntimeRandom.Next(candidates.Count)];
        }

        private static float DowntimeRange(float min, float max)
        {
            return min + (float)DowntimeRandom.NextDouble() * (max - min);
        }

        private sealed partial class RuntimeActor
        {
            private const float LightWanderAfterSeconds = 120f;
            private const float MajorWanderAfterSeconds = 300f;

            private const float LightWanderRadius = 1f;
            private const float LightMinDelay = 5f;
            private const float LightMaxDelay = 30f;

            private const float MajorWanderRadius = 5f;
            private const float MajorMinDelay = 30f;
            private const float MajorMaxDelay = 120f;
            // Major actions, as cumulative percentages: walk 25, lie down 10, sit 40, join another crewman 25.
            private const int MajorWalkBelow = 25;
            private const int MajorLieBelow = 35;
            private const int MajorSitBelow = 75;

            private const float SocialSeconds = 60f;
            // A wander walk that has not arrived by now is given up.
            private const float WanderWalkTimeout = 25f;
            private const float NavMeshSnap = 1f;

            // Joint poses: how far in front of a standing or seated crewman to stand or sit facing them, and how
            // far beside a lying one to lie down. Legs out needs room for both pairs of legs.
            private const float StandFacingDistance = 1.0f;
            private const float SitFacingDistance = 1.4f;
            private const float LegsOutSitFacingDistance = 1.9f;
            private const float LieBesideDistance = 0.75f;
            // A spot counts as having room when the deck is found this close to it.
            private const float RoomTolerance = 0.3f;
            private const float RoomMaxStep = 0.3f;

            // Lying on the deck, centered on where they stood: the head this far back along the body, this high.
            private const float LieHalfLength = 0.9f;
            private const float LyingHeadAboveDeck = 0.12f;
            // The hip joint over the deck when sitting on it (as the Player Model mod seats the player).
            private const float SeatedHipsAboveDeck = 0.10f;

            // Sitting down, lying down and getting up again play this many times slower than the Player Model mod
            // plays them for the player, by running the body's clock slower while they happen. Its own blends
            // settle in well under a second, so this window covers them at the slower pace.
            private const float PoseEaseSlowdown = 3f;
            private const float PoseTransitionSeconds = 2f;

            private enum DowntimePose { Standing, Sitting, Lying }

            private static readonly CrewSeatPose[] SeatPoses =
                { CrewSeatPose.LegsOut, CrewSeatPose.CrossLegged, CrewSeatPose.KneeUp, CrewSeatPose.KneesHugged };

            // Real time (unscaled), so time acceleration while sleeping does not rush them.
            private static float Now => Time.unscaledTime;

            // When the crewman last went idle. Backdated by the developer buttons, so it can be before the game
            // started (negative); _idleClockRunning says whether it means anything.
            private float _idleSince;
            private bool _idleClockRunning;
            private float _nextDowntimeAction;
            private bool _downtimeWalking;
            private float _downtimeWalkStarted;
            private DowntimePose _downtimePose;
            private CrewSeatPose _downtimeSeatPose;
            // The pose to take on arriving: a joint pose is walked to first.
            private DowntimePose _pendingPose;
            private CrewSeatPose _pendingSeatPose;
            // Null keeps the heading they arrive with; a joint pose turns them to face their partner.
            private Quaternion? _downtimeArrivalFacing;
            // A walk from sitting or lying waits until they are back on their feet.
            private bool _walkPending;
            private float _walkStartAt;
            private Vector3 _walkTargetWorld;
            private Vector3 _walkTargetLocal;
            private float _poseTransitionUntil;
            // A joint pose: both crewmen point at each other. The one who walked over leads.
            private RuntimeActor _socialPartner;
            private bool _socialLeader;
            private float _socialUntil;

            private bool IsDowntimeEligible => ActiveOwner == null && !Crew.IsOccupied && _hasRestLocation;

            internal CrewDowntimeLevel DowntimeLevel
            {
                get
                {
                    if (!_idleClockRunning)
                        return CrewDowntimeLevel.AtRest;

                    float idle = Now - _idleSince;
                    if (idle >= MajorWanderAfterSeconds)
                        return CrewDowntimeLevel.MajorWander;
                    return idle >= LightWanderAfterSeconds ? CrewDowntimeLevel.LightWander : CrewDowntimeLevel.AtRest;
                }
            }

            internal bool IsAvailableForSocial =>
                IsValid && IsDowntimeEligible && DowntimeLevel == CrewDowntimeLevel.MajorWander
                && !_downtimeWalking && _socialPartner == null;

            internal bool ForceDowntime(CrewDowntimeLevel level, float delaySeconds)
            {
                if (!IsDowntimeEligible)
                    return false;

                float threshold = level == CrewDowntimeLevel.MajorWander ? MajorWanderAfterSeconds
                    : level == CrewDowntimeLevel.LightWander ? LightWanderAfterSeconds
                    : 0f;
                EndSocial();
                // Idle long enough to reach the level exactly when it is time to act.
                _idleSince = Now + delaySeconds - threshold - 0.01f;
                _idleClockRunning = true;
                _nextDowntimeAction = Now + delaySeconds;
                CrewDebugLog.Ok(Phase, "Downtime forced crew='" + Crew.Name + "' level=" + level
                    + " actIn=" + delaySeconds.ToString("0.0") + "s restStand=" + _restStandLocalPosition
                    + " here=" + _logicAgent.CurrentLocalPosition);
                return true;
            }

            /// <summary>
            /// Runs each frame the crewman is idle at their rest location's boat. True while downtime is moving
            /// or posing them, so the usual return-to-rest keeps out of the way.
            /// </summary>
            private bool TickDowntime()
            {
                if (!_idleClockRunning)
                {
                    _idleSince = Now;
                    _idleClockRunning = true;
                }

                var level = DowntimeLevel;
                if (level == CrewDowntimeLevel.AtRest)
                    return false;

                _returningToRest = false;

                if (_downtimeWalking)
                {
                    TickDowntimeWalk();
                    return true;
                }

                if (_socialPartner != null)
                {
                    if (IsSocialLinkValid() && Now < _socialUntil)
                        return true;
                    EndSocial();
                }

                if (Now < _nextDowntimeAction)
                    return true;

                if (level == CrewDowntimeLevel.LightWander)
                    StartLightWander();
                else
                    StartMajorAction();
                return true;
            }

            /// <summary>Leaves downtime: the crewman has a task, or has no rest location.</summary>
            private void ResetDowntime()
            {
                if (_idleClockRunning)
                    CrewDebugLog.Ok(Phase, "Downtime reset crew='" + Crew.Name + "' owner=" + (ActiveOwner?.GetType().Name ?? "none")
                        + " task=" + (Crew.CurrentTask?.GetType().Name ?? "none") + " hasRest=" + _hasRestLocation);

                // A task with its own destination has already replaced the wander walk; only stop an orphaned one.
                if (_downtimeWalking && ActiveOwner == null)
                    _logicAgent.Stop();

                _downtimeWalking = false;
                _walkPending = false;
                _downtimePose = DowntimePose.Standing;
                EndSocial();
                _idleClockRunning = false;
            }

            private void StartLightWander()
            {
                _nextDowntimeAction = Now + DowntimeRange(LightMinDelay, LightMaxDelay);
                if (TryPickSpotNearRest(LightWanderRadius, out var spot))
                {
                    bool walking = WalkTo(spot, null, DowntimePose.Standing, CrewSeatPose.LegsOut);
                    CrewDebugLog.Ok(Phase, "Downtime light wander crew='" + Crew.Name + "' spot=" + spot + " walking=" + walking);
                }
            }

            private void StartMajorAction()
            {
                _nextDowntimeAction = Now + DowntimeRange(MajorMinDelay, MajorMaxDelay);
                int roll = DowntimeRandom.Next(100);
                CrewDebugLog.Ok(Phase, "Downtime major action crew='" + Crew.Name + "' roll=" + roll);

                // Anything that cannot be done here and now (no animated body, no room, no one to join) falls back
                // to the next simplest thing, ending at a walk.
                if (roll >= MajorSitBelow && TryStartSocial())
                    return;
                if (roll >= MajorWalkBelow && roll < MajorLieBelow && TryLieDownHere())
                    return;
                if (roll >= MajorWalkBelow && TrySitDownHere())
                    return;
                StartMajorWalk();
            }

            private void StartMajorWalk()
            {
                if (TryPickSpotNearRest(MajorWanderRadius, out var spot))
                {
                    bool walking = WalkTo(spot, null, DowntimePose.Standing, CrewSeatPose.LegsOut);
                    CrewDebugLog.Ok(Phase, "Downtime major walk crew='" + Crew.Name + "' spot=" + spot + " walking=" + walking);
                }
            }

            private bool TrySitDownHere()
            {
                if (!HasAnimatedBody)
                    return false;

                ChangeDowntimePose(DowntimePose.Sitting, SeatPoses[DowntimeRandom.Next(SeatPoses.Length)]);
                CrewDebugLog.Ok(Phase, "Downtime sit crew='" + Crew.Name + "' pose=" + _downtimeSeatPose);
                _poseSync.SetPoseOverride(_logicAgent.CurrentLocalPosition, GetVisualLocalRotation());
                return true;
            }

            private bool TryLieDownHere()
            {
                if (!HasAnimatedBody)
                    return false;

                // The way they face first, then turned, until the deck has room for the whole body.
                Vector3 here = _logicAgent.CurrentLocalPosition;
                Quaternion facing = GetVisualLocalRotation();
                for (int turn = 0; turn < 4; turn++)
                {
                    Quaternion rotation = facing * Quaternion.Euler(0f, 90f * turn, 0f);
                    if (!HasRoomToLie(here, rotation))
                        continue;

                    ChangeDowntimePose(DowntimePose.Lying, _downtimeSeatPose);
                    _poseSync.SetPoseOverride(here, rotation);
                    CrewDebugLog.Ok(Phase, "Downtime lie down crew='" + Crew.Name + "' turn=" + turn);
                    return true;
                }
                return false;
            }

            // Walk over to another idle crewman and take up a pose with them (see the class notes).
            private bool TryStartSocial()
            {
                var partner = CrewNavigationCoordinator.Instance.FindSocialPartner(this);
                if (partner == null)
                    return false;

                Vector3 partnerPosition = partner.GetVisualLocalPosition();
                Quaternion partnerRotation = partner.GetVisualLocalRotation();
                Vector3 partnerForward = partnerRotation * Vector3.forward;
                partnerForward.y = 0f;
                if (partnerForward.sqrMagnitude < 0.001f)
                    return false;
                partnerForward.Normalize();
                Quaternion facingPartner = Quaternion.LookRotation(-partnerForward, Vector3.up);

                Vector3 spot;
                Quaternion rotation;
                DowntimePose pose = partner._downtimePose;
                CrewSeatPose seatPose = partner._downtimeSeatPose;
                switch (pose)
                {
                    case DowntimePose.Sitting:
                        if (!HasAnimatedBody)
                            return false;
                        float distance = seatPose == CrewSeatPose.LegsOut ? LegsOutSitFacingDistance : SitFacingDistance;
                        spot = partnerPosition + partnerForward * distance;
                        rotation = facingPartner;
                        if (!HasRoomToStand(spot, out spot))
                            return false;
                        break;

                    case DowntimePose.Lying:
                        if (!HasAnimatedBody)
                            return false;
                        rotation = partnerRotation;
                        Vector3 right = partnerRotation * Vector3.right;
                        Vector3 side = partnerPosition + right * LieBesideDistance;
                        if (!HasRoomToLie(side, rotation))
                        {
                            side = partnerPosition - right * LieBesideDistance;
                            if (!HasRoomToLie(side, rotation))
                                return false;
                        }
                        spot = side;
                        break;

                    default:
                        spot = partnerPosition + partnerForward * StandFacingDistance;
                        rotation = facingPartner;
                        if (!HasRoomToStand(spot, out spot))
                            return false;
                        break;
                }

                if (!WalkTo(spot, rotation, pose, seatPose))
                    return false;

                // The partner holds their pose while this crewman walks over; the minute together starts on arrival.
                _socialPartner = partner;
                _socialLeader = true;
                _socialUntil = Now + WanderWalkTimeout + SocialSeconds;
                partner._socialPartner = this;
                partner._socialLeader = false;
                partner._socialUntil = _socialUntil;
                CrewDebugLog.Ok(Phase, "Crew='" + Crew.Name + "' joining crew='" + partner.Crew.Name + "' pose=" + pose);
                return true;
            }

            private bool WalkTo(Vector3 localTarget, Quaternion? arrivalFacing, DowntimePose arrivalPose, CrewSeatPose arrivalSeatPose)
            {
                if (!_navMeshProvider.TryGetWorldOnNavMeshQuiet(localTarget, NavMeshSnap, out var targetWorld))
                    return false;

                _walkTargetWorld = targetWorld;
                _walkTargetLocal = _navMeshProvider.WorldToProxyLocal(targetWorld);
                _pendingPose = arrivalPose;
                _pendingSeatPose = arrivalSeatPose;
                _downtimeArrivalFacing = arrivalFacing;
                _downtimeWalking = true;

                // From sitting or lying, get up where they are first; the walk starts once they are standing.
                bool wasDown = _downtimePose != DowntimePose.Standing;
                ChangeDowntimePose(DowntimePose.Standing, _downtimeSeatPose);
                _walkPending = true;
                _walkStartAt = wasDown ? Now + PoseTransitionSeconds : Now;
                if (!wasDown)
                    StartPendingWalk();
                return true;
            }

            private void StartPendingWalk()
            {
                _walkPending = false;
                _poseSync.ClearPoseOverride();
                _poseSync.ClearRotationOverride();
                _logicAgent.SetDestination(_walkTargetWorld, _walkTargetLocal);
                _downtimeWalkStarted = Now;
            }

            private void TickDowntimeWalk()
            {
                if (_walkPending)
                {
                    if (Now >= _walkStartAt)
                        StartPendingWalk();
                    return;
                }

                if (_logicAgent.HasArrived)
                {
                    _logicAgent.Stop();
                    _downtimeWalking = false;
                    // Hold the heading they walked in on, unless the pose needs them to face someone.
                    _poseSync.SetPoseOverride(_logicAgent.CurrentLocalPosition, _downtimeArrivalFacing ?? GetVisualLocalRotation());
                    ChangeDowntimePose(_pendingPose, _pendingSeatPose);
                    if (_socialLeader && IsSocialLinkValid())
                    {
                        _socialUntil = Now + SocialSeconds;
                        _socialPartner._socialUntil = _socialUntil;
                    }
                    return;
                }

                if (Now - _downtimeWalkStarted > WanderWalkTimeout)
                {
                    _logicAgent.Stop();
                    _downtimeWalking = false;
                    EndSocial();
                }
            }

            private bool IsSocialLinkValid()
            {
                var partner = _socialPartner;
                return partner != null && partner.IsValid && partner._socialPartner == this
                    && partner.IsDowntimeEligible && partner.DowntimeLevel == CrewDowntimeLevel.MajorWander;
            }

            // Ends a joint pose for both. Each then picks their next action straight away.
            private void EndSocial()
            {
                var partner = _socialPartner;
                if (partner == null)
                    return;

                _socialPartner = null;
                _nextDowntimeAction = Now;
                if (partner._socialPartner == this)
                {
                    partner._socialPartner = null;
                    partner._nextDowntimeAction = Now;
                }
            }

            private bool TryPickSpotNearRest(float radius, out Vector3 spot)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    Vector2 offset = Random.insideUnitCircle * radius;
                    Vector3 candidate = _restStandLocalPosition + new Vector3(offset.x, 0f, offset.y);
                    if (!_navMeshProvider.TryGetWorldOnNavMeshQuiet(candidate, NavMeshSnap, out var world))
                        continue;

                    Vector3 local = _navMeshProvider.WorldToProxyLocal(world);
                    if (LocalHorizontalDistance(local, _restStandLocalPosition) <= radius + 0.25f)
                    {
                        spot = local;
                        return true;
                    }
                }

                CrewDebugLog.Warn(Phase, "Downtime found no deck within " + radius + " m of rest crew='" + Crew.Name
                    + "' restStand=" + _restStandLocalPosition);
                spot = _restStandLocalPosition;
                return false;
            }

            private bool HasRoomToStand(Vector3 local, out Vector3 onDeck)
            {
                onDeck = local;
                if (!_navMeshProvider.TryGetWorldOnNavMeshQuiet(local, RoomTolerance, out var world))
                    return false;

                onDeck = _navMeshProvider.WorldToProxyLocal(world);
                return LocalHorizontalDistance(onDeck, local) <= RoomTolerance;
            }

            // Deck under the head and the feet, near level with where they stand.
            private bool HasRoomToLie(Vector3 center, Quaternion rotation)
            {
                Vector3 along = rotation * Vector3.forward;
                along.y = 0f;
                if (along.sqrMagnitude < 0.001f)
                    return false;
                along.Normalize();

                foreach (var end in new[] { center - along * LieHalfLength, center + along * LieHalfLength })
                {
                    if (!HasRoomToStand(end, out var onDeck) || Mathf.Abs(onDeck.y - center.y) > RoomMaxStep)
                        return false;
                }
                return true;
            }

            private void ChangeDowntimePose(DowntimePose pose, CrewSeatPose seatPose)
            {
                bool changed = pose != _downtimePose || (pose == DowntimePose.Sitting && seatPose != _downtimeSeatPose);
                _downtimePose = pose;
                _downtimeSeatPose = seatPose;
                if (changed)
                    _poseTransitionUntil = Now + PoseTransitionSeconds;
            }

            /// <summary>
            /// The frame time to run the body on: slowed while a downtime pose change plays. Not when a task has
            /// called them away, so they get up at the usual pace to go to work.
            /// </summary>
            private float ScaleBodyDeltaForDowntime(float deltaTime)
            {
                return IsDowntimeEligible && Now < _poseTransitionUntil ? deltaTime / PoseEaseSlowdown : deltaTime;
            }

            // Sitting or lying is shown every frame it lasts (the body gets up on its own once the calls stop).
            private void ApplyDowntimePose(ICrewBodyAnimator body)
            {
                if (_downtimeWalking || _downtimePose == DowntimePose.Standing || !IsDowntimeEligible)
                    return;

                Transform root = _visualAgent.VisualRoot.transform;
                Vector3 up = root.up;
                Vector3 forward = root.forward;
                if (_downtimePose == DowntimePose.Sitting)
                    body.SetSeat(root.position + up * SeatedHipsAboveDeck, forward, _downtimeSeatPose, root.position.y);
                else
                    body.SetLying(root.position - forward * LieHalfLength + up * LyingHeadAboveDeck, forward, up);
            }
        }
    }
}
