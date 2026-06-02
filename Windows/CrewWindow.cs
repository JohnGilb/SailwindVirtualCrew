using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class CrewWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(20, 20, 440, 560);
        private static readonly int windowId = "VirtualCrewWindow".GetHashCode();
        private WindowResizer _resizer;

        public string WindowKey => "CrewWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 20f, 20f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private ICommonSailActions selectedSail = null;
        private string renameBuffer = "";
        private string vesselRenameBuffer = "";
        private bool   _renamingSail   = false;
        private bool   _renamingVessel = false;
        private readonly Dictionary<GPButtonRopeWinch, bool> _pendingWinchCache = new Dictionary<GPButtonRopeWinch, bool>();

        private const float ButtonHeight      = 28f;
        private const float BaseContentHeight = 600f;

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            var manager = VirtualCrewManager.Instance;
            var sails   = manager.AllSails;
            _pendingWinchCache.Clear();

            if (selectedSail != null && !sails.Contains(selectedSail))
            {
                selectedSail  = null;
                renameBuffer  = "";
                _renamingSail = false;
            }

            float sailListHeight = sails.Count > 0 ? sails.Count * ButtonHeight : 40f;

            float commandHeight = 0f;
            if (selectedSail != null)
            {
                // Space(4) + "Commands:" label + rename row + (opt group btn) + "Halyard:" + halyard row + "Sheet:"
                commandHeight = 4f + ButtonHeight * 5;
                var sg = manager.SelectedGroup;
                if (sg != null && !sg.IsAllSails) commandHeight += ButtonHeight;
                if (selectedSail is SimpleSail)
                    commandHeight += ButtonHeight * 2;
                else if (selectedSail is DualSheetSail ds)
                    commandHeight += ds.getSubtype() == DualSheetSail.DualSheetSailSubtype.Jib
                        ? ButtonHeight * 4
                        : ButtonHeight * 2;
            }

            // Vessel label + rename row + "Sails" label = 3 rows
            float autoHeight = BaseContentHeight + ButtonHeight * 3 + sailListHeight + commandHeight;
            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : autoHeight;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Deck Orders");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            var manager = VirtualCrewManager.Instance;
            var sails   = manager.AllSails;

            // ── Vessel ──────────────────────────────────────────────────────
            string vesselKey     = manager.CurrentVesselKey;
            string vesselFriendly = manager.CurrentVesselFriendlyName;
            string vesselDisplay = !string.IsNullOrEmpty(vesselFriendly) ? vesselFriendly
                                 : !string.IsNullOrEmpty(vesselKey)      ? vesselKey
                                 : "(No vessel — press V to scan)";
            GUILayout.Label($"Vessel: {vesselDisplay}");
            if (!_renamingVessel)
            {
                if (GUILayout.Button("Rename", GUILayout.Width(80)))
                {
                    _renamingVessel    = true;
                    vesselRenameBuffer = vesselFriendly ?? "";
                }
            }
            else
            {
                GUILayout.BeginHorizontal();
                vesselRenameBuffer = GUILayout.TextField(vesselRenameBuffer);
                if (GUILayout.Button("Set", GUILayout.Width(46)) && vesselRenameBuffer.Trim().Length > 0)
                {
                    manager.SetVesselFriendlyName(vesselRenameBuffer.Trim());
                    _renamingVessel = false;
                }
                GUILayout.EndHorizontal();
            }

            // ── Anchor ──────────────────────────────────────────────────────
            var anchors = manager.AnchorWinches;
            if (anchors.Count > 0)
            {
                GUILayout.BeginHorizontal();
                bool anchorBusy = manager.HasPendingRequestForAnyWinch(anchors);
                GUI.enabled = !anchorBusy;
                if (GUILayout.Button("Drop Anchor"))
                    manager.AddWorkRequest(new WorkRequest(null, "Drop Anchor",
                        anchors.Select(w => new WinchTarget(w, 1f)).ToArray()));
                if (GUILayout.Button("Raise Anchor"))
                    manager.AddWorkRequest(new WorkRequest(null, "Raise Anchor",
                        anchors.Select(w => new WinchTarget(w, 0f)).ToArray()));
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }

            // ── Sail list ───────────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            DrawMooringButton(manager, "Moor Port", MooringSide.Port);
            DrawMooringButton(manager, "Moor Starboard", MooringSide.Starboard);
            GUILayout.EndHorizontal();

            GUILayout.Label("Sails  (click to select)");
            if (sails.Count == 0)
            {
                GUILayout.Label("No sails mapped. Press V to scan the boat.");
            }
            else
            {
                foreach (var sail in sails)
                {
                    GUI.color = (sail == selectedSail) ? Color.cyan : Color.white;
                    if (GUILayout.Button(sail.getSailName()))
                    {
                        if (sail == selectedSail) { selectedSail = null; renameBuffer = ""; _renamingSail = false; }
                        else                      { selectedSail = sail; renameBuffer = sail.FriendlyName ?? ""; _renamingSail = false; }
                    }
                    GUI.color = Color.white;
                }
            }

            // ── Sail command panel ──────────────────────────────────────────
            if (selectedSail != null)
            {
                var selectedGroup = manager.SelectedGroup;

                GUILayout.Space(4);
                GUILayout.Label($"Commands: {selectedSail.getSailName()}");

                if (!_renamingSail)
                {
                    if (GUILayout.Button("Rename", GUILayout.Width(80)))
                    {
                        _renamingSail = true;
                        renameBuffer  = selectedSail.FriendlyName ?? "";
                    }
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    renameBuffer = GUILayout.TextField(renameBuffer);
                    if (GUILayout.Button("Set", GUILayout.Width(46)) && renameBuffer.Trim().Length > 0)
                    {
                        manager.SetSailFriendlyName(selectedSail, renameBuffer.Trim());
                        _renamingSail = false;
                    }
                    GUILayout.EndHorizontal();
                }

                if (selectedGroup != null && !selectedGroup.IsAllSails)
                {
                    string btnLabel = selectedGroup.Contains(selectedSail)
                        ? $"Remove from {selectedGroup.Name}"
                        : $"Add to {selectedGroup.Name}";
                    if (GUILayout.Button(btnLabel))
                    {
                        if (selectedGroup.Contains(selectedSail)) selectedGroup.RemoveSail(selectedSail);
                        else                                      selectedGroup.AddSail(selectedSail);
                    }
                }

                GUILayout.Label("Halyard:");
                GUILayout.BeginHorizontal();
                DrawHalyardButton(manager, "Reef", selectedSail, 0.00f);
                DrawHalyardButton(manager, "1/4",  selectedSail, 0.25f);
                DrawHalyardButton(manager, "1/2",  selectedSail, 0.50f);
                DrawHalyardButton(manager, "3/4",  selectedSail, 0.75f);
                DrawHalyardButton(manager, "Full", selectedSail, 1.00f);
                GUILayout.EndHorizontal();

                GUILayout.Label("Sheet:");
                if (selectedSail is SimpleSail simpleSail)
                {
                    GUILayout.BeginHorizontal();
                    DrawSimpleSheetButton(manager, "Hard",    simpleSail, 0.00f);
                    DrawSimpleSheetButton(manager, "1/4",     simpleSail, 0.25f);
                    DrawSimpleSheetButton(manager, "1/2",     simpleSail, 0.50f);
                    DrawSimpleSheetButton(manager, "3/4",     simpleSail, 0.75f);
                    DrawSimpleSheetButton(manager, "Let Fly", simpleSail, 1.00f);
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    DrawRelativeSheetButton(manager, "Harden Up", simpleSail, -0.10f);
                    DrawRelativeSheetButton(manager, "Ease Out",  simpleSail,  0.10f);
                    DrawTrimButton(manager, "Trim", simpleSail);
                    GUILayout.EndHorizontal();
                }
                else if (selectedSail is DualSheetSail dualSail)
                {
                    GUILayout.BeginHorizontal();
                    if (dualSail.getSubtype() == DualSheetSail.DualSheetSailSubtype.Square)
                    {
                        DrawDualSheetButton(manager, "Full Port", dualSail, 0.00f, 1.00f);
                        DrawDualSheetButton(manager, "1/2 Port",  dualSail, 0.25f, 0.75f);
                        DrawDualSheetButton(manager, "Ahead",     dualSail, 0.50f, 0.50f);
                        DrawDualSheetButton(manager, "1/2 Stbd",  dualSail, 0.75f, 0.25f);
                        DrawDualSheetButton(manager, "Full Stbd", dualSail, 1.00f, 0.00f);
                    }
                    else // Jib
                    {
                        DrawDualSheetButton(manager, "Full Port", dualSail, 0.00f, 1.00f);
                        DrawDualSheetButton(manager, "3/4 Port",  dualSail, 0.25f, 1.00f);
                        DrawDualSheetButton(manager, "1/2 Port",  dualSail, 0.50f, 1.00f);
                        DrawDualSheetButton(manager, "1/4 Port",  dualSail, 0.75f, 1.00f);
                        GUILayout.EndHorizontal();
                        GUILayout.BeginHorizontal();
                        DrawDualSheetButton(manager, "Let Fly",   dualSail, 1.00f, 1.00f);
                        GUILayout.EndHorizontal();
                        GUILayout.BeginHorizontal();
                        DrawDualSheetButton(manager, "Full Stbd", dualSail, 1.00f, 0.00f);
                        DrawDualSheetButton(manager, "3/4 Stbd",  dualSail, 1.00f, 0.25f);
                        DrawDualSheetButton(manager, "1/2 Stbd",  dualSail, 1.00f, 0.50f);
                        DrawDualSheetButton(manager, "1/4 Stbd",  dualSail, 1.00f, 0.75f);
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    if (dualSail.getSubtype() == DualSheetSail.DualSheetSailSubtype.Jib)
                        DrawJibTrimButton(manager, "Trim", dualSail);
                    else
                        DrawSquareTrimButton(manager, "Trim", dualSail);
                    GUILayout.EndHorizontal();
                }
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        // ── Per-sail button helpers ──────────────────────────────────────────

        private void DrawHalyardButton(VirtualCrewManager manager, string label,
                                       ICommonSailActions sail, float target)
        {
            var winch = sail.getHalyardWinch();
            GUI.enabled = !IsWinchPending(manager, winch);
            if (GUILayout.Button(label))
                manager.AddWorkRequest(new WorkRequest(sail, "Halyard " + label, new WinchTarget(winch, target)));
            GUI.enabled = true;
        }

        private void DrawSimpleSheetButton(VirtualCrewManager manager, string label,
                                           SimpleSail sail, float target)
        {
            var winch = sail.getSheetWinch();
            GUI.enabled = !IsWinchPending(manager, winch);
            if (GUILayout.Button(label))
                manager.AddWorkRequest(new WorkRequest(sail, "Sheet " + label, new WinchTarget(winch, target)));
            GUI.enabled = true;
        }

        private void DrawRelativeSheetButton(VirtualCrewManager manager, string label,
                                             SimpleSail sail, float delta)
        {
            var winch = sail.getSheetWinch();
            GUI.enabled = !IsWinchPending(manager, winch);
            if (GUILayout.Button(label))
            {
                float target = Mathf.Clamp01(winch.rope.currentLength + delta);
                manager.AddWorkRequest(new WorkRequest(sail, "Sheet " + label, new WinchTarget(winch, target)));
            }
            GUI.enabled = true;
        }

        private void DrawSquareTrimButton(VirtualCrewManager manager, string label, DualSheetSail sail)
        {
            var port = sail.getPortSheetWinch();
            var star = sail.getStarboardSheetWinch();
            GUI.enabled = !IsWinchPending(manager, port) && !IsWinchPending(manager, star);
            if (GUILayout.Button(label))
                manager.AddSquareTrimRequest(new SquareTrimRequest(sail));
            GUI.enabled = true;
        }

        private void DrawJibTrimButton(VirtualCrewManager manager, string label, DualSheetSail sail)
        {
            var port = sail.getPortSheetWinch();
            var star = sail.getStarboardSheetWinch();
            GUI.enabled = !IsWinchPending(manager, port) && !IsWinchPending(manager, star);
            if (GUILayout.Button(label))
                manager.AddJibTrimRequest(new JibTrimRequest(sail));
            GUI.enabled = true;
        }

        private void DrawTrimButton(VirtualCrewManager manager, string label, SimpleSail sail)
        {
            var winch = sail.getSheetWinch();
            GUI.enabled = !IsWinchPending(manager, winch);
            if (GUILayout.Button(label))
                manager.AddTrimRequest(new TrimRequest(sail));
            GUI.enabled = true;
        }

        private void DrawMooringButton(VirtualCrewManager manager, string label, MooringSide side)
        {
            GUI.enabled = manager.CanAddMooringRequest(side);
            if (GUILayout.Button(label))
                manager.AddMooringRequests(side);
            GUI.enabled = true;
        }

        private void DrawDualSheetButton(VirtualCrewManager manager, string label,
                                         DualSheetSail sail, float portTarget, float starboardTarget)
        {
            var port = sail.getPortSheetWinch();
            var star = sail.getStarboardSheetWinch();
            GUI.enabled = !IsWinchPending(manager, port) && !IsWinchPending(manager, star);
            if (GUILayout.Button(label))
            {
                manager.AddWorkRequest(new WorkRequest(sail, "Port Sheet " + label,
                    new WinchTarget(port, portTarget)));
                manager.AddWorkRequest(new WorkRequest(sail, "Starboard Sheet " + label,
                    new WinchTarget(star, starboardTarget)));
            }
            GUI.enabled = true;
        }

        private bool IsWinchPending(VirtualCrewManager manager, GPButtonRopeWinch winch)
        {
            if (!winch)
                return false;

            bool pending;
            if (_pendingWinchCache.TryGetValue(winch, out pending))
                return pending;

            pending = manager.HasPendingRequestForWinch(winch);
            _pendingWinchCache[winch] = pending;
            return pending;
        }
    }
}
