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
                height += 30f * 17; // developer actions plus instrumentation controls

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
                if (GUILayout.Button("Add Steward"))
                    VirtualCrewManager.Instance.Crew.Add(VirtualCrewManager.Instance.CreateRandomCrewman(ShipRole.Steward));
                if (GUILayout.Button("Reticulate Splines"))
                    showLegendaryWindow = !showLegendaryWindow;
                if (GUILayout.Button("Refresh Crew at Ports"))
                    VirtualCrewManager.Instance.RefreshPortCrewPools();
                if (GUILayout.Button("Fill Water Barrels"))
                {
                    int refilled = VirtualCrewManager.Instance.FillAllWaterBarrelsOnActiveVessel();
                    NotificationUi.instance?.ShowNotification(
                        refilled > 0
                            ? "Filled " + refilled + " water barrel" + (refilled == 1 ? "" : "s")
                            : "No empty water barrels found");
                }
                if (GUILayout.Button("Refill Lantern Fuel Boxes"))
                {
                    var result = CrewLanternService.RefillLanternFuelBoxesOnCurrentVessel();
                    int total = result.BoxesRefilled + result.OilBottlesRefilled;
                    NotificationUi.instance?.ShowNotification(
                        total > 0
                            ? "Refilled " + result.BoxesRefilled + " fuel box" + (result.BoxesRefilled == 1 ? "" : "es")
                                + " and " + result.OilBottlesRefilled + " oil bottle" + (result.OilBottlesRefilled == 1 ? "" : "s")
                            : "No lantern fuel boxes needed refilling");
                }
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

                DrawInstrumentationControls();
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private static void DrawInstrumentationControls()
        {
            GUILayout.Space(8);
            GUILayout.Label("Instrumentation");

            bool configEnabled = PerformanceInstrumentation.IsCollectionAllowed;
            string configLabel = configEnabled ? "Disable Instrumentation Config" : "Enable Instrumentation Config";
            if (GUILayout.Button(configLabel))
            {
                Plugin.InstrumentationEnabled.Value = !Plugin.InstrumentationEnabled.Value;
                if (!Plugin.InstrumentationEnabled.Value)
                    PerformanceInstrumentation.StopSession();
            }

            GUILayout.Label(PerformanceInstrumentation.IsRunning
                ? "Status: Running, " + PerformanceInstrumentation.EventCount + " events, " + PerformanceInstrumentation.ElapsedSeconds.ToString("0.0") + "s"
                : "Status: Stopped");

            if (!configEnabled)
                GUILayout.Label("Config opt-in is off.");

            GUI.enabled = configEnabled && !PerformanceInstrumentation.IsRunning;
            if (GUILayout.Button("Start Profiling Session"))
                PerformanceInstrumentation.StartSession();

            GUI.enabled = PerformanceInstrumentation.IsRunning;
            if (GUILayout.Button("Stop Profiling Session"))
                PerformanceInstrumentation.StopSession();
            if (GUILayout.Button("Flush Profiling Data"))
                PerformanceInstrumentation.FlushNow();
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(PerformanceInstrumentation.OutputDirectory))
                GUILayout.Label("Output: " + PerformanceInstrumentation.OutputDirectory);
            if (!string.IsNullOrEmpty(PerformanceInstrumentation.LastError))
                GUILayout.Label("Last error: " + PerformanceInstrumentation.LastError);
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
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Steward));
        }

        private WorkstationCustomizerWindow GetWorkstationCustomizerWindow()
        {
            if (_workstationCustomizerWindow == null)
                _workstationCustomizerWindow = GetComponent<WorkstationCustomizerWindow>();
            return _workstationCustomizerWindow;
        }
    }
}
