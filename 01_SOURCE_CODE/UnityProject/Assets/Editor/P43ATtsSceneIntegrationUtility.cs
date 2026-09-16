using System;
using System.Collections.Generic;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityObject = UnityEngine.Object;

public static class P43ATtsSceneIntegrationUtility
{
    private const string ScenePath = "Assets/Scenes/autonomous_demo_step22_multimodal_bridge.unity";

    [MenuItem("Tools/TFG/P43A/Connect Structured TTS Feedback Sink")]
    public static void ConnectStructuredTtsFeedbackSinkForBatch()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        VoiceAutonomyCommandConnector[] connectors = FindSceneObjects<VoiceAutonomyCommandConnector>();
        if (connectors.Length == 0)
        {
            throw new InvalidOperationException("No VoiceAutonomyCommandConnector found in the target scene.");
        }

        int added = 0;
        foreach (VoiceAutonomyCommandConnector connector in connectors)
        {
            StructuredTtsFeedbackSink sink = connector.GetComponent<StructuredTtsFeedbackSink>();
            if (sink == null)
            {
                sink = connector.gameObject.AddComponent<StructuredTtsFeedbackSink>();
                added++;
            }

            MonoBehaviour conditionProvider = ReadObjectReference<MonoBehaviour>(connector, "_experimentConditionProviderComponent")
                ?? FindFirstSceneObject<ExperimentConditionConfigBehaviour>();

            SerializedObject sinkObject = new SerializedObject(sink);
            SetBool(sinkObject, "_enabled", true);
            SetBool(sinkObject, "_diagnosticModeNoSpeech", false);
            SetBool(sinkObject, "_onlyWhenVoiceEnabled", true);
            SetObject(sinkObject, "_experimentConditionProviderComponent", conditionProvider);
            SetString(sinkObject, "_languageOrVoice", "es-ES");
            SetFloat(sinkObject, "_volume", 0.85f);
            SetFloat(sinkObject, "_rate", 0f);
            SetString(sinkObject, "_preferredCulture", "es-ES");
            SetString(sinkObject, "_preferredVoiceName", string.Empty);
            SetString(sinkObject, "_preferredVoiceGender", string.Empty);
            SetInt(sinkObject, "_voiceRate", -1);
            SetInt(sinkObject, "_voiceVolume", 100);
            SetBool(sinkObject, "_logInstalledVoicesOnAwake", true);
            SetBool(sinkObject, "_requirePreferredCulture", false);
            SetInt(sinkObject, "_preSpeechSilenceMs", 350);
            SetBool(sinkObject, "_useSsmlForPowershellSapi", true);
            SetInt(sinkObject, "_maxQueueSize", 4);
            SetFloat(sinkObject, "_duplicateCooldownSeconds", 1.5f);
            SetBool(sinkObject, "_logDiagnostics", true);
            SetBool(sinkObject, "_debugSpeakTestNow", false);
            SetString(sinkObject, "_debugSpeakText", "Prueba de voz del robot.");
            SetBool(sinkObject, "_debugSpanishSpeakTestNow", false);
            SetString(sinkObject, "_debugSpanishSpeakText", "Prueba de voz del robot en español.");
            sinkObject.ApplyModifiedPropertiesWithoutUndo();

            RobotVoiceFeedbackDiagnosticRecorder recorder = connector.GetComponent<RobotVoiceFeedbackDiagnosticRecorder>();
            if (recorder == null)
            {
                recorder = connector.gameObject.AddComponent<RobotVoiceFeedbackDiagnosticRecorder>();
            }

            SerializedObject connectorObject = new SerializedObject(connector);
            SetObject(connectorObject, "_robotFeedbackSinkComponent", sink);
            SetObject(connectorObject, "_robotVoiceFeedbackDiagnosticRecorder", recorder);
            connectorObject.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log(
                $"[P43ATtsSceneIntegrationUtility] connected StructuredTtsFeedbackSink on '{connector.gameObject.name}' " +
                $"condition_provider='{(conditionProvider != null ? conditionProvider.name : "<none>")}'");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[P43ATtsSceneIntegrationUtility] scene_saved added_sinks={added} connectors={connectors.Length} path={ScenePath}");
    }

    [MenuItem("Tools/TFG/P43A/Validate Structured TTS Feedback Sink")]
    public static void ValidateStructuredTtsFeedbackSinkForBatch()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        VoiceAutonomyCommandConnector[] connectors = FindSceneObjects<VoiceAutonomyCommandConnector>();
        if (connectors.Length == 0)
        {
            throw new InvalidOperationException("No VoiceAutonomyCommandConnector found in the target scene.");
        }

