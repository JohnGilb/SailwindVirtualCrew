using UnityEngine;

namespace SailwindVirtualCrew
{
    public class CrewRosterWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(760, 20, 500, 400);
        private static readonly int windowId = "VirtualCrewRosterWindow".GetHashCode();

        private WindowResizer _resizer;
        private GUIStyle _leftButtonStyle;
        private GUIStyle _wrappedLabelStyle;
        private bool _stylesDarkMode;

        public string WindowKey => "CrewRosterWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 760f, 20f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private Crewman selectedShipCrew  = null;
        private Crewman selectedAvailable = null;
        private string  crewRenameBuffer  = "";
        private bool    _renamingShipCrew = false;

        private int? bedCount = null;

        private const float RowHeight  = 28f;
        private const float StatHeight = 24f;

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();
            EnsureStyles();
            var mgr = VirtualCrewManager.Instance;

            windowRect.width = Mathf.Max(windowRect.width, 500f);

            float h = RowHeight * 4f                     // pay totals + "On Ship:" label
                    + mgr.Crew.Count * RowHeight
                    + (selectedShipCrew  != null ? StatHeight + RowHeight : 0f)
                    + 8f + RowHeight;                    // space + "Available at Port:" label

            var avail = mgr.AvailableAtPort;
            if (avail.Count == 0)
                h += RowHeight;
            else
            {
                h += avail.Count * RowHeight;
                if (selectedAvailable != null) h += StatHeight + RowHeight;
            }

            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : h + 400f;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Crew Roster");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            var mgr = VirtualCrewManager.Instance;

            // ── Capacity ────────────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            string bedLabel = bedCount.HasValue ? $"Beds: {bedCount}" : "Beds: ?";
            GUILayout.Label($"{bedLabel}  |  Crew: {mgr.Crew.Count}");
            if (GUILayout.Button("Scan", GUILayout.Width(60)))
                bedCount = LocatorUtils.CountBeds();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            GUILayout.Label($"Pay - salary: {mgr.DailySalaryPay} Al'Ankh Lions (Daily)", _wrappedLabelStyle);
            GUILayout.Label($"Pay - shares: {mgr.CrewProfitSharePercent}% of ship profits", _wrappedLabelStyle);
            GUILayout.Space(4);

