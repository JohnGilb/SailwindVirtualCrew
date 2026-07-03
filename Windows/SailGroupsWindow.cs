using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class SailGroupsWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(440, 20, 440, 560);
        private static readonly int windowId = "VirtualCrewSailGroupsWindow".GetHashCode();

        private WindowResizer _resizer;
        private SailGroupMembersWindow _membersWindow;

        public string WindowKey => "SailGroupsWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 440f, 20f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private string groupNameBuffer = "";
        private bool   _renamingGroup  = false;

        private const float ButtonHeight      = 28f;
        private const float BaseContentHeight = 300f;

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            var manager       = VirtualCrewManager.Instance;
            var sails         = manager.AllSails;
            var selectedGroup = manager.SelectedGroup;

            if (selectedGroup != null && !manager.SailGroups.Contains(selectedGroup))
            {
                manager.SelectedGroup = null;
                selectedGroup         = null;
                groupNameBuffer       = "";
                _renamingGroup        = false;
            }

            // Height accounting
            float contentHeight = ButtonHeight * 3; // members toggle + header row
            contentHeight += manager.SailGroups.Count * ButtonHeight;
            if (selectedGroup != null)
            {
                contentHeight += 2f + ButtonHeight; // Space(2) + rename row
                var caps = selectedGroup.GetCommonCapabilities(sails);
                if (caps.HasFlag(SailCapability.Halyard))          contentHeight += 2 * ButtonHeight;
                if (caps.HasFlag(SailCapability.SimpleSheet))       contentHeight += 3 * ButtonHeight;
                else if (caps.HasFlag(SailCapability.SquareSheet))  contentHeight += 2 * ButtonHeight;
                else if (caps.HasFlag(SailCapability.JibSheet))     contentHeight += 4 * ButtonHeight;
                if (caps.HasFlag(SailCapability.Trim))              contentHeight += ButtonHeight;
            }

            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : BaseContentHeight + contentHeight;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Sail Groups");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            var manager       = VirtualCrewManager.Instance;
            var sails         = manager.AllSails;
            var selectedGroup = manager.SelectedGroup;

            // ── Group list ──────────────────────────────────────────────────
            var membersWindow = GetMembersWindow();
            if (membersWindow != null
                && GUILayout.Button(membersWindow.IsVisible ? "Hide Group Members" : "Show Group Members"))
                membersWindow.ToggleWindow();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Groups (click to select)", GUILayout.ExpandWidth(false));
            if (GUILayout.Button("New Group", GUILayout.Width(110)))
            {
                var newGroup = manager.CreateSailGroup("New Group");
                manager.SelectedGroup = newGroup;
                groupNameBuffer       = newGroup.Name;
                _renamingGroup        = true;
            }
            GUILayout.EndHorizontal();

            SailGroup groupToDelete = null;
            foreach (var group in manager.SailGroups)
            {
                GUILayout.BeginHorizontal();
                GUI.color = (group == selectedGroup) ? Color.cyan : Color.white;
                if (GUILayout.Button(group.Name))
                {
                    if (group == selectedGroup) { manager.SelectedGroup = null; groupNameBuffer = ""; _renamingGroup = false; }
                    else                        { manager.SelectedGroup = group; groupNameBuffer = group.Name; _renamingGroup = false; }
                }
                GUI.color   = Color.white;
                GUI.enabled = !group.IsAllSails;
                if (GUILayout.Button("X", GUILayout.Width(28))) groupToDelete = group;
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            if (groupToDelete != null)
            {
                if (selectedGroup == groupToDelete) { groupNameBuffer = ""; _renamingGroup = false; }
                manager.DeleteSailGroup(groupToDelete); // also clears SelectedGroup if needed
                selectedGroup = manager.SelectedGroup;
            }

            // ── Selected-group detail ───────────────────────────────────────
            selectedGroup = manager.SelectedGroup;
            if (selectedGroup != null)
            {
                GUILayout.Space(2);

                if (!_renamingGroup)
                {
                    if (GUILayout.Button("Rename", GUILayout.Width(80)))
                    {
                        _renamingGroup  = true;
                        groupNameBuffer = selectedGroup.Name;
                    }
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    groupNameBuffer = GUILayout.TextField(groupNameBuffer);
                    if (GUILayout.Button("Set", GUILayout.Width(46)) && groupNameBuffer.Trim().Length > 0)
                    {
                        selectedGroup.Name = groupNameBuffer.Trim();
                        _renamingGroup     = false;
                    }
                    GUILayout.EndHorizontal();
                }

                DrawGroupCommandPanel(manager, selectedGroup, sails);
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        // ── Group command panel ─────────────────────────────────────────────

        private void DrawGroupCommandPanel(VirtualCrewManager manager, SailGroup group,
                                           IReadOnlyList<ICommonSailActions> allSails)
        {
            DrawGroupStorageActions(manager, group, allSails);

            var caps = group.GetCommonCapabilities(allSails);
            if (caps == SailCapability.None) return;

            if (caps.HasFlag(SailCapability.Halyard))
            {
                GUILayout.Label("Halyard:");
                GUILayout.BeginHorizontal();
                DrawGroupHalyardButton(manager, "Reef", group, allSails, 0.00f);
                DrawGroupHalyardButton(manager, "1/4",  group, allSails, 0.25f);
                DrawGroupHalyardButton(manager, "1/2",  group, allSails, 0.50f);
                DrawGroupHalyardButton(manager, "3/4",  group, allSails, 0.75f);
                DrawGroupHalyardButton(manager, "Full", group, allSails, 1.00f);
                GUILayout.EndHorizontal();
            }

            if (caps.HasFlag(SailCapability.SimpleSheet))
            {
                GUILayout.Label("Sheet:");
                GUILayout.BeginHorizontal();
                DrawGroupSimpleSheetButton(manager, "Hard",    group, allSails, 0.00f);
                DrawGroupSimpleSheetButton(manager, "1/4",     group, allSails, 0.25f);
                DrawGroupSimpleSheetButton(manager, "1/2",     group, allSails, 0.50f);
                DrawGroupSimpleSheetButton(manager, "3/4",     group, allSails, 0.75f);
                DrawGroupSimpleSheetButton(manager, "Let Fly", group, allSails, 1.00f);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawGroupRelativeSheetButton(manager, "Harden Up", group, allSails, -0.10f);
                DrawGroupRelativeSheetButton(manager, "Ease Out",  group, allSails,  0.10f);
                DrawGroupTrimButton(manager, group, allSails);
                GUILayout.EndHorizontal();
            }
            else if (caps.HasFlag(SailCapability.SquareSheet))
            {
                GUILayout.Label("Sheet:");
                GUILayout.BeginHorizontal();
                DrawGroupDualSheetButton(manager, "Full Port", group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Square, 0.00f, 1.00f);
                DrawGroupDualSheetButton(manager, "1/2 Port",  group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Square, 0.25f, 0.75f);
                DrawGroupDualSheetButton(manager, "Ahead",     group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Square, 0.50f, 0.50f);
                DrawGroupDualSheetButton(manager, "1/2 Stbd",  group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Square, 0.75f, 0.25f);
                DrawGroupDualSheetButton(manager, "Full Stbd", group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Square, 1.00f, 0.00f);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawGroupTrimButton(manager, group, allSails);
                GUILayout.EndHorizontal();
            }
            else if (caps.HasFlag(SailCapability.JibSheet))
            {
                GUILayout.Label("Sheet:");
                GUILayout.BeginHorizontal();
                DrawGroupDualSheetButton(manager, "Full Port", group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Jib, 0.00f, 1.00f);
                DrawGroupDualSheetButton(manager, "3/4 Port",  group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Jib, 0.25f, 1.00f);
                DrawGroupDualSheetButton(manager, "1/2 Port",  group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Jib, 0.50f, 1.00f);
                DrawGroupDualSheetButton(manager, "1/4 Port",  group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Jib, 0.75f, 1.00f);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawGroupDualSheetButton(manager, "Let Fly",   group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 1.00f);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawGroupDualSheetButton(manager, "Full Stbd", group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.00f);
                DrawGroupDualSheetButton(manager, "3/4 Stbd",  group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.25f);
                DrawGroupDualSheetButton(manager, "1/2 Stbd",  group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.50f);
                DrawGroupDualSheetButton(manager, "1/4 Stbd",  group, allSails,
                    DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.75f);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawGroupTrimButton(manager, group, allSails);
                GUILayout.EndHorizontal();
            }
            else if (caps.HasFlag(SailCapability.Trim))
            {
                GUILayout.BeginHorizontal();
                DrawGroupTrimButton(manager, group, allSails);
                GUILayout.EndHorizontal();
            }
        }

        // ── Group button helpers ────────────────────────────────────────────

        private void DrawGroupStorageActions(VirtualCrewManager manager, SailGroup group,
                                             IReadOnlyList<ICommonSailActions> allSails)
        {
            var members = group.GetMembers(allSails).ToList();
            if (members.Count == 0)
                return;

            bool canStoreAll = members.All(SailStorageService.CanStore);
            bool canRestoreAll = members.All(s => s is StowedSail
                && !manager.HasPendingRequestForSailIdentifier(s.getDefaultIdentifier()));

            if (!canStoreAll && !canRestoreAll)
                return;

            GUILayout.Label("Locker:");
            GUILayout.BeginHorizontal();
            if (canStoreAll && GUILayout.Button("Store Sails"))
            {
                foreach (var sail in members)
                    manager.AddSailStorageRequest(new SailStorageRequest(sail));
            }
            if (canRestoreAll && GUILayout.Button("Restore Sails"))
            {
                foreach (var stowed in members.OfType<StowedSail>())
                    manager.AddSailStorageRequest(new SailStorageRequest(stowed.Data));
            }
            GUILayout.EndHorizontal();
        }

        private void DrawGroupHalyardButton(VirtualCrewManager manager, string label,
                                            SailGroup group, IReadOnlyList<ICommonSailActions> allSails,
                                            float target)
        {
            if (GUILayout.Button(label))
            {
                foreach (var sail in group.GetMembers(allSails))
                {
                    if (sail.getRealSail() == null || sail.getHalyardWinch() == null)
                        continue;
                    manager.AddWorkRequest(new WorkRequest(sail, "Halyard " + label,
                        new WinchTarget(sail.getHalyardWinch(), target)));
                }
            }
        }

        private void DrawGroupSimpleSheetButton(VirtualCrewManager manager, string label,
                                                SailGroup group, IReadOnlyList<ICommonSailActions> allSails,
                                                float target)
        {
            if (GUILayout.Button(label))
            {
                foreach (var sail in group.GetMembers(allSails).OfType<SimpleSail>())
                {
                    if (sail.getRealSail() == null || sail.getSheetWinch() == null)
                        continue;
                    manager.AddWorkRequest(new WorkRequest(sail, "Sheet " + label,
                        new WinchTarget(sail.getSheetWinch(), target)));
                }
            }
        }

        private void DrawGroupRelativeSheetButton(VirtualCrewManager manager, string label,
                                                  SailGroup group, IReadOnlyList<ICommonSailActions> allSails,
                                                  float delta)
        {
            if (GUILayout.Button(label))
            {
                foreach (var sail in group.GetMembers(allSails).OfType<SimpleSail>())
                {
                    var winch  = sail.getSheetWinch();
                    if (sail.getRealSail() == null || winch == null || winch.rope == null)
                        continue;
                    float target = Mathf.Clamp01(winch.rope.currentLength + delta);
                    manager.AddWorkRequest(new WorkRequest(sail, "Sheet " + label,
                        new WinchTarget(winch, target)));
                }
            }
        }

        private void DrawGroupDualSheetButton(VirtualCrewManager manager, string label,
                                              SailGroup group, IReadOnlyList<ICommonSailActions> allSails,
                                              DualSheetSail.DualSheetSailSubtype subtype,
                                              float portTarget, float starboardTarget)
        {
            if (GUILayout.Button(label))
            {
                foreach (var sail in group.GetMembers(allSails).OfType<DualSheetSail>()
                                          .Where(s => s.getSubtype() == subtype))
                {
                    if (sail.getRealSail() == null)
                        continue;
                    if (sail.getSubtype() == DualSheetSail.DualSheetSailSubtype.Square)
                    {
                        if (sail.getPortSheetWinch() != null && sail.getStarboardSheetWinch() != null)
                            manager.AddWorkRequest(new WorkRequest(sail, "Square Sheet " + label,
                                new WinchTarget(sail.getPortSheetWinch(), portTarget),
                                new WinchTarget(sail.getStarboardSheetWinch(), starboardTarget))
                            {
                                RequiresTwoDeckhands = true
                            });
                        continue;
                    }
                    if (sail.getPortSheetWinch() != null)
                        manager.AddWorkRequest(new WorkRequest(sail, "Port Sheet " + label,
                        new WinchTarget(sail.getPortSheetWinch(), portTarget)));
                    if (sail.getStarboardSheetWinch() != null)
                        manager.AddWorkRequest(new WorkRequest(sail, "Starboard Sheet " + label,
                        new WinchTarget(sail.getStarboardSheetWinch(), starboardTarget)));
                }
            }
        }

        private void DrawGroupTrimButton(VirtualCrewManager manager,
                                         SailGroup group, IReadOnlyList<ICommonSailActions> allSails)
        {
            if (GUILayout.Button("Trim"))
            {
                foreach (var sail in group.GetMembers(allSails))
                {
                    if (sail is SimpleSail simple)
                        manager.AddTrimRequest(new TrimRequest(simple));
                    else if (sail is DualSheetSail dual)
                    {
                        if (dual.getSubtype() == DualSheetSail.DualSheetSailSubtype.Jib)
                            manager.AddJibTrimRequest(new JibTrimRequest(dual));
                        else if (dual.getSubtype() == DualSheetSail.DualSheetSailSubtype.Square)
                            manager.AddSquareTrimRequest(new SquareTrimRequest(dual));
                    }
                }
            }
        }

        private SailGroupMembersWindow GetMembersWindow()
        {
            if (_membersWindow == null)
                _membersWindow = GetComponent<SailGroupMembersWindow>();
            return _membersWindow;
        }
    }
}
