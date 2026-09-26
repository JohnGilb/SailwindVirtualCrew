using BepInEx.Configuration;
using UnityEngine;
using System.Collections.Generic;

namespace SailwindVirtualCrew
{
    internal static class WindowLayoutUtility
    {
        private const float VisibleEdge = 40f;

        private const float FreeMouseHoldSeconds = 0.33f;

        private static bool _crewKeyHeld;
        private static bool _crewKeyFreedMouse;
        private static bool _crewKeyLayerWasVisible;
        private static float _crewKeyDownTime;
        private static bool _modLayerVisible;
        private static readonly Dictionary<int, Vector2> _windowScrollPositions = new Dictionary<int, Vector2>();
        private static readonly List<Rect> _imguiWindowRects = new List<Rect>();
        private static int _activeScrollableWindowId;
        private static bool _activeScrollableWindowEnded;
        private static int _imguiWindowRectFrame = -1;

        internal static bool ModLayerVisible => _modLayerVisible;

        // Stored in the BepInEx config so the lock survives restarting the game and loading a save.
        internal static bool WindowPositionsLocked =>
            Plugin.LockWindowPositions != null && Plugin.LockWindowPositions.Value;

        internal static void SetWindowPositionsLocked(bool locked)
        {
            if (Plugin.LockWindowPositions != null)
                Plugin.LockWindowPositions.Value = locked;
        }

        internal static bool ShouldToggleWindowsThisFrame()
        {
            return false;
        }

        // Tap B: show the windows, or hide them (and end free mouse) if already shown.
        // Hold B: show the windows if needed and free the mouse, keeping the windows up on release.
        internal static void TickCrewWindowKey()
        {
            KeyboardShortcut shortcut = Plugin.ToggleCrewWindow.Value;

            if (!_crewKeyHeld)
            {
                if (!shortcut.IsDown() || Input.GetMouseButton(0) || TextInputHotkeySuppressor.ShouldSuppressFavoriteActionHotkeys)
                    return;

                _crewKeyHeld = true;
                _crewKeyFreedMouse = false;
                _crewKeyDownTime = Time.unscaledTime;
                _crewKeyLayerWasVisible = _modLayerVisible;
                _modLayerVisible = true;
                return;
            }

            if (Input.GetKey(shortcut.MainKey))
            {
                if (!_crewKeyFreedMouse && Time.unscaledTime - _crewKeyDownTime >= FreeMouseHoldSeconds)
                {
                    _crewKeyFreedMouse = true;
                    FreeMouseMode.Enable();
                }
                return;
            }

            _crewKeyHeld = false;
            if (_crewKeyLayerWasVisible && !_crewKeyFreedMouse)
            {
                _modLayerVisible = false;
                FreeMouseMode.Disable();
            }
        }

        internal static float GetScrollableContentHeight(Rect windowRect)
        {
            return Mathf.Max(24f, windowRect.height - WindowResizer.HandleHeight - 36f);
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
                if (WindowPositionsLocked)
                {
                    updated.x = windowRect.x;
                    updated.y = windowRect.y;
                }

                ClampToScreen(ref updated);
                RegisterImguiWindowRect(updated);
                return updated;
            }
        }

        internal static bool IsPointerOverImguiWindow(Vector2 screenPosition)
        {
            if (!_modLayerVisible || _imguiWindowRectFrame < Time.frameCount - 1)
                return false;

            Vector2 guiPoint = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            for (int i = _imguiWindowRects.Count - 1; i >= 0; i--)
                if (_imguiWindowRects[i].Contains(guiPoint))
                    return true;

            return false;
        }

        private static void RegisterImguiWindowRect(Rect rect)
        {
            if (_imguiWindowRectFrame != Time.frameCount)
            {
                _imguiWindowRects.Clear();
                _imguiWindowRectFrame = Time.frameCount;
            }

            _imguiWindowRects.Add(rect);
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
