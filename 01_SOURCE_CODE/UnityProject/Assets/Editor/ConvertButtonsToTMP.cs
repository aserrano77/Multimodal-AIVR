// Tools/Tiago/Convert Buttons To TMP
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public static class ConvertButtonsToTMP {
    [MenuItem("Tools/Tiago/Convert Buttons To TMP")]
    public static void Run() {
        var panel = Selection.activeTransform;
        if (!panel) { Debug.LogWarning("Selecciona el objeto 'Panel' del Canvas."); return; }
        int count = 0;
        foreach (Transform t in panel) {
            var btn = t.GetComponent<Button>();
            if (!btn) continue;

            // Image visible
            var img = t.GetComponent<Image>() ?? t.gameObject.AddComponent<Image>();
            img.color = new Color(1,1,1,0.92f);
            btn.transition = Selectable.Transition.ColorTint;
            btn.targetGraphic = img;

            // Layout
            var le = t.GetComponent<LayoutElement>() ?? t.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 0.05f; le.preferredHeight = 0.05f;

            // Elimina Text legacy
            var legacy = t.GetComponentInChildren<Text>();
            if (legacy) Object.DestroyImmediate(legacy.gameObject);

            // Crea TMP si no existe
            var tmp = t.GetComponentInChildren<TextMeshProUGUI>();
            if (!tmp) {
                var go = new GameObject("Text (TMP)", typeof(RectTransform), typeof(TextMeshProUGUI));
                go.transform.SetParent(t, false);
                tmp = go.GetComponent<TextMeshProUGUI>();
            }
            tmp.text = t.name;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 12; tmp.fontSizeMax = 48;
            tmp.alignment = TextAlignmentOptions.MidlineGeoAligned | TextAlignmentOptions.Center;
            tmp.color = Color.black;
            var rt = tmp.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;

            count++;
        }
        // Canvas: canales extra
        var canvas = panel.GetComponentInParent<Canvas>();
        if (canvas) canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 |
                                                       AdditionalCanvasShaderChannels.TexCoord2 |
                                                       AdditionalCanvasShaderChannels.Normal |
                                                       AdditionalCanvasShaderChannels.Tangent;
        Debug.Log($"Convertidos {count} botones a TMP y ajustados.");
    }
}
