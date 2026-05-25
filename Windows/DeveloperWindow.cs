using UnityEngine;

namespace SailwindVirtualCrew
{
    public class DeveloperWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(20, 20, 300, 80);
        private bool showLegendaryWindow = false;
        private Rect legendaryWindowRect = new Rect(340, 20, 360, 260);
        private static readonly int windowId = "VirtualCrewDeveloperWindow".GetHashCode();
        private static readonly int legendaryWindowId = "VirtualCrewLegendaryDeveloperWindow".GetHashCode();

        private WindowResizer _resizer;
        private WindowResizer _legendaryResizer;
        private WorkstationCustomizerWindow _workstationCustomizerWindow;

        public string WindowKey => "DeveloperWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 20f, 20f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            float height = 100f + 30f; // title bar + activate button
            if (DeveloperMode.IsEnabled)
                height += 30f * 6; // add basic crew + refresh ports + legendary + stamina buttons + workstation customizer

            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : height;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Developer Tools");

            if (DeveloperMode.IsEnabled && showLegendaryWindow)
            {
                float legendaryHeight = 68f + VirtualCrewManager.Instance.LegendaryCrewDefinitions.Count * 48f;
                legendaryWindowRect.height = _legendaryResizer.UserHeight > 0f ? _legendaryResizer.UserHeight : legendaryHeight;
                legendaryWindowRect = WindowLayoutUtility.DrawClampedWindow(
                    legendaryWindowId,
                    legendaryWindowRect,
                    DrawLegendaryWindow,
                    "Reticulate Splines");
            }
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            string toggleLabel = DeveloperMode.IsEnabled ? "Deactivate Developer Mode" : "Activate Developer Mode";
            if (GUILayout.Button(toggleLabel))
                DeveloperMode.IsEnabled = !DeveloperMode.IsEnabled;

            if (DeveloperMode.IsEnabled)
            {
                if (GUILayout.Button("Add Basic Crew"))
                    AddBasicCrew();
                if (GUILayout.Button("Reticulate Splines"))
                    showLegendaryWindow = !showLegendaryWindow;
                if (GUILayout.Button("Refresh Crew at Ports"))
                    VirtualCrewManager.Instance.RefreshPortCrewPools();
                if (GUILayout.Button("Drain 60 Stamina (All Crew)"))
                    foreach (var c in VirtualCrewManager.Instance.Crew)
                        c.DrainStamina(60f);
                if (GUILayout.Button("Restore 60 Stamina (All Crew)"))
                    foreach (var c in VirtualCrewManager.Instance.Crew)
                        c.RestoreStamina(60f);

                var workstationCustomizer = GetWorkstationCustomizerWindow();
                if (workstationCustomizer != null
                    && GUILayout.Button(workstationCustomizer.IsVisible ? "Hide Workstation Customizer" : "Show Workstation Customizer"))
                    workstationCustomizer.ToggleWindow();
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void DrawLegendaryWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            var mgr = VirtualCrewManager.Instance;
            foreach (var legendary in mgr.LegendaryCrewDefinitions)
            {
                GUILayout.Label(legendary.Name + " - " + legendary.Role.DisplayName() + " at " + legendary.HomePort);

                string reason;
                bool canAdd = mgr.CanAddLegendaryCrewToRoster(legendary, out reason);
                GUI.enabled = canAdd;
                if (GUILayout.Button("Add " + legendary.Name))
                    mgr.AddLegendaryCrewToRoster(legendary.Id, out reason);
                GUI.enabled = true;

                if (!canAdd)
                    GUILayout.Label(reason);
            }

            if (GUILayout.Button("Close"))
                showLegendaryWindow = false;

            _legendaryResizer.HandleInWindow(ref legendaryWindowRect);
            GUI.DragWindow();
        }

        private static void AddBasicCrew()
        {
            var mgr = VirtualCrewManager.Instance;
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Deckhand));
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Deckhand));
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Deckhand));
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Pilot));
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Navigator));
            if (!mgr.Crew.Exists(c => c.Role == ShipRole.ChiefOfficer))
                mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.ChiefOfficer));
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Lookout));
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Quartermaster));
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Supercargo));
        }

        private WorkstationCustomizerWindow GetWorkstationCustomizerWindow()
        {
            if (_workstationCustomizerWindow == null)
                _workstationCustomizerWindow = GetComponent<WorkstationCustomizerWindow>();
            return _workstationCustomizerWindow;
        }
    }
}
