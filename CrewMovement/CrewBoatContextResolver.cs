using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class CrewBoatContextResolver
    {
        private const string Phase = "Phase01";

        internal static CrewBoatContext ResolveAndLog()
        {
            var context = Resolve();
            if (context == null)
                return null;

            LogContext(context);
            return context;
        }

        internal static CrewBoatContext Resolve()
        {
            if (!TryResolveBoatTransforms(out var topBoat, out var worldBoat, out bool playerEmbarked))
            {
                //CrewDebugLog.Fail(Phase, "No active vessel context; player may not have visited a boat yet.");
                return null;
            }

            var walkCol = ResolveWalkCol(topBoat, worldBoat);
            if (!walkCol)
            {
                CrewDebugLog.Fail(Phase, "Could not resolve walk collider from BoatRefs or BoatEmbarkCollider.");
                return null;
            }

            var saveable = topBoat.GetComponent<SaveableObject>();
            int sceneIndex = saveable ? saveable.sceneIndex : -1;

            return new CrewBoatContext
            {
                TopBoat = topBoat,
                WorldBoat = worldBoat,
                WalkCol = walkCol,
                SaveSceneIndex = sceneIndex,
                PlayerEmbarked = playerEmbarked
            };
        }

        internal static bool TryResolveBoatTransforms(out Transform topBoat, out Transform worldBoat)
        {
            return TryResolveBoatTransforms(out topBoat, out worldBoat, out _);
        }

        internal static Transform GetActiveTopBoat()
        {
            return TryResolveBoatTransforms(out var topBoat, out _) ? topBoat : null;
        }

        internal static Transform GetActiveWorldBoat()
        {
            return TryResolveBoatTransforms(out _, out var worldBoat) ? worldBoat : null;
        }

        internal static bool IsActiveTopBoat(Transform topBoat)
        {
            return topBoat && TryResolveBoatTransforms(out var activeTopBoat, out _) && activeTopBoat == topBoat;
        }

        internal static string GetActiveVesselKey()
        {
            var worldBoat = GetActiveWorldBoat();
            return worldBoat ? worldBoat.name.Replace("(Clone)", "").Trim() : null;
        }

        private static bool TryResolveBoatTransforms(out Transform topBoat, out Transform worldBoat, out bool playerEmbarked)
        {
            topBoat = null;
            worldBoat = null;
            playerEmbarked = false;

            if (GameState.currentBoat)
            {
                worldBoat = GameState.currentBoat;
                topBoat = worldBoat.parent ? worldBoat.parent : GameState.lastBoat;
                playerEmbarked = GameState.lastBoat && topBoat == GameState.lastBoat;
            }
            else if (GameState.lastBoat)
            {
                topBoat = GameState.lastBoat;
                worldBoat = ResolveWorldBoatFromTopBoat(topBoat);
            }

            if (!topBoat && worldBoat && worldBoat.parent)
                topBoat = worldBoat.parent;

            if (!worldBoat && topBoat)
                worldBoat = ResolveWorldBoatFromTopBoat(topBoat);

            if (!topBoat || !worldBoat)
                return false;

            if (worldBoat.parent && worldBoat.parent != topBoat)
            {
                CrewDebugLog.Warn(Phase,
                    "Resolved world boat parent differs from top boat. world='"
                    + worldBoat.name + "' parent='" + worldBoat.parent.name
                    + "' top='" + topBoat.name + "'");
            }

            return true;
        }

        private static Transform ResolveWorldBoatFromTopBoat(Transform topBoat)
        {
            if (!topBoat)
                return null;

            var refs = topBoat.GetComponent<BoatRefs>();
            if (refs && refs.boatModel)
                return refs.boatModel;

            var embarkColliders = topBoat.GetComponentsInChildren<BoatEmbarkCollider>(true);
            foreach (var embarkCollider in embarkColliders)
                if (embarkCollider && embarkCollider.transform.parent)
                    return embarkCollider.transform.parent;

            return null;
        }

        private static Transform ResolveWalkCol(Transform topBoat, Transform worldBoat)
        {
            var playerWalkCol = ResolvePlayerWalkCol();
            if (playerWalkCol)
                return playerWalkCol;

            var refs = topBoat.GetComponent<BoatRefs>();
            if (refs && refs.walkCol)
                return refs.walkCol;

            var embarkColliders = topBoat.GetComponentsInChildren<BoatEmbarkCollider>(true);
            foreach (var embarkCollider in embarkColliders)
            {
                if (embarkCollider && embarkCollider.walkCollider)
                    return embarkCollider.walkCollider;
            }

            embarkColliders = worldBoat.GetComponentsInChildren<BoatEmbarkCollider>(true);
            foreach (var embarkCollider in embarkColliders)
            {
                if (embarkCollider && embarkCollider.walkCollider)
                    return embarkCollider.walkCollider;
            }

            return null;
        }

        private static Transform ResolvePlayerWalkCol()
        {
            if (Refs.charController == null || Refs.charController.transform == null)
                return null;

            var parent = Refs.charController.transform.parent;
            if (!parent)
                return null;

            if (parent.CompareTag("WalkColBoat") || parent.name.ToLowerInvariant().Contains("walk"))
                return parent;

            return null;
        }

        private static void LogContext(CrewBoatContext context)
        {
            CrewDebugLog.Ok(Phase,
                "worldBoat='" + context.WorldBoat.name
                + "', playerEmbarked=" + context.PlayerEmbarked);

            var saveable = context.Saveable;
            var rigidbody = context.Rigidbody;
            var boatMass = context.BoatMass;
            CrewDebugLog.Ok(Phase,
                "topBoat='" + context.TopBoat.name
                + "', has SaveableObject=" + (saveable != null)
                + ", has Rigidbody=" + (rigidbody != null)
                + ", has BoatMass=" + (boatMass != null));

            CrewDebugLog.Ok(Phase,
                "walkCol='" + context.WalkCol.name
                + "', tag='" + context.WalkCol.tag
                + "', layer=" + context.WalkCol.gameObject.layer);

            if (saveable)
                CrewDebugLog.Ok(Phase, "sceneIndex=" + context.SaveSceneIndex);
            else
                CrewDebugLog.Warn(Phase, "Top boat has no SaveableObject; sceneIndex unavailable.");

            CrewDebugLog.Ok(Phase,
                "Resolved boat context: top='" + context.TopBoat.name
                + "', world='" + context.WorldBoat.name
                + "', walkCol='" + context.WalkCol.name
                + "', sceneIndex=" + context.SaveSceneIndex);
        }
    }
}