        int validated = 0;
        foreach (VoiceAutonomyCommandConnector connector in connectors)
        {
            StructuredTtsFeedbackSink sink = connector.GetComponent<StructuredTtsFeedbackSink>();
            if (sink == null)
            {
                throw new InvalidOperationException($"VoiceAutonomyCommandConnector on '{connector.gameObject.name}' has no local StructuredTtsFeedbackSink.");
            }

            MonoBehaviour assignedSink = ReadObjectReference<MonoBehaviour>(connector, "_robotFeedbackSinkComponent");
            if (!ReferenceEquals(assignedSink, sink))
            {
                throw new InvalidOperationException($"VoiceAutonomyCommandConnector on '{connector.gameObject.name}' is not explicitly assigned to its local TTS sink.");
            }

            RobotVoiceFeedbackDiagnosticRecorder recorder = connector.GetComponent<RobotVoiceFeedbackDiagnosticRecorder>();
            MonoBehaviour assignedRecorder = ReadObjectReference<MonoBehaviour>(connector, "_robotVoiceFeedbackDiagnosticRecorder");
            if (recorder == null || !ReferenceEquals(assignedRecorder, recorder))
            {
                throw new InvalidOperationException($"VoiceAutonomyCommandConnector on '{connector.gameObject.name}' is not explicitly assigned to its local diagnostic recorder.");
            }

            MonoBehaviour conditionProvider = ReadObjectReference<MonoBehaviour>(sink, "_experimentConditionProviderComponent");
            if (conditionProvider is not ExperimentConditionConfigBehaviour experimentConditionProvider)
            {
                throw new InvalidOperationException($"StructuredTtsFeedbackSink on '{connector.gameObject.name}' has no canonical condition provider assigned.");
            }

            if (!ReadBool(sink, "_enabled") || !ReadBool(sink, "_onlyWhenVoiceEnabled"))
            {
                throw new InvalidOperationException($"StructuredTtsFeedbackSink on '{connector.gameObject.name}' is not enabled with condition gating.");
            }

            if (ReadBool(sink, "_diagnosticModeNoSpeech"))
            {
                throw new InvalidOperationException($"StructuredTtsFeedbackSink on '{connector.gameObject.name}' is configured in no-speech diagnostic mode.");
            }

            if (!string.Equals(ReadString(sink, "_preferredCulture"), "es-ES", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"StructuredTtsFeedbackSink on '{connector.gameObject.name}' is not configured with preferredCulture=es-ES.");
            }

            int preSpeechSilenceMs = ReadInt(sink, "_preSpeechSilenceMs");
            if (preSpeechSilenceMs < 250 || preSpeechSilenceMs > 400)
            {
                throw new InvalidOperationException($"StructuredTtsFeedbackSink on '{connector.gameObject.name}' has preSpeechSilenceMs={preSpeechSilenceMs}, outside the expected P43C range.");
            }

            ValidateConditionGating(sink, experimentConditionProvider);
            validated++;
        }

        int missingScripts = CountMissingScripts();
        if (missingScripts > 0)
        {
            throw new InvalidOperationException($"Scene contains missing scripts: {missingScripts}");
        }

