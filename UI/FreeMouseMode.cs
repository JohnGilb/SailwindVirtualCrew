using UnityEngine;

namespace SailwindVirtualCrew
{
    // Frees the cursor without opening the inventory, using the same switch the game's own menus use
    // (MouseLook.ToggleMouseLookAndCursor), so camera look and world clicks pause while it is on.
    internal static class FreeMouseMode
    {
        private static bool _active;

        public static bool IsActive => _active;

        public static void Tick()
        {
            // A game menu (inventory, pause, map table, shipyard...) re-locked the cursor when it closed.
            if (_active && (Cursor.lockState == CursorLockMode.Locked || !GameState.inCursorMenu))
                _active = false;
        }

        public static void Enable()
        {
            // Leave the cursor alone while a game menu owns it; that menu will re-lock it on close.
            if (_active || GameState.inCursorMenu || GameState.currentlyLoading || GameState.inBed)
                return;

            _active = true;
            MouseLook.ToggleMouseLookAndCursor(newState: false);
        }

        public static void Disable()
        {
            if (!_active)
                return;

            _active = false;
            MouseLook.ToggleMouseLookAndCursor(newState: true);
        }
    }
}
