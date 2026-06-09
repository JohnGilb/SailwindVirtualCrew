using System;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class SailGroupCommandPalette
    {
        internal static SailCapability Draw(VirtualCrewManager manager,
                                            SailGroup group,
                                            bool includeTrim,
                                            Action<string, float> onHalyard,
                                            Action<string, float> onSimpleSheet,
                                            Action<string, DualSheetSail.DualSheetSailSubtype, float, float> onDualSheet,
                                            Action onTrim)
        {
            var caps = group.GetCommonCapabilities(manager.AllSails);
            if (caps == SailCapability.None)
            {
                GUILayout.Label("This group has no sails.");
                return caps;
            }

            if (caps.HasFlag(SailCapability.Halyard))
            {
                GUILayout.Label("Halyard:");
                GUILayout.BeginHorizontal();
                DrawButton("Reef", () => onHalyard("Reef", 0.00f));
                DrawButton("1/4", () => onHalyard("1/4", 0.25f));
                DrawButton("1/2", () => onHalyard("1/2", 0.50f));
                DrawButton("3/4", () => onHalyard("3/4", 0.75f));
                DrawButton("Full", () => onHalyard("Full", 1.00f));
                GUILayout.EndHorizontal();
            }

            if (caps.HasFlag(SailCapability.SimpleSheet))
            {
                GUILayout.Label("Sheet:");
                GUILayout.BeginHorizontal();
                DrawButton("Hard", () => onSimpleSheet("Hard", 0.00f));
                DrawButton("1/4", () => onSimpleSheet("1/4", 0.25f));
                DrawButton("1/2", () => onSimpleSheet("1/2", 0.50f));
                DrawButton("3/4", () => onSimpleSheet("3/4", 0.75f));
                DrawButton("Let Fly", () => onSimpleSheet("Let Fly", 1.00f));
                GUILayout.EndHorizontal();
            }
            else if (caps.HasFlag(SailCapability.SquareSheet))
            {
                GUILayout.Label("Sheet:");
                GUILayout.BeginHorizontal();
                DrawDualButton("Full Port", DualSheetSail.DualSheetSailSubtype.Square, 0.00f, 1.00f, onDualSheet);
                DrawDualButton("1/2 Port", DualSheetSail.DualSheetSailSubtype.Square, 0.25f, 0.75f, onDualSheet);
                DrawDualButton("Ahead", DualSheetSail.DualSheetSailSubtype.Square, 0.50f, 0.50f, onDualSheet);
                DrawDualButton("1/2 Stbd", DualSheetSail.DualSheetSailSubtype.Square, 0.75f, 0.25f, onDualSheet);
                DrawDualButton("Full Stbd", DualSheetSail.DualSheetSailSubtype.Square, 1.00f, 0.00f, onDualSheet);
                GUILayout.EndHorizontal();
            }
            else if (caps.HasFlag(SailCapability.JibSheet))
            {
                GUILayout.Label("Sheet:");
                GUILayout.BeginHorizontal();
                DrawDualButton("Full Port", DualSheetSail.DualSheetSailSubtype.Jib, 0.00f, 1.00f, onDualSheet);
                DrawDualButton("3/4 Port", DualSheetSail.DualSheetSailSubtype.Jib, 0.25f, 1.00f, onDualSheet);
                DrawDualButton("1/2 Port", DualSheetSail.DualSheetSailSubtype.Jib, 0.50f, 1.00f, onDualSheet);
                DrawDualButton("1/4 Port", DualSheetSail.DualSheetSailSubtype.Jib, 0.75f, 1.00f, onDualSheet);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawDualButton("Let Fly", DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 1.00f, onDualSheet);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawDualButton("Full Stbd", DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.00f, onDualSheet);
                DrawDualButton("3/4 Stbd", DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.25f, onDualSheet);
                DrawDualButton("1/2 Stbd", DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.50f, onDualSheet);
                DrawDualButton("1/4 Stbd", DualSheetSail.DualSheetSailSubtype.Jib, 1.00f, 0.75f, onDualSheet);
                GUILayout.EndHorizontal();
            }

            if (includeTrim && caps.HasFlag(SailCapability.Trim) && onTrim != null)
            {
                GUILayout.BeginHorizontal();
                DrawButton("Trim", onTrim);
                GUILayout.EndHorizontal();
            }

            return caps;
        }

        private static void DrawDualButton(string label,
                                           DualSheetSail.DualSheetSailSubtype subtype,
                                           float portTarget,
                                           float starboardTarget,
                                           Action<string, DualSheetSail.DualSheetSailSubtype, float, float> onDualSheet)
        {
            DrawButton(label, () => onDualSheet(label, subtype, portTarget, starboardTarget));
        }

        private static void DrawButton(string label, Action onClick)
        {
            if (GUILayout.Button(label))
                onClick?.Invoke();
        }
    }
}
