using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class SailwindGuiStyle
    {
        private const float NightStartHour = 18f;
        private const float NightEndHour   = 6f;

        private static bool _initialized;
        private static Font _immortalFont;
        private static Font _architectsFont;
        private static Texture2D _dayWindow;
        private static Texture2D _dayButton;
        private static Texture2D _dayButtonHover;
        private static Texture2D _dayButtonActive;
        private static Texture2D _dayLabel;
        private static Texture2D _dayField;
        private static Texture2D _dayBox;

        private static Texture2D _nightWindow;
        private static Texture2D _nightButton;
        private static Texture2D _nightButtonHover;
        private static Texture2D _nightButtonActive;
        private static Texture2D _nightLabel;
        private static Texture2D _nightField;
        private static Texture2D _nightBox;

        internal static bool IsDarkMode => IsNightTime();

        internal static void Apply()
        {
            if (!_initialized) Initialize();

            var palette = IsDarkMode ? Palette.Night : Palette.Day;

            GUI.skin.window.font            = _architectsFont;
            GUI.skin.window.normal.background = palette.WindowBackground;
            GUI.skin.window.onNormal.background = palette.WindowBackground;
            GUI.skin.window.normal.textColor = palette.Text;
            GUI.skin.window.onNormal.textColor = palette.Text;

            GUI.skin.label.font             = _immortalFont;
            GUI.skin.label.fontSize         = 18;
            GUI.skin.label.alignment        = TextAnchor.MiddleCenter;
            ApplyState(GUI.skin.label.normal, palette.LabelBackground, palette.Text);
            ApplyState(GUI.skin.label.hover, palette.LabelBackground, palette.Text);
            ApplyState(GUI.skin.label.active, palette.LabelBackground, palette.Text);
            ApplyState(GUI.skin.label.focused, palette.LabelBackground, palette.Text);

            GUI.skin.button.font            = _architectsFont;
            GUI.skin.button.fontSize        = 14;
            GUI.skin.button.alignment       = TextAnchor.MiddleCenter;
            ApplyState(GUI.skin.button.normal, palette.ButtonBackground, palette.Text);
            ApplyState(GUI.skin.button.hover, palette.ButtonHoverBackground, palette.Text);
            ApplyState(GUI.skin.button.active, palette.ButtonActiveBackground, palette.Text);
            ApplyState(GUI.skin.button.focused, palette.ButtonHoverBackground, palette.Text);
            ApplyState(GUI.skin.button.onNormal, palette.ButtonActiveBackground, palette.Text);
            ApplyState(GUI.skin.button.onHover, palette.ButtonHoverBackground, palette.Text);
            ApplyState(GUI.skin.button.onActive, palette.ButtonActiveBackground, palette.Text);
            ApplyState(GUI.skin.button.onFocused, palette.ButtonHoverBackground, palette.Text);

            GUI.skin.textField.font         = _architectsFont;
            GUI.skin.textField.fontSize     = 14;
            ApplyState(GUI.skin.textField.normal, palette.FieldBackground, palette.Text);
            ApplyState(GUI.skin.textField.hover, palette.FieldBackground, palette.Text);
            ApplyState(GUI.skin.textField.active, palette.FieldBackground, palette.Text);
            ApplyState(GUI.skin.textField.focused, palette.FieldBackground, palette.Text);

            GUI.skin.toggle.font            = _architectsFont;
            GUI.skin.toggle.fontSize        = 14;
            SetTextColor(GUI.skin.toggle.normal, palette.Text);
            SetTextColor(GUI.skin.toggle.hover, palette.Text);
            SetTextColor(GUI.skin.toggle.active, palette.Text);
            SetTextColor(GUI.skin.toggle.focused, palette.Text);
            SetTextColor(GUI.skin.toggle.onNormal, palette.Text);
            SetTextColor(GUI.skin.toggle.onHover, palette.Text);
            SetTextColor(GUI.skin.toggle.onActive, palette.Text);
            SetTextColor(GUI.skin.toggle.onFocused, palette.Text);

            ApplyState(GUI.skin.box.normal, palette.BoxBackground, palette.Text);
            ApplyState(GUI.skin.box.hover, palette.BoxBackground, palette.Text);
            ApplyState(GUI.skin.box.active, palette.BoxBackground, palette.Text);
            ApplyState(GUI.skin.box.focused, palette.BoxBackground, palette.Text);

            ApplyState(GUI.skin.horizontalScrollbar.normal, palette.BoxBackground, palette.Text);
            ApplyState(GUI.skin.horizontalScrollbarThumb.normal, palette.ButtonBackground, palette.Text);
            ApplyState(GUI.skin.horizontalScrollbarThumb.hover, palette.ButtonHoverBackground, palette.Text);
            ApplyState(GUI.skin.horizontalScrollbarThumb.active, palette.ButtonActiveBackground, palette.Text);
            ApplyState(GUI.skin.horizontalScrollbarLeftButton.normal, palette.ButtonBackground, palette.Text);
            ApplyState(GUI.skin.horizontalScrollbarRightButton.normal, palette.ButtonBackground, palette.Text);

            ApplyState(GUI.skin.verticalScrollbar.normal, palette.BoxBackground, palette.Text);
            ApplyState(GUI.skin.verticalScrollbarThumb.normal, palette.ButtonBackground, palette.Text);
            ApplyState(GUI.skin.verticalScrollbarThumb.hover, palette.ButtonHoverBackground, palette.Text);
            ApplyState(GUI.skin.verticalScrollbarThumb.active, palette.ButtonActiveBackground, palette.Text);
            ApplyState(GUI.skin.verticalScrollbarUpButton.normal, palette.ButtonBackground, palette.Text);
            ApplyState(GUI.skin.verticalScrollbarDownButton.normal, palette.ButtonBackground, palette.Text);

            ApplyState(GUI.skin.horizontalSlider.normal, palette.BoxBackground, palette.Text);
            ApplyState(GUI.skin.horizontalSliderThumb.normal, palette.ButtonBackground, palette.Text);
            ApplyState(GUI.skin.horizontalSliderThumb.hover, palette.ButtonHoverBackground, palette.Text);
            ApplyState(GUI.skin.horizontalSliderThumb.active, palette.ButtonActiveBackground, palette.Text);
        }

        internal static bool HasThemeChanged(bool previousDarkMode)
        {
            return previousDarkMode != IsDarkMode;
        }

        private static void Initialize()
        {
            _initialized = true;

            foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (font.name == "IMMORTAL")            _immortalFont   = font;
                else if (font.name == "ArchitectsDaughter") _architectsFont = font;
            }

            _dayWindow       = MakeTexture(new Color(144f / 255f, 120f / 255f,  97f / 255f));
            _dayButton       = MakeTexture(new Color(230f / 255f, 187f / 255f, 156f / 255f));
            _dayButtonHover  = MakeTexture(new Color(244f / 255f, 204f / 255f, 173f / 255f));
            _dayButtonActive = MakeTexture(new Color(196f / 255f, 150f / 255f, 118f / 255f));
            _dayLabel        = MakeTexture(new Color(153f / 255f, 103f / 255f,  93f / 255f));
            _dayField        = MakeTexture(new Color(242f / 255f, 218f / 255f, 190f / 255f));
            _dayBox          = MakeTexture(new Color(116f / 255f,  92f / 255f,  73f / 255f));

            _nightWindow       = MakeTexture(new Color(38f / 255f, 32f / 255f, 27f / 255f));
            _nightButton       = MakeTexture(new Color(78f / 255f, 60f / 255f, 47f / 255f));
            _nightButtonHover  = MakeTexture(new Color(98f / 255f, 76f / 255f, 59f / 255f));
            _nightButtonActive = MakeTexture(new Color(116f / 255f, 86f / 255f, 62f / 255f));
            _nightLabel        = MakeTexture(new Color(55f / 255f, 42f / 255f, 35f / 255f));
            _nightField        = MakeTexture(new Color(46f / 255f, 38f / 255f, 32f / 255f));
            _nightBox          = MakeTexture(new Color(62f / 255f, 50f / 255f, 42f / 255f));
        }

        private static bool IsNightTime()
        {
            return Sun.sun != null && (Sun.sun.localTime >= NightStartHour || Sun.sun.localTime < NightEndHour);
        }

        private static void ApplyState(GUIStyleState state, Texture2D background, Color textColor)
        {
            state.background = background;
            state.textColor = textColor;
        }

        private static void SetTextColor(GUIStyleState state, Color textColor)
        {
            state.textColor = textColor;
        }

        private static Texture2D MakeTexture(Color color)
        {
            var tex = new Texture2D(2, 2);
            for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                    tex.SetPixel(x, y, color);
            tex.Apply();
            return tex;
        }

        private struct Palette
        {
            internal static Palette Day => new Palette
            {
                Text = Color.black,
                WindowBackground = _dayWindow,
                ButtonBackground = _dayButton,
                ButtonHoverBackground = _dayButtonHover,
                ButtonActiveBackground = _dayButtonActive,
                LabelBackground = _dayLabel,
                FieldBackground = _dayField,
                BoxBackground = _dayBox
            };

            internal static Palette Night => new Palette
            {
                Text = new Color(0.82f, 0.75f, 0.65f),
                WindowBackground = _nightWindow,
                ButtonBackground = _nightButton,
                ButtonHoverBackground = _nightButtonHover,
                ButtonActiveBackground = _nightButtonActive,
                LabelBackground = _nightLabel,
                FieldBackground = _nightField,
                BoxBackground = _nightBox
            };

            internal Color Text;
            internal Texture2D WindowBackground;
            internal Texture2D ButtonBackground;
            internal Texture2D ButtonHoverBackground;
            internal Texture2D ButtonActiveBackground;
            internal Texture2D LabelBackground;
            internal Texture2D FieldBackground;
            internal Texture2D BoxBackground;
        }
    }
}
