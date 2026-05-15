using UnityEngine;

namespace SailwindVirtualCrew
{
    internal sealed class CrewStation
    {
        internal string Id { get; set; }
        internal string StableKey { get; set; }
        internal string TypeName { get; set; }
        internal string TransformPath { get; set; }
        internal Vector3 RequestedLocalStand { get; set; }
        internal Vector3 ProjectedLocalStand { get; set; }
        internal Quaternion LocalRotation { get; set; }
        internal bool Projected { get; set; }
        internal bool HasCustomLocation { get; set; }
        internal Component Control { get; set; }
    }
}
