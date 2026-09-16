using TMPro;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

public class BoxVisualSync : MonoBehaviour
{
    private const string EdgeFrameName = "BoxEdgeFrame";
    private const string DefaultEdgeMaterialShader = "Universal Render Pipeline/Lit";
    private const string ShaderBasedEdgeHighlight = "Custom/BoxEdgeHighlight_URP";
    private const int EdgeCount = 12;

    [Header("Box renderer")]
    public Renderer targetRenderer;

    [Header("Box colors by type")]
    public Color boxColorA = new Color(0f, 0.678f, 1f);         // #00ADFF
    public Color boxColorB = new Color(0.984f, 1f, 0f);         // #FBFF00
    public Color boxColorC = new Color(0.812f, 0.204f, 0.463f); // #CF3476

    [Header("Label colors by type")]
    public Color labelColorA = Color.black;
    public Color labelColorB = Color.black;
    public Color labelColorC = Color.black;

    [Header("Label typography")]
    public TMP_FontAsset labelFont;
    public float labelFontSize = 3f;
    public FontStyles labelFontStyle = FontStyles.Bold;
    public HorizontalAlignmentOptions horizontalAlignment = HorizontalAlignmentOptions.Center;
    public VerticalAlignmentOptions verticalAlignment = VerticalAlignmentOptions.Middle;
    public Color labelOutlineColor = Color.white;
    [Range(0f, 1f)] public float labelOutlineWidth = 0f;

    [Header("Label layout")]
    public Vector2 labelRectSize = new Vector2(0.5f, 0.25f);
    public Vector4 labelMargins = new Vector4(0.05f, 0.02f, 0.05f, 0.02f);
    public bool enableWordWrapping = false;
    public TextOverflowModes labelOverflowMode = TextOverflowModes.Overflow;
    public float characterSpacing = 0f;
    public float wordSpacing = 0f;

    [Header("Label style options")]
    [Tooltip("When false, each child TextMeshPro owns its font, size, alignment, margins, material and outline. BoxVisualSync only updates dynamic text, visibility, and optional color.")]
    public bool overrideExistingLabelStyle = false;
    [Tooltip("When true, BoxVisualSync updates label color from the box type. Disable this to keep per-face TextMeshPro colors from the prefab.")]
    public bool overrideLabelColor = true;

    [Header("Options")]
    public bool applyOnStart = true;
    public bool includeInactive = true;

    [Header("Geometric edges")]
    public bool EnableGeometricBoxEdges = true;
    public float EdgeThickness = 0.02f;
    public float EdgeOutset = 0.01f;
    public float BottomEdgeLift = 0.005f;
    public Material EdgeMaterial;
    public bool DisableShaderBasedEdgeHighlight = true;
    public bool LogEdgeValidationWarnings = false;

    public enum VisualMode
    {
        ColorAndLabel,
        ColorOnly,
        LabelOnly
    }

    [Header("Visual mode")]
    public VisualMode visualMode = VisualMode.ColorAndLabel;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static Material fallbackEdgeMaterial;
    private static Mesh edgeCubeMesh;

    private void Reset()
    {
        if (targetRenderer == null)
            targetRenderer = FindPrimaryRenderer();
    }

    private void Start()
    {
        if (applyOnStart)
            ApplyVisuals(runtime: true);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (Application.isPlaying)
            return;

        if (!CanRunEditorValidation())
            return;

        EditorApplication.delayCall += () =>
        {
            if (this == null || Application.isPlaying)
                return;

            if (!CanRunEditorValidation())
                return;

            ApplyVisuals(runtime: false);
        };
    }
#endif

    public void ApplyVisuals()
    {
        ApplyVisuals(runtime: Application.isPlaying);
    }

    [ContextMenu("Rebuild Geometric Box Edges")]
    private void RebuildGeometricBoxEdges()
    {
        if (targetRenderer == null)
            targetRenderer = FindPrimaryRenderer();

        ApplyGeometricEdges();
    }

