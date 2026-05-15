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
            var worldBoat = GameState.currentBoat;
            if (!worldBoat)
            {
                //CrewDebugLog.Fail(Phase, "GameState.currentBoat is null; player may not be embarked.");
                return null;
            }

            var topBoat = worldBoat.parent;
            if (!topBoat)
            {
                CrewDebugLog.Fail(Phase, "GameState.currentBoat has no parent; cannot resolve top boat.");
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
                SaveSceneIndex = sceneIndex
            };
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
            CrewDebugLog.Ok(Phase, "currentBoat='" + context.WorldBoat.name + "'");

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
