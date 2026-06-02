using System.Reflection;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class WindowVisibilityUtility
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static bool TryGetVisible(Component component, out bool visible)
        {
            visible = false;
            if (component == null)
                return false;

            var field = component.GetType().GetField("showWindow", InstanceFlags);
            if (field == null || field.FieldType != typeof(bool))
                return false;

            visible = (bool)field.GetValue(component);
            return true;
        }

        internal static bool TrySetVisible(Component component, bool visible)
        {
            if (component == null)
                return false;

            var setVisible = component.GetType().GetMethod("SetVisible", InstanceFlags, null, new[] { typeof(bool) }, null);
            if (setVisible != null)
            {
                setVisible.Invoke(component, new object[] { visible });
                return true;
            }

            var field = component.GetType().GetField("showWindow", InstanceFlags);
            if (field == null || field.FieldType != typeof(bool))
                return false;

            field.SetValue(component, visible);
            return true;
        }
    }
}
