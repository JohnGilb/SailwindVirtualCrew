using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace SailwindVirtualCrew
{
    internal sealed class ProxyNavMeshNavigationProvider
    {
        private const string Phase = "Phase05";
        private readonly ProxyBoat _proxy;
        private readonly List<NavMeshBuildSource> _sources = new List<NavMeshBuildSource>();
        private NavMeshData _navMeshData;
        private NavMeshDataInstance _navMeshDataInstance;

        internal ProxyNavMeshNavigationProvider(ProxyBoat proxy)
        {
            _proxy = proxy;
        }

        internal bool IsBaked => _navMeshData != null && _navMeshDataInstance.valid;
        internal ProxyBoat Proxy => _proxy;

        internal bool Bake()
        {
            Clear();

            if (_proxy == null || !_proxy.IsValid)
            {
                CrewDebugLog.Warn(Phase, "No proxy boat exists; create one before baking.");
                return false;
            }

            int layerMask = 1 << _proxy.Root.layer;
            var markups = new List<NavMeshBuildMarkup>();
            NavMeshBuilder.CollectSources(
                _proxy.Root.transform,
                layerMask,
                NavMeshCollectGeometry.PhysicsColliders,
                0,
                markups,
                _sources);

            CrewDebugLog.Ok(Phase, "Build sources=" + _sources.Count);
            DumpBuildSourceSummary();
            if (_sources.Count == 0)
            {
                CrewDebugLog.Fail(Phase, "No NavMesh build sources were collected from the proxy.");
                return false;
            }

            var settings = NavMesh.GetSettingsByID(0);
            settings.agentRadius = 0.18f;
            settings.agentHeight = 1.7f;
            settings.agentClimb = 0.45f;
            settings.agentSlope = 50f;
            settings.minRegionArea = 0.05f;

            Bounds localBounds = GetExpandedLocalBounds(_proxy);
            _navMeshData = NavMeshBuilder.BuildNavMeshData(
                settings,
                _sources,
                localBounds,
                _proxy.Root.transform.position,
                _proxy.Root.transform.rotation);

            CrewDebugLog.Ok(Phase, "NavMeshData created=" + (_navMeshData != null));
            if (_navMeshData == null)
            {
                CrewDebugLog.Fail(Phase, "NavMeshBuilder.BuildNavMeshData returned null.");
                return false;
            }

            _navMeshDataInstance = NavMesh.AddNavMeshData(_navMeshData);
            CrewDebugLog.Ok(Phase, "NavMeshDataInstance valid=" + _navMeshDataInstance.valid);
            DumpTriangulationDiagnostics(Vector3.zero, "after-bake");

            if (_navMeshDataInstance.valid
                && _proxy.GeneratedDeckSurfaceCount == 0
                && CountProxyVertices(NavMesh.CalculateTriangulation().vertices) == 0)
            {
                CrewDebugLog.Warn(Phase, "Proxy NavMesh bake produced zero vertices; generating synthetic deck surfaces and retrying.");
                Clear();
                int generated = ProxyBoatBuilder.GenerateSyntheticDeckSurfaces(_proxy);
                if (generated > 0)
                    return Bake();

                CrewDebugLog.Warn(Phase, "Synthetic deck surface generation produced no surfaces; keeping empty bake.");
            }

            return _navMeshDataInstance.valid;
        }

        internal void Clear()
        {
            if (_navMeshDataInstance.valid)
                _navMeshDataInstance.Remove();

            _navMeshDataInstance = new NavMeshDataInstance();
            _navMeshData = null;
            _sources.Clear();
        }

        internal bool SampleLocal(Vector3 localPosition, float maxDistance, out NavMeshHit hit)
        {
            hit = new NavMeshHit();
            if (_proxy == null || !_proxy.IsValid)
            {
                CrewDebugLog.Warn(Phase, "No proxy boat exists; cannot sample NavMesh.");
                return false;
            }

            Vector3 world = _proxy.Root.transform.TransformPoint(localPosition);
            bool success = NavMesh.SamplePosition(world, out hit, maxDistance, NavMesh.AllAreas);
            CrewDebugLog.Ok(Phase,
                "SamplePosition local=" + Format(localPosition)
                + " success=" + success
                + " hit=" + Format(hit.position));
            return success;
        }

        internal void DumpSampleDiagnostics(Vector3 localPosition, float maxDistance, string label)
        {
            if (_proxy == null || !_proxy.IsValid)
            {
                CrewDebugLog.Warn(Phase, "Sample diagnostics unavailable; proxy is missing label='" + label + "'");
                return;
            }

            Vector3 proxyWorld = _proxy.Root.transform.TransformPoint(localPosition);
            Bounds localBounds = GetExpandedLocalBounds(_proxy);
            bool withinExpandedBounds = localBounds.Contains(localPosition);
            CrewDebugLog.Ok(Phase,
                "Sample diagnostics label='" + label
                + "' local=" + Format(localPosition)
                + " proxyWorld=" + Format(proxyWorld)
                + " maxDistance=" + maxDistance.ToString("0.000")
                + " proxyBoundsLocalCenter=" + Format(localBounds.center)
                + " proxyBoundsLocalSize=" + Format(localBounds.size)
                + " withinExpandedBounds=" + withinExpandedBounds);

            bool success = NavMesh.SamplePosition(proxyWorld, out var hit, maxDistance, NavMesh.AllAreas);
            CrewDebugLog.Ok(Phase,
                "Sample diagnostics result label='" + label
                + "' success=" + success
                + " hitWorld=" + Format(hit.position)
                + (success ? " hitLocal=" + Format(WorldToProxyLocal(hit.position)) : ""));

            DumpTriangulationDiagnostics(localPosition, label);
        }

        internal bool TryGetWorldOnNavMesh(Vector3 localPosition, float maxDistance, out Vector3 worldPosition)
        {
            worldPosition = Vector3.zero;
            if (!SampleLocal(localPosition, maxDistance, out var hit))
                return false;

            worldPosition = hit.position;
            return true;
        }

        internal bool TryGetWorldOnNavMeshQuiet(Vector3 localPosition, float maxDistance, out Vector3 worldPosition)
        {
            worldPosition = Vector3.zero;
            if (_proxy == null || !_proxy.IsValid)
                return false;

            Vector3 world = _proxy.Root.transform.TransformPoint(localPosition);
            if (!NavMesh.SamplePosition(world, out var hit, maxDistance, NavMesh.AllAreas))
                return false;

            worldPosition = hit.position;
            return true;
        }

        internal Vector3 WorldToProxyLocal(Vector3 worldPosition)
        {
            if (_proxy == null || !_proxy.IsValid)
                return worldPosition;

            return _proxy.Root.transform.InverseTransformPoint(worldPosition);
        }

        internal bool TryGetNearestNavMeshVertexLocal(Vector3 localPosition, out Vector3 nearestLocal, out float distance)
        {
            nearestLocal = Vector3.zero;
            distance = float.MaxValue;
            if (_proxy == null || !_proxy.IsValid)
                return false;

            var triangulation = NavMesh.CalculateTriangulation();
            if (triangulation.vertices == null || triangulation.vertices.Length == 0)
                return false;

            Bounds proxyBounds = _proxy.Bounds;
            proxyBounds.Expand(8f);
            bool found = false;
            for (int i = 0; i < triangulation.vertices.Length; i++)
            {
                Vector3 world = triangulation.vertices[i];
                if (!proxyBounds.Contains(world))
                    continue;

                Vector3 local = WorldToProxyLocal(world);
                float candidateDistance = Vector3.Distance(localPosition, local);
                if (!found || candidateDistance < distance)
                {
                    found = true;
                    nearestLocal = local;
                    distance = candidateDistance;
                }
            }

            return found;
        }

        internal bool CalculateTestPath(Vector3 fromLocal, Vector3 toLocal)
        {
            if (_proxy == null || !_proxy.IsValid)
            {
                CrewDebugLog.Warn(Phase, "No proxy boat exists; cannot calculate path.");
                return false;
            }

            if (!SampleLocal(fromLocal, 6f, out var fromHit) || !SampleLocal(toLocal, 6f, out var toHit))
            {
                CrewDebugLog.Warn(Phase, "Test path endpoints could not be sampled onto the NavMesh.");
                return false;
            }

            var path = new NavMeshPath();
            bool success = NavMesh.CalculatePath(fromHit.position, toHit.position, NavMesh.AllAreas, path);
            CrewDebugLog.Ok(Phase,
                "Test path success=" + success
                + " status=" + path.status
                + " corners=" + path.corners.Length);
            return success && path.status == NavMeshPathStatus.PathComplete;
        }

        private static Bounds GetExpandedLocalBounds(ProxyBoat proxy)
        {
            Vector3 center = proxy.Root.transform.InverseTransformPoint(proxy.Bounds.center);
            Vector3 size = proxy.Bounds.size;
            size.x += 4f;
            size.y += 4f;
            size.z += 4f;
            return new Bounds(center, size);
        }

        private void DumpBuildSourceSummary()
        {
            int boxes = _sources.Count(s => s.shape == NavMeshBuildSourceShape.Box);
            int meshes = _sources.Count(s => s.shape == NavMeshBuildSourceShape.Mesh);
            int capsules = _sources.Count(s => s.shape == NavMeshBuildSourceShape.Capsule);
            int spheres = _sources.Count(s => s.shape == NavMeshBuildSourceShape.Sphere);
            CrewDebugLog.Ok(Phase,
                "Build source summary box=" + boxes
                + " mesh=" + meshes
                + " capsule=" + capsules
                + " sphere=" + spheres);

            var largest = _sources
                .Select((s, i) => new { Source = s, Index = i, Area = Mathf.Abs(s.size.x * s.size.z), Size = s.size })
                .OrderByDescending(x => x.Area)
                .Take(8)
                .ToList();

            foreach (var item in largest)
            {
                Vector3 center = item.Source.transform.MultiplyPoint3x4(Vector3.zero);
                Vector3 centerLocal = _proxy.Root.transform.InverseTransformPoint(center);
                CrewDebugLog.Ok(Phase,
                    "Build source largest[" + item.Index
                    + "] shape=" + item.Source.shape
                    + " size=" + Format(item.Size)
                    + " centerLocal=" + Format(centerLocal)
                    + " areaXZ=" + item.Area.ToString("0.000"));
            }
        }

        private void DumpTriangulationDiagnostics(Vector3 localPosition, string label)
        {
            var triangulation = NavMesh.CalculateTriangulation();
            int vertexCount = triangulation.vertices == null ? 0 : triangulation.vertices.Length;
            int triangleIndexCount = triangulation.indices == null ? 0 : triangulation.indices.Length;
            int proxyVertexCount = CountProxyVertices(triangulation.vertices);
            CrewDebugLog.Ok(Phase,
                "NavMesh triangulation label='" + label
                + "' vertices=" + vertexCount
                + " proxyVertices=" + proxyVertexCount
                + " triangleIndices=" + triangleIndexCount);

            if (TryGetNearestNavMeshVertexLocal(localPosition, out var nearestLocal, out var distance))
            {
                Vector3 delta = nearestLocal - localPosition;
                CrewDebugLog.Ok(Phase,
                    "Nearest proxy NavMesh vertex label='" + label
                    + "' local=" + Format(nearestLocal)
                    + " distance=" + distance.ToString("0.000")
                    + " horizontal=" + new Vector2(delta.x, delta.z).magnitude.ToString("0.000")
                    + " vertical=" + delta.y.ToString("0.000"));
            }
            else
            {
                CrewDebugLog.Warn(Phase, "No proxy NavMesh vertices found for diagnostics label='" + label + "'");
            }
        }

        private int CountProxyVertices(Vector3[] vertices)
        {
            if (vertices == null || _proxy == null || !_proxy.IsValid)
                return 0;

            Bounds proxyBounds = _proxy.Bounds;
            proxyBounds.Expand(8f);
            int count = 0;
            for (int i = 0; i < vertices.Length; i++)
                if (proxyBounds.Contains(vertices[i]))
                    count++;

            return count;
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }
    }
}
