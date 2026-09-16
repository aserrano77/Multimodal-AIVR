using Autonomy.Domain;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class StructuredTtsFeedbackSink : MonoBehaviour, IRobotVoiceFeedbackSink
    {
        private const string LogPrefix = "[StructuredTtsFeedbackSink]";

        [Header("TTS")]
        [SerializeField] private bool _enabled = true;
        [SerializeField] private TtsBackendSelection _backendSelection = TtsBackendSelection.Auto;
        [SerializeField] private bool _diagnosticModeNoSpeech;
        [SerializeField] private bool _onlyWhenVoiceEnabled = true;
        [SerializeField] private MonoBehaviour _experimentConditionProviderComponent;
        [SerializeField] private string _languageOrVoice = "es-ES";
        [SerializeField, Range(0f, 1f)] private float _volume = 1f;
        [SerializeField, Range(-10f, 10f)] private float _rate = 0f;

        [Header("Audio Clip Backend")]
        [SerializeField] private string _audioClipManifestResourcePath = AudioClipTtsSpeechBackend.DefaultManifestResourcePath;
        [SerializeField] private AudioSource _audioClipAudioSource;

        [Header("Voice Selection")]
        [SerializeField] private string _preferredCulture = "es-ES";
        [SerializeField] private string _preferredVoiceName = "";
        [SerializeField] private string _preferredVoiceGender = "";
        [SerializeField, Range(-10, 10)] private int _voiceRate = -1;
        [SerializeField, Range(0, 100)] private int _voiceVolume = 100;
        [SerializeField] private bool _logInstalledVoicesOnAwake = true;
        [SerializeField] private bool _requirePreferredCulture;

        [Header("Speech Timing")]
        [SerializeField, Range(0, 1000)] private int _preSpeechSilenceMs = 350;
        [SerializeField] private bool _useSsmlForPowershellSapi = true;

        [Header("Queue")]
        [SerializeField, Min(1)] private int _maxQueueSize = 4;
        [SerializeField, Min(0f)] private float _duplicateCooldownSeconds = 1.5f;

        [Header("Diagnostics")]
        [SerializeField] private bool _logDiagnostics = true;
        [SerializeField] private bool _debugSpeakTestNow;
        [SerializeField] private string _debugSpeakText = "Prueba de voz del robot.";
        [SerializeField] private bool _debugSpanishSpeakTestNow;
        [SerializeField] private string _debugSpanishSpeakText = "Prueba de voz del robot en español.";

        private readonly RobotVoiceFeedbackTtsFormatter _formatter = new();
        private TtsFeedbackQueue _queue;
        private ITtsSpeechBackend _backend;
        private bool _availabilityWarningLogged;

        public int PendingCount => _queue?.Count ?? 0;
        public bool IsBackendAvailable => _backend?.IsAvailable ?? false;
        public string BackendUnavailableReason => _backend?.UnavailableReason ?? "backend_not_initialized";
        public string BackendMode => ResolveBackendMode();

        private void Awake()
        {
            LogTtsEvent("tts_sink_awake", BuildBasePayload());
            EnsureInitialized();
        }

        private void Update()
        {
            EnsureInitialized();
            if (_debugSpeakTestNow)
            {
                _debugSpeakTestNow = false;
                DebugSpeakTest();
            }

            if (_debugSpanishSpeakTestNow)
            {
                _debugSpanishSpeakTestNow = false;
                DebugSpeakSpanishTest();
            }

            _backend?.Tick();
            if (!_enabled || _backend == null || !_backend.IsAvailable || _backend.IsSpeaking)
            {
                return;
            }

            if (_queue.TryDequeue(out TtsFeedbackUtterance utterance))
            {
                LogTtsEvent("tts_speak_requested", BuildUtterancePayload(utterance, "update_queue_dequeue"));
                LogBackendSpeechPreparation(utterance);
                if (!_backend.TrySpeak(utterance.Text, out string failureReason))
                {
                    LogTtsEvent("tts_speak_failed", BuildUtterancePayload(utterance, failureReason));
                    LogWarningOnce($"tts_speak_failed:{failureReason}");
                }
                else
                {
                    Dictionary<string, object> payload = BuildUtterancePayload(utterance, "speak_async_accepted");
                    payload["completion_state"] = "speak_async_accepted";
                    LogTtsEvent("tts_speak_completed", payload);
                }
            }
        }

        private void OnDisable()
        {
            LogTtsEvent("tts_sink_disabled", BuildBasePayload());
            _backend?.Stop();
            _queue?.Clear();
        }

        public void Emit(RobotVoiceFeedbackMessage message)
        {
            EnsureInitialized();
            LogTtsEvent("tts_feedback_received", BuildMessagePayload(message, "emit"));
            if (!_enabled)
            {
                LogTtsEvent("tts_message_dropped", BuildMessagePayload(message, "sink_disabled"));
                return;
            }

            if (message == null)
            {
                LogTtsEvent("tts_message_dropped", BuildMessagePayload(message, "message_null"));
                return;
            }

            ConditionGateEvaluation gate = EvaluateConditionGate();
            LogTtsEvent("tts_condition_gate_evaluated", BuildConditionGatePayload(gate));
            if (!gate.Allowed)
            {
                LogTtsEvent("tts_message_dropped", BuildMessagePayload(message, gate.Reason));
                LogDiagnostic($"tts_suppressed_by_condition | reason={gate.Reason}");
                return;
            }

            TtsFeedbackUtterance utterance = _formatter.Format(message);
            LogTtsEvent("tts_message_formatted", BuildUtterancePayload(utterance, message.Kind.ToString()));
            if (utterance.IsEmpty)
            {
                LogTtsEvent("tts_message_dropped", BuildUtterancePayload(utterance, "formatted_empty"));
                return;
            }

            if (_backend == null || !_backend.IsAvailable)
            {
                LogTtsEvent("tts_backend_unavailable", BuildUtterancePayload(utterance, BackendUnavailableReason));
                LogWarningOnce($"tts_unavailable:{BackendUnavailableReason}");
                return;
            }

            if (_queue.TryEnqueue(utterance, Time.unscaledTimeAsDouble, out string reason))
            {
                LogTtsEvent("tts_message_enqueued", BuildUtterancePayload(utterance, reason));
                LogDiagnostic($"tts_queued | reason={reason} | text='{utterance.Text}'");
            }
            else
            {
                LogTtsEvent("tts_message_dropped", BuildUtterancePayload(utterance, reason));
                LogDiagnostic($"tts_dropped | reason={reason} | text='{utterance.Text}'");
            }
        }

        public void ConfigureForTests(bool enabled, bool diagnosticModeNoSpeech, int maxQueueSize, float duplicateCooldownSeconds, ITtsSpeechBackend backend)
        {
            _enabled = enabled;
            _diagnosticModeNoSpeech = diagnosticModeNoSpeech;
            _maxQueueSize = Math.Max(1, maxQueueSize);
            _duplicateCooldownSeconds = Math.Max(0f, duplicateCooldownSeconds);
            _backend = backend;
            _queue = new TtsFeedbackQueue(_maxQueueSize, _duplicateCooldownSeconds);
            _backend?.Configure(_languageOrVoice, Mathf.Clamp01(_voiceVolume / 100f), _voiceRate);
            _availabilityWarningLogged = false;
            LogTtsEvent("tts_sink_configured", BuildBasePayload());
        }

        public void PumpForTests()
        {
            Update();
        }

        [ContextMenu("Debug Speak Test")]
        public void DebugSpeakTest()
        {
            string text = string.IsNullOrWhiteSpace(_debugSpeakText)
                ? "Prueba de voz del robot."
                : _debugSpeakText.Trim();
            SpeakDebugText(text, "debug_speak_test");
        }

        [ContextMenu("Debug Speak Spanish Test")]
        public void DebugSpeakSpanishTest()
        {
            string text = string.IsNullOrWhiteSpace(_debugSpanishSpeakText)
                ? "Prueba de voz del robot en español."
                : _debugSpanishSpeakText.Trim();
            SpeakDebugText(text, "debug_spanish_speak_test");
        }

        [ContextMenu("List Installed TTS Voices")]
        public void ListInstalledTtsVoices()
        {
            EnsureInitialized();
            if (_backend is WindowsSapiTtsSpeechBackend windowsBackend)
            {
                windowsBackend.LogInstalledVoicesForDiagnostics("manual_list_installed_voices");
                return;
            }

            Dictionary<string, object> payload = BuildBasePayload();
            payload["reason"] = "backend_does_not_support_voice_listing";
            LogTtsEvent("tts_voice_selection_failed", payload);
        }

        private void SpeakDebugText(string text, string source)
        {
            EnsureInitialized();
            TtsFeedbackUtterance utterance = new TtsFeedbackUtterance(text, TtsFeedbackPriority.Critical, source);
            LogTtsEvent("tts_debug_speak_test_requested", BuildUtterancePayload(utterance, "inspector_or_context_menu"));

            if (!_enabled)
            {
                LogTtsEvent("tts_debug_speak_test_failed", BuildUtterancePayload(utterance, "sink_disabled"));
                Debug.LogWarning($"{LogPrefix} debug_speak_test_failed:sink_disabled", this);
                return;
            }

            if (_backend == null || !_backend.IsAvailable)
            {
                LogTtsEvent("tts_backend_unavailable", BuildUtterancePayload(utterance, BackendUnavailableReason));
                LogTtsEvent("tts_debug_speak_test_failed", BuildUtterancePayload(utterance, BackendUnavailableReason));
                Debug.LogWarning($"{LogPrefix} debug_speak_test_failed:{BackendUnavailableReason}", this);
                return;
            }

            LogTtsEvent("tts_speak_requested", BuildUtterancePayload(utterance, source));
            LogBackendSpeechPreparation(utterance);
            if (_backend.TrySpeak(utterance.Text, out string failureReason))
            {
                Dictionary<string, object> payload = BuildUtterancePayload(utterance, "speak_async_accepted");
                payload["completion_state"] = "speak_async_accepted";
                LogTtsEvent("tts_speak_completed", payload);
            }
            else
            {
                LogTtsEvent("tts_speak_failed", BuildUtterancePayload(utterance, failureReason));
                Debug.LogWarning($"{LogPrefix} debug_speak_test_failed:{failureReason}", this);
            }
        }

        private void EnsureInitialized()
        {
            _queue ??= new TtsFeedbackQueue(_maxQueueSize, _duplicateCooldownSeconds);
            if (_backend == null)
            {
                LogTtsEvent("tts_backend_init_started", BuildBasePayload());
                _backend = CreateBackend();
                _backend.Configure(_languageOrVoice, Mathf.Clamp01(_voiceVolume / 100f), _voiceRate);
                Dictionary<string, object> payload = BuildBasePayload();
                payload["backend_available"] = _backend.IsAvailable;
                payload["backend_unavailable_reason"] = _backend.UnavailableReason;
                payload["backend_type"] = _backend.GetType().Name;
                LogTtsEvent(_backend.IsAvailable ? "tts_backend_init_succeeded" : "tts_backend_init_failed", payload);
                LogTtsEvent("tts_sink_configured", payload);
            }
        }

        public static ResolvedTtsBackendKind ResolveBackendKindForPlatform(
            TtsBackendSelection selection,
            bool diagnosticModeNoSpeech,
            bool isAndroidRuntime)
        {
            if (diagnosticModeNoSpeech || selection == TtsBackendSelection.DiagnosticNoSpeech)
            {
                return ResolvedTtsBackendKind.DiagnosticNoSpeech;
            }

            if (isAndroidRuntime)
            {
                return ResolvedTtsBackendKind.AudioClips;
            }

            if (selection == TtsBackendSelection.AudioClips)
            {
                return ResolvedTtsBackendKind.AudioClips;
            }

            if (selection == TtsBackendSelection.WindowsSapi)
            {
                return ResolvedTtsBackendKind.WindowsSapi;
            }

            return isAndroidRuntime
                ? ResolvedTtsBackendKind.AudioClips
                : ResolvedTtsBackendKind.WindowsSapi;
        }

        private ITtsSpeechBackend CreateBackend()
        {
            bool isAndroidRuntime = IsAndroidRuntime();
            var backendKind = ResolveBackendKindForPlatform(_backendSelection, _diagnosticModeNoSpeech, isAndroidRuntime);
            if (backendKind == ResolvedTtsBackendKind.AudioClips && _backendSelection == TtsBackendSelection.WindowsSapi && isAndroidRuntime)
            {
                LogTtsEvent(
                    "tts_backend_windows_sapi_ignored_on_android",
                    new Dictionary<string, object>
                    {
                        ["requested_backend"] = _backendSelection.ToString(),
                        ["resolved_backend"] = backendKind.ToString()
                    });
            }

            return backendKind switch
            {
                ResolvedTtsBackendKind.DiagnosticNoSpeech => new DiagnosticTtsSpeechBackend(),
                ResolvedTtsBackendKind.AudioClips => new AudioClipTtsSpeechBackend(
                    ResolveAudioClipAudioSource(),
                    _audioClipManifestResourcePath,
                    LogBackendTtsEvent),
                _ => new WindowsSapiTtsSpeechBackend(BuildVoiceSettings(), LogBackendTtsEvent)
            };
        }

        private static bool IsAndroidRuntime()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }

        private AudioSource ResolveAudioClipAudioSource()
        {
            if (_audioClipAudioSource != null)
            {
                _audioClipAudioSource.playOnAwake = false;
                return _audioClipAudioSource;
            }

            _audioClipAudioSource = GetComponent<AudioSource>();
            if (_audioClipAudioSource == null)
            {
                _audioClipAudioSource = gameObject.AddComponent<AudioSource>();
            }

            _audioClipAudioSource.playOnAwake = false;
            return _audioClipAudioSource;
        }

        private ConditionGateEvaluation EvaluateConditionGate()
        {
            if (!_onlyWhenVoiceEnabled)
            {
                return ConditionGateEvaluation.Allow("condition_gate_disabled", ResolveConditionProvider(), null);
            }

            IExperimentConditionProvider provider = ResolveConditionProvider();
            ExperimentConditionConfig condition = provider?.CurrentCondition;
            if (condition == null)
            {
                return ConditionGateEvaluation.Allow("condition_unavailable_allowing_tts", provider, null);
            }

            return condition.VoiceEnabled && condition.RobotEnabled
                ? ConditionGateEvaluation.Allow("condition_allows_tts", provider, condition)
                : ConditionGateEvaluation.Block("condition_blocks_tts", provider, condition);
        }

        private IExperimentConditionProvider ResolveConditionProvider()
        {
            if (_experimentConditionProviderComponent is IExperimentConditionProvider configuredProvider)
            {
                return configuredProvider;
            }

            return GetComponent<IExperimentConditionProvider>();
        }

        private void LogDiagnostic(string message)
        {
            if (_logDiagnostics)
            {
                Debug.Log($"{LogPrefix} {message}", this);
            }
        }

        private void LogWarningOnce(string message)
        {
            if (_availabilityWarningLogged)
            {
                return;
            }

            _availabilityWarningLogged = true;
            Debug.LogWarning($"{LogPrefix} {message}", this);
        }

        private void LogBackendTtsEvent(string eventType, Dictionary<string, object> payload)
        {
            Dictionary<string, object> enriched = BuildBasePayload();
            if (payload != null)
            {
                foreach (KeyValuePair<string, object> entry in payload)
                {
                    enriched[entry.Key] = entry.Value;
                }
            }

            LogTtsEvent(eventType, enriched);
        }

        private void LogTtsEvent(string eventType, Dictionary<string, object> payload)
        {
            payload ??= BuildBasePayload();
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
            if (_logDiagnostics)
            {
                string reason = payload.TryGetValue("reason", out object reasonValue) ? Convert.ToString(reasonValue) : string.Empty;
                string text = payload.TryGetValue("tts_text", out object textValue) ? Convert.ToString(textValue) : string.Empty;
                Debug.Log($"{LogPrefix} {eventType} | reason={reason} | text='{text}'", this);
            }
        }

        private Dictionary<string, object> BuildBasePayload()
        {
            return new Dictionary<string, object>
            {
                ["tts_sink"] = name,
                ["scene"] = SceneManager.GetActiveScene().name,
                ["enabled"] = _enabled,
                ["backend_selection"] = _backendSelection.ToString(),
                ["diagnostic_mode_no_speech"] = _diagnosticModeNoSpeech,
                ["only_when_voice_enabled"] = _onlyWhenVoiceEnabled,
                ["audio_clip_manifest_resource_path"] = _audioClipManifestResourcePath ?? string.Empty,
                ["language_or_voice"] = _languageOrVoice ?? string.Empty,
                ["volume"] = _volume,
                ["rate"] = _rate,
                ["preferred_culture"] = _preferredCulture ?? string.Empty,
                ["preferred_voice_name"] = _preferredVoiceName ?? string.Empty,
                ["preferred_voice_gender"] = _preferredVoiceGender ?? string.Empty,
                ["voice_rate"] = _voiceRate,
                ["voice_volume"] = _voiceVolume,
                ["log_installed_voices_on_awake"] = _logInstalledVoicesOnAwake,
                ["require_preferred_culture"] = _requirePreferredCulture,
                ["pre_speech_silence_ms"] = _preSpeechSilenceMs,
                ["use_ssml_for_powershell_sapi"] = _useSsmlForPowershellSapi,
                ["max_queue_size"] = _maxQueueSize,
                ["duplicate_cooldown_seconds"] = _duplicateCooldownSeconds,
                ["pending_count"] = PendingCount,
                ["backend_available"] = _backend?.IsAvailable ?? false,
                ["backend_unavailable_reason"] = _backend?.UnavailableReason ?? "backend_not_initialized",
                ["backend_type"] = _backend != null ? _backend.GetType().Name : "none",
                ["backend_mode"] = ResolveBackendMode()
            };
        }

        private string ResolveBackendMode()
        {
            if (_backend is WindowsSapiTtsSpeechBackend windowsBackend)
            {
                return windowsBackend.BackendMode;
            }

            if (_backend is DiagnosticTtsSpeechBackend)
            {
                return "diagnostic_no_speech";
            }

            if (_backend is AudioClipTtsSpeechBackend audioClipBackend)
            {
                return audioClipBackend.BackendMode;
            }

            return _backend != null ? _backend.GetType().Name : "none";
        }

        private TtsVoiceSettings BuildVoiceSettings()
        {
            return new TtsVoiceSettings(
                _preferredCulture,
                _preferredVoiceName,
                _preferredVoiceGender,
                _voiceRate,
                _voiceVolume,
                _logInstalledVoicesOnAwake,
                _requirePreferredCulture,
                _preSpeechSilenceMs,
                _useSsmlForPowershellSapi);
        }

        private void LogBackendSpeechPreparation(TtsFeedbackUtterance utterance)
        {
            if (_backend is not WindowsSapiTtsSpeechBackend windowsBackend)
            {
                return;
            }

            windowsBackend.LogSpeechPreparation(utterance.Reason);
        }

        private Dictionary<string, object> BuildMessagePayload(RobotVoiceFeedbackMessage message, string reason)
        {
            Dictionary<string, object> payload = BuildBasePayload();
            payload["reason"] = reason ?? string.Empty;
            payload["feedback_kind"] = message != null ? message.Kind.ToString() : string.Empty;
            payload["robot_feedback_reason"] = message != null ? message.Kind.ToString() : string.Empty;
            payload["feedback_text"] = message != null ? message.Text ?? string.Empty : string.Empty;
            RobotVoiceFeedbackContext context = message?.Context;
            payload["target_alias"] = context != null ? context.TargetAlias : string.Empty;
            payload["target_id"] = context != null ? context.TargetId : string.Empty;
            payload["destination"] = context != null ? context.Destination : string.Empty;
            payload["robot_task_state"] = context != null ? context.RobotTaskState : string.Empty;
            payload["accepted"] = context != null && context.Accepted;
            return payload;
        }

        private Dictionary<string, object> BuildUtterancePayload(TtsFeedbackUtterance utterance, string reason)
        {
            Dictionary<string, object> payload = BuildBasePayload();
            payload["reason"] = reason ?? string.Empty;
            payload["tts_text"] = utterance.Text ?? string.Empty;
            payload["tts_priority"] = utterance.Priority.ToString();
            payload["tts_source"] = utterance.Reason ?? string.Empty;
            payload["robot_feedback_reason"] = utterance.Reason ?? string.Empty;
            payload["tts_is_empty"] = utterance.IsEmpty;
            return payload;
        }

        private Dictionary<string, object> BuildConditionGatePayload(ConditionGateEvaluation gate)
        {
            Dictionary<string, object> payload = BuildBasePayload();
            payload["reason"] = gate.Reason;
            payload["tts_gate_allowed"] = gate.Allowed;
            payload["condition_provider"] = gate.Provider != null ? gate.Provider.GetType().Name : string.Empty;
            payload["condition_name"] = gate.Condition != null ? gate.Condition.ConditionName : string.Empty;
            payload["robot_enabled"] = gate.Condition != null && gate.Condition.RobotEnabled;
            payload["voice_enabled"] = gate.Condition != null && gate.Condition.VoiceEnabled;
            payload["assistance_mode"] = gate.Condition != null ? gate.Condition.AssistanceMode.ToString() : string.Empty;
            return payload;
        }

        private readonly struct ConditionGateEvaluation
        {
            private ConditionGateEvaluation(bool allowed, string reason, IExperimentConditionProvider provider, ExperimentConditionConfig condition)
            {
                Allowed = allowed;
                Reason = reason ?? string.Empty;
                Provider = provider;
                Condition = condition;
            }

            public bool Allowed { get; }
            public string Reason { get; }
            public IExperimentConditionProvider Provider { get; }
            public ExperimentConditionConfig Condition { get; }

            public static ConditionGateEvaluation Allow(string reason, IExperimentConditionProvider provider, ExperimentConditionConfig condition)
            {
                return new ConditionGateEvaluation(true, reason, provider, condition);
            }

            public static ConditionGateEvaluation Block(string reason, IExperimentConditionProvider provider, ExperimentConditionConfig condition)
            {
                return new ConditionGateEvaluation(false, reason, provider, condition);
            }
        }

        private sealed class DiagnosticTtsSpeechBackend : ITtsSpeechBackend
        {
            public bool IsAvailable => true;
            public bool IsSpeaking { get; private set; }
            public string UnavailableReason => string.Empty;

            public void Configure(string languageOrVoice, float volume, float rate)
            {
            }

            public bool TrySpeak(string text, out string failureReason)
            {
                failureReason = string.Empty;
                IsSpeaking = false;
                Debug.Log($"{LogPrefix} diagnostic_no_speech | {text}");
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

        private readonly struct TtsVoiceSettings
        {
            public TtsVoiceSettings(
                string preferredCulture,
                string preferredVoiceName,
                string preferredVoiceGender,
                int voiceRate,
                int voiceVolume,
                bool logInstalledVoicesOnAwake,
                bool requirePreferredCulture,
                int preSpeechSilenceMs,
                bool useSsmlForPowershellSapi)
            {
                PreferredCulture = string.IsNullOrWhiteSpace(preferredCulture) ? "es-ES" : preferredCulture.Trim();
                PreferredVoiceName = preferredVoiceName?.Trim() ?? string.Empty;
                PreferredVoiceGender = preferredVoiceGender?.Trim() ?? string.Empty;
                VoiceRate = Mathf.Clamp(voiceRate, -10, 10);
                VoiceVolume = Mathf.Clamp(voiceVolume, 0, 100);
                LogInstalledVoicesOnAwake = logInstalledVoicesOnAwake;
                RequirePreferredCulture = requirePreferredCulture;
                PreSpeechSilenceMs = Mathf.Clamp(preSpeechSilenceMs, 0, 1000);
                UseSsmlForPowershellSapi = useSsmlForPowershellSapi;
            }

            public string PreferredCulture { get; }
            public string PreferredVoiceName { get; }
            public string PreferredVoiceGender { get; }
            public int VoiceRate { get; }
            public int VoiceVolume { get; }
            public bool LogInstalledVoicesOnAwake { get; }
            public bool RequirePreferredCulture { get; }
            public int PreSpeechSilenceMs { get; }
            public bool UseSsmlForPowershellSapi { get; }
        }

        private sealed class WindowsSapiTtsSpeechBackend : ITtsSpeechBackend
        {
            private readonly TtsVoiceSettings _settings;
            private readonly Action<string, Dictionary<string, object>> _logEvent;
            private object _synthesizer;
            private Type _synthesizerType;
            private PropertyInfo _stateProperty;
            private MethodInfo _speakAsyncMethod;
            private MethodInfo _speakAsyncCancelAllMethod;
            private MethodInfo _selectVoiceMethod;
            private PropertyInfo _volumeProperty;
            private PropertyInfo _rateProperty;
            private object _comVoice;
            private Type _comVoiceType;
            private MethodInfo _comSpeakMethod;
            private MethodInfo _comWaitUntilDoneMethod;
            private PropertyInfo _comVolumeProperty;
            private PropertyInfo _comRateProperty;
            private string _powerShellPath;
            private System.Diagnostics.Process _powerShellProcess;
            private bool _powerShellOutputConsumed = true;
            private int _powerShellVolume = 100;
            private int _powerShellRate;
            private string _powerShellSelectedVoiceName = string.Empty;
            private string _powerShellSelectedVoiceCulture = string.Empty;
            private string _powerShellSelectionMode = "not_selected";
            private bool _powerShellVoiceSelectionFatal;
            private string _unavailableReason = "not_initialized";
            private string _backendMode = "none";

            public WindowsSapiTtsSpeechBackend(TtsVoiceSettings settings, Action<string, Dictionary<string, object>> logEvent)
            {
                _settings = settings;
                _logEvent = logEvent;
                TryInitialize();
            }

            public bool IsAvailable => _synthesizer != null || _comVoice != null || (!string.IsNullOrWhiteSpace(_powerShellPath) && !_powerShellVoiceSelectionFatal);
            public bool IsSpeaking
            {
                get
                {
                    if (_powerShellProcess != null)
                    {
                        try
                        {
                            return !_powerShellProcess.HasExited;
                        }
                        catch
                        {
                            return false;
                        }
                    }

                    if (_synthesizer != null)
                    {
                        return string.Equals(Convert.ToString(_stateProperty?.GetValue(_synthesizer)), "Speaking", StringComparison.OrdinalIgnoreCase);
                    }

                    if (_comVoice != null && _comWaitUntilDoneMethod != null)
                    {
                        try
                        {
                            object result = _comWaitUntilDoneMethod.Invoke(_comVoice, new object[] { 0 });
                            return result is bool completed && !completed;
                        }
                        catch
                        {
                            return false;
                        }
                    }

                    return false;
                }
            }
            public string UnavailableReason => IsAvailable ? string.Empty : _unavailableReason;
            public string BackendMode => _backendMode;

            public void LogSpeechPreparation(string source)
            {
                if (!string.Equals(_backendMode, "powershell_sapi", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (_settings.PreSpeechSilenceMs > 0)
                {
                    _logEvent?.Invoke(
                        "tts_prespeech_silence_applied",
                        new Dictionary<string, object>
                        {
                            ["pre_speech_silence_ms"] = _settings.PreSpeechSilenceMs,
                            ["backend_mode"] = _backendMode,
                            ["tts_source"] = source ?? string.Empty
                        });
                }

                if (_settings.UseSsmlForPowershellSapi)
                {
                    _logEvent?.Invoke(
                        "tts_ssml_speak_requested",
                        new Dictionary<string, object>
                        {
                            ["pre_speech_silence_ms"] = _settings.PreSpeechSilenceMs,
                            ["backend_mode"] = _backendMode,
                            ["tts_source"] = source ?? string.Empty
                        });
                }
            }

            public void Configure(string languageOrVoice, float volume, float rate)
            {
                if (!IsAvailable)
                {
                    return;
                }

                if (_comVoice != null)
                {
                    ConfigureComVoice(volume, rate);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_powerShellPath))
                {
                    _powerShellVolume = _settings.VoiceVolume;
                    _powerShellRate = _settings.VoiceRate;
                    ConfigurePowerShellVoiceSelection();
                    return;
                }

                try
                {
                    _volumeProperty?.SetValue(_synthesizer, _settings.VoiceVolume);
                    _rateProperty?.SetValue(_synthesizer, _settings.VoiceRate);
                    string voiceName = !string.IsNullOrWhiteSpace(_settings.PreferredVoiceName)
                        ? _settings.PreferredVoiceName
                        : languageOrVoice;
                    if (!string.IsNullOrWhiteSpace(voiceName) && _selectVoiceMethod != null)
                    {
                        try
                        {
                            _selectVoiceMethod.Invoke(_synthesizer, new object[] { voiceName });
                        }
                        catch
                        {
                            // Keep the platform default voice if the requested voice is not installed.
                        }
                    }
                }
                catch (Exception ex)
                {
                    _unavailableReason = $"configure_failed:{ex.GetType().Name}";
                }
            }

            public bool TrySpeak(string text, out string failureReason)
            {
                failureReason = string.Empty;
                if (!IsAvailable)
                {
                    failureReason = _unavailableReason;
                    return false;
                }

                try
                {
                    if (_comVoice != null)
                    {
                        _comSpeakMethod.Invoke(_comVoice, new object[] { text, 1 });
                    }
                    else if (!string.IsNullOrWhiteSpace(_powerShellPath))
                    {
                        StartPowerShellSpeech(text);
                    }
                    else
                    {
                        _speakAsyncMethod.Invoke(_synthesizer, new object[] { text });
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    failureReason = $"speak_async_failed:{_backendMode}:{ex.GetType().Name}:{ex.Message}";
                    return false;
                }
            }

            public void Tick()
            {
                ConsumePowerShellOutputIfCompleted();
            }

            public void Stop()
            {
                if (!IsAvailable)
                {
                    return;
                }

                try
                {
                    if (_comVoice != null)
                    {
                        _comSpeakMethod?.Invoke(_comVoice, new object[] { string.Empty, 3 });
                    }
                    else if (_powerShellProcess != null && !_powerShellProcess.HasExited)
                    {
                        _powerShellProcess.Kill();
                        ConsumePowerShellOutputIfCompleted();
                        _powerShellProcess.Dispose();
                        _powerShellProcess = null;
                    }
                    else
                    {
                        _speakAsyncCancelAllMethod?.Invoke(_synthesizer, null);
                    }
                }
                catch
                {
                }
            }

            private void TryInitialize()
            {
                string systemSpeechReason = string.Empty;
                try
                {
                    _synthesizerType = Type.GetType("System.Speech.Synthesis.SpeechSynthesizer, System.Speech");
                    if (_synthesizerType == null)
                    {
                        systemSpeechReason = "system_speech_not_available";
                    }
                    else
                    {
                        _synthesizer = Activator.CreateInstance(_synthesizerType);
                        _stateProperty = _synthesizerType.GetProperty("State");
                        _speakAsyncMethod = _synthesizerType.GetMethod("SpeakAsync", new[] { typeof(string) });
                        _speakAsyncCancelAllMethod = _synthesizerType.GetMethod("SpeakAsyncCancelAll", Type.EmptyTypes);
                        _selectVoiceMethod = _synthesizerType.GetMethod("SelectVoice", new[] { typeof(string) });
                        _volumeProperty = _synthesizerType.GetProperty("Volume");
                        _rateProperty = _synthesizerType.GetProperty("Rate");
                        if (_speakAsyncMethod == null)
                        {
                            _synthesizer = null;
                            systemSpeechReason = "speak_async_method_missing";
                        }
                        else
                        {
                            _backendMode = "system_speech";
                            _unavailableReason = string.Empty;
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _synthesizer = null;
                    systemSpeechReason = $"system_speech_init_failed:{ex.GetType().Name}:{ex.Message}";
                }

                if (TryInitializeComSapi(out string comReason))
                {
                    _backendMode = "com_sapi";
                    _unavailableReason = string.Empty;
                    return;
                }

                if (TryInitializePowerShellSapi(out string powerShellReason))
                {
                    _backendMode = "powershell_sapi";
                    _unavailableReason = string.Empty;
                    return;
                }

                _backendMode = "unavailable";
                _unavailableReason = string.IsNullOrWhiteSpace(systemSpeechReason)
                    ? $"{comReason};{powerShellReason}"
                    : $"{systemSpeechReason};{comReason};{powerShellReason}";
            }

            private bool TryInitializeComSapi(out string failureReason)
            {
                failureReason = string.Empty;
                try
                {
                    _comVoiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
                    if (_comVoiceType == null)
                    {
                        failureReason = "com_sapi_prog_id_not_available";
                        return false;
                    }

                    _comVoice = Activator.CreateInstance(_comVoiceType);
                    _comSpeakMethod = _comVoiceType.GetMethod("Speak", new[] { typeof(string), typeof(int) });
                    _comWaitUntilDoneMethod = _comVoiceType.GetMethod("WaitUntilDone", new[] { typeof(int) });
                    _comVolumeProperty = _comVoiceType.GetProperty("Volume");
                    _comRateProperty = _comVoiceType.GetProperty("Rate");
                    if (_comSpeakMethod == null)
                    {
                        _comVoice = null;
                        failureReason = "com_sapi_speak_method_missing";
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _comVoice = null;
                    failureReason = $"com_sapi_init_failed:{ex.GetType().Name}:{ex.Message}";
                    return false;
                }
            }

            private bool TryInitializePowerShellSapi(out string failureReason)
            {
                failureReason = string.Empty;
                string systemPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string candidate = Path.Combine(systemPath, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(candidate))
                {
                    candidate = "powershell.exe";
                }

                _powerShellPath = candidate;
                return true;
            }

            public void LogInstalledVoicesForDiagnostics(string reason)
            {
                if (string.IsNullOrWhiteSpace(_powerShellPath))
                {
                    _logEvent?.Invoke(
                        "tts_voice_selection_failed",
                        new Dictionary<string, object>
                        {
                            ["reason"] = "powershell_sapi_not_available",
                            ["diagnostic_reason"] = reason ?? string.Empty
                        });
                    return;
                }

                RunPowerShellVoiceDiagnostics(reason ?? "manual");
            }

            private void ConfigurePowerShellVoiceSelection()
            {
                RunPowerShellVoiceDiagnostics("configure");
            }

            private void RunPowerShellVoiceDiagnostics(string reason)
            {
                _logEvent?.Invoke(
                    "tts_voice_selection_started",
                    new Dictionary<string, object>
                    {
                        ["reason"] = reason ?? string.Empty,
                        ["preferred_culture"] = _settings.PreferredCulture,
                        ["preferred_voice_name"] = _settings.PreferredVoiceName,
                        ["preferred_voice_gender"] = _settings.PreferredVoiceGender
                    });

                string script = BuildPowerShellVoiceDiagnosticScript(reason);
                if (!RunPowerShellCapture(script, 7000, out List<string> lines, out string failureReason))
                {
                    _logEvent?.Invoke(
                        "tts_voice_selection_failed",
                        new Dictionary<string, object>
                        {
                            ["reason"] = failureReason,
                            ["preferred_culture"] = _settings.PreferredCulture,
                            ["preferred_voice_name"] = _settings.PreferredVoiceName
                        });
                    return;
                }

                ParsePowerShellVoiceDiagnosticLines(lines);
            }

            private void ParsePowerShellVoiceDiagnosticLines(List<string> lines)
            {
                foreach (string rawLine in lines)
                {
                    if (string.IsNullOrWhiteSpace(rawLine))
                    {
                        continue;
                    }

                    string[] parts = rawLine.Split('|');
                    string recordType = parts.Length > 0 ? parts[0] : string.Empty;
                    switch (recordType)
                    {
                        case "VOICE":
                            if (parts.Length >= 6)
                            {
                                _logEvent?.Invoke(
                                    "tts_installed_voice_detected",
                                    new Dictionary<string, object>
                                    {
                                        ["voice_name"] = parts[1],
                                        ["culture"] = parts[2],
                                        ["gender"] = parts[3],
                                        ["age"] = parts[4],
                                        ["enabled"] = parts[5]
                                    });
                            }
                            break;
                        case "SELECTED":
                            if (parts.Length >= 6)
                            {
                                _powerShellSelectedVoiceName = parts[1];
                                _powerShellSelectedVoiceCulture = parts[2];
                                _powerShellSelectionMode = parts[5];
                                _powerShellVoiceSelectionFatal = false;
                                _logEvent?.Invoke(
                                    "tts_voice_selected",
                                    new Dictionary<string, object>
                                    {
                                        ["voice_name"] = _powerShellSelectedVoiceName,
                                        ["selected_voice_name"] = _powerShellSelectedVoiceName,
                                        ["culture"] = _powerShellSelectedVoiceCulture,
                                        ["gender"] = parts[3],
                                        ["age"] = parts[4],
                                        ["selection_mode"] = _powerShellSelectionMode,
                                        ["selection_reason"] = _powerShellSelectionMode
                                    });
                            }
                            break;
                        case "DEFAULT_FALLBACK":
                            if (parts.Length >= 5)
                            {
                                _logEvent?.Invoke(
                                    "tts_voice_default_fallback_used",
                                    new Dictionary<string, object>
                                    {
                                        ["voice_name"] = parts[1],
                                        ["selected_voice_name"] = parts[1],
                                        ["culture"] = parts[2],
                                        ["gender"] = parts[3],
                                        ["age"] = parts[4],
                                        ["preferred_culture"] = _settings.PreferredCulture,
                                        ["preferred_voice_name"] = _settings.PreferredVoiceName,
                                        ["preferred_voice_gender"] = _settings.PreferredVoiceGender,
                                        ["reason"] = parts.Length >= 6 ? parts[5] : "no_matching_voice"
                                    });
                            }
                            break;
                        case "SELECT_FAILED":
                            string reason = parts.Length >= 2 ? parts[1] : "voice_selection_failed";
                            Dictionary<string, object> failurePayload = new Dictionary<string, object>
                            {
                                ["reason"] = reason,
                                ["preferred_culture"] = _settings.PreferredCulture,
                                ["preferred_voice_name"] = _settings.PreferredVoiceName,
                                ["preferred_voice_gender"] = _settings.PreferredVoiceGender
                            };
                            _logEvent?.Invoke(
                                "tts_voice_selection_failed",
                                failurePayload);
                            if (string.Equals(reason, "preferred_voice_name_not_installed", StringComparison.OrdinalIgnoreCase))
                            {
                                _logEvent?.Invoke("tts_preferred_voice_not_found", failurePayload);
                            }
                            if (_settings.RequirePreferredCulture)
                            {
                                _powerShellVoiceSelectionFatal = true;
                                _unavailableReason = reason;
                            }
                            break;
                    }
                }
            }

            private string BuildPowerShellVoiceDiagnosticScript(string reason)
            {
                string preferredCulture = ToPowerShellSingleQuotedString(_settings.PreferredCulture);
                string preferredVoiceName = ToPowerShellSingleQuotedString(_settings.PreferredVoiceName);
                string preferredGender = ToPowerShellSingleQuotedString(_settings.PreferredVoiceGender);
                string requireCulture = _settings.RequirePreferredCulture ? "$true" : "$false";
                string emitVoices = _settings.LogInstalledVoicesOnAwake || !string.Equals(reason, "configure", StringComparison.OrdinalIgnoreCase)
                    ? "foreach ($v in $allVoices) { $i = $v.VoiceInfo; Emit 'VOICE' $i.Name $i.Culture.Name $i.Gender $i.Age $v.Enabled; }; "
                    : string.Empty;

                return
                    "Add-Type -AssemblyName System.Speech; " +
                    "function Clean($v) { if ($null -eq $v) { return '' }; return (($v.ToString()) -replace '\\|','/'); }; " +
                    "function Emit($a,$b,$c,$d,$e,$f) { Write-Output ((Clean $a)+'|'+(Clean $b)+'|'+(Clean $c)+'|'+(Clean $d)+'|'+(Clean $e)+'|'+(Clean $f)); }; " +
                    "$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                    $"$preferredCulture = {preferredCulture}; " +
                    $"$preferredVoiceName = {preferredVoiceName}; " +
                    $"$preferredGender = {preferredGender}; " +
                    $"$requireCulture = {requireCulture}; " +
                    "$allVoices = @($synth.GetInstalledVoices() | Sort-Object { $_.VoiceInfo.Name }); " +
                    "$voices = @($synth.GetInstalledVoices() | Where-Object { $_.Enabled } | Sort-Object { $_.VoiceInfo.Name }); " +
                    emitVoices +
                    "$selected = $null; $mode = ''; " +
                    "if (![string]::IsNullOrWhiteSpace($preferredVoiceName)) { " +
                    "  $selectedList = @($voices | Where-Object { $_.VoiceInfo.Name -eq $preferredVoiceName } | Select-Object -First 1); " +
                    "  if ($selectedList.Count -gt 0) { $selected = $selectedList[0]; $mode = 'exact_name'; } else { Emit 'SELECT_FAILED' 'preferred_voice_name_not_installed' $preferredVoiceName '' ''; } " +
                    "} " +
                    "if ($null -eq $selected -and ![string]::IsNullOrWhiteSpace($preferredCulture)) { " +
                    "  try { " +
                    "    $culture = [System.Globalization.CultureInfo]::GetCultureInfo($preferredCulture); " +
                    "    $candidates = @($synth.GetInstalledVoices($culture) | Where-Object { $_.Enabled } | Sort-Object { $_.VoiceInfo.Name }); " +
                    "    if (![string]::IsNullOrWhiteSpace($preferredGender)) { $filtered = @($candidates | Where-Object { $_.VoiceInfo.Gender.ToString() -eq $preferredGender }); if ($filtered.Count -gt 0) { $candidates = $filtered; $mode = 'culture_gender_match'; } else { Emit 'SELECT_FAILED' 'preferred_gender_voice_not_installed' $preferredCulture $preferredGender ''; } } " +
                    "    if ($candidates.Count -gt 0) { $selected = $candidates[0]; if ([string]::IsNullOrWhiteSpace($mode)) { $mode = 'culture_match'; } } else { Emit 'SELECT_FAILED' 'preferred_culture_voice_not_installed' $preferredCulture '' ''; } " +
                    "  } catch { Emit 'SELECT_FAILED' ('preferred_culture_invalid:' + $_.Exception.GetType().Name) $preferredCulture '' ''; } " +
                    "} " +
                    "if ($null -ne $selected) { $i = $selected.VoiceInfo; Emit 'SELECTED' $i.Name $i.Culture.Name $i.Gender $i.Age $mode; } " +
                    "else { " +
                    "  if ($requireCulture) { Emit 'SELECT_FAILED' 'preferred_culture_required_but_missing' $preferredCulture '' ''; exit 20; } " +
                    "  $i = $synth.Voice; Emit 'DEFAULT_FALLBACK' $i.Name $i.Culture.Name $i.Gender $i.Age 'no_matching_preferred_voice'; Emit 'SELECTED' $i.Name $i.Culture.Name $i.Gender $i.Age 'default_fallback'; " +
                    "}";
            }

            private bool RunPowerShellCapture(string command, int timeoutMilliseconds, out List<string> lines, out string failureReason)
            {
                lines = new List<string>();
                failureReason = string.Empty;
                try
                {
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _powerShellPath,
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo);
                    if (process == null)
                    {
                        failureReason = "powershell_process_not_started";
                        return false;
                    }

                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        failureReason = "powershell_voice_diagnostics_timeout";
                        return false;
                    }

                    while (!process.StandardOutput.EndOfStream)
                    {
                        lines.Add(process.StandardOutput.ReadLine());
                    }

                    string error = process.StandardError.ReadToEnd();
                    if (process.ExitCode != 0 && _settings.RequirePreferredCulture)
                    {
                        failureReason = string.IsNullOrWhiteSpace(error)
                            ? $"powershell_voice_diagnostics_exit_{process.ExitCode}"
                            : $"powershell_voice_diagnostics_exit_{process.ExitCode}:{error.Trim()}";
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        _logEvent?.Invoke(
                            "tts_voice_selection_failed",
                            new Dictionary<string, object>
                            {
                                ["reason"] = $"powershell_voice_diagnostics_stderr:{error.Trim()}"
                            });
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    failureReason = $"powershell_voice_diagnostics_failed:{ex.GetType().Name}:{ex.Message}";
                    return false;
                }
            }

            private string BuildPowerShellSelectVoiceStatement()
            {
                if (string.IsNullOrWhiteSpace(_powerShellSelectedVoiceName) ||
                    string.Equals(_powerShellSelectionMode, "default_fallback", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return $"try {{ $synth.SelectVoice({ToPowerShellSingleQuotedString(_powerShellSelectedVoiceName)}); }} catch {{ }} ";
            }

            private void StartPowerShellSpeech(string text)
            {
                string escapedText = ToPowerShellSingleQuotedString(text);
                string escapedSsml = ToPowerShellSingleQuotedString(BuildSsml(text));
                string useSsml = _settings.UseSsmlForPowershellSapi ? "$true" : "$false";
                string command =
                    "Add-Type -AssemblyName System.Speech; " +
                    "$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                    BuildPowerShellSelectVoiceStatement() +
                    $"$synth.Volume = {_powerShellVolume}; " +
                    $"$synth.Rate = {_powerShellRate}; " +
                    $"$useSsml = {useSsml}; " +
                    $"$ssml = {escapedSsml}; " +
                    $"$plainText = {escapedText}; " +
                    "if ($useSsml) { " +
                    "  try { $synth.SpeakSsml($ssml); } " +
                    "  catch { Write-Output ('SSML_FALLBACK|' + ($_.Exception.GetType().Name -replace '\\|','/') + '|' + ($_.Exception.Message -replace '\\|','/')); $synth.Speak($plainText); } " +
                    "} else { " +
                    $"  Start-Sleep -Milliseconds {_settings.PreSpeechSilenceMs}; $synth.Speak($plainText); " +
                    "}";

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _powerShellPath,
                    Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{command}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _powerShellProcess?.Dispose();
                _powerShellProcess = System.Diagnostics.Process.Start(startInfo);
                _powerShellOutputConsumed = false;
            }

            private string BuildSsml(string text)
            {
                string culture = string.IsNullOrWhiteSpace(_powerShellSelectedVoiceCulture)
                    ? _settings.PreferredCulture
                    : _powerShellSelectedVoiceCulture;
                string escapedCulture = EscapeXmlAttribute(culture);
                string escapedText = EscapeXmlText(text);
                return
                    $"<speak version='1.0' xml:lang='{escapedCulture}' xmlns='http://www.w3.org/2001/10/synthesis'>" +
                    $"<break time='{_settings.PreSpeechSilenceMs}ms'/>" +
                    escapedText +
                    "</speak>";
            }

            private void ConsumePowerShellOutputIfCompleted()
            {
                if (_powerShellProcess == null || _powerShellOutputConsumed)
                {
                    return;
                }

                try
                {
                    if (!_powerShellProcess.HasExited)
                    {
                        return;
                    }

                    _powerShellOutputConsumed = true;
                    string output = _powerShellProcess.StandardOutput.ReadToEnd();
                    string error = _powerShellProcess.StandardError.ReadToEnd();
                    ParsePowerShellSpeechOutput(output, error);
                }
                catch (Exception ex)
                {
                    _powerShellOutputConsumed = true;
                    _logEvent?.Invoke(
                        "tts_speak_failed",
                        new Dictionary<string, object>
                        {
                            ["reason"] = $"powershell_speech_output_read_failed:{ex.GetType().Name}:{ex.Message}",
                            ["backend_mode"] = _backendMode
                        });
                }
            }

            private void ParsePowerShellSpeechOutput(string output, string error)
            {
                if (!string.IsNullOrWhiteSpace(output))
                {
                    string[] lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("SSML_FALLBACK|", StringComparison.OrdinalIgnoreCase))
                        {
                            string[] parts = line.Split('|');
                            _logEvent?.Invoke(
                                "tts_ssml_fallback_used",
                                new Dictionary<string, object>
                                {
                                    ["reason"] = parts.Length >= 2 ? parts[1] : "ssml_speak_failed",
                                    ["detail"] = parts.Length >= 3 ? parts[2] : string.Empty,
                                    ["backend_mode"] = _backendMode
                                });
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logEvent?.Invoke(
                        "tts_speak_failed",
                        new Dictionary<string, object>
                        {
                            ["reason"] = $"powershell_speech_stderr:{error.Trim()}",
                            ["backend_mode"] = _backendMode
                        });
                }
            }

            private static string ToPowerShellSingleQuotedString(string value)
            {
                return $"'{(value ?? string.Empty).Replace("'", "''")}'";
            }

            private static string EscapeXmlText(string value)
            {
                return (value ?? string.Empty)
                    .Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;");
            }

            private static string EscapeXmlAttribute(string value)
            {
                return EscapeXmlText(value)
                    .Replace("\"", "&quot;")
                    .Replace("'", "&apos;");
            }

            private void ConfigureComVoice(float volume, float rate)
            {
                try
                {
                    _comVolumeProperty?.SetValue(_comVoice, Mathf.Clamp(Mathf.RoundToInt(volume * 100f), 0, 100));
                    _comRateProperty?.SetValue(_comVoice, Mathf.Clamp(Mathf.RoundToInt(rate), -10, 10));
                }
                catch (Exception ex)
                {
                    _unavailableReason = $"com_sapi_configure_failed:{ex.GetType().Name}:{ex.Message}";
                }
            }
        }
    }
}