        Debug.Log($"[P43ATtsSceneIntegrationUtility] validation_ok connectors={connectors.Length} tts_sinks={validated} missing_scripts={missingScripts} recorder_assigned=1 c11_spoke=1 c10_spoke=0");
    }

    [MenuItem("Tools/TFG/P43A/Debug Real TTS Backend")]
    public static void DebugRealTtsBackendForBatch()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        StructuredTtsFeedbackSink sink = FindFirstSceneObject<StructuredTtsFeedbackSink>();
        if (sink == null)
        {
            throw new InvalidOperationException("No StructuredTtsFeedbackSink found in the target scene.");
        }

        sink.DebugSpeakTest();
        Debug.Log(
            $"[P43ATtsSceneIntegrationUtility] debug_real_tts_backend requested=1 " +
            $"backend_available={sink.IsBackendAvailable} mode='{sink.BackendMode}' reason='{sink.BackendUnavailableReason}'");
    }

    [MenuItem("Tools/TFG/P43B/List Installed TTS Voices")]
    public static void ListInstalledTtsVoicesForBatch()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        StructuredTtsFeedbackSink sink = RequireSingleTtsSink();
        sink.ListInstalledTtsVoices();
        Debug.Log($"[P43ATtsSceneIntegrationUtility] list_installed_tts_voices requested=1 backend_available={sink.IsBackendAvailable} mode='{sink.BackendMode}' reason='{sink.BackendUnavailableReason}'");
    }

    [MenuItem("Tools/TFG/P43B/Debug Speak Spanish Test")]
    public static void DebugSpeakSpanishTestForBatch()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        StructuredTtsFeedbackSink sink = RequireSingleTtsSink();
        sink.DebugSpeakSpanishTest();
        Debug.Log($"[P43ATtsSceneIntegrationUtility] debug_speak_spanish requested=1 backend_available={sink.IsBackendAvailable} mode='{sink.BackendMode}' reason='{sink.BackendUnavailableReason}'");
    }

    private static T[] FindSceneObjects<T>() where T : UnityObject
    {
        return UnityObject.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
    }

    private static T FindFirstSceneObject<T>() where T : UnityObject
    {
        T[] objects = FindSceneObjects<T>();
        return objects.Length > 0 ? objects[0] : null;
    }

    private static StructuredTtsFeedbackSink RequireSingleTtsSink()
    {
        StructuredTtsFeedbackSink[] sinks = FindSceneObjects<StructuredTtsFeedbackSink>();
        if (sinks.Length != 1)
        {
            throw new InvalidOperationException($"Expected exactly one StructuredTtsFeedbackSink, found {sinks.Length}.");
        }

        return sinks[0];
    }

    private static T ReadObjectReference<T>(UnityObject target, string propertyName) where T : UnityObject
    {
        SerializedObject serializedObject = new SerializedObject(target);
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        return property?.objectReferenceValue as T;
    }

    private static bool ReadBool(UnityObject target, string propertyName)
    {
        SerializedObject serializedObject = new SerializedObject(target);
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        return property != null && property.boolValue;
    }

    private static string ReadString(UnityObject target, string propertyName)
    {
        SerializedObject serializedObject = new SerializedObject(target);
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        return property != null ? property.stringValue : string.Empty;
    }

    private static int ReadInt(UnityObject target, string propertyName)
    {
        SerializedObject serializedObject = new SerializedObject(target);
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        return property != null ? property.intValue : 0;
    }

    private static void SetBool(SerializedObject serializedObject, string propertyName, bool value)
    {
        SerializedProperty property = RequireProperty(serializedObject, propertyName);
        property.boolValue = value;
    }

    private static void SetString(SerializedObject serializedObject, string propertyName, string value)
    {
        SerializedProperty property = RequireProperty(serializedObject, propertyName);
        property.stringValue = value;
    }

    private static void SetFloat(SerializedObject serializedObject, string propertyName, float value)
    {
        SerializedProperty property = RequireProperty(serializedObject, propertyName);
        property.floatValue = value;
    }

    private static void SetInt(SerializedObject serializedObject, string propertyName, int value)
    {
        SerializedProperty property = RequireProperty(serializedObject, propertyName);
        property.intValue = value;
    }

    private static void SetObject(SerializedObject serializedObject, string propertyName, UnityObject value)
    {
        SerializedProperty property = RequireProperty(serializedObject, propertyName);
        property.objectReferenceValue = value;
    }

    private static SerializedProperty RequireProperty(SerializedObject serializedObject, string propertyName)
    {
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        if (property == null)
        {
            throw new InvalidOperationException($"Missing serialized property '{propertyName}' on {serializedObject.targetObject}.");
        }

        return property;
    }

    private static int CountMissingScripts()
    {
        int missing = 0;
        foreach (GameObject gameObject in EnumerateSceneGameObjects())
        {
            missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject);
        }

        return missing;
    }

    private static void ValidateConditionGating(StructuredTtsFeedbackSink sink, ExperimentConditionConfigBehaviour conditionProvider)
    {
        CapturingTtsBackend backend = new CapturingTtsBackend();
        sink.ConfigureForTests(enabled: true, diagnosticModeNoSpeech: false, maxQueueSize: 4, duplicateCooldownSeconds: 0f, backend);

        conditionProvider.ApplyConditionFromOrchestrator(
            robotEnabled: true,
            voiceEnabled: false,
            conditionName: "C10_robot_on_voice_off",
            assistanceMode: RobotAssistanceMode.AssistedSelection,
            source: "p43a_scene_validation");
        sink.Emit(BuildAcceptanceMessage());
        sink.PumpForTests();
        if (backend.Spoken.Count != 0)
        {
            throw new InvalidOperationException("StructuredTtsFeedbackSink spoke while scene condition was C10 voice-off.");
        }

        conditionProvider.ApplyConditionFromOrchestrator(
            robotEnabled: true,
            voiceEnabled: true,
            conditionName: "C11_robot_on_voice_on",
            assistanceMode: RobotAssistanceMode.AssistedSelection,
            source: "p43a_scene_validation");
        sink.Emit(BuildAcceptanceMessage());
        sink.PumpForTests();
        if (backend.Spoken.Count != 1)
        {
            throw new InvalidOperationException("StructuredTtsFeedbackSink did not speak while scene condition was C11 voice-on.");
        }
    }

    private static RobotVoiceFeedbackMessage BuildAcceptanceMessage()
    {
        return new RobotVoiceFeedbackMessage(
            RobotVoiceFeedbackKind.OrderAccepted,
            string.Empty,
            new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.OrderAccepted,
                TargetAlias = "A1",
                Destination = "SELF"
            });
    }

    private sealed class CapturingTtsBackend : ITtsSpeechBackend
    {
        public readonly List<string> Spoken = new List<string>();

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

    private static IEnumerable<GameObject> EnumerateSceneGameObjects()
    {
        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (GameObject gameObject in EnumerateHierarchy(root))
            {
                yield return gameObject;
            }
        }
    }

    private static IEnumerable<GameObject> EnumerateHierarchy(GameObject root)
    {
        yield return root;
        foreach (Transform child in root.transform)
        {
            foreach (GameObject nested in EnumerateHierarchy(child.gameObject))
            {
                yield return nested;
            }
        }
    }
}
