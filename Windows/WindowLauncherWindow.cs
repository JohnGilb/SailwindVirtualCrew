using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class WindowLauncherWindow : MonoBehaviour, IWindowPosition
    {
        private const float ButtonSize = 42f;
        private const float ButtonGap = 4f;
        private const int Columns = 4;

        private static readonly int windowId = "VirtualCrewWindowLauncherWindow".GetHashCode();

        private readonly LauncherEntry[] entries =
        {
            new LauncherEntry("CrewWindow", "Deck Orders", "DO"),
            new LauncherEntry("SailGroupsWindow", "Sail Groups", "SG"),
            new LauncherEntry("WorkRequestsWindow", "Work Requests", "WR"),
            new LauncherEntry("NavigatorWindow", "Navigator", "NV"),
            new LauncherEntry("NavigatorMapWindow", "Navigator Map", "MP"),
            new LauncherEntry("MaintenanceWindow", "Maintenance", "MT"),
            new LauncherEntry("SupercargoWindow", "Supercargo", "SC"),
            new LauncherEntry("StewardWindow", "Steward", "ST"),
            new LauncherEntry("FirstOfficerWindow", "First Officer", "FO"),
            new LauncherEntry("PilotingWindow", "Piloting", "PL"),
            new LauncherEntry("CrewRosterWindow", "Crew Roster", "CR"),
            new LauncherEntry("LookoutWindow", "Lookout", "LO"),
            new LauncherEntry("FavoriteActionsWindow", "Favorites", "FA"),
            new LauncherEntry("DeveloperWindow", "Developer Tools", "DV"),
        };

        private Rect windowRect = new Rect(20, 20, 210, 190);
        private Vector2 _scroll;
        private WindowResizer _resizer;
        private readonly Dictionary<string, Component> _componentCache = new Dictionary<string, Component>();
        private GUIStyle _activeButtonStyle;
        private GUIStyle _inactiveButtonStyle;
        private bool _stylesDarkMode;

        public string WindowKey => "WindowLauncherWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 20f, 20f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleLauncherThisFrame())
                WindowLayoutUtility.ToggleModLayer();
        }

        private void OnGUI()
        {
            if (!WindowLayoutUtility.ModLayerVisible) return;
            SailwindGuiStyle.Apply();

            int rows = Mathf.CeilToInt(entries.Length / (float)Columns);
            float defaultHeight = 48f + rows * (ButtonSize + ButtonGap) + 26f;
            windowRect.width = Mathf.Max(windowRect.width, Columns * (ButtonSize + ButtonGap) + 28f);
            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : defaultHeight;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Virtual Crew", false);
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            EnsureStyles();
            GUILayout.Space(4);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(WindowLayoutUtility.GetScrollableContentHeight(windowRect)));

            for (int i = 0; i < entries.Length; i += Columns)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < Columns && i + column < entries.Length; column++)
                    DrawLauncherButton(entries[i + column]);
                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();
            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void DrawLauncherButton(LauncherEntry entry)
        {
            var component = GetWindowComponent(entry.WindowKey);
            bool visible = false;
            bool canToggle = component != null && WindowVisibilityUtility.TryGetVisible(component, out visible);
            if (!canToggle)
            {
                GUI.enabled = false;
                visible = false;
            }

            var content = new GUIContent(entry.Icon, entry.Title);
            if (GUILayout.Button(content, visible ? _activeButtonStyle : _inactiveButtonStyle, GUILayout.Width(ButtonSize), GUILayout.Height(ButtonSize)))
                WindowVisibilityUtility.TrySetVisible(component, !visible);

            GUI.enabled = true;
        }

        private Component GetWindowComponent(string key)
        {
            Component component;
            if (_componentCache.TryGetValue(key, out component) && component != null)
                return component;

            component = GetComponents<IWindowPosition>()
                .OfType<Component>()
                .FirstOrDefault(c => string.Equals(((IWindowPosition)c).WindowKey, key, StringComparison.Ordinal));
            _componentCache[key] = component;
            return component;
        }

        private void EnsureStyles()
        {
            if (_activeButtonStyle != null && !SailwindGuiStyle.HasThemeChanged(_stylesDarkMode))
                return;

            _stylesDarkMode = SailwindGuiStyle.IsDarkMode;
            _inactiveButtonStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
                clipping = TextClipping.Clip
            };
            _activeButtonStyle = new GUIStyle(_inactiveButtonStyle)
            {
                normal = { textColor = Color.cyan },
                hover = { textColor = Color.cyan },
                active = { textColor = Color.cyan }
            };
        }

        private struct LauncherEntry
        {
            internal readonly string WindowKey;
            internal readonly string Title;
            internal readonly string Icon;

            internal LauncherEntry(string windowKey, string title, string icon)
            {
                WindowKey = windowKey;
                Title = title;
                Icon = icon;
            }
        }
    }
}
