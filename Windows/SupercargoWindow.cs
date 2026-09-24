using UnityEngine;

namespace SailwindVirtualCrew
{
    public class SupercargoWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(880, 340, 300, 170);
        private static readonly int windowId = "VirtualCrewSupercargoWindow".GetHashCode();
        private WindowResizer _resizer;
        private float _nextSnapshotRefreshTime;
        private int _keptCargoCount;
        private bool _canBulkSellUnmarkedCargo;
        private bool _isCurrentBoatMoored;
        private int _loadableDockCargoCount;
        private CargoArea _statsArea;
        private int _statsVersion = -1;
        private float _areaVolume;
        // The Clear button asks again before throwing away the painted area.
        private float _confirmClearUntil;
        private const float ConfirmClearSeconds = 3f;

        public string WindowKey => "SupercargoWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 880f, 340f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private const float ButtonHeight = 28f;

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            float contentHeight = ButtonHeight * 12 + 32f;
            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : contentHeight + 40f;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Supercargo");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            RefreshSnapshotIfNeeded();

            GUILayout.Label("Cargo Orders");
            GUILayout.Label(_isCurrentBoatMoored ? "Boat moored" : "Boat not moored");
            GUILayout.Label("Kept cargo: " + _keptCargoCount);

            GUI.enabled = _canBulkSellUnmarkedCargo;
            if (GUILayout.Button("Sell All Unmarked Cargo"))
            {
                int queued = SupercargoTradeService.MarkAllUnkeptCargoForSale();
                _nextSnapshotRefreshTime = 0f;
                NotificationUi.instance?.ShowNotification(
                    queued > 0
                        ? "Queued " + queued + " cargo for port sale"
                        : "No unmarked cargo available to sell");
            }

            bool loading = CargoLoadService.IsLoadingAll;
            GUI.enabled = !loading && _loadableDockCargoCount > 0;
            string loadLabel = loading
                ? "Planning Cargo Loading..."
                : "Load All Dock Cargo" + (_loadableDockCargoCount > 0 ? " (" + _loadableDockCargoCount + ")" : "");
            if (GUILayout.Button(loadLabel))
            {
                if (CargoLoadService.StartLoadingAll() == 0)
                    NotificationUi.instance?.ShowNotification("No cargo on the dock to load");
                _nextSnapshotRefreshTime = 0f;
            }
            GUI.enabled = true;

            DrawCargoAreaControls();

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        // Painting the cargo area: walk through the space to be used for cargo while painting (erasing takes it out).
        private void DrawCargoAreaControls()
        {
            GUILayout.Space(8);
            var area = CargoAreaPainter.GetActiveArea();
            if (area == null)
            {
                GUILayout.Label("Cargo area: no boat");
                return;
            }

            if (area != _statsArea || area.Version != _statsVersion)
            {
                _areaVolume = area.VolumeCubicMeters;
                _statsArea = area;
                _statsVersion = area.Version;
            }

            GUILayout.Label(area.RunCount == 0
                ? "Cargo area: not painted"
                : "Cargo area: " + area.FloorAreaSquareMeters.ToString("0.0") + " sq m, " + _areaVolume.ToString("0.0") + " cu m");

            var mode = CargoAreaPainter.Mode;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(mode == CargoPaintMode.Paint ? "[Paint]" : "Paint"))
                CargoAreaPainter.SetMode(CargoPaintMode.Paint);
            if (GUILayout.Button(mode == CargoPaintMode.Erase ? "[Erase]" : "Erase"))
                CargoAreaPainter.SetMode(CargoPaintMode.Erase);
            GUI.enabled = mode != CargoPaintMode.Off;
            if (GUILayout.Button("Stop"))
                CargoAreaPainter.SetMode(CargoPaintMode.Off);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (mode != CargoPaintMode.Off)
            {
                GUILayout.Label(mode == CargoPaintMode.Paint
                    ? "Walk through the space to use for cargo (" + Plugin.CargoPaintCycleModeKey.Value + " cycles)."
                    : "Walk through space to take out of the cargo area.");
                if (!string.IsNullOrEmpty(CargoAreaPainter.LastProblem))
                    GUILayout.Label(CargoAreaPainter.LastProblem);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Brush " + CargoAreaPainter.BrushRadius.ToString("0.0") + "m", GUILayout.Width(90));
            CargoAreaPainter.BrushRadius = GUILayout.HorizontalSlider(CargoAreaPainter.BrushRadius, 0.3f, 3f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Stack " + area.StackHeight.ToString("0.0") + "m", GUILayout.Width(90));
            area.StackHeight = GUILayout.HorizontalSlider(area.StackHeight, CargoArea.MinStackHeight, CargoArea.MaxStackHeight);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            CargoAreaPainter.ShowOverlay = GUILayout.Toggle(CargoAreaPainter.ShowOverlay, "Show cargo area");
            GUI.enabled = area.RunCount > 0;
            bool confirming = Time.realtimeSinceStartup < _confirmClearUntil;
            if (GUILayout.Button(confirming ? "Confirm Clear" : "Clear Area"))
            {
                if (confirming)
                {
                    CargoAreaPainter.ClearActiveArea();
                    _confirmClearUntil = 0f;
                }
                else
                {
                    _confirmClearUntil = Time.realtimeSinceStartup + ConfirmClearSeconds;
                }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void RefreshSnapshotIfNeeded()
        {
            if (Time.realtimeSinceStartup < _nextSnapshotRefreshTime)
                return;

            _nextSnapshotRefreshTime = Time.realtimeSinceStartup + 1f;
            _isCurrentBoatMoored = MooringLocator.IsCurrentBoatMooredFast();
            if (!_isCurrentBoatMoored)
            {
                _keptCargoCount = 0;
                _canBulkSellUnmarkedCargo = false;
                _loadableDockCargoCount = 0;
                return;
            }

            _loadableDockCargoCount = CargoLoadService.IsLoadingAll ? 0 : CargoLoadService.FindLoadableDockCargo().Count;

            _keptCargoCount = SupercargoTradeService.CountKeptCargoOnCurrentVessel();
            _canBulkSellUnmarkedCargo = SupercargoTradeService.CanBulkSellUnmarkedCargo();
        }
    }
}
