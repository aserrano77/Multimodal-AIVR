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

public static class FinalSceneConsolidationAuditUtility
{
    private const string ScenePath = "Assets/Scenes/autonomous_demo_step22_multimodal_bridge.unity";
    private const string OutputDirectory = "Logs/FinalSceneConsolidation";

    private sealed class Audit
    {
        public readonly List<string> Passes = new();
        public readonly List<string> Warnings = new();
        public readonly List<string> Failures = new();
    }

    [MenuItem("Tools/TFG/P43E/Final Scene Consolidation Audit")]
    public static void RunForBatch()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Audit audit = new();

        Check(audit, scene.IsValid() && scene.path == ScenePath, "target_scene_open", scene.path);
        CheckRequiredRoots(audit, scene);
        CheckMissingScripts(audit, scene);
        CheckNoActiveStepOutsideDebugLegacy(audit, scene);
        CheckExperimentConfiguration(audit, scene);
        CheckVoiceAndTtsConfiguration(audit, scene);
        CheckNoPrimaryVoiceOnlySceneContext(audit, scene);

        string summary = WriteReport(audit, scene);
        if (audit.Failures.Count > 0)
        {
            throw new InvalidOperationException(summary);
        }

        Debug.Log(summary);
    }

    private static void CheckRequiredRoots(Audit audit, Scene scene)
    {
        string[] expectedRoots = { "Experiment", "Autonomy", "MultimodalInput", "Diagnostics", "DebugLegacy" };
        HashSet<string> roots = scene.GetRootGameObjects().Select(root => root.name).ToHashSet(StringComparer.Ordinal);
        foreach (string root in expectedRoots)
        {
            Check(audit, roots.Contains(root), "root_present_" + root, root);
        }
    }

    private static void CheckMissingScripts(Audit audit, Scene scene)
    {
        int missing = AllGameObjects(scene).Sum(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount);
        Check(audit, missing == 0, "missing_scripts_zero", missing.ToString(CultureInfo.InvariantCulture));
    }

    private static void CheckNoActiveStepOutsideDebugLegacy(Audit audit, Scene scene)
    {
        List<string> hits = AllTransforms(scene)
            .Where(transform => transform.gameObject.activeInHierarchy)
            .Where(transform => transform.name.StartsWith("STEP", StringComparison.OrdinalIgnoreCase))
            .Where(transform => !IsUnderRoot(transform, "DebugLegacy"))
            .Select(PathOf)
            .ToList();

        Check(audit, hits.Count == 0, "no_active_step_outside_debuglegacy", hits.Count == 0 ? "none" : string.Join(" | ", hits));
    }

    private static void CheckExperimentConfiguration(Audit audit, Scene scene)
    {
        ExperimentSessionOrchestrator orchestrator = FindSingle<ExperimentSessionOrchestrator>(audit, scene);
        ExperimentConditionConfigBehaviour gate = FindSingle<ExperimentConditionConfigBehaviour>(audit, scene);
        ExperimentInstrumentationController instrumentation = FindSingle<ExperimentInstrumentationController>(audit, scene);
        AutonomousRobotAdapter robotAdapter = FindSingle<AutonomousRobotAdapter>(audit, scene);
        MultimodalAutonomyCommandBridge bridge = FindSingle<MultimodalAutonomyCommandBridge>(audit, scene);
        VoiceAutonomyCommandConnector voiceConnector = FindSingle<VoiceAutonomyCommandConnector>(audit, scene);
        VoiceRecognitionController voiceController = FindSingle<VoiceRecognitionController>(audit, scene);

        Check(audit, orchestrator != null && orchestrator.gameObject.activeInHierarchy && IsEnabled(orchestrator), "orchestrator_active", PathOf(orchestrator));
        Check(audit, gate != null && gate.gameObject.activeInHierarchy && IsEnabled(gate), "condition_gate_active", PathOf(gate));
        Check(audit, instrumentation != null && instrumentation.gameObject.activeInHierarchy && IsEnabled(instrumentation), "instrumentation_active", PathOf(instrumentation));
        Check(audit, robotAdapter != null && robotAdapter.gameObject.activeInHierarchy && IsEnabled(robotAdapter), "robot_adapter_active", PathOf(robotAdapter));
        Check(audit, bridge != null && bridge.gameObject.activeInHierarchy && IsEnabled(bridge), "bridge_active", PathOf(bridge));
        Check(audit, voiceConnector != null && voiceConnector.gameObject.activeInHierarchy && IsEnabled(voiceConnector), "voice_connector_active", PathOf(voiceConnector));
        Check(audit, voiceController != null && voiceController.gameObject.activeInHierarchy && IsEnabled(voiceController), "voice_controller_active", PathOf(voiceController));

        Check(audit, EnumFieldEquals(orchestrator, "_runMode", ExperimentRunMode.Orchestrated2x2), "orchestrator_run_mode_orchestrated_2x2", FieldSummary(orchestrator, "_runMode"));
        CheckConditionMatrix(audit, orchestrator);
        Check(audit, IntField(orchestrator, "_selectedConditionIndex") == 0, "selected_condition_index_defaults_to_c00", FieldSummary(orchestrator, "_selectedConditionIndex"));

        Check(audit,
            BoolField(gate, "_robotEnabled") == false &&
            BoolField(gate, "_voiceEnabled") == false &&
            StringField(gate, "_conditionName") == "uninitialized" &&
            EnumFieldEquals(gate, "_assistanceMode", RobotAssistanceMode.Disabled) &&
            BoolField(gate, "_logConditionOnStart") == false &&
            EnumFieldEquals(gate, "_runMode", ExperimentRunMode.Orchestrated2x2),
            "condition_gate_neutral",
            FieldSummary(gate, "_robotEnabled", "_voiceEnabled", "_conditionName", "_assistanceMode", "_logConditionOnStart", "_runMode"));

        Check(audit,
            StringField(instrumentation, "_conditionId") == "uninitialized" &&
            StringField(instrumentation, "_conditionName") == "uninitialized" &&
            BoolField(instrumentation, "_robotEnabled") == false &&
            BoolField(instrumentation, "_voiceEnabled") == false &&
            StringField(instrumentation, "_assistanceMode") == "Disabled" &&
            BoolField(instrumentation, "_configureSessionOnStart") == false &&
            BoolField(instrumentation, "_startTrialOnStart") == false &&
            EnumFieldEquals(instrumentation, "_runMode", ExperimentRunMode.Orchestrated2x2),
            "instrumentation_neutral",
            FieldSummary(instrumentation, "_conditionId", "_conditionName", "_robotEnabled", "_voiceEnabled", "_assistanceMode", "_configureSessionOnStart", "_startTrialOnStart", "_runMode"));

        Check(audit, ReferenceEquals(ReadObject<UnityEngine.Object>(orchestrator, "_voiceConnector"), voiceConnector), "orchestrator_refs_voice_connector", PathOf(ReadObject<UnityEngine.Object>(orchestrator, "_voiceConnector")));
        Check(audit, ReferenceEquals(ReadObject<UnityEngine.Object>(orchestrator, "_commandBridge"), bridge), "orchestrator_refs_bridge", PathOf(ReadObject<UnityEngine.Object>(orchestrator, "_commandBridge")));
        Check(audit, ReferenceEquals(ReadObject<UnityEngine.Object>(orchestrator, "_robotAdapter"), robotAdapter), "orchestrator_refs_robot_adapter", PathOf(ReadObject<UnityEngine.Object>(orchestrator, "_robotAdapter")));
        Check(audit, ReferenceEquals(ReadObject<UnityEngine.Object>(voiceConnector, "_experimentConditionProviderComponent"), gate), "voice_connector_refs_condition_gate", PathOf(ReadObject<UnityEngine.Object>(voiceConnector, "_experimentConditionProviderComponent")));
        CheckVoiceStopCommandRoute(audit, voiceConnector, bridge, robotAdapter);
        CheckVoiceResumeCommandRoute(audit, voiceConnector, bridge, robotAdapter);
    }

    private static void CheckVoiceStopCommandRoute(
        Audit audit,
        VoiceAutonomyCommandConnector voiceConnector,
        MultimodalAutonomyCommandBridge bridge,
        AutonomousRobotAdapter robotAdapter)
    {
        bool routerHasStopHandler = typeof(VoiceAutonomyCommandRouter)
            .GetConstructors()
            .Any(ctor => ctor.GetParameters().Any(parameter => parameter.Name == "applyStopCommand"));
        bool adapterHasStopMethod = typeof(AutonomousRobotAdapter).GetMethod(nameof(AutonomousRobotAdapter.ApplyVoiceStopCommand)) != null;
        bool sceneRouteWired = voiceConnector != null &&
                               bridge != null &&
                               robotAdapter != null &&
                               ReferenceEquals(bridge.RobotAdapter, robotAdapter);
        Check(audit,
            routerHasStopHandler && adapterHasStopMethod && sceneRouteWired,
            "voice_stop_command_has_operational_route",
            $"routerHandler={routerHasStopHandler}, adapterMethod={adapterHasStopMethod}, sceneRouteWired={sceneRouteWired}");
    }

    private static void CheckVoiceResumeCommandRoute(
        Audit audit,
        VoiceAutonomyCommandConnector voiceConnector,
        MultimodalAutonomyCommandBridge bridge,
        AutonomousRobotAdapter robotAdapter)
    {
        bool routerHasResumeHandler = typeof(VoiceAutonomyCommandRouter)
            .GetConstructors()
            .Any(ctor => ctor.GetParameters().Any(parameter => parameter.Name == "applyResumeCommand"));
        bool adapterHasResumeMethod = typeof(AutonomousRobotAdapter).GetMethod(nameof(AutonomousRobotAdapter.ApplyVoiceResumeCommand)) != null;
        bool sceneRouteWired = voiceConnector != null &&
                               bridge != null &&
                               robotAdapter != null &&
                               ReferenceEquals(bridge.RobotAdapter, robotAdapter);
        Check(audit,
            routerHasResumeHandler && adapterHasResumeMethod && sceneRouteWired,
            "voice_resume_command_has_operational_route",
            $"routerHandler={routerHasResumeHandler}, adapterMethod={adapterHasResumeMethod}, sceneRouteWired={sceneRouteWired}");
    }

    private static void CheckConditionMatrix(Audit audit, ExperimentSessionOrchestrator orchestrator)
    {
        SerializedProperty conditions = FindProperty(orchestrator, "_conditions");
        Check(audit, conditions != null && conditions.isArray && conditions.arraySize == 3, "condition_matrix_has_three_final_entries", conditions != null ? conditions.arraySize.ToString(CultureInfo.InvariantCulture) : "missing");
        if (conditions == null || !conditions.isArray || conditions.arraySize < 3)
        {
            return;
        }

        CheckCondition(audit, conditions.GetArrayElementAtIndex(0), "C00_robot_off_voice_off", false, false, RobotAssistanceMode.Disabled);
        CheckCondition(audit, conditions.GetArrayElementAtIndex(1), "C10_robot_on_voice_off", true, false, RobotAssistanceMode.AssistedSelection);
        CheckCondition(audit, conditions.GetArrayElementAtIndex(2), "C11_robot_on_voice_on", true, true, RobotAssistanceMode.AssistedSelection);
        Check(audit, !ContainsConditionId(conditions, "C01_robot_off_voice_on"), "condition_matrix_excludes_c01_legacy_debug", "C01 absent from active plan");
    }

    private static void CheckCondition(Audit audit, SerializedProperty property, string id, bool robot, bool voice, RobotAssistanceMode assistance)
    {
        string actualId = property.FindPropertyRelative("ConditionId")?.stringValue ?? string.Empty;
        bool actualRobot = property.FindPropertyRelative("RobotEnabled")?.boolValue ?? false;
        bool actualVoice = property.FindPropertyRelative("VoiceEnabled")?.boolValue ?? false;
        RobotAssistanceMode actualAssistance = EnumValue<RobotAssistanceMode>(property.FindPropertyRelative("AssistanceMode"));
        Check(audit,
            actualId == id && actualRobot == robot && actualVoice == voice && actualAssistance == assistance,
            "condition_" + id,
            $"id={actualId}, robot={actualRobot}, voice={actualVoice}, assistance={actualAssistance}");
    }

    private static bool ContainsConditionId(SerializedProperty conditions, string conditionId)
    {
        if (conditions == null || !conditions.isArray)
        {
            return false;
        }

        for (int i = 0; i < conditions.arraySize; i++)
        {
            SerializedProperty item = conditions.GetArrayElementAtIndex(i);
            string actualId = item.FindPropertyRelative("ConditionId")?.stringValue ?? string.Empty;
            if (string.Equals(actualId, conditionId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckVoiceAndTtsConfiguration(Audit audit, Scene scene)
    {
        VoiceRecognitionController voiceController = FindSingle<VoiceRecognitionController>(audit, scene);
        VoiceAutonomyCommandConnector voiceConnector = FindSingle<VoiceAutonomyCommandConnector>(audit, scene);
        AsrDiagnosticRecorder asrRecorder = FindSingle<AsrDiagnosticRecorder>(audit, scene);
        RobotVoiceFeedbackDiagnosticRecorder feedbackRecorder = FindSingle<RobotVoiceFeedbackDiagnosticRecorder>(audit, scene);
        StructuredTtsFeedbackSink ttsSink = FindSingle<StructuredTtsFeedbackSink>(audit, scene);
        ExperimentConditionConfigBehaviour gate = FindSingle<ExperimentConditionConfigBehaviour>(audit, scene);

        Check(audit, asrRecorder != null && voiceController != null && ReferenceEquals(ReadObject<UnityEngine.Object>(voiceController, "_asrDiagnosticRecorder"), asrRecorder), "voice_controller_refs_asr_recorder", PathOf(asrRecorder));
        Check(audit, feedbackRecorder != null && voiceConnector != null && ReferenceEquals(ReadObject<UnityEngine.Object>(voiceConnector, "_robotVoiceFeedbackDiagnosticRecorder"), feedbackRecorder), "voice_connector_refs_feedback_recorder", PathOf(feedbackRecorder));
        Check(audit, ttsSink != null && voiceConnector != null && ttsSink.gameObject == voiceConnector.gameObject, "tts_sink_local_to_voice_connector", PathOf(ttsSink));
        Check(audit, ttsSink != null && ReferenceEquals(ReadObject<UnityEngine.Object>(voiceConnector, "_robotFeedbackSinkComponent"), ttsSink), "voice_connector_refs_tts_sink", PathOf(ReadObject<UnityEngine.Object>(voiceConnector, "_robotFeedbackSinkComponent")));
        Check(audit, ttsSink != null && ReferenceEquals(ReadObject<UnityEngine.Object>(ttsSink, "_experimentConditionProviderComponent"), gate), "tts_sink_refs_condition_gate", PathOf(ReadObject<UnityEngine.Object>(ttsSink, "_experimentConditionProviderComponent")));
        Check(audit, ttsSink != null && BoolField(ttsSink, "_enabled") && BoolField(ttsSink, "_onlyWhenVoiceEnabled") && !BoolField(ttsSink, "_diagnosticModeNoSpeech"), "tts_sink_voice_condition_gated", FieldSummary(ttsSink, "_enabled", "_onlyWhenVoiceEnabled", "_diagnosticModeNoSpeech"));
        Check(audit, ttsSink != null && StringField(ttsSink, "_preferredCulture").Equals("es-ES", StringComparison.OrdinalIgnoreCase), "tts_preferred_culture_es_es", FieldSummary(ttsSink, "_preferredCulture"));

        string preferredVoiceName = StringField(ttsSink, "_preferredVoiceName");
        bool spanishAutoOrHelena = string.IsNullOrWhiteSpace(preferredVoiceName) ||
                                   preferredVoiceName.IndexOf("Helena", StringComparison.OrdinalIgnoreCase) >= 0;
        bool accidentalEnglish = preferredVoiceName.IndexOf("en-US", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 preferredVoiceName.IndexOf("Zira", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 preferredVoiceName.IndexOf("David", StringComparison.OrdinalIgnoreCase) >= 0;
        Check(audit, spanishAutoOrHelena && !accidentalEnglish, "tts_voice_selector_not_english_test_voice", FieldSummary(ttsSink, "_preferredCulture", "_preferredVoiceName", "_preferredVoiceGender"));

        CheckTtsConditionGating(audit, ttsSink, gate);
    }

    private static void CheckTtsConditionGating(Audit audit, StructuredTtsFeedbackSink sink, ExperimentConditionConfigBehaviour gate)
    {
        if (sink == null || gate == null)
        {
            Check(audit, false, "tts_condition_gating_simulation", "missing sink or gate");
            return;
        }

        CapturingTtsBackend backend = new();
        sink.ConfigureForTests(enabled: true, diagnosticModeNoSpeech: false, maxQueueSize: 4, duplicateCooldownSeconds: 0f, backend);

        ApplyCondition(gate, false, false, "C00_robot_off_voice_off", RobotAssistanceMode.Disabled);
        sink.Emit(BuildAcceptanceMessage());
        sink.PumpForTests();
        int afterC00 = backend.Spoken.Count;

        ApplyCondition(gate, true, false, "C10_robot_on_voice_off", RobotAssistanceMode.AssistedSelection);
        sink.Emit(BuildAcceptanceMessage());
        sink.PumpForTests();
        int afterC10 = backend.Spoken.Count;

        ApplyCondition(gate, true, true, "C11_robot_on_voice_on", RobotAssistanceMode.AssistedSelection);
        sink.Emit(BuildAcceptanceMessage());
        sink.PumpForTests();
        int afterC11 = backend.Spoken.Count;

        Check(audit, afterC00 == 0 && afterC10 == 0 && afterC11 == 1, "tts_speaks_only_for_c11", $"afterC00={afterC00}, afterC10={afterC10}, afterC11={afterC11}");
    }

    private static void CheckNoPrimaryVoiceOnlySceneContext(Audit audit, Scene scene)
    {
        List<string> hits = new();
        foreach (Component component in AllComponents(scene))
        {
            if (component == null)
            {
                continue;
            }

            SerializedObject serialized = new(component);
            SerializedProperty iterator = serialized.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyType == SerializedPropertyType.String &&
                    string.Equals(iterator.stringValue, "voice_only", StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add($"{PathOf(component)}/{iterator.propertyPath}");
                }
            }
        }

        Check(audit, hits.Count == 0, "scene_has_no_primary_voice_only_context", hits.Count == 0 ? "none" : string.Join(" | ", hits));
    }

    private static void ApplyCondition(ExperimentConditionConfigBehaviour gate, bool robotEnabled, bool voiceEnabled, string conditionName, RobotAssistanceMode assistanceMode)
    {
        gate.ApplyConditionFromOrchestrator(robotEnabled, voiceEnabled, conditionName, assistanceMode, "final_scene_consolidation_audit");
    }

    private static RobotVoiceFeedbackMessage BuildAcceptanceMessage()
    {
        return new RobotVoiceFeedbackMessage(
            RobotVoiceFeedbackKind.OrderAccepted,
            string.Empty,
            new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.OrderAccepted,
                TargetAlias = "B1",
                Destination = "SELF"
            });
    }

    private static T FindSingle<T>(Audit audit, Scene scene) where T : Component
    {
        T[] values = AllComponents(scene).OfType<T>().ToArray();
        if (values.Length != 1)
        {
            Check(audit, false, "single_" + typeof(T).Name, values.Length.ToString(CultureInfo.InvariantCulture));
            return values.FirstOrDefault();
        }

        Check(audit, true, "single_" + typeof(T).Name, PathOf(values[0]));
        return values[0];
    }

    private static IEnumerable<GameObject> AllGameObjects(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                yield return transform.gameObject;
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

    private static IEnumerable<Component> AllComponents(Scene scene)
    {
        foreach (GameObject gameObject in AllGameObjects(scene))
        {
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component != null)
                {
                    yield return component;
                }
            }
        }
    }

    private static bool IsEnabled(Component component)
    {
        return component is not Behaviour behaviour || behaviour.enabled;
    }

    private static bool IsUnderRoot(Transform transform, string rootName)
    {
        return transform != null && transform.root != null && string.Equals(transform.root.name, rootName, StringComparison.OrdinalIgnoreCase);
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

    private static string StringField(UnityEngine.Object target, string propertyPath)
    {
        SerializedProperty property = FindProperty(target, propertyPath);
        return property != null && property.propertyType == SerializedPropertyType.String ? property.stringValue ?? string.Empty : string.Empty;
    }

    private static bool EnumFieldEquals<T>(UnityEngine.Object target, string propertyPath, T expected) where T : struct, Enum
    {
        SerializedProperty property = FindProperty(target, propertyPath);
        return property != null &&
               property.propertyType == SerializedPropertyType.Enum &&
               string.Equals(property.enumNames[property.enumValueIndex], expected.ToString(), StringComparison.Ordinal);
    }

    private static T EnumValue<T>(SerializedProperty property) where T : struct, Enum
    {
        if (property == null || property.propertyType != SerializedPropertyType.Enum)
        {
            return default;
        }

        return Enum.TryParse(property.enumNames[property.enumValueIndex], out T value) ? value : default;
    }

    private static T ReadObject<T>(UnityEngine.Object target, string propertyPath) where T : UnityEngine.Object
    {
        return FindProperty(target, propertyPath)?.objectReferenceValue as T;
    }

    private static SerializedProperty FindProperty(UnityEngine.Object target, string propertyPath)
    {
        if (target == null)
        {
            return null;
        }

        return new SerializedObject(target).FindProperty(propertyPath);
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
            SerializedPropertyType.Integer => property.intValue.ToString(CultureInfo.InvariantCulture),
            SerializedPropertyType.ObjectReference => PathOf(property.objectReferenceValue),
            _ => property.type
        };
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

    private static void Check(Audit audit, bool passed, string name, string detail)
    {
        string line = $"{name}: {detail}";
        if (passed)
        {
            audit.Passes.Add(line);
        }
        else
        {
            audit.Failures.Add(line);
        }
    }

    private static string WriteReport(Audit audit, Scene scene)
    {
        string outputRoot = Path.Combine(Directory.GetCurrentDirectory(), OutputDirectory);
        Directory.CreateDirectory(outputRoot);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(outputRoot, $"final_scene_consolidation_audit_{timestamp}.md");

        StringBuilder builder = new();
        builder.AppendLine("# P43E Final Scene Consolidation Audit");
        builder.AppendLine();
        builder.AppendLine($"- Scene: `{scene.path}`");
        builder.AppendLine($"- Exported at: `{DateTime.Now:o}`");
        builder.AppendLine($"- Result: `{(audit.Failures.Count == 0 ? "PASS" : "FAIL")}`");
        builder.AppendLine($"- Passed: `{audit.Passes.Count}`");
        builder.AppendLine($"- Failed: `{audit.Failures.Count}`");
        builder.AppendLine();
        AppendList(builder, "Failures", audit.Failures);
        AppendList(builder, "Passes", audit.Passes);
        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);

        return $"[FinalSceneConsolidationAuditUtility] result={(audit.Failures.Count == 0 ? "PASS" : "FAIL")} passed={audit.Passes.Count} failed={audit.Failures.Count} report={path}";
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

    private sealed class CapturingTtsBackend : ITtsSpeechBackend
    {
        public readonly List<string> Spoken = new();

        public bool IsAvailable => true;
        public bool IsSpeaking { get; private set; }
        public string UnavailableReason => string.Empty;

        public void Configure(string languageOrVoice, float volume, float rate)
        {
        }

        public bool TrySpeak(string text, out string failureReason)
        {
            failureReason = string.Empty;
            Spoken.Add(text);
            IsSpeaking = false;
            return true;
        }

        public void Tick()
        {
            IsSpeaking = false;
        }

        public void Stop()
        {
            IsSpeaking = false;
        }
    }
}
