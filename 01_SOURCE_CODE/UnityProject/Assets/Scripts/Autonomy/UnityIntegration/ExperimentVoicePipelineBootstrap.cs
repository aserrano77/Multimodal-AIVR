using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Autonomy.UnityIntegration
{
    public enum MicrophonePermissionState
    {
        Unknown,
        RequestInProgress,
        Granted,
        Denied,
        DeniedDontAskAgain
    }

    public enum VoicePipelinePreparationState
    {
        NotStarted,
        WaitingForPermission,
        PreparingModel,
        CreatingRecognizer,
        Prepared,
        Failed
    }

    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-20000)]
    public sealed class ExperimentVoicePipelineBootstrap : MonoBehaviour
    {
        private const string LogPrefix = "[P46M-01][VoiceBootstrap]";
        private const int PreparationTimeoutMs = 150000;
        private static ExperimentVoicePipelineBootstrap s_instance;

        private readonly object _stateLock = new();
        private SherpaOnnxASRBackend _sharedBackend;
        private Task _preparationTask;
        private Task<SherpaVoicePipelinePreparationResult> _nativePreparationTask;
        private int _preparationAttemptId;
        private int _timedOutPreparationAttemptId;
        private bool _destroyed;
        private long _bootstrapStartedTimestamp;
        private bool _bootstrapStarted;
        private bool _microphoneOpen;
        private bool _captureActive;
        private bool _bridgeEnabled;
        private string _failureReason = string.Empty;
        private string _modelDirectory = string.Empty;
        private long _modelLoadMs;
        private long _recognizerCreateMs;

#if UNITY_ANDROID && !UNITY_EDITOR
        private UnityEngine.Android.PermissionCallbacks _permissionCallbacks;
#endif

        public static ExperimentVoicePipelineBootstrap Instance => s_instance;
        public static bool HasInstance => s_instance != null;
        public MicrophonePermissionState PermissionState { get; private set; } = MicrophonePermissionState.Unknown;
        public VoicePipelinePreparationState PreparationState { get; private set; } = VoicePipelinePreparationState.NotStarted;
        public bool IsPrepared => PermissionState == MicrophonePermissionState.Granted && PreparationState == VoicePipelinePreparationState.Prepared;
        public bool CanRetryPermission => PermissionState == MicrophonePermissionState.Denied;
        public bool CanRetryPreparation =>
            PermissionState == MicrophonePermissionState.Granted &&
            PreparationState == VoicePipelinePreparationState.Failed &&
            !IsNativePreparationInProgress;
        public string FailureReason => _failureReason;
        public string ModelDirectory => _modelDirectory;
        public long ModelLoadMs => _modelLoadMs;
        public long RecognizerCreateMs => _recognizerCreateMs;
        public SherpaOnnxASRBackend SharedBackend => _sharedBackend;
        public string ReadinessToken => $"{PermissionState}|{PreparationState}|{_failureReason}";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BootstrapBeforeFirstScene()
        {
            EnsureExists();
        }

        public static ExperimentVoicePipelineBootstrap EnsureExists()
        {
            if (s_instance != null)
            {
                return s_instance;
            }

            ExperimentVoicePipelineBootstrap existing = FindFirstObjectByType<ExperimentVoicePipelineBootstrap>();
            if (existing != null)
            {
                s_instance = existing;
                return existing;
            }

            GameObject host = new("ExperimentVoicePipelineBootstrap_P46M01");
            DontDestroyOnLoad(host);
            return host.AddComponent<ExperimentVoicePipelineBootstrap>();
        }

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            DontDestroyOnLoad(gameObject);
            _sharedBackend = new SherpaOnnxASRBackend(
                SherpaOnnxBackendOptions.CreateQuestOperationalDefaults(),
                new ASRDebugOptions
                {
                    DebugLogging = true,
                    Log = message => Debug.Log($"{LogPrefix} {message}"),
                    Warn = message => Debug.LogWarning($"{LogPrefix} {message}")
                });
        }

        private IEnumerator Start()
        {
            // Android permission requests need a live player activity. One frame is enough and
            // still happens on the start screen, long before any C11 trial can be started.
            yield return null;
            BeginBootstrap();
        }

        public void BeginBootstrap()
        {
            if (!_bootstrapStarted)
            {
                _bootstrapStarted = true;
                _bootstrapStartedTimestamp = Stopwatch.GetTimestamp();
                LogPhase("app_voice_bootstrap_started", 0L, "application_start");
            }

            CheckPermissionAndContinue(requestWhenMissing: true);
        }

        public void RetryPermissionOrPreparation()
        {
            if (PermissionState == MicrophonePermissionState.Denied)
            {
                CheckPermissionAndContinue(requestWhenMissing: true);
                return;
            }

            if (PermissionState == MicrophonePermissionState.Granted &&
                PreparationState == VoicePipelinePreparationState.Failed)
            {
                StartPipelinePreparation(forceRetry: true);
            }
        }

        public void EnsurePreparationStarted()
        {
            if (!_bootstrapStarted)
            {
                BeginBootstrap();
                return;
            }

            if (PermissionState == MicrophonePermissionState.Granted &&
                (PreparationState == VoicePipelinePreparationState.NotStarted ||
                 PreparationState == VoicePipelinePreparationState.WaitingForPermission))
            {
                StartPipelinePreparation(forceRetry: false);
            }
        }

        public string BuildParticipantStatusMessage()
        {
            return PermissionState switch
            {
                MicrophonePermissionState.RequestInProgress => "Esperando autorización del micrófono...",
                MicrophonePermissionState.Denied => "El reconocimiento vocal necesita permiso de micrófono. Puedes volver a solicitarlo.",
                MicrophonePermissionState.DeniedDontAskAgain => "El permiso de micrófono está bloqueado. Actívalo en Ajustes de la aplicación y vuelve a intentarlo.",
                _ when PreparationState == VoicePipelinePreparationState.PreparingModel => "Preparando el reconocimiento vocal...",
                _ when PreparationState == VoicePipelinePreparationState.CreatingRecognizer => "Inicializando el reconocimiento vocal...",
                _ when PreparationState == VoicePipelinePreparationState.Failed && IsNativePreparationInProgress => "La preparación está tardando más de lo esperado. Esperando a que finalice de forma segura...",
                _ when PreparationState == VoicePipelinePreparationState.Failed => "No se pudo preparar el reconocimiento vocal. Puedes reintentar o volver al inicio.",
                _ when IsPrepared => "Reconocimiento vocal preparado.",
                _ => "Comprobando el reconocimiento vocal..."
            };
        }

        public void ReportRuntimeState(bool microphoneOpen, bool captureActive, bool bridgeEnabled)
        {
            _microphoneOpen = microphoneOpen;
            _captureActive = captureActive;
            _bridgeEnabled = bridgeEnabled;
        }

        public void LogRuntimePhase(
            string eventName,
            long durationMs,
            string reason,
            string conditionId = "",
            int visibleTrial = 0,
            int round = 0,
            IDictionary<string, object> extra = null)
        {
            LogPhase(eventName, durationMs, reason, conditionId, visibleTrial, round, extra);
        }

        private void CheckPermissionAndContinue(bool requestWhenMissing)
        {
            bool granted = HasMicrophonePermission();
            PermissionState = granted ? MicrophonePermissionState.Granted : MicrophonePermissionState.Unknown;
            LogPhase("microphone_permission_state_checked", 0L, granted ? "already_granted" : "not_granted");
            if (granted)
            {
                LogPhase("microphone_permission_granted", 0L, "already_granted");
                StartPipelinePreparation(forceRetry: false);
                return;
            }

            PreparationState = VoicePipelinePreparationState.WaitingForPermission;
            if (!requestWhenMissing)
            {
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            PermissionState = MicrophonePermissionState.RequestInProgress;
            LogPhase("microphone_permission_request_started", 0L, "runtime_request_before_protocol");
            UnsubscribePermissionCallbacks();
            _permissionCallbacks = new UnityEngine.Android.PermissionCallbacks();
            _permissionCallbacks.PermissionGranted += HandlePermissionGranted;
            _permissionCallbacks.PermissionDenied += HandlePermissionDenied;
            _permissionCallbacks.PermissionDeniedAndDontAskAgain += HandlePermissionDeniedAndDontAskAgain;
            UnityEngine.Android.Permission.RequestUserPermission(
                UnityEngine.Android.Permission.Microphone,
                _permissionCallbacks);
#else
            PermissionState = MicrophonePermissionState.Granted;
            LogPhase("microphone_permission_request_completed", 0L, "non_android_simulated_grant");
            LogPhase("microphone_permission_granted", 0L, "non_android_simulated_grant");
            StartPipelinePreparation(forceRetry: false);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void HandlePermissionGranted(string permission)
        {
            UnsubscribePermissionCallbacks();
            if (_destroyed)
            {
                return;
            }

            PermissionState = MicrophonePermissionState.Granted;
            LogPhase("microphone_permission_request_completed", 0L, "granted");
            LogPhase("microphone_permission_granted", 0L, permission);
            StartPipelinePreparation(forceRetry: false);
        }

        private void HandlePermissionDenied(string permission)
        {
            UnsubscribePermissionCallbacks();
            if (_destroyed)
            {
                return;
            }

            PermissionState = MicrophonePermissionState.Denied;
            PreparationState = VoicePipelinePreparationState.WaitingForPermission;
            _failureReason = "microphone_permission_denied";
            LogPhase("microphone_permission_request_completed", 0L, "denied");
            LogPhase("microphone_permission_denied", 0L, permission);
        }

        private void HandlePermissionDeniedAndDontAskAgain(string permission)
        {
            UnsubscribePermissionCallbacks();
            if (_destroyed)
            {
                return;
            }

            PermissionState = MicrophonePermissionState.DeniedDontAskAgain;
            PreparationState = VoicePipelinePreparationState.WaitingForPermission;
            _failureReason = "microphone_permission_denied_dont_ask_again";
            LogPhase("microphone_permission_request_completed", 0L, "denied_dont_ask_again");
            LogPhase("microphone_permission_denied", 0L, permission);
        }

        private void UnsubscribePermissionCallbacks()
        {
            if (_permissionCallbacks == null)
            {
                return;
            }

            _permissionCallbacks.PermissionGranted -= HandlePermissionGranted;
            _permissionCallbacks.PermissionDenied -= HandlePermissionDenied;
            _permissionCallbacks.PermissionDeniedAndDontAskAgain -= HandlePermissionDeniedAndDontAskAgain;
            _permissionCallbacks = null;
        }
#endif

        private bool IsNativePreparationInProgress =>
            _nativePreparationTask != null && !_nativePreparationTask.IsCompleted;

        private void StartPipelinePreparation(bool forceRetry)
        {
            lock (_stateLock)
            {
                if (_destroyed ||
                    (_preparationTask != null && !_preparationTask.IsCompleted) ||
                    IsNativePreparationInProgress)
                {
                    return;
                }

                if (!forceRetry && PreparationState == VoicePipelinePreparationState.Prepared)
                {
                    return;
                }

                _failureReason = string.Empty;
                int attemptId = ++_preparationAttemptId;
                _timedOutPreparationAttemptId = 0;
                _preparationTask = ObservePipelinePreparationAsync(attemptId);
            }
        }

        private async Task ObservePipelinePreparationAsync(int attemptId)
        {
            try
            {
                await PreparePipelineAsync(attemptId);
            }
            catch (Exception ex)
            {
                if (!IsCurrentPreparationAttempt(attemptId))
                {
                    return;
                }

                PreparationState = VoicePipelinePreparationState.Failed;
                _failureReason = "voice_pipeline_prepare_exception:" + ex.GetBaseException().Message;
                LogPhase("voice_pipeline_prepare_failed", 0L, _failureReason);
            }
        }

        private async Task PreparePipelineAsync(int attemptId)
        {
            if (PermissionState != MicrophonePermissionState.Granted)
            {
                if (IsCurrentPreparationAttempt(attemptId))
                {
                    PreparationState = VoicePipelinePreparationState.WaitingForPermission;
                }

                return;
            }

            PreparationState = VoicePipelinePreparationState.PreparingModel;
            Task<SherpaVoicePipelinePreparationResult> nativeTask = _sharedBackend.PrepareVoicePipelineAsync(
                (phase, durationMs, detail) => HandlePreparationPhase(attemptId, phase, durationMs, detail));
            _nativePreparationTask = nativeTask;

            try
            {
                using CancellationTokenSource delayCancellation = new();
                Task delayTask = Task.Delay(PreparationTimeoutMs, delayCancellation.Token);
                Task completed = await Task.WhenAny(nativeTask, delayTask);
                if (completed == nativeTask)
                {
                    delayCancellation.Cancel();
                }
                else if (IsCurrentPreparationAttempt(attemptId))
                {
                    _timedOutPreparationAttemptId = attemptId;
                    PreparationState = VoicePipelinePreparationState.Failed;
                    _failureReason = "voice_pipeline_prepare_timeout";
                    LogPhase("voice_pipeline_prepare_failed", PreparationTimeoutMs, _failureReason);
                }

                SherpaVoicePipelinePreparationResult result = await nativeTask;
                if (!IsCurrentPreparationAttempt(attemptId))
                {
                    return;
                }

                _modelLoadMs = result.ModelLoadMs;
                _recognizerCreateMs = result.RecognizerCreateMs;
                _modelDirectory = result.ModelDirectory;
                if (!result.Success)
                {
                    PreparationState = VoicePipelinePreparationState.Failed;
                    _failureReason = result.ErrorReason;
                    LogPhase("voice_pipeline_prepare_failed", result.TotalMs, result.ErrorReason);
                    return;
                }

                _timedOutPreparationAttemptId = 0;
                _failureReason = string.Empty;
                PreparationState = VoicePipelinePreparationState.Prepared;
                LogPhase("voice_pipeline_prepared", result.TotalMs, result.RecognizerReused ? "recognizer_reused" : "recognizer_created");
            }
            finally
            {
                if (ReferenceEquals(_nativePreparationTask, nativeTask))
                {
                    _nativePreparationTask = null;
                }
            }
        }

        private bool IsCurrentPreparationAttempt(int attemptId)
        {
            lock (_stateLock)
            {
                return !_destroyed && attemptId == _preparationAttemptId;
            }
        }

        private void HandlePreparationPhase(int attemptId, string phase, long durationMs, string detail)
        {
            if (!IsCurrentPreparationAttempt(attemptId) || _timedOutPreparationAttemptId == attemptId)
            {
                return;
            }

            switch (phase)
            {
                case "model_load_started":
                    PreparationState = VoicePipelinePreparationState.PreparingModel;
                    LogPhase("asr_model_load_started", 0L, "prewarm_before_c11");
                    break;
                case "model_load_completed":
                    _modelLoadMs = durationMs;
                    _modelDirectory = detail ?? string.Empty;
                    LogPhase("asr_model_load_completed", durationMs, "model_ready");
                    break;
                case "recognizer_create_started":
                    PreparationState = VoicePipelinePreparationState.CreatingRecognizer;
                    LogPhase("asr_recognizer_create_started", 0L, "prewarm_before_c11");
                    break;
                case "recognizer_create_completed":
                    _recognizerCreateMs = durationMs;
                    LogPhase("asr_recognizer_create_completed", durationMs, "recognizer_ready");
                    break;
                case "model_load_failed":
                case "recognizer_create_failed":
                    _failureReason = detail ?? phase;
                    break;
            }
        }

        private bool HasMicrophonePermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone);
#else
            return true;
#endif
        }

        private void LogPhase(
            string eventName,
            long durationMs,
            string reason,
            string conditionId = "",
            int visibleTrial = 0,
            int round = 0,
            IDictionary<string, object> extra = null)
        {
            Dictionary<string, object> payload = new()
            {
                ["timestamp_utc"] = DateTime.UtcNow.ToString("O"),
                ["elapsed_ms"] = ElapsedMilliseconds(),
                ["duration_ms"] = Math.Max(0L, durationMs),
                ["scene"] = SceneManager.GetActiveScene().name,
                ["condition_id"] = conditionId ?? string.Empty,
                ["visible_prueba"] = Math.Max(0, visibleTrial),
                ["round"] = Math.Max(0, round),
                ["permission_state"] = PermissionState.ToString(),
                ["recognizer_prepared"] = PreparationState == VoicePipelinePreparationState.Prepared,
                ["microphone_open"] = _microphoneOpen,
                ["capture_active"] = _captureActive,
                ["bridge_enabled"] = _bridgeEnabled,
                ["preparation_state"] = PreparationState.ToString(),
                ["model_name"] = SherpaOnnxBackendOptions.QuestOperationalModelName,
                ["model_path"] = _modelDirectory,
                ["model_load_ms"] = _modelLoadMs,
                ["recognizer_create_ms"] = _recognizerCreateMs,
                ["reason"] = reason ?? string.Empty,
                ["thread_id"] = Environment.CurrentManagedThreadId
            };
            if (extra != null)
            {
                foreach (KeyValuePair<string, object> pair in extra)
                {
                    payload[pair.Key] = pair.Value;
                }
            }

            TiagoExperimentTelemetry.LogEvent(eventName, payload);
            Debug.Log($"{LogPrefix} {eventName} | elapsed_ms={payload["elapsed_ms"]} duration_ms={payload["duration_ms"]} permission={PermissionState} preparation={PreparationState} microphone_open={_microphoneOpen} capture_active={_captureActive} bridge_enabled={_bridgeEnabled} reason={reason ?? string.Empty}");
        }

        private long ElapsedMilliseconds()
        {
            if (_bootstrapStartedTimestamp == 0L)
            {
                return 0L;
            }

            long elapsed = Stopwatch.GetTimestamp() - _bootstrapStartedTimestamp;
            return Math.Max(0L, (long)(elapsed * 1000.0 / Stopwatch.Frequency));
        }

        private void OnApplicationQuit()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            UnsubscribePermissionCallbacks();
