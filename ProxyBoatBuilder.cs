using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class ProxyBoatBuilder
    {
        private const string Phase = "Phase04";
        private static readonly Vector3 ProxyOrigin = new Vector3(10000f, -5000f, 10000f);
        private const int ProxyLayer = 2;

        internal static ProxyBoat Create(CrewBoatContext context)
        {
            var root = new GameObject("VC_Proxy_Boat_" + context.SaveSceneIndex);
            root.transform.position = ProxyOrigin;
            root.transform.rotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            root.layer = ProxyLayer;
            root.isStatic = true;

            var transformMap = BuildTransformMirror(context.WalkCol, root.transform);
            var sourceColliders = context.WalkCol.GetComponentsInChildren<Collider>(true);
            int copied = 0;
            int skipped = 0;

            foreach (var source in sourceColliders)
            {
                if (!ShouldCopy(source))
                {
                    skipped++;
                    continue;
                }

                if (!transformMap.TryGetValue(source.transform, out var targetTransform))
                {
                    skipped++;
                    continue;
                }

                if (CopyCollider(source, targetTransform.gameObject))
                    copied++;
                else
                    skipped++;
            }

            int generatedDeckSurfaces = AddSyntheticDeckSurfaces(root);
            SetStaticAndLayer(root.transform);
            Bounds bounds = CalculateBounds(root);

            var proxy = new ProxyBoat(root, bounds, sourceColliders.Length, copied, skipped, generatedDeckSurfaces);
            LogCreated(proxy);
            return proxy;
        }

        internal static void Destroy(ProxyBoat proxy)
        {
            if (proxy != null && proxy.Root)
                Object.Destroy(proxy.Root);
        }

        internal static void LogProxy(ProxyBoat proxy)
        {
            if (proxy == null || !proxy.IsValid)
            {
                CrewDebugLog.Warn(Phase, "No proxy boat exists.");
                return;
            }

            CrewDebugLog.Ok(Phase, "Proxy root='" + proxy.Root.name + "'");
            CrewDebugLog.Ok(Phase, "Proxy origin=" + Format(proxy.Root.transform.position));
            CrewDebugLog.Ok(Phase, "Source colliders=" + proxy.SourceColliderCount + ", copied=" + proxy.CopiedColliderCount + ", skipped=" + proxy.SkippedColliderCount);
            CrewDebugLog.Ok(Phase, "Generated deck surfaces=" + proxy.GeneratedDeckSurfaceCount);
            CrewDebugLog.Ok(Phase, "Proxy bounds center=" + Format(proxy.Bounds.center) + ", size=" + Format(proxy.Bounds.size));
        }

        internal static void LogProxyColliderDiagnostics(ProxyBoat proxy, Vector3 sampleLocal)
        {
            if (proxy == null || !proxy.IsValid)
            {
                CrewDebugLog.Warn(Phase, "Proxy collider diagnostics unavailable; proxy is missing.");
                return;
            }

            var colliders = proxy.Root.GetComponentsInChildren<Collider>(true);
            int boxes = colliders.Count(c => c is BoxCollider);
            int capsules = colliders.Count(c => c is CapsuleCollider);
            int spheres = colliders.Count(c => c is SphereCollider);
            int meshes = colliders.Count(c => c is MeshCollider);
            CrewDebugLog.Ok(Phase,
                "Proxy collider diagnostics count=" + colliders.Length
                + " box=" + boxes
                + " capsule=" + capsules
                + " sphere=" + spheres
                + " mesh=" + meshes
                + " sampleLocal=" + Format(sampleLocal));

            var candidates = colliders
                .Select(c => CreateCandidate(proxy, c, sampleLocal))
                .OrderBy(c => c.Score)
                .Take(12)
                .ToList();

            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                CrewDebugLog.Ok(Phase,
                    "Proxy collider candidate[" + i
                    + "] type=" + c.TypeName
                    + " path='" + c.Path
                    + "' topLocal=" + Format(c.TopLocal)
                    + " centerLocal=" + Format(c.CenterLocal)
                    + " size=" + Format(c.Size)
                    + " horizontal=" + c.HorizontalDistance.ToString("0.000")
                    + " vertical=" + c.VerticalDelta.ToString("0.000")
                    + " containsXZ=" + c.ContainsXZ);
            }
        }

        internal static bool TryFindNearestColliderTop(ProxyBoat proxy, Vector3 sampleLocal, out Vector3 topLocal, out string description)
        {
            topLocal = Vector3.zero;
            description = null;
            if (proxy == null || !proxy.IsValid)
                return false;

            var colliders = proxy.Root.GetComponentsInChildren<Collider>(true);
            if (colliders.Length == 0)
                return false;

            var best = colliders
                .Select(c => CreateCandidate(proxy, c, sampleLocal))
                .OrderBy(c => c.Score)
                .FirstOrDefault();

            if (best == null)
                return false;

            topLocal = best.TopLocal;
            description = best.TypeName + " path='" + best.Path + "' horizontal=" + best.HorizontalDistance.ToString("0.000") + " vertical=" + best.VerticalDelta.ToString("0.000");
            return true;
        }

        internal static void ShowProxyMarkers(ProxyBoat proxy)
        {
            if (proxy == null || !proxy.IsValid)
            {
                CrewDebugLog.Warn(Phase, "No proxy boat exists; create one before showing proxy markers.");
                return;
            }

            CreateMarker(proxy.Root.transform, "VC_Proxy_Marker_Origin", Vector3.zero, Color.magenta, 1.5f);
            Vector3 localCenter = proxy.Root.transform.InverseTransformPoint(proxy.Bounds.center);
            CreateMarker(proxy.Root.transform, "VC_Proxy_Marker_BoundsCenter", localCenter, Color.yellow, 1f);
            CrewDebugLog.Ok(Phase, "Created proxy markers at origin and bounds center.");
        }

        private static Dictionary<Transform, Transform> BuildTransformMirror(Transform sourceRoot, Transform proxyRoot)
        {
            var map = new Dictionary<Transform, Transform>();
            map[sourceRoot] = proxyRoot;

            var sourceTransforms = sourceRoot.GetComponentsInChildren<Transform>(true);
            foreach (var source in sourceTransforms)
            {
                if (source == sourceRoot)
                    continue;

                if (!source.gameObject.activeInHierarchy)
                    continue;

                if (!map.TryGetValue(source.parent, out var targetParent))
                    continue;

                var copy = new GameObject("VC_Proxy_" + source.name);
                copy.transform.SetParent(targetParent, false);
                copy.transform.localPosition = source.localPosition;
                copy.transform.localRotation = source.localRotation;
                copy.transform.localScale = SanitizeScale(source.localScale, source);
                copy.layer = ProxyLayer;
                copy.isStatic = true;
                map[source] = copy.transform;
            }

            return map;
        }

        private static bool ShouldCopy(Collider source)
        {
            if (!source || !source.enabled || source.isTrigger || !source.gameObject.activeInHierarchy)
                return false;

            if (HasZeroScale(source.transform))
            {
                CrewDebugLog.Warn(Phase, "Skipping proxy collider with zero scale path='" + GetPath(source.transform) + "'");
                return false;
            }

            if (IsDynamicWalkColAttachment(source.transform))
            {
                CrewDebugLog.Ok(Phase, "Skipping dynamic walk collider path='" + GetPath(source.transform) + "'");
                return false;
            }

            // Skip colliders owned by ship items so they don't carve holes in the NavMesh.
            // ItemRigidbody.EnterBoat() reparents the physics body directly to walkCol, so
            // there is no ShipItem in its parent chain — check both types.
            if (source.GetComponentInParent<ShipItem>() != null)
                return false;
            if (source.GetComponentInParent<ItemRigidbody>() != null)
                return false;

            return true;
        }

        private static Vector3 SanitizeScale(Vector3 scale, Transform source)
        {
            var sanitized = new Vector3(
                Mathf.Max(0.001f, Mathf.Abs(scale.x)),
                Mathf.Max(0.001f, Mathf.Abs(scale.y)),
                Mathf.Max(0.001f, Mathf.Abs(scale.z)));

            if (sanitized != scale)
                CrewDebugLog.Warn(Phase, "Normalizing proxy transform scale path='" + GetPath(source) + "' sourceScale=" + Format(scale));

            return sanitized;
        }

        private static bool HasZeroScale(Transform transform)
        {
            Vector3 scale = transform.lossyScale;
            if (Mathf.Abs(scale.x) <= 0.001f || Mathf.Abs(scale.y) <= 0.001f || Mathf.Abs(scale.z) <= 0.001f)
                return true;

            for (var current = transform; current != null; current = current.parent)
            {
                Vector3 localScale = current.localScale;
                if (Mathf.Abs(localScale.x) <= 0.001f || Mathf.Abs(localScale.y) <= 0.001f || Mathf.Abs(localScale.z) <= 0.001f)
                    return true;
            }

            return false;
        }

        private static bool IsDynamicWalkColAttachment(Transform transform)
        {
            for (var current = transform; current != null; current = current.parent)
            {
                string name = current.name.ToLowerInvariant();
                if (name.Contains("trapdoor") || name.Contains("hatch"))
                    return true;
            }

            return false;
        }

        private static int AddSyntheticDeckSurfaces(GameObject root)
        {
            int created = 0;
            var meshColliders = root.GetComponentsInChildren<MeshCollider>(true);
            foreach (var meshCollider in meshColliders)
            {
                if (!ShouldGenerateDeckSurfaceTiles(root.transform, meshCollider, ref created))
                    continue;

                if (created > 1800)
                {
                    CrewDebugLog.Warn(Phase, "Synthetic deck surface generation hit safety limit; remaining surfaces skipped.");
                    break;
                }
            }

            if (created > 0)
                CrewDebugLog.Ok(Phase, "Generated synthetic deck surface tiles count=" + created);

            return created;
        }

        private static bool ShouldGenerateDeckSurfaceTiles(Transform root, MeshCollider meshCollider, ref int created)
        {
            if (!meshCollider || !meshCollider.enabled)
                return false;

            string name = meshCollider.name.ToLowerInvariant();
            string path = GetPath(meshCollider.transform).ToLowerInvariant();
            bool transitionNamed = path.Contains("stair") || path.Contains("step") || path.Contains("ladder") || path.Contains("companion");
            bool deckNamed = name.Contains("deck") || path.Contains("deck");
            if ((!deckNamed && !transitionNamed) || (!transitionNamed && (name.Contains("trim") || path.Contains("trim"))))
                return false;

            Bounds bounds = meshCollider.bounds;
            Vector3 minLocal = root.InverseTransformPoint(bounds.min);
            Vector3 maxLocal = root.InverseTransformPoint(bounds.max);
            Vector3 localMin = Vector3.Min(minLocal, maxLocal);
            Vector3 localMax = Vector3.Max(minLocal, maxLocal);
            Vector3 localSize = localMax - localMin;
            float area = localSize.x * localSize.z;
            if (area < 2f || localSize.x < 0.5f || localSize.z < 0.5f || (!transitionNamed && localSize.y > 2.25f))
                return false;

            float tileSize = transitionNamed ? 0.75f : 1.5f;
            int before = created;
            int perMesh = 0;
            for (float x = localMin.x + tileSize * 0.5f; x <= localMax.x; x += tileSize)
            {
                for (float z = localMin.z + tileSize * 0.5f; z <= localMax.z; z += tileSize)
                {
                    if (!TrySampleMeshTop(root, meshCollider, new Vector3(x, 0f, z), localMin.y, localMax.y, transitionNamed, out var hitLocal))
                        continue;

                    CreateSyntheticDeckTile(root, meshCollider, hitLocal, tileSize, transitionNamed);
                    created++;
                    perMesh++;
                    if (perMesh >= 500 || created > 1800)
                        break;
                }

                if (perMesh >= 500 || created > 1800)
                    break;
            }

            if (perMesh > 0)
            {
                CrewDebugLog.Ok(Phase,
                    "Generated deck surface tiles from mesh path='" + GetPath(meshCollider.transform)
                    + "' tiles=" + perMesh
                    + " transition=" + transitionNamed
                    + " boundsSize=" + Format(localSize)
                    + " total=" + created);
            }

            return created > before;
        }

        private static bool TrySampleMeshTop(Transform root, MeshCollider meshCollider, Vector3 sampleLocal, float minY, float maxY, bool transition, out Vector3 hitLocal)
        {
            hitLocal = Vector3.zero;
            Vector3 originLocal = new Vector3(sampleLocal.x, maxY + 3f, sampleLocal.z);
            Vector3 originWorld = root.TransformPoint(originLocal);
            Vector3 directionWorld = root.TransformDirection(Vector3.down);
            float distance = Mathf.Max(6f, maxY - minY + 6f);
            if (!meshCollider.Raycast(new Ray(originWorld, directionWorld), out var hit, distance))
                return false;

            Vector3 normalLocal = root.InverseTransformDirection(hit.normal);
            float minNormalY = transition ? 0.15f : 0.45f;
            if (normalLocal.y < minNormalY)
                return false;

            hitLocal = root.InverseTransformPoint(hit.point);
            return true;
        }

        private static void CreateSyntheticDeckTile(Transform root, MeshCollider meshCollider, Vector3 hitLocal, float tileSize, bool transition)
        {
            var deck = new GameObject("VC_Proxy_GeneratedDeckTile_" + meshCollider.name);
            deck.transform.SetParent(root, false);
            deck.transform.localPosition = hitLocal + Vector3.up * 0.03f;
            deck.transform.localRotation = Quaternion.identity;
            deck.transform.localScale = Vector3.one;
            deck.layer = ProxyLayer;
            deck.isStatic = true;

            var box = deck.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = new Vector3(tileSize * 1.05f, transition ? 0.06f : 0.08f, tileSize * 1.05f);
            box.isTrigger = false;
        }

        private static bool CopyCollider(Collider source, GameObject target)
        {
            if (source is BoxCollider box)
            {
                var copy = target.AddComponent<BoxCollider>();
                copy.center = box.center;
                copy.size = box.size;
                copy.isTrigger = false;
                return true;
            }

            if (source is CapsuleCollider capsule)
            {
                var copy = target.AddComponent<CapsuleCollider>();
                copy.center = capsule.center;
                copy.radius = capsule.radius;
                copy.height = capsule.height;
                copy.direction = capsule.direction;
                copy.isTrigger = false;
                return true;
            }

            if (source is SphereCollider sphere)
            {
                var copy = target.AddComponent<SphereCollider>();
                copy.center = sphere.center;
                copy.radius = sphere.radius;
                copy.isTrigger = false;
                return true;
            }

            if (source is MeshCollider mesh)
            {
                if (!mesh.sharedMesh)
                    return false;

                var copy = target.AddComponent<MeshCollider>();
                copy.sharedMesh = mesh.sharedMesh;
                copy.convex = mesh.convex;
                copy.isTrigger = false;
                return true;
            }

            return false;
        }

        private static Bounds CalculateBounds(GameObject root)
        {
            var colliders = root.GetComponentsInChildren<Collider>(true);
            if (colliders.Length == 0)
                return new Bounds(root.transform.position, Vector3.zero);

            var bounds = colliders[0].bounds;
            for (int i = 1; i < colliders.Length; i++)
                bounds.Encapsulate(colliders[i].bounds);
            return bounds;
        }

        private static ColliderCandidate CreateCandidate(ProxyBoat proxy, Collider collider, Vector3 sampleLocal)
        {
            Bounds worldBounds = collider.bounds;
            Vector3 centerLocal = proxy.Root.transform.InverseTransformPoint(worldBounds.center);
            Vector3 minLocal = proxy.Root.transform.InverseTransformPoint(worldBounds.min);
            Vector3 maxLocal = proxy.Root.transform.InverseTransformPoint(worldBounds.max);
            Vector3 localMin = Vector3.Min(minLocal, maxLocal);
            Vector3 localMax = Vector3.Max(minLocal, maxLocal);
            Vector3 closestLocal = new Vector3(
                Mathf.Clamp(sampleLocal.x, localMin.x, localMax.x),
                localMax.y,
                Mathf.Clamp(sampleLocal.z, localMin.z, localMax.z));
            Vector3 delta = closestLocal - sampleLocal;
            float horizontal = new Vector2(delta.x, delta.z).magnitude;
            float vertical = delta.y;
            bool containsXZ = sampleLocal.x >= localMin.x && sampleLocal.x <= localMax.x
                && sampleLocal.z >= localMin.z && sampleLocal.z <= localMax.z;

            return new ColliderCandidate
            {
                TypeName = collider.GetType().Name,
                Path = GetPath(collider.transform),
                CenterLocal = centerLocal,
                TopLocal = closestLocal,
                Size = localMax - localMin,
                HorizontalDistance = horizontal,
                VerticalDelta = vertical,
                ContainsXZ = containsXZ,
                Score = horizontal + Mathf.Abs(vertical) * 0.25f
            };
        }

        private static void SetStaticAndLayer(Transform transform)
        {
            transform.gameObject.layer = ProxyLayer;
            transform.gameObject.isStatic = true;
            foreach (Transform child in transform)
                SetStaticAndLayer(child);
        }

        private static void CreateMarker(Transform parent, string name, Vector3 localPosition, Color color, float size)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = name;
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = localPosition;
            marker.transform.localRotation = Quaternion.identity;
            marker.transform.localScale = Vector3.one * size;
            marker.layer = ProxyLayer;

            var collider = marker.GetComponent<Collider>();
            if (collider)
                Object.Destroy(collider);

            var renderer = marker.GetComponent<Renderer>();
            if (renderer)
                renderer.material.color = color;
        }

        private static void LogCreated(ProxyBoat proxy)
        {
            CrewDebugLog.Ok(Phase, "Created proxy root='" + proxy.Root.name + "'");
            CrewDebugLog.Ok(Phase, "Proxy origin=" + Format(proxy.Root.transform.position));
            CrewDebugLog.Ok(Phase, "Source colliders copied=" + proxy.CopiedColliderCount + ", skipped=" + proxy.SkippedColliderCount + ", total=" + proxy.SourceColliderCount + ", generatedDeckSurfaces=" + proxy.GeneratedDeckSurfaceCount);
            CrewDebugLog.Ok(Phase, "Proxy bounds center=" + Format(proxy.Bounds.center) + ", size=" + Format(proxy.Bounds.size));
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }

        private static string GetPath(Transform transform)
        {
            if (!transform)
                return "null";

            string path = transform.name;
            var current = transform.parent;
            while (current)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private sealed class ColliderCandidate
        {
            internal string TypeName;
            internal string Path;
            internal Vector3 CenterLocal;
            internal Vector3 TopLocal;
            internal Vector3 Size;
            internal float HorizontalDistance;
            internal float VerticalDelta;
            internal bool ContainsXZ;
            internal float Score;
        }
    }
}
