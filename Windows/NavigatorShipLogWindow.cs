using System;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class NavigatorShipLogWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(1140, 80, 360, 420);
        private static readonly int windowId = "VirtualCrewNavigatorShipLogWindow".GetHashCode();

        private WindowResizer _resizer;
        private GUIStyle _leftLabel;
        private GUIStyle _centerLabel;
        private bool _stylesDarkMode;

        public string WindowKey => "NavigatorShipLogWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 1140f, 80f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }
        public void SetVisible(bool visible) { showWindow = visible; }
        public void ToggleVisible() { showWindow = !showWindow; }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            windowRect.width = Mathf.Max(320f, windowRect.width);
            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : Mathf.Max(300f, windowRect.height);
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Ship Log");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            EnsureStyles();
            GUILayout.Space(4);

            var entries = VirtualCrewManager.Instance.NavigatorShipLog
                .Where(e => e != null && e.HasPosition)
                .OrderByDescending(e => e.localDay)
                .ToList();

            if (entries.Count == 0)
            {
                GUILayout.Label("No ship positions logged.", _centerLabel);
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Day", _leftLabel, GUILayout.Width(48f));
            GUILayout.Label("Latitude", _leftLabel, GUILayout.Width(92f));
            GUILayout.Label("Longitude", _leftLabel, GUILayout.Width(102f));
            GUILayout.Label("Samples", _leftLabel, GUILayout.Width(76f));
            GUILayout.EndHorizontal();

            foreach (var entry in entries)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("D" + entry.localDay, _leftLabel, GUILayout.Width(48f));
                GUILayout.Label(FormatLatitude(entry.Latitude), _leftLabel, GUILayout.Width(92f));
                GUILayout.Label(FormatLongitude(entry.Longitude), _leftLabel, GUILayout.Width(102f));
                GUILayout.Label(entry.latitudeCount + "/" + entry.longitudeCount, _leftLabel, GUILayout.Width(76f));
                GUILayout.EndHorizontal();
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void EnsureStyles()
        {
            if (_leftLabel != null && !SailwindGuiStyle.HasThemeChanged(_stylesDarkMode))
                return;

            _stylesDarkMode = SailwindGuiStyle.IsDarkMode;
            _leftLabel = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 14,
                wordWrap = false,
                clipping = TextClipping.Clip
            };
            _centerLabel = new GUIStyle(_leftLabel)
            {
                alignment = TextAnchor.MiddleCenter
            };
        }

        private static string FormatLatitude(float latitude)
        {
            return FormatCoordinate(latitude, "N", "S");
        }

        private static string FormatLongitude(float longitude)
        {
            return FormatCoordinate(longitude, "E", "W");
        }

        private static string FormatCoordinate(float value, string positiveHemisphere, string negativeHemisphere)
        {
            string hemisphere = value < 0f ? negativeHemisphere : positiveHemisphere;
            return Math.Abs(value).ToString("0.00") + " " + hemisphere;
        }
    }
}
