using UnityEngine;

namespace SailwindVirtualCrew
{
    public class FirstOfficerWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(520, 340, 330, 220);
        private static readonly int windowId = "VirtualCrewFirstOfficerWindow".GetHashCode();

        private WindowResizer _resizer;

        public string WindowKey => "FirstOfficerWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 520f, 340f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private const float DefaultHeight = 220f;

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
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "First Officer");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            GUILayout.Space(4);

            var manager = VirtualCrewManager.Instance;
            var firstOfficers = manager.FirstOfficers;
            if (firstOfficers.Count == 0)
            {
                GUILayout.Label("No First Officer aboard.");
                GUI.enabled = false;
                GUILayout.Toggle(false, "Enable auto-trim");
                GUILayout.Toggle(false, "Enforce Standing Orders");
                GUI.enabled = true;
            }
            else
            {
                foreach (var firstOfficer in firstOfficers)
                    GUILayout.Label($"First Officer: {firstOfficer.Name}  [{firstOfficer.FatigueTag}]");

                bool enabled = GUILayout.Toggle(manager.FirstOfficerAutoTrimEnabled, "Enable auto-trim");
                if (enabled != manager.FirstOfficerAutoTrimEnabled)
                    manager.SetFirstOfficerAutoTrimEnabled(enabled);

                bool standingOrdersEnabled = GUILayout.Toggle(manager.FirstOfficerStandingOrdersEnabled, "Enforce Standing Orders");
                if (standingOrdersEnabled != manager.FirstOfficerStandingOrdersEnabled)
                    manager.SetFirstOfficerStandingOrdersEnabled(standingOrdersEnabled);
            }

            var standingOrdersWindow = GetComponent<StandingOrdersWindow>();
            if (standingOrdersWindow != null
                && GUILayout.Button(standingOrdersWindow.IsVisible ? "Hide Standing Orders" : "Show Standing Orders"))
                standingOrdersWindow.ToggleWindow();

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }
    }
}
