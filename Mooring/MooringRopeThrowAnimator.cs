using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class MooringRopeThrowAnimator
    {
        private const float MinThrowDuration = 0.6f;
        private const float MaxThrowDuration = 1.4f;
        private const float DurationPerMeter = 0.06f;
        private const float MinArcHeight = 0.75f;
        private const float ArcHeightPerMeter = 0.18f;

        internal static float EstimateDuration(Vector3 from, Vector3 to)
        {
            return Mathf.Clamp(Vector3.Distance(from, to) * DurationPerMeter, MinThrowDuration, MaxThrowDuration);
        }

        internal static void ThrowTo(
            PickupableBoatMooringRope rope,
            GPButtonDockMooring dock,
            Func<bool> isCancelled,
            Action<bool> onComplete)
        {
            if (!rope || !dock)
            {
                onComplete?.Invoke(false);
                return;
            }

            if (Plugin.Instance == null)
            {
                onComplete?.Invoke(TryMoorNow(rope, dock));
                return;
            }

            Plugin.Instance.StartCoroutine(ThrowRoutine(rope, dock, isCancelled, onComplete));
        }

        internal static void ThrowBackToVessel(
            PickupableBoatMooringRope rope,
            Func<bool> isCancelled,
            Action<bool> onComplete)
        {
            if (!rope)
            {
                onComplete?.Invoke(false);
                return;
            }

            if (Plugin.Instance == null)
            {
                onComplete?.Invoke(TryUnmoorNow(rope));
                return;
            }

            Plugin.Instance.StartCoroutine(ThrowBackRoutine(rope, isCancelled, onComplete));
        }

        private static IEnumerator ThrowRoutine(
            PickupableBoatMooringRope rope,
            GPButtonDockMooring dock,
            Func<bool> isCancelled,
            Action<bool> onComplete)
        {
            Transform startParent = rope.transform.parent;
            Vector3 startLocalPosition = rope.transform.localPosition;
            Quaternion startLocalRotation = rope.transform.localRotation;
            Vector3 startWorldPosition = rope.transform.position;
            Quaternion startWorldRotation = rope.transform.rotation;
            Vector3 endWorldPosition = dock.transform.position;
            Quaternion endWorldRotation = dock.transform.rotation;

            float distance = Vector3.Distance(startWorldPosition, endWorldPosition);
            float duration = EstimateDuration(startWorldPosition, endWorldPosition);
            float arcHeight = Mathf.Max(MinArcHeight, distance * ArcHeightPerMeter);

            var colliderStates = DisableColliders(rope);
            float startTime = Time.time;
            bool moored = false;

            while (rope && dock)
            {
                if (isCancelled != null && isCancelled())
                    break;

                float elapsed = Time.time - startTime;
                float normalized = Mathf.Clamp01(elapsed / duration);
                float eased = Mathf.SmoothStep(0f, 1f, normalized);

                Vector3 currentStart = startParent
                    ? startParent.TransformPoint(startLocalPosition)
                    : startWorldPosition;
                Vector3 currentEnd = dock.transform.position;
                Vector3 arcOffset = Vector3.up * (Mathf.Sin(eased * Mathf.PI) * arcHeight);

                rope.transform.position = Vector3.Lerp(currentStart, currentEnd, eased) + arcOffset;
                rope.transform.rotation = Quaternion.Slerp(startWorldRotation, endWorldRotation, eased);

                if (normalized >= 1f)
                    break;

                yield return new WaitForEndOfFrame();
            }

            bool cancelled = isCancelled != null && isCancelled();
            if (!cancelled && rope && dock)
                moored = TryMoorNow(rope, dock);

            if (!moored && rope && !rope.IsMoored())
                RestoreRopePose(rope, startParent, startLocalPosition, startLocalRotation, startWorldPosition, startWorldRotation);

            RestoreColliders(colliderStates);
            onComplete?.Invoke(moored);
        }

        private static IEnumerator ThrowBackRoutine(
            PickupableBoatMooringRope rope,
            Func<bool> isCancelled,
            Action<bool> onComplete)
        {
            Vector3 startWorldPosition = rope.transform.position;
            Quaternion startWorldRotation = rope.transform.rotation;
            Vector3 endWorldPosition = MooringLocator.GetRopeAnchorWorld(rope);
            Quaternion endWorldRotation = rope.transform.rotation;

            float distance = Vector3.Distance(startWorldPosition, endWorldPosition);
            float duration = EstimateDuration(startWorldPosition, endWorldPosition);
            float arcHeight = Mathf.Max(MinArcHeight, distance * ArcHeightPerMeter);

            var colliderStates = DisableColliders(rope);
            bool unmoored = TryUnmoorNow(rope);
            float startTime = Time.time;

            while (rope)
            {
                if (isCancelled != null && isCancelled())
                    break;

                float elapsed = Time.time - startTime;
                float normalized = Mathf.Clamp01(elapsed / duration);
                float eased = Mathf.SmoothStep(0f, 1f, normalized);
                Vector3 currentEnd = MooringLocator.GetRopeAnchorWorld(rope);
                Vector3 arcOffset = Vector3.up * (Mathf.Sin(eased * Mathf.PI) * arcHeight);

                rope.transform.position = Vector3.Lerp(startWorldPosition, currentEnd, eased) + arcOffset;
                rope.transform.rotation = Quaternion.Slerp(startWorldRotation, endWorldRotation, eased);

                if (normalized >= 1f)
                    break;

                yield return new WaitForEndOfFrame();
            }

            bool cancelled = isCancelled != null && isCancelled();
            if (rope)
                rope.ResetRopePos();

            RestoreColliders(colliderStates);
            onComplete?.Invoke(unmoored && !cancelled);
        }

        private static bool TryMoorNow(PickupableBoatMooringRope rope, GPButtonDockMooring dock)
        {
            if (!rope || !dock || rope.IsMoored() || dock.spring == null || dock.spring.connectedBody != null)
                return false;

            rope.MoorTo(dock);
            return true;
        }

        private static bool TryUnmoorNow(PickupableBoatMooringRope rope)
        {
            if (!rope || !rope.IsMoored())
                return false;

            rope.Unmoor();
            return true;
        }

        private static List<ColliderState> DisableColliders(PickupableBoatMooringRope rope)
        {
            var states = new List<ColliderState>();
            foreach (var collider in rope.GetComponentsInChildren<Collider>())
            {
                states.Add(new ColliderState(collider, collider.enabled));
                collider.enabled = false;
            }
            return states;
        }

        private static void RestoreColliders(List<ColliderState> colliderStates)
        {
            foreach (var state in colliderStates)
            {
                if (state.Collider)
                    state.Collider.enabled = state.WasEnabled;
            }
        }

        private static void RestoreRopePose(
            PickupableBoatMooringRope rope,
            Transform startParent,
            Vector3 startLocalPosition,
            Quaternion startLocalRotation,
            Vector3 startWorldPosition,
            Quaternion startWorldRotation)
        {
            if (rope.transform.parent == startParent)
            {
                rope.transform.localPosition = startLocalPosition;
                rope.transform.localRotation = startLocalRotation;
                return;
            }

            rope.transform.position = startWorldPosition;
            rope.transform.rotation = startWorldRotation;
        }

        private sealed class ColliderState
        {
            internal ColliderState(Collider collider, bool wasEnabled)
            {
                Collider = collider;
                WasEnabled = wasEnabled;
            }

            internal Collider Collider { get; }
            internal bool WasEnabled { get; }
        }
    }
}