    private void ApplyVisuals(bool runtime)
    {
        BoxMetadata metadata = GetComponent<BoxMetadata>();
        if (metadata == null)
        {
            Debug.LogWarning($"BoxVisualSync: no se encontró BoxMetadata en {name}", this);
            return;
        }

        if (targetRenderer == null)
            targetRenderer = FindPrimaryRenderer();

        string labelText = ResolveLabelText(metadata);
        Color labelColor = GetLabelColor(metadata.boxType);
        Color cubeColor = GetBoxColor(metadata.boxType);

        TextMeshPro[] labels = GetComponentsInChildren<TextMeshPro>(includeInactive);
        foreach (TextMeshPro label in labels)
        {
            if (label == null)
                continue;

            ApplyLabelVisuals(label, labelText, labelColor);
        }

        if (targetRenderer == null)
        {
            SetEdgeFrameActive(false);
            return;
        }

#if UNITY_EDITOR
        // Si estamos editando el prefab asset directamente en Project,
        // no se debe acceder a renderer.material.
        bool isPersistentObject = EditorUtility.IsPersistent(targetRenderer);
        if (!runtime && isPersistentObject)
        {
            return;
        }
#endif

        Material mat = targetRenderer.sharedMaterial;

        if (mat == null)
            return;

        DisableShaderBasedHighlightIfNeeded(mat);

        bool showColor = (visualMode != VisualMode.LabelOnly);
        Color finalColor = showColor ? cubeColor : new Color(0.85f, 0.85f, 0.85f);

        if (!TryApplyRendererColor(targetRenderer, mat, finalColor, runtime))
        {
            Debug.LogWarning(
                $"BoxVisualSync: el material de {targetRenderer.name} no tiene ni _BaseColor ni _Color.",
                targetRenderer
            );
        }

        ApplyGeometricEdges();
    }

    private void ApplyLabelVisuals(TextMeshPro label, string labelText, Color labelColor)
    {
        if (label == null)
            return;

        bool showLabels = visualMode != VisualMode.ColorOnly;
        label.gameObject.SetActive(showLabels);
        if (!showLabels)
            return;

        label.text = labelText ?? string.Empty;
        if (overrideLabelColor)
        {
            label.color = labelColor;
        }

        if (!overrideExistingLabelStyle)
            return;

        if (labelFont != null)
            label.font = labelFont;

        label.fontSize = Mathf.Max(0.01f, labelFontSize);
        label.fontStyle = labelFontStyle;
        label.horizontalAlignment = horizontalAlignment;
        label.verticalAlignment = verticalAlignment;
        label.margin = labelMargins;
        label.textWrappingMode = enableWordWrapping ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        label.overflowMode = labelOverflowMode;
        label.characterSpacing = characterSpacing;
        label.wordSpacing = wordSpacing;
        label.outlineColor = labelOutlineColor;
        label.outlineWidth = Mathf.Clamp01(labelOutlineWidth);

        RectTransform rect = label.rectTransform;
        if (rect != null)
        {
            rect.sizeDelta = new Vector2(
                Mathf.Max(0.001f, labelRectSize.x),
                Mathf.Max(0.001f, labelRectSize.y));
        }
    }

    private static string ResolveLabelText(BoxMetadata metadata)
    {
        if (metadata == null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(metadata.displayLabel))
            return metadata.displayLabel;

        if (!string.IsNullOrWhiteSpace(metadata.spokenLabel))
            return metadata.spokenLabel;

        if (!string.IsNullOrWhiteSpace(metadata.voiceAlias))
            return metadata.voiceAlias;

        return metadata.boxType.ToString();
    }

    private static bool TryApplyRendererColor(Renderer renderer, Material mat, Color color, bool runtime)
    {
        if (renderer == null || mat == null)
            return false;

        if (mat.HasProperty(BaseColorId))
        {
            if (runtime)
            {
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                block.SetColor(BaseColorId, color);
                renderer.SetPropertyBlock(block);
            }
            else
            {
                mat.SetColor(BaseColorId, color);
            }

            return true;
        }

        if (mat.HasProperty(ColorId))
        {
            if (runtime)
            {
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                block.SetColor(ColorId, color);
                renderer.SetPropertyBlock(block);
            }
            else
            {
                mat.SetColor(ColorId, color);
            }

            return true;
        }

        return false;
    }

