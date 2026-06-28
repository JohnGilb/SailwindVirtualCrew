using UnityEngine;

namespace SailwindVirtualCrew
{
    public class SkullingWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(880, 340, 260, 260);
        private static readonly int windowId = "VirtualCrewSkullingWindow".GetHashCode();

        private WindowResizer _resizer;
        private string lastMessage = "";

        private const float ButtonWidth = 76f;
        private const float ButtonHeight = 28f;

        public string WindowKey => "SkullingWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 880f, 340f, 0f };
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

            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : 260f;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Skulling");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            GUILayout.Space(4);
            DrawStatus();
            GUILayout.Space(6);

            GUILayout.BeginHorizontal();
            GUILayout.Space(ButtonWidth + 4f);
            DrawCommandButton("Ahead", SkullingCommand.Ahead);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            DrawCommandButton("Port", SkullingCommand.Port);
            DrawCommandButton("Stop", SkullingCommand.Stop);
            DrawCommandButton("Stbd", SkullingCommand.Starboard);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Space(ButtonWidth + 4f);
            DrawCommandButton("Aback", SkullingCommand.Aback);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            DrawCommandButton("Turn Port", SkullingCommand.TurnPort);
            DrawCommandButton("Turn Stbd", SkullingCommand.TurnStarboard);
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(lastMessage))
            {
                GUILayout.Space(6);
                GUILayout.Label(lastMessage);
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void DrawStatus()
        {
            var request = VirtualCrewManager.Instance.ActiveSkullingRequest;
            int oars = LocatorUtils.FindOarsOnCurrentVessel().Count;
            if (request == null)
            {
                GUILayout.Label("Idle");
                GUILayout.Label("Oars: " + oars);
                return;
            }

            string state = request.IsWaiting
                ? "Waiting"
                : request.Status == WorkRequestStatus.Positioning
                    ? "Moving"
                    : request.IsForceBurstActive ? "Rowing" : "Recovering";

            GUILayout.Label(state + ": " + SkullingRequest.GetCommandLabel(request.Command));
            GUILayout.Label("Crew: " + request.AssignedCrewCount + "  Stations: " + request.ActiveStationCount + "  Oars: " + oars);
            if (!string.IsNullOrEmpty(request.StatusMessage))
                GUILayout.Label(request.StatusMessage);
        }

        private void DrawCommandButton(string label, SkullingCommand command)
        {
            if (GUILayout.Button(label, GUILayout.Width(ButtonWidth), GUILayout.Height(ButtonHeight)))
            {
                string reason;
                if (VirtualCrewManager.Instance.StartSkulling(command, out reason))
                    lastMessage = reason;
                else
                    lastMessage = reason;
            }
        }
    }
}
