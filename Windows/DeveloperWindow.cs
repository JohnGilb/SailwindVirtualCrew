using UnityEngine;

namespace SailwindVirtualCrew
{
    public class DeveloperWindow : MonoBehaviour
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(20, 20, 300, 80);
        private static readonly int windowId = "VirtualCrewDeveloperWindow".GetHashCode();

        private void Update()
        {
            if (Plugin.ToggleCrewWindow.Value.IsDown())
                showWindow = !showWindow;
        }

        private void OnGUI()
        {
            if (!showWindow) return;

            float height = 100f + 26f; // title bar + activate button
            if (DeveloperMode.IsEnabled)
                height += 26f; // add basic crew button

            windowRect.height = height;
            windowRect = GUI.Window(windowId, windowRect, DrawWindow, "Developer Tools");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            string toggleLabel = DeveloperMode.IsEnabled ? "Deactivate Developer Mode" : "Activate Developer Mode";
            if (GUILayout.Button(toggleLabel))
                DeveloperMode.IsEnabled = !DeveloperMode.IsEnabled;

            if (DeveloperMode.IsEnabled)
            {
                if (GUILayout.Button("Add Basic Crew"))
                    AddBasicCrew();
            }

            GUI.DragWindow();
        }

        private static void AddBasicCrew()
        {
            var mgr = VirtualCrewManager.Instance;
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Deckhand));
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Deckhand));
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Deckhand));
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Pilot));
            mgr.Crew.Add(mgr.CreateRandomCrewman(ShipRole.Navigator));
        }
    }
}
