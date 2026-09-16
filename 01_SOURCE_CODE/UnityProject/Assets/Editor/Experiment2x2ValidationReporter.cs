using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class Experiment2x2ValidationReporter
{
    private const string ScenePath = "Assets/Scenes/autonomous_demo_step22_multimodal_bridge.unity";
    private const string OutputDirectory = "Logs/Experiment2x2Validation";
    private const string Step19LegacyId = "STEP19_TestBox_A_01";

    private static readonly ConditionExpectation[] ExpectedConditions =
    {
        new("C00_robot_off_voice_off", "Robot OFF + Voice OFF", false, false, RobotAssistanceMode.Disabled),
        new("C10_robot_on_voice_off", "Robot ON + Voice OFF", true, false, RobotAssistanceMode.AssistedSelection),
        new("C11_robot_on_voice_on", "Robot ON + Voice ON", true, true, RobotAssistanceMode.AssistedSelection)
    };

    private sealed class ValidationReport
    {
        public string ExportedAtLocal;
        public string SceneName;
        public string ScenePath;
        public string UnityVersion;
        public readonly List<CheckResult> Checks = new();
        public readonly List<string> RedFlags = new();
        public readonly List<string> Warnings = new();
        public readonly Dictionary<string, string> CriticalState = new();

        public bool Passed => RedFlags.Count == 0 && Checks.All(check => check.Passed || check.WarningOnly);
    }

    private sealed class CheckResult
    {
        public string Name;
        public bool Passed;
        public bool WarningOnly;
        public string Detail;
    }

    private sealed class ConditionExpectation
    {
        public ConditionExpectation(string id, string name, bool robotEnabled, bool voiceEnabled, RobotAssistanceMode assistanceMode)
        {
            Id = id;
            Name = name;
            RobotEnabled = robotEnabled;
            VoiceEnabled = voiceEnabled;
            AssistanceMode = assistanceMode;
        }

        public string Id { get; }
        public string Name { get; }
        public bool RobotEnabled { get; }
        public bool VoiceEnabled { get; }
        public RobotAssistanceMode AssistanceMode { get; }
    }

    [MenuItem("Tools/Multimodal AI-VR/Experiment 2x2 Validation/Preflight")]
    public static void Preflight()
    {
        ValidationReport report = BuildReport();
        string markdownPath = WriteMarkdown(report, "preflight");
        string jsonPath = WriteJson(report, "preflight");
        Debug.Log(BuildConsoleSummary(report) + $"\nMarkdown: {markdownPath}\nJSON: {jsonPath}");
    }

    public static void PreflightTargetSceneForBatch()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        ValidationReport report = BuildReport();
        string markdownPath = WriteMarkdown(report, "preflight_batch");
        string jsonPath = WriteJson(report, "preflight_batch");
        string summary = BuildConsoleSummary(report) + $"\nMarkdown: {markdownPath}\nJSON: {jsonPath}";
        if (!report.Passed)
        {
            throw new InvalidOperationException(summary);
        }

        Debug.Log(summary);
    }

    [MenuItem("Tools/Multimodal AI-VR/Experiment 2x2 Validation/Export Validation Checklist")]
    public static void ExportValidationChecklist()
    {
        ValidationReport report = BuildReport();
        string markdownPath = WriteMarkdown(report, "checklist");
        string jsonPath = WriteJson(report, "checklist");
        Debug.Log($"Experiment final-condition validation checklist exported.\nMarkdown: {markdownPath}\nJSON: {jsonPath}");
    }

    [MenuItem("Tools/Multimodal AI-VR/Experiment 2x2 Validation/Repair Serialized Condition Matrix")]
    public static void RepairSerializedConditionMatrix()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || scene.path != ScenePath)
        {
            Debug.LogError($"Open the expected scene before repairing the final condition matrix: {ScenePath}");
            return;
        }

        if (TryRepairConditionMatrix(scene, out string detail))
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("Experiment final condition matrix repaired and scene saved. " + detail);
            return;
        }

        Debug.Log("Experiment final condition matrix already canonical. " + detail);
    }

    public static void RepairTargetSceneConditionMatrixForBatch()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        if (TryRepairConditionMatrix(scene, out string detail))
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        Debug.Log("Experiment final condition matrix batch repair completed. " + detail);
    }

    private static ValidationReport BuildReport()
    {
        Scene scene = SceneManager.GetActiveScene();
        var report = new ValidationReport
        {
            ExportedAtLocal = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
            SceneName = scene.IsValid() ? scene.name : string.Empty,
            ScenePath = scene.IsValid() ? scene.path : string.Empty,
            UnityVersion = Application.unityVersion
        };

        AddCheck(report, "Scene open", scene.IsValid() && scene.path == ScenePath, $"Active=`{report.ScenePath}`, expected=`{ScenePath}`");
        if (!scene.IsValid())
        {
            return report;
        }

        ExperimentSessionOrchestrator orchestrator = FindSceneComponent<ExperimentSessionOrchestrator>(scene);
        ExperimentConditionConfigBehaviour conditionGate = FindSceneComponent<ExperimentConditionConfigBehaviour>(scene);
        ExperimentInstrumentationController instrumentation = FindSceneComponent<ExperimentInstrumentationController>(scene);
        SpawnManager spawnManager = FindSceneComponent<SpawnManager>(scene);
        RoundManager roundManager = FindSceneComponent<RoundManager>(scene);
        RobotAssistanceRoundCoordinator coordinator = FindSceneComponent<RobotAssistanceRoundCoordinator>(scene);
        MultimodalAutonomyCommandBridge bridge = FindSceneComponent<MultimodalAutonomyCommandBridge>(scene);
        VoiceAutonomyCommandConnector voiceConnector = FindSceneComponent<VoiceAutonomyCommandConnector>(scene);
        VoiceRecognitionController voiceController = FindSceneComponent<VoiceRecognitionController>(scene);
        AutonomousRobotAdapter robotAdapter = FindSceneComponent<AutonomousRobotAdapter>(scene);

        AddObjectState(report, "orchestrator", orchestrator);
        AddObjectState(report, "condition_gate", conditionGate);
        AddObjectState(report, "instrumentation", instrumentation);
        AddObjectState(report, "spawn_manager", spawnManager);
        AddObjectState(report, "round_manager", roundManager);
        AddObjectState(report, "assistance_coordinator", coordinator);
        AddObjectState(report, "bridge", bridge);
        AddObjectState(report, "voice_connector", voiceConnector);
        AddObjectState(report, "voice_controller", voiceController);
        AddObjectState(report, "robot_adapter", robotAdapter);

        AddCheck(report, "ExperimentSessionOrchestrator present and active", IsActiveBehaviour(orchestrator), PathOf(orchestrator));
        AddCheck(report, "ExperimentSessionOrchestrator run mode", EnumFieldEquals(orchestrator, "_runMode", ExperimentRunMode.Orchestrated2x2), FieldSummary(orchestrator, "_runMode"));
        ValidateConditions(report, orchestrator);
        ValidateRoundsPerCondition(report, orchestrator);
        ValidateNeutralConditionGate(report, conditionGate);
        ValidateNeutralInstrumentation(report, instrumentation);
        ValidateAutoStarts(report, spawnManager, roundManager, coordinator);
        ValidateBridge(report, bridge);
        ValidateSerializedReferences(report, orchestrator, conditionGate, instrumentation, spawnManager, roundManager, coordinator, bridge, voiceConnector, voiceController, robotAdapter);
        ValidateStepObjects(report, scene);
        ValidateStep19References(report, scene);

        return report;
    }

    private static bool TryRepairConditionMatrix(Scene scene, out string detail)
    {
        detail = string.Empty;
        ExperimentSessionOrchestrator orchestrator = FindSceneComponent<ExperimentSessionOrchestrator>(scene);
        if (orchestrator == null)
        {
            detail = "ExperimentSessionOrchestrator missing.";
            return false;
        }

        Undo.RecordObject(orchestrator, "Repair Experiment Final Condition Matrix");
        bool changed = orchestrator.EnsureCanonicalConditionMatrix();
        EditorUtility.SetDirty(orchestrator);
        detail = FieldSummary(orchestrator, "_conditions");
        return changed;
    }

    private static void ValidateConditions(ValidationReport report, ExperimentSessionOrchestrator orchestrator)
    {
        SerializedProperty conditions = FindProperty(orchestrator, "_conditions");
        int count = conditions != null && conditions.isArray ? conditions.arraySize : 0;
        AddCheck(report, "Exactly three final conditions", count == ExpectedConditions.Length, $"count={count}");
        if (conditions == null || !conditions.isArray)
        {
            return;
        }

        for (int i = 0; i < ExpectedConditions.Length; i++)
        {
            ConditionExpectation expected = ExpectedConditions[i];
            if (i >= count)
            {
                AddCheck(report, "Condition " + expected.Id, false, "missing");
                continue;
            }

            SerializedProperty item = conditions.GetArrayElementAtIndex(i);
            string id = item.FindPropertyRelative("ConditionId")?.stringValue ?? string.Empty;
            string name = item.FindPropertyRelative("ConditionName")?.stringValue ?? string.Empty;
            bool robot = item.FindPropertyRelative("RobotEnabled")?.boolValue ?? false;
            bool voice = item.FindPropertyRelative("VoiceEnabled")?.boolValue ?? false;
            RobotAssistanceMode assistance = EnumValue<RobotAssistanceMode>(item.FindPropertyRelative("AssistanceMode"));
            bool passed = string.Equals(id, expected.Id, StringComparison.Ordinal) &&
                          robot == expected.RobotEnabled &&
                          voice == expected.VoiceEnabled &&
                          assistance == expected.AssistanceMode;
            AddCheck(report, "Condition " + expected.Id, passed, $"index={i}, id={id}, name={name}, robot={robot}, voice={voice}, assistance={assistance}");
        }
    }

    private static void ValidateRoundsPerCondition(ValidationReport report, ExperimentSessionOrchestrator orchestrator)
    {
        int roundsPerCondition = IntField(orchestrator, "_roundsPerCondition");
        int conditionCount = ArrayFieldSize(orchestrator, "_conditions");
        int expectedTrials = Math.Max(0, conditionCount) * Math.Max(1, roundsPerCondition);
        AddCheck(
            report,
            "Rounds Per Condition configured",
            orchestrator != null && roundsPerCondition >= 1,
            $"roundsPerCondition={roundsPerCondition}, conditionCount={conditionCount}, expectedTrials={expectedTrials}");
    }

    private static void ValidateNeutralConditionGate(ValidationReport report, ExperimentConditionConfigBehaviour gate)
    {
        bool passed = BoolField(gate, "_robotEnabled") == false &&
                      BoolField(gate, "_voiceEnabled") == false &&
                      StringField(gate, "_conditionName") == "uninitialized" &&
                      EnumFieldEquals(gate, "_assistanceMode", RobotAssistanceMode.Disabled) &&
                      BoolField(gate, "_logConditionOnStart") == false &&
                      EnumFieldEquals(gate, "_runMode", ExperimentRunMode.Orchestrated2x2);
        AddCheck(report, "ExperimentConditionGate neutral", passed, FieldSummary(gate, "_robotEnabled", "_voiceEnabled", "_conditionName", "_assistanceMode", "_logConditionOnStart", "_runMode"));
    }

    private static void ValidateNeutralInstrumentation(ValidationReport report, ExperimentInstrumentationController instrumentation)
    {
        bool passed = StringField(instrumentation, "_conditionId") == "uninitialized" &&
                      StringField(instrumentation, "_conditionName") == "uninitialized" &&
                      BoolField(instrumentation, "_robotEnabled") == false &&
                      BoolField(instrumentation, "_voiceEnabled") == false &&
                      StringField(instrumentation, "_assistanceMode") == "Disabled" &&
                      BoolField(instrumentation, "_configureSessionOnStart") == false &&
                      BoolField(instrumentation, "_startTrialOnStart") == false &&
                      EnumFieldEquals(instrumentation, "_runMode", ExperimentRunMode.Orchestrated2x2);
        AddCheck(report, "ExperimentInstrumentationController neutral", passed, FieldSummary(instrumentation, "_conditionId", "_conditionName", "_robotEnabled", "_voiceEnabled", "_assistanceMode", "_configureSessionOnStart", "_startTrialOnStart", "_runMode"));
    }

    private static void ValidateAutoStarts(ValidationReport report, SpawnManager spawn, RoundManager round, RobotAssistanceRoundCoordinator coordinator)
    {
        AddCheck(report, "SpawnManager spawnOnStart disabled", spawn != null && !spawn.spawnOnStart, spawn != null ? $"spawnOnStart={spawn.spawnOnStart}, spawnGenerationMode={spawn.spawnGenerationMode}" : "missing");
        AddCheck(report, "RoundManager autoStartNextRound disabled", round != null && !round.autoStartNextRound, round != null ? $"autoStartNextRound={round.autoStartNextRound}" : "missing");
        AddCheck(report, "RobotAssistanceRoundCoordinator autoInitializeRound disabled", BoolField(coordinator, "_autoInitializeRound") == false, FieldSummary(coordinator, "_autoInitializeRound"));

        if (spawn != null && !spawn.spawnOnStart && spawn.spawnGenerationMode == SpawnGenerationMode.DeterministicDebug)
        {
            AddWarning(report, "SpawnManager DeterministicDebug is acceptable as legacy/debug fallback because spawnOnStart=false and Orchestrated2x2 applies trial context before spawn.");
        }
    }

    private static void ValidateBridge(ValidationReport report, MultimodalAutonomyCommandBridge bridge)
    {
        bool passed = BoolField(bridge, "_preferConfiguredTargetForCategory") == false &&
                      BoolField(bridge, "_submitSimulatedIntentOnStart") == false &&
                      !ContainsStep19(StringField(bridge, "_preferredTargetId")) &&
                      !ContainsStep19(StringField(bridge, "_simulatedTargetId"));
        AddCheck(report, "Bridge legacy target preference disabled", passed, FieldSummary(bridge, "_preferConfiguredTargetForCategory", "_preferredTargetId", "_submitSimulatedIntentOnStart", "_simulatedTargetId", "_simulatedPlaceTargetId"));
    }

    private static void ValidateSerializedReferences(
        ValidationReport report,
        ExperimentSessionOrchestrator orchestrator,
        ExperimentConditionConfigBehaviour conditionGate,
        ExperimentInstrumentationController instrumentation,
        SpawnManager spawnManager,
        RoundManager roundManager,
        RobotAssistanceRoundCoordinator coordinator,
        MultimodalAutonomyCommandBridge bridge,
        VoiceAutonomyCommandConnector voiceConnector,
        VoiceRecognitionController voiceController,
        AutonomousRobotAdapter robotAdapter)
    {
        AddReferenceCheck(report, orchestrator, "_conditionConfig", conditionGate);
        AddReferenceCheck(report, orchestrator, "_instrumentation", instrumentation);
        AddReferenceCheck(report, orchestrator, "_spawnManager", spawnManager);
        AddReferenceCheck(report, orchestrator, "_roundManager", roundManager);
        AddReferenceCheck(report, orchestrator, "_assistanceCoordinator", coordinator);
        AddReferenceCheck(report, orchestrator, "_voiceRecognitionController", voiceController);
        AddReferenceCheck(report, orchestrator, "_voiceConnector", voiceConnector);
        AddReferenceCheck(report, orchestrator, "_commandBridge", bridge);
        AddReferenceCheck(report, orchestrator, "_robotAdapter", robotAdapter);
        AddReferenceCheck(report, voiceConnector, "_experimentConditionProviderComponent", conditionGate);
        AddReferenceCheck(report, coordinator, "_experimentConditionProviderComponent", conditionGate);
        AddReferenceCheck(report, coordinator, "_commandBridge", bridge);
        AddReferenceCheck(report, coordinator, "_robotAdapter", robotAdapter);
        AddReferenceCheck(report, bridge, "_robotAdapter", robotAdapter);
        AddReferenceCheck(report, bridge, "_roundLifecycleComponent", roundManager);
    }

    private static void ValidateStepObjects(ValidationReport report, Scene scene)
    {
        List<string> hits = new();
        foreach (Transform transform in AllTransforms(scene))
        {
            if (transform.gameObject.activeInHierarchy &&
                transform.name.StartsWith("STEP", StringComparison.OrdinalIgnoreCase) &&
                !IsUnderRoot(transform, "DebugLegacy"))
            {
                hits.Add(PathOf(transform));
            }
        }

        AddCheck(report, "No active STEP objects outside DebugLegacy", hits.Count == 0, hits.Count == 0 ? "none" : string.Join(" | ", hits));
    }

    private static void ValidateStep19References(ValidationReport report, Scene scene)
    {
        List<string> hits = new();
        foreach (Component component in AllComponents(scene))
        {
            if (component == null || IsUnderRoot(component.transform, "DebugLegacy"))
            {
                continue;
            }

            try
            {
                SerializedObject serialized = new(component);
                SerializedProperty iterator = serialized.GetIterator();
                bool enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (iterator.propertyType == SerializedPropertyType.String && ContainsStep19(iterator.stringValue))
                    {
                        hits.Add($"{PathOf(component.transform)}/{component.GetType().Name}.{iterator.propertyPath}='{iterator.stringValue}'");
                    }
                    else if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue != null)
                    {
                        string referencePath = PathOf(iterator.objectReferenceValue);
                        if (ContainsStep19(referencePath))
                        {
                            hits.Add($"{PathOf(component.transform)}/{component.GetType().Name}.{iterator.propertyPath}->{referencePath}");
                        }
                    }
                }
            }
            catch
            {
                hits.Add($"{PathOf(component.transform)}/{component.GetType().Name}: serialized scan failed");
            }
        }

        AddCheck(report, "No active STEP19_TestBox_A_01 references outside DebugLegacy", hits.Count == 0, hits.Count == 0 ? "none" : string.Join(" | ", hits.Take(20)));
    }

    private static void AddReferenceCheck(ValidationReport report, UnityEngine.Object owner, string propertyPath, UnityEngine.Object expected)
    {
        SerializedProperty property = FindProperty(owner, propertyPath);
        UnityEngine.Object value = property != null ? property.objectReferenceValue : null;
        bool passed = owner != null && expected != null && value == expected;
        AddCheck(report, $"Reference {OwnerName(owner)}.{propertyPath}", passed, $"actual={PathOf(value)}, expected={PathOf(expected)}");
    }

    private static void AddObjectState(ValidationReport report, string key, Component component)
    {
        report.CriticalState[key] = component != null ? $"{PathOf(component.transform)} active={component.gameObject.activeInHierarchy} enabled={IsEnabled(component)}" : "missing";
    }

    private static void AddCheck(ValidationReport report, string name, bool passed, string detail, bool warningOnly = false)
    {
        report.Checks.Add(new CheckResult { Name = name, Passed = passed, WarningOnly = warningOnly, Detail = detail ?? string.Empty });
        if (!passed && !warningOnly)
        {
            report.RedFlags.Add($"{name}: {detail}");
        }
    }

    private static void AddWarning(ValidationReport report, string message)
    {
        report.Warnings.Add(message);
        report.Checks.Add(new CheckResult { Name = "Warning", Passed = true, WarningOnly = true, Detail = message });
    }

    private static T FindSceneComponent<T>(Scene scene) where T : Component
    {
        return AllComponents(scene).OfType<T>().FirstOrDefault();
    }

    private static IEnumerable<Component> AllComponents(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component != null)
                {
                    yield return component;
                }
            }
        }
    }

    private static IEnumerable<Transform> AllTransforms(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                yield return transform;
            }
        }
    }

    private static bool IsActiveBehaviour(Component component)
    {
        return component != null && component.gameObject.activeInHierarchy && IsEnabled(component);
    }

    private static bool IsEnabled(Component component)
    {
        return component is not Behaviour behaviour || behaviour.enabled;
    }

    private static bool BoolField(UnityEngine.Object target, string propertyPath)
    {
        SerializedProperty property = FindProperty(target, propertyPath);
        return property != null && property.propertyType == SerializedPropertyType.Boolean && property.boolValue;
    }

    private static int IntField(UnityEngine.Object target, string propertyPath)
    {
        SerializedProperty property = FindProperty(target, propertyPath);
        return property != null && property.propertyType == SerializedPropertyType.Integer ? property.intValue : 0;
    }

    private static int ArrayFieldSize(UnityEngine.Object target, string propertyPath)
    {
        SerializedProperty property = FindProperty(target, propertyPath);
        return property != null && property.isArray ? property.arraySize : 0;
    }

    private static string StringField(UnityEngine.Object target, string propertyPath)
    {
        SerializedProperty property = FindProperty(target, propertyPath);
        return property != null && property.propertyType == SerializedPropertyType.String ? property.stringValue ?? string.Empty : string.Empty;
    }

    private static bool EnumFieldEquals<T>(UnityEngine.Object target, string propertyPath, T expected) where T : struct, Enum
    {
        SerializedProperty property = FindProperty(target, propertyPath);
        if (property == null || property.propertyType != SerializedPropertyType.Enum)
        {
            return false;
        }

        return string.Equals(property.enumNames[property.enumValueIndex], expected.ToString(), StringComparison.Ordinal);
    }

    private static T EnumValue<T>(SerializedProperty property) where T : struct, Enum
    {
        if (property == null || property.propertyType != SerializedPropertyType.Enum)
        {
            return default;
        }

        return Enum.TryParse(property.enumNames[property.enumValueIndex], out T value) ? value : default;
    }

    private static SerializedProperty FindProperty(UnityEngine.Object target, string propertyPath)
    {
        if (target == null)
        {
            return null;
        }

        SerializedObject serialized = new(target);
        return serialized.FindProperty(propertyPath);
    }

    private static string FieldSummary(UnityEngine.Object target, params string[] propertyPaths)
    {
        if (target == null)
        {
            return "missing";
        }

        SerializedObject serialized = new(target);
        return string.Join(", ", propertyPaths.Select(path =>
        {
            SerializedProperty property = serialized.FindProperty(path);
            return property == null ? $"{path}=<missing>" : $"{path}={PropertyValue(property)}";
        }));
    }

    private static string PropertyValue(SerializedProperty property)
    {
        return property.propertyType switch
        {
            SerializedPropertyType.Boolean => property.boolValue.ToString(),
            SerializedPropertyType.String => property.stringValue ?? string.Empty,
            SerializedPropertyType.Enum => property.enumDisplayNames[property.enumValueIndex],
            SerializedPropertyType.ObjectReference => PathOf(property.objectReferenceValue),
            SerializedPropertyType.Integer => property.intValue.ToString(CultureInfo.InvariantCulture),
            _ => property.type
        };
    }

    private static string OwnerName(UnityEngine.Object owner)
    {
        return owner != null ? owner.GetType().Name : "missing";
    }

    private static string PathOf(UnityEngine.Object value)
    {
        if (value == null)
        {
            return "null";
        }

        if (value is GameObject gameObject)
        {
            return PathOf(gameObject.transform);
        }

        if (value is Component component)
        {
            return $"{PathOf(component.transform)}/{component.GetType().Name}";
        }

        string assetPath = AssetDatabase.GetAssetPath(value);
        return string.IsNullOrWhiteSpace(assetPath) ? value.name : assetPath;
    }

    private static string PathOf(Transform transform)
    {
        if (transform == null)
        {
            return "null";
        }

        Stack<string> parts = new();
        while (transform != null)
        {
            parts.Push(transform.name);
            transform = transform.parent;
        }

        return string.Join("/", parts);
    }

    private static bool IsUnderRoot(Transform transform, string rootName)
    {
        return transform != null && transform.root != null && string.Equals(transform.root.name, rootName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsStep19(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.IndexOf(Step19LegacyId, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string WriteMarkdown(ValidationReport report, string suffix)
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), OutputDirectory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"experiment_2x2_validation_{suffix}_{Sanitize(report.SceneName)}_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        File.WriteAllText(path, BuildMarkdown(report, suffix), Encoding.UTF8);
        return path;
    }

    private static string WriteJson(ValidationReport report, string suffix)
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), OutputDirectory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"experiment_2x2_validation_{suffix}_{Sanitize(report.SceneName)}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(path, BuildJson(report, suffix), Encoding.UTF8);
        return path;
    }

    private static string BuildMarkdown(ValidationReport report, string mode)
    {
        StringBuilder builder = new();
        builder.AppendLine("# P43E Final Condition Validation");
        builder.AppendLine();
        builder.AppendLine($"- Mode: `{mode}`");
        builder.AppendLine($"- Exported at: `{report.ExportedAtLocal}`");
        builder.AppendLine($"- Scene: `{report.SceneName}`");
        builder.AppendLine($"- Scene path: `{report.ScenePath}`");
        builder.AppendLine($"- Unity: `{report.UnityVersion}`");
        builder.AppendLine($"- Preflight result: `{(report.Passed ? "PASS" : "FAIL")}`");
        builder.AppendLine();

        AppendList(builder, "Red Flags", report.RedFlags);
        AppendList(builder, "Warnings", report.Warnings);

        builder.AppendLine("## Critical Component State");
        builder.AppendLine();
        foreach (KeyValuePair<string, string> pair in report.CriticalState)
        {
            builder.AppendLine($"- `{pair.Key}`: {pair.Value}");
        }

        builder.AppendLine();
        builder.AppendLine("## Preflight Checks");
        builder.AppendLine();
        foreach (CheckResult check in report.Checks)
        {
            string status = check.WarningOnly ? "WARN" : check.Passed ? "PASS" : "FAIL";
            builder.AppendLine($"- `{status}` {check.Name}: {check.Detail}");
        }

        builder.AppendLine();
        AppendExpectedMatrix(builder);
        AppendManualSteps(builder);
        AppendEventExpectations(builder);
        AppendClosingChecklist(builder);
        return builder.ToString();
    }

    private static string BuildJson(ValidationReport report, string mode)
    {
        StringBuilder builder = new();
        builder.AppendLine("{");
        builder.AppendLine($"  \"mode\": \"{EscapeJson(mode)}\",");
        builder.AppendLine($"  \"exported_at_local\": \"{EscapeJson(report.ExportedAtLocal)}\",");
        builder.AppendLine($"  \"scene\": \"{EscapeJson(report.SceneName)}\",");
        builder.AppendLine($"  \"scene_path\": \"{EscapeJson(report.ScenePath)}\",");
        builder.AppendLine($"  \"unity_version\": \"{EscapeJson(report.UnityVersion)}\",");
        builder.AppendLine($"  \"preflight_passed\": {report.Passed.ToString().ToLowerInvariant()},");
        builder.AppendLine($"  \"red_flags\": {JsonArray(report.RedFlags)},");
        builder.AppendLine($"  \"warnings\": {JsonArray(report.Warnings)},");
        builder.AppendLine("  \"critical_state\": {");
        builder.AppendLine(string.Join(",\n", report.CriticalState.Select(pair => $"    \"{EscapeJson(pair.Key)}\": \"{EscapeJson(pair.Value)}\"")));
        builder.AppendLine("  },");
        builder.AppendLine("  \"checks\": [");
        builder.AppendLine(string.Join(",\n", report.Checks.Select(check => $"    {{\"name\":\"{EscapeJson(check.Name)}\",\"passed\":{check.Passed.ToString().ToLowerInvariant()},\"warning_only\":{check.WarningOnly.ToString().ToLowerInvariant()},\"detail\":\"{EscapeJson(check.Detail)}\"}}")));
        builder.AppendLine("  ]");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendExpectedMatrix(StringBuilder builder)
    {
        builder.AppendLine("## Expected Final C00-C10-C11 Matrix");
        builder.AppendLine();
        builder.AppendLine("| Condition | Robot | Voice | Expected behavior |");
        builder.AppendLine("| --- | --- | --- | --- |");
        builder.AppendLine("| C00 | OFF | OFF | Voice blocked, assistance blocked, bridge not invoked. |");
        builder.AppendLine("| C10 | ON | OFF | Voice execution blocked, automatic assistance may run in active trial/round. |");
        builder.AppendLine("| C11 | ON | ON | Voice and assistance enabled; explicit voice intent has priority. |");
        builder.AppendLine();
    }

    private static void AppendManualSteps(StringBuilder builder)
    {
        builder.AppendLine("## Manual Validation Steps");
        builder.AppendLine();
        builder.AppendLine("- Preflight: run `Tools/Multimodal AI-VR/Experiment 2x2 Validation/Preflight` with the target scene open.");
        builder.AppendLine("- Zero test: enter Play Mode and do nothing; from `ExperimentSessionOrchestrator` context menu run `Experiment/Log Zero-State Snapshot`.");
        builder.AppendLine("- Start Session: in Play Mode run `Experiment/Start Session`; confirm no round or trial starts by itself.");
        builder.AppendLine("- Start Next Trial: select C00, C10, or C11 in the orchestrator, then run `Experiment/Start Next Trial`.");
        builder.AppendLine("- Condition sweep: repeat Start Next Trial for all three final conditions after ending/restarting trials as needed.");
        builder.AppendLine();
    }

    private static void AppendEventExpectations(StringBuilder builder)
    {
        builder.AppendLine("## Expected Events");
        builder.AppendLine();
        builder.AppendLine("- Zero test: `experiment_zero_state_sanity_passed`; no `experiment_session_started`, no `experiment_trial_started`, no `spawn_round_started`, no `autonomy_request_submitted`.");
        builder.AppendLine("- Start Session: `experiment_session_started` with participant_id, session_id, task_id, input_mode, experiment_run_mode.");
        builder.AppendLine("- Start Next Trial: `experiment_trial_prepare_started`, `experiment_condition_orchestrator_applied`, `experiment_trial_reset_started`, `experiment_trial_reset_completed`, `experiment_trial_sanity_check_started`, `experiment_trial_sanity_check_passed`, `experiment_trial_round_spawn_requested`, `experiment_trial_round_spawn_completed`, `experiment_trial_started_by_orchestrator`.");
        builder.AppendLine("- C00: `voice_command_blocked_by_condition` if voice is tested; `robot_assistance_blocked_by_condition` or `assisted_selection_blocked_detailed` with robot disabled.");
        builder.AppendLine("- C10: voice blocked by `voice_disabled_by_condition`; assisted selection may submit when round active and robot idle.");
        builder.AppendLine("- C11: `voice_command_allowed_by_condition`, `explicit_voice_intent_processing_started`, `explicit_voice_intent_processing_finished`; assistance blocked while explicit intent is processing.");
        builder.AppendLine();
    }

    private static void AppendClosingChecklist(StringBuilder builder)
    {
        builder.AppendLine("## Closing Checklist");
        builder.AppendLine();
        builder.AppendLine("- Preflight PASS or every red flag is understood.");
        builder.AppendLine("- Zero-state snapshot PASS before any manual session/trial action.");
        builder.AppendLine("- Start Session logs metadata and does not spawn.");
        builder.AppendLine("- Start Next Trial logs the full prepare/reset/sanity/spawn/trial chain.");
        builder.AppendLine("- C00/C10/C11 behavior matches the expected final matrix.");
        builder.AppendLine("- No active STEP objects or STEP19 references outside DebugLegacy.");
        builder.AppendLine("- No automatic Play Mode trial, spawn, assistance, bridge call, or robot motion.");
    }

    private static void AppendList(StringBuilder builder, string title, List<string> values)
    {
        builder.AppendLine("## " + title);
        builder.AppendLine();
        if (values.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (string value in values)
            {
                builder.AppendLine("- " + value);
            }
        }

        builder.AppendLine();
    }

    private static string BuildConsoleSummary(ValidationReport report)
    {
        return $"Experiment final-condition preflight {(report.Passed ? "PASS" : "FAIL")} | checks={report.Checks.Count} redFlags={report.RedFlags.Count} warnings={report.Warnings.Count}";
    }

    private static string JsonArray(IEnumerable<string> values)
    {
        return "[" + string.Join(",", values.Select(value => $"\"{EscapeJson(value)}\"")) + "]";
    }

    private static string EscapeJson(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "scene";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }
}