    private void ApplyGeometricEdges()
    {
        if (!EnableGeometricBoxEdges)
        {
            SetEdgeFrameActive(false);
            return;
        }

        if (targetRenderer == null)
            return;

        if (!CanMutateEdgeHierarchy(targetRenderer.transform))
            return;

        Transform frame = GetOrCreateEdgeFrame(targetRenderer.transform);
        if (frame == null)
            return;

        frame.gameObject.SetActive(true);
        frame.localPosition = Vector3.zero;
        frame.localRotation = Quaternion.identity;
        frame.localScale = Vector3.one;

        Bounds bounds = GetCoreBoxBounds();
        float thickness = Mathf.Max(EdgeThickness, 0.0001f);
        float outset = Mathf.Max(EdgeOutset, 0f);
        float bottomLift = Mathf.Max(BottomEdgeLift, 0f);

        Vector3 center = bounds.center;
        Vector3 size = bounds.size;
        Vector3 extents = bounds.extents;

        int index = 0;

        for (int ySign = -1; ySign <= 1; ySign += 2)
        {
            for (int zSign = -1; zSign <= 1; zSign += 2)
            {
                float y = ySign * (extents.y + outset);
                if (ySign < 0)
                    y += bottomLift;

                Vector3 position = center + new Vector3(0f, y, zSign * (extents.z + outset));
                Vector3 scale = new Vector3(size.x + outset * 2f, thickness, thickness);
                ConfigureEdge(frame, index++, position, scale);
            }
        }

        for (int xSign = -1; xSign <= 1; xSign += 2)
        {
            for (int zSign = -1; zSign <= 1; zSign += 2)
            {
                Vector3 position = center + new Vector3(xSign * (extents.x + outset), bottomLift, zSign * (extents.z + outset));
                Vector3 scale = new Vector3(thickness, size.y + outset * 2f, thickness);
                ConfigureEdge(frame, index++, position, scale);
            }
        }

        for (int xSign = -1; xSign <= 1; xSign += 2)
        {
            for (int ySign = -1; ySign <= 1; ySign += 2)
            {
                float y = ySign * (extents.y + outset);
                if (ySign < 0)
                    y += bottomLift;

                Vector3 position = center + new Vector3(xSign * (extents.x + outset), y, 0f);
                Vector3 scale = new Vector3(thickness, thickness, size.z + outset * 2f);
                ConfigureEdge(frame, index++, position, scale);
            }
        }

        DisableExtraManagedEdges(frame, index);
    }

