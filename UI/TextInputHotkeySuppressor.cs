using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public static class TextInputHotkeySuppressor
    {
        private const string TextControlPrefix = "VirtualCrewTextInput.";
        private static readonly HashSet<string> ActiveContexts = new HashSet<string>();

        public static bool ShouldSuppressFavoriteActionHotkeys => ActiveContexts.Count > 0;

        public static string ControlName(string name) => TextControlPrefix + name;

        public static bool IsFocusedControl(string name)
        {
            return GUI.GetNameOfFocusedControl() == ControlName(name);
        }

        public static void SetActive(string context, bool active)
        {
            if (string.IsNullOrEmpty(context))
                return;

            if (active)
                ActiveContexts.Add(context);
            else
                ActiveContexts.Remove(context);
        }

    }
}
