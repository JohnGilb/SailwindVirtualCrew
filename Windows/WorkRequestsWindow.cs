using UnityEngine;

namespace SailwindVirtualCrew
{
    public class WorkRequestsWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(20, 580, 560, 560);
        private static readonly int windowId = "VirtualCrewWorkRequestsWindow".GetHashCode();

        private WindowResizer _resizer;

        public string WindowKey => "WorkRequestsWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 20f, 580f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private Texture2D fillTexture;
        private Texture2D positioningTexture;

        private const float ButtonHeight           = 28f;
        private const float BaseContentHeight      = 300f;
        private const float OpenTaskHeight         = 28f;
        private const float InProgressTaskHeight   = 46f;
        private const float RepositioningTaskHeight = 60f;

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            var manager            = VirtualCrewManager.Instance;
            var requests           = manager.WorkRequests;
            var trimRequests       = manager.TrimRequests;
            var jibTrimRequests    = manager.JibTrimRequests;
            var squareTrimRequests = manager.SquareTrimRequests;
            var navigateRequests   = manager.NavigateRequests;
            var mooringRequests    = manager.MooringRequests;
            var haulSellRequests   = manager.HaulSellRequests;
            var bailRequests       = manager.BailRequests;
            var sleepRequests      = manager.SleepRequests;
            var pilotTask          = manager.ActivePilotTask;
            var lookoutTask        = manager.ActiveLookoutTask;

            int totalTasks = requests.Count + trimRequests.Count
                           + jibTrimRequests.Count + squareTrimRequests.Count
                           + navigateRequests.Count + mooringRequests.Count + haulSellRequests.Count + bailRequests.Count + sleepRequests.Count
                           + (pilotTask   != null ? 1 : 0)
                           + (lookoutTask != null ? 1 : 0);

            float taskListHeight;
            if (totalTasks == 0)
            {
                taskListHeight = ButtonHeight;
            }
            else
            {
                taskListHeight = 0f;
                foreach (var r in requests)
                    taskListHeight += (r.Status == WorkRequestStatus.InProgress || r.Status == WorkRequestStatus.Positioning)
                        ? InProgressTaskHeight : OpenTaskHeight;
                foreach (var r in trimRequests)
                    taskListHeight += (r.Status == WorkRequestStatus.InProgress || r.Status == WorkRequestStatus.Positioning)
                        ? InProgressTaskHeight : OpenTaskHeight;
                foreach (var r in jibTrimRequests)
                    taskListHeight += r.Status == WorkRequestStatus.Positioning ? InProgressTaskHeight
                                    : r.Status == WorkRequestStatus.InProgress && r.IsRepositioning ? RepositioningTaskHeight
                                    : r.Status == WorkRequestStatus.InProgress ? InProgressTaskHeight
                                    : OpenTaskHeight;
                foreach (var r in squareTrimRequests)
                    taskListHeight += (r.Status == WorkRequestStatus.InProgress || r.Status == WorkRequestStatus.Positioning)
                        ? InProgressTaskHeight : OpenTaskHeight;
                foreach (var r in navigateRequests)
                    taskListHeight += r.Status == WorkRequestStatus.InProgress
                        ? InProgressTaskHeight : OpenTaskHeight;
                foreach (var r in mooringRequests)
                    taskListHeight += (r.Status == WorkRequestStatus.InProgress || r.Status == WorkRequestStatus.Positioning)
                        ? InProgressTaskHeight : OpenTaskHeight;
                foreach (var r in haulSellRequests)
                    taskListHeight += (r.Status == WorkRequestStatus.InProgress || r.Status == WorkRequestStatus.Positioning)
                        ? InProgressTaskHeight : OpenTaskHeight;
                foreach (var r in bailRequests)
                    taskListHeight += r.Status == WorkRequestStatus.InProgress
                        ? InProgressTaskHeight : OpenTaskHeight;
                foreach (var r in sleepRequests)
                    taskListHeight += (r.Status == WorkRequestStatus.InProgress || r.Status == WorkRequestStatus.Positioning)
                        ? InProgressTaskHeight : OpenTaskHeight;
                if (pilotTask   != null) taskListHeight += OpenTaskHeight;
                if (lookoutTask != null) taskListHeight += OpenTaskHeight;
            }

            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : BaseContentHeight + ButtonHeight + taskListHeight;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Work Requests");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            var manager            = VirtualCrewManager.Instance;
            var requests           = manager.WorkRequests;
            var trimRequests       = manager.TrimRequests;
            var jibTrimRequests    = manager.JibTrimRequests;
            var squareTrimRequests = manager.SquareTrimRequests;
            var navigateRequests   = manager.NavigateRequests;
            var mooringRequests    = manager.MooringRequests;
            var haulSellRequests   = manager.HaulSellRequests;
            var bailRequests       = manager.BailRequests;
            var sleepRequests      = manager.SleepRequests;
            var pilotTask          = manager.ActivePilotTask;
            var lookoutTask        = manager.ActiveLookoutTask;

