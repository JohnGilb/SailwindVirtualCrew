using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class WindowLayoutUtility
    {
        private const float VisibleEdge = 40f;

        private static int _toggleFrame = -1;
        private static bool _togglePressedThisFrame;

        internal static bool ShouldToggleWindowsThisFrame()
        {
            if (_toggleFrame != Time.frameCount)
            {
                _toggleFrame = Time.frameCount;
                _togglePressedThisFrame = Plugin.ToggleCrewWindow.Value.IsDown() && !Input.GetMouseButton(0);
            }

            return _togglePressedThisFrame;
        }

        internal static Rect DrawClampedWindow(int windowId, Rect windowRect, GUI.WindowFunction drawWindow, string title)
        {
            Rect updated = GUI.Window(windowId, windowRect, drawWindow, title);
            ClampToScreen(ref updated);
            return updated;
        }

        internal static void ClampToScreen(ref Rect rect)
        {
            float minX = Mathf.Min(0f, Screen.width - rect.width);
            float maxX = Mathf.Max(0f, Screen.width - VisibleEdge);
            float minY = 0f;
            float maxY = Mathf.Max(0f, Screen.height - VisibleEdge);

            rect.x = Mathf.Clamp(rect.x, minX, maxX);
            rect.y = Mathf.Clamp(rect.y, minY, maxY);
        }
    }
}
