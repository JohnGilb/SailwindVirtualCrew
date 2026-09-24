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
        private CargoArea _cargoIslandsArea;
        private int _cargoIslandsVersion = -1;
        private int _cargoIslandCount;
        private float _cargoVolume;

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
                height += 30f * 31; // developer actions, cargo painting and instrumentation controls

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
                // Downtime only reaches idle crew with a rest location, as in play.
                if (GUILayout.Button("Light Wander Now (Idle Crew)"))
                    NotifyDowntimeForced(CrewNavigationCoordinator.Instance.ForceDowntime(CrewDowntimeLevel.LightWander, 0f), "light wander");
                if (GUILayout.Button("Major Wander Now (Idle Crew, 1s Apart)"))
                    NotifyDowntimeForced(CrewNavigationCoordinator.Instance.ForceDowntime(CrewDowntimeLevel.MajorWander, 1f), "major wander");

                var workstationCustomizer = GetWorkstationCustomizerWindow();
                if (workstationCustomizer != null
                    && GUILayout.Button(workstationCustomizer.IsVisible ? "Hide Workstation Customizer" : "Show Workstation Customizer"))
                    workstationCustomizer.ToggleWindow();

                DrawCargoPaintingControls();
                DrawInstrumentationControls();
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private static void NotifyDowntimeForced(int count, string label)
        {
            NotificationUi.instance?.ShowNotification(count > 0
                ? count + " crew set to " + label
                : "No idle crew with a rest location to set to " + label);
        }

        private void DrawCargoPaintingControls()
        {
            GUILayout.Space(8);
            GUILayout.Label("Cargo Area Painting (" + Plugin.CargoPaintCycleModeKey.Value + " cycles mode)");

            GUILayout.BeginHorizontal();
            DrawCargoPaintModeButton("Paint", CargoPaintMode.Paint);
            DrawCargoPaintModeButton("Erase", CargoPaintMode.Erase);
            DrawCargoPaintModeButton("Off", CargoPaintMode.Off);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Brush " + CargoAreaPainter.BrushRadius.ToString("0.0") + "m", GUILayout.Width(90));
            CargoAreaPainter.BrushRadius = GUILayout.HorizontalSlider(CargoAreaPainter.BrushRadius, 0.3f, 3f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Height " + CargoAreaPainter.HeightCap.ToString("0.0") + "m", GUILayout.Width(90));
            CargoAreaPainter.HeightCap = GUILayout.HorizontalSlider(CargoAreaPainter.HeightCap, 0.5f, 3f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            CargoAreaPainter.ShowOverlay = GUILayout.Toggle(CargoAreaPainter.ShowOverlay, "Show overlay");
            if (GUILayout.Button("Clear"))
                CargoAreaPainter.ClearActiveArea();
            if (GUILayout.Button("Log"))
                CargoAreaPainter.LogActiveArea();
            if (GUILayout.Button("Inspect"))
                CargoAreaPainter.InspectUnderPlayer();
            GUILayout.EndHorizontal();

            var area = CargoAreaPainter.GetActiveArea();
            if (area == null)
            {
                GUILayout.Label("No active vessel.");
                return;
            }

            if (area.Version != _cargoIslandsVersion || area != _cargoIslandsArea)
            {
                _cargoIslandCount = area.FindIslands().Count;
                _cargoVolume = area.VolumeCubicMeters;
                _cargoIslandsVersion = area.Version;
                _cargoIslandsArea = area;
            }

            GUILayout.Label(area.FloorAreaSquareMeters.ToString("0.0") + " sq m floor, "
                + _cargoVolume.ToString("0.0") + " cu m, "
                + _cargoIslandCount + " island" + (_cargoIslandCount == 1 ? "" : "s")
                + ", " + CargoAreaPainter.RejectedCount + " closed edges");
            if (CargoAreaPainter.Mode != CargoPaintMode.Off)
                GUILayout.Label("Last stroke: " + CargoAreaPainter.LastStampQueries + " queries, "
                    + CargoAreaPainter.LastStampRejected + " rejected, "
                    + CargoAreaPainter.LastStampMilliseconds.ToString("0.0") + " ms, "
                    + CargoAreaPainter.CachedColumnCount + " columns cached");
            if (CargoAreaPainter.Mode != CargoPaintMode.Off && !string.IsNullOrEmpty(CargoAreaPainter.LastProblem))
                GUILayout.Label(CargoAreaPainter.LastProblem);

            DrawCargoSolverControls();
        }

        private static void DrawCargoSolverControls()
        {
            GUILayout.Space(4);
            GUILayout.Label("Packing Solver (" + Plugin.CargoSolverKey.Value + " on an item: preview, again: place; "
                + Plugin.CargoSolverClaimKey.Value + ": claim its spot works)");

            var selected = CargoPackingSolver.SelectedItem;
            GUILayout.BeginHorizontal();
            GUI.enabled = selected && CargoPackingSolver.HasPlacement;
            if (GUILayout.Button("Place"))
                CargoPackingSolver.PlaceSelected();
            GUI.enabled = selected;
            if (GUILayout.Button("Re-solve"))
                CargoPackingSolver.Solve(selected);
            GUI.enabled = true;
            if (GUILayout.Button("Clear"))
                CargoPackingSolver.ClearPreview();
            GUILayout.EndHorizontal();

            if (CargoPackingSolver.CandidateCount > 0)
                GUILayout.Label(CargoPackingSolver.CandidateCount + " candidates, "
                    + CargoPackingSolver.PhysicsEvaluated + " tested ("
                    + CargoPackingSolver.CountOf(CargoPackingSolver.Outcome.Valid) + " ok, "
                    + CargoPackingSolver.CountOf(CargoPackingSolver.Outcome.NoRoom) + " no room, "
                    + (CargoPackingSolver.CountOf(CargoPackingSolver.Outcome.NoSurface) + CargoPackingSolver.CountOf(CargoPackingSolver.Outcome.Hanging)) + " no support, "
                    + CargoPackingSolver.CountOf(CargoPackingSolver.Outcome.Unstable) + " unstable), "
                    + CargoPackingSolver.LastSolveMilliseconds.ToString("0") + " ms");
            if (!string.IsNullOrEmpty(CargoPackingSolver.LastMessage))
                GUILayout.Label(CargoPackingSolver.LastMessage);
        }

        private static void DrawCargoPaintModeButton(string label, CargoPaintMode mode)
        {
            bool active = CargoAreaPainter.Mode == mode;
            if (GUILayout.Button(active ? "[" + label + "]" : label))
                CargoAreaPainter.SetMode(mode);
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
