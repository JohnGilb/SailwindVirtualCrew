using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SailwindVirtualCrew
{
    // TextMesh's default font material uses Unity's built-in "GUI/Text Shader", which is hard-wired to
    // ZTest Always, so in-world labels draw through decks and hulls. Unity's built-in "UI/Default" shader
    // (always included in builds) can draw the same font atlas with normal depth testing instead:
    //  - its ZTest comes from the unity_GUIZTestMode property, which we set per material to LessEqual;
    //  - font atlases are alpha-only, so, exactly as uGUI does for Text, _TextureSampleAdd = (1,1,1,0)
    //    makes the glyph RGB white and lets the TextMesh color through.
    internal static class DepthTestedTextMaterial
    {
        private static readonly Dictionary<Font, Material> Materials = new Dictionary<Font, Material>();
        private static bool _subscribedToRebuilds;

        internal static void Apply(TextMesh text)
        {
            if (!text)
                return;

            Font font = text.font ? text.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (!font)
                return;

            var material = GetMaterial(font);
            var renderer = text.GetComponent<MeshRenderer>();
            if (material == null || !renderer)
                return; // Keep the default (always-on-top) material rather than render nothing.

            text.font = font;
            renderer.sharedMaterial = material;
        }

        private static Material GetMaterial(Font font)
        {
            if (Materials.TryGetValue(font, out var material) && material)
                return material;

            var shader = Shader.Find("UI/Default");
            if (!shader || !font.material)
                return null;

            material = new Material(shader)
            {
                name = "VC_DepthTestedText_" + font.name,
                mainTexture = font.material.mainTexture,
                renderQueue = (int)RenderQueue.Transparent
            };
            material.SetVector("_TextureSampleAdd", new Vector4(1f, 1f, 1f, 0f));
            material.SetFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
            Materials[font] = material;

            if (!_subscribedToRebuilds)
            {
                // Dynamic fonts can reallocate their atlas when new glyphs are requested.
                Font.textureRebuilt += OnFontTextureRebuilt;
                _subscribedToRebuilds = true;
            }

            return material;
        }

        private static void OnFontTextureRebuilt(Font font)
        {
            if (font && font.material && Materials.TryGetValue(font, out var material) && material)
                material.mainTexture = font.material.mainTexture;
        }
    }
}
