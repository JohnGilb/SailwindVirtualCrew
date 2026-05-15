using UnityEngine;

namespace SailwindVirtualCrew
{
    internal sealed class CrewAgent
    {
        internal string Id { get; private set; }
        internal GameObject VisualRoot { get; private set; }

        internal CrewAgent(string id, GameObject visualRoot)
        {
            Id = id;
            VisualRoot = visualRoot;
        }

        internal void Destroy()
        {
            if (VisualRoot)
                Object.Destroy(VisualRoot);
        }
    }
}
