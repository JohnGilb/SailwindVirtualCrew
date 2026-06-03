using System;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class NavigatorWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(860, 340, 330, 400);
        private static readonly int windowId = "VirtualCrewNavigatorWindow".GetHashCode();

        private WindowResizer _resizer;

        public string WindowKey => "NavigatorWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 860f, 340f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        // Equipment state (auto-detected by ScanForTools)
        private bool hasChronocompass = false;
        private bool hasChronometer   = false;
        private bool hasCompass       = false;
        private bool hasQuadrant      = false;
        private bool hasSunCompass    = false;
        private bool hasChipLog       = false;

        private static readonly string[] ToolItemNames =
        {
            "chronocompass",
            "chronometer",
            "compass",
            "quadrant",
            "sun compass",
            "chip log",
        };

        private const float ButtonHeight      = 28f;
        private const float BaseContentHeight = 300f;

        // Time windows for each method
        // Quadrant: local 20:00–04:00 (wraps midnight)
        // Sun Compass: local 11:00–13:00
        // Chronometer: global 11:00–13:00
        // Chronocompass: local 08:00–16:00

        private bool overrideTimeWindows = false;
        private GUIStyle _leftLabel;
        private bool _leftLabelDarkMode;

        private bool EffectiveOverride    => DeveloperMode.IsEnabled && overrideTimeWindows;
        private bool CanUseQuadrant      => CanUseMethod(hasQuadrant,      NavigationMethod.Quadrant);
        private bool CanUseSunCompass    => CanUseMethod(hasSunCompass,    NavigationMethod.SunCompass);
        private bool CanUseChronometer   => CanUseMethod(hasChronometer,   NavigationMethod.Chronometer);
        private bool CanUseChronocompass => CanUseMethod(hasChronocompass, NavigationMethod.Chronocompass);

        private bool CanUseMethod(bool hasTool, NavigationMethod method)
        {
            return hasTool
                && (EffectiveOverride || (VirtualCrewManager.Instance.IsNavigationMethodInTimeWindow(method) && !IsOnCooldown(method)));
        }

        private bool IsOnCooldown(NavigationMethod m) =>
            VirtualCrewManager.Instance.IsNavigationToolOnCooldown(m);

        private WeatherState currentWeather = WeatherState.Clear;
        private float weatherPollTimer = 0f;
        private const float WeatherPollInterval = 30f;
        private const float ToolScanIntervalSeconds = 30f * 60f;
        private float lastToolScanRealTime = float.MinValue;

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;

            weatherPollTimer -= Time.deltaTime;
            if (weatherPollTimer <= 0f)
            {
                currentWeather   = WeatherUtils.GetWeatherState();
                weatherPollTimer = WeatherPollInterval;
            }

            if (ShouldAutoScanTools())
                ScanForTools();
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            var manager   = VirtualCrewManager.Instance;
            var navigator = manager.Navigator;
            var pending   = manager.NavigateRequests.Count > 0
                            ? manager.NavigateRequests[0] : null;
            bool showGlobalTime = ShouldShowGlobalTime(manager, navigator);

            float contentHeight = ButtonHeight * 2                               // name + stats
                                + (showGlobalTime ? ButtonHeight : 0f)           // chronometer global time
                                + ButtonHeight                                   // Search for Tools button
                                + 4f + ButtonHeight                              // space + "Equipment:"
                                + 6 * ButtonHeight                               // 6 tool status labels
                                + ButtonHeight                                   // weather label
                                + 4f                                             // space
                                + (DeveloperMode.IsEnabled ? ButtonHeight : 0f) // override toggle
                                + 4 * ButtonHeight;                              // 4 instrument buttons

            if (pending != null)
                contentHeight += pending.Status == WorkRequestStatus.InProgress
                    ? 14f + ButtonHeight   // progress bar + label
                    : ButtonHeight;        // status label

            foreach (NavigationMethod m in new[] {
                NavigationMethod.Quadrant, NavigationMethod.SunCompass,
                NavigationMethod.Chronometer, NavigationMethod.Chronocompass })
            {
                if (IsOnCooldown(m))
                    contentHeight += ButtonHeight + 14f; // label + cooldown bar
            }

            contentHeight += 4f + ButtonHeight; // space + "Recent Fixes:" label
            if (manager.RecentNavigationResults.Count > 0)
            {
                int totalLines = 0;
                foreach (var r in manager.RecentNavigationResults)
                    totalLines += r.Contains("\n") ? 2 : 1;
                contentHeight += totalLines * ButtonHeight;
            }
            else
            {
                contentHeight += ButtonHeight; // "No fixes taken."
            }

            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : BaseContentHeight + contentHeight;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Navigator");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            var manager   = VirtualCrewManager.Instance;
            var navigator = manager.Navigator;
            var pending   = manager.NavigateRequests.Count > 0
                            ? manager.NavigateRequests[0] : null;

            // ── Navigator stats ─────────────────────────────────────────────
            if (navigator == null)
            {
                GUILayout.Label("No navigator in crew.");
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            GUILayout.Label($"Navigator: {navigator.Name}");
            if (DeveloperMode.IsEnabled)
                GUILayout.Label($"Dexterity: {navigator.Dexterity}   Intelligence: {navigator.Intelligence}");
            else
                GUILayout.Label($"Dexterity: {navigator.AdvDexterity}   Intelligence: {navigator.AdvIntelligence}");

            if (ShouldShowGlobalTime(manager, navigator))
                GUILayout.Label($"Global Time: {FormatGlobalTimeMinute()}");

            // ── Tool search ─────────────────────────────────────────────────
            if (GUILayout.Button("Search for Tools"))
                ScanForTools();

            // ── Equipment status ────────────────────────────────────────────
            GUILayout.Space(4);
            if (_leftLabel == null || SailwindGuiStyle.HasThemeChanged(_leftLabelDarkMode))
            {
                _leftLabel = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
                _leftLabelDarkMode = SailwindGuiStyle.IsDarkMode;
            }
            GUILayout.Label($"Weather: {currentWeather}", _leftLabel);
            GUILayout.Label("Equipment:", _leftLabel);
            GUILayout.Label($"{Check(hasChronocompass)} Chronocompass", _leftLabel);
            GUILayout.Label($"{Check(hasChronometer)} Chronometer", _leftLabel);
            GUILayout.Label($"{Check(hasCompass)} Compass", _leftLabel);
            GUILayout.Label($"{Check(hasQuadrant)} Quadrant", _leftLabel);
            GUILayout.Label($"{Check(hasSunCompass)} Sun Compass", _leftLabel);
            GUILayout.Label($"{Check(hasChipLog)} Chip Log", _leftLabel);
            

            // ── Instrument buttons ──────────────────────────────────────────
            GUILayout.Space(4);
            if (DeveloperMode.IsEnabled)
                overrideTimeWindows = GUILayout.Toggle(overrideTimeWindows, "Override time windows");
            bool navFree = !navigator.IsOccupied && pending == null;

            // Quadrant — latitude, local 20:00–04:00
            GUI.enabled = navFree && CanUseQuadrant;
            if (GUILayout.Button("Quadrant"))
                QueueNavigation(manager, NavigationMethod.Quadrant);

            // Sun Compass — latitude, local 11:00–13:00
            GUI.enabled = navFree && CanUseSunCompass;
            if (GUILayout.Button("Sun Compass"))
                QueueNavigation(manager, NavigationMethod.SunCompass);

            // Chronometer — longitude, global 11:00–13:00
            GUI.enabled = navFree && CanUseChronometer;
            if (GUILayout.Button("Chronometer"))
                QueueNavigation(manager, NavigationMethod.Chronometer);

            // Chronocompass — latitude + longitude, local 08:00–16:00
            GUI.enabled = navFree && CanUseChronocompass;
            if (GUILayout.Button("Chronocompass"))
                QueueNavigation(manager, NavigationMethod.Chronocompass);

            GUI.enabled = true;

            // ── Pending task indicator ──────────────────────────────────────
            if (pending != null)
            {
                if (pending.Status == WorkRequestStatus.Open)
                {
                    GUILayout.Label("Waiting for navigator...");
                }
                else if (pending.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.Label($"[{pending.Navigator.Name}] plotting...");
                    DrawProgressBar(pending.GetProgress());
                }
            }

            // ── Tool cooldowns ──────────────────────────────────────────────
            foreach (NavigationMethod m in new[] {
                NavigationMethod.Quadrant, NavigationMethod.SunCompass,
                NavigationMethod.Chronometer, NavigationMethod.Chronocompass })
            {
                if (IsOnCooldown(m))
                    DrawCooldownBar(m);
            }

            // ── Recent fixes ────────────────────────────────────────────────
            GUILayout.Space(4);
            GUILayout.Label("Recent Fixes:");
            if (manager.RecentNavigationResults.Count == 0)
            {
                GUILayout.Label("No fixes taken.");
            }
            else
            {
                foreach (var result in manager.RecentNavigationResults)
                    GUILayout.Label(result);
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private static string Check(bool value) => value ? "[x]" : "[ ]";

        private bool ShouldShowGlobalTime(VirtualCrewManager manager, Crewman navigator)
        {
            return manager.IsCrewAvailable(navigator) && hasChronometer;
        }

        private static string FormatGlobalTimeMinute()
        {
            int totalMinutes = Mathf.RoundToInt(Sun.sun.globalTime * 60f);
            totalMinutes %= 24 * 60;
            if (totalMinutes < 0)
                totalMinutes += 24 * 60;

            int hour = totalMinutes / 60;
            int minute = totalMinutes % 60;
            return $"{hour:00}:{minute:00}";
        }

        private bool ShouldAutoScanTools()
        {
            return Time.realtimeSinceStartup - lastToolScanRealTime >= ToolScanIntervalSeconds;
        }

        public NavigatorToolScanSaveData GetToolScanSaveData()
        {
            return new NavigatorToolScanSaveData
            {
                hasChronocompass = hasChronocompass,
                hasChronometer = hasChronometer,
                hasCompass = hasCompass,
                hasQuadrant = hasQuadrant,
                hasSunCompass = hasSunCompass,
                hasChipLog = hasChipLog
            };
        }

        public void RestoreToolScanSaveData(NavigatorToolScanSaveData data)
        {
            if (data == null)
                return;

            hasChronocompass = data.hasChronocompass;
            hasChronometer = data.hasChronometer;
            hasCompass = data.hasCompass;
            hasQuadrant = data.hasQuadrant;
            hasSunCompass = data.hasSunCompass;
            hasChipLog = data.hasChipLog;
            lastToolScanRealTime = Time.realtimeSinceStartup;
        }

        private void QueueNavigation(VirtualCrewManager manager, NavigationMethod method)
        {
            if (!manager.TryAddNavigateRequest(method, out string reason, requireTimeWindow: !EffectiveOverride))
                manager.AddNavigationMessage(reason);
        }

        private void ScanForTools()
        {
            bool[] found     = LocatorUtils.FindItemsOnCurrentVessel(ToolItemNames);
            hasChronocompass = found[0];
            hasChronometer   = found[1];
            hasCompass       = found[2];
            hasQuadrant      = found[3];
            hasSunCompass    = found[4];
            hasChipLog       = found[5];
            lastToolScanRealTime = Time.realtimeSinceStartup;
        }

        private Texture2D fillTexture;
        private Texture2D cooldownTexture;

        private void DrawCooldownBar(NavigationMethod method)
        {
            var manager = VirtualCrewManager.Instance;
            if (!manager.IsNavigationToolOnCooldown(method)) return;

            float progress = manager.GetNavigationToolCooldownProgress(method);

            string label = VirtualCrewManager.GetNavigationToolLabel(method);
            GUILayout.Label($"{label} exhausted for now");

            if (cooldownTexture == null)
            {
                cooldownTexture = new Texture2D(1, 1);
                cooldownTexture.SetPixel(0, 0, new Color(1f, 0.6f, 0f));
                cooldownTexture.Apply();
            }
            Rect bar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
            GUI.Box(bar, "");
            float fillWidth = (bar.width - 4) * progress;
            if (fillWidth > 0f)
                GUI.DrawTexture(new Rect(bar.x + 2, bar.y + 2, fillWidth, bar.height - 4), cooldownTexture);
        }

        private void DrawProgressBar(float progress)
        {
            if (fillTexture == null)
            {
                fillTexture = new Texture2D(1, 1);
                fillTexture.SetPixel(0, 0, Color.green);
                fillTexture.Apply();
            }
            Rect bar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
            GUI.Box(bar, "");
            float fillWidth = (bar.width - 4) * Mathf.Clamp01(progress / 100f);
            if (fillWidth > 0f)
                GUI.DrawTexture(new Rect(bar.x + 2, bar.y + 2, fillWidth, bar.height - 4), fillTexture);
        }
    }
}
