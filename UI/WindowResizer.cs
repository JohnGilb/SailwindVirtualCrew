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
        // Draw as an absolute overlay so overflowing controls cannot obscure it.
        internal void HandleInWindow(ref Rect windowRect)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Space(HandleHeight);

            Rect handle = new Rect(0f, windowRect.height - HandleHeight, windowRect.width, HandleHeight);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            DrawHandle(handle);

            var e = Event.current;
            EventType eventType = e.type == EventType.Used ? e.rawType : e.type;
            if (eventType == EventType.MouseDown && handle.Contains(e.mousePosition))
            {
                _resizing        = true;
                _dragStartLocalY = e.mousePosition.y;
                _dragStartHeight = windowRect.height;
                GUIUtility.hotControl = controlId;
                if (e.type != EventType.Used) e.Use();
            }
            else if (eventType == EventType.MouseDrag && _resizing)
            {
                UserHeight = Mathf.Max(80f, _dragStartHeight + (e.mousePosition.y - _dragStartLocalY));
                GUIUtility.hotControl = controlId;
                if (e.type != EventType.Used) e.Use();
            }
            else if (eventType == EventType.MouseUp)
            {
                _resizing = false;
                if (GUIUtility.hotControl == controlId)
                    GUIUtility.hotControl = 0;
            }
        }

        private static void DrawHandle(Rect handle)
        {
            int oldDepth = GUI.depth;
            GUI.depth = int.MinValue;
            GUI.Box(handle, "");
            GUI.depth = oldDepth;
        }
    }
}
