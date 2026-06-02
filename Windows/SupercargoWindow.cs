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

            float contentHeight = ButtonHeight * 4 + 32f;
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
            GUI.enabled = true;

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
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
                return;
            }

            _keptCargoCount = SupercargoTradeService.CountKeptCargoOnCurrentVessel();
            _canBulkSellUnmarkedCargo = SupercargoTradeService.CanBulkSellUnmarkedCargo();
        }
    }
}
