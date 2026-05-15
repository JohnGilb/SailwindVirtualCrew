using UnityEngine;

namespace SailwindVirtualCrew
{
    internal sealed class CrewBoatContext
    {
        internal Transform TopBoat { get; set; }
        internal Transform WorldBoat { get; set; }
        internal Transform WalkCol { get; set; }
        internal int SaveSceneIndex { get; set; }

        internal SaveableObject Saveable => TopBoat ? TopBoat.GetComponent<SaveableObject>() : null;
        internal Rigidbody Rigidbody => TopBoat ? TopBoat.GetComponent<Rigidbody>() : null;
        internal BoatMass BoatMass => TopBoat ? TopBoat.GetComponent<BoatMass>() : null;
    }
}
