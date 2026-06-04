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
        private bool showIgnoredWindow = false;
        private Rect windowRect = new Rect(20, 300, 500, 400);
        private Rect ignoredWindowRect = new Rect(540, 300, 280, 260);
        private static readonly int windowId = "VirtualCrewLookoutWindow".GetHashCode();
        private static readonly int ignoredWindowId = "VirtualCrewIgnoredLandWindow".GetHashCode();
        private Vector2 scrollPos;
        private Vector2 ignoredScrollPos;
        private const float LookoutEyeMarkerDiameter = 0.2f;
        private const float SampleMarkerDiameter = 0.2f;
        private const float MarkerUpdateInterval = 0.25f;

        // Retained for the old peak debugger helpers below; live visibility uses LookoutVisibility.
        private readonly Dictionary<int, float> _peakCache = new Dictionary<int, float>();
        private readonly List<GameObject> _losSampleMarkers = new List<GameObject>();
        private readonly List<LookoutWaveSample> _losDebugSamples = new List<LookoutWaveSample>();
        private GameObject _lookoutEyeMarker;
        private float _nextMarkerUpdateTime;

        private static readonly string[] CompassPoints =
        {
            "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"
        };

        private WindowResizer _resizer;

        public string WindowKey => "LookoutWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 20f, 300f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;

            if (!showWindow || !DeveloperMode.IsEnabled)
            {
                ClearLineOfSightMarkers();
                return;
            }

            if (Time.time >= _nextMarkerUpdateTime)
            {
                _nextMarkerUpdateTime = Time.time + MarkerUpdateInterval;
                UpdateLineOfSightMarkers();
            }
        }

        private void OnDestroy()
        {
            ClearLineOfSightMarkers();
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            string title = DeveloperMode.IsEnabled ? "Lookout [Debug]" : "Lookout";
            windowRect.width = DeveloperMode.IsEnabled ? 500f : 280f;
            if (_resizer.UserHeight > 0f) windowRect.height = _resizer.UserHeight;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, title);

            if (showIgnoredWindow)
                ignoredWindowRect = WindowLayoutUtility.DrawClampedWindow(ignoredWindowId, ignoredWindowRect, DrawIgnoredWindow, "Ignored Land");
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
                DrawLookoutStationControls(manager);
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

            float spyglassZoom = manager.GetLookoutSpyglassZoom();
            if (manager.LookoutSpyglassScanned)
                GUILayout.Label(spyglassZoom > 1f
                    ? $"Spyglass: {spyglassZoom:F1}x effective zoom"
                    : "Spyglass: none");

            DrawLookoutStationControls(manager);

            var tracker = IslandDistanceTracker.instance;
            if (tracker == null || tracker.islands == null || tracker.islands.Count == 0)
            {
                GUILayout.Label("No land sighted.");
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            Vector3 playerPos = GetPlayerPosition();
            Vector3 observerPos = GetObservationEyePosition(lookout);
            float cameraY     = GetCameraY();

            var sorted = tracker.islands
                .Where(i => i != null)
                .Select(i => (island: i, dist: Vector3.Distance(i.GetPosition(), observerPos)))
                .OrderBy(x => x.dist)
                .Take(8)
                .ToList();

            // ── Main report ───────────────────────────────────────────────────
            string report = "Scanning the horizon";
            bool hasAnyCertainty = false;
            IslandHorizon reportedIsland = null;
            foreach (var (island, dist) in sorted)
            {
                bool ignored = manager.IsLookoutIgnored(island);
                float certainty = GetLookoutCertainty(island);
                if (!ignored && certainty > 0f)
                    hasAnyCertainty = true;

                if (!ignored && certainty >= 1f)
                {
                    string bearing = GetBearing(playerPos, island.GetPosition());
                    if (LookoutIslandKnowledge.TryIdentifyIsland(island, observerPos, lookout, spyglassZoom, out string islandName, out _))
                    {
                        manager.RememberLookoutIslandName(island, islandName);
                        report = $"Land Sighted: {islandName} ({bearing})";
                    }
                    else if (manager.TryGetRememberedLookoutIslandName(island, out islandName))
                    {
                        report = $"Land Sighted: {islandName} ({bearing})";
                    }
                    else
                    {
                        report = $"Land Sighted: {bearing}";
                    }
                    reportedIsland = island;
                    break;
                }
            }
            if (report == "Scanning the horizon" && hasAnyCertainty)
                report = "Focusing on something.";
            GUILayout.Label(report);
            DrawIgnoreControls(manager, reportedIsland);

            if (!DeveloperMode.IsEnabled)
            {
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            // ── Developer debug section ───────────────────────────────────────
            float threshold = LookoutVisibility.GetBaseVisibilityThreshold(lookout);
            GUILayout.Space(4);
            GUILayout.Label($"Camera Y: {cameraY:F1}  Islands tracked: {tracker.islands.Count}");
            GUILayout.Label($"Threshold: {threshold:F2} deg  Effective: {LookoutVisibility.GetEffectiveVisibilityThreshold(lookout, spyglassZoom):F2} deg  Zoom: {spyglassZoom:F1}x");
            GUILayout.Label($"Wave LOS: first {LookoutVisibility.MaxWaveOcclusionDistance:F0}m  step ~{LookoutVisibility.WaveSampleSpacing:F1}m  clearance {LookoutVisibility.WaveOcclusionClearance:F1}m");
            DrawGroundingRiskDebug();
            GUILayout.Space(4);

            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Height(280));
            foreach (var (island, dist) in sorted)
                DrawIslandVisibilityRow(island, dist, lookout);
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (sorted.Count > 0 && GUILayout.Button("Dump closest renderers"))
                DumpRenderers(sorted[0].island);
            GUILayout.EndHorizontal();

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void DrawGroundingRiskDebug()
        {
            var risk = LookoutGroundingRisk.LastResult;
            string hitTag = risk.HasHit
                ? $"hit {risk.HitColliderName} @ {risk.HitDistance:F0}m"
                : risk.Reason;
            string cooldownTag = risk.CooldownRemainingSeconds > 0f
                ? $" cooldown {risk.CooldownRemainingSeconds / 60f:F1}m"
                : "";
            GUILayout.Label($"Grounding: {risk.Speed:F1}m/s  {risk.ProjectedDistance:F0}m/60s  {hitTag}{cooldownTag}");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Sample Grounding"))
                LookoutGroundingRisk.ForceSample();
            if (GUILayout.Button("Dump Grounding"))
                LookoutGroundingRisk.DumpLatest();
            if (GUILayout.Button(LookoutGroundingRisk.DebugVisualsEnabled ? "Hide Grounding Ray" : "Show Grounding Ray"))
            {
                if (LookoutGroundingRisk.DebugVisualsEnabled)
                    LookoutGroundingRisk.ClearDebugVisuals();
                else
                    LookoutGroundingRisk.ShowDebugVisuals();
            }
            GUILayout.EndHorizontal();
        }

        private void DrawIgnoreControls(VirtualCrewManager manager, IslandHorizon reportedIsland)
        {
            GUILayout.BeginHorizontal();
            GUI.enabled = reportedIsland != null;
            if (GUILayout.Button("Ignore for 24h"))
                manager.IgnoreLookoutIsland(reportedIsland, 24f);
            GUI.enabled = true;

            bool hasIgnored = manager.HasIgnoredLookoutIslands();
            GUI.enabled = hasIgnored || showIgnoredWindow;
            if (GUILayout.Button(showIgnoredWindow ? "Hide Ignored" : "Show Ignored"))
                showIgnoredWindow = !showIgnoredWindow;
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (!hasIgnored)
                showIgnoredWindow = false;
        }

        private void DrawIgnoredWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            var manager = VirtualCrewManager.Instance;
            var tracker = IslandDistanceTracker.instance;
            if (tracker == null || tracker.islands == null)
            {
                GUILayout.Label("No ignored landmasses.");
                GUI.DragWindow();
                return;
            }

            var ignored = tracker.islands
                .Where(i => i != null && manager.IsLookoutIgnored(i))
                .Select(i => (island: i, remaining: manager.GetLookoutIgnoreRemainingHours(i)))
                .Where(x => x.remaining > 0f)
                .OrderBy(x => GetBearing(GetPlayerPosition(), x.island.GetPosition()))
                .ToList();

            if (ignored.Count == 0)
            {
                GUILayout.Label("No ignored landmasses.");
                GUI.DragWindow();
                return;
            }

            var lookout = manager.Lookout;
            Vector3 playerPos = GetPlayerPosition();
            Vector3 observerPos = lookout != null ? GetObservationEyePosition(lookout) : GetPlayerEyePosition();

            ignoredScrollPos = GUILayout.BeginScrollView(ignoredScrollPos, GUILayout.Height(190));
            foreach (var item in ignored)
            {
                IslandHorizon island = item.island;
                string bearing = GetBearing(playerPos, island.GetPosition());
                string label = bearing;
                if (lookout != null
                    && LookoutIslandKnowledge.TryIdentifyIsland(island, observerPos, lookout, GetLookoutSpyglassZoom(), out string islandName, out _))
                {
                    manager.RememberLookoutIslandName(island, islandName);
                    label = $"{islandName} ({bearing})";
                }
                else if (manager.TryGetRememberedLookoutIslandName(island, out islandName))
                {
                    label = $"{islandName} ({bearing})";
                }

                if (GUILayout.Button($"{label}  {item.remaining:F1}h"))
                    manager.ClearLookoutIgnore(island);
            }
            GUILayout.EndScrollView();

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

        private void DrawLookoutStationControls(VirtualCrewManager manager)
        {
            GUILayout.Space(4);
            bool hasStation = manager.TryGetLookoutStation(out var station);
            if (hasStation)
            {
                Vector3 local = ToVector3(station.localPosition);
                string stationLabel = "Station: " + (station.isCrowsNest ? "Crow's Nest" : "Deck");
                if (DeveloperMode.IsEnabled)
                    stationLabel += "  " + Format(local);
                GUILayout.Label(stationLabel);
            }
            else
            {
                GUILayout.Label("Station: none");
            }

            GUILayout.BeginHorizontal();
            if (hasStation)
            {
                if (GUILayout.Button("Clear Lookout Station"))
                    CrewNavigationCoordinator.Instance.ClearLookoutStation();
            }
            else if (GUILayout.Button("Set Lookout Station Here"))
            {
                CrewNavigationCoordinator.Instance.SetLookoutStationAtPlayer();
            }
            GUILayout.EndHorizontal();
        }

        private void DrawIslandVisibilityRow(IslandHorizon island, float dist, Crewman lookout)
        {
            if (!LookoutVisibility.TryEvaluate(island, GetObservationEyePosition(lookout), lookout, GetLookoutSpyglassZoom(), out var visibility))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{GetIslandName(island)} ({dist:F0}m)  [no-peak]");
                if (GUILayout.Button("Dump Renderers", GUILayout.Width(120)))
                    DumpRenderers(island);
                GUILayout.EndHorizontal();
                GUILayout.Space(2);
                return;
            }

            string waveTag = visibility.WaveBlocked
                ? $"WAVE BLOCK {visibility.WaveBlockDistance:F0}m"
                : "waves clear";
            float certainty = GetLookoutCertainty(island);
            float ignoreRemaining = VirtualCrewManager.Instance.GetLookoutIgnoreRemainingHours(island);
            var idInfo = LookoutIslandKnowledge.GetIdentificationInfo(island, GetObservationEyePosition(lookout), lookout, GetLookoutSpyglassZoom());
            string visitedTag = idInfo.HasVisited ? "visited" : "unvisited";
            string identifiedTag = idInfo.Identified
                ? "identified"
                : VirtualCrewManager.Instance.TryGetRememberedLookoutIslandName(island, out _)
                    ? "remembered"
                    : "unidentified";
            string clearTag = idInfo.CurrentlyVisible ? "clear" : "blocked";
            string ignoreTag = ignoreRemaining > 0f ? $"IGNORED {ignoreRemaining:F1}h" : "";
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{GetIslandName(island)} ({dist:F0}m)  certainty:{certainty:F2}  [{(island.SceneLoaded() ? "scene" : "horizon")}]  {(certainty >= 1f ? "SIGHTED" : "")} {ignoreTag}");
            if (GUILayout.Button("Dump Renderers", GUILayout.Width(120)))
                DumpRenderers(island);
            GUILayout.EndHorizontal();
            GUILayout.Label($"  drop:{visibility.CurrentDrop:F1}m  peak:{visibility.PeakAboveRoot:F0}m  angle:{visibility.AngleDeg:F2} deg  {waveTag}");
            GUILayout.Label($"  name:{identifiedTag}  visit:{visitedTag}  current:{clearTag}  identify angle:{idInfo.EffectiveAngleDeg:F2}>{LookoutIslandKnowledge.IdentificationAngleDeg:F1} deg");
            if (visibility.WaveSampleCount > 0)
                GUILayout.Label($"  wave scan:{visibility.WaveSampleCount} samples / {visibility.WaveSampleMaxDistance:F0}m  spacing:{visibility.WaveSampleSpacing:F1}m");
            else
                GUILayout.Label("  wave scan: skipped before wave test");
            if (visibility.WaveBlocked)
                GUILayout.Label($"  wave:{visibility.WaveBlockHeight:F1}m  sightline:{visibility.SightlineHeightAtBlock:F1}m");
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
            return GetVisibilityThreshold(lookout) / Mathf.Max(1f, GetLookoutSpyglassZoom());
        }

        private static bool IsNightwatch()
        {
            float t = Sun.sun.localTime;
            return t >= 20f || t < 4f;
        }

        private void ScanForSpyglass()
        {
            VirtualCrewManager.Instance.ScanLookoutSpyglass();
        }

        private static float GetLookoutSpyglassZoom()
        {
            return VirtualCrewManager.Instance.GetLookoutSpyglassZoom();
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
            float islandRootY = island.transform.position.y;

            // Try children of the IslandHorizon transform (works if terrain is reparented after scene load)
            foreach (var r in island.GetComponentsInChildren<Renderer>())
                if (LookoutVisibility.IsPeakCandidateRenderer(r, islandRootY, maxY))
                    maxY = r.bounds.max.y;

            // Also scan the additively-loaded island scene directly
            if (island.islandIndex > 0 && island.SceneLoaded())
            {
                var scene = SceneManager.GetSceneByBuildIndex(island.islandIndex);
                if (scene.isLoaded)
                    foreach (var root in scene.GetRootGameObjects())
                        foreach (var r in root.GetComponentsInChildren<Renderer>())
                            if (LookoutVisibility.IsPeakCandidateRenderer(r, islandRootY, maxY))
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
            if (LookoutIslandKnowledge.TryGetPortName(island, out string portName))
                return portName;

            string goName = island.gameObject.name;
            if (!string.IsNullOrEmpty(goName) && goName != "Island")
                return goName;
            return island.islandIndex >= 0 ? $"Island #{island.islandIndex}" : "Unknown Island";
        }

        private static Vector3 GetPlayerPosition()
        {
            if (Refs.observerMirror != null) return Refs.observerMirror.transform.position;
            var worldBoat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (worldBoat != null) return worldBoat.transform.position;
            return Vector3.zero;
        }

        private static float GetCameraY()
        {
            if (Refs.ovrCameraRig != null) return Refs.ovrCameraRig.position.y;
            return 3f;
        }

        private void UpdateLineOfSightMarkers()
        {
            var lookout = VirtualCrewManager.Instance.Lookout;
            if (lookout == null || !TryGetLookoutEyePosition(lookout, out var eyeWorld))
            {
                ClearLineOfSightMarkers();
                return;
            }

            EnsureMarker(ref _lookoutEyeMarker, "VC_Lookout_Debug_ModelTop", Color.cyan, LookoutEyeMarkerDiameter);
            _lookoutEyeMarker.transform.position = eyeWorld;

            var tracker = IslandDistanceTracker.instance;
            if (tracker == null || tracker.islands == null || tracker.islands.Count == 0)
            {
                SetSampleMarkerCount(0);
                return;
            }

            var closest = tracker.islands
                .Where(i => i != null)
                .OrderBy(i => Vector3.Distance(i.GetPosition(), eyeWorld))
                .FirstOrDefault();
            if (closest == null
                || !LookoutVisibility.TryEvaluate(closest, eyeWorld, lookout, GetLookoutSpyglassZoom(), out _, _losDebugSamples))
            {
                SetSampleMarkerCount(0);
                return;
            }

            SetSampleMarkerCount(_losDebugSamples.Count);
            for (int i = 0; i < _losDebugSamples.Count; i++)
            {
                var sample = _losDebugSamples[i];
                var marker = _losSampleMarkers[i];
                marker.transform.position = sample.SightlineWorldPosition;
                marker.transform.localScale = Vector3.one * SampleMarkerDiameter;
                SetMarkerColor(marker, sample.BlocksLineOfSight ? Color.red : Color.yellow);
            }
        }

        private void SetSampleMarkerCount(int count)
        {
            while (_losSampleMarkers.Count < count)
            {
                GameObject marker = null;
                EnsureMarker(ref marker, "VC_Lookout_Debug_WaveSample", Color.yellow, SampleMarkerDiameter);
                _losSampleMarkers.Add(marker);
            }

            while (_losSampleMarkers.Count > count)
            {
                int last = _losSampleMarkers.Count - 1;
                if (_losSampleMarkers[last])
                    Destroy(_losSampleMarkers[last]);
                _losSampleMarkers.RemoveAt(last);
            }
        }

        private static void EnsureMarker(ref GameObject marker, string name, Color color, float diameter)
        {
            if (marker)
                return;

            marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = name;
            marker.transform.localScale = Vector3.one * diameter;

            var collider = marker.GetComponent<Collider>();
            if (collider)
                Destroy(collider);

            SetMarkerColor(marker, color);
        }

        private static void SetMarkerColor(GameObject marker, Color color)
        {
            if (!marker)
                return;

            var renderer = marker.GetComponent<Renderer>();
            if (renderer)
                renderer.material.color = color;
        }

        private void ClearLineOfSightMarkers()
        {
            if (_lookoutEyeMarker)
                Destroy(_lookoutEyeMarker);
            _lookoutEyeMarker = null;

            foreach (var marker in _losSampleMarkers)
                if (marker)
                    Destroy(marker);
            _losSampleMarkers.Clear();
            _losDebugSamples.Clear();
        }

        private Vector3 GetObservationEyePosition(Crewman lookout)
        {
            return TryGetLookoutEyePosition(lookout, out var eyeWorld)
                ? eyeWorld
                : GetPlayerEyePosition();
        }

        private static bool TryGetLookoutEyePosition(Crewman lookout, out Vector3 eyeWorld)
        {
            return CrewNavigationCoordinator.Instance.TryGetLookoutEyeWorldPosition(lookout, out eyeWorld);
        }

        private static float GetLookoutCertainty(IslandHorizon island)
        {
            return VirtualCrewManager.Instance.GetLookoutCertainty(island);
        }

        private static Vector3 ToVector3(float[] values)
        {
            return values != null && values.Length >= 3
                ? new Vector3(values[0], values[1], values[2])
                : Vector3.zero;
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }

        private static Vector3 GetPlayerEyePosition()
        {
            if (Refs.ovrCameraRig != null) return Refs.ovrCameraRig.position;
            if (Refs.observerMirror != null) return Refs.observerMirror.transform.position + Vector3.up * 1.7f;
            var worldBoat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (worldBoat != null) return worldBoat.transform.position + Vector3.up * 2f;
            return Vector3.up * 2f;
        }
    }
}
