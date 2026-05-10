using UnityEngine;

namespace SailwindVirtualCrew
{
    public class CrewRosterWindow : MonoBehaviour
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(840, 20, 300, 400);
        private static readonly int windowId = "VirtualCrewRosterWindow".GetHashCode();

        private Crewman selectedShipCrew  = null;
        private Crewman selectedAvailable = null;

        private const float RowHeight  = 22f;
        private const float StatHeight = 18f;

        private void Update()
        {
            if (Plugin.ToggleCrewWindow.Value.IsDown())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            var mgr = VirtualCrewManager.Instance;

            float h = RowHeight                          // "On Ship:" label
                    + mgr.Crew.Count * RowHeight
                    + (selectedShipCrew  != null ? StatHeight + RowHeight : 0f)
                    + 8f + RowHeight;                    // space + "Available at Port:" label

            var avail = mgr.AvailableAtPort;
            if (avail.Count == 0)
                h += RowHeight;
            else
            {
                h += avail.Count * RowHeight;
                if (selectedAvailable != null) h += StatHeight + RowHeight;
            }

            windowRect.height = h + 400f;
            windowRect = GUI.Window(windowId, windowRect, DrawWindow, "Crew Roster");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            var mgr = VirtualCrewManager.Instance;

            // ── On Ship ─────────────────────────────────────────────────────
            GUILayout.Label("On Ship:");
            foreach (var c in mgr.Crew)
            {
                bool sel = c == selectedShipCrew;
                string label = sel ? $"► {c.Name}  ({c.Role})" : $"  {c.Name}  ({c.Role})";
                if (GUILayout.Button(label))
                {
                    selectedShipCrew  = sel ? null : c;
                    selectedAvailable = null;
                }
            }
            if (selectedShipCrew != null)
            {
                GUILayout.Label(StatLine(selectedShipCrew));
                if (GUILayout.Button($"Fire {selectedShipCrew.Name}"))
                {
                    mgr.FireCrew(selectedShipCrew);
                    selectedShipCrew = null;
                }
            }

            // ── Available at Port ────────────────────────────────────────────
            GUILayout.Space(8);
            GUILayout.Label("Available at Port:");
            var avail = mgr.AvailableAtPort;
            if (avail.Count == 0)
            {
                GUILayout.Label(mgr.CurrentPort == null
                    ? "Visit a port to hire crew."
                    : "No crew available here.");
            }
            else
            {
                foreach (var c in avail)
                {
                    bool sel = c == selectedAvailable;
                    string label = sel ? $"► {c.Name}  ({c.Role})" : $"  {c.Name}  ({c.Role})";
                    if (GUILayout.Button(label))
                    {
                        selectedAvailable = sel ? null : c;
                        selectedShipCrew  = null;
                    }
                }
                if (selectedAvailable != null)
                {
                    GUILayout.Label(StatLine(selectedAvailable));
                    if (GUILayout.Button($"Hire {selectedAvailable.Name}"))
                    {
                        mgr.HireCrew(selectedAvailable);
                        selectedAvailable = null;
                    }
                }
            }

            GUI.DragWindow();
        }

        private static string StatLine(Crewman c) =>
            DeveloperMode.IsEnabled ? c.TrueStatLine() : c.AdvertisedStatLine();
    }
}
