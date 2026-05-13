using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal sealed class CrewStationScanner
    {
        private const string Phase = "Phase08";
        private readonly CrewBoatContext _context;
        private readonly ProxyNavMeshNavigationProvider _navMesh;

        internal CrewStationScanner(CrewBoatContext context, ProxyNavMeshNavigationProvider navMesh)
        {
            _context = context;
            _navMesh = navMesh;
        }

        internal List<CrewStation> Scan()
        {
            var stations = new List<CrewStation>();
            AddControls(stations, _context.WorldBoat.GetComponentsInChildren<GPButtonSteeringWheel>(true), "helm", 1.15f);
            AddControls(stations, _context.WorldBoat.GetComponentsInChildren<GPButtonRopeWinch>(true), "rope", 0.85f);
            AddControls(stations, _context.WorldBoat.GetComponentsInChildren<BilgePump>(true), "bilge", 0.9f);

            CrewDebugLog.Ok(Phase, "Scanned stations count=" + stations.Count);
            return stations;
        }

        private void AddControls<T>(List<CrewStation> stations, T[] controls, string typeName, float standDistance) where T : Component
        {
            int index = 0;
            int skipped = 0;
            foreach (var control in controls)
            {
                if (!IsLiveControl(control))
                {
                    skipped++;
                    continue;
                }

                var station = BuildStation(control, typeName, index++, standDistance);
                stations.Add(station);
                LogStation(station);
            }

            if (skipped > 0)
                CrewDebugLog.Ok(Phase, "Skipped inactive/unrendered controls type='" + typeName + "' count=" + skipped);
        }

        private static bool IsLiveControl<T>(T control) where T : Component
        {
            if (!control || !control.gameObject.activeInHierarchy)
                return false;

            var behaviour = control as Behaviour;
            if (behaviour != null && !behaviour.enabled)
                return false;

            bool hasEnabledCollider = false;
            var colliders = control.GetComponentsInChildren<Collider>(false);
            foreach (var collider in colliders)
            {
                if (collider && collider.enabled && !collider.isTrigger)
                {
                    hasEnabledCollider = true;
                    break;
                }
            }

            bool hasEnabledRenderer = false;
            var renderers = control.GetComponentsInChildren<Renderer>(false);
            foreach (var renderer in renderers)
            {
                if (renderer && renderer.enabled)
                {
                    hasEnabledRenderer = true;
                    break;
                }
            }

            return hasEnabledCollider || hasEnabledRenderer;
        }

        private CrewStation BuildStation(Component control, string typeName, int index, float standDistance)
        {
            Transform t = control.transform;
            Vector3 anchorWorld = GetStationAnchorWorld(control, typeName);
            Vector3 standWorld = anchorWorld + t.forward * standDistance;
            Vector3 requestedLocal = _context.WorldBoat.InverseTransformPoint(standWorld);
            Vector3 projectedLocal = requestedLocal;
            bool projected = false;

            if (TryProjectStation(typeName, requestedLocal, out var hitWorld))
            {
                projectedLocal = _navMesh.WorldToProxyLocal(hitWorld);
                projected = true;
            }

            Vector3 controlLocal = _context.WorldBoat.InverseTransformPoint(anchorWorld);
            Vector3 facing = controlLocal - projectedLocal;
            facing.y = 0f;
            if (facing.sqrMagnitude < 0.001f)
                facing = Vector3.forward;

            return new CrewStation
            {
                Id = typeName + "-" + index.ToString("00"),
                TypeName = typeName,
                TransformPath = GetPath(t, _context.WorldBoat),
                RequestedLocalStand = requestedLocal,
                ProjectedLocalStand = projectedLocal,
                LocalRotation = Quaternion.LookRotation(facing.normalized, Vector3.up),
                Projected = projected,
                Control = control
            };
        }

        private bool TryProjectStation(string typeName, Vector3 requestedLocal, out Vector3 hitWorld)
        {
            hitWorld = Vector3.zero;
            if (_navMesh == null || !_navMesh.IsBaked)
                return false;

            if (typeName == "helm" && TryProjectHelmToHighestDeck(requestedLocal, out hitWorld))
                return true;

            return _navMesh.TryGetWorldOnNavMeshQuiet(requestedLocal, 4f, out hitWorld);
        }

        private bool TryProjectHelmToHighestDeck(Vector3 requestedLocal, out Vector3 hitWorld)
        {
            hitWorld = Vector3.zero;
            Vector3 bestWorld = Vector3.zero;
            Vector3 bestLocal = Vector3.zero;
            bool found = false;

            float[] yOffsets = { 0f, 1.5f, 3f, 4.5f, 6f, 8f, 10f };
            foreach (float yOffset in yOffsets)
            {
                Vector3 probeLocal = requestedLocal + Vector3.up * yOffset;
                if (!_navMesh.TryGetWorldOnNavMeshQuiet(probeLocal, 2.25f, out var candidateWorld))
                    continue;

                Vector3 candidateLocal = _navMesh.WorldToProxyLocal(candidateWorld);
                Vector2 requestedXZ = new Vector2(requestedLocal.x, requestedLocal.z);
                Vector2 candidateXZ = new Vector2(candidateLocal.x, candidateLocal.z);
                if (Vector2.Distance(requestedXZ, candidateXZ) > 2.25f)
                    continue;

                if (!found || candidateLocal.y > bestLocal.y)
                {
                    found = true;
                    bestWorld = candidateWorld;
                    bestLocal = candidateLocal;
                }
            }

            if (!found)
                return false;

            hitWorld = bestWorld;
            CrewDebugLog.Ok(Phase, "Helm station projected to highest deck local=" + Format(bestLocal));
            return true;
        }

        private static Vector3 GetStationAnchorWorld(Component control, string typeName)
        {
            if (typeName != "helm")
                return control.transform.position;

            var renderers = control.GetComponentsInChildren<Renderer>(false);
            bool hasBounds = false;
            Bounds bounds = new Bounds(control.transform.position, Vector3.zero);
            foreach (var renderer in renderers)
            {
                if (!renderer || !renderer.enabled)
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                    bounds.Encapsulate(renderer.bounds);
            }

            return hasBounds ? bounds.center : control.transform.position;
        }

        private void LogStation(CrewStation station)
        {
            CrewDebugLog.Ok(Phase,
                "Found control type=" + station.Control.GetType().Name
                + " path='" + station.TransformPath + "'");

            string message = "station='" + station.Id
                + "' projected=" + station.Projected
                + " localStand=" + Format(station.ProjectedLocalStand)
                + " requestedLocal=" + Format(station.RequestedLocalStand);

            if (station.Projected)
                CrewDebugLog.Ok(Phase, message);
            else
                CrewDebugLog.Warn(Phase, message + "; projection failed.");
        }

        private static string GetPath(Transform transform, Transform root)
        {
            string path = transform.name;
            var current = transform.parent;
            while (current && current != root)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }
    }
}