    private Renderer FindPrimaryRenderer()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive);
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
                continue;

            if (renderer.GetComponent<TextMeshPro>() != null)
                continue;

            if (IsInsideEdgeFrame(renderer.transform))
                continue;

            return renderer;
        }

        return null;
    }

    private bool IsInsideEdgeFrame(Transform candidate)
    {
        Transform current = candidate;
        while (current != null && current != transform)
        {
            if (current.name == EdgeFrameName)
                return true;

            current = current.parent;
        }

        return false;
    }

    private void DisableShaderBasedHighlightIfNeeded(Material mat)
    {
        if (!DisableShaderBasedEdgeHighlight || mat.shader == null || mat.shader.name != ShaderBasedEdgeHighlight)
            return;

        Shader shader = Shader.Find(DefaultEdgeMaterialShader);
        if (shader != null)
            mat.shader = shader;
    }

    private Transform GetOrCreateEdgeFrame(Transform parent)
    {
        if (!CanMutateEdgeHierarchy(parent))
            return null;

        Transform frame = parent.Find(EdgeFrameName);
        if (frame != null)
            return frame;

        GameObject frameObject = new GameObject(EdgeFrameName);
        frameObject.transform.SetParent(parent, false);
        return frameObject.transform;
    }

    private void ConfigureEdge(Transform frame, int index, Vector3 localPosition, Vector3 localScale)
    {
        Transform edge = GetOrCreateEdge(frame, index);
        if (edge == null)
            return;

        edge.gameObject.SetActive(true);
        edge.localPosition = localPosition;
        edge.localRotation = Quaternion.identity;
        edge.localScale = localScale;
        RemoveEdgeColliderIfPresent(edge.gameObject);

        MeshFilter meshFilter = edge.GetComponent<MeshFilter>();
        if (meshFilter == null)
            meshFilter = edge.gameObject.AddComponent<MeshFilter>();

        meshFilter.sharedMesh = EnsureEdgeCubeMesh();

        MeshRenderer renderer = edge.GetComponent<MeshRenderer>();
        if (renderer == null)
            renderer = edge.gameObject.AddComponent<MeshRenderer>();

        renderer.enabled = true;
        renderer.sharedMaterial = GetEdgeMaterial();

        ValidateEdge(edge, meshFilter, renderer);
    }

    private Transform GetOrCreateEdge(Transform frame, int index)
    {
        if (!CanMutateEdgeHierarchy(frame))
            return null;

        string edgeName = $"Edge_{index:00}";
        Transform edge = frame.Find(edgeName);
        if (edge != null)
            return edge;

        GameObject edgeObject = new GameObject(edgeName);
        edgeObject.name = edgeName;
        edgeObject.transform.SetParent(frame, false);

        MeshFilter meshFilter = edgeObject.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = EnsureEdgeCubeMesh();

        edgeObject.AddComponent<MeshRenderer>();

        return edgeObject.transform;
    }

    private void RemoveEdgeColliderIfPresent(GameObject edgeObject)
    {
        Collider collider = edgeObject.GetComponent<Collider>();
        if (collider == null)
            return;

        if (Application.isPlaying)
        {
            Destroy(collider);
            return;
        }

#if UNITY_EDITOR
        EditorApplication.delayCall += () =>
        {
            if (collider != null)
                DestroyImmediate(collider);
        };
#endif
    }

    private Mesh EnsureEdgeCubeMesh()
    {
        if (edgeCubeMesh != null)
            return edgeCubeMesh;

        edgeCubeMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        if (edgeCubeMesh != null)
            return edgeCubeMesh;

        Mesh mesh = new Mesh
        {
            name = "BoxEdgeFrame_CubeMesh"
        };

        Vector3[] vertices =
        {
            new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
            new Vector3(0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
            new Vector3(0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f),
            new Vector3(-0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, 0.5f), new Vector3(-0.5f, -0.5f, 0.5f)
        };

        int[] triangles =
        {
            0, 1, 2, 0, 2, 3,
            4, 5, 6, 4, 6, 7,
            8, 9, 10, 8, 10, 11,
            12, 13, 14, 12, 14, 15,
            16, 17, 18, 16, 18, 19,
            20, 21, 22, 20, 22, 23
        };

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        edgeCubeMesh = mesh;
        return edgeCubeMesh;
    }

    private void ValidateEdge(Transform edge, MeshFilter meshFilter, MeshRenderer renderer)
    {
        if (!LogEdgeValidationWarnings)
            return;

        if (meshFilter == null)
            Debug.LogWarning($"BoxVisualSync: {edge.name} no tiene MeshFilter.", edge);
        else if (meshFilter.sharedMesh == null)
            Debug.LogWarning($"BoxVisualSync: {edge.name} tiene MeshFilter sin sharedMesh.", edge);

        if (renderer == null)
            Debug.LogWarning($"BoxVisualSync: {edge.name} no tiene MeshRenderer.", edge);
        else if (renderer.sharedMaterial == null)
            Debug.LogWarning($"BoxVisualSync: {edge.name} tiene MeshRenderer sin sharedMaterial.", edge);

        if (edge.localScale.x == 0f || edge.localScale.y == 0f || edge.localScale.z == 0f)
            Debug.LogWarning($"BoxVisualSync: {edge.name} tiene escala local cero: {edge.localScale}.", edge);
    }

    private Material GetEdgeMaterial()
    {
        if (EdgeMaterial != null)
            return EdgeMaterial;

        if (fallbackEdgeMaterial == null)
        {
            Shader shader = Shader.Find(DefaultEdgeMaterialShader);
            fallbackEdgeMaterial = new Material(shader != null ? shader : Shader.Find("Standard"))
            {
                name = "BoxEdgeFrame_Black_Runtime"
            };
            fallbackEdgeMaterial.color = Color.black;

            if (fallbackEdgeMaterial.HasProperty(BaseColorId))
                fallbackEdgeMaterial.SetColor(BaseColorId, Color.black);
        }

        return fallbackEdgeMaterial;
    }

    private Bounds GetCoreBoxBounds()
    {
        BoxCollider boxCollider = GetComponent<BoxCollider>();
        if (boxCollider != null)
            return new Bounds(boxCollider.center, boxCollider.size);

        MeshFilter meshFilter = targetRenderer.GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != null)
            return meshFilter.sharedMesh.bounds;

        Vector3 localCenter = targetRenderer.transform.InverseTransformPoint(targetRenderer.bounds.center);
        Vector3 localSize = targetRenderer.transform.InverseTransformVector(targetRenderer.bounds.size);
        localSize = new Vector3(Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z));
        return new Bounds(localCenter, localSize);
    }

    private void SetEdgeFrameActive(bool active)
    {
        if (targetRenderer == null)
            return;

        if (!CanMutateEdgeHierarchy(targetRenderer.transform))
            return;

        Transform frame = targetRenderer.transform.Find(EdgeFrameName);
        if (frame != null)
            frame.gameObject.SetActive(active);
    }

    private bool CanRunEditorValidation()
    {
#if UNITY_EDITOR
        if (this == null || gameObject == null)
            return false;

        if (!gameObject.scene.IsValid())
            return false;

        if (EditorUtility.IsPersistent(this) ||
            EditorUtility.IsPersistent(gameObject) ||
            PrefabUtility.IsPartOfPrefabAsset(gameObject) ||
            PrefabStageUtility.GetPrefabStage(gameObject) != null)
        {
            return false;
        }
#endif

        return true;
    }

    private bool CanMutateEdgeHierarchy(Transform parent)
    {
#if UNITY_EDITOR
        if (Application.isPlaying)
            return true;

        if (this == null || gameObject == null || parent == null || parent.gameObject == null)
            return false;

        if (!gameObject.scene.IsValid() || !parent.gameObject.scene.IsValid())
            return false;

        if (EditorUtility.IsPersistent(this) ||
            EditorUtility.IsPersistent(gameObject) ||
            EditorUtility.IsPersistent(parent) ||
            EditorUtility.IsPersistent(parent.gameObject) ||
            PrefabUtility.IsPartOfPrefabAsset(gameObject) ||
            PrefabUtility.IsPartOfPrefabAsset(parent.gameObject) ||
            PrefabStageUtility.GetPrefabStage(gameObject) != null ||
            PrefabStageUtility.GetPrefabStage(parent.gameObject) != null)
        {
            return false;
        }
#endif

        return true;
    }

    private void DisableExtraManagedEdges(Transform frame, int usedCount)
    {
        for (int i = usedCount; i < EdgeCount + 8; i++)
        {
            Transform extra = frame.Find($"Edge_{i:00}");
            if (extra != null)
                extra.gameObject.SetActive(false);
        }
    }

    private Color GetLabelColor(BoxMetadata.BoxType boxType)
    {
        switch (boxType)
        {
            case BoxMetadata.BoxType.A: return labelColorA;
            case BoxMetadata.BoxType.B: return labelColorB;
            case BoxMetadata.BoxType.C: return labelColorC;
            default: return Color.white;
        }
    }

    private Color GetBoxColor(BoxMetadata.BoxType boxType)
    {
        switch (boxType)
        {
            case BoxMetadata.BoxType.A: return boxColorA;
            case BoxMetadata.BoxType.B: return boxColorB;
            case BoxMetadata.BoxType.C: return boxColorC;
            default: return Color.white;
        }
    }
}
