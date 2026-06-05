using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class StewardRequestNavigation
    {
        internal static bool TryBeginNearPlayer(object owner, Crewman crewman, string label)
        {
            if (!TryGetNearPlayerLocalPose(out var localPosition, out var localRotation))
                return false;

            return CrewNavigationCoordinator.Instance.TryBeginRolePositioning(
                owner, crewman, localPosition, localRotation, label);
        }

        internal static bool TryRetargetNearPlayer(object owner, string label)
        {
            if (!TryGetNearPlayerLocalPose(out var localPosition, out var localRotation))
                return false;

            return CrewNavigationCoordinator.Instance.TryRetargetRolePositioning(owner, localPosition, localRotation, label);
        }

        internal static bool FacePlayer(object owner)
        {
            if (owner == null
                || Refs.observerMirror == null
                || !CrewNavigationCoordinator.Instance.TryGetOwnerWorldPose(owner, out var position, out var rotation))
                return false;

            Vector3 direction = Refs.observerMirror.transform.position - position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.001f)
                rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);

            return CrewNavigationCoordinator.Instance.TrySetPoseOverrideWorld(owner, position, rotation);
        }

        private static bool TryGetNearPlayerLocalPose(out Vector3 localPosition, out Quaternion localRotation)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;

            if (Refs.observerMirror == null)
                return false;

            var context = CrewBoatContextResolver.Resolve();
            if (context == null || !context.WorldBoat)
                return false;

            Transform player = Refs.observerMirror.transform;
            Vector3 worldPosition = player.position - player.forward * 1.25f;
            localPosition = context.WorldBoat.InverseTransformPoint(worldPosition);
            if (CrewNavigationCoordinator.Instance.TryProjectLocalToNavMesh(localPosition, out var projectedLocal))
                localPosition = projectedLocal;

            Vector3 localForward = context.WorldBoat.InverseTransformDirection(player.position - context.WorldBoat.TransformPoint(localPosition));
            localForward.y = 0f;
            if (localForward.sqrMagnitude < 0.001f)
                localForward = context.WorldBoat.InverseTransformDirection(player.forward);
            if (localForward.sqrMagnitude < 0.001f)
                localForward = Vector3.forward;

            localRotation = Quaternion.LookRotation(localForward.normalized, Vector3.up);
            return true;
        }
    }
}
