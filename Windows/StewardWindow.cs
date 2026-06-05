using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class StewardWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(880, 520, 320, 190);
        private static readonly int windowId = "VirtualCrewStewardWindow".GetHashCode();
        private WindowResizer _resizer;
        private bool _hasFoodScan;
        private int _looseFoodCount;
        private int _unsealedCrateFoodCount;
        private List<string> _looseFoodLines = new List<string>();
        private List<string> _unsealedCrateFoodLines = new List<string>();
        private Vector2 _foodScanScroll;

        public string WindowKey => "StewardWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 880f, 520f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            float foodScanHeight = _hasFoodScan
                ? Mathf.Min(180f, Mathf.Max(60f, (_looseFoodLines.Count + _unsealedCrateFoodLines.Count + 4) * 22f))
                : 0f;
            float contentHeight = 150f + foodScanHeight;
            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : contentHeight + 40f;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Steward");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            var manager = VirtualCrewManager.Instance;
            var steward = manager.FreshestCrewman(ShipRole.Steward);
            if (steward == null)
                GUILayout.Label("No available Steward");
            else if (DeveloperMode.IsEnabled)
                GUILayout.Label($"Steward: {steward.Name}  [{steward.FatigueTag}]   D{steward.Dexterity}  W{steward.Wisdom}");
            else
                GUILayout.Label($"Steward: {steward.Name}  [{steward.FatigueTag}]   D{steward.AdvDexterity}  W{steward.AdvWisdom}");

            DrawLimitSlider("Thirst Limit", manager.StewardThirstLimitPercent, manager.SetStewardThirstLimit);
            DrawLimitSlider("Hunger Limit", manager.StewardHungerLimitPercent, manager.SetStewardHungerLimit);

            GUI.enabled = manager.CanStartStewardPhilosophy();
            if (GUILayout.Button("Philosophize with Steward"))
                manager.StartStewardPhilosophy();
            GUI.enabled = true;

            if (GUILayout.Button("Scan for Food"))
            {
                manager.ScanStewardFoodSources(
                    out _looseFoodCount,
                    out _unsealedCrateFoodCount,
                    out _looseFoodLines,
                    out _unsealedCrateFoodLines);
                _hasFoodScan = true;
            }
            if (_hasFoodScan)
                DrawFoodScanResults();

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private static void DrawLimitSlider(string label, float value, System.Action<float> setter)
        {
            GUILayout.Label(label + ": " + Mathf.RoundToInt(value) + "%");
            float next = GUILayout.HorizontalSlider(value, 0f, 100f);
            if (!Mathf.Approximately(next, value))
                setter(next);
        }

        private void DrawFoodScanResults()
        {
            GUILayout.Label("Food: " + _looseFoodCount + " loose, " + _unsealedCrateFoodCount + " in unsealed crates");

            float scrollHeight = Mathf.Min(160f, Mathf.Max(48f, (_looseFoodLines.Count + _unsealedCrateFoodLines.Count + 4) * 22f));
            _foodScanScroll = GUILayout.BeginScrollView(_foodScanScroll, GUILayout.Height(scrollHeight));
            GUILayout.Label("Loose");
            DrawFoodLines(_looseFoodLines);
            GUILayout.Label("Unsealed Crates");
            DrawFoodLines(_unsealedCrateFoodLines);
            GUILayout.EndScrollView();
        }

        private static void DrawFoodLines(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                GUILayout.Label("0");
                return;
            }

            foreach (var line in lines)
                GUILayout.Label(line);
        }
    }
}
