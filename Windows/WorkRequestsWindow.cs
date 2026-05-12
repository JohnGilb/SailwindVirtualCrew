using UnityEngine;

namespace SailwindVirtualCrew
{
    public class WorkRequestsWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(20, 580, 500, 560);
        private static readonly int windowId = "VirtualCrewWorkRequestsWindow".GetHashCode();

        public string WindowKey => "WorkRequestsWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y };
        public void SetPosition(float x, float y) { windowRect.x = x; windowRect.y = y; }

        private Texture2D fillTexture;
        private Texture2D positioningTexture;

        private const float ButtonHeight           = 22f;
        private const float BaseContentHeight      = 300f;
        private const float OpenTaskHeight         = 22f;
        private const float InProgressTaskHeight   = 40f;
        private const float RepositioningTaskHeight = 54f;

        private void Update()
        {
            if (Plugin.ToggleCrewWindow.Value.IsDown())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;

            var manager            = VirtualCrewManager.Instance;
            var requests           = manager.WorkRequests;
            var trimRequests       = manager.TrimRequests;
            var jibTrimRequests    = manager.JibTrimRequests;
            var squareTrimRequests = manager.SquareTrimRequests;
            var navigateRequests   = manager.NavigateRequests;
            var sleepRequests      = manager.SleepRequests;

            int totalTasks = requests.Count + trimRequests.Count
                           + jibTrimRequests.Count + squareTrimRequests.Count
                           + navigateRequests.Count + sleepRequests.Count;

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
                foreach (var r in sleepRequests)
                    taskListHeight += r.Status == WorkRequestStatus.InProgress
                        ? InProgressTaskHeight : OpenTaskHeight;
            }

            windowRect.height = BaseContentHeight + ButtonHeight + taskListHeight; // ButtonHeight for "Tasks" label
            windowRect = GUI.Window(windowId, windowRect, DrawWindow, "Work Requests");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            var manager            = VirtualCrewManager.Instance;
            var requests           = manager.WorkRequests;
            var trimRequests       = manager.TrimRequests;
            var jibTrimRequests    = manager.JibTrimRequests;
            var squareTrimRequests = manager.SquareTrimRequests;
            var navigateRequests   = manager.NavigateRequests;
            var sleepRequests      = manager.SleepRequests;

            GUILayout.Label("Tasks");

            if (requests.Count == 0 && trimRequests.Count == 0
             && jibTrimRequests.Count == 0 && squareTrimRequests.Count == 0
             && navigateRequests.Count == 0 && sleepRequests.Count == 0)
            {
                GUILayout.Label("No tasks queued.");
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
                    if (GUILayout.Button("X", GUILayout.Width(22))) toCancel = req;
                    GUILayout.EndHorizontal();
                }
                else if (req.Status == WorkRequestStatus.Positioning)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{req.AssignedCrewman.Name}] (moving) {req.DisplayLabel}");
                    if (GUILayout.Button("X", GUILayout.Width(22))) toCancel = req;
                    GUILayout.EndHorizontal();
                    DrawPositioningBar(req.GetPositioningProgress());
                }
                else if (req.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{req.AssignedCrewman.Name}] {req.DisplayLabel}");
                    if (GUILayout.Button("X", GUILayout.Width(22))) toCancel = req;
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
                    if (GUILayout.Button("X", GUILayout.Width(22))) trimToCancel = trim;
                    GUILayout.EndHorizontal();
                }
                else if (trim.Status == WorkRequestStatus.Positioning)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{trim.AssignedCrewman.Name}] (moving) {trim.CommandName} — {trim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(22))) trimToCancel = trim;
                    GUILayout.EndHorizontal();
                    DrawPositioningBar(trim.GetPositioningProgress());
                }
                else if (trim.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{trim.AssignedCrewman.Name}] {trim.CommandName} — {trim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(22))) trimToCancel = trim;
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
                    if (GUILayout.Button("X", GUILayout.Width(22))) jibToCancel = jtrim;
                    GUILayout.EndHorizontal();
                }
                else if (jtrim.Status == WorkRequestStatus.Positioning)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{jtrim.AssignedCrewman.Name}] (moving) {jtrim.CommandName} — {jtrim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(22))) jibToCancel = jtrim;
                    GUILayout.EndHorizontal();
                    DrawPositioningBar(jtrim.GetPositioningProgress());
                }
                else if (jtrim.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{jtrim.AssignedCrewman.Name}] {jtrim.CommandName} — {jtrim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(22))) jibToCancel = jtrim;
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
                    if (GUILayout.Button("X", GUILayout.Width(22))) squareToCancel = strim;
                    GUILayout.EndHorizontal();
                }
                else if (strim.Status == WorkRequestStatus.Positioning)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{strim.AssignedCrewman.Name}, {strim.AssignedCrewman2.Name}] (moving) {strim.CommandName} — {strim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(22))) squareToCancel = strim;
                    GUILayout.EndHorizontal();
                    DrawPositioningBar(strim.GetPositioningProgress());
                }
                else if (strim.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{strim.AssignedCrewman.Name}, {strim.AssignedCrewman2.Name}] {strim.CommandName} — {strim.Sail.getSailName()}");
                    if (GUILayout.Button("X", GUILayout.Width(22))) squareToCancel = strim;
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
                    if (GUILayout.Button("X", GUILayout.Width(22))) navToCancel = nav;
                    GUILayout.EndHorizontal();
                }
                else if (nav.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{nav.Navigator.Name}] Plotting Vessel");
                    if (GUILayout.Button("X", GUILayout.Width(22))) navToCancel = nav;
                    GUILayout.EndHorizontal();
                    DrawProgressBar(nav.GetProgress());
                }
            }
            if (navToCancel != null) manager.CancelNavigateRequest(navToCancel);

            SleepRequest sleepToCancel = null;
            foreach (var sleep in sleepRequests)
            {
                if (sleep.Status == WorkRequestStatus.Open)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{sleep.AssignedCrewman.Name}] Waiting for bed");
                    if (GUILayout.Button("X", GUILayout.Width(22))) sleepToCancel = sleep;
                    GUILayout.EndHorizontal();
                }
                else if (sleep.Status == WorkRequestStatus.InProgress)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"[{sleep.AssignedCrewman.Name}] Sleeping");
                    if (GUILayout.Button("X", GUILayout.Width(22))) sleepToCancel = sleep;
                    GUILayout.EndHorizontal();
                    DrawProgressBar(sleep.GetProgress());
                }
            }
            if (sleepToCancel != null) manager.CancelSleepRequest(sleepToCancel);

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
