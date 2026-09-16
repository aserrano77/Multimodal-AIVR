using TMPro;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    /// <summary>
    /// Shared, allocation-free typography contract for the experiment's runtime canvases.
    /// The material is assigned through fontSharedMaterial so individual labels never clone it.
    /// </summary>
    public static class ExperimentCanvasTypography
    {
        public const string FontResourcePath = "ExperimentTypography/Arial SDF - Experimental Canvas";
        public const string MaterialResourcePath = "ExperimentTypography/Arial SDF - Experimental Canvas No Effects";
        public const string ExpectedShaderName = "TextMeshPro/Mobile/Distance Field";

        private static TMP_FontAsset s_fontAsset;
        private static Material s_sharedMaterial;

        public static TMP_FontAsset FontAsset
        {
            get
            {
                if (s_fontAsset == null)
                {
                    s_fontAsset = Resources.Load<TMP_FontAsset>(FontResourcePath);
                }

                return s_fontAsset;
            }
        }

        public static Material SharedMaterial
        {
            get
            {
                if (s_sharedMaterial == null)
                {
                    s_sharedMaterial = Resources.Load<Material>(MaterialResourcePath);
                }

                return s_sharedMaterial;
            }
        }

        public static void Configure(TextMeshProUGUI text)
        {
            if (text == null)
            {
                return;
            }

            TMP_FontAsset fontAsset = FontAsset;
            Material sharedMaterial = SharedMaterial;
            if (fontAsset == null || sharedMaterial == null)
            {
                Debug.LogError(
                    $"[P46J-02] typography_asset_missing | font={FontResourcePath} material={MaterialResourcePath}");
                return;
            }

            text.font = fontAsset;
            text.fontSharedMaterial = sharedMaterial;
            text.extraPadding = false;
            text.richText = true;
            text.raycastTarget = false;
        }

        public static void ApplyLegacyLineSpacing(TextMeshProUGUI text, float legacyMultiplier)
        {
            if (text == null)
            {
                return;
            }

            text.lineSpacing = text.fontSize * Mathf.Max(0f, legacyMultiplier - 1f);
        }

        public static bool UsesSharedNoEffectsMaterial(TextMeshProUGUI text)
        {
            return text != null &&
                   text.font == FontAsset &&
                   text.fontSharedMaterial == SharedMaterial;
        }
    }
}
