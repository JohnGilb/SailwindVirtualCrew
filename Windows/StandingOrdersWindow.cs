using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class StandingOrdersWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(860, 340, 460, 560);
        private static readonly int windowId = "VirtualCrewStandingOrdersWindow".GetHashCode();

        private WindowResizer _resizer;
        private SailGroup _selectedGroup;
        private StandingOrderWindState _selectedState = StandingOrderWindState.PortClose;

        private const float DefaultHeight = 560f;

        public string WindowKey => "StandingOrdersWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 860f, 340f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        public bool IsVisible => showWindow;

        public void ToggleWindow()
        {
            SetVisible(!showWindow);
        }

        public void SetVisible(bool visible)
        {
            showWindow = visible;
        }

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
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Standing Orders");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            GUILayout.Space(4);

            var manager = VirtualCrewManager.Instance;
            if (_selectedGroup != null && !manager.SailGroups.Contains(_selectedGroup))
                _selectedGroup = null;

            DrawWindStateButtons(manager);
            GUILayout.Space(6);
            DrawGroupPicker(manager);
            GUILayout.Space(4);
            DrawSelectedGroupCommands(manager);

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void DrawWindStateButtons(VirtualCrewManager manager)
        {
            GUILayout.Label("Wind Direction");

            GUILayout.BeginHorizontal();
            DrawWindStateButton("Port Close", StandingOrderWindState.PortClose);
            DrawWindStateButton("Port Beam", StandingOrderWindState.PortBeam);
            DrawWindStateButton("Port Broad", StandingOrderWindState.PortBroad);
            DrawWindStateButton("Port Run", StandingOrderWindState.PortRun);
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Mirror All Port commands to Starboard"))
                manager.MirrorPortStandingOrdersToStarboard();

            GUILayout.BeginHorizontal();
            DrawWindStateButton("Stbd Close", StandingOrderWindState.StbdClose);
            DrawWindStateButton("Stbd Beam", StandingOrderWindState.StbdBeam);
            DrawWindStateButton("Stbd Broad", StandingOrderWindState.StbdBroad);
            DrawWindStateButton("Stbd Run", StandingOrderWindState.StbdRun);
            GUILayout.EndHorizontal();
        }

        private void DrawWindStateButton(string label, StandingOrderWindState state)
        {
            GUI.color = _selectedState == state ? Color.cyan : Color.white;
            if (GUILayout.Button(label))
                _selectedState = state;
            GUI.color = Color.white;
        }

        private void DrawGroupPicker(VirtualCrewManager manager)
        {
            GUILayout.Label("Groups (click to edit)");
            foreach (var group in manager.SailGroups)
            {
                string label = group.Name + GetStandingOrderSummary(manager, group);
                GUI.color = group == _selectedGroup ? Color.cyan : Color.white;
                if (GUILayout.Button(label))
                    _selectedGroup = group == _selectedGroup ? null : group;
                GUI.color = Color.white;
            }
        }

        private string GetStandingOrderSummary(VirtualCrewManager manager, SailGroup group)
        {
            var members = group.GetMembers(manager.AllSails).ToList();
            if (members.Count == 0)
                return "";

            var parts = new List<string>();
            string halyard = GetAggregateHalyardLabel(manager, members);
            if (!string.IsNullOrEmpty(halyard))
                parts.Add("[H:" + halyard + "]");

            string sheet = GetAggregateSheetLabel(manager, members);
            if (!string.IsNullOrEmpty(sheet))
                parts.Add("[S:" + sheet + "]");

            return parts.Count == 0 ? "" : " " + string.Join(" ", parts.ToArray());
        }

        private string GetAggregateHalyardLabel(VirtualCrewManager manager, List<ICommonSailActions> members)
        {
            int configured = 0;
            string label = null;
            bool mixed = false;

            foreach (var member in members)
            {
                if (!manager.TryGetStandingOrderTargets(_selectedState, member, out StandingOrderTargets targets)
                    || !targets.HasHalyard)
                    continue;

                configured++;
                string current = FormatHalyardTarget(targets.Halyard);
                if (label == null)
                    label = current;
                else if (label != current)
                    mixed = true;
            }

            if (configured == 0)
                return null;

            return mixed || configured != members.Count ? "Mixed" : label;
        }

        private string GetAggregateSheetLabel(VirtualCrewManager manager, List<ICommonSailActions> members)
        {
            int configured = 0;
            string label = null;
            bool mixed = false;

            foreach (var member in members)
            {
                if (!manager.TryGetStandingOrderTargets(_selectedState, member, out StandingOrderTargets targets))
                    continue;

                string current = GetSheetLabel(member, targets);
                if (string.IsNullOrEmpty(current))
                    continue;

                configured++;
                if (current == "Mixed")
                    mixed = true;
                else if (label == null)
                    label = current;
                else if (label != current)
                    mixed = true;
            }

            if (configured == 0)
                return null;

            return mixed || configured != members.Count ? "Mixed" : label;
        }

        private static string GetSheetLabel(ICommonSailActions sail, StandingOrderTargets targets)
        {
            if (sail is SimpleSail && targets.HasSimpleSheet)
                return FormatSimpleSheetTarget(targets.SimpleSheet);

            var dual = sail as DualSheetSail;
            if (dual == null || (!targets.HasPortSheet && !targets.HasStarboardSheet))
                return null;

            if (!targets.HasPortSheet || !targets.HasStarboardSheet)
                return "Mixed";

            return FormatDualSheetTarget(dual.getSubtype(), targets.PortSheet, targets.StarboardSheet);
        }

        private static string FormatHalyardTarget(float target)
        {
            if (Approximately(target, 0.00f)) return "Reef";
            if (Approximately(target, 0.25f)) return "1/4";
            if (Approximately(target, 0.50f)) return "1/2";
            if (Approximately(target, 0.75f)) return "3/4";
            if (Approximately(target, 1.00f)) return "Full";
            return FormatPercent(target);
        }

        private static string FormatSimpleSheetTarget(float target)
        {
            if (Approximately(target, 0.00f)) return "Hard";
            if (Approximately(target, 0.25f)) return "1/4";
            if (Approximately(target, 0.50f)) return "1/2";
            if (Approximately(target, 0.75f)) return "3/4";
            if (Approximately(target, 1.00f)) return "Let Fly";
            return FormatPercent(target);
        }

        private static string FormatDualSheetTarget(DualSheetSail.DualSheetSailSubtype subtype,
                                                    float portTarget, float starboardTarget)
        {
            if (subtype == DualSheetSail.DualSheetSailSubtype.Square)
            {
                if (Approximately(portTarget, 0.00f) && Approximately(starboardTarget, 1.00f)) return "Full Port";
                if (Approximately(portTarget, 0.25f) && Approximately(starboardTarget, 0.75f)) return "1/2 Port";
                if (Approximately(portTarget, 0.50f) && Approximately(starboardTarget, 0.50f)) return "Ahead";
                if (Approximately(portTarget, 0.75f) && Approximately(starboardTarget, 0.25f)) return "1/2 Stbd";
                if (Approximately(portTarget, 1.00f) && Approximately(starboardTarget, 0.00f)) return "Full Stbd";
            }
            else
            {
                if (Approximately(portTarget, 0.00f) && Approximately(starboardTarget, 1.00f)) return "Full Port";
                if (Approximately(portTarget, 0.25f) && Approximately(starboardTarget, 1.00f)) return "3/4 Port";
                if (Approximately(portTarget, 0.50f) && Approximately(starboardTarget, 1.00f)) return "1/2 Port";
                if (Approximately(portTarget, 0.75f) && Approximately(starboardTarget, 1.00f)) return "1/4 Port";
                if (Approximately(portTarget, 1.00f) && Approximately(starboardTarget, 1.00f)) return "Let Fly";
                if (Approximately(portTarget, 1.00f) && Approximately(starboardTarget, 0.00f)) return "Full Stbd";
                if (Approximately(portTarget, 1.00f) && Approximately(starboardTarget, 0.25f)) return "3/4 Stbd";
                if (Approximately(portTarget, 1.00f) && Approximately(starboardTarget, 0.50f)) return "1/2 Stbd";
                if (Approximately(portTarget, 1.00f) && Approximately(starboardTarget, 0.75f)) return "1/4 Stbd";
            }

            return "P" + FormatPercent(portTarget) + "/S" + FormatPercent(starboardTarget);
        }

        private static bool Approximately(float value, float target)
        {
            return Mathf.Abs(value - target) <= 0.01f;
        }

        private static string FormatPercent(float target)
        {
            return Mathf.RoundToInt(Mathf.Clamp01(target) * 100f) + "%";
        }

        private void DrawSelectedGroupCommands(VirtualCrewManager manager)
        {
            if (_selectedGroup == null)
            {
                GUILayout.Label("Select a group to edit saved sail positions.");
                return;
            }

            var sails = manager.AllSails;
            var caps = _selectedGroup.GetCommonCapabilities(sails);
            if (caps == SailCapability.None)
            {
                GUILayout.Label("This group has no sails.");
                return;
            }

            GUILayout.Label("Editing: " + WindAngleUtils.GetStateLabel(_selectedState) + " / " + _selectedGroup.Name);

            if (caps.HasFlag(SailCapability.Halyard))
            {
                GUILayout.Label("Halyard:");
                GUILayout.BeginHorizontal();
                DrawHalyardButton(manager, "Reef", 0.00f);
                DrawHalyardButton(manager, "1/4", 0.25f);
                DrawHalyardButton(manager, "1/2", 0.50f);
                DrawHalyardButton(manager, "3/4", 0.75f);
                DrawHalyardButton(manager, "Full", 1.00f);
                GUILayout.EndHorizontal();
            }

            if (caps.HasFlag(SailCapability.SimpleSheet))
            {
                GUILayout.Label("Sheet:");
                GUILayout.BeginHorizontal();
                DrawSimpleSheetButton(manager, "Hard", 0.00f);
                DrawSimpleSheetButton(manager, "1/4", 0.25f);
                DrawSimpleSheetButton(manager, "1/2", 0.50f);
                DrawSimpleSheetButton(manager, "3/4", 0.75f);
                DrawSimpleSheetButton(manager, "Let Fly", 1.00f);
                GUILayout.EndHorizontal();
            }
            else if (caps.HasFlag(SailCapability.SquareSheet))
            {
                GUILayout.Label("Sheet:");
                GUILayout.BeginHorizontal();
                DrawDualSheetButton(manager, "Full Port",
                    DualSheetSail.DualSheetSailSubtype.Square, 0.00f, 1.00f);
                DrawDualSheetButton(manager, "1/2 Port",
                    DualSheetSail.DualSheetSailSubtype.Square, 0.25f, 0.75f);
                DrawDualSheetButton(manager, "Ahead",
                    DualSheetSail.DualSheetSailSubtype.Square, 0.50f, 0.50f);
                DrawDualSheetButton(manager, "1/2 Stbd",
                    DualSheetSail.DualSheetSailSubtype.Square, 0.75f, 0.25f);
                DrawDualSheetButton(manager, "Full Stbd",
                    DualSheetSail.DualSheetSailSubtype.Square, 1.00f, 0.00f);
                GUILayout.EndHorizontal();
            }
            else if (caps.HasFlag(SailCapability.JibSheet))
            {
                GUILayout.Label("Sheet:");
                GUILayout.BeginHorizontal();
                DrawDualSheetButton(manager, "Full Port",
                    DualSheetSail.DualSheetSailSubtype.Jib, 0.00f, 1.00f);
                DrawDualSheetButton(manager, "3/4 Port",
                    DualSheetSail.DualSheetSailSubtype.Jib, 0.25f, 1.00f);
                DrawDualSheetButton(manager, "1/2 Port",
                    DualSheetSail.DualSheetSailSubtype.Jib, 0.50f, 1.00f);
                DrawDualSheetButton(manager, "1/4 Port",
                    DualSheetSail.DualSheetSailSubtype.Jib, 0.75f, 1.00f);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawDualSheetButton(manager, "Let Fly",
                    DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 1.00f);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawDualSheetButton(manager, "Full Stbd",
                    DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.00f);
                DrawDualSheetButton(manager, "3/4 Stbd",
                    DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.25f);
                DrawDualSheetButton(manager, "1/2 Stbd",
                    DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.50f);
                DrawDualSheetButton(manager, "1/4 Stbd",
                    DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.75f);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4);
            if (GUILayout.Button("Clear saved orders for this group / wind"))
                manager.ClearStandingOrdersForGroup(_selectedState, _selectedGroup);
        }

        private void DrawHalyardButton(VirtualCrewManager manager, string label, float target)
        {
            if (GUILayout.Button(label))
                manager.SetStandingOrderHalyard(_selectedState, _selectedGroup, target);
        }

        private void DrawSimpleSheetButton(VirtualCrewManager manager, string label, float target)
        {
            if (GUILayout.Button(label))
                manager.SetStandingOrderSimpleSheet(_selectedState, _selectedGroup, target);
        }

        private void DrawDualSheetButton(VirtualCrewManager manager, string label,
                                         DualSheetSail.DualSheetSailSubtype subtype,
                                         float portTarget, float starboardTarget)
        {
            if (GUILayout.Button(label))
                manager.SetStandingOrderDualSheet(_selectedState, _selectedGroup, subtype, portTarget, starboardTarget);
        }
    }
}
