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
        private const float LockButtonHeight = 24f;
        private const float TooltipDelaySeconds = 0.25f;
        private const float TooltipMaxWidth = 220f;

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
            new LauncherEntry("StandingOrdersWindow", "Standing Orders", "SO"),
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
        private GUIStyle _tooltipStyle;
        private bool _stylesDarkMode;
        private string _hoverTooltip = "";
        private float _hoverTooltipStartTime;

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
            float defaultHeight = 48f + LockButtonHeight + ButtonGap + rows * (ButtonSize + ButtonGap) + 26f;
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
            DrawLockButton();
            GUILayout.Space(ButtonGap);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(WindowLayoutUtility.GetScrollableContentHeight(windowRect)));

            for (int i = 0; i < entries.Length; i += Columns)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < Columns && i + column < entries.Length; column++)
                    DrawLauncherButton(entries[i + column]);
                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();
            UpdateAndDrawTooltip();
            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void DrawLockButton()
        {
            bool locked = WindowLayoutUtility.WindowPositionsLocked;
            string label = locked ? "Unlock" : "Lock";
            string tooltip = locked
                ? "Unlock window positions so Virtual Crew windows can be moved again."
                : "Lock window positions. Windows can still be shown, hidden, and resized.";

            if (GUILayout.Button(new GUIContent(label, tooltip), GUILayout.Height(LockButtonHeight)))
                WindowLayoutUtility.SetWindowPositionsLocked(!locked);
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

        private void UpdateAndDrawTooltip()
        {
            string tooltip = GUI.tooltip ?? "";
            if (!string.Equals(tooltip, _hoverTooltip, StringComparison.Ordinal))
            {
                _hoverTooltip = tooltip;
                _hoverTooltipStartTime = Time.realtimeSinceStartup;
            }

            if (string.IsNullOrEmpty(_hoverTooltip)
                || Time.realtimeSinceStartup - _hoverTooltipStartTime < TooltipDelaySeconds)
                return;

            EnsureTooltipStyle();

            var content = new GUIContent(_hoverTooltip);
            float width = Mathf.Min(TooltipMaxWidth, Mathf.Max(80f, _tooltipStyle.CalcSize(content).x + 12f));
            float height = _tooltipStyle.CalcHeight(content, width) + 8f;
            Vector2 mouse = Event.current.mousePosition;
            Rect rect = new Rect(mouse.x + 12f, mouse.y + 16f, width, height);

            if (rect.xMax > windowRect.width - 4f)
                rect.x = Mathf.Max(4f, windowRect.width - rect.width - 4f);
            if (rect.yMax > windowRect.height - WindowResizer.HandleHeight - 4f)
                rect.y = Mathf.Max(24f, mouse.y - rect.height - 8f);

            GUI.Box(rect, content, _tooltipStyle);
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
            _tooltipStyle = null;
        }

        private void EnsureTooltipStyle()
        {
            if (_tooltipStyle != null && !SailwindGuiStyle.HasThemeChanged(_stylesDarkMode))
                return;

            _tooltipStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 12,
                wordWrap = true,
                padding = new RectOffset(6, 6, 4, 4)
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
