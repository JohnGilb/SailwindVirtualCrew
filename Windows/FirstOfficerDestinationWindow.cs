using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    // Lists the Navigator's plotted islands; picking one has the First Officer send the Pilot
    // a destination to steer for.
    public class FirstOfficerDestinationWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(860, 340, 340, 420);
        private static readonly int windowId = "VirtualCrewFirstOfficerDestinationWindow".GetHashCode();

        private WindowResizer _resizer;
        private Vector2 _scroll;

        private const float DefaultHeight = 420f;

        public string WindowKey => "FirstOfficerDestinationWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 860f, 340f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        public bool IsVisible => showWindow;

        public void ToggleWindow()
        {
            showWindow = !showWindow;
        }

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();
            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : DefaultHeight;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Set Destination");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            GUILayout.Space(4);

            var manager = VirtualCrewManager.Instance;
            var pilotingWindow = GetComponent<PilotingWindow>();

            if (!manager.CanSetFirstOfficerDestination)
            {
                GUILayout.Label("Requires a First Officer, a Navigator, and a Pilot.");
            }
            else
            {
                if (pilotingWindow != null && pilotingWindow.HasDestination)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Current: " + pilotingWindow.DestinationName);
                    if (GUILayout.Button("Clear", GUILayout.Width(60)))
                        pilotingWindow.ClearDestination();
                    GUILayout.EndHorizontal();
                }

                List<NavigatorIslandMapEntrySaveData> islands = GetPlottedIslands(manager);
                if (islands.Count == 0)
                {
                    GUILayout.Label("No islands plotted yet. The Navigator plots an island when taking a fix while moored or anchored there.");
                }
                else
                {
                    GUILayout.Label("Plotted islands");
                    _scroll = GUILayout.BeginScrollView(_scroll);
                    foreach (var island in islands)
                    {
                        string label = GetIslandName(island) + "   "
                            + NavigationResult.FormatLat(island.Latitude) + ", "
                            + NavigationResult.FormatLon(island.Longitude);
                        if (GUILayout.Button(label))
                            SendDestination(manager, pilotingWindow, island);
                    }
                    GUILayout.EndScrollView();
                }
            }

            if (GUILayout.Button("Close"))
                showWindow = false;

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void SendDestination(VirtualCrewManager manager, PilotingWindow pilotingWindow,
                                     NavigatorIslandMapEntrySaveData island)
        {
            if (pilotingWindow == null)
                return;

            // Put the freshest pilot on the helm if nobody is steering yet.
            if (manager.ActivePilotTask == null)
                manager.StartPilot(manager.FreshestCrewman(ShipRole.Pilot));

            if (pilotingWindow.SetDestination(GetIslandName(island), island.Latitude, island.Longitude))
            {
                CrewDebugLog.Info("Piloting", "First Officer set destination '" + GetIslandName(island) + "'.");
                showWindow = false;
            }
        }

        private static List<NavigatorIslandMapEntrySaveData> GetPlottedIslands(VirtualCrewManager manager)
        {
            return (manager.NavigatorIslandMap ?? new Dictionary<string, NavigatorIslandMapEntrySaveData>())
                .Values
                .Where(e => e != null && e.HasPosition)
                .OrderBy(e => GetIslandName(e))
                .ToList();
        }

        private static string GetIslandName(NavigatorIslandMapEntrySaveData island)
        {
            return !string.IsNullOrEmpty(island.name) ? island.name : "Island";
        }
    }
}
