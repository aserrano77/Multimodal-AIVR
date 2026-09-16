using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ExperimentalSceneNormalizer
{
    private const string ScenePath = "Assets/Scenes/autonomous_demo_step22_multimodal_bridge.unity";
    private const string OutputDirectory = "Logs/SceneNormalization";

    private static readonly string[] RequiredRoots =
    {
        "Robot",
        "Autonomy",
        "MultimodalInput",
        "Experiment",
        "Diagnostics",
        "DebugLegacy"
    };

    private static readonly string[] LiteralReferenceTerms =
    {
        "STEP22_MultimodalBridge_Config",
        "STEP31_ExperimentCondition_Config",
        "STEP31_RobotAssistanceRoundCoordinator",
        "STEP19_TestBox_A_01",
        "RobotAutonomyRuntime",
        "VoiceRecognitionController",
        "ExperimentInstrumentationController",
        "ExperimentSessionOrchestrator",
        "tiago_dual"
    };

    private static readonly string[] CodeFindPatterns =
    {
        "GameObject.Find",
        "Transform.Find",
        ".Find(\"",
        ".name ==",
        ".name!=",
        ".name !=",
        ".name.Equals",
        "STEP"
    };

    private sealed class NormalizationPlan
    {
        public string sceneName;
        public string scenePath;
        public string exportedAtLocal;
        public readonly List<string> createRootActions = new List<string>();
        public readonly List<ObjectAction> objectActions = new List<ObjectAction>();
        public readonly List<PropertyAction> propertyActions = new List<PropertyAction>();
        public readonly List<string> skippedActions = new List<string>();
        public readonly List<string> blockedRenames = new List<string>();
        public readonly List<string> safetyNotes = new List<string>();
        public readonly List<DependencyHit> dependencyHits = new List<DependencyHit>();
        public readonly List<FindPatternHit> findPatternHits = new List<FindPatternHit>();
        public readonly List<string> preflightRedFlags = new List<string>();
    }

    private sealed class ObjectAction
    {
        public GameObject target;
        public string originalPath;
        public string newParentPath;
        public string requestedNewName;
        public bool renameBlocked;
        public string reason;

        public string EffectiveName => renameBlocked || string.IsNullOrWhiteSpace(requestedNewName) ? target.name : requestedNewName;
    }

    private sealed class PropertyAction
    {
        public UnityEngine.Object target;
        public string targetPath;
        public string propertyPath;
        public string newValueSummary;
        public Action apply;
    }

    private sealed class DependencyHit
    {
        public string term;
        public string location;
        public string detail;
        public bool dangerousForRename;
    }

    private sealed class FindPatternHit
    {
        public string pattern;
        public string file;
        public int line;
        public string text;
    }

    [MenuItem("Tools/Multimodal AI-VR/Scene Normalizer/Dry Run")]
    public static void DryRun()
    {
        NormalizationPlan plan = BuildPlan();
        string path = WriteMarkdown(plan, "dry_run");
        Debug.Log(BuildConsoleSummary(plan, false) + "\nDry Run report: " + path);
    }

    [MenuItem("Tools/Multimodal AI-VR/Scene Normalizer/Apply")]
    public static void Apply()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || scene.path != ScenePath)
        {
            Debug.LogError($"Scene Normalizer Apply aborted. Open the expected scene first: {ScenePath}");
            return;
        }

        NormalizationPlan plan = BuildPlan();
        if (plan.blockedRenames.Count > 0)
        {
            Debug.LogWarning("Scene Normalizer Apply will skip blocked renames. See Dry Run report for dependency details.");
        }

        string backupPath = BuildBackupPath(scene.path);
        if (!AssetDatabase.CopyAsset(scene.path, backupPath))
        {
            Debug.LogError($"Scene Normalizer Apply aborted. Could not create backup scene: {backupPath}");
            return;
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Normalize Experimental Scene");

        Dictionary<string, GameObject> roots = EnsureRoots(plan);
        foreach (ObjectAction action in plan.objectActions)
        {
            ApplyObjectAction(action, roots);
        }

        foreach (PropertyAction action in plan.propertyActions)
        {
            action.apply();
        }

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.Refresh();

        string reportPath = WriteMarkdown(plan, "apply");
        Debug.Log(BuildConsoleSummary(plan, true) + $"\nBackup scene: {backupPath}\nApply report: {reportPath}");
    }

    public static void ApplyTargetSceneForBatch()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Apply();
    }

    [MenuItem("Tools/Multimodal AI-VR/Scene Normalizer/Export Post-Normalization Audit")]
    public static void ExportPostNormalizationAudit()
    {
        SceneAuditExporter.ExportSceneAudit();
    }

    private static NormalizationPlan BuildPlan()
    {
        Scene scene = SceneManager.GetActiveScene();
        NormalizationPlan plan = new NormalizationPlan
        {
            sceneName = scene.IsValid() ? scene.name : string.Empty,
            scenePath = scene.IsValid() ? scene.path : string.Empty,
            exportedAtLocal = DateTime.Now.ToString("o", CultureInfo.InvariantCulture)
        };

        if (!scene.IsValid())
        {
            plan.safetyNotes.Add("No valid active scene.");
            return plan;
        }

        if (scene.path != ScenePath)
        {
            plan.safetyNotes.Add($"Active scene differs from expected scene. Active: {scene.path}. Expected: {ScenePath}.");
        }

        Dictionary<string, GameObject> byPath = BuildPathIndex(scene);
        Dictionary<string, List<GameObject>> byName = BuildNameIndex(scene);
        AddDependencyFindings(plan, scene);
        AddCodeFindPatternFindings(plan);
        AddRootActions(plan, byName);
        AddObjectActions(plan, byPath, byName);
        AddPropertyActions(plan, byPath, byName);
        AddValidationFindings(plan, scene);
        return plan;
    }

    private static void AddRootActions(NormalizationPlan plan, Dictionary<string, List<GameObject>> byName)
    {
        foreach (string root in RequiredRoots)
        {
            if (!byName.TryGetValue(root, out List<GameObject> objects) || objects.All(go => go.transform.parent != null))
            {
                plan.createRootActions.Add($"Create root `{root}`.");
            }
        }
    }

    private static void AddObjectActions(
        NormalizationPlan plan,
        Dictionary<string, GameObject> byPath,
        Dictionary<string, List<GameObject>> byName)
    {
        AddMoveRename(plan, byName, "tiago_dual", "Robot", "TIAGoDual", true);
        AddMoveRename(plan, byName, "RobotAutonomyRuntime", "Autonomy", null, false);
        AddMoveRename(plan, byName, "STEP22_MultimodalBridge_Config", "Autonomy", "MultimodalAutonomyBridge", true);
        AddMoveRename(plan, byName, "STEP31_RobotAssistanceRoundCoordinator", "Autonomy", "RobotAssistanceRoundCoordinator", true);
        AddMoveRename(plan, byName, "WhisperManager", "MultimodalInput", null, false);
        AddMoveRename(plan, byName, "VoiceRecognitionController", "MultimodalInput", null, false);
        AddMoveRename(plan, byName, "ExperimentInstrumentationController", "Experiment", "ExperimentInstrumentation", true);
        AddMoveRename(plan, byName, "ExperimentSessionOrchestrator", "Experiment", null, false);
        AddMoveRename(plan, byName, "STEP31_ExperimentCondition_Config", "Experiment", "ExperimentConditionGate", true);

        AddMoveIfSafe(plan, byName, "NavMesh Surface", "Diagnostics");
        AddMoveIfSafe(plan, byName, "TargetLine", "Diagnostics");
        AddMoveIfSafe(plan, byName, "TargetVisual", "Diagnostics");
        AddRootInactiveBoxEdgeFrameActions(plan, byName);

        AddMoveIfRootInactive(plan, byName, "DemoManager", "DebugLegacy");
        AddMoveIfRootInactive(plan, byName, "TiagoDemoCanvasVR", "DebugLegacy");
        AddMoveIfRootInactive(plan, byName, "STEP19_TestPallet_A_01", "DebugLegacy");
        AddMoveIfRootInactive(plan, byName, "STEP19_TestBox_A_01", "DebugLegacy");
        AddMoveIfRootInactive(plan, byName, "Step19_TestPoints", "DebugLegacy");
        AddMoveIfRootInactive(plan, byName, "STEP21_PlaceTargetSeeder_Config", "DebugLegacy");

        foreach (string path in new[] { "Gameplay/SpawnZone", "Gameplay/SpawnManager", "Gameplay/RoundManager", "Gameplay/ActiveRound", "Environment/Layout/DepositZones" })
        {
            if (!byPath.ContainsKey(path))
            {
                plan.safetyNotes.Add($"Expected retained object not found at `{path}`.");
            }
        }
    }

    private static void AddMoveRename(
        NormalizationPlan plan,
        Dictionary<string, List<GameObject>> byName,
        string sourceName,
        string parentName,
        string requestedNewName,
        bool renameRequiresDependencyClearance)
    {
        GameObject target = UniqueByName(plan, byName, sourceName);
        if (target == null)
        {
            return;
        }

        bool renameBlocked = false;
        if (renameRequiresDependencyClearance && !string.IsNullOrEmpty(requestedNewName))
        {
            renameBlocked = HasDangerousDependency(plan, sourceName);
            if (renameBlocked)
            {
                string message = $"Rename `{sourceName}` -> `{requestedNewName}` blocked by literal/name dependency.";
                plan.blockedRenames.Add(message);
                plan.safetyNotes.Add(message);
            }
        }

        plan.objectActions.Add(new ObjectAction
        {
            target = target,
            originalPath = GetPath(target.transform),
            newParentPath = parentName,
            requestedNewName = requestedNewName,
            renameBlocked = renameBlocked,
            reason = "Controlled experimental hierarchy normalization."
        });
    }

    private static void AddMoveIfSafe(NormalizationPlan plan, Dictionary<string, List<GameObject>> byName, string sourceName, string parentName)
    {
        GameObject target = UniqueByName(plan, byName, sourceName);
        if (target == null)
        {
            return;
        }

        if (IsInsideProtectedHierarchy(target))
        {
            plan.skippedActions.Add($"Skipped `{GetPath(target.transform)}`: protected XR/Environment/URDF hierarchy.");
            return;
        }

        plan.objectActions.Add(new ObjectAction
        {
            target = target,
            originalPath = GetPath(target.transform),
            newParentPath = parentName,
            reason = "Diagnostic/debug object candidate."
        });
    }

    private static void AddMoveIfRootInactive(NormalizationPlan plan, Dictionary<string, List<GameObject>> byName, string sourceName, string parentName)
    {
        GameObject target = UniqueByName(plan, byName, sourceName);
        if (target == null)
        {
            return;
        }

        if (target.transform.parent != null || target.activeInHierarchy)
        {
            plan.skippedActions.Add($"Skipped `{GetPath(target.transform)}`: requested DebugLegacy move is limited to inactive root objects.");
            return;
        }

        plan.objectActions.Add(new ObjectAction
        {
            target = target,
            originalPath = GetPath(target.transform),
            newParentPath = parentName,
            reason = "Inactive root legacy/debug object."
        });
    }

    private static void AddPropertyActions(
        NormalizationPlan plan,
        Dictionary<string, GameObject> byPath,
        Dictionary<string, List<GameObject>> byName)
    {
        GameObject orchestrator = FirstByName(byName, "ExperimentSessionOrchestrator");
        GameObject conditionGate = FirstByNames(byName, "ExperimentConditionGate", "STEP31_ExperimentCondition_Config");
        GameObject instrumentation = FirstByNames(byName, "ExperimentInstrumentation", "ExperimentInstrumentationController");
        GameObject spawnManager = FirstByPathOrName(byPath, byName, "Gameplay/SpawnManager", "SpawnManager");
        GameObject roundManager = FirstByPathOrName(byPath, byName, "Gameplay/RoundManager", "RoundManager");
        GameObject voice = FirstByName(byName, "VoiceRecognitionController");
        GameObject autonomyRuntime = FirstByName(byName, "RobotAutonomyRuntime");
        GameObject bridge = FirstByNames(byName, "MultimodalAutonomyBridge", "STEP22_MultimodalBridge_Config");
        GameObject coordinator = FirstByNames(byName, "RobotAssistanceRoundCoordinator", "STEP31_RobotAssistanceRoundCoordinator");

        if (orchestrator != null)
        {
            Component component = FindComponent(orchestrator, "ExperimentSessionOrchestrator");
            AddObjectReferenceAction(plan, component, "_conditionConfig", FindComponent(conditionGate, "ExperimentConditionConfigBehaviour"));
            AddObjectReferenceAction(plan, component, "_instrumentation", FindComponent(instrumentation, "ExperimentInstrumentationController"));
            AddObjectReferenceAction(plan, component, "_spawnManager", FindComponent(spawnManager, "SpawnManager"));
            AddObjectReferenceAction(plan, component, "_roundManager", FindComponent(roundManager, "RoundManager"));
            AddObjectReferenceAction(plan, component, "_voiceRecognitionController", FindComponent(voice, "VoiceRecognitionController"));
            AddObjectReferenceAction(plan, component, "_voiceConnector", FindComponent(voice, "VoiceAutonomyCommandConnector"));
            AddObjectReferenceAction(plan, component, "_robotAdapter", FindComponent(autonomyRuntime, "AutonomousRobotAdapter"));
        }

        Component voiceConnector = FindComponent(voice, "VoiceAutonomyCommandConnector");
        AddObjectReferenceAction(plan, voiceConnector, "_experimentConditionProviderComponent", FindComponent(conditionGate, "ExperimentConditionConfigBehaviour"));

        Component coordinatorComponent = FindComponent(coordinator, "RobotAssistanceRoundCoordinator");
        AddObjectReferenceAction(plan, coordinatorComponent, "_experimentConditionProviderComponent", FindComponent(conditionGate, "ExperimentConditionConfigBehaviour"));
        AddObjectReferenceAction(plan, coordinatorComponent, "_robotAdapter", FindComponent(autonomyRuntime, "AutonomousRobotAdapter"));
        AddObjectReferenceAction(plan, coordinatorComponent, "_placeTargetRegistry", FindComponent(bridge, "MultimodalPlaceTargetRegistry"));

        Component bridgeComponent = FindComponent(bridge, "MultimodalAutonomyCommandBridge");
        AddObjectReferenceAction(plan, bridgeComponent, "_robotAdapter", FindComponent(autonomyRuntime, "AutonomousRobotAdapter"));
        AddObjectReferenceAction(plan, bridgeComponent, "_roundLifecycleComponent", FindComponent(roundManager, "RoundManager"));
        AddObjectReferenceAction(plan, bridgeComponent, "_placeTargetRegistry", FindComponent(bridge, "MultimodalPlaceTargetRegistry"));
        AddBoolAction(plan, bridgeComponent, "_preferConfiguredTargetForCategory", false);
        AddBoolAction(plan, bridgeComponent, "_submitSimulatedIntentOnStart", false);
        AddClearStringIfContainsAction(plan, bridgeComponent, "_preferredTargetId", "STEP19_TestBox_A_01");
        AddClearStringIfContainsAction(plan, bridgeComponent, "_simulatedTargetId", "STEP19_TestBox_A_01");

        Component instrumentationComponent = FindComponent(instrumentation, "ExperimentInstrumentationController");
        AddBoolAction(plan, instrumentationComponent, "_configureSessionOnStart", false);
        AddBoolAction(plan, instrumentationComponent, "_startTrialOnStart", false);
        AddStringAction(plan, instrumentationComponent, "_conditionId", "uninitialized");
        AddStringAction(plan, instrumentationComponent, "_conditionName", "uninitialized");
        AddBoolAction(plan, instrumentationComponent, "_robotEnabled", false);
        AddBoolAction(plan, instrumentationComponent, "_voiceEnabled", false);
        AddStringAction(plan, instrumentationComponent, "_assistanceMode", "Disabled");
        AddStringAction(plan, instrumentationComponent, "_roundId", string.Empty);
        AddEnumAction(plan, instrumentationComponent, "_runMode", "Orchestrated2x2");

        Component conditionComponent = FindComponent(conditionGate, "ExperimentConditionConfigBehaviour");
        AddBoolAction(plan, conditionComponent, "_robotEnabled", false);
        AddBoolAction(plan, conditionComponent, "_voiceEnabled", false);
        AddStringAction(plan, conditionComponent, "_conditionName", "uninitialized");
        AddEnumAction(plan, conditionComponent, "_assistanceMode", "Disabled");
        AddBoolAction(plan, conditionComponent, "_logConditionOnStart", false);
        AddEnumAction(plan, conditionComponent, "_runMode", "Orchestrated2x2");

        AddDisabledLegacySeederNeutralizationActions(plan, autonomyRuntime);
    }

    private static void AddRootInactiveBoxEdgeFrameActions(NormalizationPlan plan, Dictionary<string, List<GameObject>> byName)
    {
        if (!byName.TryGetValue("BoxEdgeFrame", out List<GameObject> objects) || objects.Count == 0)
        {
            plan.skippedActions.Add("Object `BoxEdgeFrame` not found.");
            return;
        }

        int index = 1;
        HashSet<string> usedNames = new HashSet<string>(byName.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (GameObject target in objects.Where(go =>
                     go != null &&
                     go.transform.parent == null &&
                     !go.activeInHierarchy &&
                     !IsUnderRoot(go.transform, "DebugLegacy")))
        {
            string newName = NextLegacyBoxEdgeFrameName(ref index, usedNames);
            plan.objectActions.Add(new ObjectAction
            {
                target = target,
                originalPath = GetPath(target.transform),
                newParentPath = "DebugLegacy",
                requestedNewName = newName,
                reason = "Inactive root BoxEdgeFrame legacy/debug object."
            });
        }
    }

    private static void AddDisabledLegacySeederNeutralizationActions(NormalizationPlan plan, GameObject autonomyRuntime)
    {
        if (autonomyRuntime == null)
        {
            plan.skippedActions.Add("Skipped legacy seeder neutralization: RobotAutonomyRuntime not found.");
            return;
        }

        foreach (Component component in autonomyRuntime.GetComponentsInChildren<Component>(true))
        {
            if (component == null)
            {
                continue;
            }

            Behaviour behaviour = component as Behaviour;
            if (behaviour == null || behaviour.enabled)
            {
                continue;
            }

            string typeName = component.GetType().Name;
            if (typeName == "ManualTargetSeeder")
            {
                AddObjectReferenceClearIfLegacyAction(plan, component, "_targetTransform", "STEP19_TestBox_A_01");
                AddObjectReferenceClearIfLegacyAction(plan, component, "_approachTransform", "STEP19_TestBox_A_01");
            }
            else if (typeName == "PerceptionTargetSeeder")
            {
                AddClearStringIfContainsAction(plan, component, "_objectId", "STEP19_TestBox_A_01");
                AddBoolAction(plan, component, "_seedOnStart", false);
            }
        }
    }

    private static void AddObjectReferenceAction(NormalizationPlan plan, UnityEngine.Object target, string propertyPath, UnityEngine.Object reference)
    {
        if (target == null || reference == null)
        {
            plan.skippedActions.Add($"Skipped reference `{propertyPath}`: target or reference missing.");
            return;
        }

        SerializedObject serializedObject = new SerializedObject(target);
        SerializedProperty property = serializedObject.FindProperty(propertyPath);
        if (property == null || property.propertyType != SerializedPropertyType.ObjectReference)
        {
            plan.skippedActions.Add($"Skipped reference `{target.name}.{propertyPath}`: property missing or not an object reference.");
            return;
        }

        if (property.objectReferenceValue == reference)
        {
            return;
        }

        plan.propertyActions.Add(new PropertyAction
        {
            target = target,
            targetPath = GetObjectPath(target),
            propertyPath = propertyPath,
            newValueSummary = GetObjectPath(reference),
            apply = () =>
            {
                Undo.RecordObject(target, "Normalize scene references");
                SerializedObject so = new SerializedObject(target);
                SerializedProperty prop = so.FindProperty(propertyPath);
                prop.objectReferenceValue = reference;
                so.ApplyModifiedProperties();
            }
        });
    }

    private static void AddObjectReferenceClearIfLegacyAction(NormalizationPlan plan, UnityEngine.Object target, string propertyPath, string legacyName)
    {
        if (target == null)
        {
            plan.skippedActions.Add($"Skipped reference clear `{propertyPath}`: target missing.");
            return;
        }

        SerializedObject serializedObject = new SerializedObject(target);
        SerializedProperty property = serializedObject.FindProperty(propertyPath);
        if (property == null || property.propertyType != SerializedPropertyType.ObjectReference)
        {
            plan.skippedActions.Add($"Skipped reference clear `{target.name}.{propertyPath}`: property missing or not an object reference.");
            return;
        }

        UnityEngine.Object current = property.objectReferenceValue;
        if (current == null || GetObjectPath(current).IndexOf(legacyName, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        plan.propertyActions.Add(new PropertyAction
        {
            target = target,
            targetPath = GetObjectPath(target),
            propertyPath = propertyPath,
            newValueSummary = "null",
            apply = () =>
            {
                Undo.RecordObject(target, "Clear disabled legacy seeder reference");
                SerializedObject so = new SerializedObject(target);
                SerializedProperty prop = so.FindProperty(propertyPath);
                prop.objectReferenceValue = null;
                so.ApplyModifiedProperties();
            }
        });
    }

    private static void AddBoolAction(NormalizationPlan plan, UnityEngine.Object target, string propertyPath, bool value)
    {
        AddScalarAction(plan, target, propertyPath, value.ToString(), prop => prop.boolValue = value, SerializedPropertyType.Boolean);
    }

    private static void AddStringAction(NormalizationPlan plan, UnityEngine.Object target, string propertyPath, string value)
    {
        AddScalarAction(plan, target, propertyPath, value, prop => prop.stringValue = value, SerializedPropertyType.String);
    }

    private static void AddEnumAction(NormalizationPlan plan, UnityEngine.Object target, string propertyPath, string enumName)
    {
        if (target == null)
        {
            plan.skippedActions.Add($"Skipped enum `{propertyPath}`: target missing.");
            return;
        }

        SerializedObject serializedObject = new SerializedObject(target);
        SerializedProperty property = serializedObject.FindProperty(propertyPath);
        if (property == null || property.propertyType != SerializedPropertyType.Enum)
        {
            plan.skippedActions.Add($"Skipped enum `{target.name}.{propertyPath}`: property missing or not enum.");
            return;
        }

        int index = Array.IndexOf(property.enumNames, enumName);
        if (index < 0)
        {
            index = Array.IndexOf(property.enumDisplayNames, enumName);
        }

        if (index < 0)
        {
            plan.skippedActions.Add($"Skipped enum `{target.name}.{propertyPath}`: enum value `{enumName}` not found.");
            return;
        }

        if (property.enumValueIndex == index)
        {
            return;
        }

        plan.propertyActions.Add(new PropertyAction
        {
            target = target,
            targetPath = GetObjectPath(target),
            propertyPath = propertyPath,
            newValueSummary = enumName,
            apply = () =>
            {
                Undo.RecordObject(target, "Normalize scene enum");
                SerializedObject so = new SerializedObject(target);
                SerializedProperty prop = so.FindProperty(propertyPath);
                prop.enumValueIndex = index;
                so.ApplyModifiedProperties();
            }
        });
    }

    private static void AddClearStringIfContainsAction(NormalizationPlan plan, UnityEngine.Object target, string propertyPath, string valueToClear)
    {
        if (target == null)
        {
            plan.skippedActions.Add($"Skipped string clear `{propertyPath}`: target missing.");
            return;
        }

        SerializedObject serializedObject = new SerializedObject(target);
        SerializedProperty property = serializedObject.FindProperty(propertyPath);
        if (property == null || property.propertyType != SerializedPropertyType.String)
        {
            plan.skippedActions.Add($"Skipped string clear `{target.name}.{propertyPath}`: property missing or not string.");
            return;
        }

        if (string.IsNullOrEmpty(property.stringValue) || property.stringValue.IndexOf(valueToClear, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        AddStringAction(plan, target, propertyPath, string.Empty);
    }

    private static void AddScalarAction(
        NormalizationPlan plan,
        UnityEngine.Object target,
        string propertyPath,
        string summary,
        Action<SerializedProperty> setter,
        SerializedPropertyType requiredType)
    {
        if (target == null)
        {
            plan.skippedActions.Add($"Skipped property `{propertyPath}`: target missing.");
            return;
        }

        SerializedObject serializedObject = new SerializedObject(target);
        SerializedProperty property = serializedObject.FindProperty(propertyPath);
        if (property == null || property.propertyType != requiredType)
        {
            plan.skippedActions.Add($"Skipped property `{target.name}.{propertyPath}`: property missing or wrong type.");
            return;
        }

        string current = PropertyValueSummary(property);
        if (current == summary)
        {
            return;
        }

        plan.propertyActions.Add(new PropertyAction
        {
            target = target,
            targetPath = GetObjectPath(target),
            propertyPath = propertyPath,
            newValueSummary = summary,
            apply = () =>
            {
                Undo.RecordObject(target, "Normalize scene property");
                SerializedObject so = new SerializedObject(target);
                SerializedProperty prop = so.FindProperty(propertyPath);
                setter(prop);
                so.ApplyModifiedProperties();
            }
        });
    }

    private static void AddDependencyFindings(NormalizationPlan plan, Scene scene)
    {
        foreach (string term in LiteralReferenceTerms)
        {
            AddCodeLiteralHits(plan, term);
            AddSceneSerializedStringHits(plan, scene, term);
        }
    }

    private static void AddCodeLiteralHits(NormalizationPlan plan, string term)
    {
        foreach (string path in Directory.GetFiles(Path.Combine(Directory.GetCurrentDirectory(), "Assets"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = ToProjectPath(path);
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bool ownTool = relative.EndsWith("ExperimentalSceneNormalizer.cs", StringComparison.OrdinalIgnoreCase) ||
                               relative.EndsWith("SceneAuditExporter.cs", StringComparison.OrdinalIgnoreCase);
                plan.dependencyHits.Add(new DependencyHit
                {
                    term = term,
                    location = relative + ":" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    detail = lines[i].Trim(),
                    dangerousForRename = !ownTool
                });
            }
        }
    }

    private static void AddSceneSerializedStringHits(NormalizationPlan plan, Scene scene, string term)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    continue;
                }

                SerializedObject serializedObject;
                try
                {
                    serializedObject = new SerializedObject(component);
                }
                catch
                {
                    continue;
                }

                SerializedProperty iterator = serializedObject.GetIterator();
                bool enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (iterator.propertyType == SerializedPropertyType.String &&
                        !string.IsNullOrEmpty(iterator.stringValue) &&
                        iterator.stringValue.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        plan.dependencyHits.Add(new DependencyHit
                        {
                            term = term,
                            location = GetPath(component.transform) + "/" + component.GetType().Name + "." + iterator.propertyPath,
                            detail = iterator.stringValue,
                            dangerousForRename = true
                        });
                    }
                }
            }
        }
    }

    private static void AddCodeFindPatternFindings(NormalizationPlan plan)
    {
        foreach (string path in Directory.GetFiles(Path.Combine(Directory.GetCurrentDirectory(), "Assets"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = ToProjectPath(path);
            if (relative.EndsWith("ExperimentalSceneNormalizer.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (string pattern in CodeFindPatterns)
                {
                    if (lines[i].IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        plan.findPatternHits.Add(new FindPatternHit
                        {
                            pattern = pattern,
                            file = relative,
                            line = i + 1,
                            text = lines[i].Trim()
                        });
                    }
                }
            }
        }
    }

    private static void AddValidationFindings(NormalizationPlan plan, Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform.gameObject.activeInHierarchy && IsStepName(transform.name) && !IsUnderRoot(transform, "DebugLegacy"))
                {
                    plan.preflightRedFlags.Add($"Active STEPXX object outside DebugLegacy: {GetPath(transform)}");
                }
            }
        }
    }

    private static Dictionary<string, GameObject> EnsureRoots(NormalizationPlan plan)
    {
        Dictionary<string, GameObject> roots = BuildNameIndex(SceneManager.GetActiveScene())
            .Where(pair => RequiredRoots.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .Where(go => go.transform.parent == null)
            .GroupBy(go => go.name)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (string rootName in RequiredRoots)
        {
            if (roots.ContainsKey(rootName))
            {
                continue;
            }

            GameObject root = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(root, "Create scene normalization root");
            roots[rootName] = root;
        }

        return roots;
    }

    private static void ApplyObjectAction(ObjectAction action, Dictionary<string, GameObject> roots)
    {
        if (action.target == null || !roots.TryGetValue(action.newParentPath, out GameObject parent))
        {
            return;
        }

        if (IsInsideProtectedHierarchy(action.target))
        {
            return;
        }

        Undo.SetTransformParent(action.target.transform, parent.transform, "Normalize scene hierarchy");
        if (!action.renameBlocked && !string.IsNullOrWhiteSpace(action.requestedNewName) && action.target.name != action.requestedNewName)
        {
            Undo.RecordObject(action.target, "Normalize scene object name");
            action.target.name = action.requestedNewName;
        }
    }

    private static GameObject UniqueByName(NormalizationPlan plan, Dictionary<string, List<GameObject>> byName, string name)
    {
        if (!byName.TryGetValue(name, out List<GameObject> objects) || objects.Count == 0)
        {
            plan.skippedActions.Add($"Object `{name}` not found.");
            return null;
        }

        if (objects.Count > 1)
        {
            plan.skippedActions.Add($"Object `{name}` is not unique ({objects.Count} matches); skipped for safety.");
            return null;
        }

        return objects[0];
    }

    private static GameObject FirstByName(Dictionary<string, List<GameObject>> byName, string name)
    {
        return byName.TryGetValue(name, out List<GameObject> objects) && objects.Count > 0 ? objects[0] : null;
    }

    private static GameObject FirstByNames(Dictionary<string, List<GameObject>> byName, params string[] names)
    {
        foreach (string name in names)
        {
            GameObject found = FirstByName(byName, name);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static GameObject FirstByPathOrName(Dictionary<string, GameObject> byPath, Dictionary<string, List<GameObject>> byName, string path, string name)
    {
        return byPath.TryGetValue(path, out GameObject found) ? found : FirstByName(byName, name);
    }

    private static Component FindComponent(GameObject gameObject, string typeName)
    {
        if (gameObject == null)
        {
            return null;
        }

        return gameObject.GetComponents<Component>()
            .FirstOrDefault(component => component != null && component.GetType().Name == typeName);
    }

    private static bool HasDangerousDependency(NormalizationPlan plan, string term)
    {
        return plan.dependencyHits.Any(hit => hit.dangerousForRename && string.Equals(hit.term, term, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, GameObject> BuildPathIndex(Scene scene)
    {
        Dictionary<string, GameObject> result = new Dictionary<string, GameObject>();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                result[GetPath(transform)] = transform.gameObject;
            }
        }

        return result;
    }

    private static Dictionary<string, List<GameObject>> BuildNameIndex(Scene scene)
    {
        Dictionary<string, List<GameObject>> result = new Dictionary<string, List<GameObject>>(StringComparer.OrdinalIgnoreCase);
        if (!scene.IsValid())
        {
            return result;
        }

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (!result.TryGetValue(transform.name, out List<GameObject> list))
                {
                    list = new List<GameObject>();
                    result[transform.name] = list;
                }

                list.Add(transform.gameObject);
            }
        }

        return result;
    }

    private static string WriteMarkdown(NormalizationPlan plan, string suffix)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string root = Path.Combine(Directory.GetCurrentDirectory(), OutputDirectory);
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, $"scene_normalizer_{suffix}_{SanitizeFileName(plan.sceneName)}_{timestamp}.md");
        File.WriteAllText(path, BuildMarkdown(plan, suffix), Encoding.UTF8);
        return path;
    }

    private static string BuildMarkdown(NormalizationPlan plan, string mode)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("# Experimental Scene Normalizer");
        builder.AppendLine();
        builder.AppendLine($"- Mode: `{mode}`");
        builder.AppendLine($"- Scene: `{plan.sceneName}`");
        builder.AppendLine($"- Scene path: `{plan.scenePath}`");
        builder.AppendLine($"- Exported at: `{plan.exportedAtLocal}`");
        builder.AppendLine();

        AppendList(builder, "Safety Notes", plan.safetyNotes);
        AppendList(builder, "Preflight Red Flags", plan.preflightRedFlags);
        AppendList(builder, "Roots To Create", plan.createRootActions);

        builder.AppendLine("## Object Moves And Renames");
        builder.AppendLine();
        foreach (ObjectAction action in plan.objectActions)
        {
            string rename = string.IsNullOrWhiteSpace(action.requestedNewName)
                ? "no rename"
                : action.renameBlocked
                    ? $"rename blocked, keep `{action.target.name}`"
                    : $"rename to `{action.requestedNewName}`";
            builder.AppendLine($"- `{action.originalPath}` -> `{action.newParentPath}/{action.EffectiveName}` ({rename}). Reason: {action.reason}");
        }

        builder.AppendLine();
        AppendList(builder, "Blocked Renames", plan.blockedRenames);

        builder.AppendLine("## Serialized Property Changes");
        builder.AppendLine();
        foreach (PropertyAction action in plan.propertyActions)
        {
            builder.AppendLine($"- `{action.targetPath}.{action.propertyPath}` -> `{action.newValueSummary}`");
        }

        builder.AppendLine();
        AppendList(builder, "Skipped Actions", plan.skippedActions);

        builder.AppendLine("## Literal Dependency Hits");
        builder.AppendLine();
        foreach (DependencyHit hit in plan.dependencyHits)
        {
            builder.AppendLine($"- `{hit.term}` at `{hit.location}` dangerousForRename=`{hit.dangerousForRename}`: `{hit.detail}`");
        }

        builder.AppendLine();

        builder.AppendLine("## Find/Name Pattern Hits");
        builder.AppendLine();
        foreach (FindPatternHit hit in plan.findPatternHits.Take(500))
        {
            builder.AppendLine($"- `{hit.pattern}` at `{hit.file}:{hit.line}`: `{hit.text}`");
        }

        if (plan.findPatternHits.Count > 500)
        {
            builder.AppendLine($"- <{plan.findPatternHits.Count - 500} additional pattern hits omitted>");
        }

        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Root creations: {plan.createRootActions.Count}");
        builder.AppendLine($"- Object actions: {plan.objectActions.Count}");
        builder.AppendLine($"- Property actions: {plan.propertyActions.Count}");
        builder.AppendLine($"- Blocked renames: {plan.blockedRenames.Count}");
        builder.AppendLine($"- Dependency hits: {plan.dependencyHits.Count}");
        builder.AppendLine($"- Preflight red flags: {plan.preflightRedFlags.Count}");
        return builder.ToString();
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

    private static string BuildConsoleSummary(NormalizationPlan plan, bool applied)
    {
        return (applied ? "Scene Normalizer Apply completed." : "Scene Normalizer Dry Run completed.") +
               $"\nRoots to create: {plan.createRootActions.Count}" +
               $"\nObject actions: {plan.objectActions.Count}" +
               $"\nProperty actions: {plan.propertyActions.Count}" +
               $"\nBlocked renames: {plan.blockedRenames.Count}" +
               $"\nPreflight red flags: {plan.preflightRedFlags.Count}";
    }

    private static string BuildBackupPath(string scenePath)
    {
        string directory = Path.GetDirectoryName(scenePath)?.Replace('\\', '/') ?? "Assets/Scenes";
        string fileName = Path.GetFileNameWithoutExtension(scenePath);
        string extension = Path.GetExtension(scenePath);
        string candidate = $"{directory}/{fileName}_before_scene_normalization{extension}";
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return $"{directory}/{fileName}_before_scene_normalization_{timestamp}{extension}";
    }

    private static string GetObjectPath(UnityEngine.Object value)
    {
        if (value == null)
        {
            return "null";
        }

        GameObject gameObject = value as GameObject;
        if (gameObject != null)
        {
            return GetPath(gameObject.transform);
        }

        Component component = value as Component;
        if (component != null)
        {
            return GetPath(component.transform) + "/" + component.GetType().Name;
        }

        string assetPath = AssetDatabase.GetAssetPath(value);
        return string.IsNullOrEmpty(assetPath) ? value.name : assetPath;
    }

    private static string PropertyValueSummary(SerializedProperty property)
    {
        switch (property.propertyType)
        {
            case SerializedPropertyType.Boolean:
                return property.boolValue.ToString();
            case SerializedPropertyType.String:
                return property.stringValue ?? string.Empty;
            case SerializedPropertyType.Enum:
                return property.enumDisplayNames != null && property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length
                    ? property.enumDisplayNames[property.enumValueIndex]
                    : property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
            default:
                return "<unsupported>";
        }
    }

    private static bool IsInsideProtectedHierarchy(GameObject gameObject)
    {
        Transform root = gameObject.transform.root;
        string rootName = root != null ? root.name : string.Empty;
        return string.Equals(rootName, "XR", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rootName, "Environment", StringComparison.OrdinalIgnoreCase) ||
               rootName.IndexOf("tiago", StringComparison.OrdinalIgnoreCase) >= 0 && gameObject.transform != root;
    }

    private static bool IsUnderRoot(Transform transform, string rootName)
    {
        return transform.root != null && string.Equals(transform.root.name, rootName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NextLegacyBoxEdgeFrameName(ref int index, HashSet<string> usedNames)
    {
        string candidate;
        do
        {
            candidate = "LegacyBoxEdgeFrame_" + index.ToString("00", CultureInfo.InvariantCulture);
            index++;
        }
        while (!usedNames.Add(candidate));

        return candidate;
    }

    private static bool IsStepName(string name)
    {
        return !string.IsNullOrEmpty(name) && name.StartsWith("STEP", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPath(Transform transform)
    {
        Stack<string> parts = new Stack<string>();
        while (transform != null)
        {
            parts.Push(transform.name);
            transform = transform.parent;
        }

        return string.Join("/", parts.ToArray());
    }

    private static string ToProjectPath(string absolutePath)
    {
        string projectRoot = Directory.GetCurrentDirectory().Replace('\\', '/');
        string normalized = absolutePath.Replace('\\', '/');
        return normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
            ? normalized.Substring(projectRoot.Length + 1)
            : normalized;
    }

    private static string SanitizeFileName(string value)
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