            // ── On Ship ─────────────────────────────────────────────────────
            GUILayout.Label("On Ship:");
            foreach (var c in mgr.Crew)
            {
                bool sel = c == selectedShipCrew;
                string fatigue = DeveloperMode.IsEnabled ? "" : $"  [{c.FatigueTag}]";
                string roleName = c.Role.DisplayName();
                string shiftTag = c.Shift.DisplayTag();
                string label = sel ? $"► {c.Name}  ({roleName}){shiftTag}{fatigue}" : $"  {c.Name}  ({roleName}){shiftTag}{fatigue}";
                if (GUILayout.Button(label, _leftButtonStyle))
                {
                    if (sel) { selectedShipCrew = null; crewRenameBuffer = ""; _renamingShipCrew = false; }
                    else     { selectedShipCrew = c;    crewRenameBuffer = c.Name; selectedAvailable = null; _renamingShipCrew = false; }
                }
            }
            if (selectedShipCrew != null)
            {
                GUILayout.Label(StatLine(selectedShipCrew));
                if (!_renamingShipCrew)
                {
                    if (GUILayout.Button("Rename", GUILayout.Width(80)))
                    {
                        _renamingShipCrew = true;
                        crewRenameBuffer = selectedShipCrew.Name;
                    }
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    crewRenameBuffer = GUILayout.TextField(crewRenameBuffer, GUILayout.MinWidth(120));
                    if (GUILayout.Button("Set", GUILayout.Width(64)) && crewRenameBuffer.Trim().Length > 0)
                    {
                        selectedShipCrew.Rename(crewRenameBuffer.Trim());
                        _renamingShipCrew = false;
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                GUI.enabled = mgr.CanSetCrewShift(CrewShift.Day);
                if (GUILayout.Button("Set Day Shift"))
                    mgr.SetCrewShift(selectedShipCrew, CrewShift.Day);
                GUI.enabled = mgr.CanSetCrewShift(CrewShift.Night);
                if (GUILayout.Button("Set Night Shift"))
                    mgr.SetCrewShift(selectedShipCrew, CrewShift.Night);
                GUI.enabled = true;
                if (GUILayout.Button("Ad-Hoc"))
                    mgr.SetCrewShift(selectedShipCrew, CrewShift.AdHoc);
                GUILayout.EndHorizontal();
                GUI.enabled = !selectedShipCrew.IsOccupied;
                if (GUILayout.Button("Sleep"))
                    mgr.AddSleepRequest(selectedShipCrew);
                GUI.enabled = true;
                if (selectedShipCrew.Role == ShipRole.Pilot)
                {
                    GUI.enabled = !selectedShipCrew.IsOccupied;
                    if (GUILayout.Button("Start Piloting"))
                        mgr.StartPilot(selectedShipCrew);
                    GUI.enabled = true;
                }
                if (selectedShipCrew.Role == ShipRole.Navigator)
                {
                    if (GUILayout.Button("Assign as Navigator"))
                        mgr.AssignNavigator(selectedShipCrew);
                }
                if (GUILayout.Button("Set Rest Location"))
                    SetRestLocation(mgr, selectedShipCrew);
                if (mgr.CurrentPort != null && GUILayout.Button($"Fire {selectedShipCrew.Name}"))
                {
                    mgr.FireCrew(selectedShipCrew);
                    selectedShipCrew = null;
                    crewRenameBuffer = "";
                    _renamingShipCrew = false;
                }
                else if (DeveloperMode.IsEnabled && GUILayout.Button($"Remove {selectedShipCrew.Name}"))
                {
                    bool wasFirstOfficer = selectedShipCrew.Role == ShipRole.ChiefOfficer;
                    selectedShipCrew.CurrentTask = null;
                    mgr.Crew.Remove(selectedShipCrew);
                    if (wasFirstOfficer && !mgr.HasFirstOfficer)
                        foreach (var crewman in mgr.Crew)
                            mgr.SetCrewShift(crewman, CrewShift.AdHoc);
                    selectedShipCrew = null;
                    crewRenameBuffer = "";
                    _renamingShipCrew = false;
                }
            }

            // ── Available at Port ────────────────────────────────────────────
            GUILayout.Space(8);
            if (mgr.CurrentPort != null && !mgr.ValidateCurrentPortDudeForHiring(out _))
                selectedAvailable = null;
            var avail = mgr.AvailableAtPort;
            string availableAtPortLabel;
            if (mgr.CurrentPort == null)
                availableAtPortLabel = "Visit a Port Trader to hire crew";
            else if (avail.Count == 0)
                availableAtPortLabel = $"Crew Available Here: 0. Refresh in {mgr.GetPortCrewRefreshDaysRemaining()} days.";
            else
                availableAtPortLabel = $"Crew Available Here: {avail.Count}";
            GUILayout.Label(availableAtPortLabel);
            if (avail.Count > 0)
            {
                foreach (var c in avail)
                {
                    bool sel = c == selectedAvailable;
                    string roleName = c.Role.DisplayName();
                    string label = sel ? $"► {c.Name}  ({roleName})" : $"  {c.Name}  ({roleName})";
                    if (GUILayout.Button(label, _leftButtonStyle))
                    {
                        selectedAvailable = sel ? null : c;
                        selectedShipCrew  = null;
                        crewRenameBuffer  = "";
                        _renamingShipCrew = false;
                    }
                }
                if (selectedAvailable != null)
                {
                    GUILayout.Label(StatLine(selectedAvailable));
                    bool canHire = mgr.CanHireCrew(selectedAvailable, out string hireReason);
                    GUI.enabled = canHire;
                    if (GUILayout.Button($"Hire {selectedAvailable.Name}"))
                    {
                        mgr.HireCrew(selectedAvailable);
                        selectedAvailable = null;
                    }
                    GUI.enabled = true;
                    if (!canHire)
                        GUILayout.Label(hireReason);
                }
            }
            else
            {
                selectedAvailable = null;
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void EnsureStyles()
        {
            if (_leftButtonStyle != null && !SailwindGuiStyle.HasThemeChanged(_stylesDarkMode))
                return;

            _stylesDarkMode = SailwindGuiStyle.IsDarkMode;
            _leftButtonStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            _wrappedLabelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                clipping = TextClipping.Clip
            };
        }

        private static string StatLine(Crewman c)
        {
            if (DeveloperMode.IsEnabled)
                return $"{c.TrueStatLine()}    Stamina: {c.CurrentStamina:F1}/{c.MaxStamina}    Model: {c.ModelIndex}";
            return c.AdvertisedStatLine();
        }

        private static void SetRestLocation(VirtualCrewManager mgr, Crewman crewman)
        {
            var context = CrewBoatContextResolver.ResolveAndLog();
            if (context == null || Refs.observerMirror == null)
                return;

            var mapper = new CrewSpaceMapper(context);
            var playerTransform = Refs.observerMirror.transform;
            Vector3 localPosition = mapper.WorldBoatLocalFromWorld(playerTransform.position);
            Quaternion localRotation = mapper.WorldBoatLocalRotationFromWorld(playerTransform.rotation);
            if (CrewNavigationCoordinator.Instance.TryProjectLocalToNavMesh(localPosition, out var projectedLocalPosition))
                localPosition = projectedLocalPosition;
            else
                CrewDebugLog.Warn("RuntimeNav", "Rest location could not be sampled onto the NavMesh crew='" + crewman.Name + "'");

            mgr.SetCrewRestLocation(crewman, localPosition, localRotation);
            CrewNavigationCoordinator.Instance.OnRestLocationChanged(crewman);
            CrewDebugLog.Ok("RuntimeNav", "Set rest location crew='" + crewman.Name + "'");
        }
    }
}
