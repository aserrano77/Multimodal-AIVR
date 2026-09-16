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

public static class SceneAuditExporter
{
    private const int MaxArrayItems = 20;
    private const int MaxStringLength = 500;
    private const string OutputDirectory = "Logs/SceneAudits";
    private const string TargetScenePath = "Assets/Scenes/autonomous_demo_step22_multimodal_bridge.unity";

    private static readonly string[] ExperimentalComponentNames =
    {
        "ExperimentSessionOrchestrator",
        "ExperimentInstrumentationController",
        "ExperimentConditionConfigBehaviour",
        "RobotAssistanceRoundCoordinator",
        "VoiceRecognitionController",
        "VoiceAutonomyCommandConnector",
        "MultimodalAutonomyCommandBridge",
        "SpawnManager",
        "RoundManager",
        "AutonomousRobotAdapter",
        "TiagoUnityManipulationService",
        "DepositZoneSlotAllocator"
    };

    private static readonly string[] AutoStartPropertyNames =
    {
        "_configureSessionOnStart",
        "configureSessionOnStart",
        "_startTrialOnStart",
        "startTrialOnStart",
        "_logConditionOnStart",
        "logConditionOnStart",
        "_autoInitializeRound",
        "autoInitializeRound",
        "spawnOnStart",
        "_submitSimulatedIntentOnStart",
        "submitSimulatedIntentOnStart"
    };

    private sealed class AuditReport
    {
        public string sceneName;
        public string scenePath;
        public string exportedAtLocal;
        public string unityVersion;
        public int maxArrayItems;
        public List<GameObjectAudit> gameObjects = new List<GameObjectAudit>();
        public List<ExperimentalComponentAudit> experimentalComponents = new List<ExperimentalComponentAudit>();
        public List<string> redFlags = new List<string>();
        public List<string> warnings = new List<string>();
        public NameHierarchyCandidates nameAndHierarchyCandidates = new NameHierarchyCandidates();
        public SummaryAudit summary = new SummaryAudit();
    }

    private sealed class SummaryAudit
    {
        public int totalGameObjects;
        public int totalActiveGameObjects;
        public int totalInactiveGameObjects;
        public int totalComponents;
        public int totalMissingScripts;
        public int totalStepObjects;
        public int totalExperimentalComponentsFound;
        public int totalRedFlags;
        public int totalWarnings;
    }

    private sealed class GameObjectAudit
    {
        public string path;
        public string name;
        public bool activeSelf;
        public bool activeInHierarchy;
        public string tag;
        public int layer;
        public string layerName;
        public bool isPrefabInstance;
        public string prefabAssetPath;
        public string parent;
        public int childCount;
        public TransformAudit transform = new TransformAudit();
        public List<ComponentAudit> components = new List<ComponentAudit>();
        public List<string> missingComponents = new List<string>();
        public int missingComponentCount;
        public string hierarchyCandidate;
    }

    private sealed class TransformAudit
    {
        public string localPosition;
        public string localRotationEuler;
        public string localScale;
    }

    private sealed class ComponentAudit
    {
        public string type;
        public string shortType;
        public bool isEnabled;
        public string enabledState;
        public List<PropertyAudit> properties = new List<PropertyAudit>();
    }

    private sealed class PropertyAudit
    {
        public string path;
        public string displayName;
        public string type;
        public string value;
        public ObjectReferenceAudit objectReference;
        public List<PropertyAudit> children;
    }

    private sealed class ObjectReferenceAudit
    {
        public string name;
        public string type;
        public string scenePath;
        public string assetPath;
        public string instanceId;
    }

    private sealed class ExperimentalComponentAudit
    {
        public string componentName;
        public string fullType;
        public string gameObjectPath;
        public bool gameObjectActiveInHierarchy;
        public string enabledState;
        public List<PropertyAudit> keyProperties = new List<PropertyAudit>();
    }

    private sealed class ComponentLocationAudit
    {
        public GameObjectAudit gameObject;
        public ComponentAudit component;
    }

    private sealed class NameHierarchyCandidates
    {
        public List<string> activeStepObjects = new List<string>();
        public List<string> unclearObjects = new List<string>();
        public List<string> probableLegacyDebugObjects = new List<string>();
        public List<string> experimentCandidates = new List<string>();
        public List<string> autonomyRuntimeCandidates = new List<string>();
        public List<string> robotCandidates = new List<string>();
        public List<string> roundAndSpawnCandidates = new List<string>();
        public List<string> multimodalInputCandidates = new List<string>();
        public List<string> debugLegacyCandidates = new List<string>();
    }

    [MenuItem("Tools/Multimodal AI-VR/Export Scene Audit")]
    public static void ExportSceneAudit()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
        {
            Debug.LogWarning("Scene audit export skipped: active scene is not valid.");
            return;
        }

        AuditReport report = BuildReport(scene);
        string safeSceneName = SanitizeFileName(string.IsNullOrWhiteSpace(scene.name) ? "UntitledScene" : scene.name);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string outputRoot = Path.Combine(Directory.GetCurrentDirectory(), OutputDirectory);
        Directory.CreateDirectory(outputRoot);

        string markdownPath = Path.Combine(outputRoot, $"scene_audit_{safeSceneName}_{timestamp}.md");
        string jsonPath = Path.Combine(outputRoot, $"scene_audit_{safeSceneName}_{timestamp}.json");

        bool wasDirty = scene.isDirty;
        File.WriteAllText(markdownPath, BuildMarkdown(report), Encoding.UTF8);
        File.WriteAllText(jsonPath, BuildJson(report), Encoding.UTF8);

