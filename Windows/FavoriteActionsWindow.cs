using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class FavoriteActionsWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(20, 840, 420, 360);
        private static readonly int windowId = "VirtualCrewFavoriteActionsWindow".GetHashCode();

        private WindowResizer _resizer;
        private Vector2 _scroll;
        private string _captureActionId;

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

            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : 360f;
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

            if (!string.IsNullOrEmpty(_captureActionId))
                GUILayout.Label("Press a key. Esc cancels, Backspace clears.");

            if (actions.Count == 0)
            {
                GUILayout.Label("No favorite actions yet.");
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll);
            FavoriteAction remove = null;
            foreach (var action in actions)
                DrawFavoriteActionRow(mgr, action, ref remove);
            GUILayout.EndScrollView();

            if (remove != null)
            {
                if (_captureActionId == remove.id)
                    _captureActionId = null;
                mgr.RemoveFavoriteAction(remove);
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void DrawFavoriteActionRow(VirtualCrewManager mgr, FavoriteAction action, ref FavoriteAction remove)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(action.DisplayName + "  [" + action.HotkeyLabel + "]"))
                mgr.InvokeFavoriteAction(action);
            if (GUILayout.Button("Set Key", GUILayout.Width(70)))
                _captureActionId = action.id;
            if (GUILayout.Button("X", GUILayout.Width(28)))
                remove = action;
            GUILayout.EndHorizontal();
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
    }
}
