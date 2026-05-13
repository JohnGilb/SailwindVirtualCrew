using UnityEngine;

namespace SailwindVirtualCrew
{
    public class VirtualCrewDebugWindow : MonoBehaviour, IWindowPosition
    {
        private Rect windowRect = new Rect(20, 660, 360, 170);
        private static readonly int windowId = "VirtualCrewDebugWindow".GetHashCode();
        private WindowResizer _resizer;
        private Vector2 _scrollPosition;

        public string WindowKey => "VirtualCrewDebugWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private void OnGUI()
        {
            if (!DeveloperMode.IsEnabled) return;
            SailwindGuiStyle.Apply();

            float height = 170f;
            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : height;
            windowRect = GUI.Window(windowId, windowRect, DrawWindow, "Virtual Crew Debug");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition);

            if (GUILayout.Button("Run API Probe"))
                CrewApiProbe.Run();

            var result = CrewApiProbe.LastResult;
            GUILayout.Label(result == null ? "Phase 0 probe has not run yet." : result.Summary);

            GUILayout.Space(6);
            GUILayout.Label("Setup");
            if (GUILayout.Button("Dump Boat Context"))
                CrewBoatContextResolver.ResolveAndLog();
            if (GUILayout.Button("Setup Proxy Agent"))
                CrewDebugObjects.SetupProxyAgent();
            if (GUILayout.Button("Setup Synced Visual Agent"))
                CrewDebugObjects.SetupSyncedAgent();

            GUILayout.Space(6);
            GUILayout.Label("Stations");
            if (GUILayout.Button("Scan Workstations"))
                CrewDebugObjects.ScanWorkstations();
            if (GUILayout.Button("Select Next Station"))
                CrewDebugObjects.SelectNextStation();
            if (GUILayout.Button("Assign Crew To Station"))
                CrewDebugObjects.AssignCrewToSelectedStation();
            if (GUILayout.Button("Show Station Markers"))
                CrewDebugObjects.ShowStationMarkers();

            GUILayout.Space(6);
            GUILayout.Label("Diagnostics");
            if (GUILayout.Button("Dump Stations"))
                CrewDebugObjects.DumpStations();
            if (GUILayout.Button("Dump Agent State"))
                CrewDebugObjects.DumpLogicAgentState();
            if (GUILayout.Button("Dump Task State"))
                CrewDebugObjects.DumpStationTaskState();
            if (GUILayout.Button("Cancel Current Task"))
                CrewDebugObjects.CancelCurrentStationTask();
            if (GUILayout.Button("Pause/Resume Sync"))
                CrewDebugObjects.ToggleVisualSyncPause();
            if (GUILayout.Button("Clear Test Objects"))
                CrewDebugObjects.Clear();

            GUILayout.EndScrollView();

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }
    }
}
