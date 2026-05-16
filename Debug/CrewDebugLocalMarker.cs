using UnityEngine;

namespace SailwindVirtualCrew
{
    internal sealed class CrewDebugLocalMarker
    {
        private const string Phase = "Phase02";
        private readonly GameObject _walkMarker;
        private readonly GameObject _realMarker;

        internal Vector3 WorldBoatLocalPosition { get; private set; }
        internal Quaternion WorldBoatLocalRotation { get; private set; }

        internal CrewDebugLocalMarker(Vector3 localPosition, Quaternion localRotation)
        {
            WorldBoatLocalPosition = localPosition;
            WorldBoatLocalRotation = localRotation;
            _walkMarker = CreateMarker("VC_Debug_Phase02_WalkMappedMarker", Color.yellow, 0.32f);
            _realMarker = CreateMarker("VC_Debug_Phase02_RealBoatGhost", new Color(1f, 1f, 1f, 0.55f), 0.2f);
        }

        internal void MoveLocal(Vector3 localOffset)
        {
            WorldBoatLocalPosition += localOffset;
        }

        internal void Update(CrewSpaceMapper mapper)
        {
            if (_walkMarker)
            {
                _walkMarker.transform.position = mapper.WalkWorldFromWorldBoatLocal(WorldBoatLocalPosition);
                _walkMarker.transform.rotation = mapper.WalkWorldRotationFromWorldBoatLocal(WorldBoatLocalRotation);
            }

            if (_realMarker)
            {
                _realMarker.transform.position = mapper.RealWorldFromWorldBoatLocal(WorldBoatLocalPosition);
                _realMarker.transform.rotation = mapper.RealWorldRotationFromWorldBoatLocal(WorldBoatLocalRotation);
            }
        }

        internal void LogPose(CrewSpaceMapper mapper)
        {
            Vector3 realWorld = mapper.RealWorldFromWorldBoatLocal(WorldBoatLocalPosition);
            Vector3 walkWorld = mapper.WalkWorldFromWorldBoatLocal(WorldBoatLocalPosition);
            Vector3 roundTripLocal = mapper.WorldBoatLocalFromWalkWorld(walkWorld);
            float error = Vector3.Distance(WorldBoatLocalPosition, roundTripLocal);

            CrewDebugLog.Ok(Phase,
                "Stored marker localPos=" + Format(WorldBoatLocalPosition)
                + ", localRot=" + Format(WorldBoatLocalRotation.eulerAngles));
            CrewDebugLog.Ok(Phase,
                "Real world pos=" + Format(realWorld)
                + ", mapped walk pos=" + Format(walkWorld));
            CrewDebugLog.Ok(Phase, "Mapping roundtrip error=" + error.ToString("0.0000") + "m");
        }

        internal void Destroy()
        {
            if (_walkMarker)
                Object.Destroy(_walkMarker);
            if (_realMarker)
                Object.Destroy(_realMarker);
        }

        private static GameObject CreateMarker(string name, Color color, float size)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = name;
            marker.transform.localScale = Vector3.one * size;

            var collider = marker.GetComponent<Collider>();
            if (collider)
                Object.Destroy(collider);

            var renderer = marker.GetComponent<Renderer>();
            if (renderer)
            {
                renderer.material.color = color;
            }

            return marker;
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }
    }
}
