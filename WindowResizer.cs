using UnityEngine;

namespace SailwindVirtualCrew
{
    internal struct WindowResizer
    {
        internal const float HandleHeight = 10f;

        public  float UserHeight;
        private bool  _resizing;
        private float _dragStartLocalY;
        private float _dragStartHeight;

        // Call at every return point in DrawWindow, just before GUI.DragWindow().
        // FlexibleSpace pushes the handle to the visual bottom of the window.
        internal void HandleInWindow(ref Rect windowRect)
        {
            GUILayout.FlexibleSpace();
            Rect handle = GUILayoutUtility.GetRect(0f, HandleHeight, GUILayout.ExpandWidth(true));
            GUI.Box(handle, "");

            var e = Event.current;
            if (e.type == EventType.MouseDown && handle.Contains(e.mousePosition))
            {
                _resizing        = true;
                _dragStartLocalY = e.mousePosition.y;
                _dragStartHeight = windowRect.height;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _resizing)
            {
                UserHeight = Mathf.Max(80f, _dragStartHeight + (e.mousePosition.y - _dragStartLocalY));
                e.Use();
            }
            else if (e.type == EventType.MouseUp)
            {
                _resizing = false;
            }
        }
    }
}
