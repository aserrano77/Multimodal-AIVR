using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

public static class CreateVRPanel
{
    [MenuItem("Tools/Tiago/Create VR Canvas (World Space)")]
    public static void CreateCanvas()
    {
        var cam = Camera.main;
        if (!cam) { Debug.LogWarning("No se encontró Main Camera. Asigna la Event Camera después."); }

        // Canvas
        var canvasGO = new GameObject("TiagoDemoCanvasVR",
            typeof(Canvas), typeof(CanvasScaler),
            typeof(GraphicRaycaster), typeof(TrackedDeviceGraphicRaycaster));
        var canvas = canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        if (cam) canvas.worldCamera = cam;

        var scaler = canvasGO.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.dynamicPixelsPerUnit = 700;

        // Tamaño y pose (delante de la cámara si existe)
        var rt = canvasGO.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0.35f, 0.50f);
        if (cam)
        {
            canvasGO.transform.SetParent(cam.transform, false);
            canvasGO.transform.localPosition = new Vector3(0.35f, -0.10f, 0.60f);
            canvasGO.transform.localRotation = Quaternion.identity;
        }

        // Panel
        var panelGO = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        panelGO.transform.SetParent(canvasGO.transform, false);
        var img = panelGO.GetComponent<Image>(); img.color = new Color(0,0,0,0.45f);
        var v = panelGO.GetComponent<VerticalLayoutGroup>();
        v.spacing = 0.008f; v.padding = new RectOffset(12,12,12,12);
        v.childControlHeight = v.childControlWidth = true;

        // Helper para botones
        void AddBtn(string label)
        {
            var b = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
            b.transform.SetParent(panelGO.transform, false);
            b.GetComponent<Image>().color = new Color(1,1,1,0.92f);
            var txtGO = new GameObject("Text", typeof(RectTransform), typeof(Text));
            txtGO.transform.SetParent(b.transform, false);
            var txt = txtGO.GetComponent<Text>();
            txt.text = label; txt.alignment = TextAnchor.MiddleCenter;
            txt.resizeTextForBestFit = true; txt.resizeTextMinSize = 10; txt.resizeTextMaxSize = 36; txt.color = Color.black;
            var t = txtGO.GetComponent<RectTransform>(); t.anchorMin = Vector2.zero; t.anchorMax = Vector2.one; t.offsetMin = t.offsetMax = Vector2.zero;
            b.AddComponent<LayoutElement>().minHeight = 0.05f;
        }

        AddBtn("Home");
        AddBtn("Wave (R)");
        AddBtn("Torso Up");
        AddBtn("Torso Down");
        AddBtn("Head Left");
        AddBtn("Head Right");
        AddBtn("Head Up");
        AddBtn("Head Down");
        AddBtn("Open Gripper R");
        AddBtn("Close Gripper R");

        // EventSystem (si falta)
        if (!Object.FindFirstObjectByType<EventSystem>())
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        Selection.activeObject = canvasGO;
        EditorGUIUtility.PingObject(canvasGO);
        Debug.Log("VR Canvas creado. Conecta los botones a TiagoController desde el Inspector.");
    }
}
