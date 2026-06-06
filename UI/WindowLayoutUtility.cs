using UnityEngine;
using System.Collections.Generic;

namespace SailwindVirtualCrew
{
    internal static class WindowLayoutUtility
    {
        private const float VisibleEdge = 40f;

        private static int _toggleFrame = -1;
        private static bool _togglePressedThisFrame;
        private static bool _modLayerVisible;
        private static readonly Dictionary<int, Vector2> _windowScrollPositions = new Dictionary<int, Vector2>();
        private static int _activeScrollableWindowId;
        private static bool _activeScrollableWindowEnded;

        internal static bool ModLayerVisible => _modLayerVisible;

        internal static void ToggleModLayer()
        {
            _modLayerVisible = !_modLayerVisible;
        }

        internal static bool ShouldToggleWindowsThisFrame()
        {
            return false;
        }

        internal static bool ShouldToggleLauncherThisFrame()
        {
            if (_toggleFrame != Time.frameCount)
            {
                _toggleFrame = Time.frameCount;
                _togglePressedThisFrame = Plugin.ToggleCrewWindow.Value.IsDown() && !Input.GetMouseButton(0);
            }

            return _togglePressedThisFrame;
        }

        internal static float GetScrollableContentHeight(Rect windowRect)
        {
            return Mathf.Max(24f, windowRect.height);
        }

        internal static Rect DrawClampedWindow(int windowId, Rect windowRect, GUI.WindowFunction drawWindow, string title)
        {
            return DrawClampedWindow(windowId, windowRect, drawWindow, title, true);
        }

        internal static Rect DrawClampedWindow(int windowId, Rect windowRect, GUI.WindowFunction drawWindow, string title, bool scrollContent)
        {
            if (!_modLayerVisible)
            {
                ClampToScreen(ref windowRect);
                return windowRect;
            }

            GUI.WindowFunction windowFunction = drawWindow;
            if (scrollContent)
            {
                Rect capturedWindowRect = windowRect;
                windowFunction = id => DrawScrollableWindow(windowId, capturedWindowRect, id, drawWindow);
            }

            GUI.WindowFunction measuredWindowFunction = id =>
            {
                using (PerformanceInstrumentation.MeasureGui("UI.Window.Draw." + title))
                    windowFunction(id);
            };

            using (PerformanceInstrumentation.MeasureGui("UI.Window." + title))
            {
                Rect updated = GUI.Window(windowId, windowRect, measuredWindowFunction, title);
                ClampToScreen(ref updated);
                return updated;
            }
        }

        private static void DrawScrollableWindow(int windowId, Rect windowRect, int id, GUI.WindowFunction drawWindow)
        {
            Vector2 scroll;
            _windowScrollPositions.TryGetValue(windowId, out scroll);
            _activeScrollableWindowId = windowId;
            _activeScrollableWindowEnded = false;
            scroll = GUILayout.BeginScrollView(scroll, false, false, GUILayout.Height(GetScrollableContentHeight(windowRect)));
            _windowScrollPositions[windowId] = scroll;
            drawWindow(id);
            if (!_activeScrollableWindowEnded)
            {
                GUILayout.EndScrollView();
                _windowScrollPositions[windowId] = scroll;
            }

            _activeScrollableWindowId = 0;
            _activeScrollableWindowEnded = false;
        }

        internal static void EndScrollableContentForResizeHandle()
        {
            if (_activeScrollableWindowId == 0 || _activeScrollableWindowEnded)
                return;

            GUILayout.EndScrollView();
            _activeScrollableWindowEnded = true;
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
