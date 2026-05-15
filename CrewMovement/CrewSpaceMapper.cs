using UnityEngine;

namespace SailwindVirtualCrew
{
    internal sealed class CrewSpaceMapper
    {
        private readonly CrewBoatContext _context;

        internal CrewSpaceMapper(CrewBoatContext context)
        {
            _context = context;
        }

        internal Vector3 WorldBoatLocalFromWorld(Vector3 worldPosition)
        {
            return _context.WorldBoat.InverseTransformPoint(worldPosition);
        }

        internal Quaternion WorldBoatLocalRotationFromWorld(Quaternion worldRotation)
        {
            return Quaternion.Inverse(_context.WorldBoat.rotation) * worldRotation;
        }

        internal Vector3 RealWorldFromWorldBoatLocal(Vector3 worldBoatLocalPosition)
        {
            return _context.WorldBoat.TransformPoint(worldBoatLocalPosition);
        }

        internal Quaternion RealWorldRotationFromWorldBoatLocal(Quaternion worldBoatLocalRotation)
        {
            return _context.WorldBoat.rotation * worldBoatLocalRotation;
        }

        internal Vector3 WalkWorldFromWorldBoatLocal(Vector3 worldBoatLocalPosition)
        {
            return _context.WalkCol.TransformPoint(worldBoatLocalPosition);
        }

        internal Quaternion WalkWorldRotationFromWorldBoatLocal(Quaternion worldBoatLocalRotation)
        {
            return _context.WalkCol.rotation * worldBoatLocalRotation;
        }

        internal Vector3 WorldBoatLocalFromWalkWorld(Vector3 walkWorldPosition)
        {
            return _context.WalkCol.InverseTransformPoint(walkWorldPosition);
        }
    }
}
