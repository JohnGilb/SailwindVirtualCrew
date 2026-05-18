using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class WorkstationCustomizerWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(1240, 20, 420, 520);
        private static readonly int windowId = "VirtualCrewWorkstationCustomizerWindow".GetHashCode();

        private WindowResizer _resizer;
        private Vector2 _scroll;
        private string _selectedKey;
        private GameObject _marker;
        private string _markerKey;
        private bool _markerProjected;

        public string WindowKey => "WorkstationCustomizerWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 1240f, 20f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        public bool IsVisible => showWindow;

        public void ToggleWindow()
        {
            SetVisible(!showWindow);
        }

        public void SetVisible(bool visible)
        {
            showWindow = visible;
            if (!showWindow)
                ClearMarker();
        }

        private void OnDestroy()
        {
            ClearMarker();
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : 520f;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Workstation Customizer");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            var stations = CrewNavigationCoordinator.Instance.GetWorkstations();
            if (stations == null || stations.Count == 0)
            {
                GUILayout.Label("No workstations scanned.");
                if (GUILayout.Button("Scan Workstations"))
                    CrewNavigationCoordinator.Instance.RebuildWorkstations();
                ClearMarker();
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            int failed = stations.Count(s => !s.Projected);
            int custom = stations.Count(s => s.HasCustomLocation);
            GUILayout.Label("Workstations: " + stations.Count + "  Failed: " + failed + "  Custom: " + custom);
            if (GUILayout.Button("Rescan Workstations"))
                CrewNavigationCoordinator.Instance.RebuildWorkstations();

            var selected = stations.FirstOrDefault(s => s.StableKey == _selectedKey);
            DrawSelectedStation(selected);

            GUILayout.Space(6);
            GUILayout.Label("Stations");

            float selectedReserve = selected == null ? 12f : 170f;
            float listHeight = Mathf.Max(90f, windowRect.height - selectedReserve - 160f);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(listHeight));
            foreach (var station in stations)
                DrawStationRow(station);
            GUILayout.EndScrollView();

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void DrawStationRow(CrewStation station)
        {
            bool selected = station.StableKey == _selectedKey;
            Color oldColor = GUI.color;
            if (!station.Projected)
                GUI.color = new Color(1f, 0.35f, 0.35f);
            else if (station.HasCustomLocation)
                GUI.color = new Color(0.55f, 1f, 0.65f);

            string prefix = selected ? "> " : "  ";
            string status = station.HasCustomLocation ? "custom" : station.Projected ? "ok" : "failed";
            if (GUILayout.Button(prefix + station.Id + "  [" + station.TypeName + "]  " + status))
            {
                _selectedKey = selected ? null : station.StableKey;
                if (_selectedKey == null)
                    ClearMarker();
                else
                    ShowMarker(station);
            }

            GUI.color = oldColor;
        }

        private void DrawSelectedStation(CrewStation station)
        {
            if (station == null)
            {
                ClearMarker();
                GUILayout.Label("Select a workstation to edit.");
                return;
            }

            ShowMarker(station);
            GUILayout.Space(6);
            GUILayout.Label("Selected: " + station.Id);
            GUILayout.Label((station.HasCustomLocation ? "Custom" : station.Projected ? "Projected" : "Failed")
                + "  " + Format(station.Projected ? station.ProjectedLocalStand : station.RequestedLocalStand));

            if (!station.Projected)
                GUILayout.Label("Projection failed; red marker shows attempted location.");
            else if (station.HasCustomLocation)
                GUILayout.Label("Using custom work location.");

            if (GUILayout.Button("Set Custom Work Location"))
                SetCustomWorkLocation(station);

            GUI.enabled = station.HasCustomLocation;
            if (GUILayout.Button("Clear Custom Work Location"))
                ClearCustomWorkLocation(station);
            GUI.enabled = true;
        }

        private void SetCustomWorkLocation(CrewStation station)
        {
            var context = CrewBoatContextResolver.ResolveAndLog();
            if (context == null || Refs.observerMirror == null)
                return;

            var mapper = new CrewSpaceMapper(context);
            Transform player = Refs.observerMirror.transform;
            Vector3 localPosition = mapper.WorldBoatLocalFromWorld(player.position);
            Vector3 localForward = context.WorldBoat.InverseTransformDirection(player.forward);
            localForward.y = 0f;
            if (localForward.sqrMagnitude < 0.001f)
                localForward = Vector3.forward;

            Quaternion localRotation = Quaternion.LookRotation(localForward.normalized, Vector3.up);
            CrewNavigationCoordinator.Instance.ApplyCustomWorkstationLocation(station, localPosition, localRotation);
            CrewDebugLog.Ok("RuntimeNav", "Set custom workstation location key='" + station.StableKey + "' local=" + Format(localPosition));
            ShowMarker(station);
        }

        private void ClearCustomWorkLocation(CrewStation station)
        {
            string key = station.StableKey;
            CrewNavigationCoordinator.Instance.ClearCustomWorkstationLocation(station);
            var refreshed = CrewNavigationCoordinator.Instance.GetWorkstations().FirstOrDefault(s => s.StableKey == key);
            _selectedKey = refreshed?.StableKey;
            if (refreshed == null)
                ClearMarker();
            else
                ShowMarker(refreshed);
        }

        private void ShowMarker(CrewStation station)
        {
            var context = CrewBoatContextResolver.Resolve();
            if (context == null)
            {
                ClearMarker();
                return;
            }

            bool projected = station.Projected;
            if (_marker == null || _markerKey != station.StableKey || _markerProjected != projected || _marker.transform.parent != context.WorldBoat)
            {
                ClearMarker();
                _marker = GameObject.CreatePrimitive(projected ? PrimitiveType.Cylinder : PrimitiveType.Sphere);
                _marker.name = projected ? "VC_Workstation_Customizer_" + station.Id : "VC_Workstation_Customizer_Failed_" + station.Id;
                _marker.transform.SetParent(context.WorldBoat, false);
                var collider = _marker.GetComponent<Collider>();
                if (collider)
                    Destroy(collider);
                _markerKey = station.StableKey;
                _markerProjected = projected;
            }

            _marker.transform.localPosition = projected ? station.ProjectedLocalStand : station.RequestedLocalStand;
            _marker.transform.localRotation = projected ? station.LocalRotation : Quaternion.identity;
            _marker.transform.localScale = projected ? new Vector3(0.55f, 0.1f, 0.55f) : Vector3.one * 0.4f;

            var renderer = _marker.GetComponent<Renderer>();
            if (renderer)
                renderer.material.color = GetMarkerColor(station);
        }

        private void ClearMarker()
        {
            if (_marker)
                Destroy(_marker);

            _marker = null;
            _markerKey = null;
            _markerProjected = false;
        }

        private static Color GetMarkerColor(CrewStation station)
        {
            if (!station.Projected)
                return Color.red;
            if (station.HasCustomLocation)
                return new Color(0.4f, 1f, 0.45f);
            if (station.TypeName == "helm")
                return Color.green;
            if (station.TypeName == "rope")
                return Color.yellow;
            return Color.cyan;
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }
    }
}
