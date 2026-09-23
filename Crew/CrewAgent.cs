using UnityEngine;

namespace SailwindVirtualCrew
{
    internal sealed class CrewAgent
    {
        internal string Id { get; private set; }
        internal GameObject VisualRoot { get; private set; }
        // Null when the body is a static NPC clone or the fallback mannequin.
        internal ICrewBodyAnimator Body { get; private set; }

        internal CrewAgent(string id, GameObject visualRoot, ICrewBodyAnimator body = null)
        {
            Id = id;
            VisualRoot = visualRoot;
            Body = body;
        }

        internal void AttachBody(ICrewBodyAnimator body)
        {
            Body?.Destroy();
            Body = body;
        }

        internal void Destroy()
        {
            Body?.Destroy();
            Body = null;

            if (VisualRoot)
                Object.Destroy(VisualRoot);
        }
    }
}
