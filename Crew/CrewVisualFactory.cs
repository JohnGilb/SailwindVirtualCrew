using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class CrewVisualFactory
    {
        private const string Phase = "Phase03";
        private static readonly Vector3 NpcBodyScale = Vector3.one;
        private static readonly List<GameObject> _cachedTemplates = new List<GameObject>();

        internal static CrewAgent SpawnTestCrewVisual(CrewBoatContext context, Vector3 localPosition, Quaternion localRotation, string id = "test-crew-001", int modelIndex = 0, Crewman crew = null)
        {
            var root = new GameObject("VC_VisualCrew_" + id);
            root.transform.SetParent(context.WorldBoat, false);
            root.transform.localPosition = localPosition;
            root.transform.localRotation = localRotation;
            root.transform.localScale = Vector3.one;

            var animatedBody = TryCreateAnimatedBody(root.transform, id, crew);
            if (animatedBody == null && !TryCreateNpcBody(root.transform, modelIndex))
                CreateBody(root.transform);

            var agent = new CrewAgent(id, root, animatedBody);
            CrewDebugLog.Ok(Phase, "Spawned visual crew id='" + agent.Id + "' modelIndex=" + modelIndex
                + " body=" + (animatedBody != null ? "PlayerModel" : "static"));
            CrewDebugLog.Ok(Phase, "Parent worldBoat='" + context.WorldBoat.name + "'");
            LogPose(agent);
            return agent;
        }

        internal static void LogPose(CrewAgent agent)
        {
            if (agent == null || !agent.VisualRoot)
            {
                CrewDebugLog.Warn(Phase, "No test crew visual exists.");
                return;
            }

            var t = agent.VisualRoot.transform;
            CrewDebugLog.Ok(Phase,
                "Local pose=pos" + Format(t.localPosition)
                + ", rot" + Format(t.localEulerAngles)
                + ", parent='" + (t.parent ? t.parent.name : "null") + "'");
        }

        /// <summary>
        /// Swap a static body for a Player Model one, for crew spawned before its NPC template had loaded.
        /// </summary>
        internal static bool TryUpgradeToAnimatedBody(CrewAgent agent, Crewman crew)
        {
            if (agent == null || !agent.VisualRoot || agent.Body != null)
                return false;

            var root = agent.VisualRoot.transform;
            var animatedBody = TryCreateAnimatedBody(root, agent.Id, crew);
            if (animatedBody == null)
                return false;

            // The static bodies are the only children named VC_VisualCrew_*; the Player Model body is "Body".
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var child = root.GetChild(i);
                if (child.name.StartsWith("VC_VisualCrew_"))
                    Object.Destroy(child.gameObject);
            }

            agent.AttachBody(animatedBody);
            CrewDebugLog.Ok(Phase, "Upgraded visual crew id='" + agent.Id + "' to a Player Model body");
            return true;
        }

        // A crewman wears their saved look; one without a look yet gets one, which is kept on them to be saved.
        private static ICrewBodyAnimator TryCreateAnimatedBody(Transform root, string id, Crewman crew)
        {
            if (!PlayerModelCrewBodies.IsEnabled)
                return null;

            string appearance = crew != null ? ResolveAppearance(crew) : PlayerModelCrewBodies.DefaultAppearance(id, false);
            if (string.IsNullOrEmpty(appearance))
                return null;

            var body = PlayerModelCrewBodies.TryCreate(root, "VC " + id, appearance);
            if (body != null && crew != null && string.IsNullOrEmpty(crew.Appearance))
            {
                crew.SetAppearance(appearance);
                CrewDebugLog.Ok(Phase, "Assigned appearance to crew='" + crew.Name + "': " + appearance);
            }
            return body;
        }

        /// <summary>
        /// The crewman's saved look, or the one they get before anyone chooses: seeded by their id, and female for
        /// a female name. Null when the Player Model mod is absent.
        /// </summary>
        internal static string ResolveAppearance(Crewman crew)
        {
            if (crew == null)
                return null;

            if (!string.IsNullOrEmpty(crew.Appearance))
                return crew.Appearance;

            return PlayerModelCrewBodies.DefaultAppearance(crew.Id, VirtualCrewManager.IsFemaleCrewName(crew.Name));
        }

        private static void CreateBody(Transform root)
        {
            var torso = CreatePrimitive("Torso", PrimitiveType.Capsule, root, new Vector3(0f, 0.9f, 0f), Quaternion.identity, new Vector3(0.35f, 0.75f, 0.35f), new Color(0.12f, 0.32f, 0.75f));
            var head = CreatePrimitive("Head", PrimitiveType.Sphere, root, new Vector3(0f, 1.62f, 0f), Quaternion.identity, new Vector3(0.34f, 0.34f, 0.34f), new Color(0.82f, 0.66f, 0.48f));
            var leftArm = CreatePrimitive("LeftArm", PrimitiveType.Capsule, root, new Vector3(-0.34f, 1.02f, 0f), Quaternion.Euler(0f, 0f, 16f), new Vector3(0.14f, 0.45f, 0.14f), new Color(0.12f, 0.32f, 0.75f));
            var rightArm = CreatePrimitive("RightArm", PrimitiveType.Capsule, root, new Vector3(0.34f, 1.02f, 0f), Quaternion.Euler(0f, 0f, -16f), new Vector3(0.14f, 0.45f, 0.14f), new Color(0.12f, 0.32f, 0.75f));
            var leftLeg = CreatePrimitive("LeftLeg", PrimitiveType.Capsule, root, new Vector3(-0.13f, 0.28f, 0f), Quaternion.identity, new Vector3(0.16f, 0.35f, 0.16f), new Color(0.12f, 0.12f, 0.16f));
            var rightLeg = CreatePrimitive("RightLeg", PrimitiveType.Capsule, root, new Vector3(0.13f, 0.28f, 0f), Quaternion.identity, new Vector3(0.16f, 0.35f, 0.16f), new Color(0.12f, 0.12f, 0.16f));

            torso.name = "VC_VisualCrew_Torso";
            head.name = "VC_VisualCrew_Head";
            leftArm.name = "VC_VisualCrew_LeftArm";
            rightArm.name = "VC_VisualCrew_RightArm";
            leftLeg.name = "VC_VisualCrew_LeftLeg";
            rightLeg.name = "VC_VisualCrew_RightLeg";
        }

        private static bool TryCreateNpcBody(Transform root, int modelIndex)
        {
            RefreshTemplates();
            if (_cachedTemplates.Count == 0)
                return false;

            var template = _cachedTemplates[modelIndex % _cachedTemplates.Count];
            var body = Object.Instantiate(template);
            body.name = "VC_VisualCrew_NpcBody";
            body.transform.SetParent(root, false);
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            body.transform.localScale = NpcBodyScale;

            StripGameplayComponents(body);
            EnableRenderers(body);
            CrewDebugLog.Ok(Phase, "Using NPC template[" + (modelIndex % _cachedTemplates.Count) + "]='" + template.name + "'");
            return true;
        }

        private static void RefreshTemplates()
        {
            // Remove destroyed entries from a previous scene.
            _cachedTemplates.RemoveAll(t => !t);
            if (_cachedTemplates.Count > 0) return;

            // All NPC visuals in Sailwind are GameObjects named "Modular NPC".
            // Use NPCAnimations as an entry point and walk up to that ancestor.
            var seen = new System.Collections.Generic.HashSet<int>();
            foreach (var anims in Object.FindObjectsOfType<NPCAnimations>())
            {
                if (!anims || IsVirtualCrewObject(anims.transform)) continue;
                var modularNpc = FindModularNpc(anims.transform);
                if (modularNpc != null && seen.Add(modularNpc.GetInstanceID()))
                    _cachedTemplates.Add(modularNpc);
            }

            if (_cachedTemplates.Count == 0)
                CrewDebugLog.Warn(Phase, "No 'Modular NPC' templates found; using fallback mannequin.");
            else
                CrewDebugLog.Ok(Phase, "NPC template pool size=" + _cachedTemplates.Count);
        }

        private static GameObject FindModularNpc(Transform t)
        {
            while (t != null)
            {
                if (t.name == "Modular NPC") return t.gameObject;
                t = t.parent;
            }
            return null;
        }



        private static bool IsVirtualCrewObject(Transform transform)
        {
            while (transform)
            {
                if (transform.name.StartsWith("VC_"))
                    return true;

                transform = transform.parent;
            }

            return false;
        }

        private static void StripGameplayComponents(GameObject root)
        {
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                Object.Destroy(collider);

            foreach (var rigidbody in root.GetComponentsInChildren<Rigidbody>(true))
                Object.Destroy(rigidbody);

            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                Object.Destroy(behaviour);
        }

        private static void EnableRenderers(GameObject root)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = true;
        }

        private static GameObject CreatePrimitive(string name, PrimitiveType primitiveType, Transform parent, Vector3 localPosition, Quaternion localRotation, Vector3 localScale, Color color)
        {
            var obj = GameObject.CreatePrimitive(primitiveType);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = localPosition;
            obj.transform.localRotation = localRotation;
            obj.transform.localScale = localScale;

            var collider = obj.GetComponent<Collider>();
            if (collider)
                Object.Destroy(collider);

            var renderer = obj.GetComponent<Renderer>();
            if (renderer)
                renderer.material.color = color;

            return obj;
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }
    }
}
