using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class SailwindGuiStyle
    {
        private static bool _initialized;
        private static Font _immortalFont;
        private static Font _architectsFont;
        private static Texture2D _darkBrown;
        private static Texture2D _lightBrown;
        private static Texture2D _reddish;

        internal static void Apply()
        {
            if (!_initialized) Initialize();

            GUI.skin.window.font                = _architectsFont;
            GUI.skin.label.font                 = _immortalFont;
            GUI.skin.label.fontSize             = 18;
            GUI.skin.label.normal.textColor     = Color.black;
            GUI.skin.label.normal.background    = _reddish;
            GUI.skin.label.alignment            = TextAnchor.MiddleCenter;
            GUI.skin.button.font         = _architectsFont;
            GUI.skin.button.fontSize     = 14;
            GUI.skin.button.normal.background  = _lightBrown;
            GUI.skin.button.normal.textColor   = Color.black;
            GUI.skin.button.alignment          = TextAnchor.MiddleCenter;
            GUI.skin.textField.font      = _architectsFont;
            GUI.skin.textField.fontSize  = 14;
            GUI.skin.toggle.font         = _architectsFont;
            GUI.skin.toggle.fontSize     = 14;
        }

        private static void Initialize()
        {
            _initialized = true;

            foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (font.name == "IMMORTAL")            _immortalFont   = font;
                else if (font.name == "ArchitectsDaughter") _architectsFont = font;
            }

            _darkBrown  = MakeTexture(new Color(144f / 255f, 120f / 255f,  97f / 255f));
            _lightBrown = MakeTexture(new Color(230f / 255f, 187f / 255f, 156f / 255f));
            _reddish    = MakeTexture(new Color(153f / 255f, 103f / 255f,  93f / 255f));
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
    }
}
