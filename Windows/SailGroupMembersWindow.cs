using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class SailGroupMembersWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(860, 20, 320, 560);
        private static readonly int windowId = "VirtualCrewSailGroupMembersWindow".GetHashCode();

        private WindowResizer _resizer;

        public string WindowKey => "SailGroupMembersWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private const float ButtonHeight      = 28f;
        private const float BaseContentHeight = 300f;

        private void Update()
        {
            if (Plugin.ToggleCrewWindow.Value.IsDown())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            var manager       = VirtualCrewManager.Instance;
            var sails         = manager.AllSails;
            var selectedGroup = manager.SelectedGroup;

            float contentHeight = ButtonHeight; // header label
            if (selectedGroup == null)
                contentHeight += ButtonHeight;
            else if (selectedGroup.IsAllSails)
                contentHeight += ButtonHeight;
            else
                contentHeight += selectedGroup.GetMembers(sails).Count() * ButtonHeight;

            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : BaseContentHeight + contentHeight;
            windowRect = GUI.Window(windowId, windowRect, DrawWindow, "Group Members");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            var manager       = VirtualCrewManager.Instance;
            var sails         = manager.AllSails;
            var selectedGroup = manager.SelectedGroup;

            if (selectedGroup == null)
            {
                GUILayout.Label("No group selected.");
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            GUILayout.Label($"Members: {selectedGroup.Name}");

            if (selectedGroup.IsAllSails)
            {
                GUILayout.Label("All sails — managed automatically.");
            }
            else
            {
                ICommonSailActions memberToRemove = null;
                foreach (var member in selectedGroup.GetMembers(sails))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(member.getSailName());
                    if (GUILayout.Button("−", GUILayout.Width(28))) memberToRemove = member;
                    GUILayout.EndHorizontal();
                }
                if (memberToRemove != null) selectedGroup.RemoveSail(memberToRemove);
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }
    }
}