#endif
            _sharedBackend?.Dispose();
            _sharedBackend = null;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus || !_bootstrapStarted)
            {
                return;
            }

            // Returning from Android application settings is a supported recovery path for
            // DeniedDontAskAgain. Re-check state without forcing another system dialog.
            if (PermissionState != MicrophonePermissionState.RequestInProgress &&
                PermissionState != MicrophonePermissionState.Granted)
            {
                CheckPermissionAndContinue(requestWhenMissing: false);
            }
        }

        private void OnDestroy()
        {
            lock (_stateLock)
            {
                _destroyed = true;
                _preparationAttemptId++;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            UnsubscribePermissionCallbacks();
#endif
            if (s_instance == this)
            {
                s_instance = null;
            }
        }
    }

    public static class VoicePipelineReadinessPolicy
    {
        public const string VoiceEnabledConditionId = "C11_robot_on_voice_on";

        public static bool CanStartC11(
            MicrophonePermissionState permissionState,
            VoicePipelinePreparationState preparationState)
        {
            return permissionState == MicrophonePermissionState.Granted &&
                preparationState == VoicePipelinePreparationState.Prepared;
        }

        public static bool ShouldRequestPermission(MicrophonePermissionState permissionState)
        {
            return permissionState == MicrophonePermissionState.Unknown ||
                permissionState == MicrophonePermissionState.Denied;
        }

        public static bool ShouldCaptureVoice(
            string conditionId,
            bool trialActive,
            MicrophonePermissionState permissionState,
            VoicePipelinePreparationState preparationState)
        {
            return trialActive &&
                string.Equals(conditionId, VoiceEnabledConditionId, StringComparison.Ordinal) &&
                CanStartC11(permissionState, preparationState);
        }

        public static bool HasPreparedAndActivatedBeforeC11TrialStarted(IReadOnlyList<string> eventSequence)
        {
            if (eventSequence == null)
            {
                return false;
            }

            int prepared = -1;
            int activated = -1;
            int trialStarted = -1;
            for (int i = 0; i < eventSequence.Count; i++)
            {
                switch (eventSequence[i])
                {
                    case "voice_pipeline_prepared":
                        prepared = i;
                        break;
                    case "voice_pipeline_activated":
                        activated = i;
                        break;
                    case "c11_trial_started":
                        trialStarted = i;
                        break;
                }
            }

            return prepared >= 0 && activated > prepared && trialStarted > activated;
        }
    }
}
