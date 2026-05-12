using UnityEngine;

namespace SailwindVirtualCrew
{
    public class CrewRosterWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(840, 20, 300, 400);
        private static readonly int windowId = "VirtualCrewRosterWindow".GetHashCode();

        public string WindowKey => "CrewRosterWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y };
        public void SetPosition(float x, float y) { windowRect.x = x; windowRect.y = y; }

        private Crewman selectedShipCrew  = null;
        private Crewman selectedAvailable = null;
        private string  crewRenameBuffer  = "";

        private int? bedCount = null;

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

            // ── Capacity ────────────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            string bedLabel = bedCount.HasValue ? $"Beds: {bedCount}" : "Beds: ?";
            GUILayout.Label($"{bedLabel}  |  Crew: {mgr.Crew.Count}");
            if (GUILayout.Button("Scan", GUILayout.Width(50)))
                bedCount = LocatorUtils.CountBeds();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            // ── On Ship ─────────────────────────────────────────────────────
            GUILayout.Label("On Ship:");
            foreach (var c in mgr.Crew)
            {
                bool sel = c == selectedShipCrew;
                string fatigue = DeveloperMode.IsEnabled ? "" : $"  [{c.FatigueTag}]";
                string label = sel ? $"► {c.Name}  ({c.Role}){fatigue}" : $"  {c.Name}  ({c.Role}){fatigue}";
                if (GUILayout.Button(label))
                {
                    if (sel) { selectedShipCrew = null; crewRenameBuffer = ""; }
                    else     { selectedShipCrew = c;    crewRenameBuffer = c.Name; selectedAvailable = null; }
                }
            }
            if (selectedShipCrew != null)
            {
                GUILayout.Label(StatLine(selectedShipCrew));
                GUILayout.BeginHorizontal();
                GUILayout.Label("Name:", GUILayout.Width(42));
                crewRenameBuffer = GUILayout.TextField(crewRenameBuffer);
                if (GUILayout.Button("Set", GUILayout.Width(36)) && crewRenameBuffer.Trim().Length > 0)
                    selectedShipCrew.Rename(crewRenameBuffer.Trim());
                GUILayout.EndHorizontal();
                GUI.enabled = !selectedShipCrew.IsOccupied;
                if (GUILayout.Button($"Sleep"))
                    mgr.AddSleepRequest(selectedShipCrew);
                GUI.enabled = true;
                if (mgr.CurrentPort != null && GUILayout.Button($"Fire {selectedShipCrew.Name}"))
                {
                    mgr.FireCrew(selectedShipCrew);
                    selectedShipCrew = null;
                    crewRenameBuffer = "";
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
                        crewRenameBuffer  = "";
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

        private static string StatLine(Crewman c)
        {
            if (DeveloperMode.IsEnabled)
                return $"{c.TrueStatLine()}    Stamina: {c.CurrentStamina:F1}/{c.MaxStamina}";
            return c.AdvertisedStatLine();
        }
    }
}
