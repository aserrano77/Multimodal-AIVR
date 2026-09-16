using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autonomy.UnityIntegration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class P46J03CanvasButtonPaletteAudit
{
    private const string ScenePath = "Assets/Scenes/autonomous_demo_step22_multimodal_bridge.unity";
    private const string UtilityPath = "Assets/Scripts/Autonomy/UnityIntegration/ExperimentRuntimeButtonUtility.cs";
    private const string StartPath = "Assets/Scripts/ExperimentRuntimeStartScreenUI.cs";
    private const string PausePath = "Assets/Scripts/ExperimentPauseMenuController.cs";
    private const string ProtocolPath = "Assets/Scripts/ExperimentRuntimeProtocolUI.cs";
    private const string BoxVisualPath = "Assets/Scripts/BoxVisualSync.cs";
    private const string ReportPath = "analysis/P46J03_canvas_button_palette_audit.md";

    private sealed class Audit
    {
        public readonly List<string> Passed = new();
        public readonly List<string> Failed = new();
        public readonly List<string> Notes = new();
        public int MissingScripts;
    }

    private sealed class Route
    {
        public Route(string group, string label, ExperimentButtonRole role, string path, string anchor)
        {
            Group = group;
            Label = label;
            Role = role;
            Path = path;
            Anchor = anchor;
        }

        public string Group { get; }
        public string Label { get; }
        public ExperimentButtonRole Role { get; }
        public string Path { get; }
        public string Anchor { get; }
    }

    private static readonly Route[] Routes =
    {
        new("Pantalla inicial", "Comenzar sesion experimental", ExperimentButtonRole.Primary, StartPath, "StartPressed, ExperimentButtonRole.Primary"),
        new("Pantalla inicial", "Historial de IDs", ExperimentButtonRole.Secondary, StartPath, "ShowHistoryPressed, ExperimentButtonRole.Secondary"),
        new("Pantalla inicial", "Cerrar aplicacion", ExperimentButtonRole.Destructive, StartPath, "ExitPressed, ExperimentButtonRole.Destructive"),
        new("Recuperacion", "Continuar desde Prueba N", ExperimentButtonRole.Primary, StartPath, "ResumeSavedExitFromCheckpointPressed, ExperimentButtonRole.Primary"),
        new("Recuperacion", "Empezar nuevo intento", ExperimentButtonRole.Warning, StartPath, "StartNewAttemptAfterSavedExitPressed, ExperimentButtonRole.Warning"),
        new("Recuperacion", "Historial", ExperimentButtonRole.Secondary, StartPath, "AddSavedExitPromptButton(\"Historial\", ShowHistoryPressed, ExperimentButtonRole.Secondary"),
        new("Recuperacion", "Salir", ExperimentButtonRole.Destructive, StartPath, "AddSavedExitPromptButton(\"Salir\", ExitPressed, ExperimentButtonRole.Destructive"),
        new("Historial", "Volver", ExperimentButtonRole.Secondary, StartPath, "BackFromHistoryPressed, ExperimentButtonRole.Secondary"),
        new("Historial", "Mas recientes", ExperimentButtonRole.Secondary, StartPath, "PreviousHistoryPagePressed,\n                ExperimentButtonRole.Secondary"),
        new("Historial", "Mas antiguas", ExperimentButtonRole.Primary, StartPath, "NextHistoryPagePressed,\n                ExperimentButtonRole.Primary"),
        new("Wizard general (ruta StartScreen heredada)", "Anterior", ExperimentButtonRole.Secondary, StartPath, "Button_GlobalInstructionsAnterior"),
        new("Wizard general (ruta StartScreen heredada)", "Siguiente", ExperimentButtonRole.Primary, StartPath, "Button_GlobalInstructionsSiguiente"),
        new("Wizard general (ruta StartScreen heredada)", "Entendido / Comenzar", ExperimentButtonRole.Primary, StartPath, "Button_GlobalInstructionsCompletar"),
        new("Wizard general activo", "Anterior", ExperimentButtonRole.Secondary, ProtocolPath, "PreviousRuntimeGlobalInstructionsPagePressed, ExperimentButtonRole.Secondary"),
        new("Wizard general activo", "Siguiente", ExperimentButtonRole.Primary, ProtocolPath, "NextRuntimeGlobalInstructionsPagePressed, ExperimentButtonRole.Primary"),
        new("Wizard general activo", "Entendido / Comenzar", ExperimentButtonRole.Primary, ProtocolPath, "CompleteRuntimeGlobalInstructionsPressed, ExperimentButtonRole.Primary"),
        new("Menu de pausa", "Continuar", ExperimentButtonRole.Primary, PausePath, "Continue, ExperimentButtonRole.Primary"),
        new("Menu de pausa", "Reiniciar sesion experimental", ExperimentButtonRole.Warning, PausePath, "RequestRestart, ExperimentButtonRole.Warning"),
        new("Menu de pausa", "Guardar y volver al inicio", ExperimentButtonRole.Secondary, PausePath, "SaveAndExit, ExperimentButtonRole.Secondary"),
        new("Menu de pausa", "Salir al inicio sin guardar", ExperimentButtonRole.Destructive, PausePath, "RequestExitWithoutCompletion, ExperimentButtonRole.Destructive"),
        new("Confirmacion de reinicio", "Reiniciar sesion", ExperimentButtonRole.Warning, PausePath, "ConfirmRestart, ExperimentButtonRole.Warning"),
        new("Confirmacion de reinicio", "Cancelar", ExperimentButtonRole.Secondary, PausePath, "pause_restart_confirm_cancelled"),
        new("Confirmacion de guardado", "Guardar y volver al inicio", ExperimentButtonRole.Primary, PausePath, "ConfirmSaveAndExit, ExperimentButtonRole.Primary"),
        new("Confirmacion de guardado", "Cancelar", ExperimentButtonRole.Secondary, PausePath, "pause_save_exit_confirm_cancelled"),
        new("Confirmacion destructiva", "Salir al inicio", ExperimentButtonRole.Destructive, PausePath, "ConfirmExitWithoutCompletion, ExperimentButtonRole.Destructive"),
        new("Confirmacion destructiva", "Cancelar", ExperimentButtonRole.Secondary, PausePath, "exit_without_completion_cancelled"),
        new("Canvas mural", "Repetir instrucciones", ExperimentButtonRole.Secondary, ProtocolPath, "PlayInstruction(conditionId, userLabel.Label, forceRestart: true), ExperimentButtonRole.Secondary"),
        new("Canvas mural", "Comenzar la prueba", ExperimentButtonRole.Primary, ProtocolPath, "BeginCondition, ExperimentButtonRole.Primary"),
        new("Canvas mural", "Siguiente ronda", ExperimentButtonRole.Primary, ProtocolPath, "AddButton(\"Siguiente ronda\", BeginCondition, ExperimentButtonRole.Primary"),
        new("Canvas mural", "Siguiente prueba", ExperimentButtonRole.Primary, ProtocolPath, "ExperimentButtonRole.Primary, !snapshot.TrialTransitionBusy"),
        new("Canvas mural", "Finalizar sesion", ExperimentButtonRole.Primary, ProtocolPath, "FinishSession, ExperimentButtonRole.Primary"),
        new("Canvas mural", "Avance no disponible", ExperimentButtonRole.Primary, ProtocolPath, "ExperimentButtonRole.Primary, false"),
        new("Canvas mural / incidencia", "Cerrar ronda por incidencia", ExperimentButtonRole.Warning, ProtocolPath, "ExperimentButtonRole.Warning, snapshot.SessionActive"),
        new("Canvas mural / incidencia", "Confirmar cierre por incidencia", ExperimentButtonRole.Warning, ProtocolPath, "ConfirmTechnicalIncidentClose, ExperimentButtonRole.Warning"),
        new("Canvas mural / incidencia", "Cancelar incidencia", ExperimentButtonRole.Secondary, ProtocolPath, "ExperimentButtonRole.Secondary, true"),
        new("Canvas mural / final", "He anotado el codigo / Finalizar experimento", ExperimentButtonRole.Primary, ProtocolPath, "AcknowledgeFinalCompletion, ExperimentButtonRole.Primary")
    };

    [MenuItem("Tools/Multimodal AI-VR/P46J-03/Audit Canvas Button Palette")]
    public static void AuditCanvasButtonPalette()
    {
        Run(throwOnFailure: false);
    }

    public static void AuditCanvasButtonPaletteForBatch()
    {
        Run(throwOnFailure: true);
    }

    private static void Run(bool throwOnFailure)
    {
        var audit = new Audit();
        AuditPalette(audit);
        AuditRuntimeApplication(audit);
        AuditSources(audit);
        AuditScene(audit);
        WriteReport(audit);

        foreach (string passed in audit.Passed)
        {
            Debug.Log($"[P46J03-AUDIT] PASS | {passed}");
        }

        foreach (string failed in audit.Failed)
        {
            Debug.LogError($"[P46J03-AUDIT] FAIL | {failed}");
        }

        Debug.Log($"[P46J03-AUDIT] RESULT={(audit.Failed.Count == 0 ? "PASS" : "FAIL")} checks={audit.Passed.Count + audit.Failed.Count} passed={audit.Passed.Count} failed={audit.Failed.Count} missing_scripts={audit.MissingScripts} report={ReportPath}");
        if (throwOnFailure && audit.Failed.Count > 0)
        {
            throw new InvalidOperationException($"P46J-03 audit failed with {audit.Failed.Count} checks. See {ReportPath}.");
        }
    }

    private static void AuditPalette(Audit audit)
    {
        CheckRole(audit, ExperimentButtonRole.Primary, "#006EA6", "#0077B6", "#005B8C");
        CheckRole(audit, ExperimentButtonRole.Secondary, "#4B5563", "#5B6470", "#374151");
        CheckRole(audit, ExperimentButtonRole.Warning, "#A85700", "#B85C00", "#7D4200");
        CheckRole(audit, ExperimentButtonRole.Destructive, "#B3264A", "#C12C51", "#8F1E3C");
        Check(audit, ToHex(ExperimentRuntimeButtonUtility.DisabledColor, includeAlpha: true) == "#3F444BA6", "disabled_color_rgba", ToHex(ExperimentRuntimeButtonUtility.DisabledColor, true));
        Check(audit, ExperimentRuntimeButtonUtility.DisabledColor.a >= 0.60f && ExperimentRuntimeButtonUtility.DisabledColor.a <= 0.70f, "disabled_opacity_60_to_70_percent", ExperimentRuntimeButtonUtility.DisabledColor.a.ToString("0.000"));
        Check(audit, Mathf.Approximately(ExperimentRuntimeButtonUtility.ColorMultiplier, 1f), "color_multiplier_constant", ExperimentRuntimeButtonUtility.ColorMultiplier.ToString("0.00"));
        Check(audit, Mathf.Abs(ExperimentRuntimeButtonUtility.FadeDuration - 0.10f) < 0.0001f, "fade_duration_constant", ExperimentRuntimeButtonUtility.FadeDuration.ToString("0.00"));
    }

    private static void CheckRole(Audit audit, ExperimentButtonRole role, string normal, string highlighted, string pressed)
    {
        ColorBlock colors = ExperimentRuntimeButtonUtility.BuildColorBlock(role);
        Check(audit, ToHex(colors.normalColor) == normal, $"{role}_normal", ToHex(colors.normalColor));
        Check(audit, ToHex(colors.highlightedColor) == highlighted, $"{role}_highlighted", ToHex(colors.highlightedColor));
        Check(audit, ToHex(colors.pressedColor) == pressed, $"{role}_pressed", ToHex(colors.pressedColor));
        Check(audit, ToHex(colors.selectedColor) == highlighted, $"{role}_selected_equals_highlighted", ToHex(colors.selectedColor));
        Check(audit, ToHex(colors.disabledColor, true) == "#3F444BA6", $"{role}_disabled", ToHex(colors.disabledColor, true));
        Check(audit, Mathf.Approximately(colors.colorMultiplier, 1f), $"{role}_multiplier", colors.colorMultiplier.ToString("0.00"));
        Check(audit, Mathf.Abs(colors.fadeDuration - 0.10f) < 0.0001f, $"{role}_fade", colors.fadeDuration.ToString("0.00"));
    }

    private static void AuditRuntimeApplication(Audit audit)
    {
        foreach (ExperimentButtonRole role in Enum.GetValues(typeof(ExperimentButtonRole)))
        {
            var target = new GameObject($"P46J03_{role}", typeof(RectTransform), typeof(Image), typeof(Button));
            try
            {
                Image image = target.GetComponent<Image>();
                Button button = target.GetComponent<Button>();
                image.color = new Color(0.25f, 0.50f, 0.75f, 1f);
                Material materialBefore = image.material;
                Navigation navigationBefore = new Navigation { mode = Navigation.Mode.Explicit };
                button.navigation = navigationBefore;
                int invoked = 0;
                int listenerCount = ExperimentRuntimeButtonUtility.ConfigureRuntimeButton(button, image, () => invoked++, role, role != ExperimentButtonRole.Secondary);
                button.onClick.Invoke();

                Check(audit, ToHex(image.color) == "#FFFFFF", $"{role}_base_image_is_white", ToHex(image.color));
                Check(audit, image.material == materialBefore, $"{role}_material_not_instanced", image.material != null ? image.material.name : "default");
                Check(audit, button.targetGraphic == image && image.raycastTarget, $"{role}_target_graphic_and_raycast", $"target={button.targetGraphic?.name} raycast={image.raycastTarget}");
                Check(audit, button.transition == Selectable.Transition.ColorTint, $"{role}_uses_color_tint", button.transition.ToString());
                Check(audit, button.navigation.mode == Navigation.Mode.Explicit, $"{role}_navigation_preserved", button.navigation.mode.ToString());
                Check(audit, listenerCount == 1 && invoked == 1, $"{role}_onclick_preserved", $"listeners={listenerCount} invoked={invoked}");
                Check(audit, button.interactable == (role != ExperimentButtonRole.Secondary), $"{role}_interactable_argument_preserved", button.interactable.ToString());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }

    private static void AuditSources(Audit audit)
    {
        string utility = File.ReadAllText(UtilityPath);
        string start = File.ReadAllText(StartPath);
        string pause = File.ReadAllText(PausePath);
        string protocol = File.ReadAllText(ProtocolPath);
        string controllers = start + "\n" + pause + "\n" + protocol;

        Check(audit, CountRuntimePaletteDefinitions("0x00, 0x6E, 0xA6") == 1, "single_authoritative_primary_palette_definition", UtilityPath);
        Check(audit, CountRuntimePaletteDefinitions("0x4B, 0x55, 0x63") == 1, "single_authoritative_secondary_palette_definition", UtilityPath);
        Check(audit, CountRuntimePaletteDefinitions("0xA8, 0x57, 0x00") == 1, "single_authoritative_warning_palette_definition", UtilityPath);
        Check(audit, CountRuntimePaletteDefinitions("0xB3, 0x26, 0x4A") == 1, "single_authoritative_destructive_palette_definition", UtilityPath);
        Check(audit, utility.Contains("BuildColorBlock(ExperimentButtonRole role)", StringComparison.Ordinal), "shared_colorblock_builder_exists", UtilityPath);
        Check(audit, utility.Contains("image.color = Color.white", StringComparison.Ordinal), "base_image_white_prevents_double_tint", UtilityPath);
        Check(audit, !controllers.Contains("new Color(0.18f, 0.38f, 0.48f", StringComparison.Ordinal) &&
                     !controllers.Contains("new Color(0.52f, 0.30f, 0.12f", StringComparison.Ordinal) &&
                     !controllers.Contains("new Color(0.50f, 0.20f, 0.16f", StringComparison.Ordinal),
            "legacy_button_tints_removed_from_controllers", "blue/orange/red local button tints absent");
        Check(audit, !controllers.Contains("enabledColor", StringComparison.Ordinal), "no_per_controller_color_override_parameters", "enabledColor absent");
        Check(audit, !controllers.Contains("new Material(", StringComparison.Ordinal) && !utility.Contains("new Material(", StringComparison.Ordinal), "no_material_per_button", "new Material absent in target paths");
        Check(audit, !controllers.Contains("fontMaterial", StringComparison.Ordinal) && !controllers.Contains("fontSharedMaterial", StringComparison.Ordinal), "tmp_materials_untouched", "no TMP material assignment in P46J-03 paths");
        Check(audit, !controllers.Contains("Contains(\"Salir", StringComparison.Ordinal) &&
                     !controllers.Contains("Contains(\"Continuar", StringComparison.Ordinal) &&
                     !controllers.Contains("Contains(\"Reiniciar", StringComparison.Ordinal) &&
                     !controllers.Contains("ResolveButtonRole", StringComparison.Ordinal),
            "no_visible_text_semantic_classification", "roles are explicit call-site arguments");

        foreach (Route route in Routes)
        {
            string source = route.Path == StartPath ? start : route.Path == PausePath ? pause : protocol;
            bool found = HasRoleNearAnchor(source, route.Anchor, route.Role);
            Check(audit, found, $"route_{Sanitize(route.Group)}_{Sanitize(route.Label)}", $"{route.Role} | {route.Path} | anchor={route.Anchor}");
        }

        string boxVisual = File.ReadAllText(BoxVisualPath);
        Check(audit, boxVisual.Contains("new Color(0f, 0.678f, 1f);         // #00ADFF", StringComparison.Ordinal), "box_palette_blue_unchanged", "#00ADFF");
        Check(audit, boxVisual.Contains("new Color(0.984f, 1f, 0f);         // #FBFF00", StringComparison.Ordinal), "box_palette_yellow_unchanged", "#FBFF00");
        Check(audit, boxVisual.Contains("new Color(0.812f, 0.204f, 0.463f); // #CF3476", StringComparison.Ordinal), "box_palette_magenta_unchanged", "#CF3476");
        Check(audit, !controllers.Contains("#00ADFF", StringComparison.Ordinal) && !controllers.Contains("#FBFF00", StringComparison.Ordinal) && !controllers.Contains("#CF3476", StringComparison.Ordinal), "box_palette_not_reused_as_button_background", "box hex values absent from controllers");
        audit.Notes.Add("The active general-instructions owner is ExperimentRuntimeProtocolUI; the blocked legacy StartScreen wizard path also uses the shared palette.");
        audit.Notes.Add("No abandon/discard button currently exists on the protocol mural; technical-incident closure is the existing consequential route and is Warning.");
        audit.Notes.Add("FindButtonByLabel remains a functional lookup only; it does not choose a semantic role.");
    }

    private static void AuditScene(Audit audit)
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int missing = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform item in root.GetComponentsInChildren<Transform>(true))
            {
                missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(item.gameObject);
            }
        }

        audit.MissingScripts = missing;
        Check(audit, missing == 0, "scene_missing_scripts_zero", missing.ToString());
        Check(audit, !scene.isDirty, "audit_does_not_modify_scene", scene.isDirty.ToString());

        string yaml = File.ReadAllText(ScenePath);
        Check(audit, Count(yaml, "ConditionId: C00_robot_off_voice_off") == 1, "condition_C00_present_once", Count(yaml, "ConditionId: C00_robot_off_voice_off").ToString());
        Check(audit, Count(yaml, "ConditionId: C10_robot_on_voice_off") == 1, "condition_C10_present_once", Count(yaml, "ConditionId: C10_robot_on_voice_off").ToString());
        Check(audit, Count(yaml, "ConditionId: C11_robot_on_voice_on") == 1, "condition_C11_present_once", Count(yaml, "ConditionId: C11_robot_on_voice_on").ToString());
        Check(audit, Count(yaml, "ConditionId: C01_robot_off_voice_on") == 0, "condition_C01_absent", Count(yaml, "ConditionId: C01_robot_off_voice_on").ToString());
    }

    private static bool HasRoleNearAnchor(string source, string anchor, ExperimentButtonRole role)
    {
        source = source.Replace("\r\n", "\n");
        anchor = anchor.Replace("\r\n", "\n");
        int anchorIndex = source.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            return false;
        }

        int start = Math.Max(0, anchorIndex - 1200);
        int length = Math.Min(source.Length - start, 2400);
        string window = source.Substring(start, length);
        return window.Contains("ExperimentButtonRole." + role, StringComparison.Ordinal);
    }

    private static int CountRuntimePaletteDefinitions(string token)
    {
        string scriptsRoot = Path.Combine(Application.dataPath, "Scripts");
        int count = 0;
        foreach (string path in Directory.GetFiles(scriptsRoot, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = path.Replace('\\', '/');
            if (normalized.Contains("/Tests/", StringComparison.Ordinal))
            {
                continue;
            }

            count += Count(File.ReadAllText(path), token);
        }

        return count;
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

    private static string ToHex(Color color, bool includeAlpha = false)
    {
        Color32 value = color;
        return includeAlpha
            ? $"#{value.r:X2}{value.g:X2}{value.b:X2}{value.a:X2}"
            : $"#{value.r:X2}{value.g:X2}{value.b:X2}";
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder();
        foreach (char character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_');
        }

        return builder.ToString().Trim('_');
    }

    private static void WriteReport(Audit audit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "analysis");
        var builder = new StringBuilder();
        builder.AppendLine("# OE8 - P46J-03 - Auditoria de paleta semantica de botones");
        builder.AppendLine();
        builder.AppendLine($"Resultado: **{(audit.Failed.Count == 0 ? "PASS" : "FAIL")}**. Checks: {audit.Passed.Count} PASS / {audit.Failed.Count} FAIL. Missing scripts: {audit.MissingScripts}.");
        builder.AppendLine();
        builder.AppendLine("## Implementacion anterior y causa");
        builder.AppendLine();
        builder.AppendLine("Los tres controladores runtime asignaban colores base locales a `Image.color`: azul grisaceo `(0.18, 0.38, 0.48)`, naranja `(0.52, 0.30, 0.12)` y rojo apagado `(0.50, 0.20, 0.16)`. `ExperimentRuntimeButtonUtility` solo configuraba `interactable`, raycast y callbacks; no definia `ColorBlock`. Por ello no habia una autoridad unica, los estados heredaban el bloque por defecto de Unity y el resultado dependia del tinte previo de cada imagen.");
        builder.AppendLine();
        builder.AppendLine("## Diseno final");
        builder.AppendLine();
        builder.AppendLine("`ExperimentRuntimeButtonUtility` es la unica autoridad runtime. Recibe `ExperimentButtonRole`, construye el `ColorBlock`, fija `ColorTint`, `colorMultiplier=1`, `fadeDuration=0.10`, conserva eventos/navegacion/interactable y normaliza `Image.color=white`. No crea materiales ni toca TMP.");
        builder.AppendLine();
        builder.AppendLine("| Rol | Normal | Highlighted / Selected | Pressed | Disabled | Texto |");
        builder.AppendLine("|---|---|---|---|---|---|");
        builder.AppendLine("| Primary | #006EA6 | #0077B6 | #005B8C | #3F444B @ 65% | #FFFFFF |");
        builder.AppendLine("| Secondary | #4B5563 | #5B6470 | #374151 | #3F444B @ 65% | #FFFFFF |");
        builder.AppendLine("| Warning | #A85700 | #B85C00 | #7D4200 | #3F444B @ 65% | #FFFFFF |");
        builder.AppendLine("| Destructive | #B3264A | #C12C51 | #8F1E3C | #3F444B @ 65% | #FFFFFF |");
        builder.AppendLine();
        builder.AppendLine("## Rutas y roles encontrados");
        builder.AppendLine();
        builder.AppendLine("| Grupo | Boton/ruta | Rol | Fuente |");
        builder.AppendLine("|---|---|---|---|");
        foreach (Route route in Routes)
        {
            builder.AppendLine($"| {route.Group} | {route.Label} | {route.Role} | `{route.Path}` |");
        }
        builder.AppendLine();
        builder.AppendLine("## Notas de cobertura");
        builder.AppendLine();
        foreach (string note in audit.Notes)
        {
            builder.AppendLine("- " + note);
        }
        builder.AppendLine("- BoxVisualSync conserva #00ADFF, #FBFF00 y #CF3476; no se modifican cajas, zonas, materiales ni la atenuacion del punto 12.");
        builder.AppendLine();
        builder.AppendLine("## Checks automatizados");
        builder.AppendLine();
        foreach (string passed in audit.Passed)
        {
            builder.AppendLine("- PASS - " + passed);
        }
        foreach (string failed in audit.Failed)
        {
            builder.AppendLine("- FAIL - " + failed);
        }
        File.WriteAllText(ReportPath, builder.ToString(), new UTF8Encoding(false));
    }
}
