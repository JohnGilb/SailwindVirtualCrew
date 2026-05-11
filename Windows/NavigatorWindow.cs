using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class NavigatorWindow : MonoBehaviour
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(860, 340, 300, 400);
        private static readonly int windowId = "VirtualCrewNavigatorWindow".GetHashCode();

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

        private readonly List<string> recentResults = new List<string>();
        private const int MaxResults = 3;

        private const float ButtonHeight      = 22f;
        private const float BaseContentHeight = 300f;

        // Time windows for each method
        private static float LocalTime  => Sun.sun.localTime;
        private static float GlobalTime => Sun.sun.globalTime;

        // Quadrant: local 20:00–04:00 (wraps midnight)
        private static bool InQuadrantWindow     => LocalTime  >= 20f || LocalTime  < 4f;
        // Sun Compass: local 11:00–13:00
        private static bool InSunCompassWindow   => LocalTime  >= 11f && LocalTime  < 13f;
        // Chronometer: global 11:00–13:00
        private static bool InChronometerWindow  => GlobalTime >= 11f && GlobalTime < 13f;
        // Chronocompass: local 08:00–16:00
        private static bool InChronocompassWindow => LocalTime >= 8f && LocalTime < 16f;

        private bool overrideTimeWindows = false;

        private bool EffectiveOverride    => DeveloperMode.IsEnabled && overrideTimeWindows;
        private bool CanUseQuadrant      => hasQuadrant      && (EffectiveOverride || InQuadrantWindow);
        private bool CanUseSunCompass    => hasSunCompass    && (EffectiveOverride || InSunCompassWindow);
        private bool CanUseChronometer   => hasChronometer   && (EffectiveOverride || InChronometerWindow);
        private bool CanUseChronocompass => hasChronocompass && (EffectiveOverride || InChronocompassWindow);

        private void Update()
        {
            if (Plugin.ToggleCrewWindow.Value.IsDown())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;

            var manager   = VirtualCrewManager.Instance;
            var navigator = manager.Navigator;
            var pending   = manager.NavigateRequests.Count > 0
                            ? manager.NavigateRequests[0] : null;

            float contentHeight = ButtonHeight * 2                               // name + stats
                                + ButtonHeight                                   // Search for Tools button
                                + 4f + ButtonHeight                              // space + "Equipment:"
                                + 6 * ButtonHeight                               // 6 tool status labels
                                + 4f                                             // space
                                + (DeveloperMode.IsEnabled ? ButtonHeight : 0f) // override toggle
                                + 4 * ButtonHeight;                              // 4 instrument buttons

            if (pending != null)
                contentHeight += pending.Status == WorkRequestStatus.InProgress
                    ? 14f + ButtonHeight   // progress bar + label
                    : ButtonHeight;        // status label

            contentHeight += 4f + ButtonHeight; // space + "Recent Fixes:" label
            contentHeight += recentResults.Count > 0
                ? recentResults.Count * ButtonHeight * 2  // header line + coords line per result
                : ButtonHeight; // "No fixes taken."

            windowRect.height = BaseContentHeight + contentHeight;
            windowRect = GUI.Window(windowId, windowRect, DrawWindow, "Navigator");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            var manager   = VirtualCrewManager.Instance;
            var navigator = manager.Navigator;
            var pending   = manager.NavigateRequests.Count > 0
                            ? manager.NavigateRequests[0] : null;

            // ── Navigator stats ─────────────────────────────────────────────
            if (navigator == null)
            {
                GUILayout.Label("No navigator in crew.");
                GUI.DragWindow();
                return;
            }

            GUILayout.Label($"Navigator: {navigator.Name}");
            if (DeveloperMode.IsEnabled)
                GUILayout.Label($"Dexterity: {navigator.Dexterity}   Intelligence: {navigator.Intelligence}");
            else
                GUILayout.Label($"Dexterity: {navigator.AdvDexterity}   Intelligence: {navigator.AdvIntelligence}");

            // ── Tool search ─────────────────────────────────────────────────
            if (GUILayout.Button("Search for Tools"))
                ScanForTools();

            // ── Equipment status ────────────────────────────────────────────
            GUILayout.Space(4);
            GUILayout.Label("Equipment:");
            GUILayout.Label($"  {Check(hasChronocompass)} Chronocompass");
            GUILayout.Label($"  {Check(hasChronometer)}   Chronometer");
            GUILayout.Label($"  {Check(hasCompass)}       Compass");
            GUILayout.Label($"  {Check(hasQuadrant)}      Quadrant");
            GUILayout.Label($"  {Check(hasSunCompass)}    Sun Compass");
            GUILayout.Label($"  {Check(hasChipLog)}       Chip Log");

            // ── Instrument buttons ──────────────────────────────────────────
            GUILayout.Space(4);
            if (DeveloperMode.IsEnabled)
                overrideTimeWindows = GUILayout.Toggle(overrideTimeWindows, "Override time windows");
            bool navFree = !navigator.IsOccupied && pending == null;

            // Quadrant — latitude, local 20:00–04:00
            GUI.enabled = navFree && CanUseQuadrant;
            if (GUILayout.Button("Quadrant"))
            {
                hasQuadrant = LocatorUtils.findItem(new[] { "quadrant" })[0];
                if (!hasQuadrant)
                    AddResult("Can't find quadrant nearby!");
                else
                    manager.AddNavigateRequest(new NavigateRequest(NavigationMethod.Quadrant, OnNavigationComplete));
            }

            // Sun Compass — latitude, local 11:00–13:00
            GUI.enabled = navFree && CanUseSunCompass;
            if (GUILayout.Button("Sun Compass"))
            {
                hasSunCompass = LocatorUtils.findItem(new[] { "sun compass" })[0];
                if (!hasSunCompass)
                    AddResult("Can't find sun compass nearby!");
                else
                    manager.AddNavigateRequest(new NavigateRequest(NavigationMethod.SunCompass, OnNavigationComplete));
            }

            // Chronometer — longitude, global 11:00–13:00
            GUI.enabled = navFree && CanUseChronometer;
            if (GUILayout.Button("Chronometer"))
            {
                hasChronometer = LocatorUtils.findItem(new[] { "chronometer" })[0];
                if (!hasChronometer)
                    AddResult("Can't find chronometer nearby!");
                else
                    manager.AddNavigateRequest(new NavigateRequest(NavigationMethod.Chronometer, OnNavigationComplete));
            }

            // Chronocompass — latitude + longitude, local 08:00–16:00
            GUI.enabled = navFree && CanUseChronocompass;
            if (GUILayout.Button("Chronocompass"))
            {
                hasChronocompass = LocatorUtils.findItem(new[] { "chronocompass" })[0];
                if (!hasChronocompass)
                    AddResult("Can't find chronocompass nearby!");
                else
                    manager.AddNavigateRequest(new NavigateRequest(NavigationMethod.Chronocompass, OnNavigationComplete));
            }

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

            // ── Recent fixes ────────────────────────────────────────────────
            GUILayout.Space(4);
            GUILayout.Label("Recent Fixes:");
            if (recentResults.Count == 0)
            {
                GUILayout.Label("No fixes taken.");
            }
            else
            {
                foreach (var result in recentResults)
                    GUILayout.Label(result);
            }

            GUI.DragWindow();
        }

        private static string Check(bool value) => value ? "[x]" : "[ ]";

        private void AddResult(string text)
        {
            recentResults.Insert(0, text);
            if (recentResults.Count > MaxResults)
                recentResults.RemoveAt(recentResults.Count - 1);
        }

        private void OnNavigationComplete(NavigationResult result)
        {
            string coords = "";
            if (result.HasLatitude)  coords += result.LatitudeText;
            if (result.HasLatitude && result.HasLongitude) coords += "  ";
            if (result.HasLongitude) coords += result.LongitudeText;
            AddResult($"{result.Header}\n{coords}");
        }

        private void ScanForTools()
        {
            bool[] found     = LocatorUtils.findItem(ToolItemNames);
            hasChronocompass = found[0];
            hasChronometer   = found[1];
            hasCompass       = found[2];
            hasQuadrant      = found[3];
            hasSunCompass    = found[4];
            hasChipLog       = found[5];
        }

        private Texture2D fillTexture;

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
