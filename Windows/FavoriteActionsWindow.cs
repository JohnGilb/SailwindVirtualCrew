using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class FavoriteActionsWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(20, 840, 500, 560);
        private static readonly int windowId = "VirtualCrewFavoriteActionsWindow".GetHashCode();

        private WindowResizer _resizer;
        private string _captureActionId;
        private FavoriteAction _selectedAction;
        private SailGroup _selectedGroup;
        private string _newActionName = "New Favorite";
        private string _selectedActionNameBuffer = "";

        public string WindowKey => "FavoriteActionsWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 20f, 840f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;

            if (!string.IsNullOrEmpty(_captureActionId))
                return;

            var mgr = VirtualCrewManager.Instance;
            foreach (var action in mgr.FavoriteActions.ToList())
            {
                if (action.Key != KeyCode.None && Input.GetKeyDown(action.Key))
                    mgr.InvokeFavoriteAction(action);
            }
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : 560f;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Favorite Actions");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            CapturePendingKey();

            GUILayout.Space(4);
            var mgr = VirtualCrewManager.Instance;
            var actions = mgr.FavoriteActions.ToList();
            if (_selectedAction != null && !actions.Contains(_selectedAction))
            {
                _selectedAction = null;
                _selectedGroup = null;
                _selectedActionNameBuffer = "";
            }

            DrawCreateControls(mgr);

            if (!string.IsNullOrEmpty(_captureActionId))
                GUILayout.Label("Press a key. Esc cancels, Backspace clears.");

            if (actions.Count == 0)
            {
                GUILayout.Label("No favorite actions yet.");
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            FavoriteAction remove = null;
            foreach (var action in actions)
                DrawFavoriteActionRow(mgr, action, ref remove);

            if (remove != null)
            {
                if (_captureActionId == remove.id)
                    _captureActionId = null;
                if (_selectedAction == remove)
                {
                    _selectedAction = null;
                    _selectedGroup = null;
                    _selectedActionNameBuffer = "";
                }
                mgr.RemoveFavoriteAction(remove);
            }

            GUILayout.Space(6);
            DrawSelectedActionEditor(mgr);

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void DrawCreateControls(VirtualCrewManager mgr)
        {
            GUILayout.BeginHorizontal();
            _newActionName = GUILayout.TextField(_newActionName);
            if (GUILayout.Button("Create", GUILayout.Width(80)))
            {
                var action = mgr.CreateFavoriteAction(_newActionName.Trim());
                SelectAction(action);
                _newActionName = "New Favorite";
            }
            GUILayout.EndHorizontal();
        }

        private void DrawFavoriteActionRow(VirtualCrewManager mgr, FavoriteAction action, ref FavoriteAction remove)
        {
            GUILayout.BeginHorizontal();
            GUI.color = action == _selectedAction ? Color.cyan : Color.white;
            if (GUILayout.Button(action.DisplayName + "  [" + action.HotkeyLabel + "]"))
                SelectAction(action);
            GUI.color = Color.white;

            if (GUILayout.Button("Run", GUILayout.Width(48)))
                mgr.InvokeFavoriteAction(action);
            if (GUILayout.Button("Set Key", GUILayout.Width(70)))
                _captureActionId = action.id;
            if (GUILayout.Button("X", GUILayout.Width(28)))
                remove = action;
            GUILayout.EndHorizontal();
        }

        private void SelectAction(FavoriteAction action)
        {
            _selectedAction = action;
            _selectedGroup = null;
            _selectedActionNameBuffer = action != null ? action.DisplayName : "";
        }

        private void DrawSelectedActionEditor(VirtualCrewManager mgr)
        {
            if (_selectedAction == null)
            {
                GUILayout.Label("Select a favorite to edit its sail group actions.");
                return;
            }

            GUILayout.Label("Editing Favorite");
            GUILayout.BeginHorizontal();
            _selectedActionNameBuffer = GUILayout.TextField(_selectedActionNameBuffer);
            if (GUILayout.Button("Rename", GUILayout.Width(80)))
                mgr.SetFavoriteActionName(_selectedAction, _selectedActionNameBuffer);
            GUILayout.EndHorizontal();

            if (!_selectedAction.IsCustom)
            {
                GUILayout.Label("Legacy favorite. Editing it here converts it to a custom favorite.");
            }

            DrawShipActionEditor(mgr);
            DrawGroupPicker(mgr);
            GUILayout.Space(4);
            DrawGroupCommandEditor(mgr);
        }

        private void DrawShipActionEditor(VirtualCrewManager mgr)
        {
            GUILayout.Label("Ship Orders");
            GUILayout.BeginHorizontal();
            DrawShipActionToggle(mgr, "Drop Anchor", FavoriteShipAction.DropAnchor, _selectedAction.dropAnchor);
            DrawShipActionToggle(mgr, "Raise Anchor", FavoriteShipAction.RaiseAnchor, _selectedAction.raiseAnchor);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            DrawShipActionToggle(mgr, "Moor Port", FavoriteShipAction.MoorPort, _selectedAction.moorPort);
            DrawShipActionToggle(mgr, "Moor Starboard", FavoriteShipAction.MoorStarboard, _selectedAction.moorStarboard);
            GUILayout.EndHorizontal();
        }

        private void DrawShipActionToggle(VirtualCrewManager mgr, string label, FavoriteShipAction actionKind, bool enabled)
        {
            if (GUILayout.Button((enabled ? "Remove " : "Add ") + label))
                mgr.SetFavoriteShipAction(_selectedAction, actionKind, !enabled);
        }

        private void DrawGroupPicker(VirtualCrewManager mgr)
        {
            GUILayout.Label("Groups (click to edit)");
            foreach (var group in mgr.SailGroups)
            {
                string label = group.Name + GetFavoriteGroupSummary(mgr, group);
                GUI.color = group == _selectedGroup ? Color.cyan : Color.white;
                if (GUILayout.Button(label))
                    _selectedGroup = group == _selectedGroup ? null : group;
                GUI.color = Color.white;
            }
        }

        private string GetFavoriteGroupSummary(VirtualCrewManager mgr, SailGroup group)
        {
            if (!mgr.TryGetFavoriteActionTargets(_selectedAction, group, out StandingOrderTargets targets))
                return "";

            var parts = new List<string>();
            if (targets.HasHalyard)
                parts.Add("[H:" + FormatHalyardTarget(targets.Halyard) + "]");

            string sheet = GetSheetLabel(group, targets);
            if (!string.IsNullOrEmpty(sheet))
                parts.Add("[S:" + sheet + "]");
            if (targets.HasTrim)
                parts.Add("[T:Trim]");

            return parts.Count == 0 ? "" : " " + string.Join(" ", parts.ToArray());
        }

        private static string GetSheetLabel(SailGroup group, StandingOrderTargets targets)
        {
            bool hasSimple = targets.HasSimpleSheet;
            bool hasDual = targets.HasPortSheet || targets.HasStarboardSheet;
            if (hasSimple && hasDual)
                return "Mixed";
            if (hasSimple)
                return FormatSimpleSheetTarget(targets.SimpleSheet);
            if (!hasDual)
                return null;
            if (!targets.HasPortSheet || !targets.HasStarboardSheet)
                return "Mixed";

            return "P" + FormatPercent(targets.PortSheet) + "/S" + FormatPercent(targets.StarboardSheet);
        }

        private void DrawGroupCommandEditor(VirtualCrewManager mgr)
        {
            if (_selectedGroup == null)
            {
                GUILayout.Label("Select a group to add Halyard and Sheet actions.");
                return;
            }

            GUILayout.Label("Commands: " + _selectedGroup.Name);
            SailGroupCommandPalette.Draw(
                mgr,
                _selectedGroup,
                includeTrim: true,
                onHalyard: (label, target) => mgr.SetFavoriteActionHalyard(_selectedAction, _selectedGroup, target),
                onSimpleSheet: (label, target) => mgr.SetFavoriteActionSimpleSheet(_selectedAction, _selectedGroup, target),
                onDualSheet: (label, subtype, portTarget, starboardTarget) =>
                    mgr.SetFavoriteActionDualSheet(_selectedAction, _selectedGroup, portTarget, starboardTarget),
                onTrim: () => mgr.SetFavoriteActionTrim(_selectedAction, _selectedGroup));

            GUILayout.Space(4);
            if (GUILayout.Button("Clear this group from favorite"))
                mgr.ClearFavoriteActionGroup(_selectedAction, _selectedGroup);
        }

        private void CapturePendingKey()
        {
            if (string.IsNullOrEmpty(_captureActionId))
                return;

            var e = Event.current;
            if (e.type != EventType.KeyDown || e.keyCode == KeyCode.None)
                return;

            var action = VirtualCrewManager.Instance.FavoriteActions.FirstOrDefault(a => a.id == _captureActionId);
            if (action != null)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    _captureActionId = null;
                    e.Use();
                    return;
                }

                if (e.keyCode == KeyCode.Backspace || e.keyCode == KeyCode.Delete)
                    VirtualCrewManager.Instance.SetFavoriteActionKey(action, KeyCode.None);
                else
                    VirtualCrewManager.Instance.SetFavoriteActionKey(action, e.keyCode);
            }

            _captureActionId = null;
            e.Use();
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

        private static bool Approximately(float value, float target)
        {
            return Mathf.Abs(value - target) <= 0.01f;
        }

        private static string FormatPercent(float target)
        {
            return Mathf.RoundToInt(Mathf.Clamp01(target) * 100f) + "%";
        }
    }
}
