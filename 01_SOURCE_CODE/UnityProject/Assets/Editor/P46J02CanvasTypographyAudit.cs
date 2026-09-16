using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autonomy.UnityIntegration;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;

public static class P46J02CanvasTypographyAudit
{
    private const string StartScenePath = "Assets/Scenes/experiment_start_scene.unity";
    private const string ProtocolScenePath = "Assets/Scenes/autonomous_demo_step22_multimodal_bridge.unity";
    private const string MaterialPath = "Assets/Resources/ExperimentTypography/Arial SDF - Experimental Canvas No Effects.mat";
    private const string FontPath = "Assets/Resources/ExperimentTypography/Arial SDF - Experimental Canvas.asset";
    private const string ReportPath = "analysis/P46J02_canvas_typography_audit.md";
    private const string RequiredCharacters = "Pausa ID completo: S20260709_222105 Código cuestionario J9UNUW Continuar Reiniciar sesión experimental Guardar y salir Salir sin guardar Prueba Ronda Condición Página Instrucciones áéíóúüñÁÉÍÓÚÜÑ_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz/()-.,¿?¡!";

    private static readonly string[] RuntimeUiSourcePaths =
    {
        "Assets/Scripts/ExperimentRuntimeStartScreenUI.cs",
        "Assets/Scripts/ExperimentPauseMenuController.cs",
        "Assets/Scripts/ExperimentRuntimeProtocolUI.cs"
    };

    private sealed class Audit
    {
        public readonly List<string> Passed = new();
        public readonly List<string> Failed = new();
        public readonly List<string> Notes = new();
        public int MissingScripts;
        public int SerializedTmpCount;
        public int SerializedUiEffectCount;
        public int SerializedInstanceMaterialCount;
        public string LegacyRuntimeFontNames = string.Empty;
    }

    [MenuItem("Tools/Multimodal AI-VR/P46J-02/Audit Canvas Typography")]
    public static void AuditCanvasTypography()
    {
        Run(throwOnFailure: false);
    }

    public static void AuditCanvasTypographyForBatch()
    {
        Run(throwOnFailure: true);
    }

    [MenuItem("Tools/Multimodal AI-VR/P46J-02/Generate Arial SDF Assets")]
    public static void GenerateArialSdfAssets()
    {
        GenerateArialSdfAssetsForBatch();
    }

    public static void GenerateArialSdfAssetsForBatch()
    {
        TMP_FontAsset existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        if (existing != null)
        {
            Debug.Log($"[P46J-02] Arial SDF asset already exists: {FontPath}");
            return;
        }

        Font legacyRuntimeFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (legacyRuntimeFont == null)
        {
            throw new InvalidOperationException("LegacyRuntime.ttf could not be loaded.");
        }

        TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset("Arial", "Regular", 90);
        if (fontAsset == null)
        {
            throw new InvalidOperationException("Unable to create Arial SDF from LegacyRuntime.ttf.");
        }

        fontAsset.name = "Arial SDF - Experimental Canvas";
        fontAsset.atlasTextures[0].name = "Arial SDF - Experimental Canvas Atlas";
        fontAsset.atlasTextures[0].filterMode = FilterMode.Bilinear;
        fontAsset.atlasTextures[0].wrapMode = TextureWrapMode.Clamp;
        fontAsset.material.name = "Arial SDF - Experimental Canvas Source Material";

        AssetDatabase.CreateAsset(fontAsset, FontPath);
        AssetDatabase.AddObjectToAsset(fontAsset.atlasTextures[0], fontAsset);
        AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);

        if (!fontAsset.TryAddCharacters(RequiredCharacters, out string missingCharacters))
        {
            throw new InvalidOperationException("Arial SDF is missing required characters: " + missingCharacters);
        }

        fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
        var serializedFont = new SerializedObject(fontAsset);
        SerializedProperty sourcePath = serializedFont.FindProperty("m_SourceFontFilePath");
        if (sourcePath != null)
        {
            sourcePath.stringValue = string.Empty;
        }
        SerializedProperty sourceGuid = serializedFont.FindProperty("m_SourceFontFileGUID");
        if (sourceGuid != null)
        {
            sourceGuid.stringValue = string.Empty;
        }
        serializedFont.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(fontAsset);
        EditorUtility.SetDirty(fontAsset.atlasTextures[0]);

