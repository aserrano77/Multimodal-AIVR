using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class P44PilotRunReadinessUtility
{
    private const string ScenePath = "Assets/Scenes/autonomous_demo_step22_multimodal_bridge.unity";
    private const string OutputDirectory = "Logs/P44PilotRunReadiness";
    private const string OfflineAnalysisPath = "analysis/offline_experiment_analysis.py";
    private const string OrchestratorPath = "Assets/Scripts/ExperimentSessionOrchestrator.cs";
    private const int SafeWindowsPathLengthWarningThreshold = 240;

    private static readonly ConditionExpectation[] ExpectedConditions =
    {
        new("C00_robot_off_voice_off", "Robot OFF + Voice OFF", false, false, RobotAssistanceMode.Disabled),
        new("C10_robot_on_voice_off", "Robot ON + Voice OFF", true, false, RobotAssistanceMode.AssistedSelection),
        new("C11_robot_on_voice_on", "Robot ON + Voice ON", true, true, RobotAssistanceMode.AssistedSelection)
    };

    private sealed class ReadinessReport
    {
        public readonly List<string> Passes = new();
        public readonly List<string> Warnings = new();
        public readonly List<string> Failures = new();
        public readonly List<string> ManualChecks = new();

        public string Recommendation
        {
            get
            {
                if (Failures.Count > 0)
                {
                    return "not_ready";
                }

                return Warnings.Count > 0 || ManualChecks.Count > 0
                    ? "ready_with_manual_microvalidation"
                    : "ready_for_formal_pilot";
            }
        }
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

    [MenuItem("Tools/TFG/P44/Pilot Run Readiness Audit")]
    public static void RunFromMenu()
    {
        RunForBatch();
    }

    public static void RunForBatch()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        ReadinessReport report = BuildReport(scene);
        string summary = WriteReport(report, scene);
        if (report.Failures.Count > 0)
        {
            throw new InvalidOperationException(summary);
        }

        Debug.Log(summary);
    }

    [MenuItem("Tools/TFG/P44A/Configure XR Rig Reset")]
    public static void ConfigureXrRigResetFromMenu()
    {
        ConfigureXrRigResetForBatch();
    }

    public static void ConfigureXrRigResetForBatch()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        ExperimentSessionOrchestrator orchestrator = AllComponents(scene).OfType<ExperimentSessionOrchestrator>().FirstOrDefault();
        if (orchestrator == null)
        {
            throw new InvalidOperationException("ExperimentSessionOrchestrator missing in target scene.");
        }

        Component xrOrigin = FindXrOriginComponent(scene);
        Transform rigRoot = xrOrigin != null
            ? xrOrigin.transform
            : AllTransforms(scene).FirstOrDefault(transform => transform.name.IndexOf("XR Origin", StringComparison.OrdinalIgnoreCase) >= 0);
        CharacterController characterController = rigRoot != null ? rigRoot.GetComponentInChildren<CharacterController>(true) : null;
        Transform anchor = FindOrCreateParticipantStartAnchor(scene, rigRoot);

        ExperimentXrRigResetter resetter = orchestrator.GetComponent<ExperimentXrRigResetter>();
        if (resetter == null)
        {
            resetter = orchestrator.gameObject.AddComponent<ExperimentXrRigResetter>();
        }

        SetObject(resetter, "_rigRoot", rigRoot);
        SetObject(resetter, "_xrOrigin", xrOrigin);
        SetObject(resetter, "_characterController", characterController);
        SetObject(resetter, "_participantStartAnchor", anchor);
        SetObject(orchestrator, "_xrRigResetter", resetter);

        EditorUtility.SetDirty(resetter);
        EditorUtility.SetDirty(orchestrator);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[P44PilotRunReadinessUtility] XR rig reset configured | rig={PathOf(rigRoot)} anchor={PathOf(anchor)} resetter={PathOf(resetter)}");
    }

    private static Component FindXrOriginComponent(Scene scene)
    {
        return AllComponents(scene)
            .FirstOrDefault(component =>
                component != null &&
                string.Equals(component.GetType().Name, "XROrigin", StringComparison.Ordinal));
    }

    private static ReadinessReport BuildReport(Scene scene)
    {
        var report = new ReadinessReport();
        Check(report, scene.IsValid() && scene.path == ScenePath, "target_scene_open", scene.path);
        if (!scene.IsValid())
        {
            return report;
        }

        CheckRequiredRoots(report, scene);
        CheckMissingScripts(report, scene);
        CheckNoActiveStepOutsideDebugLegacy(report, scene);
        CheckFinalMatrixAndGating(report, scene);
        CheckXrRigResetReadiness(report, scene);
        CheckDepositZoneReadiness(report, scene);
        CheckExperimentalBoxPhysicsReadiness(report, scene);
        CheckPilotArtifactReadiness(report, scene);
        CheckTrialSummaryPathReadiness(report, scene);
        CheckOfflineAnalysisReadiness(report);
        AddManualPilotChecks(report);
        return report;
    }

    private static void CheckRequiredRoots(ReadinessReport report, Scene scene)
    {
        string[] expectedRoots = { "Experiment", "Autonomy", "MultimodalInput", "Diagnostics", "DebugLegacy" };
        HashSet<string> roots = scene.GetRootGameObjects().Select(root => root.name).ToHashSet(StringComparer.Ordinal);
        foreach (string root in expectedRoots)
        {
            Check(report, roots.Contains(root), "root_present_" + root, root);
        }
    }

    private static void CheckMissingScripts(ReadinessReport report, Scene scene)
    {
        int missing = AllGameObjects(scene).Sum(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount);
        Check(report, missing == 0, "missing_scripts_zero", missing.ToString(CultureInfo.InvariantCulture));
    }

    private static void CheckNoActiveStepOutsideDebugLegacy(ReadinessReport report, Scene scene)
    {
        List<string> activeStepObjects = AllTransforms(scene)
            .Where(transform => transform.gameObject.activeInHierarchy)
            .Where(transform => transform.name.StartsWith("STEP", StringComparison.OrdinalIgnoreCase))
            .Where(transform => !IsUnderRoot(transform, "DebugLegacy"))
            .Select(PathOf)
            .ToList();

        Check(report,
            activeStepObjects.Count == 0,
            "no_active_step_objects_outside_debuglegacy",
            activeStepObjects.Count == 0 ? "none" : string.Join(" | ", activeStepObjects));
    }

    private static void CheckFinalMatrixAndGating(ReadinessReport report, Scene scene)
    {
        ExperimentSessionOrchestrator orchestrator = FindSingle<ExperimentSessionOrchestrator>(report, scene);
        ExperimentSessionOrchestrator[] orchestrators = AllComponents(scene).OfType<ExperimentSessionOrchestrator>().ToArray();
        int activeOrchestratorCount = orchestrators.Count(IsActiveBehaviour);
        string orchestratorPaths = string.Join(" | ", orchestrators.Select(value => $"{PathOf(value)} active={IsActiveBehaviour(value)}"));
        Check(report, true, "experiment_orchestrator_paths", string.IsNullOrWhiteSpace(orchestratorPaths) ? "none" : orchestratorPaths);
        Check(report, activeOrchestratorCount == 1, "single_active_ExperimentSessionOrchestrator", $"active={activeOrchestratorCount} total={orchestrators.Length} paths={orchestratorPaths}");
        ExperimentConditionConfigBehaviour gate = FindSingle<ExperimentConditionConfigBehaviour>(report, scene);
        ExperimentInstrumentationController instrumentation = FindSingle<ExperimentInstrumentationController>(report, scene);
        AutonomousRobotAdapter robotAdapter = FindSingle<AutonomousRobotAdapter>(report, scene);
        MultimodalAutonomyCommandBridge bridge = FindSingle<MultimodalAutonomyCommandBridge>(report, scene);
        VoiceAutonomyCommandConnector voiceConnector = FindSingle<VoiceAutonomyCommandConnector>(report, scene);
        VoiceRecognitionController voiceController = FindSingle<VoiceRecognitionController>(report, scene);
        StructuredTtsFeedbackSink ttsSink = FindSingle<StructuredTtsFeedbackSink>(report, scene);
        RobotVoiceFeedbackDiagnosticRecorder ttsRecorder = FindSingle<RobotVoiceFeedbackDiagnosticRecorder>(report, scene);
        AsrDiagnosticRecorder asrRecorder = FindSingle<AsrDiagnosticRecorder>(report, scene);

        Check(report, IsActiveBehaviour(orchestrator), "orchestrator_active", PathOf(orchestrator));
        Check(report, EnumFieldEquals(orchestrator, "_runMode", ExperimentRunMode.Orchestrated2x2), "orchestrator_uses_orchestrated_final_plan", FieldSummary(orchestrator, "_runMode"));
        Check(report, IntField(orchestrator, "_selectedConditionIndex") == 0, "selected_condition_defaults_to_c00", FieldSummary(orchestrator, "_selectedConditionIndex"));
        Check(report, IntField(orchestrator, "_roundsPerCondition") >= 1, "rounds_per_condition_positive", FieldSummary(orchestrator, "_roundsPerCondition"));
        CheckConditionMatrix(report, orchestrator);

        Check(report,
            IsActiveBehaviour(gate) &&
            !BoolField(gate, "_robotEnabled") &&
            !BoolField(gate, "_voiceEnabled") &&
            StringField(gate, "_conditionName") == "uninitialized" &&
            EnumFieldEquals(gate, "_assistanceMode", RobotAssistanceMode.Disabled) &&
            EnumFieldEquals(gate, "_runMode", ExperimentRunMode.Orchestrated2x2),
            "condition_gate_neutral_before_play",
            FieldSummary(gate, "_robotEnabled", "_voiceEnabled", "_conditionName", "_assistanceMode", "_runMode"));

        Check(report,
            IsActiveBehaviour(instrumentation) &&
            StringField(instrumentation, "_conditionId") == "uninitialized" &&
            StringField(instrumentation, "_conditionName") == "uninitialized" &&
            !BoolField(instrumentation, "_robotEnabled") &&
            !BoolField(instrumentation, "_voiceEnabled") &&
            StringField(instrumentation, "_assistanceMode") == "Disabled" &&
            !BoolField(instrumentation, "_configureSessionOnStart") &&
            !BoolField(instrumentation, "_startTrialOnStart") &&
            EnumFieldEquals(instrumentation, "_runMode", ExperimentRunMode.Orchestrated2x2),
            "instrumentation_neutral_before_play",
            FieldSummary(instrumentation, "_conditionId", "_conditionName", "_robotEnabled", "_voiceEnabled", "_assistanceMode", "_configureSessionOnStart", "_startTrialOnStart", "_runMode"));

        Check(report, ReferenceEquals(ReadObject<UnityEngine.Object>(orchestrator, "_conditionConfig"), gate), "orchestrator_refs_condition_gate", PathOf(ReadObject<UnityEngine.Object>(orchestrator, "_conditionConfig")));
        Check(report, ReferenceEquals(ReadObject<UnityEngine.Object>(orchestrator, "_instrumentation"), instrumentation), "orchestrator_refs_instrumentation", PathOf(ReadObject<UnityEngine.Object>(orchestrator, "_instrumentation")));
        Check(report, ReferenceEquals(ReadObject<UnityEngine.Object>(orchestrator, "_voiceRecognitionController"), voiceController), "orchestrator_refs_voice_controller", PathOf(ReadObject<UnityEngine.Object>(orchestrator, "_voiceRecognitionController")));
        Check(report, ReferenceEquals(ReadObject<UnityEngine.Object>(orchestrator, "_voiceConnector"), voiceConnector), "orchestrator_refs_voice_connector", PathOf(ReadObject<UnityEngine.Object>(orchestrator, "_voiceConnector")));
        Check(report, ReferenceEquals(ReadObject<UnityEngine.Object>(orchestrator, "_commandBridge"), bridge), "orchestrator_refs_bridge", PathOf(ReadObject<UnityEngine.Object>(orchestrator, "_commandBridge")));
        Check(report, ReferenceEquals(ReadObject<UnityEngine.Object>(orchestrator, "_robotAdapter"), robotAdapter), "orchestrator_refs_robot_adapter", PathOf(ReadObject<UnityEngine.Object>(orchestrator, "_robotAdapter")));
        Check(report, ReferenceEquals(ReadObject<UnityEngine.Object>(voiceConnector, "_experimentConditionProviderComponent"), gate), "voice_connector_refs_condition_gate", PathOf(ReadObject<UnityEngine.Object>(voiceConnector, "_experimentConditionProviderComponent")));
        Check(report, ReferenceEquals(ReadObject<UnityEngine.Object>(voiceConnector, "_robotFeedbackSinkComponent"), ttsSink), "voice_connector_refs_tts_sink", PathOf(ReadObject<UnityEngine.Object>(voiceConnector, "_robotFeedbackSinkComponent")));
        Check(report, ReferenceEquals(ReadObject<UnityEngine.Object>(voiceConnector, "_robotVoiceFeedbackDiagnosticRecorder"), ttsRecorder), "voice_connector_refs_tts_diagnostic_recorder", PathOf(ReadObject<UnityEngine.Object>(voiceConnector, "_robotVoiceFeedbackDiagnosticRecorder")));
        Check(report, ReferenceEquals(ReadObject<UnityEngine.Object>(voiceController, "_asrDiagnosticRecorder"), asrRecorder), "voice_controller_refs_asr_recorder", PathOf(ReadObject<UnityEngine.Object>(voiceController, "_asrDiagnosticRecorder")));
        Check(report, ttsSink != null && ReferenceEquals(ReadObject<UnityEngine.Object>(ttsSink, "_experimentConditionProviderComponent"), gate), "tts_sink_refs_condition_gate", PathOf(ReadObject<UnityEngine.Object>(ttsSink, "_experimentConditionProviderComponent")));
        Check(report,
            ttsSink != null &&
            BoolField(ttsSink, "_enabled") &&
            BoolField(ttsSink, "_onlyWhenVoiceEnabled") &&
            !BoolField(ttsSink, "_diagnosticModeNoSpeech") &&
            StringField(ttsSink, "_preferredCulture").Equals("es-ES", StringComparison.OrdinalIgnoreCase),
            "tts_runtime_voice_gated_and_spanish",
            FieldSummary(ttsSink, "_enabled", "_onlyWhenVoiceEnabled", "_diagnosticModeNoSpeech", "_preferredCulture", "_preferredVoiceName"));

        CheckOperationalControlRoutes(report, voiceConnector, bridge, robotAdapter);
        CheckTtsConditionGating(report, ttsSink, gate);
    }

    private static void CheckXrRigResetReadiness(ReadinessReport report, Scene scene)
    {
        ExperimentXrRigResetter[] resetters = AllComponents(scene).OfType<ExperimentXrRigResetter>().ToArray();
        if (resetters.Length == 0)
        {
            AddWarning(report, "xr_rig_reset_configuration_missing: no ExperimentXrRigResetter found; Play Mode is still valid on PC, but participant recentering will not be automatic.");
            return;
        }

        Check(report, resetters.Length == 1, "single_ExperimentXrRigResetter", resetters.Length == 1 ? PathOf(resetters[0]) : resetters.Length.ToString(CultureInfo.InvariantCulture));
        ExperimentXrRigResetter resetter = resetters[0];
        Check(report, IsActiveBehaviour(resetter), "xr_rig_resetter_active", PathOf(resetter));
        Check(report, ReadObject<UnityEngine.Object>(resetter, "_participantStartAnchor") != null, "xr_rig_start_anchor_assigned", PathOf(ReadObject<UnityEngine.Object>(resetter, "_participantStartAnchor")));
        Check(report, ReadObject<UnityEngine.Object>(resetter, "_rigRoot") != null || ReadObject<UnityEngine.Object>(resetter, "_xrOrigin") != null, "xr_rig_root_or_origin_assigned", FieldSummary(resetter, "_rigRoot", "_xrOrigin"));

        ExperimentSessionOrchestrator orchestrator = AllComponents(scene).OfType<ExperimentSessionOrchestrator>().FirstOrDefault();
        Check(report, ReferenceEquals(ReadObject<UnityEngine.Object>(orchestrator, "_xrRigResetter"), resetter), "orchestrator_refs_xr_rig_resetter", PathOf(ReadObject<UnityEngine.Object>(orchestrator, "_xrRigResetter")));
    }

    private static void CheckDepositZoneReadiness(ReadinessReport report, Scene scene)
    {
        DepositZone[] zones = AllComponents(scene).OfType<DepositZone>().ToArray();
        Check(report, zones.Length == 3, "deposit_zone_count_abc", zones.Length == 3 ? string.Join(" | ", zones.Select(DescribeDepositZone)) : zones.Length.ToString(CultureInfo.InvariantCulture));

        foreach (BoxMetadata.BoxType expectedType in Enum.GetValues(typeof(BoxMetadata.BoxType)))
        {
            DepositZone[] matching = zones.Where(zone => zone != null && zone.acceptedType == expectedType).ToArray();
            string expectedName = "DepositZone" + expectedType;
            Check(report, matching.Length == 1, "deposit_zone_single_category_" + expectedType, matching.Length == 1 ? DescribeDepositZone(matching[0]) : matching.Length.ToString(CultureInfo.InvariantCulture));
            if (matching.Length == 0)
            {
                continue;
            }

            DepositZone zone = matching[0];
            Collider collider = zone.GetComponent<Collider>();
            Check(report, IsActiveBehaviour(zone), "deposit_zone_active_" + expectedType, PathOf(zone));
            Check(report, string.Equals(zone.gameObject.name, expectedName, StringComparison.Ordinal), "deposit_zone_name_" + expectedType, PathOf(zone));
            Check(report, collider != null, "deposit_zone_collider_present_" + expectedType, DescribeDepositZone(zone));
            Check(report, collider != null && collider.enabled, "deposit_zone_collider_enabled_" + expectedType, DescribeDepositZone(zone));
            Check(report, collider != null && collider.isTrigger, "deposit_zone_collider_is_trigger_" + expectedType, DescribeDepositZone(zone));
            Check(report, zone.transform.childCount >= 2, "deposit_zone_has_place_and_navigation_children_" + expectedType, $"children={zone.transform.childCount} {DescribeDepositZone(zone)}");
            if (collider is BoxCollider box)
            {
                Check(report,
                    box.size.x > 0f && box.size.y > 0f && box.size.z > 0f,
                    "deposit_zone_box_collider_positive_size_" + expectedType,
                    $"size={box.size} center={box.center} {DescribeDepositZone(zone)}");
            }
        }

        DepositZone zoneA = zones.FirstOrDefault(zone => zone != null && zone.acceptedType == BoxMetadata.BoxType.A);
        DepositZone zoneB = zones.FirstOrDefault(zone => zone != null && zone.acceptedType == BoxMetadata.BoxType.B);
        DepositZone zoneC = zones.FirstOrDefault(zone => zone != null && zone.acceptedType == BoxMetadata.BoxType.C);
        if (zoneA != null && zoneB != null && zoneC != null)
        {
            Check(report, DepositZoneColliderShapeMatches(zoneA, zoneB), "deposit_zone_b_shape_matches_a", $"{DescribeDepositZone(zoneB)} vs {DescribeDepositZone(zoneA)}");
            Check(report, DepositZoneColliderShapeMatches(zoneB, zoneC), "deposit_zone_b_shape_matches_c", $"{DescribeDepositZone(zoneB)} vs {DescribeDepositZone(zoneC)}");
        }
    }

    private static void CheckConditionMatrix(ReadinessReport report, ExperimentSessionOrchestrator orchestrator)
    {
        SerializedProperty conditions = FindProperty(orchestrator, "_conditions");
        int count = conditions != null && conditions.isArray ? conditions.arraySize : 0;
        Check(report, count == ExpectedConditions.Length, "condition_matrix_exactly_c00_c10_c11", $"count={count}");
        if (conditions == null || !conditions.isArray)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < conditions.arraySize; i++)
        {
            SerializedProperty item = conditions.GetArrayElementAtIndex(i);
            string actualId = item.FindPropertyRelative("ConditionId")?.stringValue ?? string.Empty;
            seen.Add(actualId);
            if (i >= ExpectedConditions.Length)
            {
                continue;
            }

            ConditionExpectation expected = ExpectedConditions[i];
            string actualName = item.FindPropertyRelative("ConditionName")?.stringValue ?? string.Empty;
            bool actualRobot = item.FindPropertyRelative("RobotEnabled")?.boolValue ?? false;
            bool actualVoice = item.FindPropertyRelative("VoiceEnabled")?.boolValue ?? false;
            RobotAssistanceMode actualAssistance = EnumValue<RobotAssistanceMode>(item.FindPropertyRelative("AssistanceMode"));
            Check(report,
                actualId == expected.Id &&
                actualName == expected.Name &&
                actualRobot == expected.RobotEnabled &&
                actualVoice == expected.VoiceEnabled &&
                actualAssistance == expected.AssistanceMode,
                "condition_" + expected.Id,
                $"index={i}, id={actualId}, name={actualName}, robot={actualRobot}, voice={actualVoice}, assistance={actualAssistance}");
        }

        Check(report, seen.SetEquals(ExpectedConditions.Select(condition => condition.Id)), "condition_ids_are_final_set_only", string.Join(" | ", seen));
        Check(report, !seen.Contains("C01_robot_off_voice_on"), "c01_not_active_in_p44_plan", "C01 legacy condition absent from active orchestrator matrix");
    }

    private static void CheckOperationalControlRoutes(
        ReadinessReport report,
        VoiceAutonomyCommandConnector voiceConnector,
        MultimodalAutonomyCommandBridge bridge,
        AutonomousRobotAdapter robotAdapter)
    {
        bool routerHasStopHandler = typeof(VoiceAutonomyCommandRouter)
            .GetConstructors()
            .Any(ctor => ctor.GetParameters().Any(parameter => parameter.Name == "applyStopCommand"));
        bool routerHasResumeHandler = typeof(VoiceAutonomyCommandRouter)
            .GetConstructors()
            .Any(ctor => ctor.GetParameters().Any(parameter => parameter.Name == "applyResumeCommand"));
        bool adapterHasStop = typeof(AutonomousRobotAdapter).GetMethod(nameof(AutonomousRobotAdapter.ApplyVoiceStopCommand)) != null;
        bool adapterHasResume = typeof(AutonomousRobotAdapter).GetMethod(nameof(AutonomousRobotAdapter.ApplyVoiceResumeCommand)) != null;
        bool sceneRouteWired = voiceConnector != null && bridge != null && robotAdapter != null && ReferenceEquals(bridge.RobotAdapter, robotAdapter);

        Check(report,
            routerHasStopHandler && adapterHasStop && sceneRouteWired,
            "stop_command_operational_route_wired",
            $"router={routerHasStopHandler}, adapter={adapterHasStop}, sceneRoute={sceneRouteWired}");
        Check(report,
            routerHasResumeHandler && adapterHasResume && sceneRouteWired,
            "resume_command_operational_route_wired",
            $"router={routerHasResumeHandler}, adapter={adapterHasResume}, sceneRoute={sceneRouteWired}");
    }

    private static void CheckTtsConditionGating(ReadinessReport report, StructuredTtsFeedbackSink sink, ExperimentConditionConfigBehaviour gate)
    {
        if (sink == null || gate == null)
        {
            Check(report, false, "tts_gating_c00_c10_c11_simulation", "missing sink or gate");
            return;
        }

        CapturingTtsBackend backend = new();
        sink.ConfigureForTests(enabled: true, diagnosticModeNoSpeech: false, maxQueueSize: 4, duplicateCooldownSeconds: 0f, backend);

        ApplyCondition(gate, ExpectedConditions[0]);
        sink.Emit(BuildAcceptanceMessage());
        sink.PumpForTests();
        int afterC00 = backend.Spoken.Count;

        ApplyCondition(gate, ExpectedConditions[1]);
        sink.Emit(BuildAcceptanceMessage());
        sink.PumpForTests();
        int afterC10 = backend.Spoken.Count;

        ApplyCondition(gate, ExpectedConditions[2]);
        sink.Emit(BuildAcceptanceMessage());
        sink.PumpForTests();
        int afterC11 = backend.Spoken.Count;

        Check(report,
            afterC00 == 0 && afterC10 == 0 && afterC11 == 1,
            "tts_audible_only_in_c11",
            $"afterC00={afterC00}, afterC10={afterC10}, afterC11={afterC11}");

        ApplyCondition(gate, false, false, "uninitialized", RobotAssistanceMode.Disabled);
    }

    private static void CheckExperimentalBoxPhysicsReadiness(ReadinessReport report, Scene scene)
    {
        SpawnManager[] spawnManagers = AllComponents(scene).OfType<SpawnManager>().ToArray();
        Check(report, spawnManagers.Length == 1, "p45_single_spawn_manager_for_box_physics", spawnManagers.Length == 1 ? PathOf(spawnManagers[0]) : spawnManagers.Length.ToString(CultureInfo.InvariantCulture));
        SpawnManager spawnManager = spawnManagers.FirstOrDefault();
        if (spawnManager == null)
        {
            return;
        }

        GameObject[] prefabs = (spawnManager.boxPrefabs ?? Array.Empty<GameObject>())
            .Where(prefab => prefab != null)
            .Distinct()
            .ToArray();
        Check(report, prefabs.Length == 3, "p45_experimental_box_prefab_count_abc", string.Join(" | ", prefabs.Select(PathOf)));

        HashSet<BoxMetadata.BoxType> coveredTypes = new();
        foreach (GameObject prefab in prefabs)
        {
            BoxMetadata metadata = prefab.GetComponent<BoxMetadata>();
            Rigidbody rigidbody = prefab.GetComponent<Rigidbody>();
            Collider collider = prefab.GetComponent<Collider>();
            Component grabInteractable = prefab.GetComponents<Component>()
                .FirstOrDefault(component => component != null && string.Equals(component.GetType().Name, "XRGrabInteractable", StringComparison.Ordinal));
            BoxReleaseVelocityLimiter releaseLimiter = prefab.GetComponent<BoxReleaseVelocityLimiter>();

            if (metadata != null)
            {
                coveredTypes.Add(metadata.boxType);
            }

            SerializedObject rigidbodyObject = rigidbody != null ? new SerializedObject(rigidbody) : null;
            float mass = FloatProperty(rigidbodyObject, "m_Mass");
            float drag = FloatProperty(rigidbodyObject, "m_Drag");
            float angularDrag = FloatProperty(rigidbodyObject, "m_AngularDrag");
            int collisionDetection = IntProperty(rigidbodyObject, "m_CollisionDetection");
            PhysicsMaterial material = collider != null ? collider.sharedMaterial : null;

            SerializedObject grabObject = grabInteractable != null ? new SerializedObject(grabInteractable) : null;
            bool throwOnDetach = BoolProperty(grabObject, "m_ThrowOnDetach", defaultValue: true);
            float throwVelocityScale = FloatProperty(grabObject, "m_ThrowVelocityScale");
            float throwAngularVelocityScale = FloatProperty(grabObject, "m_ThrowAngularVelocityScale");
            float maxReleaseHorizontalSpeed = releaseLimiter != null ? releaseLimiter.MaxReleaseHorizontalSpeed : float.NaN;
            float maxReleaseUpwardSpeed = releaseLimiter != null ? releaseLimiter.MaxReleaseUpwardSpeed : float.NaN;
            float maxReleaseAngularSpeed = releaseLimiter != null ? releaseLimiter.MaxReleaseAngularSpeed : float.NaN;

            string detail =
                $"prefab={PathOf(prefab)}, category={(metadata != null ? metadata.boxType.ToString() : "missing")}, " +
                $"mass={FormatFloat(mass)}, drag={FormatFloat(drag)}, angularDrag={FormatFloat(angularDrag)}, collisionDetection={collisionDetection}, " +
                $"physicMaterial={PathOf(material)}, dynamicFriction={FormatFloat(material != null ? material.dynamicFriction : float.NaN)}, " +
                $"staticFriction={FormatFloat(material != null ? material.staticFriction : float.NaN)}, bounciness={FormatFloat(material != null ? material.bounciness : float.NaN)}, " +
                $"throwOnDetach={throwOnDetach}, throwVelocityScale={FormatFloat(throwVelocityScale)}, throwAngularVelocityScale={FormatFloat(throwAngularVelocityScale)}, " +
                $"releaseLimiter={PathOf(releaseLimiter)}, maxReleaseHorizontalSpeed={FormatFloat(maxReleaseHorizontalSpeed)}, maxReleaseUpwardSpeed={FormatFloat(maxReleaseUpwardSpeed)}, maxReleaseAngularSpeed={FormatFloat(maxReleaseAngularSpeed)}";
            Check(report, true, "p45_box_physics_profile_" + prefab.name, detail);

            if (rigidbody == null)
            {
                AddWarning(report, $"p45_box_physics_rigidbody_missing: {PathOf(prefab)}");
                continue;
            }

            if (mass < 2f)
            {
                AddWarning(report, $"p45_box_mass_low: {PathOf(prefab)} mass={FormatFloat(mass)}");
            }

            if (drag < 0.1f)
            {
                AddWarning(report, $"p45_box_drag_low: {PathOf(prefab)} drag={FormatFloat(drag)}");
            }

            if (angularDrag < 0.5f)
            {
                AddWarning(report, $"p45_box_angular_drag_low: {PathOf(prefab)} angularDrag={FormatFloat(angularDrag)}");
            }

            if (collisionDetection == 0)
            {
                AddWarning(report, $"p45_box_collision_detection_discrete: {PathOf(prefab)}");
            }

            if (collider == null)
            {
                AddWarning(report, $"p45_box_collider_missing: {PathOf(prefab)}");
            }
            else if (material == null)
            {
                AddWarning(report, $"p45_box_friction_material_missing: {PathOf(prefab)} collider={collider.GetType().Name}");
            }
            else
            {
                if (material.dynamicFriction <= 0.05f || material.staticFriction <= 0.05f)
                {
                    AddWarning(report, $"p45_box_friction_near_zero: {PathOf(prefab)} material={PathOf(material)} dynamic={FormatFloat(material.dynamicFriction)} static={FormatFloat(material.staticFriction)}");
                }

                if (material.bounciness > 0.05f)
                {
                    AddWarning(report, $"p45_box_bounciness_high: {PathOf(prefab)} material={PathOf(material)} bounciness={FormatFloat(material.bounciness)}");
                }
            }

            if (grabInteractable == null)
            {
                AddWarning(report, $"p45_box_xr_grab_interactable_missing: {PathOf(prefab)}");
            }
            else
            {
                if (throwOnDetach)
                {
                    AddWarning(report, $"p45_box_throw_on_detach_active: {PathOf(prefab)}");
                }

                if (throwVelocityScale > 0.25f || throwAngularVelocityScale > 0.15f)
                {
                    AddWarning(report, $"p45_box_throw_scale_high: {PathOf(prefab)} velocityScale={FormatFloat(throwVelocityScale)} angularScale={FormatFloat(throwAngularVelocityScale)}");
                }
            }

            if (releaseLimiter == null)
            {
                AddWarning(report, $"p45_box_release_velocity_limiter_missing: {PathOf(prefab)}");
            }
            else
            {
                if (maxReleaseHorizontalSpeed <= 0f || maxReleaseHorizontalSpeed > 0.75f)
                {
                    AddWarning(report, $"p45_box_release_horizontal_limit_outside_expected_range: {PathOf(prefab)} maxReleaseHorizontalSpeed={FormatFloat(maxReleaseHorizontalSpeed)}");
                }

                if (maxReleaseUpwardSpeed < 0f || maxReleaseUpwardSpeed > 0.25f)
                {
                    AddWarning(report, $"p45_box_release_upward_limit_outside_expected_range: {PathOf(prefab)} maxReleaseUpwardSpeed={FormatFloat(maxReleaseUpwardSpeed)}");
                }

                if (maxReleaseAngularSpeed <= 0f || maxReleaseAngularSpeed > 3f)
                {
                    AddWarning(report, $"p45_box_release_angular_limit_outside_expected_range: {PathOf(prefab)} maxReleaseAngularSpeed={FormatFloat(maxReleaseAngularSpeed)}");
                }
            }
        }

        string covered = string.Join(" | ", coveredTypes.OrderBy(value => value).Select(value => value.ToString()));
        bool coversFinalCategories = coveredTypes.SetEquals(Enum.GetValues(typeof(BoxMetadata.BoxType)).Cast<BoxMetadata.BoxType>());
        Check(report, coversFinalCategories, "p45_box_prefabs_cover_categories_abc", string.IsNullOrWhiteSpace(covered) ? "none" : covered);
    }

    private static void CheckPilotArtifactReadiness(ReadinessReport report, Scene scene)
    {
        TiagoExperimentLogger logger = FindSingle<TiagoExperimentLogger>(report, scene);
        Check(report, IsActiveBehaviour(logger), "experiment_logger_active", PathOf(logger));
        Check(report, logger != null && BoolField(logger, "_startOnAwake"), "experiment_logger_start_on_awake", FieldSummary(logger, "_startOnAwake", "_runLabel"));
        Check(report, typeof(TiagoExperimentLogger).GetProperty(nameof(TiagoExperimentLogger.ManifestPath)) != null, "logger_exposes_manifest_path", nameof(TiagoExperimentLogger.ManifestPath));
        Check(report, typeof(TiagoExperimentLogger).GetProperty(nameof(TiagoExperimentLogger.EventPath)) != null, "logger_exposes_events_path", nameof(TiagoExperimentLogger.EventPath));
        Check(report, typeof(TiagoExperimentLogger).GetProperty(nameof(TiagoExperimentLogger.SamplePath)) != null, "logger_exposes_samples_path", nameof(TiagoExperimentLogger.SamplePath));

        string header = ExperimentSessionIndexWriter.GetCsvHeader();
        CheckHeaderContains(report, header, "session_trials_has_condition", "condition_id");
        CheckHeaderContains(report, header, "session_trials_has_round", "round_id");
        CheckHeaderContains(report, header, "session_trials_has_validity", "valid_for_analysis");
        CheckHeaderContains(report, header, "session_trials_has_artifact_paths", "trial_summary_csv_path", "events_file_path", "samples_file_path", "manifest_file_path");

        Type summaryType = typeof(ExperimentTrialSummary);
        CheckField(report, summaryType, "ConditionId");
        CheckField(report, summaryType, "TrialId");
        CheckField(report, summaryType, "RoundId");
        CheckField(report, summaryType, "Success");
        CheckField(report, summaryType, "Aborted");
        CheckField(report, summaryType, "TerminalState");
        CheckField(report, summaryType, "FailureReason");

        Check(report,
            typeof(ExperimentDataConsistencyValidator).GetMethod(nameof(ExperimentDataConsistencyValidator.ValidateRunDirectory), new[] { typeof(string), typeof(string) }) != null,
            "run_data_validator_available",
            nameof(ExperimentDataConsistencyValidator.ValidateRunDirectory));
        Check(report,
            typeof(FinalSceneConsolidationAuditUtility).GetMethod(nameof(FinalSceneConsolidationAuditUtility.RunForBatch), BindingFlags.Public | BindingFlags.Static) != null,
            "final_scene_consolidation_audit_available",
            nameof(FinalSceneConsolidationAuditUtility.RunForBatch));
    }

    private static void CheckTrialSummaryPathReadiness(ReadinessReport report, Scene scene)
    {
        string projectRoot = Directory.GetCurrentDirectory();
        string sceneName = string.IsNullOrWhiteSpace(scene.name)
            ? Path.GetFileNameWithoutExtension(ScenePath)
            : scene.name;
        string representativeRunDirectory = Path.Combine(
            projectRoot,
            "logs",
            "experiments",
            $"tiago_run_20260622_184207__{sceneName}__Unknown-Unknown__orchestrated_2x2_multi_condition__attempt99");

        string representativeSessionId = "P001_20260622_184227";
        string representativeSummaryJsonl = Path.Combine(
            representativeRunDirectory,
            $"{representativeSessionId}__t001__C00__trial_summary.jsonl");
        string representativeSummaryCsv = Path.Combine(
            representativeRunDirectory,
            $"{representativeSessionId}__t001__C00__trial_summary.csv");

        Check(report,
            representativeSummaryJsonl.Length < SafeWindowsPathLengthWarningThreshold &&
            representativeSummaryCsv.Length < SafeWindowsPathLengthWarningThreshold,
            "trial_summary_short_path_estimate_under_240",
            $"jsonl={representativeSummaryJsonl.Length}, csv={representativeSummaryCsv.Length}, threshold={SafeWindowsPathLengthWarningThreshold}");
        Check(report,
            typeof(ExperimentInstrumentationController).GetMethod("WriteSummary", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "trial_summary_writer_available",
            nameof(ExperimentInstrumentationController));
    }

    private static void CheckOfflineAnalysisReadiness(ReadinessReport report)
    {
        Check(report, File.Exists(OfflineAnalysisPath), "offline_analysis_script_present", OfflineAnalysisPath);
        if (!File.Exists(OfflineAnalysisPath))
        {
            return;
        }

        string source = File.ReadAllText(OfflineAnalysisPath);
        Check(report, source.Contains("--self-test", StringComparison.Ordinal), "offline_analysis_self_test_available", "--self-test");
        Check(report,
            ExpectedConditions.All(condition => source.Contains(condition.Id, StringComparison.Ordinal)),
            "offline_analysis_knows_final_conditions",
            string.Join(" | ", ExpectedConditions.Select(condition => condition.Id)));
        Check(report, source.Contains("condition_validation_report", StringComparison.Ordinal), "offline_analysis_exports_condition_validation", "condition_validation_report");
        Check(report, source.Contains("run_integrity_report", StringComparison.Ordinal), "offline_analysis_exports_integrity_report", "run_integrity_report");
        Check(report, source.Contains("voice_metrics_by_trial", StringComparison.Ordinal), "offline_analysis_exports_voice_metrics", "voice_metrics_by_trial");
        Check(report, source.Contains("reset_metrics_by_trial", StringComparison.Ordinal), "offline_analysis_exports_stop_resume_metrics", "reset_metrics_by_trial");
    }

    private static void AddManualPilotChecks(ReadinessReport report)
    {
        report.ManualChecks.Add("Run Play Mode pilot across C00, C10 and C11, then end current trial/session and verify zero-state snapshot has no active task or active round.");
        report.ManualChecks.Add("In C10, complete at least one micro-round with robot ON and voice/TTS silent.");
        report.ManualChecks.Add("In C11, issue one Pick & Place command plus optional Stop/Resume, then verify the held-box resume path deposits and continues the round.");
        report.ManualChecks.Add("After Play Mode, run ExperimentDataConsistencyValidator and offline_experiment_analysis.py on the generated run folder.");
    }

    private static Transform FindOrCreateParticipantStartAnchor(Scene scene, Transform rigRoot)
    {
        Transform existing = AllTransforms(scene).FirstOrDefault(transform => string.Equals(transform.name, "ParticipantStartAnchor", StringComparison.Ordinal));
        if (existing != null)
        {
            return existing;
        }

        GameObject experimentRoot = scene.GetRootGameObjects().FirstOrDefault(root => string.Equals(root.name, "Experiment", StringComparison.Ordinal));
        GameObject anchorObject = new("ParticipantStartAnchor");
        if (experimentRoot != null)
        {
            anchorObject.transform.SetParent(experimentRoot.transform, worldPositionStays: true);
        }

        if (rigRoot != null)
        {
            anchorObject.transform.SetPositionAndRotation(
                rigRoot.position,
                Quaternion.Euler(0f, rigRoot.eulerAngles.y, 0f));
        }

        return anchorObject.transform;
    }

    private static void ApplyCondition(ExperimentConditionConfigBehaviour gate, ConditionExpectation expectation)
    {
        ApplyCondition(gate, expectation.RobotEnabled, expectation.VoiceEnabled, expectation.Id, expectation.AssistanceMode);
    }

    private static void ApplyCondition(ExperimentConditionConfigBehaviour gate, bool robotEnabled, bool voiceEnabled, string conditionName, RobotAssistanceMode assistanceMode)
    {
        gate.ApplyConditionFromOrchestrator(robotEnabled, voiceEnabled, conditionName, assistanceMode, "p44_pilot_readiness_audit");
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

    private static T FindSingle<T>(ReadinessReport report, Scene scene) where T : Component
    {
        T[] values = AllComponents(scene).OfType<T>().ToArray();
        Check(report, values.Length == 1, "single_" + typeof(T).Name, values.Length == 1 ? PathOf(values[0]) : values.Length.ToString(CultureInfo.InvariantCulture));
        return values.FirstOrDefault();
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

    private static bool IsActiveBehaviour(Component component)
    {
        return component != null && component.gameObject.activeInHierarchy && (component is not Behaviour behaviour || behaviour.enabled);
    }

    private static bool DepositZoneColliderShapeMatches(DepositZone first, DepositZone second)
    {
        if (first == null || second == null)
        {
            return false;
        }

        BoxCollider firstCollider = first.GetComponent<BoxCollider>();
        BoxCollider secondCollider = second.GetComponent<BoxCollider>();
        if (firstCollider == null || secondCollider == null)
        {
            return false;
        }

        return VectorApproximately(firstCollider.size, secondCollider.size) &&
            VectorApproximately(firstCollider.center, secondCollider.center) &&
            VectorApproximately(first.transform.localScale, second.transform.localScale);
    }

    private static bool VectorApproximately(Vector3 first, Vector3 second)
    {
        return Mathf.Abs(first.x - second.x) < 0.001f &&
            Mathf.Abs(first.y - second.y) < 0.001f &&
            Mathf.Abs(first.z - second.z) < 0.001f;
    }

    private static string DescribeDepositZone(DepositZone zone)
    {
        if (zone == null)
        {
            return "null";
        }

        Collider collider = zone.GetComponent<Collider>();
        string colliderSummary = collider == null
            ? "collider=null"
            : $"collider={collider.GetType().Name} enabled={collider.enabled} trigger={collider.isTrigger}";
        if (collider is BoxCollider box)
        {
            colliderSummary += $" size={box.size} center={box.center}";
        }

        return $"{PathOf(zone)} accepted={zone.acceptedType} active={IsActiveBehaviour(zone)} layer={zone.gameObject.layer} tag={zone.tag} children={zone.transform.childCount} {colliderSummary}";
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

    private static void SetObject(UnityEngine.Object target, string propertyPath, UnityEngine.Object value)
    {
        SerializedObject serialized = new(target);
        SerializedProperty property = serialized.FindProperty(propertyPath);
        if (property == null)
        {
            throw new InvalidOperationException($"{target.GetType().Name}.{propertyPath} missing.");
        }

        property.objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static SerializedProperty FindProperty(UnityEngine.Object target, string propertyPath)
    {
        if (target == null)
        {
            return null;
        }

        return new SerializedObject(target).FindProperty(propertyPath);
    }

    private static void CheckHeaderContains(ReadinessReport report, string header, string name, params string[] columns)
    {
        Check(report,
            columns.All(column => header.Contains(column, StringComparison.Ordinal)),
            name,
            string.Join(" | ", columns));
    }

    private static void CheckField(ReadinessReport report, Type type, string fieldName)
    {
        Check(report, type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance) != null, "trial_summary_field_" + fieldName, type.Name);
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

    private static float FloatProperty(SerializedObject serializedObject, string propertyName)
    {
        SerializedProperty property = serializedObject?.FindProperty(propertyName);
        return property != null && property.propertyType == SerializedPropertyType.Float ? property.floatValue : float.NaN;
    }

    private static int IntProperty(SerializedObject serializedObject, string propertyName)
    {
        SerializedProperty property = serializedObject?.FindProperty(propertyName);
        return property?.propertyType switch
        {
            SerializedPropertyType.Integer => property.intValue,
            SerializedPropertyType.Enum => property.enumValueIndex,
            _ => -1
        };
    }

    private static bool BoolProperty(SerializedObject serializedObject, string propertyName, bool defaultValue)
    {
        SerializedProperty property = serializedObject?.FindProperty(propertyName);
        return property != null && property.propertyType == SerializedPropertyType.Boolean ? property.boolValue : defaultValue;
    }

    private static string FormatFloat(float value)
    {
        return float.IsNaN(value) ? "missing" : value.ToString("0.###", CultureInfo.InvariantCulture);
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

    private static void Check(ReadinessReport report, bool passed, string name, string detail)
    {
        string line = $"{name}: {detail}";
        if (passed)
        {
            report.Passes.Add(line);
        }
        else
        {
            report.Failures.Add(line);
        }
    }

    private static void AddWarning(ReadinessReport report, string warning)
    {
        if (!string.IsNullOrWhiteSpace(warning))
        {
            report.Warnings.Add(warning);
        }
    }

    private static string WriteReport(ReadinessReport report, Scene scene)
    {
        string outputRoot = Path.Combine(Directory.GetCurrentDirectory(), OutputDirectory);
        Directory.CreateDirectory(outputRoot);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(outputRoot, $"p44_pilot_run_readiness_{timestamp}.md");

        StringBuilder builder = new();
        builder.AppendLine("# P44 Pilot Run Readiness Audit");
        builder.AppendLine();
        builder.AppendLine($"- Scene: `{scene.path}`");
        builder.AppendLine($"- Exported at: `{DateTime.Now:o}`");
        builder.AppendLine($"- Result: `{(report.Failures.Count == 0 ? "PASS" : "FAIL")}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- Passed: `{report.Passes.Count}`");
        builder.AppendLine($"- Warnings: `{report.Warnings.Count}`");
        builder.AppendLine($"- Failed: `{report.Failures.Count}`");
        builder.AppendLine();
        AppendList(builder, "Failures", report.Failures);
        AppendList(builder, "Warnings", report.Warnings);
        AppendList(builder, "Manual Microvalidation", report.ManualChecks);
        AppendList(builder, "Passes", report.Passes);
        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);

        return $"[P44PilotRunReadinessUtility] result={(report.Failures.Count == 0 ? "PASS" : "FAIL")} recommendation={report.Recommendation} passed={report.Passes.Count} failed={report.Failures.Count} report={path}";
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
