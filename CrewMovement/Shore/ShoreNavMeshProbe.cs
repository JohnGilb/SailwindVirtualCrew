using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace SailwindVirtualCrew
{
    // Developer view of ShoreNavMesh: draws the baked NavMesh and building stand-ins, and walks the same routes the
    // crew take from the dock to the trader, the warehouse and dock cargo.
    internal static class ShoreNavMeshProbe
    {
        private const string Phase = "ShoreNav";
        private const int MaxCargoTargets = 4;

        private static int _shownVersion = -1;
        private static GameObject _root;
        private static GameObject _meshViz;
        private static GameObject _standInViz;
        private static GameObject _paths;

        internal static bool Enabled { get; set; }
        internal static bool ShowMesh { get; set; } = true;
        internal static bool ShowStandIns { get; set; }
        internal static List<string> PathReport { get; } = new List<string>();

        internal static void Tick()
        {
            if (!Enabled)
            {
                if (_root)
                    ClearViz();
                _shownVersion = -1;
                return;
            }

            if (_shownVersion != ShoreNavMesh.Version)
            {
                _shownVersion = ShoreNavMesh.Version;
                ClearViz();
                if (ShoreNavMesh.IsReady)
                {
                    BuildViz();
                    TestPaths();
                }
            }

            if (!_root)
                return;

            // Follow the port through world shifts; the drawings are kept relative to it.
            if (ShoreNavMesh.Frame)
                _root.transform.position = ShoreNavMesh.Frame.position;
            if (_meshViz && _meshViz.activeSelf != ShowMesh)
                _meshViz.SetActive(ShowMesh);
            if (_standInViz && _standInViz.activeSelf != ShowStandIns)
                _standInViz.SetActive(ShowStandIns);
        }

        internal static void Retest()
        {
            if (_root && ShoreNavMesh.IsReady)
                TestPaths();
        }

        private static void ClearViz()
        {
            if (_root)
                Object.Destroy(_root);
            _root = null;
            _meshViz = null;
            _standInViz = null;
            _paths = null;
            PathReport.Clear();
        }

        private static void BuildViz()
        {
            _root = new GameObject("VC_ShoreNav_Viz");
            _root.transform.position = ShoreNavMesh.Frame.position;

            var triangulation = NavMesh.CalculateTriangulation();
            var bounds = ShoreNavMesh.LocalBounds;
            bounds.Expand(1f);
            Vector3 origin = ShoreNavMesh.Frame.position;

            var map = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int i = 0; i + 2 < triangulation.indices.Length; i += 3)
            {
                int a = triangulation.indices[i], b = triangulation.indices[i + 1], c = triangulation.indices[i + 2];
                if (!bounds.Contains(triangulation.vertices[a] - origin)
                    || !bounds.Contains(triangulation.vertices[b] - origin)
                    || !bounds.Contains(triangulation.vertices[c] - origin))
                    continue;

                foreach (int index in new[] { a, b, c })
                {
                    if (!map.TryGetValue(index, out int mapped))
                    {
                        mapped = vertices.Count;
                        // Lifted slightly so the overlay isn't buried in the ground.
                        vertices.Add(triangulation.vertices[index] - origin + Vector3.up * 0.08f);
                        map[index] = mapped;
                    }
                    triangles.Add(mapped);
                }
            }
            _meshViz = CreateMeshObject("VC_ShoreNav_Mesh", vertices, triangles, new Color(0.1f, 0.8f, 1f, 0.35f));
            _meshViz.SetActive(ShowMesh);

            var standIns = ShoreNavMesh.StandIns;
            if (standIns == null || standIns.Sources.Count == 0)
                return;

            var boxVertices = new List<Vector3>(standIns.Sources.Count * 8);
            var boxTriangles = new List<int>(standIns.Sources.Count * 36);
            foreach (var source in standIns.Sources)
            {
                int first = boxVertices.Count;
                Vector3 half = source.size * 0.5f;
                for (int i = 0; i < 8; i++)
                    boxVertices.Add(source.transform.MultiplyPoint3x4(new Vector3(
                        (i & 1) == 0 ? -half.x : half.x,
                        (i & 4) == 0 ? -half.y : half.y,
                        (i & 2) == 0 ? -half.z : half.z)));
                foreach (int index in BoxTriangles)
                    boxTriangles.Add(first + index);
            }
            _standInViz = CreateMeshObject("VC_ShoreNav_StandIns", boxVertices, boxTriangles, new Color(1f, 0.5f, 0.1f, 0.3f));
            _standInViz.SetActive(ShowStandIns);
        }

        private static readonly int[] BoxTriangles =
        {
            0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4,
            2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5
        };

        private static GameObject CreateMeshObject(string name, List<Vector3> vertices, List<int> triangles, Color color)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();

            var go = new GameObject(name) { layer = 2 };
            go.transform.SetParent(_root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = color };
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        // ---------------------------------------------------------------- walks

        private static void TestPaths()
        {
            PathReport.Clear();
            if (_paths)
                Object.Destroy(_paths);
            _paths = new GameObject("VC_ShoreNav_Paths");
            _paths.transform.SetParent(_root.transform, false);

            var boat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (!boat || !ShoreRoute.TryFind(boat.position, out var route))
            {
                AddPathReport("No way ashore (not moored, or anchored with no landing in reach); no walks to test.");
                return;
            }

            Vector3 dock = route.DockWorld;
            CreateMarker("Dock", dock, Color.white);
            CreateLine("Jump", new List<Vector3> { route.BoatAnchorWorld, dock }, Color.white);
            AddPathReport("Ashore via " + route.Label + ", a " + Vector3.Distance(route.BoatAnchorWorld, dock).ToString("0.0") + "m jump.");
            foreach (var target in FindTargets())
            {
                if (ShoreNavMesh.TryBuildWalk(dock, target.Value, 0f, out var points, out string detail))
                {
                    float length = 0f;
                    for (int i = 1; i < points.Count; i++)
                        length += Vector3.Distance(points[i - 1], points[i]);
                    CreateLine(target.Key, points, Color.green);
                    CreateMarker(target.Key, target.Value, Color.green);
                    AddPathReport(target.Key + ": " + detail + ", " + length.ToString("0.0") + "m walked vs "
                        + Vector3.Distance(dock, target.Value).ToString("0.0") + "m straight.");
                }
                else
                {
                    CreateLine(target.Key + " (straight)", new List<Vector3> { dock, target.Value }, Color.red);
                    CreateMarker(target.Key, target.Value, Color.red);
                    AddPathReport(target.Key + ": " + detail + "; walks straight.");
                }
            }
        }

        private static List<KeyValuePair<string, Vector3>> FindTargets()
        {
            var targets = new List<KeyValuePair<string, Vector3>>();
            if (SupercargoTradeService.TryFindNearestPortDude(out var dude))
            {
                targets.Add(new KeyValuePair<string, Vector3>("Trader", dude.transform.position));
                var market = dude.GetPort()?.GetComponent<IslandMarket>();
                var warehouse = market ? market.GetWarehouseArea() : null;
                if (warehouse)
                {
                    var col = warehouse.GetComponent<Collider>();
                    targets.Add(new KeyValuePair<string, Vector3>("Warehouse", col ? col.bounds.center : warehouse.transform.position));
                }
            }

            int cargo = 0;
            foreach (var item in CargoLoadService.FindLoadableDockCargo())
            {
                if (cargo++ >= MaxCargoTargets)
                    break;
                targets.Add(new KeyValuePair<string, Vector3>("Cargo '" + item.name + "'", item.transform.position));
            }
            return targets;
        }

        private static void CreateLine(string name, List<Vector3> worldPoints, Color color)
        {
            var go = new GameObject("VC_ShoreNav_Path " + name) { layer = 2 };
            go.transform.SetParent(_paths.transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = worldPoints.Count;
            Vector3 origin = _root.transform.position;
            for (int i = 0; i < worldPoints.Count; i++)
                line.SetPosition(i, worldPoints[i] - origin + Vector3.up * 0.25f);
            line.startWidth = 0.12f;
            line.endWidth = 0.12f;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = color;
            line.endColor = color;
        }

        private static void CreateMarker(string name, Vector3 world, Color color)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "VC_ShoreNav_Marker " + name;
            Object.Destroy(marker.GetComponent<Collider>());
            marker.layer = 2;
            marker.transform.SetParent(_paths.transform, false);
            marker.transform.position = world + Vector3.up * 0.25f;
            marker.transform.localScale = Vector3.one * 0.5f;
            var renderer = marker.GetComponent<Renderer>();
            renderer.material = new Material(Shader.Find("Sprites/Default")) { color = color };
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        private static void AddPathReport(string message)
        {
            PathReport.Add(message);
            CrewDebugLog.Info(Phase, message);
        }
    }
}