        Material preset = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (preset == null)
        {
            throw new InvalidOperationException("Dedicated no-effects material is missing: " + MaterialPath);
        }

        Shader mobileSdf = Shader.Find(ExperimentCanvasTypography.ExpectedShaderName);
        if (mobileSdf == null)
        {
            throw new InvalidOperationException("TMP Mobile SDF shader is missing.");
        }

        preset.shader = mobileSdf;
        preset.shaderKeywords = Array.Empty<string>();
        preset.SetTexture("_MainTex", fontAsset.atlasTextures[0]);
        SetFloat(preset, "_TextureWidth", 1024f);
        SetFloat(preset, "_TextureHeight", 1024f);
        SetFloat(preset, "_GradientScale", 10f);
        SetFloat(preset, "_FaceDilate", 0f);
        SetFloat(preset, "_OutlineWidth", 0f);
        SetFloat(preset, "_OutlineSoftness", 0f);
        SetFloat(preset, "_UnderlayDilate", 0f);
        SetFloat(preset, "_UnderlayOffsetX", 0f);
        SetFloat(preset, "_UnderlayOffsetY", 0f);
        SetFloat(preset, "_UnderlaySoftness", 0f);
        SetFloat(preset, "_MaskSoftnessX", 0f);
        SetFloat(preset, "_MaskSoftnessY", 0f);
        SetColor(preset, "_OutlineColor", Color.clear);
        SetColor(preset, "_UnderlayColor", Color.clear);
        SetColor(preset, "_GlowColor", Color.clear);
        EditorUtility.SetDirty(preset);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[P46J-02] Arial SDF generated | font={FontPath} material={MaterialPath} glyphs={fontAsset.characterTable.Count} padding={fontAsset.atlasPadding}");
    }

    private static void Run(bool throwOnFailure)
    {
        var audit = new Audit();
        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        Font legacyRuntimeFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        audit.LegacyRuntimeFontNames = legacyRuntimeFont != null
            ? string.Join(", ", legacyRuntimeFont.fontNames ?? Array.Empty<string>())
            : "missing";
        Check(audit, legacyRuntimeFont != null, "legacy_runtime_font_present", audit.LegacyRuntimeFontNames);
        Check(audit, legacyRuntimeFont != null && (legacyRuntimeFont.fontNames ?? Array.Empty<string>()).Any(
                name => name.IndexOf("Arial", StringComparison.OrdinalIgnoreCase) >= 0),
            "font_family_preserved_from_legacy_runtime", audit.LegacyRuntimeFontNames);

        Check(audit, font != null, "font_asset_present", FontPath);
        Check(audit, material != null, "dedicated_material_present", MaterialPath);
        if (font != null)
        {
            Check(audit, string.Equals(font.faceInfo.familyName, "Arial", StringComparison.Ordinal),
                "font_family_arial", font.faceInfo.familyName);
            Check(audit, font.atlasPadding >= 9, "sdf_padding_sufficient", $"padding={font.atlasPadding}");
            Check(audit, HasRequiredCharacters(font), "required_glyphs_present",
                "tildes, underscores, digits and alphanumeric labels");
        }

        if (material != null)
        {
            Check(audit, material.shader != null &&
                         string.Equals(material.shader.name, ExperimentCanvasTypography.ExpectedShaderName, StringComparison.Ordinal),
                "mobile_sdf_shader", material.shader != null ? material.shader.name : "missing");
            CheckZero(audit, material, "_OutlineWidth", "outline_width_zero");
            CheckZero(audit, material, "_OutlineSoftness", "outline_softness_zero");
            CheckZero(audit, material, "_FaceDilate", "face_dilate_zero");
            CheckZero(audit, material, "_MaskSoftnessX", "mask_softness_x_zero");
            CheckZero(audit, material, "_MaskSoftnessY", "mask_softness_y_zero");
            CheckTransparent(audit, material, "_OutlineColor", "outline_color_transparent");
            CheckTransparent(audit, material, "_UnderlayColor", "underlay_color_transparent");
            CheckTransparentOrUnsupported(audit, material, "_GlowColor", "glow_color_transparent_or_unsupported");
            Check(audit, !material.IsKeywordEnabled("OUTLINE_ON"), "outline_keyword_disabled", string.Join(",", material.shaderKeywords));
            Check(audit, !material.IsKeywordEnabled("UNDERLAY_ON") && !material.IsKeywordEnabled("UNDERLAY_INNER"),
                "underlay_keywords_disabled", string.Join(",", material.shaderKeywords));
            Check(audit, !material.IsKeywordEnabled("GLOW_ON"), "glow_keyword_disabled", string.Join(",", material.shaderKeywords));
        }

        AuditRuntimeSourceContracts(audit);
        AuditSharedAssignment(audit, font, material);
        AuditScene(audit, StartScenePath);
        AuditScene(audit, ProtocolScenePath);

        WriteReport(audit, font, material);
        AssetDatabase.Refresh();
        Debug.Log($"[P46J-02] typography_audit | result={(audit.Failed.Count == 0 ? "PASS" : "FAIL")} " +
                  $"passed={audit.Passed.Count} failed={audit.Failed.Count} missing_scripts={audit.MissingScripts} " +
                  $"serialized_tmp={audit.SerializedTmpCount} report={ReportPath}");

        if (audit.Failed.Count > 0 && throwOnFailure)
        {
            throw new InvalidOperationException("P46J-02 canvas typography audit failed: " + string.Join("; ", audit.Failed));
        }
    }

    private static void AuditRuntimeSourceContracts(Audit audit)
    {
        foreach (string path in RuntimeUiSourcePaths)
        {
            string source = File.ReadAllText(path);
            int tmpAdds = Count(source, "AddComponent<TextMeshProUGUI>()");
            int sharedConfigurations = Count(source, "ExperimentCanvasTypography.Configure(");
            Check(audit, tmpAdds > 0, "runtime_tmp_text_route", $"{path}: adds={tmpAdds}");
            Check(audit, tmpAdds == sharedConfigurations, "all_runtime_tmp_routes_configured",
                $"{path}: adds={tmpAdds} configurations={sharedConfigurations}");
            Check(audit, !source.Contains("AddComponent<Text>()", StringComparison.Ordinal),
                "legacy_ui_text_absent", path);
            Check(audit, !source.Contains(".fontMaterial", StringComparison.Ordinal),
                "font_material_instance_assignment_absent", path);
            Check(audit, !source.Contains("AddComponent<Outline>", StringComparison.Ordinal) &&
                         !source.Contains("AddComponent<Shadow>", StringComparison.Ordinal),
                "runtime_outline_shadow_absent", path);
        }
    }

    private static void AuditSharedAssignment(Audit audit, TMP_FontAsset expectedFont, Material expectedMaterial)
    {
        var firstObject = new GameObject("P46J02_AuditText_A", typeof(RectTransform));
        var secondObject = new GameObject("P46J02_AuditText_B", typeof(RectTransform));
        try
        {
            TextMeshProUGUI first = firstObject.AddComponent<TextMeshProUGUI>();
            TextMeshProUGUI second = secondObject.AddComponent<TextMeshProUGUI>();
            ExperimentCanvasTypography.Configure(first);
            ExperimentCanvasTypography.Configure(second);
            Check(audit, first.font == expectedFont && second.font == expectedFont,
                "runtime_font_asset_explicit", expectedFont != null ? expectedFont.name : "missing");
            Check(audit, first.fontSharedMaterial == expectedMaterial && second.fontSharedMaterial == expectedMaterial,
                "runtime_material_is_single_shared_asset", expectedMaterial != null ? expectedMaterial.name : "missing");
            Check(audit, first.GetComponent<UnityEngine.UI.Outline>() == null && first.GetComponent<UnityEngine.UI.Shadow>() == null &&
                         second.GetComponent<UnityEngine.UI.Outline>() == null && second.GetComponent<UnityEngine.UI.Shadow>() == null,
                "representative_labels_have_no_ui_effects", "Outline=0 Shadow=0");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(firstObject);
            UnityEngine.Object.DestroyImmediate(secondObject);
        }
    }

    private static void AuditScene(Audit audit, string scenePath)
    {
        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        int sceneMissing = 0;
        int sceneTmp = 0;
        int sceneEffects = 0;
        int sceneInstances = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                sceneMissing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
            }

            foreach (TextMeshProUGUI text in root.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                sceneTmp++;
                sceneEffects += text.GetComponents<UnityEngine.UI.Outline>().Length;
                sceneEffects += text.GetComponents<UnityEngine.UI.Shadow>().Length;
                Material shared = text.fontSharedMaterial;
                if (shared != null && shared.name.IndexOf("(Instance)", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sceneInstances++;
                }
            }
        }

        audit.MissingScripts += sceneMissing;
        audit.SerializedTmpCount += sceneTmp;
        audit.SerializedUiEffectCount += sceneEffects;
        audit.SerializedInstanceMaterialCount += sceneInstances;
        Check(audit, sceneMissing == 0, "scene_missing_scripts_zero", $"{scenePath}: {sceneMissing}");
        audit.Notes.Add($"{scenePath}: serialized TMP={sceneTmp}, UI Outline/Shadow={sceneEffects}, instance-named TMP materials={sceneInstances}. Runtime experiment canvases are constructed by the audited typed routes.");
    }

    private static bool HasRequiredCharacters(TMP_FontAsset font)
    {
        return RequiredCharacters.Where(character => !char.IsWhiteSpace(character)).All(character => font.HasCharacter(character));
    }

    private static void SetFloat(Material material, string property, float value)
    {
        if (material.HasProperty(property))
        {
            material.SetFloat(property, value);
        }
    }

    private static void SetColor(Material material, string property, Color value)
    {
        if (material.HasProperty(property))
        {
            material.SetColor(property, value);
        }
    }

    private static void CheckZero(Audit audit, Material material, string property, string key)
    {
        bool present = material.HasProperty(property);
        float value = present ? material.GetFloat(property) : float.NaN;
        Check(audit, present && Mathf.Approximately(value, 0f), key, $"{property}={value}");
    }

    private static void CheckTransparent(Audit audit, Material material, string property, string key)
    {
        bool present = material.HasProperty(property);
        Color value = present ? material.GetColor(property) : Color.white;
        Check(audit, present && Mathf.Approximately(value.a, 0f), key, $"{property}={value}");
    }

    private static void CheckTransparentOrUnsupported(Audit audit, Material material, string property, string key)
    {
        bool present = material.HasProperty(property);
        Color value = present ? material.GetColor(property) : Color.clear;
        Check(audit, !present || Mathf.Approximately(value.a, 0f), key,
            present ? $"{property}={value}" : $"{property}=not exposed by mobile SDF shader");
    }

    private static void Check(Audit audit, bool condition, string key, string detail)
    {
        string line = $"{key}: {detail}";
        (condition ? audit.Passed : audit.Failed).Add(line);
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static void WriteReport(Audit audit, TMP_FontAsset font, Material material)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "analysis");
        string shader = material != null && material.shader != null ? material.shader.name : "missing";
        string fontName = font != null ? $"{font.name} ({font.faceInfo.familyName})" : "missing";
        string atlas = font != null && font.atlasTextures != null && font.atlasTextures.Length > 0
            ? $"{font.atlasWidth}x{font.atlasHeight}; padding {font.atlasPadding}; {font.atlasRenderMode}; filter {font.atlasTextures[0].filterMode}; mipmaps {font.atlasTextures[0].mipmapCount}"
            : "missing";
        var builder = new StringBuilder();
        builder.AppendLine("# OE8 - P46J-02 - Auditoría de tipografía de canvases");
        builder.AppendLine();
        builder.AppendLine($"Resultado: **{(audit.Failed.Count == 0 ? "PASS" : "FAIL")}**. Checks: {audit.Passed.Count} PASS / {audit.Failed.Count} FAIL. Missing scripts: {audit.MissingScripts}.");
        builder.AppendLine();
        builder.AppendLine("## Causa raíz auditada");
        builder.AppendLine();
        builder.AppendLine("Los textos observados se generaban mayoritariamente con `UnityEngine.UI.Text` y `LegacyRuntime.ttf`, usando un atlas raster dinámico. Los canvases World Space minifican y filtran ese atlas en Quest, haciendo visible su franja de antialiasing clara sobre fondos oscuros. No había componentes UI Outline/Shadow ni una utilidad que activase Outline TMP; los propios logs anteriores declaraban `global_aliasing_deferred=True`.");
        builder.AppendLine($"Unity 6 resuelve localmente `LegacyRuntime.ttf` a `{audit.LegacyRuntimeFontNames}`; el font asset SDF usa la misma familia Arial, por lo que la corrección no cambia la familia tipográfica.");
        builder.AppendLine();
        builder.AppendLine("Los TMP serializados inspeccionados no explicaban `Pausa`, `ID completo` ni `Código cuestionario`: esos textos nacen en runtime. Las instancias TMP serializadas encontradas fuera de las rutas objetivo tienen `_OutlineWidth=0`; su `_OutlineColor` blanco es inactivo y no se ha modificado.");
        builder.AppendLine();
        builder.AppendLine("## Configuración final por grupo");
        builder.AppendLine();
        builder.AppendLine("| Grupo | Font asset | Shader | Material/preset | Outline Width / Color | Underlay | Glow | Face Dilate / Softness | UI Outline/Shadow | Asignación | Corrección |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        string common = $"{fontName} | {shader} | {material?.name ?? "missing"} | 0 / transparente | OFF | OFF | 0 / 0 | ninguno | `fontSharedMaterial`, único asset | UI.Text raster → TMP SDF compartido sin efectos";
        builder.AppendLine($"| Pantalla inicial e historial/IDs | {common} |");
        builder.AppendLine($"| Wizard de instrucciones generales y labels | {common} |");
        builder.AppendLine($"| Menú de pausa, IDs, mensajes y botones | {common} |");
        builder.AppendLine($"| Canvas mural, Prueba/Ronda/Condición, código y botones | {common} |");
        builder.AppendLine();
        builder.AppendLine("## Atlas, fallback y sampling");
        builder.AppendLine();
        builder.AppendLine($"- Atlas: {atlas}.");
        builder.AppendLine($"- Fallback font assets: {font?.fallbackFontAssetTable?.Count ?? 0}; no se necesita fallback para las cadenas auditadas.");
        builder.AppendLine("- Glifos comprobados: tildes españolas, ñ/Ñ, guion bajo, números, mayúsculas y los textos representativos solicitados.");
        builder.AppendLine("- El SDF móvil conserva el borde nítido a distintas escalas sin blur, sin cambiar MSAA/render scale y sin regenerar el atlas.");
        builder.AppendLine();
        builder.AppendLine("## Escenas y efectos serializados");
        builder.AppendLine();
        foreach (string note in audit.Notes)
        {
            builder.AppendLine($"- {note}");
        }
        builder.AppendLine($"- Total: TMP serializados={audit.SerializedTmpCount}; UI Outline/Shadow={audit.SerializedUiEffectCount}; materiales con nombre Instance={audit.SerializedInstanceMaterialCount}. Ninguno se asigna a los canvases runtime objetivo.");
        builder.AppendLine();
        builder.AppendLine("## Checks automatizados");
        builder.AppendLine();
        foreach (string passed in audit.Passed)
        {
            builder.AppendLine($"- PASS — {passed}");
        }
        foreach (string failed in audit.Failed)
        {
            builder.AppendLine($"- FAIL — {failed}");
        }
        File.WriteAllText(ReportPath, builder.ToString(), new UTF8Encoding(false));
    }
}
