using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// Edits one crewman's look (Sailwind Player Model mod only): gender, each body part and each color, with a
    /// rotating preview. The crewman's body aboard restyles as the edit goes; Save keeps the look on the crewman
    /// (saved with the game), Cancel puts the old one back. Opened from the Crew Roster.
    /// </summary>
    public class CrewAppearanceWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(1270, 20, 560, 560);
        private static readonly int windowId = "VirtualCrewAppearanceWindow".GetHashCode();

        private WindowResizer _resizer;

        public string WindowKey => "CrewAppearanceWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 1270f, 20f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private const float PreviewWidth = 220f;
        private const float ArrowWidth = 30f;
        private const float LabelWidth = 90f;
        private const float ValueWidth = 110f;
        private const float DragTurnDegreesPerPixel = 0.6f;
        private const float TurnStepDegrees = 30f;

        private Crewman _crewman;
        private ICrewAppearanceEditor _editor;
        private string _message;

        public bool IsEditing(Crewman crewman) => showWindow && _crewman == crewman;

        /// <summary>Start editing <paramref name="crewman"/>'s look, ending any edit already open without saving.</summary>
        public void Open(Crewman crewman)
        {
            EndEdit(keep: false);
            _crewman = crewman;
            _message = null;

            string appearance = CrewVisualFactory.ResolveAppearance(crewman);
            var liveBody = CrewNavigationCoordinator.Instance.TryGetCrewBody(crewman);
            _editor = PlayerModelCrewBodies.TryBeginEdit(appearance, liveBody);
            if (_editor == null)
                _message = "The appearance editor is not available yet. Try again once a port or ship has loaded.";

            showWindow = true;
        }

        private void Update()
        {
            // The crewman left the crew (fired, removed) while their look was open.
            if (showWindow && _crewman != null && !VirtualCrewManager.Instance.Crew.Contains(_crewman))
                Close(keep: false);
        }

        private void OnDestroy()
        {
            EndEdit(keep: false);
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            windowRect.width = Mathf.Max(windowRect.width, 560f);
            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : 560f;
            string title = _crewman != null ? "Appearance: " + _crewman.Name : "Appearance";
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, title);
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            if (_editor == null)
            {
                GUILayout.Label(_message ?? "Nothing to edit.");
                if (GUILayout.Button("Close"))
                    Close(keep: false);
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            GUILayout.BeginHorizontal();
            DrawPreview();
            GUILayout.Space(8);
            DrawRows();
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Randomize"))
                _editor.Randomize();
            if (GUILayout.Button("Save"))
                Close(keep: true);
            if (GUILayout.Button("Cancel"))
                Close(keep: false);
            GUILayout.EndHorizontal();

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void DrawPreview()
        {
            float height = PreviewWidth * _editor.PreviewHeightPerWidth;
            GUILayout.BeginVertical(GUILayout.Width(PreviewWidth));
            Rect rect = GUILayoutUtility.GetRect(PreviewWidth, height, GUILayout.Width(PreviewWidth), GUILayout.Height(height));
            var texture = _editor.PreviewTexture;
            if (texture != null)
            {
                if (Event.current.type == EventType.Repaint)
                    GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Label(rect, "Preparing preview...");
            }

            // Drag across the preview to turn the model.
            var e = Event.current;
            if (e.type == EventType.MouseDrag && rect.Contains(e.mousePosition))
            {
                _editor.TurnPreview(-e.delta.x * DragTurnDegreesPerPixel);
                e.Use();
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<"))
                _editor.TurnPreview(TurnStepDegrees);
            if (GUILayout.Button(">"))
                _editor.TurnPreview(-TurnStepDegrees);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void DrawRows()
        {
            GUILayout.BeginVertical();
            for (int row = 0; row < _editor.RowCount; row++)
            {
                if (!_editor.IsRowShown(row))
                    continue;

                GUILayout.BeginHorizontal();
                GUILayout.Label(_editor.RowLabel(row), GUILayout.Width(LabelWidth));
                if (GUILayout.Button("<", GUILayout.Width(ArrowWidth)))
                    _editor.Step(row, -1);
                GUILayout.Label(_editor.RowValue(row), GUILayout.Width(ValueWidth));
                if (GUILayout.Button(">", GUILayout.Width(ArrowWidth)))
                    _editor.Step(row, 1);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        private void Close(bool keep)
        {
            EndEdit(keep);
            showWindow = false;
        }

        private void EndEdit(bool keep)
        {
            if (_editor != null)
            {
                if (keep && _crewman != null)
                {
                    _crewman.SetAppearance(_editor.Result);
                    CrewDebugLog.Ok("PlayerModel", "Saved appearance for crew='" + _crewman.Name + "': " + _crewman.Appearance);
                }
                _editor.Close(keep);
                _editor = null;
            }
            _crewman = null;
        }
    }
}
