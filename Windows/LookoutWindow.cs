using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SailwindVirtualCrew
{
    public class LookoutWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(20, 300, 500, 400);
        private static readonly int windowId = "VirtualCrewLookoutWindow".GetHashCode();
        private Vector2 scrollPos;

        // Cache: island key -> peak height above island root (world units)
        private readonly Dictionary<int, float> _peakCache = new Dictionary<int, float>();

        private float _spyglassZoom = 1f;
        private bool  _spyglassScanned = false;

        private static readonly string[] CompassPoints =
        {
            "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"
        };

        private WindowResizer _resizer;

        public string WindowKey => "LookoutWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private void Update()
        {
            if (Plugin.ToggleCrewWindow.Value.IsDown())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            string title = DeveloperMode.IsEnabled ? "Lookout [Debug]" : "Lookout";
            windowRect.width = DeveloperMode.IsEnabled ? 500f : 280f;
            if (_resizer.UserHeight > 0f) windowRect.height = _resizer.UserHeight;
            windowRect = GUI.Window(windowId, windowRect, DrawWindow, title);
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            var manager = VirtualCrewManager.Instance;
            var lookout = manager.Lookout;

            if (lookout == null)
            {
                var freshest = manager.FreshestCrewman(ShipRole.Lookout);
                GUI.enabled = freshest != null;
                if (GUILayout.Button(freshest != null ? $"Assign freshest Lookout ({freshest.Name})" : "Assign freshest Lookout"))
                    manager.StartLookout(freshest);
                GUI.enabled = true;
                if (freshest == null)
                    GUILayout.Label("No lookout in crew.");
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            if (DeveloperMode.IsEnabled)
                GUILayout.Label($"Lookout: {lookout.Name}  [{lookout.FatigueTag}]   D{lookout.Dexterity}  W{lookout.Wisdom}");
            else
                GUILayout.Label($"Lookout: {lookout.Name}  [{lookout.FatigueTag}]   D{lookout.AdvDexterity}  W{lookout.AdvWisdom}");

            // ── Spyglass ─────────────────────────────────────────────────────
            if (GUILayout.Button("Scan for Spyglass"))
                ScanForSpyglass();

            if (_spyglassScanned)
                GUILayout.Label(_spyglassZoom > 1f
                    ? $"Spyglass: {_spyglassZoom:F1}x zoom"
                    : "Spyglass: none");

            var tracker = IslandDistanceTracker.instance;
            if (tracker == null || tracker.islands == null || tracker.islands.Count == 0)
            {
                GUILayout.Label("No land sighted.");
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            Vector3 playerPos = GetPlayerPosition();
            float cameraY     = GetCameraY();

            var sorted = tracker.islands
                .Where(i => i != null)
                .Select(i => (island: i, dist: Vector3.Distance(i.GetPosition(), playerPos)))
                .OrderBy(x => x.dist)
                .Take(8)
                .ToList();

            // ── Main report ───────────────────────────────────────────────────
            string report = "No land sighted.";
            foreach (var (island, dist) in sorted)
            {
                if (IsLandVisible(island, dist, lookout))
                {
                    string bearing = GetBearing(playerPos, island.GetPosition());
                    report = $"Land Sighted: {bearing}";
                    break;
                }
            }
            GUILayout.Label(report);

            if (!DeveloperMode.IsEnabled)
            {
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            // ── Developer debug section ───────────────────────────────────────
            float threshold = GetVisibilityThreshold(lookout);
            GUILayout.Space(4);
            GUILayout.Label($"Camera Y: {cameraY:F1}  Islands tracked: {tracker.islands.Count}");
            GUILayout.Label($"Threshold: {threshold:F2} deg  Effective: {GetEffectiveVisibilityThreshold(lookout):F2} deg  Night: {(IsNightwatch() ? "yes" : "no")}  Zoom: {_spyglassZoom:F1}x");
            GUILayout.Space(4);

            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Height(280));
            foreach (var (island, dist) in sorted)
                DrawIslandRow(island, dist, lookout);
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear peak cache"))
                _peakCache.Clear();
            if (sorted.Count > 0 && GUILayout.Button("Dump closest renderers"))
                DumpRenderers(sorted[0].island);
            GUILayout.EndHorizontal();

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void DrawIslandRow(IslandHorizon island, float dist, Crewman lookout)
        {
            float initH       = GetInitialHeight(island);
            float currentDrop = initH - island.transform.localPosition.y;
            float peak        = GetPeakAboveRoot(island);
            float visibleH    = peak - currentDrop;
            float angleDeg    = Mathf.Atan2(visibleH, dist) * Mathf.Rad2Deg;
            bool  visible     = IsLandVisible(island, dist, lookout);

            string cacheTag = _peakCache.ContainsKey(GetIslandKey(island)) ? "cached" : (island.SceneLoaded() ? "scene" : "no-scene");
            GUILayout.Label($"{GetIslandName(island)} ({dist:F0}m)  [{cacheTag}]  {(visible ? "VISIBLE" : "")}");
            GUILayout.Label($"  drop:{currentDrop:F1}m  peak:{peak:F0}m  angle:{angleDeg:F2}°");
            GUILayout.Space(2);
        }

        private bool IsLandVisible(IslandHorizon island, float dist, Crewman lookout)
        {
            float peak = GetPeakAboveRoot(island);
            if (peak <= 0f || dist <= 0f) return false;
            float initH       = GetInitialHeight(island);
            float currentDrop = initH - island.transform.localPosition.y;
            float angleDeg    = Mathf.Atan2(peak - currentDrop, dist) * Mathf.Rad2Deg;
            return angleDeg >= GetEffectiveVisibilityThreshold(lookout);
        }

        private static float GetVisibilityThreshold(Crewman lookout)
        {
            float threshold = 1f - lookout.Wisdom * 0.1f;
            if (IsNightwatch()) threshold *= 5f;
            return threshold;
        }

        private float GetEffectiveVisibilityThreshold(Crewman lookout)
        {
            return GetVisibilityThreshold(lookout) / Mathf.Max(1f, _spyglassZoom);
        }

        private static bool IsNightwatch()
        {
            float t = Sun.sun.localTime;
            return t >= 20f || t < 4f;
        }

        private void ScanForSpyglass()
        {
            _spyglassScanned = true;
            _spyglassZoom = LocatorUtils.FindBestSpyglassZoomOnCurrentVessel();
        }

        private static string GetBearing(Vector3 from, Vector3 to)
        {
            Vector3 dir     = to - from;
            float   bearing = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            if (bearing < 0f) bearing += 360f;
            int index = Mathf.RoundToInt(bearing / 22.5f) % 16;
            return CompassPoints[index];
        }

        private float GetPeakAboveRoot(IslandHorizon island)
        {
            int key = GetIslandKey(island);
            if (_peakCache.TryGetValue(key, out float cached)) return cached;

            float maxY = ScanMaxWorldY(island);
            if (maxY == float.MinValue) return 0f;

            float peak = maxY - island.transform.position.y;

            // Delay caching to let renderer bounds settle after scene load.
            if (island.SceneLoaded() && Time.time > 5f)
                _peakCache[key] = peak;

            return peak;
        }

        private float ScanMaxWorldY(IslandHorizon island)
        {
            float maxY = float.MinValue;

            // Try children of the IslandHorizon transform (works if terrain is reparented after scene load)
            foreach (var r in island.GetComponentsInChildren<Renderer>())
                if (r.bounds.min.y < 250f && r.bounds.max.y > maxY)
                    maxY = r.bounds.max.y;

            // Also scan the additively-loaded island scene directly
            if (island.islandIndex > 0 && island.SceneLoaded())
            {
                var scene = SceneManager.GetSceneByBuildIndex(island.islandIndex);
                if (scene.isLoaded)
                    foreach (var root in scene.GetRootGameObjects())
                        foreach (var r in root.GetComponentsInChildren<Renderer>())
                            if (r.bounds.min.y < 250f && r.bounds.max.y > maxY)
                                maxY = r.bounds.max.y;
            }

            return maxY;
        }

        private static void DumpRenderers(IslandHorizon island)
        {
            string name = GetIslandName(island);
            Debug.Log($"[Lookout] Renderer dump for {name} (islandIndex={island.islandIndex})");

            foreach (var r in island.GetComponentsInChildren<Renderer>())
                Debug.Log($"  [child] {r.gameObject.name} ({r.GetType().Name}) maxY={r.bounds.max.y:F1}  path={GetPath(r.transform)}");

            if (island.islandIndex > 0 && island.SceneLoaded())
            {
                var scene = SceneManager.GetSceneByBuildIndex(island.islandIndex);
                if (scene.isLoaded)
                    foreach (var root in scene.GetRootGameObjects())
                        foreach (var r in root.GetComponentsInChildren<Renderer>())
                            Debug.Log($"  [scene] {r.gameObject.name} ({r.GetType().Name}) maxY={r.bounds.max.y:F1}  path={GetPath(r.transform)}");
            }

            Debug.Log($"[Lookout] End dump for {name}");
        }

        private static string GetPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
            return path;
        }

        private static int GetIslandKey(IslandHorizon island)
            => island.islandIndex >= 0 ? island.islandIndex : island.GetInstanceID();

        private float GetInitialHeight(IslandHorizon island)
        {
            try { return Traverse.Create(island).Field("initialHeight").GetValue<float>(); }
            catch { return 0f; }
        }

        private static string GetIslandName(IslandHorizon island)
        {
            if (Port.ports != null)
                foreach (Port port in Port.ports)
                    if (port != null && island.economy != null && port.island == island.economy)
                        return port.GetPortName();

            string goName = island.gameObject.name;
            if (!string.IsNullOrEmpty(goName) && goName != "Island")
                return goName;
            return island.islandIndex >= 0 ? $"Island #{island.islandIndex}" : "Unknown Island";
        }

        private static Vector3 GetPlayerPosition()
        {
            if (Refs.observerMirror != null) return Refs.observerMirror.transform.position;
            if (GameState.currentBoat  != null) return GameState.currentBoat.transform.position;
            return Vector3.zero;
        }

        private static float GetCameraY()
        {
            if (Refs.ovrCameraRig != null) return Refs.ovrCameraRig.position.y;
            return 3f;
        }
    }
}
