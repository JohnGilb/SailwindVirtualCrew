using System;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public enum FavoriteActionKind
    {
        Custom,
        Halyard,
        SimpleSheet,
        RelativeSheet,
        DualSheet,
        Trim
    }

    public enum FavoriteShipAction
    {
        DropAnchor,
        RaiseAnchor,
        MoorPort,
        MoorStarboard
    }

    [Serializable]
    public class FavoriteActionGroupCommand
    {
        public string groupId;
        public string groupName;
        public bool hasHalyard;
        public float halyard;
        public bool hasSimpleSheet;
        public float simpleSheet;
        public bool hasPortSheet;
        public float portSheet;
        public bool hasStarboardSheet;
        public float starboardSheet;
        public bool trim;
    }

    [Serializable]
    public class FavoriteAction
    {
        public string id;
        public string name;
        public string groupId;
        public string groupName;
        public FavoriteActionKind kind;
        public string label;
        public float target;
        public float delta;
        public float portTarget;
        public float starboardTarget;
        public DualSheetSail.DualSheetSailSubtype dualSheetSubtype;
        public int keyCode;
        public bool dropAnchor;
        public bool raiseAnchor;
        public bool moorPort;
        public bool moorStarboard;
        public System.Collections.Generic.List<FavoriteActionGroupCommand> commands =
            new System.Collections.Generic.List<FavoriteActionGroupCommand>();

        public KeyCode Key => (KeyCode)keyCode;
        public bool IsCustom => kind == FavoriteActionKind.Custom || (commands != null && commands.Count > 0);

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(name))
                    return name;

                string group = string.IsNullOrEmpty(groupName) ? "Group" : groupName;
                return group + " - " + ActionLabel;
            }
        }

        public string HotkeyLabel => Key == KeyCode.None ? "None" : Key.ToString();

        public string ActionLabel
        {
            get
            {
                switch (kind)
                {
                    case FavoriteActionKind.Custom:        return "Custom";
                    case FavoriteActionKind.Halyard:       return "Halyard: " + label;
                    case FavoriteActionKind.SimpleSheet:   return "Sheet: " + label;
                    case FavoriteActionKind.RelativeSheet: return "Sheet: " + label;
                    case FavoriteActionKind.DualSheet:     return "Sheet: " + label;
                    case FavoriteActionKind.Trim:          return "Trim";
                    default:                               return label ?? "Action";
                }
            }
        }

        public static FavoriteAction Custom(string name) =>
            new FavoriteAction
            {
                id = Guid.NewGuid().ToString("N"),
                name = string.IsNullOrEmpty(name) ? "New Favorite" : name,
                kind = FavoriteActionKind.Custom,
                keyCode = (int)KeyCode.None,
                commands = new System.Collections.Generic.List<FavoriteActionGroupCommand>()
            };

        public static FavoriteAction Halyard(SailGroup group, string label, float target) =>
            Create(group, FavoriteActionKind.Halyard, label, a => a.target = target);

        public static FavoriteAction SimpleSheet(SailGroup group, string label, float target) =>
            Create(group, FavoriteActionKind.SimpleSheet, label, a => a.target = target);

        public static FavoriteAction RelativeSheet(SailGroup group, string label, float delta) =>
            Create(group, FavoriteActionKind.RelativeSheet, label, a => a.delta = delta);

        public static FavoriteAction DualSheet(SailGroup group, string label, DualSheetSail.DualSheetSailSubtype subtype, float portTarget, float starboardTarget) =>
            Create(group, FavoriteActionKind.DualSheet, label, a =>
            {
                a.dualSheetSubtype = subtype;
                a.portTarget = portTarget;
                a.starboardTarget = starboardTarget;
            });

        public static FavoriteAction Trim(SailGroup group) =>
            Create(group, FavoriteActionKind.Trim, "Trim", _ => { });

        private static FavoriteAction Create(SailGroup group, FavoriteActionKind kind, string label, Action<FavoriteAction> configure)
        {
            var action = new FavoriteAction
            {
                id = Guid.NewGuid().ToString("N"),
                groupId = group.Id,
                groupName = group.Name,
                kind = kind,
                label = label,
                keyCode = (int)KeyCode.None
            };
            configure(action);
            return action;
        }
    }
}
