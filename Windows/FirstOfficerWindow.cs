using UnityEngine;

namespace SailwindVirtualCrew
{
    public class FirstOfficerWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(520, 340, 330, 170);
        private static readonly int windowId = "VirtualCrewFirstOfficerWindow".GetHashCode();

        private WindowResizer _resizer;

        public string WindowKey => "FirstOfficerWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 520f, 340f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private const float DefaultHeight = 170f;

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
            var firstOfficer = manager.FirstOfficer;
            if (firstOfficer == null)
            {
                GUILayout.Label("No First Officer aboard.");
                GUI.enabled = false;
                GUILayout.Toggle(false, "Enable auto-trim");
                GUI.enabled = true;
            }
            else
            {
                GUILayout.Label($"First Officer: {firstOfficer.Name}  [{firstOfficer.FatigueTag}]");
                bool enabled = GUILayout.Toggle(manager.FirstOfficerAutoTrimEnabled, "Enable auto-trim");
                if (enabled != manager.FirstOfficerAutoTrimEnabled)
                    manager.SetFirstOfficerAutoTrimEnabled(enabled);
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }
    }
}
