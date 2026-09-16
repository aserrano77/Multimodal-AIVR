using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Autonomy.Domain;
using Autonomy.Services;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Debug = UnityEngine.Debug;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)]
    public sealed class VoiceRecognitionController : MonoBehaviour
    {
        private const string LogPrefix = "[VoiceRecognitionController]";
        private const float AndroidWhisperBaseReadinessTimeoutSeconds = 90f;
        private const float WhisperReadinessPollIntervalSeconds = 0.25f;

        [Header("ASR")]
        [SerializeField] private ASRBackend _backend = ASRBackend.Auto;
        [SerializeField] private Object _whisperManager;
        [SerializeField] private string _language = "es";
        [SerializeField] private string _microphoneDevice = "";
        [SerializeField] private int _sampleRate = 16000;
        [SerializeField] private float _maxCaptureSeconds = 4f;

        [Header("Sherpa ONNX Prototype")]
        [SerializeField] private SherpaOnnxBackendOptions _sherpaOnnxOptions = new();

        [Header("Listening Mode")]
        [SerializeField] private VoiceListeningMode _listeningMode = VoiceListeningMode.PushToTalk;

        [Header("Continuous VAD")]
        [SerializeField] private float _vadVoiceRmsThreshold = 0.015f;
        [SerializeField] private float _vadMinVoiceDurationToOpenSeconds = 0.18f;
        [SerializeField] private float _vadSilenceDurationToCloseSeconds = 0.65f;
        [SerializeField] private float _vadMinUtteranceSeconds = 0.45f;
        [SerializeField] private float _vadMaxUtteranceSeconds = 4f;
        [SerializeField] private float _vadCooldownSeconds = 1.25f;
        [SerializeField] private float _vadRmsWindowSeconds = 0.06f;
        [SerializeField] private float _vadPreRollSeconds = 0.20f;
        [SerializeField] private float _vadShortCommandMinUtteranceSeconds = 1.05f;
        [SerializeField] private float _vadShortCommandCandidateMaxSeconds = 1.70f;
        [SerializeField] private string _optionalSoftWakePrefix = "";

        [Header("Diagnostics")]
        [SerializeField] private bool _logMicrophoneDevicesOnStart = true;
        [SerializeField] private bool _asrDebugLogging = true;
        [SerializeField] private bool _saveLastCapturedClipToWav = false;
        [SerializeField] private float _silenceRmsThreshold = 0.005f;
        [SerializeField] private bool _enableCommandNormalizationDiagnostics = false;
        [SerializeField] private bool _enableAsrFrameHitchWatchdog = true;
        [SerializeField] private float _frameHitchWarningThresholdMs = 80f;
        [SerializeField] private float _frameHitchCriticalThresholdMs = 150f;

        [Header("ASR Diagnostic Mode")]
        [SerializeField] private bool _enableAsrDiagnosticMode;
        [SerializeField] private AsrDiagnosticRecorder _asrDiagnosticRecorder;
        [SerializeField] private bool _diagnosticSaveUtteranceWavs = true;
        [SerializeField] private bool _diagnosticOverrideWhisperModel;
        [SerializeField] private ASRModelSize _diagnosticModelSize = ASRModelSize.Unknown;
        [SerializeField] private string _conditionId = "";
        [SerializeField] private string _roundId = "";

        [Header("Autonomy Command Routing")]
        [SerializeField] private VoiceAutonomyCommandConnector _autonomyCommandConnector;

        [Header("Voice Experiment Logging")]
        [SerializeField] private bool _enableVoiceExperimentLogging = true;

        [Header("Test Control")]
        [SerializeField] private KeyCode _pushToTalkKey = KeyCode.V;
        [SerializeField] private bool _holdKeyToTalk = true;
        [SerializeField] private string _manualTranscript = "recoge la caja A";

        [Header("Inspector Debug Submit")]
        [SerializeField] private bool _debugSubmitManualTranscriptNow;
        [SerializeField] private bool _debugSubmitConfirmationYesNow;
        [SerializeField] private bool _debugSubmitConfirmationNoNow;

        [Header("Optional Experiment Metadata")]
        [SerializeField] private string _participantId = "";
        [SerializeField] private string _sessionId = "";
        [SerializeField] private string _trialId = "";

        private UnityMicrophoneASRService _microphoneService;
        private IASRService _service;
        private IVoiceCommandNormalizer _commandNormalizer;
        private IVoiceCommandIntentMapper _diagnosticIntentMapper;
        private VoiceVadSegmenter _vadSegmenter;
        private bool _operationInProgress;
        private float _listeningStartedAt;
        private AudioClip _continuousVadClip;
        private string _continuousVadStartDevice;
        private bool _continuousVadCaptureActive;
        private bool _continuousVadTranscriptionInProgress;
        private bool _manualStubListening;
        private bool _manualStubUpdateBranchLogged;
        private bool _manualStubNoFocusLogged;
        private int _voiceInteractionSequence;
        private string _activeVoiceInteractionId = "";
        private int _activeVoiceInteractionPauseEpoch;
        private bool _activeVoiceInteractionStartedWhilePaused;
        private bool _vadBelowThresholdLogged;
        private Coroutine _whisperReadinessCoroutine;
        private bool _whisperReadinessTimedOut;
        private bool _continuousVadWaitingForWhisperReadiness;
        private bool _whisperBaseLoadAttempted;
        private bool _shortCommandVadExtensionLogged;
        private bool _asrFrameHitchWatchdogActive;
        private string _asrFrameHitchWatchdogUtteranceId = "";
        private float _asrFrameHitchWatchdogStartedAt;
        private float _asrFrameHitchMaxDeltaMs;
        private int _asrFrameHitchWarningFrames;
        private int _asrFrameHitchCriticalFrames;
        private ExperimentVoicePipelineBootstrap _voiceBootstrap;
        private bool _voicePipelineActivated;
        private bool _firstVoiceCommandReceivedLogged;
        private bool _firstVoiceCommandOutcomeLogged;
        private int _visibleTrialNumber;
        private int _roundNumber;

        public bool VoicePipelineActivated => _voicePipelineActivated;
        public bool MicrophoneCaptureActive => _continuousVadCaptureActive;

        private void Awake()
        {
            _voiceBootstrap = ExperimentVoicePipelineBootstrap.EnsureExists();
            _commandNormalizer = new VoiceCommandNormalizer();
            _diagnosticIntentMapper = new VoiceCommandIntentMapper();
            TryResolveAutonomyCommandConnector(createIfMissing: true);
            TryResolveAsrDiagnosticRecorder(createIfMissing: _enableAsrDiagnosticMode);
            ApplyAndroidQuestLoggingDefaults();
            ConfigureDiagnosticWhisperModelIfRequested();
            EnsureWhisperModelPathResolvedForRuntime();
            _microphoneService = new UnityMicrophoneASRService(
                _whisperManager,
                () => _manualTranscript,
                new ASRDebugOptions
                {
                    DebugLogging = _asrDebugLogging,
                    SaveLastCapturedClipToWav = _saveLastCapturedClipToWav ||
                        (_enableAsrDiagnosticMode && _diagnosticSaveUtteranceWavs),
                    SilenceRmsThreshold = Mathf.Max(0f, _silenceRmsThreshold),
                    Log = message => Debug.Log($"{LogPrefix} {message}", this),
                    Warn = message => Debug.LogWarning($"{LogPrefix} {message}", this)
                },
                _sherpaOnnxOptions,
                () => _conditionId,
                _voiceBootstrap != null ? _voiceBootstrap.SharedBackend : null);
            _service = _microphoneService;
            RebuildVadSegmenter();
        }

        private void Start()
        {
            TryResolveAutonomyCommandConnector(createIfMissing: false);
            Debug.Log(
                $"{LogPrefix} Runtime config | backend={_backend} sherpa_model='{_sherpaOnnxOptions?.ModelName}' listeningMode={_listeningMode} pushToTalkKey={_pushToTalkKey} holdKeyToTalk={_holdKeyToTalk} manualTranscript='{_manualTranscript}' autonomyCommandConnector assigned={_autonomyCommandConnector != null} connectorGameObject='{(_autonomyCommandConnector != null ? _autonomyCommandConnector.gameObject.name : string.Empty)}'",
                this);

            if (_logMicrophoneDevicesOnStart)
            {
                LogMicrophoneDevices();
            }

            LogWhisperRuntimeModel("controller_start", _backend);

            _voiceBootstrap?.EnsurePreparationStarted();
            _voiceBootstrap?.ReportRuntimeState(false, false, false);
        }

        private void Update()
        {
            UpdateAsrFrameHitchWatchdog();

            if (HandleInspectorDebugSubmitFlags())
            {
                return;
            }

            if (!_voicePipelineActivated)
            {
                return;
            }

            if (_listeningMode == VoiceListeningMode.ContinuousVad)
            {
                if (!_voicePipelineActivated)
                {
                    StopContinuousVadCapture();
                    return;
                }

                UpdateContinuousVad();
                return;
            }

            if (_operationInProgress)
            {
                return;
            }

            if (_backend == ASRBackend.ManualStub || _backend == ASRBackend.DiagnosticStub)
            {
                if (!_manualStubUpdateBranchLogged)
                {
                    _manualStubUpdateBranchLogged = true;
                    Debug.Log($"{LogPrefix} ManualStub update branch active | holdKeyToTalk={_holdKeyToTalk} pushToTalkKey={_pushToTalkKey}", this);
                }

                if (!Application.isFocused && !_manualStubNoFocusLogged)
                {
                    _manualStubNoFocusLogged = true;
                    Debug.Log($"{LogPrefix} ManualStub ignored_no_focus_or_no_key_event | applicationFocused=false", this);
                }

                UpdateManualStubPushToTalk();
                return;
            }

            if (_service.Status == ASRStatus.Listening &&
                _maxCaptureSeconds > 0f &&
                Time.time - _listeningStartedAt >= _maxCaptureSeconds)
            {
                _ = StopListeningAndTranscribe();
                return;
            }

            if (_holdKeyToTalk)
            {
                if (RuntimeHotkeyInput.GetKeyDown(_pushToTalkKey))
                {
                    _ = StartListening();
                }
                else if (RuntimeHotkeyInput.GetKeyUp(_pushToTalkKey))
                {
                    _ = StopListeningAndTranscribe();
                }
            }
            else if (RuntimeHotkeyInput.GetKeyDown(_pushToTalkKey))
            {
                if (_service.Status == ASRStatus.Listening)
                {
                    _ = StopListeningAndTranscribe();
                }
                else
                {
                    _ = StartListening();
                }
            }
        }

        private void UpdateManualStubPushToTalk()
        {
            if (_holdKeyToTalk)
            {
                if (RuntimeHotkeyInput.GetKeyDown(_pushToTalkKey))
                {
                    Debug.Log($"{LogPrefix} ManualStub key_down | key={_pushToTalkKey} | whisper_used=false", this);
                    _ = StartManualStubListening();
                }

                if (RuntimeHotkeyInput.GetKey(_pushToTalkKey) && _manualStubListening)
                {
                    Debug.Log($"{LogPrefix} ManualStub hold_active | key={_pushToTalkKey}", this);
                }

                if (RuntimeHotkeyInput.GetKeyUp(_pushToTalkKey))
                {
                    Debug.Log($"{LogPrefix} ManualStub key_up | key={_pushToTalkKey}", this);
                    _ = SubmitManualStubTranscript();
                }

                return;
            }

            if (RuntimeHotkeyInput.GetKeyDown(_pushToTalkKey))
            {
                Debug.Log($"{LogPrefix} ManualStub key_down | key={_pushToTalkKey} | holdKeyToTalk=false | whisper_used=false", this);
                _ = DebugSubmitManualTranscriptAsync();
            }

            if (RuntimeHotkeyInput.GetKeyUp(_pushToTalkKey))
            {
                Debug.Log($"{LogPrefix} ManualStub key_up | key={_pushToTalkKey} | holdKeyToTalk=false", this);
            }
        }

        private async System.Threading.Tasks.Task StartManualStubListening()
        {
            Debug.Log($"{LogPrefix} ManualStub backend selected | simulated_listening_start | manual_transcript='{_manualTranscript}' | whisper_used=false", this);
            await StartListening();
            _manualStubListening = _service.Status == ASRStatus.Listening;
        }

        private async System.Threading.Tasks.Task SubmitManualStubTranscript()
        {
            if (!_manualStubListening && _service.Status != ASRStatus.Listening)
            {
                Debug.Log($"{LogPrefix} ManualStub key_up_without_active_listening | ignored", this);
                return;
            }

            if (string.IsNullOrWhiteSpace(_manualTranscript))
            {
                Debug.LogWarning($"{LogPrefix} ManualStub ignored_empty_transcript", this);
            }

            LogManualStubConnectorState();
            Debug.Log($"{LogPrefix} ManualStub submitting_transcript | text='{_manualTranscript}' | whisper_used=false", this);
            _manualStubListening = false;
            await StopListeningAndTranscribe();
        }

        private async System.Threading.Tasks.Task DebugSubmitManualTranscriptAsync()
        {
            Debug.Log($"{LogPrefix} ManualStub debug_submit_manual_transcript | key_route_equivalent=true | whisper_used=false", this);
            await StartManualStubListening();
            await SubmitManualStubTranscript();
        }

        private void OnDisable()
        {
            DeactivateVoicePipeline("controller_disabled");
            StopContinuousVadCapture();
        }

        private void OnDestroy()
        {
            DeactivateVoicePipeline("controller_destroyed");
            StopContinuousVadCapture();
            _microphoneService?.Dispose();
            _microphoneService = null;
            _service = null;
        }

        public bool SetExperimentVoicePipelineActive(
            bool active,
            string conditionId,
            int visibleTrialNumber,
            int roundNumber,
            string reason)
        {
            _conditionId = conditionId ?? string.Empty;
            _visibleTrialNumber = Mathf.Max(0, visibleTrialNumber);
            _roundNumber = Mathf.Max(0, roundNumber);
            _voiceBootstrap ??= ExperimentVoicePipelineBootstrap.EnsureExists();

            if (!active)
            {
                DeactivateVoicePipeline(reason);
                return true;
            }

            if (_voiceBootstrap == null || !_voiceBootstrap.IsPrepared)
            {
                _voicePipelineActivated = false;
                _voiceBootstrap?.ReportRuntimeState(false, false, false);
                _voiceBootstrap?.LogRuntimePhase(
                    "voice_pipeline_prepare_failed",
                    0L,
                    "activation_requested_before_prepared:" + (reason ?? string.Empty),
                    _conditionId,
                    _visibleTrialNumber,
                    _roundNumber);
                return false;
            }

            _voicePipelineActivated = true;
            _firstVoiceCommandReceivedLogged = false;
            _firstVoiceCommandOutcomeLogged = false;
            if (_listeningMode == VoiceListeningMode.ContinuousVad)
            {
                StartContinuousVadCaptureWhenWhisperReady("condition_voice_activated");
            }

            _voiceBootstrap.ReportRuntimeState(
                _continuousVadCaptureActive,
                _continuousVadCaptureActive,
                true);
            _voiceBootstrap.LogRuntimePhase(
                "voice_pipeline_activated",
                0L,
                reason,
                _conditionId,
                _visibleTrialNumber,
                _roundNumber);
            return true;
        }

        private void DeactivateVoicePipeline(string reason)
        {
            bool wasActive = _voicePipelineActivated || _continuousVadCaptureActive;
            _voicePipelineActivated = false;
            StopContinuousVadCapture();
            _voiceBootstrap?.ReportRuntimeState(false, false, false);
            if (wasActive)
            {
                _voiceBootstrap?.LogRuntimePhase(
                    "voice_pipeline_deactivated",
                    0L,
                    reason,
                    _conditionId,
                    _visibleTrialNumber,
                    _roundNumber);
            }
        }

        [ContextMenu("ASR/Start Listening")]
        public async void StartListeningFromInspector()
        {
            await StartListening();
        }

        [ContextMenu("ASR/Stop And Transcribe")]
        public async void StopListeningFromInspector()
        {
            await StopListeningAndTranscribe();
        }

        [ContextMenu("Debug Submit Manual Transcript")]
        public void DebugSubmitManualTranscriptFromInspector()
        {
            DebugSubmitManualCommand("DebugSubmitManualTranscript", _manualTranscript);
        }

        [ContextMenu("Debug Submit Manual Confirmation Yes")]
        public void DebugSubmitManualConfirmationYesFromInspector()
        {
            DebugSubmitManualCommand("DebugSubmitManualConfirmationYes", "sí");
        }

        [ContextMenu("Debug Submit Manual Confirmation No")]
        public void DebugSubmitManualConfirmationNoFromInspector()
        {
            DebugSubmitManualCommand("DebugSubmitManualConfirmationNo", "no");
        }

        private async System.Threading.Tasks.Task StartListening()
        {
            if (_service.Status == ASRStatus.Listening || _service.Status == ASRStatus.Transcribing)
            {
                return;
            }

            _activeVoiceInteractionId = CreateVoiceInteractionId();
            CaptureActiveVoiceInteractionPauseBoundary();
            _operationInProgress = true;
            ASRRequest request = new(
                _backend,
                _language,
                _microphoneDevice,
                _sampleRate,
                _maxCaptureSeconds);
            LogWhisperRuntimeModel("utterance_start", request.RequestedBackend, request.UtteranceId);
            LogP45DAsrUtteranceStarted(request, ResolveCommandSource(request.RequestedBackend));

            if (_asrDebugLogging)
            {
                Debug.Log(
                    $"{LogPrefix} ASR request | utterance={request.UtteranceId} backend={_backend} requested_microphone={FormatRequestedMicrophone()} sample_rate={_sampleRate} max_capture_seconds={_maxCaptureSeconds:0.###} language={_language} hold_to_talk={_holdKeyToTalk} timestamp={DateTime.Now:O}",
                    this);
            }

            ASRResult result;
            try
            {
                result = await _service.StartListeningAsync(request);
            }
            finally
            {
                _operationInProgress = false;
            }

            LogP45EBackendSelection(request, result, ResolveCommandSource(result.Backend));

            if (result.Success)
            {
                LogFirstVoiceCommandReceived(result, "continuous_vad");
                _listeningStartedAt = Time.time;
                LogVoiceEvent("voice_listening_started", result, ResolveCommandSource(result.Backend));
                LogAsrDiagnosticEvent("asr_diagnostic_utterance_started", null);
                if (result.Backend == ASRBackend.ManualStub)
                {
                    if (_backend == ASRBackend.Auto)
                    {
                        LogVoiceEvent(
                            "voice_asr_backend_unavailable",
                            ASRResult.Failed(
                                result.UtteranceId,
                                ASRBackend.WhisperUnity,
                                "whisper_unity_backend_unavailable_auto_fallback",
                                result.Language,
                                result.DurationMs,
                                result.MicrophoneDevice,
                                result.SampleRate),
                            "manual_stub");
                    }

                    LogVoiceEvent("voice_asr_manual_stub_used", result, "manual_stub");
                }

                Debug.Log($"{LogPrefix} Listening started | utterance={result.UtteranceId} backend={result.Backend}", this);
                return;
            }

            LogVoiceEvent("voice_asr_backend_unavailable", result, ResolveCommandSource(result.Backend));
            Debug.LogWarning($"{LogPrefix} ASR backend unavailable | reason={result.ErrorReason}", this);
        }

        private void StartContinuousVadCapture()
        {
            if (_continuousVadCaptureActive || !_voicePipelineActivated)
            {
                return;
            }


            if (_voiceBootstrap == null || !_voiceBootstrap.IsPrepared)
            {
                _voiceBootstrap?.LogRuntimePhase(
                    "voice_pipeline_prepare_failed",
                    0L,
                    "microphone_open_blocked_pipeline_not_prepared",
                    _conditionId,
                    _visibleTrialNumber,
                    _roundNumber);
                return;
            }

            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                LogVoiceEvent(
                    "voice_microphone_capture_error",
                    ASRResult.Failed(
                        ASRUtteranceId.Create(),
                        _backend,
                        "microphone_device_not_found",
                        _language,
                        0L,
                        _microphoneDevice,
                        _sampleRate),
                    "continuous_vad",
                    new Dictionary<string, object>
                    {
                        ["reason"] = "microphone_device_not_found",
                        ["listening_mode"] = _listeningMode.ToString()
                    });
                Debug.LogWarning($"{LogPrefix} Continuous VAD unavailable | microphone_device_not_found", this);
                return;
            }

            _continuousVadStartDevice = string.IsNullOrWhiteSpace(_microphoneDevice)
                ? null
                : _microphoneDevice;
            int sampleRate = Mathf.Max(8000, _sampleRate);
            int bufferSeconds = Mathf.Max(
                2,
                Mathf.CeilToInt(Mathf.Max(_vadMaxUtteranceSeconds, _maxCaptureSeconds) + _vadSilenceDurationToCloseSeconds + 1f));

            try
            {
                Stopwatch microphoneOpenStopwatch = Stopwatch.StartNew();
                _voiceBootstrap.LogRuntimePhase(
                    "microphone_open_started",
                    0L,
                    "c11_activation",
                    _conditionId,
                    _visibleTrialNumber,
                    _roundNumber);
                RebuildVadSegmenter();
                _continuousVadClip = Microphone.Start(_continuousVadStartDevice, true, bufferSeconds, sampleRate);
                microphoneOpenStopwatch.Stop();
                _continuousVadCaptureActive = _continuousVadClip != null;
                if (!_continuousVadCaptureActive)
                {
                    throw new InvalidOperationException("microphone_start_returned_null_clip");
                }

                _voiceBootstrap.ReportRuntimeState(true, true, true);
                _voiceBootstrap.LogRuntimePhase(
                    "microphone_open_completed",
                    microphoneOpenStopwatch.ElapsedMilliseconds,
                    "c11_capture_ready",
                    _conditionId,
                    _visibleTrialNumber,
                    _roundNumber);
                LogVoiceEvent(
                    "voice_listening_started",
                    ASRResult.Successful(
                        ASRUtteranceId.Create(),
                        _backend,
                        string.Empty,
                        _language,
                        0L,
                        _microphoneDevice,
                        sampleRate),
                    "continuous_vad",
                    new Dictionary<string, object>
                    {
                        ["listening_mode"] = _listeningMode.ToString(),
                        ["source"] = "continuous_vad"
                    });
                if (_asrDebugLogging)
                {
                Debug.Log(
                    $"{LogPrefix} Continuous VAD started | requested_microphone={FormatRequestedMicrophone()} sample_rate={sampleRate} buffer_seconds={bufferSeconds} voice_rms_threshold={_vadVoiceRmsThreshold:0.000000} optional_soft_wake_prefix='{_optionalSoftWakePrefix}'",
                    this);
            }
            }
            catch (Exception ex)
            {
                if (_continuousVadCaptureActive && Microphone.IsRecording(_continuousVadStartDevice))
                {
                    Microphone.End(_continuousVadStartDevice);
                }

                _continuousVadCaptureActive = false;
                DestroyContinuousVadClip();
                _continuousVadStartDevice = null;
                _voiceBootstrap?.ReportRuntimeState(false, false, _voicePipelineActivated);
                _voiceBootstrap?.LogRuntimePhase(
                    "voice_pipeline_prepare_failed",
                    0L,
                    "microphone_open_failed:" + ex.GetBaseException().Message,
                    _conditionId,
                    _visibleTrialNumber,
                    _roundNumber);
                LogVoiceEvent(
                    "voice_microphone_capture_error",
                    ASRResult.Failed(
                        ASRUtteranceId.Create(),
                        _backend,
                        ex.GetBaseException().Message,
                        _language,
                        0L,
                        _microphoneDevice,
                        _sampleRate),
                    "continuous_vad",
                    new Dictionary<string, object>
                    {
                        ["reason"] = "continuous_vad_start_failed",
                        ["error_message"] = ex.GetBaseException().Message,
                        ["listening_mode"] = _listeningMode.ToString()
                    });
                Debug.LogWarning($"{LogPrefix} Continuous VAD start failed | reason={ex.GetBaseException().Message}", this);
            }
        }

        private void StartContinuousVadCaptureWhenWhisperReady(string reason)
        {
            if (!RequiresWhisperBaseReadinessGate())
            {
                StartContinuousVadCapture();
                return;
            }

            WhisperModelLoadState loadState = WhisperModelConfiguration.ResolveLoadState(_whisperManager);
            if (loadState.IsLoaded)
            {
                LogWhisperReadinessReady(0f, reason, loadState);
                StartContinuousVadCapture();
                return;
            }

            if (_whisperReadinessTimedOut)
            {
                return;
            }

            if (_whisperReadinessCoroutine != null)
            {
                return;
            }

            _whisperReadinessCoroutine = StartCoroutine(WaitForWhisperBaseThenStartVad(reason));
        }

        private IEnumerator WaitForWhisperBaseThenStartVad(string reason)
        {
            _continuousVadWaitingForWhisperReadiness = true;
            _whisperReadinessTimedOut = false;
            float startedAt = Time.realtimeSinceStartup;
            WhisperModelRuntimeInfo modelInfo = ResolveWhisperModelInfo();
            WhisperModelLoadState initialState = WhisperModelConfiguration.ResolveLoadState(_whisperManager);
            WhisperModelConfiguration.TryEnsureModelLoading(_whisperManager, out string ensureReason);
            _whisperBaseLoadAttempted = true;
            LogWhisperReadinessWaitStarted(reason, modelInfo, initialState, ensureReason);
            LogP45D03ModelLoadStarted(reason, ensureReason, WhisperModelConfiguration.ResolveLoadState(_whisperManager));

            while (Time.realtimeSinceStartup - startedAt < AndroidWhisperBaseReadinessTimeoutSeconds)
            {
                WhisperModelLoadState state = WhisperModelConfiguration.ResolveLoadState(_whisperManager);
                if (state.IsLoaded)
                {
                    float elapsed = Time.realtimeSinceStartup - startedAt;
                    _continuousVadWaitingForWhisperReadiness = false;
                    _whisperReadinessCoroutine = null;
                    LogWhisperReadinessReady(elapsed, reason, state);
                    LogP45D03ModelLoadSucceeded(reason, elapsed, state);
                    StartContinuousVadCapture();
                    yield break;
                }

                if (_whisperBaseLoadAttempted && state.ManagerResolved && !state.IsLoading && !state.IsLoaded)
                {
                    float elapsed = Time.realtimeSinceStartup - startedAt;
                    _continuousVadWaitingForWhisperReadiness = false;
                    _whisperReadinessCoroutine = null;
                    LogP45D03ModelLoadFailed(reason, elapsed, state, "loading_finished_without_loaded_model");
                    yield break;
                }

                yield return new WaitForSecondsRealtime(WhisperReadinessPollIntervalSeconds);
            }

            _continuousVadWaitingForWhisperReadiness = false;
            _whisperReadinessTimedOut = true;
            _whisperReadinessCoroutine = null;
            LogWhisperReadinessTimeout(Time.realtimeSinceStartup - startedAt, reason, WhisperModelConfiguration.ResolveLoadState(_whisperManager));
            LogP45D03ModelLoadFailed(reason, Time.realtimeSinceStartup - startedAt, WhisperModelConfiguration.ResolveLoadState(_whisperManager), "readiness_timeout");
        }

        private void StopContinuousVadCapture()
        {
            if (!_continuousVadCaptureActive)
            {
                DestroyContinuousVadClip();
                _continuousVadStartDevice = null;
                return;
            }

            if (Microphone.IsRecording(_continuousVadStartDevice))
            {
                Microphone.End(_continuousVadStartDevice);
            }

            _continuousVadCaptureActive = false;
            DestroyContinuousVadClip();
            _continuousVadStartDevice = null;
            _continuousVadTranscriptionInProgress = false;
            _vadSegmenter?.Reset();
            _voiceBootstrap?.ReportRuntimeState(false, false, _voicePipelineActivated);
            LogVoiceEvent(
                "voice_listening_stopped",
                ASRResult.Successful(
                    ASRUtteranceId.Create(),
                    _backend,
                    string.Empty,
                    _language,
                    0L,
                    _microphoneDevice,
                    _sampleRate),
                "continuous_vad",
                new Dictionary<string, object>
                {
                    ["reason"] = "continuous_vad_capture_stopped",
                    ["listening_mode"] = _listeningMode.ToString(),
                    ["source"] = "continuous_vad"
                });
        }

        private void DestroyContinuousVadClip()
        {
            if (_continuousVadClip == null)
            {
                return;
            }

            Destroy(_continuousVadClip);
            _continuousVadClip = null;
        }

        private void UpdateContinuousVad()
        {
            if (_operationInProgress || _continuousVadTranscriptionInProgress)
            {
                return;
            }

            if (!_continuousVadCaptureActive || _continuousVadClip == null || !Microphone.IsRecording(_continuousVadStartDevice))
            {
                StartContinuousVadCaptureWhenWhisperReady("vad_update_capture_inactive");
                return;
            }

            if (!TryReadContinuousVadRms(out float rms))
            {
                return;
            }

            if (rms < _vadVoiceRmsThreshold && !_vadBelowThresholdLogged)
            {
                _vadBelowThresholdLogged = true;
                LogVoiceEvent(
                    "voice_vad_below_threshold",
                    ASRResult.Successful(
                        ASRUtteranceId.Create(),
                        _backend,
                        string.Empty,
                        _language,
                        0L,
                        _microphoneDevice,
                        _sampleRate),
                    "continuous_vad",
                    new Dictionary<string, object>
                    {
                        ["rms"] = rms,
                        ["rms_threshold"] = _vadVoiceRmsThreshold,
                        ["source"] = "continuous_vad"
                    });
            }

            VoiceVadSegmentDecision decision = _vadSegmenter.Process(rms, Time.time);
            if (decision.Type == VoiceVadSegmentDecisionType.SpeechStarted)
            {
                _activeVoiceInteractionId = CreateVoiceInteractionId();
                CaptureActiveVoiceInteractionPauseBoundary();
                _shortCommandVadExtensionLogged = false;
                _vadBelowThresholdLogged = false;
                LogVadSegmentOpened();
                LogAsrDiagnosticEvent("asr_diagnostic_utterance_started", null);
                if (_asrDebugLogging)
                {
                    Debug.Log($"{LogPrefix} Continuous VAD speech started | rms={rms:0.000000}", this);
                }

                return;
            }

            if (decision.Type == VoiceVadSegmentDecisionType.None &&
                !_shortCommandVadExtensionLogged &&
                _vadSegmenter != null &&
                _vadSegmenter.State == VoiceVadSegmentState.InSpeech &&
                rms < _vadVoiceRmsThreshold)
            {
                float duration = _vadSegmenter.GetCurrentSegmentDuration(Time.time);
                if (duration > 0f && duration < _vadMinUtteranceSeconds)
                {
                    _shortCommandVadExtensionLogged = true;
                    LogP45E05ShortCommandVadExtended(duration, rms);
                }
            }

            if (decision.Type == VoiceVadSegmentDecisionType.SegmentDiscarded)
            {
                LogVadSegmentClosed(decision, accepted: false);
                Debug.LogWarning(
                    $"{LogPrefix} Continuous VAD segment discarded | reason={decision.Reason} duration={decision.SegmentDuration:0.###}",
                    this);
                return;
            }

            if (decision.Type == VoiceVadSegmentDecisionType.SegmentClosed)
            {
                LogVadSegmentClosed(decision, accepted: true);
                _ = TranscribeContinuousVadSegment(decision);
            }
        }

        private async System.Threading.Tasks.Task TranscribeContinuousVadSegment(VoiceVadSegmentDecision decision)
        {
            if (_continuousVadTranscriptionInProgress)
            {
                return;
            }

            if (RequiresWhisperBaseReadinessGate() && !WhisperModelConfiguration.ResolveLoadState(_whisperManager).IsLoaded)
            {
                LogTranscriptionBlockedModelNotReady(decision, "continuous_vad_segment_closed_before_model_ready");
                return;
            }

            if (!TryBuildContinuousVadSegmentClip(decision, out AudioClip segmentClip, out ASRAudioAnalysis analysis))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_activeVoiceInteractionId))
            {
                _activeVoiceInteractionId = CreateVoiceInteractionId();
                CaptureActiveVoiceInteractionPauseBoundary();
            }
            _continuousVadTranscriptionInProgress = true;
            _operationInProgress = true;
            int requestPauseEpoch = _activeVoiceInteractionPauseEpoch;
            bool requestStartedWhilePaused = _activeVoiceInteractionStartedWhilePaused;

            ASRRequest request = new(
                _backend,
                _language,
                _microphoneDevice,
                _sampleRate,
                Mathf.Max(_maxCaptureSeconds, _vadMaxUtteranceSeconds));
            bool shortCommandCandidate = IsShortCommandCandidate(decision, null);
            if (shortCommandCandidate)
            {
                LogP45E05ShortCommandCandidate(request, decision, analysis);
            }

            LogWhisperRuntimeModel("continuous_vad_utterance_start", request.RequestedBackend, request.UtteranceId);
            LogP45DAsrUtteranceStarted(request, "continuous_vad");

            if (_asrDebugLogging)
            {
                Debug.Log(
                    $"{LogPrefix} Continuous VAD segment closed | utterance={request.UtteranceId} duration={decision.SegmentDuration:0.###} recorded_duration_ms={analysis.RecordedDurationMs} rms={analysis.Rms:0.000000}",
                    this);
            }

            ASRResult started = ASRResult.Successful(
                request.UtteranceId,
                _backend,
                string.Empty,
                request.Language,
                0L,
                request.MicrophoneDevice,
                request.SampleRate,
                float.NaN,
                analysis);
            LogVoiceEvent("voice_listening_started", started, "continuous_vad");
            LogVoiceEvent(
                "voice_listening_stopped",
                started,
                "continuous_vad",
                new Dictionary<string, object>
                {
                    ["reason"] = "vad_segment_closed",
                    ["duration_seconds"] = decision.SegmentDuration
                });

            Stopwatch asrStopwatch = Stopwatch.StartNew();
            BeginAsrFrameHitchWatchdog(request.UtteranceId);
            ASRResult result;
            try
            {
                result = await _microphoneService.TranscribeSegmentAsync(
                    segmentClip,
                    request,
                    analysis,
                    _manualTranscript);
            }
            finally
            {
                asrStopwatch.Stop();
                _operationInProgress = false;
                _continuousVadTranscriptionInProgress = false;
                Destroy(segmentClip);
            }

            LogAsrFrameHitchWatchdogResult(request.UtteranceId, asrStopwatch.ElapsedMilliseconds, "continuous_vad");
            LogP45EBackendSelection(request, result, "continuous_vad");

            if (result.Success)
            {
                shortCommandCandidate = shortCommandCandidate || IsShortCommandCandidate(decision, result);
                if (shortCommandCandidate)
                {
                    LogP45E05ShortCommandTranscript(result, decision, analysis);
                }

                LogVoiceEvent("voice_transcription_received", result, ResolveTranscriptionSource(result.Backend, "continuous_vad"));
                RecordAsrDiagnosticUtterance(result, decision, asrStopwatch.ElapsedMilliseconds);
                Debug.Log($"{LogPrefix} Transcript received | utterance={result.UtteranceId} backend={result.Backend} text='{result.Transcript}'", this);
                if (result.Backend == ASRBackend.ManualStub)
                {
                    Debug.Log($"{LogPrefix} ManualStub transcript submitted | text='{result.Transcript}'", this);
                }

                Stopwatch postprocessStopwatch = Stopwatch.StartNew();
                Stopwatch normalizationStopwatch = Stopwatch.StartNew();
                LogCommandNormalizationDiagnostic(result.Transcript);
                normalizationStopwatch.Stop();
                Stopwatch routingStopwatch = Stopwatch.StartNew();
                bool forcePauseRejection = requestStartedWhilePaused ||
                    requestPauseEpoch != ExperimentRuntimePauseCoordinator.PauseEpoch;
                VoiceAutonomyCommandRoutingResult routing = ProcessAutonomyCommand(
                    result.Transcript,
                    ResolveTranscriptionSource(result.Backend, "continuous_vad"),
                    forceExperimentPauseRejection: forcePauseRejection);
                LogFirstVoiceCommandOutcome(result, routing, "continuous_vad");
                routingStopwatch.Stop();
                postprocessStopwatch.Stop();
                LogP45E05AsrPostprocessEvaluated(result, routing, normalizationStopwatch.ElapsedMilliseconds, routingStopwatch.ElapsedMilliseconds, postprocessStopwatch.ElapsedMilliseconds, "continuous_vad");
                if (shortCommandCandidate)
                {
                    LogP45E05ShortCommandRouting(result, routing, decision, analysis);
                }

                LogP45EAsrUtteranceCompleted(result, asrStopwatch.ElapsedMilliseconds, "continuous_vad", routing);
                LogP45DAsrUtteranceCompleted(result, asrStopwatch.ElapsedMilliseconds, "continuous_vad", routing);
            }
            else
            {
                if (shortCommandCandidate)
                {
                    LogP45E05ShortCommandRejected(result.Transcript, result, result.ErrorReason, decision, analysis);
                }

                LogVoiceEvent("voice_transcription_failed", result, ResolveTranscriptionSource(result.Backend, "continuous_vad"));
                RecordAsrDiagnosticUtterance(result, decision, asrStopwatch.ElapsedMilliseconds);
                LogP45EAsrUtteranceFailed(result, asrStopwatch.ElapsedMilliseconds, "continuous_vad");
                LogP45DAsrUtteranceFailed(result, asrStopwatch.ElapsedMilliseconds, "continuous_vad");
                Debug.LogWarning($"{LogPrefix} Transcript failed | utterance={result.UtteranceId} backend={result.Backend} reason={result.ErrorReason}", this);
            }
        }

        private async System.Threading.Tasks.Task StopListeningAndTranscribe()
        {
            if (_service.Status != ASRStatus.Listening)
            {
                return;
            }

            _operationInProgress = true;
            Stopwatch asrStopwatch = Stopwatch.StartNew();
            BeginAsrFrameHitchWatchdog(_service.ActiveUtteranceId);
            ASRResult result;
            try
            {
                result = await _service.StopListeningAndTranscribeAsync(_manualTranscript);
            }
            finally
            {
                asrStopwatch.Stop();
                _operationInProgress = false;
            }

            LogAsrFrameHitchWatchdogResult(result.UtteranceId, asrStopwatch.ElapsedMilliseconds, ResolveCommandSource(result.Backend));
            ASRResult stopped = ASRResult.Successful(
                result.UtteranceId,
                result.Backend,
                string.Empty,
                result.Language,
                result.DurationMs,
                result.MicrophoneDevice,
                result.SampleRate,
                float.NaN,
                result.AudioAnalysis,
                result.DiagnosticAudioPath);
            LogVoiceEvent(
                "voice_listening_stopped",
                stopped,
                ResolveCommandSource(result.Backend),
                new Dictionary<string, object>
                {
                    ["reason"] = "stop_and_transcribe",
                    ["duration_seconds"] = result.DurationMs / 1000f
                });

            if (result.Success)
            {
                LogFirstVoiceCommandReceived(result, ResolveCommandSource(result.Backend));
                LogVoiceEvent("voice_transcription_received", result, ResolveTranscriptionSource(result.Backend, ResolveCommandSource(result.Backend)));
                RecordAsrDiagnosticUtterance(result, null, asrStopwatch.ElapsedMilliseconds);
                Debug.Log($"{LogPrefix} Transcript received | utterance={result.UtteranceId} backend={result.Backend} text='{result.Transcript}'", this);
                if (result.Backend == ASRBackend.ManualStub)
                {
                    Debug.Log($"{LogPrefix} ManualStub transcript submitted | text='{result.Transcript}'", this);
                }

                string commandSource = ResolveCommandSource(result.Backend);
                Stopwatch postprocessStopwatch = Stopwatch.StartNew();
                Stopwatch normalizationStopwatch = Stopwatch.StartNew();
                LogCommandNormalizationDiagnostic(result.Transcript);
                normalizationStopwatch.Stop();
                Stopwatch routingStopwatch = Stopwatch.StartNew();
                bool forcePauseRejection = _activeVoiceInteractionStartedWhilePaused ||
                    _activeVoiceInteractionPauseEpoch != ExperimentRuntimePauseCoordinator.PauseEpoch;
                VoiceAutonomyCommandRoutingResult routing = ProcessAutonomyCommand(
                    result.Transcript,
                    ResolveTranscriptionSource(result.Backend, commandSource),
                    forceExperimentPauseRejection: forcePauseRejection);
                LogFirstVoiceCommandOutcome(result, routing, commandSource);
                routingStopwatch.Stop();
                postprocessStopwatch.Stop();
                LogP45E05AsrPostprocessEvaluated(result, routing, normalizationStopwatch.ElapsedMilliseconds, routingStopwatch.ElapsedMilliseconds, postprocessStopwatch.ElapsedMilliseconds, commandSource);
                LogP45EAsrUtteranceCompleted(result, asrStopwatch.ElapsedMilliseconds, commandSource, routing);
                LogP45DAsrUtteranceCompleted(result, asrStopwatch.ElapsedMilliseconds, commandSource, routing);
            }
            else
            {
                LogVoiceEvent("voice_transcription_failed", result, ResolveTranscriptionSource(result.Backend, ResolveCommandSource(result.Backend)));
                RecordAsrDiagnosticUtterance(result, null, asrStopwatch.ElapsedMilliseconds);
                LogP45EAsrUtteranceFailed(result, asrStopwatch.ElapsedMilliseconds, ResolveCommandSource(result.Backend));
                LogP45DAsrUtteranceFailed(result, asrStopwatch.ElapsedMilliseconds, ResolveCommandSource(result.Backend));
                Debug.LogWarning($"{LogPrefix} Transcript failed | utterance={result.UtteranceId} backend={result.Backend} reason={result.ErrorReason}", this);
            }
        }

        private void LogFirstVoiceCommandReceived(ASRResult result, string source)
        {
            if (_firstVoiceCommandReceivedLogged)
            {
                return;
            }

            _firstVoiceCommandReceivedLogged = true;
            _voiceBootstrap?.LogRuntimePhase(
                "first_voice_command_received",
                result?.DurationMs ?? 0L,
                source,
                _conditionId,
                _visibleTrialNumber,
                _roundNumber,
                new Dictionary<string, object>
                {
                    ["utterance_id"] = result?.UtteranceId ?? string.Empty,
                    ["backend"] = result?.Backend.ToString() ?? string.Empty,
                    ["transcript_empty"] = string.IsNullOrWhiteSpace(result?.Transcript)
                });
        }

        private void LogFirstVoiceCommandOutcome(
            ASRResult result,
            VoiceAutonomyCommandRoutingResult routing,
            string source)
        {
            if (_firstVoiceCommandOutcomeLogged)
            {
                return;
            }

            _firstVoiceCommandOutcomeLogged = true;
            bool accepted = routing != null &&
                (routing.Status == VoiceAutonomyCommandRoutingStatus.Submitted ||
                 routing.Status == VoiceAutonomyCommandRoutingStatus.ConfirmationAccepted ||
                 routing.Status == VoiceAutonomyCommandRoutingStatus.PendingConfirmation);
            _voiceBootstrap?.LogRuntimePhase(
                accepted ? "first_voice_command_accepted" : "first_voice_command_rejected",
                result?.DurationMs ?? 0L,
                routing?.Reason ?? "routing_result_missing",
                _conditionId,
                _visibleTrialNumber,
                _roundNumber,
                new Dictionary<string, object>
                {
                    ["utterance_id"] = result?.UtteranceId ?? string.Empty,
                    ["source"] = source ?? string.Empty,
                    ["routing_status"] = routing?.Status.ToString() ?? string.Empty,
                    ["capture_active"] = _continuousVadCaptureActive,
                    ["pipeline_active"] = _voicePipelineActivated
                });
        }

        private void LogVoiceEvent(string eventType, ASRResult result, string source = "", Dictionary<string, object> extraPayload = null)
        {
            if (!_enableVoiceExperimentLogging)
            {
                return;
            }

            Dictionary<string, object> payload = result.ToTelemetryPayload();
            payload.Remove("diagnostic_audio_path");
            payload["raw_transcript"] = result.Transcript;
            payload["was_empty"] = string.IsNullOrWhiteSpace(result.Transcript);
            payload["source"] = string.IsNullOrWhiteSpace(source) ? ResolveCommandSource(result.Backend) : source;
            payload["command_source"] = payload["source"];
            if (!string.IsNullOrWhiteSpace(_activeVoiceInteractionId))
            {
                payload["voice_interaction_id"] = _activeVoiceInteractionId;
            }

            payload["listening_mode"] = _listeningMode.ToString();
            payload["duration_seconds"] = result.DurationMs / 1000f;
            payload["participant_id"] = _participantId;
            payload["session_id"] = _sessionId;
            payload["trial_id"] = _trialId;
            payload["asr_controller"] = name;
            if (extraPayload != null)
            {
                foreach (KeyValuePair<string, object> pair in extraPayload)
                {
                    payload[pair.Key] = pair.Value;
                }
            }

            TiagoExperimentTelemetry.LogEvent(eventType, payload);
        }

        private void LogVadSegmentOpened()
        {
            LogVoiceEvent(
                "voice_vad_segment_opened",
                ASRResult.Successful(
                    ASRUtteranceId.Create(),
                    _backend,
                    string.Empty,
                    _language,
                    0L,
                    _microphoneDevice,
                    _sampleRate),
                "continuous_vad",
                new Dictionary<string, object>
                {
                    ["rms_threshold"] = _vadVoiceRmsThreshold,
                    ["min_voice_duration"] = _vadMinVoiceDurationToOpenSeconds,
                    ["source"] = "continuous_vad"
                });
        }

        private void LogVadSegmentClosed(VoiceVadSegmentDecision decision, bool accepted)
        {
            LogVoiceEvent(
                "voice_vad_segment_closed",
                ASRResult.Successful(
                    ASRUtteranceId.Create(),
                    _backend,
                    string.Empty,
                    _language,
                    0L,
                    _microphoneDevice,
                    _sampleRate),
                "continuous_vad",
                new Dictionary<string, object>
                {
                    ["utterance_duration_seconds"] = decision.SegmentDuration,
                    ["accepted"] = accepted,
                    ["reject_reason"] = accepted ? string.Empty : decision.Reason,
                    ["cooldown_seconds"] = _vadCooldownSeconds,
                    ["source"] = "continuous_vad"
                });
        }

        private void LogP45E05ShortCommandVadExtended(float currentDurationSeconds, float rms)
        {
            Dictionary<string, object> payload = BuildP45E05ShortCommandPayload(
                string.Empty,
                null,
                null,
                "vad_extended",
                "duration_below_short_command_minimum");
            payload["current_duration_seconds"] = currentDurationSeconds;
            payload["target_min_duration_seconds"] = _vadMinUtteranceSeconds;
            payload["rms"] = rms;
            payload["rms_threshold"] = _vadVoiceRmsThreshold;
            TiagoExperimentTelemetry.LogEvent("p45e05_short_command_vad_extended", payload);
        }

        private void LogP45E05ShortCommandCandidate(
            ASRRequest request,
            VoiceVadSegmentDecision decision,
            ASRAudioAnalysis analysis)
        {
            Dictionary<string, object> payload = BuildP45E05ShortCommandPayload(
                string.Empty,
                null,
                null,
                "candidate",
                string.Empty);
            payload["utterance_id"] = request?.UtteranceId ?? string.Empty;
            payload["segment_duration_seconds"] = decision.SegmentDuration;
            payload["recorded_duration_ms"] = analysis.RecordedDurationMs;
            TiagoExperimentTelemetry.LogEvent("p45e05_short_command_candidate", payload);
        }

        private void LogP45E05ShortCommandTranscript(
            ASRResult result,
            VoiceVadSegmentDecision decision,
            ASRAudioAnalysis analysis)
        {
            Dictionary<string, object> payload = BuildP45E05ShortCommandPayload(
                result?.Transcript,
                result,
                null,
                "transcript",
                string.Empty);
            payload["segment_duration_seconds"] = decision.SegmentDuration;
            payload["recorded_duration_ms"] = analysis.RecordedDurationMs;
            TiagoExperimentTelemetry.LogEvent("p45e05_short_command_transcript", payload);
        }

        private void LogP45E05ShortCommandRouting(
            ASRResult result,
            VoiceAutonomyCommandRoutingResult routing,
            VoiceVadSegmentDecision decision,
            ASRAudioAnalysis analysis)
        {
            string eventName = routing != null && routing.Submitted
                ? "p45e05_short_command_routed"
                : "p45e05_short_command_rejected";
            Dictionary<string, object> payload = BuildP45E05ShortCommandPayload(
                result?.Transcript,
                result,
                routing,
                routing != null && routing.Submitted ? "routed" : "rejected",
                routing?.Reason ?? "routing_not_submitted");
            payload["segment_duration_seconds"] = decision.SegmentDuration;
            payload["recorded_duration_ms"] = analysis.RecordedDurationMs;
            TiagoExperimentTelemetry.LogEvent(eventName, payload);
        }

        private void LogP45E05AsrPostprocessEvaluated(
            ASRResult result,
            VoiceAutonomyCommandRoutingResult routing,
            long normalizationMs,
            long routingMs,
            long postprocessMs,
            string source)
        {
            Dictionary<string, object> payload = result?.ToTelemetryPayload() ?? new Dictionary<string, object>();
            payload.Remove("diagnostic_audio_path");
            payload["source"] = source ?? string.Empty;
            payload["main_thread_postprocess_ms"] = Math.Max(0L, postprocessMs);
            payload["normalization_ms"] = Math.Max(0L, normalizationMs);
            payload["mapping_routing_ms"] = Math.Max(0L, routingMs);
            payload["routing_status"] = routing?.Status.ToString() ?? string.Empty;
            payload["routing_reason"] = routing?.Reason ?? string.Empty;
            payload["routing_submitted"] = routing?.Submitted ?? false;
            payload["intent"] = routing?.Mapping?.IntentKind.ToString() ?? string.Empty;
            payload["intent_kind"] = payload["intent"];
            payload["platform"] = Application.platform.ToString();
            payload["condition_id"] = _conditionId ?? string.Empty;
            payload["trial_id"] = _trialId ?? string.Empty;
            payload["round_id"] = _roundId ?? string.Empty;
            payload["fallback_used"] = false;
            TiagoExperimentTelemetry.LogEvent("p45e05_asr_postprocess_evaluated", payload);
        }

        private void LogP45E05ShortCommandRejected(
            string transcript,
            ASRResult result,
            string reason,
            VoiceVadSegmentDecision decision,
            ASRAudioAnalysis analysis)
        {
            Dictionary<string, object> payload = BuildP45E05ShortCommandPayload(
                transcript,
                result,
                null,
                "rejected",
                reason);
            payload["segment_duration_seconds"] = decision.SegmentDuration;
            payload["recorded_duration_ms"] = analysis.RecordedDurationMs;
            TiagoExperimentTelemetry.LogEvent("p45e05_short_command_rejected", payload);
        }

        private Dictionary<string, object> BuildP45E05ShortCommandPayload(
            string transcript,
            ASRResult result,
            VoiceAutonomyCommandRoutingResult routing,
            string status,
            string reason)
        {
            VoiceCommandNormalizationResult normalization = _commandNormalizer.Normalize(transcript ?? string.Empty);
            return new Dictionary<string, object>
            {
                ["utterance_id"] = result?.UtteranceId ?? string.Empty,
                ["backend"] = result?.Backend.ToString() ?? _backend.ToString(),
                ["transcript"] = transcript ?? string.Empty,
                ["normalized_text"] = normalization.NormalizedText,
                ["normalization_status"] = normalization.Status.ToString(),
                ["normalization_action"] = normalization.Action.ToString(),
                ["normalization_score"] = normalization.Score,
                ["routing_status"] = routing?.Status.ToString() ?? string.Empty,
                ["routing_reason"] = routing?.Reason ?? string.Empty,
                ["routing_submitted"] = routing?.Submitted ?? false,
                ["intent"] = routing?.Mapping?.IntentKind.ToString() ?? string.Empty,
                ["short_command_status"] = status ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["min_utterance_seconds"] = _vadMinUtteranceSeconds,
                ["silence_tail_seconds"] = _vadSilenceDurationToCloseSeconds,
                ["pre_roll_seconds"] = _vadPreRollSeconds,
                ["candidate_max_seconds"] = _vadShortCommandCandidateMaxSeconds,
                ["platform"] = Application.platform.ToString(),
                ["condition_id"] = _conditionId ?? string.Empty,
                ["trial_id"] = _trialId ?? string.Empty,
                ["round_id"] = _roundId ?? string.Empty,
                ["fallback_used"] = false
            };
        }

        private bool IsShortCommandCandidate(VoiceVadSegmentDecision decision, ASRResult result)
        {
            if (decision.SegmentDuration > 0f && decision.SegmentDuration <= _vadShortCommandCandidateMaxSeconds)
            {
                return true;
            }

            string transcript = result?.Transcript ?? string.Empty;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return false;
            }

            int wordCount = transcript
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Length;
            return wordCount > 0 && wordCount <= 4;
        }

        private void BeginAsrFrameHitchWatchdog(string utteranceId)
        {
            if (!_enableAsrFrameHitchWatchdog)
            {
                return;
            }

            _asrFrameHitchWatchdogActive = true;
            _asrFrameHitchWatchdogUtteranceId = utteranceId ?? string.Empty;
            _asrFrameHitchWatchdogStartedAt = Time.realtimeSinceStartup;
            _asrFrameHitchMaxDeltaMs = 0f;
            _asrFrameHitchWarningFrames = 0;
            _asrFrameHitchCriticalFrames = 0;
        }

        private void UpdateAsrFrameHitchWatchdog()
        {
            if (!_enableAsrFrameHitchWatchdog || !_asrFrameHitchWatchdogActive)
            {
                return;
            }

            float deltaMs = Time.unscaledDeltaTime * 1000f;
            _asrFrameHitchMaxDeltaMs = Mathf.Max(_asrFrameHitchMaxDeltaMs, deltaMs);
            if (deltaMs >= _frameHitchCriticalThresholdMs)
            {
                _asrFrameHitchCriticalFrames++;
            }
            else if (deltaMs >= _frameHitchWarningThresholdMs)
            {
                _asrFrameHitchWarningFrames++;
            }
        }

        private void LogAsrFrameHitchWatchdogResult(string utteranceId, long totalAsrLatencyMs, string source)
        {
            if (!_enableAsrFrameHitchWatchdog || !_asrFrameHitchWatchdogActive)
            {
                return;
            }

            bool critical = _asrFrameHitchCriticalFrames > 0 || _asrFrameHitchMaxDeltaMs >= _frameHitchCriticalThresholdMs;
            bool warning = _asrFrameHitchWarningFrames > 0 || _asrFrameHitchMaxDeltaMs >= _frameHitchWarningThresholdMs;
            Dictionary<string, object> payload = new()
            {
                ["utterance_id"] = string.IsNullOrWhiteSpace(utteranceId) ? _asrFrameHitchWatchdogUtteranceId : utteranceId,
                ["source"] = source ?? string.Empty,
                ["main_thread_block_ms"] = Mathf.RoundToInt(_asrFrameHitchMaxDeltaMs),
                ["max_unscaled_delta_ms"] = _asrFrameHitchMaxDeltaMs,
                ["frame_hitch_detected"] = warning,
                ["frame_hitch_critical"] = critical,
                ["warning_frame_count"] = _asrFrameHitchWarningFrames,
                ["critical_frame_count"] = _asrFrameHitchCriticalFrames,
                ["warning_threshold_ms"] = _frameHitchWarningThresholdMs,
                ["critical_threshold_ms"] = _frameHitchCriticalThresholdMs,
                ["watchdog_duration_ms"] = Mathf.RoundToInt((Time.realtimeSinceStartup - _asrFrameHitchWatchdogStartedAt) * 1000f),
                ["total_asr_latency_ms"] = Math.Max(0L, totalAsrLatencyMs),
                ["platform"] = Application.platform.ToString(),
                ["condition_id"] = _conditionId ?? string.Empty,
                ["trial_id"] = _trialId ?? string.Empty,
                ["round_id"] = _roundId ?? string.Empty
            };
            TiagoExperimentTelemetry.LogEvent("p45e05_asr_frame_hitch_evaluated", payload);
            if (critical)
            {
                Debug.LogWarning(
                    $"{LogPrefix} p45e05_asr_frame_hitch_evaluated | utterance={payload["utterance_id"]} max_unscaled_delta_ms={_asrFrameHitchMaxDeltaMs:0.0} critical=True",
                    this);
            }

            _asrFrameHitchWatchdogActive = false;
            _asrFrameHitchWatchdogUtteranceId = string.Empty;
        }

        private void LogCommandNormalizationDiagnostic(string transcript)
        {
            if (!_enableCommandNormalizationDiagnostics)
            {
                return;
            }

            VoiceCommandNormalizationResult normalized = _commandNormalizer.Normalize(transcript);
            string corrections = normalized.CorrectionsApplied.Count == 0
                ? "none"
                : string.Join("; ", normalized.CorrectionsApplied);

            Debug.Log(
                $"{LogPrefix} Command normalization diagnostic | raw='{normalized.RawTranscript}' cleaned='{normalized.CleanedTranscript}' normalized='{normalized.NormalizedText}' status={normalized.Status} score={normalized.Score:0.###} action={normalized.Action} object={normalized.Object} object_label={normalized.ObjectLabel} destination_label={normalized.DestinationLabel} canonical='{normalized.CanonicalPhrase}' corrections={corrections} ambiguity='{normalized.AmbiguityReason}'",
                this);
        }

        private WhisperModelRuntimeInfo ResolveWhisperModelInfo()
        {
            return WhisperModelConfiguration.ResolveRuntimeInfo(
                _whisperManager,
                _diagnosticModelSize,
                _diagnosticOverrideWhisperModel);
        }

        private bool RequiresWhisperBaseReadinessGate()
        {
            if (!ExperimentDataPathResolver.IsAndroidRuntime())
            {
                return false;
            }

            if (_backend == ASRBackend.Auto || _backend == ASRBackend.SherpaOnnx)
            {
                return false;
            }

            if (_backend != ASRBackend.Auto && _backend != ASRBackend.WhisperUnity)
            {
                return false;
            }

            WhisperModelRuntimeInfo modelInfo = ResolveWhisperModelInfo();
            return modelInfo.RequestedModelSize == ASRModelSize.Base ||
                modelInfo.EffectiveModelSize == ASRModelSize.Base;
        }

        private Dictionary<string, object> BuildWhisperReadinessPayload(
            string reason,
            float elapsedSeconds,
            WhisperModelLoadState loadState)
        {
            WhisperModelRuntimeInfo modelInfo = ResolveWhisperModelInfo();
            WhisperManagerPathState pathState = WhisperModelConfiguration.ResolveManagerPathState(_whisperManager);
            return new Dictionary<string, object>
            {
                ["reason"] = reason ?? string.Empty,
                ["requested_model"] = modelInfo.RequestedModelSize.ToString(),
                ["effective_model"] = modelInfo.EffectiveModelSize.ToString(),
                ["effective_model_path"] = modelInfo.EffectiveModelPath,
                ["raw_persistent_path"] = pathState.RawPersistentPath,
                ["manager_model_path"] = pathState.ModelPathForManager,
                ["is_model_path_in_streaming_assets"] = pathState.IsModelPathInStreamingAssets,
                ["file_exists"] = pathState.FileExists,
                ["file_size"] = pathState.FileSize,
                ["load_state_model_path"] = loadState.ModelPath,
                ["manager_resolved"] = loadState.ManagerResolved,
                ["is_loaded"] = loadState.IsLoaded,
                ["is_loading"] = loadState.IsLoading,
                ["load_state_reason"] = loadState.Reason,
                ["wait_elapsed_seconds"] = elapsedSeconds,
                ["platform"] = Application.platform.ToString(),
                ["scene"] = SceneManager.GetActiveScene().name,
                ["condition_id"] = _conditionId ?? string.Empty,
                ["trial_id"] = _trialId ?? string.Empty,
                ["round_id"] = _roundId ?? string.Empty,
                ["listening_mode"] = _listeningMode.ToString()
            };
        }

        private void LogWhisperReadinessWaitStarted(
            string reason,
            WhisperModelRuntimeInfo modelInfo,
            WhisperModelLoadState loadState,
            string ensureReason)
        {
            Dictionary<string, object> payload = BuildWhisperReadinessPayload(reason, 0f, loadState);
            payload["ensure_loading_reason"] = ensureReason ?? string.Empty;
            payload["timeout_seconds"] = AndroidWhisperBaseReadinessTimeoutSeconds;
            payload["continuous_vad_waiting"] = _continuousVadWaitingForWhisperReadiness;
            TiagoExperimentTelemetry.LogEvent("p45d02_asr_model_load_wait_started", payload);
            Debug.Log(
                $"{LogPrefix} p45d02_asr_model_load_wait_started | requested={modelInfo.RequestedModelSize} effective={modelInfo.EffectiveModelSize} path='{modelInfo.EffectiveModelPath}' loaded={loadState.IsLoaded} loading={loadState.IsLoading} ensure={ensureReason} scene={SceneManager.GetActiveScene().name}",
                this);
        }

        private void LogWhisperReadinessReady(float elapsedSeconds, string reason, WhisperModelLoadState loadState)
        {
            Dictionary<string, object> payload = BuildWhisperReadinessPayload(reason, elapsedSeconds, loadState);
            TiagoExperimentTelemetry.LogEvent("p45d02_asr_model_load_ready", payload);
            Debug.Log(
                $"{LogPrefix} p45d02_asr_model_load_ready | elapsed={elapsedSeconds:0.###}s path='{loadState.ModelPath}' scene={SceneManager.GetActiveScene().name}",
                this);
        }

        private void LogWhisperReadinessTimeout(float elapsedSeconds, string reason, WhisperModelLoadState loadState)
        {
            Dictionary<string, object> payload = BuildWhisperReadinessPayload(reason, elapsedSeconds, loadState);
            payload["timeout_seconds"] = AndroidWhisperBaseReadinessTimeoutSeconds;
            TiagoExperimentTelemetry.LogEvent("p45d02_asr_model_load_timeout", payload);
            Debug.LogWarning(
                $"{LogPrefix} p45d02_asr_model_load_timeout | elapsed={elapsedSeconds:0.###}s loaded={loadState.IsLoaded} loading={loadState.IsLoading} path='{loadState.ModelPath}' scene={SceneManager.GetActiveScene().name}",
                this);
        }

        private void LogP45D03ModelLoadStarted(string reason, string ensureReason, WhisperModelLoadState loadState)
        {
            Dictionary<string, object> payload = BuildWhisperReadinessPayload(reason, 0f, loadState);
            payload["ensure_loading_reason"] = ensureReason ?? string.Empty;
            TiagoExperimentTelemetry.LogEvent("p45d03_asr_model_load_started", payload);
            Debug.Log(
                $"{LogPrefix} p45d03_asr_model_load_started | reason={reason} ensure={ensureReason} manager='{payload["manager_model_path"]}' raw='{payload["raw_persistent_path"]}' loaded={loadState.IsLoaded} loading={loadState.IsLoading} scene={SceneManager.GetActiveScene().name}",
                this);
        }

        private void LogP45D03ModelLoadSucceeded(string reason, float elapsedSeconds, WhisperModelLoadState loadState)
        {
            Dictionary<string, object> payload = BuildWhisperReadinessPayload(reason, elapsedSeconds, loadState);
            TiagoExperimentTelemetry.LogEvent("p45d03_asr_model_load_succeeded", payload);
            Debug.Log(
                $"{LogPrefix} p45d03_asr_model_load_succeeded | elapsed={elapsedSeconds:0.###}s manager='{payload["manager_model_path"]}' raw='{payload["raw_persistent_path"]}' loaded={loadState.IsLoaded} loading={loadState.IsLoading} scene={SceneManager.GetActiveScene().name}",
                this);
        }

        private void LogP45D03ModelLoadFailed(string reason, float elapsedSeconds, WhisperModelLoadState loadState, string failureReason)
        {
            Dictionary<string, object> payload = BuildWhisperReadinessPayload(reason, elapsedSeconds, loadState);
            payload["failure_reason"] = failureReason ?? string.Empty;
            TiagoExperimentTelemetry.LogEvent("p45d03_asr_model_load_failed", payload);
            Debug.LogWarning(
                $"{LogPrefix} p45d03_asr_model_load_failed | reason={failureReason} elapsed={elapsedSeconds:0.###}s manager='{payload["manager_model_path"]}' raw='{payload["raw_persistent_path"]}' loaded={loadState.IsLoaded} loading={loadState.IsLoading} scene={SceneManager.GetActiveScene().name}",
                this);
        }

        private void LogTranscriptionBlockedModelNotReady(VoiceVadSegmentDecision decision, string reason)
        {
            WhisperModelLoadState loadState = WhisperModelConfiguration.ResolveLoadState(_whisperManager);
            Dictionary<string, object> payload = BuildWhisperReadinessPayload(reason, 0f, loadState);
            payload["segment_duration_seconds"] = decision.SegmentDuration;
            payload["segment_start_time"] = decision.SegmentStartTime;
            payload["segment_end_time"] = decision.SegmentEndTime;
            TiagoExperimentTelemetry.LogEvent("p45d02_asr_transcription_blocked_model_not_ready", payload);
            Debug.LogWarning(
                $"{LogPrefix} p45d02_asr_transcription_blocked_model_not_ready | reason={reason} loaded={loadState.IsLoaded} loading={loadState.IsLoading} duration={decision.SegmentDuration:0.###} scene={SceneManager.GetActiveScene().name}",
                this);
        }

        private void LogWhisperRuntimeModel(string reason, ASRBackend activeBackend, string utteranceId = "")
        {
            WhisperModelRuntimeInfo modelInfo = ResolveWhisperModelInfo();
            bool whisperUsedForCurrentTranscription =
                activeBackend == ASRBackend.WhisperUnity ||
                activeBackend == ASRBackend.Auto;
            bool requestedBaseButEffectiveTiny =
                modelInfo.RequestedModelSize == ASRModelSize.Base &&
                modelInfo.EffectiveModelSize == ASRModelSize.Tiny;
            bool requestedBaseMissing =
                modelInfo.RequestedModelSize == ASRModelSize.Base &&
                !WhisperModelConfiguration.IsModelAvailable(ASRModelSize.Base);

            if (_enableVoiceExperimentLogging)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "voice_whisper_runtime_model_resolved",
                    new Dictionary<string, object>
                    {
                        ["reason"] = reason ?? string.Empty,
                        ["utterance_id"] = utteranceId ?? string.Empty,
                        ["requested_model_size"] = modelInfo.RequestedModelSize.ToString(),
                        ["effective_model_size"] = modelInfo.EffectiveModelSize.ToString(),
                        ["effective_model_file_name"] = modelInfo.EffectiveModelFileName,
                        ["effective_model_path"] = modelInfo.EffectiveModelPath,
                        ["model_available_at_start"] = modelInfo.ModelAvailableAtStart,
                        ["model_fallback_used"] = modelInfo.ModelFallbackUsed,
                        ["diagnostic_override_enabled"] = _diagnosticOverrideWhisperModel,
                        ["requested_base_missing_warning"] = requestedBaseMissing,
                        ["requested_base_but_effective_tiny_warning"] = requestedBaseButEffectiveTiny,
                        ["active_backend"] = activeBackend.ToString(),
                        ["not_used_for_current_transcription"] = !whisperUsedForCurrentTranscription
                    });
            }

            if (!whisperUsedForCurrentTranscription)
            {
                if (_asrDebugLogging)
                {
                    Debug.Log(
                        $"{LogPrefix} Whisper diagnostic model availability | reason=diagnostic_whisper_available_only active_backend={activeBackend} not_used_for_current_transcription=True utterance={utteranceId} requested={modelInfo.RequestedModelSize} effective={modelInfo.EffectiveModelSize} file='{modelInfo.EffectiveModelFileName}' path='{modelInfo.EffectiveModelPath}' available={modelInfo.ModelAvailableAtStart} model_fallback_used={modelInfo.ModelFallbackUsed}",
                        this);
                }

                return;
            }

            string message =
                $"{LogPrefix} Whisper runtime model | reason={reason} active_backend={activeBackend} not_used_for_current_transcription=False utterance={utteranceId} requested={modelInfo.RequestedModelSize} effective={modelInfo.EffectiveModelSize} file='{modelInfo.EffectiveModelFileName}' path='{modelInfo.EffectiveModelPath}' available={modelInfo.ModelAvailableAtStart} fallback_used={modelInfo.ModelFallbackUsed}";
            if (modelInfo.ModelFallbackUsed || requestedBaseButEffectiveTiny || requestedBaseMissing)
            {
                Debug.LogWarning(message, this);
            }
            else if (_asrDebugLogging)
            {
                Debug.Log(message, this);
            }
        }

        private void ApplyAndroidQuestLoggingDefaults()
        {
            if (!ExperimentDataPathResolver.IsAndroidRuntime())
            {
                return;
            }

            bool changed = _asrDebugLogging || _logMicrophoneDevicesOnStart || _enableCommandNormalizationDiagnostics;
            _asrDebugLogging = false;
            _logMicrophoneDevicesOnStart = false;
            _enableCommandNormalizationDiagnostics = false;
            if (changed)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "p45d_android_asr_verbose_logging_disabled",
                    new Dictionary<string, object>
                    {
                        ["platform"] = Application.platform.ToString(),
                        ["asr_debug_logging"] = _asrDebugLogging,
                        ["log_microphone_devices_on_start"] = _logMicrophoneDevicesOnStart,
                        ["command_normalization_diagnostics"] = _enableCommandNormalizationDiagnostics
                    });
            }

            if ((_backend == ASRBackend.Auto || _backend == ASRBackend.SherpaOnnx) &&
                _listeningMode == VoiceListeningMode.ContinuousVad)
            {
                float previousMinUtterance = _vadMinUtteranceSeconds;
                float previousSilenceTail = _vadSilenceDurationToCloseSeconds;
                float previousPreRoll = _vadPreRollSeconds;
                _vadMinUtteranceSeconds = Mathf.Max(_vadMinUtteranceSeconds, _vadShortCommandMinUtteranceSeconds);
                _vadSilenceDurationToCloseSeconds = Mathf.Max(_vadSilenceDurationToCloseSeconds, 0.85f);
                _vadPreRollSeconds = Mathf.Max(_vadPreRollSeconds, 0.20f);
                if (!Mathf.Approximately(previousMinUtterance, _vadMinUtteranceSeconds) ||
                    !Mathf.Approximately(previousSilenceTail, _vadSilenceDurationToCloseSeconds) ||
                    !Mathf.Approximately(previousPreRoll, _vadPreRollSeconds))
                {
                    TiagoExperimentTelemetry.LogEvent(
                        "p45e05_short_command_vad_defaults_applied",
                        new Dictionary<string, object>
                        {
                            ["platform"] = Application.platform.ToString(),
                            ["backend"] = _backend.ToString(),
                            ["listening_mode"] = _listeningMode.ToString(),
                            ["previous_min_utterance_seconds"] = previousMinUtterance,
                            ["min_utterance_seconds"] = _vadMinUtteranceSeconds,
                            ["previous_silence_tail_seconds"] = previousSilenceTail,
                            ["silence_tail_seconds"] = _vadSilenceDurationToCloseSeconds,
                            ["previous_pre_roll_seconds"] = previousPreRoll,
                            ["pre_roll_seconds"] = _vadPreRollSeconds
                        });
                }
            }
        }

        private void EnsureWhisperModelPathResolvedForRuntime()
        {
            if (ExperimentDataPathResolver.IsAndroidRuntime() &&
                (_backend == ASRBackend.Auto || _backend == ASRBackend.SherpaOnnx))
            {
                return;
            }

            ASRModelSize requestedModel = _diagnosticOverrideWhisperModel && _diagnosticModelSize != ASRModelSize.Unknown
                ? _diagnosticModelSize
                : WhisperModelConfiguration.ResolveModelSize(_whisperManager);
            if (requestedModel == ASRModelSize.Unknown)
            {
                return;
            }

            if (_enableAsrDiagnosticMode && _diagnosticOverrideWhisperModel && _diagnosticModelSize != ASRModelSize.Unknown)
            {
                return;
            }

            if (!ExperimentDataPathResolver.IsAndroidRuntime() && requestedModel != ASRModelSize.Base)
            {
                return;
            }

            if (!WhisperModelConfiguration.TryApplyModel(_whisperManager, requestedModel, out WhisperModelApplyResult result))
            {
                Debug.LogWarning(
                    $"{LogPrefix} Whisper runtime model path not applied | requested_model_size={requestedModel} reason={result.FailedReason}",
                    this);
            }
        }

        private void LogP45DAsrUtteranceStarted(ASRRequest request, string source)
        {
            if (request == null)
            {
                return;
            }

            WhisperModelRuntimeInfo modelInfo = WhisperModelConfiguration.ResolveRuntimeInfo(
                _whisperManager,
                _diagnosticModelSize,
                _diagnosticOverrideWhisperModel);
            TiagoExperimentTelemetry.LogEvent(
                "p45d_asr_utterance_started",
                new Dictionary<string, object>
                {
                    ["utterance_id"] = request.UtteranceId,
                    ["requested_backend"] = request.RequestedBackend.ToString(),
                    ["requested_model"] = modelInfo.RequestedModelSize.ToString(),
                    ["effective_model"] = modelInfo.EffectiveModelSize.ToString(),
                    ["effective_model_path"] = modelInfo.EffectiveModelPath,
                    ["source"] = source ?? string.Empty,
                    ["platform"] = Application.platform.ToString(),
                    ["condition_id"] = _conditionId ?? string.Empty,
                    ["trial_id"] = _trialId ?? string.Empty,
                    ["round_id"] = _roundId ?? string.Empty
                });
        }

        private void LogP45DAsrUtteranceCompleted(
            ASRResult result,
            long inferenceLatencyMs,
            string source,
            VoiceAutonomyCommandRoutingResult routing)
        {
            LogP45DAsrUtteranceResult("p45d_asr_utterance_completed", result, inferenceLatencyMs, source, routing, string.Empty);
        }

        private void LogP45DAsrUtteranceFailed(ASRResult result, long inferenceLatencyMs, string source)
        {
            LogP45DAsrUtteranceResult("p45d_asr_utterance_failed", result, inferenceLatencyMs, source, null, result?.ErrorReason ?? "unknown_error");
        }

        private void LogP45DAsrUtteranceResult(
            string eventType,
            ASRResult result,
            long inferenceLatencyMs,
            string source,
            VoiceAutonomyCommandRoutingResult routing,
            string failureReason)
        {
            result ??= ASRResult.Failed(ASRUtteranceId.Create(), _backend, failureReason, _language, 0L, _microphoneDevice, _sampleRate);
            WhisperModelRuntimeInfo modelInfo = WhisperModelConfiguration.ResolveRuntimeInfo(
                _whisperManager,
                _diagnosticModelSize,
                _diagnosticOverrideWhisperModel);
            long durationMs = result.AudioAnalysis.RecordedDurationMs > 0 ? result.AudioAnalysis.RecordedDurationMs : result.DurationMs;
            long totalLatencyMs = Math.Max(0L, inferenceLatencyMs);
            TiagoExperimentTelemetry.LogEvent(
                eventType,
                new Dictionary<string, object>
                {
                    ["utterance_id"] = result.UtteranceId,
                    ["backend"] = result.Backend.ToString(),
                    ["requested_model"] = modelInfo.RequestedModelSize.ToString(),
                    ["effective_model"] = modelInfo.EffectiveModelSize.ToString(),
                    ["effective_model_file_name"] = modelInfo.EffectiveModelFileName,
                    ["effective_model_path"] = modelInfo.EffectiveModelPath,
                    ["fallback_used"] = modelInfo.ModelFallbackUsed,
                    ["audio_duration_ms"] = durationMs,
                    ["audio_duration_seconds"] = durationMs / 1000f,
                    ["inference_latency_ms"] = Math.Max(0L, inferenceLatencyMs),
                    ["total_after_close_latency_ms"] = totalLatencyMs,
                    ["transcript"] = result.Transcript,
                    ["intent_result"] = routing?.Mapping?.IntentKind.ToString() ?? string.Empty,
                    ["routing_status"] = routing?.Status.ToString() ?? string.Empty,
                    ["routing_submitted"] = routing?.Submitted ?? false,
                    ["routing_reason"] = routing?.Reason ?? string.Empty,
                    ["error"] = !result.Success,
                    ["timeout"] = (result.ErrorReason ?? string.Empty).IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0,
                    ["failure_reason"] = failureReason ?? string.Empty,
                    ["platform"] = Application.platform.ToString(),
                    ["condition_id"] = _conditionId ?? string.Empty,
                    ["trial_id"] = _trialId ?? string.Empty,
                    ["round_id"] = _roundId ?? string.Empty,
                    ["source"] = source ?? string.Empty
                });
        }

        private void LogP45EBackendSelection(ASRRequest request, ASRResult result, string source)
        {
            ASRBackendSelection selection = _microphoneService != null ? _microphoneService.LastBackendSelection : null;
            ASRBackendPreflightResult preflight = _microphoneService != null ? _microphoneService.LastBackendPreflight : null;
            ASRBackend requested = request?.RequestedBackend ?? selection?.RequestedBackend ?? _backend;
            ASRBackend effective = selection?.EffectiveBackend ?? result?.Backend ?? ASRBackend.Unsupported;
            Dictionary<string, object> payload = BuildP45EBasePayload(
                requested,
                effective,
                preflight,
                result?.UtteranceId ?? request?.UtteranceId ?? string.Empty,
                source);
            payload["backend_selection_reason"] = selection?.Reason ?? string.Empty;
            payload["fallback_used"] = selection?.FallbackUsed ?? false;
            payload["availability"] = selection != null && selection.IsAvailable ? "available" : "unavailable";
            payload["error"] = result != null && !result.Success ? result.ErrorReason : string.Empty;
            TiagoExperimentTelemetry.LogEvent("p45e01_asr_backend_selected", payload);

            if (preflight != null)
            {
                Dictionary<string, object> preflightPayload = BuildP45EBasePayload(
                    requested,
                    effective,
                    preflight,
                    result?.UtteranceId ?? request?.UtteranceId ?? string.Empty,
                    source);
                preflightPayload["preflight_availability"] = preflight.Availability.ToString();
                preflightPayload["preflight_reason"] = preflight.Reason;
                preflightPayload["model_path"] = preflight.ModelPath;
                preflightPayload["model_main_file_size_bytes"] = preflight.ModelMainFileSizeBytes;
                preflightPayload["model_layout"] = preflight.ModelLayout;
                preflightPayload["model_files"] = preflight.ModelFilesReport;
                preflightPayload["runtime_path"] = preflight.RuntimePath;
                preflightPayload["plugin_path"] = preflight.RuntimePath;
                preflightPayload["runtime_files"] = preflight.RuntimeFilesReport;
                preflightPayload["runtime_binding_type"] = preflight.RuntimeBindingType;
                preflightPayload["architecture_abi"] = preflight.ArchitectureAbi;
                preflightPayload["android_compatible"] = preflight.AndroidCompatible;
                preflightPayload["language"] = preflight.Language;
                preflightPayload["language_compatible"] = preflight.LanguageCompatible;
                preflightPayload["error"] = preflight.Error;
                TiagoExperimentTelemetry.LogEvent("p45e01_asr_backend_preflight", preflightPayload);
                if (preflight.EffectiveBackend == ASRBackend.SherpaOnnx)
                {
                    Dictionary<string, object> p45e03Payload = BuildP45E03Payload(preflightPayload, preflight, result, 0L);
                    LogP45E03Event("p45e03_sherpa_runtime_preflight", p45e03Payload);
                    LogP45E03Event(
                        preflight.IsAvailable
                            ? "p45e03_sherpa_runtime_loaded"
                            : "p45e03_sherpa_runtime_missing",
                        p45e03Payload);
                    Dictionary<string, object> p45e04Payload = BuildP45E04Payload(p45e03Payload, preflight, result, 0L);
                    LogP45E04Event("p45e04_sherpa_android_runtime_preflight", p45e04Payload);
                    LogP45E04Event(
                        HasSherpaRuntimeCandidate(preflight)
                            ? "p45e04_sherpa_android_runtime_detected"
                            : "p45e04_sherpa_android_runtime_missing",
                        p45e04Payload);
                    LogP45E05Event(
                        HasSherpaRuntimeCandidate(preflight)
                            ? "p45e05_sherpa_runtime_files_ok"
                            : "p45e05_sherpa_android_runtime_missing",
                        p45e04Payload);
                    LogP45E04Event(
                        HasSherpaBridgeCandidate(preflight)
                            ? "p45e04_sherpa_android_bridge_detected"
                            : "p45e04_sherpa_android_bridge_missing",
                        p45e04Payload);
                    LogP45E05Event(
                        HasSherpaBridgeCandidate(preflight)
                            ? "p45e05_sherpa_android_bridge_detected"
                            : "p45e05_sherpa_android_bridge_missing",
                        p45e04Payload);
                    if (!preflight.IsAvailable && preflight.Reason.IndexOf("recognizer", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        LogP45E03Event("p45e03_sherpa_recognizer_create_failed", p45e03Payload);
                        LogP45E04Event("p45e04_sherpa_recognizer_create_failed", p45e04Payload);
                        LogP45E05Event("p45e05_sherpa_recognizer_create_failed", p45e04Payload);
                    }
                }

                if (!preflight.IsAvailable &&
                    preflight.EffectiveBackend == ASRBackend.SherpaOnnx &&
                    preflight.Error.IndexOf(SherpaOnnxASRBackend.MissingArtifactsTag, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TiagoExperimentTelemetry.LogEvent("p45e01_sherpa_missing_artifacts", preflightPayload);
                }
            }

            TiagoExperimentTelemetry.LogEvent(
                selection != null && selection.IsAvailable
                    ? "p45e01_asr_backend_ready"
                    : "p45e01_asr_backend_unavailable",
                payload);

            if (effective == ASRBackend.SherpaOnnx && result != null && result.Success)
            {
                TiagoExperimentTelemetry.LogEvent("p45e01_sherpa_utterance_started", payload);
            }
        }

        private void LogP45EAsrUtteranceCompleted(
            ASRResult result,
            long inferenceLatencyMs,
            string source,
            VoiceAutonomyCommandRoutingResult routing)
        {
            Dictionary<string, object> payload = BuildP45EResultPayload(result, inferenceLatencyMs, source, routing);
            TiagoExperimentTelemetry.LogEvent("p45e01_asr_backend_result_normalized", payload);
            TiagoExperimentTelemetry.LogEvent("p45e01_asr_latency_evaluated", payload);
            if (result != null && result.Backend == ASRBackend.SherpaOnnx)
            {
                TiagoExperimentTelemetry.LogEvent("p45e01_sherpa_utterance_completed", payload);
                LogP45E03Event("p45e03_sherpa_recognizer_created", BuildP45E03Payload(payload, _microphoneService?.LastBackendPreflight, result, inferenceLatencyMs));
                LogP45E03Event("p45e03_sherpa_transcription_completed", BuildP45E03Payload(payload, _microphoneService?.LastBackendPreflight, result, inferenceLatencyMs));
                LogP45E04Event("p45e04_sherpa_recognizer_created", BuildP45E04Payload(payload, _microphoneService?.LastBackendPreflight, result, inferenceLatencyMs));
                LogP45E04Event("p45e04_sherpa_transcription_completed", BuildP45E04Payload(payload, _microphoneService?.LastBackendPreflight, result, inferenceLatencyMs));
                LogP45E05Event("p45e05_sherpa_recognizer_created", BuildP45E04Payload(payload, _microphoneService?.LastBackendPreflight, result, inferenceLatencyMs));
                LogP45E05Event("p45e05_sherpa_transcription_completed", BuildP45E04Payload(payload, _microphoneService?.LastBackendPreflight, result, inferenceLatencyMs));
            }
        }

        private void LogP45EAsrUtteranceFailed(
            ASRResult result,
            long inferenceLatencyMs,
            string source)
        {
            Dictionary<string, object> payload = BuildP45EResultPayload(result, inferenceLatencyMs, source, null);
            TiagoExperimentTelemetry.LogEvent("p45e01_asr_latency_evaluated", payload);
            ASRBackendPreflightResult preflight = _microphoneService?.LastBackendPreflight;
            bool sherpaFailure = result != null && result.Backend == ASRBackend.SherpaOnnx;
            bool sherpaPreflightFailure = preflight != null && preflight.EffectiveBackend == ASRBackend.SherpaOnnx && !preflight.IsAvailable;
            bool sherpaArtifactFailure = result != null &&
                result.ErrorReason.IndexOf(SherpaOnnxASRBackend.MissingArtifactsTag, StringComparison.OrdinalIgnoreCase) >= 0;
            if (sherpaFailure || sherpaPreflightFailure || sherpaArtifactFailure)
            {
                TiagoExperimentTelemetry.LogEvent("p45e01_sherpa_utterance_failed", payload);
                LogP45E03Event("p45e03_sherpa_transcription_failed", BuildP45E03Payload(payload, preflight, result, inferenceLatencyMs));
                LogP45E04Event("p45e04_sherpa_transcription_failed", BuildP45E04Payload(payload, preflight, result, inferenceLatencyMs));
                LogP45E05Event("p45e05_sherpa_transcription_failed", BuildP45E04Payload(payload, preflight, result, inferenceLatencyMs));
            }
        }

        private Dictionary<string, object> BuildP45EResultPayload(
            ASRResult result,
            long inferenceLatencyMs,
            string source,
            VoiceAutonomyCommandRoutingResult routing)
        {
            result ??= ASRResult.Failed(ASRUtteranceId.Create(), _backend, "p45e01_null_asr_result", _language, 0L, _microphoneDevice, _sampleRate);
            ASRBackendPreflightResult preflight = _microphoneService != null ? _microphoneService.LastBackendPreflight : null;
            ASRBackendSelection selection = _microphoneService != null ? _microphoneService.LastBackendSelection : null;
            Dictionary<string, object> payload = BuildP45EBasePayload(
                selection?.RequestedBackend ?? _backend,
                result.Backend,
                preflight,
                result.UtteranceId,
                source);
            VoiceCommandNormalizationResult normalization = routing?.Normalization;
            VoiceCommandIntentMappingResult mapping = routing?.Mapping;
            long audioDurationMs = result.AudioAnalysis.RecordedDurationMs > 0 ? result.AudioAnalysis.RecordedDurationMs : result.DurationMs;
            long safeLatencyMs = Math.Max(0L, inferenceLatencyMs);
            payload["audio_duration_ms"] = audioDurationMs;
            payload["inference_latency_ms"] = safeLatencyMs;
            payload["total_latency_ms"] = safeLatencyMs;
            payload["partial_text"] = string.Empty;
            payload["final_text"] = result.Transcript;
            payload["normalized_text"] = normalization?.NormalizedText ?? mapping?.NormalizedText ?? string.Empty;
            payload["intent"] = mapping?.IntentKind.ToString() ?? string.Empty;
            payload["routing_status"] = routing?.Status.ToString() ?? string.Empty;
            payload["routing_reason"] = routing?.Reason ?? string.Empty;
            payload["error"] = result.Success ? string.Empty : result.ErrorReason;
            payload["viability_status"] = ASRLatencyViability.Evaluate(safeLatencyMs).ToString().ToLowerInvariant();
            return payload;
        }

        private Dictionary<string, object> BuildP45EBasePayload(
            ASRBackend requestedBackend,
            ASRBackend effectiveBackend,
            ASRBackendPreflightResult preflight,
            string utteranceId,
            string source)
        {
            return new Dictionary<string, object>
            {
                ["asr_backend_requested"] = requestedBackend.ToString(),
                ["asr_backend_effective"] = effectiveBackend.ToString(),
                ["asr_model"] = ResolveP45EAsrModel(effectiveBackend, preflight),
                ["platform"] = Application.platform.ToString(),
                ["scene"] = SceneManager.GetActiveScene().name,
                ["condition_id"] = _conditionId ?? string.Empty,
                ["trial_id"] = _trialId ?? string.Empty,
                ["round_id"] = _roundId ?? string.Empty,
                ["utterance_id"] = utteranceId ?? string.Empty,
                ["source"] = source ?? string.Empty,
                ["listening_mode"] = _listeningMode.ToString()
            };
        }

        private Dictionary<string, object> BuildP45E03Payload(
            Dictionary<string, object> source,
            ASRBackendPreflightResult preflight,
            ASRResult result,
            long inferenceLatencyMs)
        {
            Dictionary<string, object> payload = new(source ?? new Dictionary<string, object>());
            string requested = payload.TryGetValue("asr_backend_requested", out object requestedValue)
                ? requestedValue?.ToString() ?? string.Empty
                : _backend.ToString();
            string effective = payload.TryGetValue("asr_backend_effective", out object effectiveValue)
                ? effectiveValue?.ToString() ?? string.Empty
                : result?.Backend.ToString() ?? ASRBackend.Unsupported.ToString();
            string modelName = !string.IsNullOrWhiteSpace(preflight?.ModelName)
                ? preflight.ModelName
                : payload.TryGetValue("asr_model", out object modelValue)
                    ? modelValue?.ToString() ?? string.Empty
                    : string.Empty;

            payload["backend_requested"] = requested;
            payload["backend_effective"] = effective;
            payload["model_name"] = modelName;
            payload["model_layout"] = preflight?.ModelLayout ?? ReadPayloadString(payload, "model_layout");
            payload["model_path"] = preflight?.ModelPath ?? ReadPayloadString(payload, "model_path");
            payload["runtime_path"] = preflight?.RuntimePath ?? ReadPayloadString(payload, "runtime_path");
            payload["plugin_path"] = preflight?.RuntimePath ?? ReadPayloadString(payload, "plugin_path");
            payload["runtime_files"] = preflight?.RuntimeFilesReport ?? ReadPayloadString(payload, "runtime_files");
            payload["runtime_binding_type"] = preflight?.RuntimeBindingType ?? ReadPayloadString(payload, "runtime_binding_type");
            payload["architecture"] = preflight?.ArchitectureAbi ?? ReadPayloadString(payload, "architecture_abi");
            payload["architecture_abi"] = payload["architecture"];
            long audioDurationMs = 0L;
            if (result != null)
            {
                audioDurationMs = result.AudioAnalysis.RecordedDurationMs > 0
                    ? result.AudioAnalysis.RecordedDurationMs
                    : result.DurationMs;
            }
            else if (payload.TryGetValue("audio_duration_ms", out object audioDurationValue) &&
                     long.TryParse(audioDurationValue?.ToString(), out long parsedAudioDurationMs))
            {
                audioDurationMs = parsedAudioDurationMs;
            }

            payload["audio_duration_ms"] = audioDurationMs;
            payload["inference_latency_ms"] = Math.Max(0L, inferenceLatencyMs);
            payload["total_latency_ms"] = Math.Max(0L, inferenceLatencyMs);
            payload["transcript"] = result?.Transcript ?? ReadPayloadString(payload, "final_text");
            payload["error_reason"] = result != null && !result.Success
                ? result.ErrorReason
                : preflight != null && !preflight.IsAvailable
                    ? preflight.Error
                    : ReadPayloadString(payload, "error");
            payload["fallback_used"] = payload.TryGetValue("fallback_used", out object fallbackValue) && fallbackValue is bool fallback && fallback;
            return payload;
        }

        private static string ReadPayloadString(Dictionary<string, object> payload, string key)
        {
            return payload != null && payload.TryGetValue(key, out object value)
                ? value?.ToString() ?? string.Empty
                : string.Empty;
        }

        private void LogP45E03Event(string eventName, Dictionary<string, object> payload)
        {
            Dictionary<string, object> safePayload = payload ?? new Dictionary<string, object>();
            TiagoExperimentTelemetry.LogEvent(eventName, safePayload);
            Debug.Log(
                $"{LogPrefix} {eventName} | requested_backend={ReadPayloadString(safePayload, "backend_requested")} effective_backend={ReadPayloadString(safePayload, "backend_effective")} model_name='{ReadPayloadString(safePayload, "model_name")}' model_layout={ReadPayloadString(safePayload, "model_layout")} model_path='{ReadPayloadString(safePayload, "model_path")}' model_files='{ReadPayloadString(safePayload, "model_files")}' runtime_path='{ReadPayloadString(safePayload, "runtime_path")}' runtime_files='{ReadPayloadString(safePayload, "runtime_files")}' runtime_binding_type='{ReadPayloadString(safePayload, "runtime_binding_type")}' architecture_abi='{ReadPayloadString(safePayload, "architecture_abi")}' error_reason='{ReadPayloadString(safePayload, "error_reason")}' fallback_used={ReadPayloadString(safePayload, "fallback_used")} platform={ReadPayloadString(safePayload, "platform")}",
                this);
        }

        private Dictionary<string, object> BuildP45E04Payload(
            Dictionary<string, object> source,
            ASRBackendPreflightResult preflight,
            ASRResult result,
            long inferenceLatencyMs)
        {
            Dictionary<string, object> payload = BuildP45E03Payload(source, preflight, result, inferenceLatencyMs);
            payload["bridge_class"] = !string.IsNullOrWhiteSpace(preflight?.RuntimeBindingType)
                ? preflight.RuntimeBindingType
                : SherpaOnnxBackendOptions.DefaultAndroidRecognizerClassName;
            payload["sample_rate"] = result != null && result.SampleRate > 0 ? result.SampleRate : _sampleRate;
            payload["runtime_path"] = preflight?.RuntimePath ?? ReadPayloadString(payload, "runtime_path");
            payload["runtime_files"] = preflight?.RuntimeFilesReport ?? ReadPayloadString(payload, "runtime_files");
            payload["platform"] = Application.platform.ToString();
            payload["fallback_used"] = false;
            return payload;
        }

        private static bool HasSherpaRuntimeCandidate(ASRBackendPreflightResult preflight)
        {
            if (preflight == null)
            {
                return false;
            }

            string report = preflight.RuntimeFilesReport ?? string.Empty;
            return ContainsExistingRuntimeEntry(report, "libsherpa-onnx-jni.so") ||
                ContainsExistingRuntimeEntry(report, "libonnxruntime.so") ||
                ContainsExistingRuntimeEntry(report, ".aar") ||
                ContainsExistingRuntimeEntry(report, ".jar");
        }

        private static bool HasSherpaBridgeCandidate(ASRBackendPreflightResult preflight)
        {
            if (preflight == null)
            {
                return false;
            }

            string reason = preflight.Reason ?? string.Empty;
            if (reason.Equals("runtime_binding_missing", StringComparison.OrdinalIgnoreCase) ||
                reason.Equals("jni_bridge_missing", StringComparison.OrdinalIgnoreCase) ||
                reason.Equals("android_bridge_missing", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string report = preflight.RuntimeFilesReport ?? string.Empty;
            return !string.IsNullOrWhiteSpace(preflight.RuntimeBindingType) ||
                ContainsExistingRuntimeEntry(report, ".aar") ||
                ContainsExistingRuntimeEntry(report, ".jar");
        }

        private static bool ContainsExistingRuntimeEntry(string report, string marker)
        {
            if (string.IsNullOrWhiteSpace(report) || string.IsNullOrWhiteSpace(marker))
            {
                return false;
            }

            string[] entries = report.Split(',');
            foreach (string entry in entries)
            {
                if (entry.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (entry.IndexOf(":missing", StringComparison.OrdinalIgnoreCase) < 0 &&
                    entry.IndexOf(":missing_path", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void LogP45E04Event(string eventName, Dictionary<string, object> payload)
        {
            Dictionary<string, object> safePayload = payload ?? new Dictionary<string, object>();
            TiagoExperimentTelemetry.LogEvent(eventName, safePayload);
            Debug.Log(
                $"{LogPrefix} {eventName} | requested_backend={ReadPayloadString(safePayload, "backend_requested")} effective_backend={ReadPayloadString(safePayload, "backend_effective")} model_name='{ReadPayloadString(safePayload, "model_name")}' model_layout={ReadPayloadString(safePayload, "model_layout")} model_path='{ReadPayloadString(safePayload, "model_path")}' model_files='{ReadPayloadString(safePayload, "model_files")}' runtime_source='{ReadPayloadString(safePayload, "runtime_source")}' runtime_source_detail='{ReadPayloadString(safePayload, "runtime_source_detail")}' runtime_path='{ReadPayloadString(safePayload, "runtime_path")}' runtime_files='{ReadPayloadString(safePayload, "runtime_files")}' bridge_class='{ReadPayloadString(safePayload, "bridge_class")}' runtime_binding_type='{ReadPayloadString(safePayload, "runtime_binding_type")}' architecture_abi='{ReadPayloadString(safePayload, "architecture_abi")}' platform={ReadPayloadString(safePayload, "platform")} sample_rate={ReadPayloadString(safePayload, "sample_rate")} audio_duration_ms={ReadPayloadString(safePayload, "audio_duration_ms")} main_thread_audio_extract_ms={ReadPayloadString(safePayload, "main_thread_audio_extract_ms")} main_thread_resample_ms={ReadPayloadString(safePayload, "main_thread_resample_ms")} queue_wait_ms={ReadPayloadString(safePayload, "queue_wait_ms")} accept_waveform_ms={ReadPayloadString(safePayload, "accept_waveform_ms")} decode_ms={ReadPayloadString(safePayload, "decode_ms")} get_result_ms={ReadPayloadString(safePayload, "get_result_ms")} inference_latency_ms={ReadPayloadString(safePayload, "inference_latency_ms")} worker_decode_ms={ReadPayloadString(safePayload, "worker_decode_ms")} worker_thread_used={ReadPayloadString(safePayload, "worker_thread_used")} decode_thread_mode={ReadPayloadString(safePayload, "decode_thread_mode")} recognizer_reused={ReadPayloadString(safePayload, "recognizer_reused")} jni_thread_attached={ReadPayloadString(safePayload, "jni_thread_attached")} main_thread_block_ms={ReadPayloadString(safePayload, "main_thread_block_ms")} total_latency_ms={ReadPayloadString(safePayload, "total_latency_ms")} transcript='{ReadPayloadString(safePayload, "transcript")}' error_reason='{ReadPayloadString(safePayload, "error_reason")}' fallback_used={ReadPayloadString(safePayload, "fallback_used")}",
                this);
        }

        private void LogP45E05Event(string eventName, Dictionary<string, object> payload)
        {
            LogP45E04Event(eventName, payload);
        }

        private string ResolveP45EAsrModel(ASRBackend backend, ASRBackendPreflightResult preflight)
        {
            if (backend == ASRBackend.SherpaOnnx || preflight?.EffectiveBackend == ASRBackend.SherpaOnnx)
            {
                return !string.IsNullOrWhiteSpace(preflight?.ModelName)
                    ? preflight.ModelName
                    : _sherpaOnnxOptions?.ModelName ?? SherpaOnnxBackendOptions.DefaultModelName;
            }

            if (backend == ASRBackend.WhisperUnity ||
                (_backend == ASRBackend.WhisperUnity && backend == ASRBackend.Unsupported))
            {
                return WhisperModelConfiguration.ResolveModelName(_whisperManager);
            }

            if (backend == ASRBackend.ManualStub || backend == ASRBackend.DiagnosticStub)
            {
                return "diagnostic_stub";
            }

            return "unknown";
        }

        private VoiceAutonomyCommandRoutingResult ProcessAutonomyCommand(
            string transcript,
            string commandSource = "voice_command",
            string voiceInteractionId = "",
            bool forceExperimentPauseRejection = false)
        {
            TryResolveAutonomyCommandConnector(createIfMissing: true);
            if (_autonomyCommandConnector == null)
            {
                Debug.LogWarning($"{LogPrefix} autonomy_command_connector_missing | transcript was not routed to the multimodal bridge", this);
                return null;
            }

            Debug.Log($"{LogPrefix} autonomy_command_connector_present | routing transcript='{transcript}' connector='{_autonomyCommandConnector.name}'", this);
            string effectiveVoiceInteractionId = string.IsNullOrWhiteSpace(voiceInteractionId)
                ? _activeVoiceInteractionId
                : voiceInteractionId;
            VoiceAutonomyCommandRoutingResult result = _autonomyCommandConnector.ProcessFinalTranscript(
                transcript,
                commandSource,
                effectiveVoiceInteractionId,
                forceExperimentPauseRejection);
            Debug.Log($"{LogPrefix} routing_result | status={result.Status} reason='{result.Reason}' submitted={result.Submitted} intent_kind={result.Mapping?.IntentKind.ToString() ?? string.Empty}", this);
            return result;
        }

        private void RecordAsrDiagnosticUtterance(ASRResult result, VoiceVadSegmentDecision? vadDecision, long asrLatencyMs)
        {
            if (!_enableAsrDiagnosticMode)
            {
                return;
            }

            TryResolveAsrDiagnosticRecorder(createIfMissing: true);
            if (_asrDiagnosticRecorder == null)
            {
                Debug.LogWarning($"{LogPrefix} ASR diagnostic recorder missing | utterance={result?.UtteranceId}", this);
                return;
            }

            result ??= ASRResult.Failed(ASRUtteranceId.Create(), _backend, "diagnostic_null_asr_result", _language, 0L, _microphoneDevice, _sampleRate);
            Stopwatch normalizationStopwatch = Stopwatch.StartNew();
            VoiceCommandNormalizationResult normalization = _commandNormalizer.Normalize(result.Transcript);
            normalizationStopwatch.Stop();

            Stopwatch mappingStopwatch = Stopwatch.StartNew();
            VoiceCommandIntentMappingResult mapping = _diagnosticIntentMapper.Map(normalization);
            mappingStopwatch.Stop();

            string targetAlias = ResolveDiagnosticTargetAlias(normalization, mapping);
            string destination = ResolveDiagnosticDestination(normalization, mapping);
            long effectiveAsrLatencyMs = Math.Max(0L, asrLatencyMs);
            WhisperModelRuntimeInfo modelInfo = WhisperModelConfiguration.ResolveRuntimeInfo(
                _whisperManager,
                _diagnosticModelSize,
                _diagnosticOverrideWhisperModel);
            ASRBackendPreflightResult backendPreflight = _microphoneService != null ? _microphoneService.LastBackendPreflight : null;
            bool sherpaResult = result.Backend == ASRBackend.SherpaOnnx || backendPreflight?.EffectiveBackend == ASRBackend.SherpaOnnx;
            AsrDiagnosticRecord record = new()
            {
                UtteranceId = result.UtteranceId,
                TimestampEnd = DateTime.Now.ToString("O"),
                TimestampStart = DateTime.Now.AddMilliseconds(-Math.Max(0L, result.DurationMs)).ToString("O"),
                DurationMs = result.AudioAnalysis.RecordedDurationMs > 0 ? result.AudioAnalysis.RecordedDurationMs : result.DurationMs,
                VadStartTime = vadDecision.HasValue ? vadDecision.Value.SegmentStartTime : float.NaN,
                VadEndTime = vadDecision.HasValue ? vadDecision.Value.SegmentEndTime : float.NaN,
                SilenceDurationBeforeClose = vadDecision.HasValue ? _vadSilenceDurationToCloseSeconds : float.NaN,
                SampleRate = result.AudioAnalysis.Frequency > 0 ? result.AudioAnalysis.Frequency : result.SampleRate,
                ChannelCount = result.AudioAnalysis.Channels,
                ClipPath = result.DiagnosticAudioPath,
                ModelName = sherpaResult ? backendPreflight?.ModelName ?? _sherpaOnnxOptions?.ModelName ?? SherpaOnnxBackendOptions.DefaultModelName : WhisperModelConfiguration.ResolveModelName(_whisperManager),
                ModelSize = sherpaResult ? ASRModelSize.Unknown : ResolveDiagnosticModelSize(),
                ModelFileName = sherpaResult ? string.Empty : WhisperModelConfiguration.ResolveModelFileName(_whisperManager),
                ModelPath = sherpaResult ? backendPreflight?.ModelPath ?? _sherpaOnnxOptions?.BuildStreamingAssetsRelativeModelPath() ?? string.Empty : WhisperModelConfiguration.ResolveModelPath(_whisperManager),
                DiagnosticModelOverrideEnabled = !sherpaResult && _diagnosticOverrideWhisperModel,
                RequestedModelSize = sherpaResult ? ASRModelSize.Unknown : modelInfo.RequestedModelSize,
                EffectiveModelSize = sherpaResult ? ASRModelSize.Unknown : modelInfo.EffectiveModelSize,
                EffectiveModelFileName = sherpaResult ? string.Empty : modelInfo.EffectiveModelFileName,
                EffectiveModelPath = sherpaResult ? backendPreflight?.ModelPath ?? _sherpaOnnxOptions?.BuildStreamingAssetsRelativeModelPath() ?? string.Empty : modelInfo.EffectiveModelPath,
                ModelAvailableAtStart = sherpaResult ? backendPreflight != null && backendPreflight.IsAvailable : modelInfo.ModelAvailableAtStart,
                ModelFallbackUsed = !sherpaResult && modelInfo.ModelFallbackUsed,
                ModelOverrideFailedReason = sherpaResult ? backendPreflight?.Error ?? string.Empty : modelInfo.ModelOverrideFailedReason,
                Language = result.Language,
                RawTranscript = result.Transcript,
                NormalizedText = normalization.NormalizedText,
                IntentKind = mapping.IntentKind.ToString(),
                TargetAlias = targetAlias,
                Destination = destination,
                AsrLatencyMs = effectiveAsrLatencyMs,
                NormalizationLatencyMs = normalizationStopwatch.ElapsedMilliseconds,
                MappingLatencyMs = mappingStopwatch.ElapsedMilliseconds,
                TotalVoicePipelineLatencyMs = effectiveAsrLatencyMs + normalizationStopwatch.ElapsedMilliseconds + mappingStopwatch.ElapsedMilliseconds,
                ConditionId = _conditionId,
                TrialId = _trialId,
                RoundId = _roundId,
                ErrorReason = result.ErrorReason,
                Success = result.Success,
                HasConfidence = result.HasConfidence,
                Confidence = result.Confidence,
                Rms = result.AudioAnalysis.Rms,
                Peak = result.AudioAnalysis.Peak,
                NonSilentSamplePercent = result.AudioAnalysis.NonSilentSamplePercent,
                SilenceDetected = result.AudioAnalysis.SilenceDetected
            };

            _asrDiagnosticRecorder.RecordUtterance(record);
            LogAsrDiagnosticEvent("asr_diagnostic_utterance_saved", record);
            LogAsrDiagnosticEvent("asr_diagnostic_transcription_completed", record, result.Success ? string.Empty : result.ErrorReason);
            LogAsrDiagnosticEvent("asr_diagnostic_mapping_completed", record, mapping.Status.ToString());
            if (!result.Success || mapping.Status == VoiceCommandIntentMappingStatus.NotMapped)
            {
                LogAsrDiagnosticEvent("asr_diagnostic_error", record, string.IsNullOrWhiteSpace(result.ErrorReason) ? mapping.Error.ToString() : result.ErrorReason);
            }
        }

        private ASRModelSize ResolveDiagnosticModelSize()
        {
            if (_diagnosticOverrideWhisperModel && _diagnosticModelSize != ASRModelSize.Unknown)
            {
                return _diagnosticModelSize;
            }

            return WhisperModelConfiguration.ResolveModelSize(_whisperManager);
        }

        private static string ResolveDiagnosticTargetAlias(
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping)
        {
            if (mapping?.TaskIntent != null && !string.IsNullOrWhiteSpace(mapping.TaskIntent.TargetId))
            {
                return mapping.TaskIntent.TargetId;
            }

            if (normalization != null && !string.IsNullOrWhiteSpace(normalization.ObjectLabel))
            {
                return normalization.ObjectLabel;
            }

            return string.Empty;
        }

        private static string ResolveDiagnosticDestination(
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping)
        {
            if (mapping?.TaskIntent != null && !string.IsNullOrWhiteSpace(mapping.TaskIntent.PlaceTargetId))
            {
                return mapping.TaskIntent.PlaceTargetId;
            }

            if (normalization != null && !string.IsNullOrWhiteSpace(normalization.DestinationLabel))
            {
                return normalization.DestinationLabel;
            }

            return string.Empty;
        }

        private void LogAsrDiagnosticEvent(string eventType, AsrDiagnosticRecord record = null, string reason = "")
        {
            if (!_enableAsrDiagnosticMode)
            {
                return;
            }

            TryResolveAsrDiagnosticRecorder(createIfMissing: true);
            _asrDiagnosticRecorder?.LogDiagnosticEvent(eventType, record, reason);
        }

        private void DebugSubmitManualCommand(string operationName, string transcript)
        {
            TryResolveAutonomyCommandConnector(createIfMissing: true);
            string voiceInteractionId = CreateVoiceInteractionId();
            _activeVoiceInteractionId = voiceInteractionId;
            CaptureActiveVoiceInteractionPauseBoundary();
            Debug.Log($"{LogPrefix} {operationName} invoked", this);
            Debug.Log($"{LogPrefix} {operationName} text='{transcript}'", this);
            Debug.Log($"{LogPrefix} {operationName} connector_assigned={_autonomyCommandConnector != null}", this);
            ASRResult debugResult = ASRResult.Successful(
                ASRUtteranceId.Create(),
                ASRBackend.ManualStub,
                transcript,
                _language,
                0L,
                _microphoneDevice,
                _sampleRate);
            LogVoiceEvent("voice_transcription_received", debugResult, "debug_inspector");
            ProcessAutonomyCommand(transcript, "debug_inspector", voiceInteractionId);
            Debug.Log($"{LogPrefix} {operationName} completed", this);
        }

        private void CaptureActiveVoiceInteractionPauseBoundary()
        {
            _activeVoiceInteractionPauseEpoch = ExperimentRuntimePauseCoordinator.PauseEpoch;
            _activeVoiceInteractionStartedWhilePaused = ExperimentRuntimePauseCoordinator.IsExperimentPaused;
        }

        private string ResolveCommandSource(ASRBackend backend)
        {
            if (backend == ASRBackend.ManualStub ||
                backend == ASRBackend.DiagnosticStub ||
                _backend == ASRBackend.ManualStub ||
                _backend == ASRBackend.DiagnosticStub)
            {
                return "manual_stub";
            }

            return _listeningMode == VoiceListeningMode.ContinuousVad
                ? "continuous_vad"
                : "push_to_talk";
        }

        private string CreateVoiceInteractionId()
        {
            _voiceInteractionSequence++;
            string sessionToken = string.IsNullOrWhiteSpace(_sessionId) ? "session" : SanitizeToken(_sessionId);
            return $"{sessionToken}_voice_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{_voiceInteractionSequence:000}";
        }

        private static string SanitizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            char[] chars = value.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private static string ResolveTranscriptionSource(ASRBackend backend, string fallbackSource)
        {
            if (backend == ASRBackend.ManualStub || backend == ASRBackend.DiagnosticStub)
            {
                return "manual_stub";
            }

            if (backend == ASRBackend.WhisperUnity)
            {
                return "whisper";
            }

            if (backend == ASRBackend.SherpaOnnx)
            {
                return "sherpa_onnx";
            }

            return string.IsNullOrWhiteSpace(fallbackSource) ? "real_voice" : fallbackSource;
        }

        private bool HandleInspectorDebugSubmitFlags()
        {
            bool handled = false;

            if (_debugSubmitManualTranscriptNow)
            {
                _debugSubmitManualTranscriptNow = false;
                DebugSubmitManualCommand("InspectorDebugSubmitManualTranscript", _manualTranscript);
                handled = true;
            }

            if (_debugSubmitConfirmationYesNow)
            {
                _debugSubmitConfirmationYesNow = false;
                DebugSubmitManualCommand("InspectorDebugSubmitConfirmationYes", "sí");
                handled = true;
            }

            if (_debugSubmitConfirmationNoNow)
            {
                _debugSubmitConfirmationNoNow = false;
                DebugSubmitManualCommand("InspectorDebugSubmitConfirmationNo", "no");
                handled = true;
            }

            return handled;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            Debug.Log($"{LogPrefix} VoiceRecognitionController focus={hasFocus}", this);
            LogVoiceEvent(
                "voice_focus_changed",
                ASRResult.Successful(
                    ASRUtteranceId.Create(),
                    _backend,
                    string.Empty,
                    _language,
                    0L,
                    _microphoneDevice,
                    _sampleRate),
                ResolveCommandSource(_backend),
                new Dictionary<string, object>
                {
                    ["has_focus"] = hasFocus,
                    ["reason"] = hasFocus ? "focus_recovered" : "focus_lost"
                });
            if (hasFocus)
            {
                _manualStubNoFocusLogged = false;
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            Debug.Log($"{LogPrefix} VoiceRecognitionController pause={pauseStatus}", this);
        }

        private void LogManualStubConnectorState()
        {
            TryResolveAutonomyCommandConnector(createIfMissing: true);
            if (_autonomyCommandConnector == null)
            {
                Debug.LogWarning($"{LogPrefix} ManualStub connector_missing", this);
                return;
            }

            Debug.Log($"{LogPrefix} ManualStub connector_present | connector='{_autonomyCommandConnector.name}' gameObject='{_autonomyCommandConnector.gameObject.name}'", this);
        }

        private void LogMicrophoneDevices()
        {
            string[] devices = UnityMicrophoneASRService.GetMicrophoneDevices();
            if (devices.Length == 0)
            {
                Debug.LogWarning($"{LogPrefix} Microphone.devices is empty. No microphone input is available to Unity.", this);
                return;
            }

            Debug.Log($"{LogPrefix} Microphone.devices count={devices.Length}", this);
            for (int i = 0; i < devices.Length; i++)
            {
                Debug.Log($"{LogPrefix} Microphone.devices[{i}]='{devices[i]}'", this);
            }

            if (string.IsNullOrWhiteSpace(_microphoneDevice))
            {
                Debug.Log($"{LogPrefix} Microphone Device field is empty; Unity default will be used. Current first listed device='{devices[0]}'", this);
                return;
            }

            if (UnityMicrophoneASRService.IsConfiguredMicrophoneAvailable(_microphoneDevice))
            {
                Debug.Log($"{LogPrefix} Configured Microphone Device matches exactly: '{_microphoneDevice}'", this);
            }
            else
            {
                Debug.LogWarning($"{LogPrefix} Configured Microphone Device does not exactly match any Microphone.devices entry: '{_microphoneDevice}'. Clear the field to use default, or copy one device name exactly.", this);
            }
        }

        private string FormatRequestedMicrophone()
        {
            return string.IsNullOrWhiteSpace(_microphoneDevice) ? "<default>" : _microphoneDevice;
        }

        private void RebuildVadSegmenter()
        {
            _vadSegmenter = new VoiceVadSegmenter(
                _vadVoiceRmsThreshold,
                _vadMinVoiceDurationToOpenSeconds,
                _vadSilenceDurationToCloseSeconds,
                _vadMinUtteranceSeconds,
                _vadMaxUtteranceSeconds,
                _vadCooldownSeconds);
        }

        private bool TryReadContinuousVadRms(out float rms)
        {
            rms = 0f;
            if (_continuousVadClip == null)
            {
                return false;
            }

            int microphonePosition = Microphone.GetPosition(_continuousVadStartDevice);
            int frameCount = Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Max(0.01f, _vadRmsWindowSeconds) * _continuousVadClip.frequency),
                1,
                _continuousVadClip.samples);
            float[] samples = ReadCircularClipFrames(_continuousVadClip, microphonePosition - frameCount, frameCount);
            ASRAudioAnalysis analysis = ASRAudioDiagnostics.Analyze(
                samples,
                _continuousVadClip.channels,
                _continuousVadClip.frequency,
                _vadVoiceRmsThreshold,
                Mathf.Max(_vadVoiceRmsThreshold, 0.0001f));
            rms = analysis.Rms;
            return true;
        }

        private bool TryBuildContinuousVadSegmentClip(
            VoiceVadSegmentDecision decision,
            out AudioClip segmentClip,
            out ASRAudioAnalysis analysis)
        {
            segmentClip = null;
            analysis = default;
            if (_continuousVadClip == null)
            {
                return false;
            }

            int microphonePosition = Microphone.GetPosition(_continuousVadStartDevice);
            float captureDuration = Mathf.Max(
                decision.SegmentDuration,
                decision.SegmentDuration + Mathf.Max(0f, _vadPreRollSeconds));
            int frameCount = Mathf.Clamp(
                Mathf.CeilToInt(captureDuration * _continuousVadClip.frequency),
                1,
                _continuousVadClip.samples);
            float[] samples = ReadCircularClipFrames(_continuousVadClip, microphonePosition - frameCount, frameCount);
            analysis = ASRAudioDiagnostics.Analyze(
                samples,
                _continuousVadClip.channels,
                _continuousVadClip.frequency,
                _silenceRmsThreshold,
                Mathf.Max(_silenceRmsThreshold, 0.0001f));

            if (analysis.RecordedDurationMs < Mathf.RoundToInt(_vadMinUtteranceSeconds * 1000f))
            {
                LogP45E05ShortCommandRejected(
                    string.Empty,
                    null,
                    "utterance_too_short_after_capture",
                    decision,
                    analysis);
                Debug.LogWarning($"{LogPrefix} Continuous VAD segment ignored | reason=utterance_too_short_after_capture", this);
                return false;
            }

            segmentClip = AudioClip.Create(
                $"continuous_vad_segment_{ASRUtteranceId.Create()}",
                frameCount,
                _continuousVadClip.channels,
                _continuousVadClip.frequency,
                false);
            segmentClip.SetData(samples, 0);
            return true;
        }

        private static float[] ReadCircularClipFrames(AudioClip clip, int startFrame, int frameCount)
        {
            int safeFrameCount = Mathf.Clamp(frameCount, 0, clip.samples);
            float[] output = new float[safeFrameCount * clip.channels];
            if (safeFrameCount == 0)
            {
                return output;
            }

            int normalizedStart = Mod(startFrame, clip.samples);
            int firstFrameCount = Mathf.Min(safeFrameCount, clip.samples - normalizedStart);
            float[] first = new float[firstFrameCount * clip.channels];
            clip.GetData(first, normalizedStart);
            Array.Copy(first, output, first.Length);

            int remainingFrameCount = safeFrameCount - firstFrameCount;
            if (remainingFrameCount > 0)
            {
                float[] second = new float[remainingFrameCount * clip.channels];
                clip.GetData(second, 0);
                Array.Copy(second, 0, output, first.Length, second.Length);
            }

            return output;
        }

        private static int Mod(int value, int modulo)
        {
            if (modulo <= 0)
            {
                return 0;
            }

            int result = value % modulo;
            return result < 0 ? result + modulo : result;
        }

        private void TryResolveAutonomyCommandConnector(bool createIfMissing)
        {
            if (_autonomyCommandConnector != null)
            {
                return;
            }

            _autonomyCommandConnector = GetComponent<VoiceAutonomyCommandConnector>();
            if (_autonomyCommandConnector == null)
            {
                _autonomyCommandConnector = GetComponentInParent<VoiceAutonomyCommandConnector>();
            }

            if (_autonomyCommandConnector == null)
            {
                _autonomyCommandConnector = FindFirstObjectByType<VoiceAutonomyCommandConnector>();
            }

            if (_autonomyCommandConnector == null && createIfMissing)
            {
                _autonomyCommandConnector = gameObject.AddComponent<VoiceAutonomyCommandConnector>();
            }
        }

        private void TryResolveAsrDiagnosticRecorder(bool createIfMissing)
        {
            if (_asrDiagnosticRecorder == null)
            {
                _asrDiagnosticRecorder = GetComponent<AsrDiagnosticRecorder>();
            }

            if (_asrDiagnosticRecorder == null && createIfMissing)
            {
                _asrDiagnosticRecorder = gameObject.AddComponent<AsrDiagnosticRecorder>();
            }

            if (_asrDiagnosticRecorder == null && _enableAsrDiagnosticMode)
            {
                Debug.LogWarning($"{LogPrefix} ASR diagnostic recorder missing | diagnostic_enabled=true create_if_missing={createIfMissing}", this);
            }

            if (_asrDiagnosticRecorder != null && _enableAsrDiagnosticMode)
            {
                _asrDiagnosticRecorder.SetDiagnosticModeEnabled(true);
            }
        }

        private void ConfigureDiagnosticWhisperModelIfRequested()
        {
            if (!_enableAsrDiagnosticMode || !_diagnosticOverrideWhisperModel || _diagnosticModelSize == ASRModelSize.Unknown)
            {
                return;
            }

            if (WhisperModelConfiguration.TryApplyModel(_whisperManager, _diagnosticModelSize, out WhisperModelApplyResult result))
            {
                Debug.Log(
                    $"{LogPrefix} ASR diagnostic model configured | requested_model_size={result.RequestedModelSize} effective_model_size={result.EffectiveModelSize} resolved_model_path={result.ResolvedModelPath} model_file_name={result.EffectiveModelFileName} model_available_at_start={result.ModelAvailableAtStart}",
                    this);
                return;
            }

            _enableAsrDiagnosticMode = false;
            _asrDiagnosticRecorder?.SetDiagnosticModeEnabled(false);
            Debug.LogWarning(
                $"{LogPrefix} ASR diagnostic model not applied | requested_model_size={result.RequestedModelSize} resolved_model_path={result.ResolvedModelPath} reason={result.FailedReason} diagnostic_disabled=true",
                this);
        }

        private void Reset()
        {
            TryResolveAutonomyCommandConnector(createIfMissing: false);
            TryResolveAsrDiagnosticRecorder(createIfMissing: false);
        }

        private void OnValidate()
        {
            _sampleRate = Mathf.Max(8000, _sampleRate);
            _maxCaptureSeconds = Mathf.Max(0.5f, _maxCaptureSeconds);
            _vadVoiceRmsThreshold = Mathf.Max(0f, _vadVoiceRmsThreshold);
            _vadMinVoiceDurationToOpenSeconds = Mathf.Max(0f, _vadMinVoiceDurationToOpenSeconds);
            _vadSilenceDurationToCloseSeconds = Mathf.Max(0.05f, _vadSilenceDurationToCloseSeconds);
            _vadMinUtteranceSeconds = Mathf.Max(0.05f, _vadMinUtteranceSeconds);
            _vadMaxUtteranceSeconds = Mathf.Max(_vadMinUtteranceSeconds, _vadMaxUtteranceSeconds);
            _vadCooldownSeconds = Mathf.Max(0f, _vadCooldownSeconds);
            _vadRmsWindowSeconds = Mathf.Max(0.01f, _vadRmsWindowSeconds);
            _vadPreRollSeconds = Mathf.Max(0f, _vadPreRollSeconds);
            _vadShortCommandMinUtteranceSeconds = Mathf.Max(0.05f, _vadShortCommandMinUtteranceSeconds);
            _vadShortCommandCandidateMaxSeconds = Mathf.Max(_vadShortCommandMinUtteranceSeconds, _vadShortCommandCandidateMaxSeconds);
            _frameHitchWarningThresholdMs = Mathf.Max(1f, _frameHitchWarningThresholdMs);
            _frameHitchCriticalThresholdMs = Mathf.Max(_frameHitchWarningThresholdMs, _frameHitchCriticalThresholdMs);
        }
    }
}