            GUILayout.Label("Tasks");

            if (requests.Count == 0 && trimRequests.Count == 0
             && jibTrimRequests.Count == 0 && squareTrimRequests.Count == 0
             && navigateRequests.Count == 0 && mooringRequests.Count == 0 && haulSellRequests.Count == 0 && bailRequests.Count == 0 && sleepRequests.Count == 0
             && pilotTask == null && lookoutTask == null)
            {
                GUILayout.Label("No tasks queued.");
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            WorkRequest toCancel = null;
            foreach (var req in requests)
            {
                if (req.Status == WorkRequestStatus.Open)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[Waiting] {req.DisplayLabel}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) toCancel = req;
                    GUILayout.EndHorizontal();
                }
                else if (req.Status == WorkRequestStatus.Positioning)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{req.AssignedCrewman.Name}] (moving) {req.DisplayLabel}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) toCancel = req;
                    GUILayout.EndHorizontal();
                    DrawPositioningBar(req.GetPositioningProgress());
                }
                else if (req.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{req.AssignedCrewman.Name}] {req.DisplayLabel}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) toCancel = req;
                    GUILayout.EndHorizontal();
                    DrawProgressBar(req.GetProgress());
                }
            }
            if (toCancel != null) manager.CancelWorkRequest(toCancel);

            TrimRequest trimToCancel = null;
            foreach (var trim in trimRequests)
            {
                if (trim.Status == WorkRequestStatus.Open)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[Waiting] {trim.CommandName} — {trim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) trimToCancel = trim;
                    GUILayout.EndHorizontal();
                }
                else if (trim.Status == WorkRequestStatus.Positioning)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{trim.AssignedCrewman.Name}] (moving) {trim.CommandName} — {trim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) trimToCancel = trim;
                    GUILayout.EndHorizontal();
                    DrawPositioningBar(trim.GetPositioningProgress());
                }
                else if (trim.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{trim.AssignedCrewman.Name}] {trim.CommandName} — {trim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) trimToCancel = trim;
                    GUILayout.EndHorizontal();
                    DrawProgressBar(trim.GetProgress());
                }
            }
            if (trimToCancel != null) manager.CancelTrimRequest(trimToCancel);

            JibTrimRequest jibToCancel = null;
            foreach (var jtrim in jibTrimRequests)
            {
                if (jtrim.Status == WorkRequestStatus.Open)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[Waiting] {jtrim.CommandName} — {jtrim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) jibToCancel = jtrim;
                    GUILayout.EndHorizontal();
                }
                else if (jtrim.Status == WorkRequestStatus.Positioning)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{jtrim.AssignedCrewman.Name}] (moving) {jtrim.CommandName} — {jtrim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) jibToCancel = jtrim;
                    GUILayout.EndHorizontal();
                    DrawPositioningBar(jtrim.GetPositioningProgress());
                }
                else if (jtrim.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{jtrim.AssignedCrewman.Name}] {jtrim.CommandName} — {jtrim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) jibToCancel = jtrim;
                    GUILayout.EndHorizontal();
                    DrawProgressBar(jtrim.GetProgress());
                    if (jtrim.IsRepositioning) DrawPositioningBar(jtrim.GetRepositioningProgress());
                }
            }
            if (jibToCancel != null) manager.CancelJibTrimRequest(jibToCancel);

            SquareTrimRequest squareToCancel = null;
            foreach (var strim in squareTrimRequests)
            {
                if (strim.Status == WorkRequestStatus.Open)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[Need 2 crew] {strim.CommandName} — {strim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) squareToCancel = strim;
                    GUILayout.EndHorizontal();
                }
                else if (strim.Status == WorkRequestStatus.Positioning)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{strim.AssignedCrewman.Name}, {strim.AssignedCrewman2.Name}] (moving) {strim.CommandName} — {strim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) squareToCancel = strim;
                    GUILayout.EndHorizontal();
                    DrawPositioningBar(strim.GetPositioningProgress());
                }
                else if (strim.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{strim.AssignedCrewman.Name}, {strim.AssignedCrewman2.Name}] {strim.CommandName} — {strim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(28))) squareToCancel = strim;
                    GUILayout.EndHorizontal();
                    DrawProgressBar(strim.GetProgress());
                }
            }
            if (squareToCancel != null) manager.CancelSquareTrimRequest(squareToCancel);

            NavigateRequest navToCancel = null;
            foreach (var nav in navigateRequests)
            {
                if (nav.Status == WorkRequestStatus.Open)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("[Waiting] Plotting Vessel");
                    if (GUILayout.Button("X", GUILayout.Width(28))) navToCancel = nav;
                    GUILayout.EndHorizontal();
                }
                else if (nav.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{nav.Navigator.Name}] Plotting Vessel");
                    if (GUILayout.Button("X", GUILayout.Width(28))) navToCancel = nav;
                    GUILayout.EndHorizontal();
                    DrawProgressBar(nav.GetProgress());
                }
            }
            if (navToCancel != null) manager.CancelNavigateRequest(navToCancel);

            MooringRequest mooringToCancel = null;
            foreach (var mooring in mooringRequests)
            {
                if (mooring.Status == WorkRequestStatus.Open)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("[Waiting] " + mooring.CommandName);
                    if (GUILayout.Button("X", GUILayout.Width(28))) mooringToCancel = mooring;
                    GUILayout.EndHorizontal();
                }
                else if (mooring.Status == WorkRequestStatus.Positioning)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("[" + mooring.AssignedCrewman.Name + "] (moving) " + mooring.CommandName);
                    if (GUILayout.Button("X", GUILayout.Width(28))) mooringToCancel = mooring;
                    GUILayout.EndHorizontal();
                    DrawPositioningBar(mooring.GetPositioningProgress());
                }
                else if (mooring.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("[" + mooring.AssignedCrewman.Name + "] " + mooring.CommandName);
                    if (GUILayout.Button("X", GUILayout.Width(28))) mooringToCancel = mooring;
                    GUILayout.EndHorizontal();
                    DrawProgressBar(mooring.GetProgress());
                }
            }
            if (mooringToCancel != null) manager.CancelMooringRequest(mooringToCancel);

            HaulSellRequest haulToCancel = null;
            foreach (var haul in haulSellRequests)
            {
                if (haul.Status == WorkRequestStatus.Open)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("[Waiting] Haul & Sell " + haul.ItemName);
                    if (GUILayout.Button("X", GUILayout.Width(28))) haulToCancel = haul;
                    GUILayout.EndHorizontal();
                }
                else if (haul.Status == WorkRequestStatus.Positioning)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("[" + haul.AssignedCrewman.Name + "] (moving) Haul & Sell " + haul.ItemName);
                    if (GUILayout.Button("X", GUILayout.Width(28))) haulToCancel = haul;
                    GUILayout.EndHorizontal();
                    DrawPositioningBar(haul.GetPositioningProgress());
                }
                else if (haul.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("[" + haul.AssignedCrewman.Name + "] "
                        + (haul.IsReturningCanceledCargo ? "Returning canceled cargo"
                            : haul.IsReturning ? "Returning from sale" : "Haul & Sell " + haul.ItemName));
                    if (GUILayout.Button("X", GUILayout.Width(28))) haulToCancel = haul;
                    GUILayout.EndHorizontal();
                    DrawProgressBar(haul.GetProgress());
                }
            }
            if (haulToCancel != null) manager.CancelHaulSellRequest(haulToCancel);

            BailRequest bailToCancel = null;
            foreach (var bail in bailRequests)
            {
                if (bail.Status == WorkRequestStatus.Open)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("[Waiting] Bail Until Empty (" + bail.ToolName + ")");
                    if (GUILayout.Button("X", GUILayout.Width(28))) bailToCancel = bail;
                    GUILayout.EndHorizontal();
                }
                else if (bail.Status == WorkRequestStatus.InProgress)
                {
                    string phase = bail.IsPickingUp ? "scooping" : "dumping";
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("[" + bail.AssignedCrewman.Name + "] Bailing, " + phase + " (" + bail.ToolName + ")");
                    if (GUILayout.Button("X", GUILayout.Width(28))) bailToCancel = bail;
                    GUILayout.EndHorizontal();
                    DrawProgressBar(bail.GetProgress());
                }
            }
            if (bailToCancel != null) manager.CancelBailRequest(bailToCancel);

            SleepRequest sleepToCancel = null;
            foreach (var sleep in sleepRequests)
            {
                if (sleep.Status == WorkRequestStatus.Open)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{sleep.AssignedCrewman.Name}] Waiting for bed");
                    if (GUILayout.Button("X", GUILayout.Width(28))) sleepToCancel = sleep;
                    GUILayout.EndHorizontal();
                }
                else if (sleep.Status == WorkRequestStatus.Positioning)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{sleep.AssignedCrewman.Name}] Walking to bed");
                    if (GUILayout.Button("X", GUILayout.Width(28))) sleepToCancel = sleep;
                    GUILayout.EndHorizontal();
                    DrawPositioningBar(sleep.GetPositioningProgress());
                }
                else if (sleep.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{sleep.AssignedCrewman.Name}] Sleeping");
                    if (GUILayout.Button("X", GUILayout.Width(28))) sleepToCancel = sleep;
                    GUILayout.EndHorizontal();
                    DrawProgressBar(sleep.GetProgress());
                }
            }
            if (sleepToCancel != null) manager.CancelSleepRequest(sleepToCancel);

            if (pilotTask != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"[{pilotTask.AssignedCrewman.Name}] On Pilot Duty");
                if (GUILayout.Button("X", GUILayout.Width(28))) manager.StopPilot();
                GUILayout.EndHorizontal();
            }

            if (lookoutTask != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"[{lookoutTask.AssignedCrewman.Name}] On Watch");
                if (GUILayout.Button("X", GUILayout.Width(28))) manager.StopLookout();
                GUILayout.EndHorizontal();
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        // ── Progress bars ────────────────────────────────────────────────────

        private void DrawProgressBar(float progress)
        {
            if (fillTexture == null)
            {
                fillTexture = new Texture2D(1, 1);
                fillTexture.SetPixel(0, 0, Color.green);
                fillTexture.Apply();
            }
            DrawBar(progress, fillTexture);
        }

        private void DrawPositioningBar(float progress)
        {
            if (positioningTexture == null)
            {
                positioningTexture = new Texture2D(1, 1);
                positioningTexture.SetPixel(0, 0, Color.blue);
                positioningTexture.Apply();
            }
            DrawBar(progress, positioningTexture);
        }

        private void DrawBar(float progress, Texture2D tex)
        {
            Rect bar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
            GUI.Box(bar, "");
            float fillWidth = (bar.width - 4) * Mathf.Clamp01(progress / 100f);
            if (fillWidth > 0f)
                GUI.DrawTexture(new Rect(bar.x + 2, bar.y + 2, fillWidth, bar.height - 4), tex);
        }
    }
}