        Debug.Log($"Scene audit exported:\nMarkdown: {markdownPath}\nJSON: {jsonPath}");
        if (scene.isDirty != wasDirty)
        {
            Debug.LogWarning("Scene audit exporter detected a scene dirty-state change after export. The exporter does not intentionally modify scene objects.");
        }
    }

    public static void ExportTargetSceneAuditForBatch()
    {
        EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
        ExportSceneAudit();
    }

    private static AuditReport BuildReport(Scene scene)
    {
        AuditReport report = new AuditReport
        {
            sceneName = scene.name,
            scenePath = scene.path,
            exportedAtLocal = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
            unityVersion = Application.unityVersion,
            maxArrayItems = MaxArrayItems
        };

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Traverse(root.transform, report);
        }

        BuildExperimentalDiagnostics(report);
        BuildNameHierarchyCandidates(report);

        report.summary.totalGameObjects = report.gameObjects.Count;
        report.summary.totalActiveGameObjects = report.gameObjects.Count(go => go.activeInHierarchy);
        report.summary.totalInactiveGameObjects = report.gameObjects.Count - report.summary.totalActiveGameObjects;
        report.summary.totalComponents = report.gameObjects.Sum(go => go.components.Count);
        report.summary.totalMissingScripts = report.gameObjects.Sum(go => go.missingComponentCount);
        report.summary.totalStepObjects = report.gameObjects.Count(go => IsStepName(go.name));
        report.summary.totalExperimentalComponentsFound = report.experimentalComponents.Count;
        report.summary.totalRedFlags = report.redFlags.Count;
        report.summary.totalWarnings = report.warnings.Count;

        if (report.summary.totalMissingScripts > 0)
        {
            report.redFlags.Add($"Missing scripts detected: {report.summary.totalMissingScripts}");
            report.summary.totalRedFlags = report.redFlags.Count;
        }

        report.summary.totalWarnings = report.warnings.Count;

        return report;
    }

    private static void Traverse(Transform transform, AuditReport report)
    {
        report.gameObjects.Add(BuildGameObjectAudit(transform.gameObject));

        for (int i = 0; i < transform.childCount; i++)
        {
            Traverse(transform.GetChild(i), report);
        }
    }

    private static GameObjectAudit BuildGameObjectAudit(GameObject gameObject)
    {
        Transform transform = gameObject.transform;
        GameObjectAudit audit = new GameObjectAudit
        {
            path = GetHierarchyPath(transform),
            name = gameObject.name,
            activeSelf = gameObject.activeSelf,
            activeInHierarchy = gameObject.activeInHierarchy,
            tag = gameObject.tag,
            layer = gameObject.layer,
            layerName = LayerMask.LayerToName(gameObject.layer),
            isPrefabInstance = PrefabUtility.IsPartOfPrefabInstance(gameObject),
            prefabAssetPath = PrefabUtility.IsPartOfPrefabInstance(gameObject) ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject) : string.Empty,
            parent = transform.parent != null ? GetHierarchyPath(transform.parent) : string.Empty,
            childCount = transform.childCount,
            hierarchyCandidate = ClassifyHierarchyCandidate(gameObject.name, GetHierarchyPath(transform))
        };

        audit.transform.localPosition = FormatVector3(transform.localPosition);
        audit.transform.localRotationEuler = FormatVector3(transform.localEulerAngles);
        audit.transform.localScale = FormatVector3(transform.localScale);

        Component[] components = gameObject.GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (component == null)
            {
                audit.missingComponentCount++;
                audit.missingComponents.Add($"Missing script/component at index {i}");
                continue;
            }

            audit.components.Add(BuildComponentAudit(component));
        }

        return audit;
    }

    private static ComponentAudit BuildComponentAudit(Component component)
    {
        Type type = component.GetType();
        ComponentAudit audit = new ComponentAudit
        {
            type = type.FullName,
            shortType = type.Name
        };

        Behaviour behaviour = component as Behaviour;
        if (behaviour != null)
        {
            audit.isEnabled = behaviour.enabled;
            audit.enabledState = behaviour.enabled ? "enabled" : "disabled";
        }
        else
        {
            audit.enabledState = "not a Behaviour";
        }

        try
        {
            SerializedObject serializedObject = new SerializedObject(component);
            SerializedProperty iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                audit.properties.Add(BuildPropertyAudit(iterator.Copy(), 0));
                enterChildren = false;
            }
        }
        catch (Exception exception)
        {
            audit.properties.Add(new PropertyAudit
            {
                path = "<serialization>",
                displayName = "Serialization",
                type = "Unsupported",
                value = $"<unsupported: {exception.GetType().Name}>"
            });
        }

        return audit;
    }

    private static PropertyAudit BuildPropertyAudit(SerializedProperty property, int depth)
    {
        PropertyAudit audit = new PropertyAudit
        {
            path = property.propertyPath,
            displayName = property.displayName,
            type = property.propertyType.ToString()
        };

        try
        {
            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                audit.value = property.objectReferenceValue != null ? property.objectReferenceValue.name : "null";
                audit.objectReference = BuildObjectReferenceAudit(property.objectReferenceValue);
            }
            else if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                audit.value = $"Array(size={property.arraySize}, showing={Mathf.Min(property.arraySize, MaxArrayItems)})";
                audit.children = new List<PropertyAudit>();
                int count = Mathf.Min(property.arraySize, MaxArrayItems);
                for (int i = 0; i < count; i++)
                {
                    audit.children.Add(BuildPropertyAudit(property.GetArrayElementAtIndex(i), depth + 1));
                }

                if (property.arraySize > MaxArrayItems)
                {
                    audit.children.Add(new PropertyAudit
                    {
                        path = property.propertyPath + ".<truncated>",
                        displayName = "Truncated",
                        type = "ArrayLimit",
                        value = $"<{property.arraySize - MaxArrayItems} additional items omitted>"
                    });
                }
            }
            else
            {
                audit.value = ReadPropertyValue(property);
            }
        }
        catch (Exception exception)
        {
            audit.value = $"<unsupported: {exception.GetType().Name}>";
        }

        return audit;
    }

    private static string ReadPropertyValue(SerializedProperty property)
    {
        switch (property.propertyType)
        {
            case SerializedPropertyType.Integer:
                return property.intValue.ToString(CultureInfo.InvariantCulture);
            case SerializedPropertyType.Boolean:
                return property.boolValue.ToString();
            case SerializedPropertyType.Float:
                return property.floatValue.ToString("G6", CultureInfo.InvariantCulture);
            case SerializedPropertyType.String:
                return LimitString(property.stringValue);
            case SerializedPropertyType.Color:
                return property.colorValue.ToString();
            case SerializedPropertyType.LayerMask:
                return $"LayerMask({property.intValue})";
            case SerializedPropertyType.Enum:
                return property.enumDisplayNames != null && property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length
                    ? property.enumDisplayNames[property.enumValueIndex]
                    : property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
            case SerializedPropertyType.Vector2:
                return property.vector2Value.ToString();
            case SerializedPropertyType.Vector3:
                return FormatVector3(property.vector3Value);
            case SerializedPropertyType.Vector4:
                return property.vector4Value.ToString();
            case SerializedPropertyType.Rect:
                return property.rectValue.ToString();
            case SerializedPropertyType.AnimationCurve:
                return property.animationCurveValue != null ? $"AnimationCurve(keys={property.animationCurveValue.length})" : "null";
            case SerializedPropertyType.Bounds:
                return property.boundsValue.ToString();
            case SerializedPropertyType.Quaternion:
                return property.quaternionValue.eulerAngles.ToString();
            case SerializedPropertyType.Vector2Int:
                return property.vector2IntValue.ToString();
            case SerializedPropertyType.Vector3Int:
                return property.vector3IntValue.ToString();
            case SerializedPropertyType.RectInt:
                return property.rectIntValue.ToString();
            case SerializedPropertyType.BoundsInt:
                return property.boundsIntValue.ToString();
            case SerializedPropertyType.ManagedReference:
                return string.IsNullOrEmpty(property.managedReferenceFullTypename) ? "null" : property.managedReferenceFullTypename;
            case SerializedPropertyType.Generic:
                return "<generic>";
            default:
                return "<unsupported>";
        }
    }

    private static ObjectReferenceAudit BuildObjectReferenceAudit(UnityEngine.Object value)
    {
        if (value == null)
        {
            return null;
        }

        ObjectReferenceAudit audit = new ObjectReferenceAudit
        {
            name = value.name,
            type = value.GetType().FullName,
            assetPath = AssetDatabase.GetAssetPath(value),
            instanceId = value.GetInstanceID().ToString(CultureInfo.InvariantCulture)
        };

        GameObject gameObject = value as GameObject;
        if (gameObject != null)
        {
            audit.scenePath = gameObject.scene.IsValid() ? GetHierarchyPath(gameObject.transform) : string.Empty;
            return audit;
        }

        Component component = value as Component;
        if (component != null && component.gameObject != null && component.gameObject.scene.IsValid())
        {
            audit.scenePath = GetHierarchyPath(component.transform);
        }

        if (IsHeavyAsset(value))
        {
            audit.name = $"{value.name} <heavy asset summarized>";
        }

        return audit;
    }

    private static void BuildExperimentalDiagnostics(AuditReport report)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>();
        foreach (string componentName in ExperimentalComponentNames)
        {
            counts[componentName] = 0;
        }

        foreach (GameObjectAudit gameObject in report.gameObjects)
        {
            foreach (ComponentAudit component in gameObject.components)
            {
                if (!counts.ContainsKey(component.shortType))
                {
                    continue;
                }

                counts[component.shortType]++;
                report.experimentalComponents.Add(new ExperimentalComponentAudit
                {
                    componentName = component.shortType,
                    fullType = component.type,
                    gameObjectPath = gameObject.path,
                    gameObjectActiveInHierarchy = gameObject.activeInHierarchy,
                    enabledState = component.enabledState,
                    keyProperties = component.properties.Where(IsKeyExperimentalProperty).ToList()
                });

                AddComponentRedFlags(report, gameObject, component);
            }
        }

        if (counts["ExperimentConditionConfigBehaviour"] > 1)
        {
            report.redFlags.Add($"More than one condition provider found: {counts["ExperimentConditionConfigBehaviour"]} ExperimentConditionConfigBehaviour components.");
        }

        if (counts["ExperimentSessionOrchestrator"] > 1)
        {
            report.redFlags.Add($"More than one ExperimentSessionOrchestrator found: {counts["ExperimentSessionOrchestrator"]}.");
        }

        DetectConditionFlagMismatches(report);
        DetectActiveStepObjects(report);
        DetectActiveRoundInEditMode(report);
    }

    private static void AddComponentRedFlags(AuditReport report, GameObjectAudit gameObject, ComponentAudit component)
    {
        foreach (PropertyAudit property in FlattenProperties(component.properties))
        {
            if (AutoStartPropertyNames.Contains(property.path) && IsTrue(property.value))
            {
                report.redFlags.Add($"Auto-start active on {component.shortType}.{property.path} at {gameObject.path}.");
            }

            if (property.path == "spawnGenerationMode" && component.shortType == "SpawnManager" && !string.IsNullOrEmpty(property.value) && property.value != "RandomBalanced")
            {
                if (SpawnGenerationModeCanCompeteWithOrchestrator(report, component, property.value))
                {
                    report.redFlags.Add($"SpawnManager.spawnGenerationMode is {property.value} at {gameObject.path}; verify it cannot compete with the orchestrator context.");
                }
                else
                {
                    report.warnings.Add($"SpawnManager.spawnGenerationMode is {NormalizeEnumToken(property.value)} at {gameObject.path} but is treated as legacy/debug fallback because ExperimentSessionOrchestrator controls spawn in Orchestrated2x2.");
                }
            }

            if (property.path == "_preferConfiguredTargetForCategory" && IsTrue(property.value))
            {
                report.redFlags.Add($"Prefer Configured Target For Category is active at {gameObject.path}.");
            }

            if (property.value != null &&
                ContainsLegacyStepReference(property.value) &&
                !IsInDebugLegacy(gameObject.path))
            {
                AddLegacyReferenceFinding(report, gameObject, component, $"{component.shortType}.{property.path}", property.value);
            }

            if (property.objectReference != null &&
                !string.IsNullOrEmpty(property.objectReference.scenePath) &&
                ContainsLegacyStepReference(property.objectReference.scenePath) &&
                !IsInDebugLegacy(gameObject.path))
            {
                AddLegacyReferenceFinding(report, gameObject, component, $"{component.shortType}.{property.path}", property.objectReference.scenePath);
            }
        }
    }

    private static void DetectConditionFlagMismatches(AuditReport report)
    {
        foreach (ExperimentalComponentAudit component in report.experimentalComponents)
        {
            string conditionId = GetPropertyValue(component.keyProperties, "_conditionId", "conditionId", "condition_id");
            string robotEnabled = GetPropertyValue(component.keyProperties, "_robotEnabled", "robotEnabled", "robot_enabled");
            string voiceEnabled = GetPropertyValue(component.keyProperties, "_voiceEnabled", "voiceEnabled", "voice_enabled");

            if (component.gameObjectActiveInHierarchy &&
                !IsInDebugLegacy(component.gameObjectPath) &&
                Is2x2ConditionId(conditionId) &&
                !HasRuntimeContextOrActiveTrial(component))
            {
                report.redFlags.Add($"Active legacy condition_id before runtime context/trial on {component.componentName} at {component.gameObjectPath}: {conditionId}.");
            }

            if (string.IsNullOrEmpty(conditionId) || string.IsNullOrEmpty(robotEnabled) || string.IsNullOrEmpty(voiceEnabled))
            {
                continue;
            }

            bool robot;
            bool voice;
            if (!bool.TryParse(robotEnabled, out robot) || !bool.TryParse(voiceEnabled, out voice))
            {
                continue;
            }

            string lower = conditionId.ToLowerInvariant();
            bool mentionsRobotOn = lower.Contains("robot_on") || lower.Contains("c10") || lower.Contains("c11");
            bool mentionsRobotOff = lower.Contains("robot_off") || lower.Contains("c00") || lower.Contains("c01");
            bool mentionsVoiceOn = lower.Contains("voice_on") || lower.Contains("c01") || lower.Contains("c11");
            bool mentionsVoiceOff = lower.Contains("voice_off") || lower.Contains("c00") || lower.Contains("c10");

            if ((mentionsRobotOn && !robot) || (mentionsRobotOff && robot) || (mentionsVoiceOn && !voice) || (mentionsVoiceOff && voice))
            {
                report.redFlags.Add($"condition_id may be inconsistent with robot_enabled/voice_enabled on {component.componentName} at {component.gameObjectPath}: {conditionId}, robot={robot}, voice={voice}.");
            }
        }
    }

    private static void DetectActiveStepObjects(AuditReport report)
    {
        foreach (GameObjectAudit gameObject in report.gameObjects)
        {
            if (!gameObject.activeInHierarchy || !IsStepName(gameObject.name))
            {
                continue;
            }

            string lowerPath = gameObject.path.ToLowerInvariant();
            if (!IsInDebugLegacy(gameObject.path))
            {
                report.redFlags.Add($"Active STEPXX object outside DebugLegacy: {gameObject.path}.");
            }
        }
    }

    private static void DetectActiveRoundInEditMode(AuditReport report)
    {
        foreach (GameObjectAudit gameObject in report.gameObjects)
        {
            if (gameObject.name.IndexOf("ActiveRound", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (gameObject.childCount > 0)
            {
                report.redFlags.Add($"ActiveRound object has children in edit mode: {gameObject.path} ({gameObject.childCount} children).");
            }
        }
    }

    private static void BuildNameHierarchyCandidates(AuditReport report)
    {
        foreach (GameObjectAudit gameObject in report.gameObjects)
        {
            string lower = gameObject.path.ToLowerInvariant();
            if (gameObject.activeInHierarchy && IsStepName(gameObject.name))
            {
                report.nameAndHierarchyCandidates.activeStepObjects.Add(gameObject.path);
            }

            if (IsUnclearName(gameObject.name))
            {
                report.nameAndHierarchyCandidates.unclearObjects.Add(gameObject.path);
            }

            if (LooksLegacyDebug(lower))
            {
                report.nameAndHierarchyCandidates.probableLegacyDebugObjects.Add(gameObject.path);
            }

            switch (gameObject.hierarchyCandidate)
            {
                case "Experiment":
                    report.nameAndHierarchyCandidates.experimentCandidates.Add(gameObject.path);
                    break;
                case "AutonomyRuntime":
                    report.nameAndHierarchyCandidates.autonomyRuntimeCandidates.Add(gameObject.path);
                    break;
                case "Robot":
                    report.nameAndHierarchyCandidates.robotCandidates.Add(gameObject.path);
                    break;
                case "RoundAndSpawn":
                    report.nameAndHierarchyCandidates.roundAndSpawnCandidates.Add(gameObject.path);
                    break;
                case "MultimodalInput":
                    report.nameAndHierarchyCandidates.multimodalInputCandidates.Add(gameObject.path);
                    break;
                case "DebugLegacy":
                    report.nameAndHierarchyCandidates.debugLegacyCandidates.Add(gameObject.path);
                    break;
            }
        }
    }

    private static bool IsKeyExperimentalProperty(PropertyAudit property)
    {
        string path = property.path ?? string.Empty;
        return path.IndexOf("condition", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("robot", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("voice", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("round", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("target", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("slot", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("mode", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("context", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("trial", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("orchestr", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("instrument", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IEnumerable<PropertyAudit> FlattenProperties(IEnumerable<PropertyAudit> properties)
    {
        foreach (PropertyAudit property in properties)
        {
            yield return property;
            if (property.children == null)
            {
                continue;
            }

            foreach (PropertyAudit child in FlattenProperties(property.children))
            {
                yield return child;
            }
        }
    }

    private static bool SpawnGenerationModeCanCompeteWithOrchestrator(AuditReport report, ComponentAudit spawnManager, string spawnGenerationMode)
    {
        string spawnOnStart = GetPropertyValue(spawnManager.properties, "spawnOnStart");
        if (!IsFalse(spawnOnStart))
        {
            return true;
        }

        ComponentLocationAudit orchestrator = FindActiveComponent(report, "ExperimentSessionOrchestrator");

        if (orchestrator == null || !IsOrchestrated2x2(GetPropertyValue(orchestrator.component.properties, "_runMode", "runMode")))
        {
            return true;
        }

        string orchestratorDefault = GetPropertyValue(orchestrator.component.properties, "_defaultSpawnGenerationMode", "defaultSpawnGenerationMode");
        if (string.IsNullOrEmpty(orchestratorDefault))
        {
            return true;
        }

        return !EnumDisplayEquals(orchestratorDefault, spawnGenerationMode);
    }

    private static ComponentLocationAudit FindActiveComponent(AuditReport report, string shortType)
    {
        foreach (GameObjectAudit gameObject in report.gameObjects)
        {
            if (!gameObject.activeInHierarchy)
            {
                continue;
            }

            ComponentAudit component = gameObject.components.FirstOrDefault(candidate => candidate.shortType == shortType);
            if (component != null)
            {
                return new ComponentLocationAudit
                {
                    gameObject = gameObject,
                    component = component
                };
            }
        }

        return null;
    }

    private static void AddLegacyReferenceFinding(AuditReport report, GameObjectAudit gameObject, ComponentAudit component, string propertyPath, string referencedValue)
    {
        string message = $"{propertyPath} at {gameObject.path} references legacy STEP target outside DebugLegacy -> {referencedValue}.";
        if (gameObject.activeInHierarchy && ComponentCanExecute(component))
        {
            report.redFlags.Add("Active enabled component " + message);
        }
        else
        {
            report.warnings.Add("Disabled/inactive component " + message);
        }
    }

    private static bool ComponentCanExecute(ComponentAudit component)
    {
        return string.Equals(component.enabledState, "enabled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(component.enabledState, "not a Behaviour", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRuntimeContextOrActiveTrial(ExperimentalComponentAudit component)
    {
        string trialActive = GetPropertyValue(component.keyProperties, "_trialActive", "trialActive", "TrialActive");
        string currentContext = GetPropertyValue(component.keyProperties, "_currentContext", "currentContext", "CurrentContext");
        return IsTrue(trialActive) || !string.IsNullOrEmpty(currentContext) && currentContext != "null";
    }

    private static bool IsOrchestrated2x2(string value)
    {
        return EnumDisplayEquals(value, "Orchestrated2x2");
    }

    private static bool EnumDisplayEquals(string left, string right)
    {
        return NormalizeEnumDisplay(left) == NormalizeEnumDisplay(right);
    }

    private static string NormalizeEnumDisplay(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static string NormalizeEnumToken(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrEmpty(normalized) ? value : normalized;
    }

    private static bool IsFalse(string value)
    {
        return string.Equals(value, "False", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsLegacyStepReference(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value.IndexOf("STEP19", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("STEP21", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("STEP22", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("STEP31", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("Step19", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool Is2x2ConditionId(string value)
    {
        return string.Equals(value, "C00", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "C01", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "C10", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "C11", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInDebugLegacy(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.Equals("DebugLegacy", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("DebugLegacy/", StringComparison.OrdinalIgnoreCase) ||
               path.IndexOf("/DebugLegacy/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetPropertyValue(IEnumerable<PropertyAudit> properties, params string[] names)
    {
        foreach (PropertyAudit property in FlattenProperties(properties))
        {
            if (PropertyPathMatches(property.path, names))
            {
                return property.value;
            }
        }

        return string.Empty;
    }

    private static bool PropertyPathMatches(string propertyPath, params string[] names)
    {
        if (string.IsNullOrEmpty(propertyPath))
        {
            return false;
        }

        string leafName = propertyPath;
        int dotIndex = leafName.LastIndexOf('.');
        if (dotIndex >= 0 && dotIndex + 1 < leafName.Length)
        {
            leafName = leafName.Substring(dotIndex + 1);
        }

        foreach (string name in names)
        {
            if (string.Equals(propertyPath, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leafName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildMarkdown(AuditReport report)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("# Scene Audit");
        builder.AppendLine();
        builder.AppendLine($"- Scene: `{report.sceneName}`");
        builder.AppendLine($"- Scene path: `{report.scenePath}`");
        builder.AppendLine($"- Exported at: `{report.exportedAtLocal}`");
        builder.AppendLine($"- Unity: `{report.unityVersion}`");
        builder.AppendLine($"- Max array items: `{report.maxArrayItems}`");
        builder.AppendLine();

        AppendSummaryMarkdown(builder, report.summary);
        AppendRedFlagsMarkdown(builder, report.redFlags);
        AppendWarningsMarkdown(builder, report.warnings);
        AppendExperimentalMarkdown(builder, report.experimentalComponents);
        AppendNameCandidatesMarkdown(builder, report.nameAndHierarchyCandidates);
        AppendObjectsMarkdown(builder, report.gameObjects);
        AppendSummaryMarkdown(builder, report.summary);

        return builder.ToString();
    }

    private static void AppendSummaryMarkdown(StringBuilder builder, SummaryAudit summary)
    {
        builder.AppendLine("## Compact Summary");
        builder.AppendLine();
        builder.AppendLine($"- Total GameObjects: {summary.totalGameObjects}");
        builder.AppendLine($"- Active / inactive GameObjects: {summary.totalActiveGameObjects} / {summary.totalInactiveGameObjects}");
        builder.AppendLine($"- Total components: {summary.totalComponents}");
        builder.AppendLine($"- Total missing scripts: {summary.totalMissingScripts}");
        builder.AppendLine($"- Total STEPXX objects: {summary.totalStepObjects}");
        builder.AppendLine($"- Experimental components found: {summary.totalExperimentalComponentsFound}");
        builder.AppendLine($"- Red flags: {summary.totalRedFlags}");
        builder.AppendLine($"- Warnings: {summary.totalWarnings}");
        builder.AppendLine();
    }

    private static void AppendRedFlagsMarkdown(StringBuilder builder, List<string> redFlags)
    {
        builder.AppendLine("## Red Flags");
        builder.AppendLine();
        if (redFlags.Count == 0)
        {
            builder.AppendLine("- None detected by this read-only audit.");
        }
        else
        {
            foreach (string redFlag in redFlags)
            {
                builder.AppendLine($"- {redFlag}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendWarningsMarkdown(StringBuilder builder, List<string> warnings)
    {
        builder.AppendLine("## Warnings");
        builder.AppendLine();
        if (warnings.Count == 0)
        {
            builder.AppendLine("- None detected by this read-only audit.");
        }
        else
        {
            foreach (string warning in warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendExperimentalMarkdown(StringBuilder builder, List<ExperimentalComponentAudit> components)
    {
        builder.AppendLine("## Experimental Diagnostics");
        builder.AppendLine();
        foreach (ExperimentalComponentAudit component in components)
        {
            builder.AppendLine($"### {component.componentName}");
            builder.AppendLine($"- GameObject: `{component.gameObjectPath}`");
            builder.AppendLine($"- Type: `{component.fullType}`");
            builder.AppendLine($"- Active in hierarchy: `{component.gameObjectActiveInHierarchy}`");
            builder.AppendLine($"- Enabled: `{component.enabledState}`");
            foreach (PropertyAudit property in component.keyProperties)
            {
                builder.AppendLine($"- `{property.path}` = `{property.value}`");
            }

            builder.AppendLine();
        }
    }

    private static void AppendNameCandidatesMarkdown(StringBuilder builder, NameHierarchyCandidates candidates)
    {
        builder.AppendLine("## Name and Hierarchy Candidates");
        builder.AppendLine();
        AppendListMarkdown(builder, "Active STEP objects", candidates.activeStepObjects);
        AppendListMarkdown(builder, "Objects without clear prefix/category", candidates.unclearObjects);
        AppendListMarkdown(builder, "Probable legacy/debug objects", candidates.probableLegacyDebugObjects);
        AppendListMarkdown(builder, "Experiment candidates", candidates.experimentCandidates);
        AppendListMarkdown(builder, "AutonomyRuntime candidates", candidates.autonomyRuntimeCandidates);
        AppendListMarkdown(builder, "Robot candidates", candidates.robotCandidates);
        AppendListMarkdown(builder, "RoundAndSpawn candidates", candidates.roundAndSpawnCandidates);
        AppendListMarkdown(builder, "MultimodalInput candidates", candidates.multimodalInputCandidates);
        AppendListMarkdown(builder, "DebugLegacy candidates", candidates.debugLegacyCandidates);
    }

    private static void AppendListMarkdown(StringBuilder builder, string title, List<string> values)
    {
        builder.AppendLine($"### {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (string value in values.Take(200))
            {
                builder.AppendLine($"- `{value}`");
            }

            if (values.Count > 200)
            {
                builder.AppendLine($"- <{values.Count - 200} additional entries omitted from Markdown; see JSON>");
            }
        }

        builder.AppendLine();
    }

    private static void AppendObjectsMarkdown(StringBuilder builder, List<GameObjectAudit> gameObjects)
    {
        builder.AppendLine("## Scene Hierarchy and Components");
        builder.AppendLine();
        foreach (GameObjectAudit gameObject in gameObjects)
        {
            builder.AppendLine($"### {gameObject.path}");
            builder.AppendLine($"- Name: `{gameObject.name}`");
            builder.AppendLine($"- Active self / hierarchy: `{gameObject.activeSelf}` / `{gameObject.activeInHierarchy}`");
            builder.AppendLine($"- Tag / layer: `{gameObject.tag}` / `{gameObject.layerName}` ({gameObject.layer})");
            builder.AppendLine($"- Prefab instance: `{gameObject.isPrefabInstance}` `{gameObject.prefabAssetPath}`");
            builder.AppendLine($"- Parent: `{gameObject.parent}`");
            builder.AppendLine($"- Children: `{gameObject.childCount}`");
            builder.AppendLine($"- Local transform: pos `{gameObject.transform.localPosition}`, rot `{gameObject.transform.localRotationEuler}`, scale `{gameObject.transform.localScale}`");
            builder.AppendLine($"- Missing components: `{gameObject.missingComponentCount}`");
            foreach (string missingComponent in gameObject.missingComponents)
            {
                builder.AppendLine($"  - {missingComponent}");
            }

            builder.AppendLine("- Components:");
            foreach (ComponentAudit component in gameObject.components)
            {
                builder.AppendLine($"  - `{component.type}` ({component.enabledState})");
                foreach (PropertyAudit property in component.properties)
                {
                    AppendPropertyMarkdown(builder, property, 4);
                }
            }

            builder.AppendLine();
        }
    }

    private static void AppendPropertyMarkdown(StringBuilder builder, PropertyAudit property, int indent)
    {
        string spaces = new string(' ', indent);
        string reference = property.objectReference != null
            ? $" ref(name=`{property.objectReference.name}`, type=`{property.objectReference.type}`, scene=`{property.objectReference.scenePath}`, asset=`{property.objectReference.assetPath}`)"
            : string.Empty;
        builder.AppendLine($"{spaces}- `{property.path}` ({property.type}) = `{property.value}`{reference}");
        if (property.children == null)
        {
            return;
        }

        foreach (PropertyAudit child in property.children)
        {
            AppendPropertyMarkdown(builder, child, indent + 2);
        }
    }

    private static string BuildJson(AuditReport report)
    {
        StringBuilder builder = new StringBuilder();
        JsonWriter writer = new JsonWriter(builder);
        writer.BeginObject();
        writer.WriteProperty("sceneName", report.sceneName);
        writer.WriteProperty("scenePath", report.scenePath);
        writer.WriteProperty("exportedAtLocal", report.exportedAtLocal);
        writer.WriteProperty("unityVersion", report.unityVersion);
        writer.WriteProperty("maxArrayItems", report.maxArrayItems);
        writer.WritePropertyName("summary");
        WriteSummary(writer, report.summary);
        writer.WritePropertyName("redFlags");
        WriteStringArray(writer, report.redFlags);
        writer.WritePropertyName("warnings");
        WriteStringArray(writer, report.warnings);
        writer.WritePropertyName("experimentalComponents");
        WriteExperimentalArray(writer, report.experimentalComponents);
        writer.WritePropertyName("nameAndHierarchyCandidates");
        WriteNameCandidates(writer, report.nameAndHierarchyCandidates);
        writer.WritePropertyName("gameObjects");
        WriteGameObjects(writer, report.gameObjects);
        writer.EndObject();
        return builder.ToString();
    }

    private static void WriteSummary(JsonWriter writer, SummaryAudit summary)
    {
        writer.BeginObject();
        writer.WriteProperty("totalGameObjects", summary.totalGameObjects);
        writer.WriteProperty("totalActiveGameObjects", summary.totalActiveGameObjects);
        writer.WriteProperty("totalInactiveGameObjects", summary.totalInactiveGameObjects);
        writer.WriteProperty("totalComponents", summary.totalComponents);
        writer.WriteProperty("totalMissingScripts", summary.totalMissingScripts);
        writer.WriteProperty("totalStepObjects", summary.totalStepObjects);
        writer.WriteProperty("totalExperimentalComponentsFound", summary.totalExperimentalComponentsFound);
        writer.WriteProperty("totalRedFlags", summary.totalRedFlags);
        writer.WriteProperty("totalWarnings", summary.totalWarnings);
        writer.EndObject();
    }

    private static void WriteGameObjects(JsonWriter writer, List<GameObjectAudit> gameObjects)
    {
        writer.BeginArray();
        foreach (GameObjectAudit gameObject in gameObjects)
        {
            writer.BeginObject();
            writer.WriteProperty("path", gameObject.path);
            writer.WriteProperty("name", gameObject.name);
            writer.WriteProperty("activeSelf", gameObject.activeSelf);
            writer.WriteProperty("activeInHierarchy", gameObject.activeInHierarchy);
            writer.WriteProperty("tag", gameObject.tag);
            writer.WriteProperty("layer", gameObject.layer);
            writer.WriteProperty("layerName", gameObject.layerName);
            writer.WriteProperty("isPrefabInstance", gameObject.isPrefabInstance);
            writer.WriteProperty("prefabAssetPath", gameObject.prefabAssetPath);
            writer.WriteProperty("parent", gameObject.parent);
            writer.WriteProperty("childCount", gameObject.childCount);
            writer.WriteProperty("missingComponentCount", gameObject.missingComponentCount);
            writer.WritePropertyName("missingComponents");
            WriteStringArray(writer, gameObject.missingComponents);
            writer.WriteProperty("hierarchyCandidate", gameObject.hierarchyCandidate);
            writer.WritePropertyName("transform");
            writer.BeginObject();
            writer.WriteProperty("localPosition", gameObject.transform.localPosition);
            writer.WriteProperty("localRotationEuler", gameObject.transform.localRotationEuler);
            writer.WriteProperty("localScale", gameObject.transform.localScale);
            writer.EndObject();
            writer.WritePropertyName("components");
            WriteComponents(writer, gameObject.components);
            writer.EndObject();
        }

        writer.EndArray();
    }

    private static void WriteComponents(JsonWriter writer, List<ComponentAudit> components)
    {
        writer.BeginArray();
        foreach (ComponentAudit component in components)
        {
            writer.BeginObject();
            writer.WriteProperty("type", component.type);
            writer.WriteProperty("shortType", component.shortType);
            writer.WriteProperty("isEnabled", component.isEnabled);
            writer.WriteProperty("enabledState", component.enabledState);
            writer.WritePropertyName("properties");
            WriteProperties(writer, component.properties);
            writer.EndObject();
        }

        writer.EndArray();
    }

    private static void WriteProperties(JsonWriter writer, List<PropertyAudit> properties)
    {
        writer.BeginArray();
        foreach (PropertyAudit property in properties)
        {
            writer.BeginObject();
            writer.WriteProperty("path", property.path);
            writer.WriteProperty("displayName", property.displayName);
            writer.WriteProperty("type", property.type);
            writer.WriteProperty("value", property.value);
            if (property.objectReference != null)
            {
                writer.WritePropertyName("objectReference");
                WriteObjectReference(writer, property.objectReference);
            }
            else
            {
                writer.WriteNullProperty("objectReference");
            }

            if (property.children != null)
            {
                writer.WritePropertyName("children");
                WriteProperties(writer, property.children);
            }
            else
            {
                writer.WriteNullProperty("children");
            }

            writer.EndObject();
        }

        writer.EndArray();
    }

    private static void WriteObjectReference(JsonWriter writer, ObjectReferenceAudit reference)
    {
        writer.BeginObject();
        writer.WriteProperty("name", reference.name);
        writer.WriteProperty("type", reference.type);
        writer.WriteProperty("scenePath", reference.scenePath);
        writer.WriteProperty("assetPath", reference.assetPath);
        writer.WriteProperty("instanceId", reference.instanceId);
        writer.EndObject();
    }

    private static void WriteExperimentalArray(JsonWriter writer, List<ExperimentalComponentAudit> components)
    {
        writer.BeginArray();
        foreach (ExperimentalComponentAudit component in components)
        {
            writer.BeginObject();
            writer.WriteProperty("componentName", component.componentName);
            writer.WriteProperty("fullType", component.fullType);
            writer.WriteProperty("gameObjectPath", component.gameObjectPath);
            writer.WriteProperty("gameObjectActiveInHierarchy", component.gameObjectActiveInHierarchy);
            writer.WriteProperty("enabledState", component.enabledState);
            writer.WritePropertyName("keyProperties");
            WriteProperties(writer, component.keyProperties);
            writer.EndObject();
        }

        writer.EndArray();
    }

    private static void WriteNameCandidates(JsonWriter writer, NameHierarchyCandidates candidates)
    {
        writer.BeginObject();
        writer.WritePropertyName("activeStepObjects");
        WriteStringArray(writer, candidates.activeStepObjects);
        writer.WritePropertyName("unclearObjects");
        WriteStringArray(writer, candidates.unclearObjects);
        writer.WritePropertyName("probableLegacyDebugObjects");
        WriteStringArray(writer, candidates.probableLegacyDebugObjects);
        writer.WritePropertyName("experimentCandidates");
        WriteStringArray(writer, candidates.experimentCandidates);
        writer.WritePropertyName("autonomyRuntimeCandidates");
        WriteStringArray(writer, candidates.autonomyRuntimeCandidates);
        writer.WritePropertyName("robotCandidates");
        WriteStringArray(writer, candidates.robotCandidates);
        writer.WritePropertyName("roundAndSpawnCandidates");
        WriteStringArray(writer, candidates.roundAndSpawnCandidates);
        writer.WritePropertyName("multimodalInputCandidates");
        WriteStringArray(writer, candidates.multimodalInputCandidates);
        writer.WritePropertyName("debugLegacyCandidates");
        WriteStringArray(writer, candidates.debugLegacyCandidates);
        writer.EndObject();
    }

    private static void WriteStringArray(JsonWriter writer, List<string> values)
    {
        writer.BeginArray();
        foreach (string value in values)
        {
            writer.WriteValue(value);
        }

        writer.EndArray();
    }

    private static string GetHierarchyPath(Transform transform)
    {
        Stack<string> names = new Stack<string>();
        while (transform != null)
        {
            names.Push(transform.name);
            transform = transform.parent;
        }

        return string.Join("/", names.ToArray());
    }

    private static string FormatVector3(Vector3 value)
    {
        return string.Format(CultureInfo.InvariantCulture, "({0:G6}, {1:G6}, {2:G6})", value.x, value.y, value.z);
    }

    private static string LimitString(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxStringLength)
        {
            return value ?? string.Empty;
        }

        return value.Substring(0, MaxStringLength) + $" <truncated {value.Length - MaxStringLength} chars>";
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }

    private static bool IsTrue(string value)
    {
        return string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStepName(string name)
    {
        return !string.IsNullOrEmpty(name) && name.StartsWith("STEP", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnclearName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (IsStepName(name))
        {
            return false;
        }

        string lower = name.ToLowerInvariant();
        string[] clearTokens =
        {
            "experiment", "autonomy", "robot", "tiago", "round", "spawn", "voice", "multimodal",
            "debug", "legacy", "camera", "light", "xr", "canvas", "event", "manager", "zone",
            "slot", "table", "environment", "player", "floor"
        };

        return !clearTokens.Any(token => lower.Contains(token));
    }

    private static bool LooksLegacyDebug(string lowerPath)
    {
        return lowerPath.Contains("legacy") ||
               lowerPath.Contains("debug") ||
               lowerPath.Contains("test") ||
               lowerPath.Contains("old") ||
               lowerPath.Contains("step");
    }

    private static string ClassifyHierarchyCandidate(string name, string path)
    {
        string lower = (name + " " + path).ToLowerInvariant();
        if (LooksLegacyDebug(lower))
        {
            return "DebugLegacy";
        }

        if (lower.Contains("experiment") || lower.Contains("condition") || lower.Contains("instrument"))
        {
            return "Experiment";
        }

        if (lower.Contains("autonomy") || lower.Contains("bridge") || lower.Contains("blackboard") || lower.Contains("behavior"))
        {
            return "AutonomyRuntime";
        }

        if (lower.Contains("robot") || lower.Contains("tiago"))
        {
            return "Robot";
        }

        if (lower.Contains("round") || lower.Contains("spawn") || lower.Contains("deposit") || lower.Contains("slot"))
        {
            return "RoundAndSpawn";
        }

        if (lower.Contains("voice") || lower.Contains("multimodal") || lower.Contains("asr") || lower.Contains("vad"))
        {
            return "MultimodalInput";
        }

        return string.Empty;
    }

    private static bool IsHeavyAsset(UnityEngine.Object value)
    {
        return value is Mesh ||
               value is Material ||
               value is Texture ||
               value is AnimationClip ||
               value is AudioClip;
    }

    private sealed class JsonWriter
    {
        private readonly StringBuilder _builder;
        private readonly Stack<bool> _firstElementStack = new Stack<bool>();
        private bool _expectingPropertyValue;

        public JsonWriter(StringBuilder builder)
        {
            _builder = builder;
        }

        public void BeginObject()
        {
            WriteCommaIfNeeded();
            _builder.Append('{');
            _firstElementStack.Push(true);
        }

        public void EndObject()
        {
            _builder.Append('}');
            _firstElementStack.Pop();
        }

        public void BeginArray()
        {
            WriteCommaIfNeeded();
            _builder.Append('[');
            _firstElementStack.Push(true);
        }

        public void EndArray()
        {
            _builder.Append(']');
            _firstElementStack.Pop();
        }

        public void WritePropertyName(string name)
        {
            WriteCommaIfNeeded();
            WriteEscaped(name);
            _builder.Append(':');
            _expectingPropertyValue = true;
        }

        public void WriteProperty(string name, string value)
        {
            WritePropertyName(name);
            WriteValue(value);
        }

        public void WriteProperty(string name, int value)
        {
            WritePropertyName(name);
            WriteValue(value);
        }

        public void WriteProperty(string name, bool value)
        {
            WritePropertyName(name);
            WriteValue(value);
        }

        public void WriteNullProperty(string name)
        {
            WritePropertyName(name);
            WriteNull();
        }

        public void WriteValue(string value)
        {
            WriteCommaIfNeeded();
            if (value == null)
            {
                _builder.Append("null");
            }
            else
            {
                WriteEscaped(value);
            }
        }

        public void WriteValue(int value)
        {
            WriteCommaIfNeeded();
            _builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        public void WriteValue(bool value)
        {
            WriteCommaIfNeeded();
            _builder.Append(value ? "true" : "false");
        }

        public void WriteNull()
        {
            WriteCommaIfNeeded();
            _builder.Append("null");
        }

        private void WriteCommaIfNeeded()
        {
            if (_expectingPropertyValue)
            {
                _expectingPropertyValue = false;
                return;
            }

            if (_firstElementStack.Count == 0)
            {
                return;
            }

            bool first = _firstElementStack.Pop();
            if (!first)
            {
                _builder.Append(',');
            }

            _firstElementStack.Push(false);
        }

        private void WriteEscaped(string value)
        {
            _builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\':
                        _builder.Append("\\\\");
                        break;
                    case '"':
                        _builder.Append("\\\"");
                        break;
                    case '\n':
                        _builder.Append("\\n");
                        break;
                    case '\r':
                        _builder.Append("\\r");
                        break;
                    case '\t':
                        _builder.Append("\\t");
                        break;
                    case '\b':
                        _builder.Append("\\b");
                        break;
                    case '\f':
                        _builder.Append("\\f");
                        break;
                    default:
                        if (char.IsControl(c))
                        {
                            _builder.Append("\\u");
                            _builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _builder.Append(c);
                        }

                        break;
                }
            }

            _builder.Append('"');
        }
    }
}
