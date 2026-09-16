using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autonomy.Domain;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentInstrumentationController : MonoBehaviour
    {
        private const string LogPrefix = "[ExperimentInstrumentationController]";
        private const int SafeWindowsPathLengthWarningThreshold = 240;

        [Header("Session Metadata")]
        [SerializeField] private string _participantId = "";
        [SerializeField] private string _sessionId = "";
        [SerializeField] private string _conditionId = ExperimentCompensatedConditionOrder.C00;
        [SerializeField] private string _conditionName = "";
        [SerializeField] private int _conditionOrderIndex = 0;
        [SerializeField] private bool _robotEnabled = true;
        [SerializeField] private bool _voiceEnabled = true;
        [SerializeField] private string _assistanceMode = "AssistedSelection";
        [SerializeField] private string _spawnGenerationMode = "RandomBalanced";
        [SerializeField] private string _roundId = "";
        [SerializeField] private int _roundIndex = 1;
        [SerializeField] private int _roundIndexWithinCondition = 1;
        [SerializeField] private int _roundsPerCondition = 1;
        [SerializeField] private int _globalRoundIndex = 1;
        [SerializeField] private bool _allowNonSlotDynamicPlaceFallback;
        [SerializeField] private bool _useDynamicPlacePose = true;
        [SerializeField] private bool _useDepositZoneSlotAllocator = true;
        [SerializeField] private float _maxExpectedPlaceDistance = 1.25f;
        [SerializeField] private float _maxRelaxedPlaceDistance = 1.45f;
        [SerializeField] private float _placeCandidateReachabilityMargin = 0.20f;
        [SerializeField] private int _maxPlaceApproachRetries = 2;
        [SerializeField] private string _placeFailureRecoveryMode = "";
        [SerializeField] private string _postPlaceEgressMode = "";
        [SerializeField] private string _taskId = "PickAndPlace_A_to_ZoneA";
        [SerializeField] private string _inputMode = "MultimodalSimulated";
        [Tooltip("Optional declared drive profile. Leave empty to infer it from the active runtime/logger metadata. If filled, it should match the effective runtime profile; mismatches are logged and the active value is used in summaries.")]
        [SerializeField] private string _driveProfile = "Realistic";
        [Tooltip("Optional declared autonomy policy. Leave empty to infer it from the active runtime/logger metadata. If filled, it should match the effective runtime policy; mismatches are logged and the active value is used in summaries.")]
        [SerializeField] private string _autonomyPolicy = "Safe";
        [SerializeField] private string _notes = "";

        [Header("Trial Control")]
        [SerializeField] private ExperimentRunMode _runMode = ExperimentRunMode.LegacyDebug;
        [SerializeField, Tooltip("Legacy/debug only. In Orchestrated2x2, ExperimentSessionOrchestrator supplies the context.")]
        private bool _configureSessionOnStart = false;
        [SerializeField] private bool _startTrialOnStart = false;
        [SerializeField] private KeyCode _startTrialKey = KeyCode.U;
        [SerializeField] private KeyCode _abortTrialKey = KeyCode.O;
        [SerializeField] private string _trialIdPrefix = "trial";
        [SerializeField] private int _initialTrialIndex = 1;
        [SerializeField] private bool _completeAssistedRoundTrialOnRoundCompleted = true;

        [Header("Summary Export")]
        [SerializeField] private string _fallbackSummaryFolder = "logs/experiments/step23_trial_summaries";
        [SerializeField] private bool _writeCsvSummary = true;
        [SerializeField] private AnalysisValidityMode _analysisValidityMode = AnalysisValidityMode.Auto;
        [SerializeField] private string _manualExclusionReason = "";

        private enum AnalysisValidityMode
        {
            Auto,
            ForceValid,
            ForceInvalid
        }

        private ExperimentSessionMetadata _sessionMetadata;
        private ExperimentTrialMetadata _trialMetadata;
        private ExperimentTrialMetricsAccumulator _accumulator;
        private bool _sessionConfigured;
        private bool _trialActive;
        private bool _metadataMismatchLoggedForTrial;
        private int _nextTrialIndex;
        private ExperimentRuntimeContext _currentContext;
        private string _lastTerminalTrialId = string.Empty;
        private int _lastTerminalTrialIndex;
        private string _lastTerminalEventType = string.Empty;
        private string _lastTerminalState = string.Empty;
        private bool _lastTerminalSuccess;
        private bool _lastTerminalAborted;

        public static ExperimentInstrumentationController Active { get; private set; }
        public bool TrialActive => _trialActive;
        public ExperimentRunMode RunMode => _runMode;
        public ExperimentRuntimeContext CurrentContext => _currentContext;
        public string LastTerminalTrialId => _lastTerminalTrialId;
        public int LastTerminalTrialIndex => _lastTerminalTrialIndex;
        public string LastTerminalEventType => _lastTerminalEventType;
        public string LastTerminalState => _lastTerminalState;
        public bool LastTerminalSuccess => _lastTerminalSuccess;
        public bool LastTerminalAborted => _lastTerminalAborted;

        public void SetRunMode(ExperimentRunMode runMode)
        {
            _runMode = runMode;
        }

        private void Awake()
        {
            if (Active != null && Active != this)
            {
                Debug.LogWarning($"{LogPrefix} Another instrumentation controller is already active. Keeping the first active context.", this);
            }
            else
            {
                Active = this;
            }

            _nextTrialIndex = Mathf.Max(1, _initialTrialIndex);
            TiagoExperimentTelemetry.StructuredEventLogged += HandleStructuredEvent;
        }

        private void Start()
        {
            if (_configureSessionOnStart && _runMode != ExperimentRunMode.Orchestrated2x2)
            {
                ConfigureSession();
            }

            if (_startTrialOnStart && _runMode != ExperimentRunMode.Orchestrated2x2)
            {
                StartTrial();
            }
        }

        private void Update()
        {
            if (_runMode != ExperimentRunMode.Orchestrated2x2 && RuntimeHotkeyInput.GetKeyDown(_startTrialKey))
            {
                StartTrial();
            }

            if (RuntimeHotkeyInput.GetKeyDown(_abortTrialKey))
            {
                AbortTrial("manual_abort_key");
            }
        }

        private void OnDestroy()
        {
            TiagoExperimentTelemetry.StructuredEventLogged -= HandleStructuredEvent;
            if (Active == this)
            {
                Active = null;
            }
        }

        public static void EnrichPayloadWithActiveContext(Dictionary<string, object> payload)
        {
            if (payload == null || Active == null)
            {
                return;
            }

            Active.AddActiveContext(payload);
        }

        private void AddActiveContext(Dictionary<string, object> payload)
        {
            if (!_sessionConfigured)
            {
                _sessionMetadata = BuildSessionMetadata();
                _sessionConfigured = true;
            }

            ExperimentSessionMetadata session = _sessionMetadata;
            string activeTrialId = ResolveActiveTrialId();
            int activeTrialIndex = ResolveActiveTrialIndex();
            bool hasPreparedTrialContext = _currentContext != null && !string.IsNullOrWhiteSpace(activeTrialId);
            AddIfMissingOrEmpty(payload, "run_id", ResolveRunId());
            AddIfMissingOrEmpty(payload, "participant_id", session.ParticipantId);
            AddIfMissingOrEmpty(payload, "session_id", session.SessionId);
            AddIfMissingOrEmpty(payload, "trial_id", activeTrialId);
            AddIfMissingOrEmpty(payload, "trial_index", activeTrialIndex);
            AddIfMissingOrEmpty(payload, "condition_id", session.ConditionId);
            AddIfMissingOrEmpty(payload, "condition_name", session.ConditionName);
            AddIfMissing(payload, "condition_order_index", session.ConditionOrderIndex);
            AddIfMissingOrEmpty(payload, "robot_enabled", session.RobotEnabled);
            AddIfMissingOrEmpty(payload, "voice_enabled", session.VoiceEnabled);
            AddIfMissingOrEmpty(payload, "assistance_mode", session.AssistanceMode);
            AddIfMissingOrEmpty(payload, "spawn_generation_mode", session.SpawnGenerationMode);
            AddIfMissingOrEmpty(payload, "round_id", session.RoundId);
            AddIfMissingOrEmpty(payload, "round_index", hasPreparedTrialContext ? session.RoundIndex : 0);
            AddIfMissingOrEmpty(payload, "round_index_within_condition", hasPreparedTrialContext ? session.RoundIndexWithinCondition : 0);
            AddIfMissingOrEmpty(payload, "rounds_per_condition", session.RoundsPerCondition);
            AddIfMissingOrEmpty(payload, "global_round_index", hasPreparedTrialContext ? session.GlobalRoundIndex : 0);
            AddIfMissingOrEmpty(payload, "allow_non_slot_dynamic_place_fallback", session.AllowNonSlotDynamicPlaceFallback);
            AddIfMissingOrEmpty(payload, "use_dynamic_place_pose", session.UseDynamicPlacePose);
            AddIfMissingOrEmpty(payload, "use_deposit_zone_slot_allocator", session.UseDepositZoneSlotAllocator);
            AddIfMissingOrEmpty(payload, "max_expected_place_distance", session.MaxExpectedPlaceDistance);
            AddIfMissingOrEmpty(payload, "max_relaxed_place_distance", session.MaxRelaxedPlaceDistance);
            AddIfMissingOrEmpty(payload, "place_candidate_reachability_margin", session.PlaceCandidateReachabilityMargin);
            AddIfMissingOrEmpty(payload, "max_place_approach_retries", session.MaxPlaceApproachRetries);
            AddIfMissingOrEmpty(payload, "place_failure_recovery_mode", session.PlaceFailureRecoveryMode);
            AddIfMissingOrEmpty(payload, "post_place_egress_mode", session.PostPlaceEgressMode);
            AddIfMissingOrEmpty(payload, "task_id", session.TaskId);
            AddIfMissingOrEmpty(payload, "input_mode", session.InputMode);
            AddIfMissingOrEmpty(payload, "drive_profile", session.DriveProfile);
            AddIfMissingOrEmpty(payload, "autonomy_policy", session.AutonomyPolicy);
            AddIfMissingOrEmpty(payload, "scene_name", session.SceneName);
        }

        private static void AddIfMissingOrEmpty(Dictionary<string, object> payload, string key, object value)
        {
            if (!payload.TryGetValue(key, out object existing) ||
                existing == null ||
                string.IsNullOrWhiteSpace(existing.ToString()) ||
                (existing is int intValue && intValue == 0 && value is int replacementInt && replacementInt != 0))
            {
                payload[key] = value;
            }
        }

        private static void AddIfMissing(Dictionary<string, object> payload, string key, object value)
        {
            if (!payload.TryGetValue(key, out object existing) ||
                existing == null ||
                string.IsNullOrWhiteSpace(existing.ToString()))
            {
                payload[key] = value;
            }
        }

        public void ConfigureSession()
        {
            if (_runMode == ExperimentRunMode.Orchestrated2x2 && _currentContext == null)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "experiment_legacy_session_configure_ignored",
                    new Dictionary<string, object>
                    {
                        ["reason"] = "orchestrated_2x2_requires_context",
                        ["instrumentation"] = name
                    });
                return;
            }

            _sessionMetadata = BuildSessionMetadata();
            _sessionConfigured = true;
            TiagoExperimentTelemetry.LogEvent("experiment_session_configured", BuildSessionPayload());
            TiagoExperimentTelemetry.LogEvent("experiment_condition_configured", BuildSessionPayload());
            Debug.Log($"{LogPrefix} Session configured | participant={_participantId} session={_sessionMetadata.SessionId} condition={_conditionId}", this);
        }

        public void ApplyOrchestratorMetadata(
            string participantId,
            string sessionId,
            string conditionId,
            string conditionName,
            int conditionOrderIndex,
            bool robotEnabled,
            bool voiceEnabled,
            string assistanceMode,
            string spawnGenerationMode,
            string roundId,
            RobotAssistancePlaceRecoveryConfig placeRecoveryConfig,
            string taskId)
        {
            ApplyOrchestratedContext(new ExperimentRuntimeContext(
                participantId,
                sessionId,
                trialId: string.Empty,
                trialIndex: 0,
                conditionId,
                conditionName,
                conditionOrderIndex,
                robotEnabled,
                voiceEnabled,
                ParseAssistanceMode(assistanceMode),
                ParseSpawnMode(spawnGenerationMode),
                taskId,
                _inputMode,
                roundId,
                roundIndex: 1,
                roundIndexWithinCondition: 1,
                roundsPerCondition: 1,
                globalRoundIndex: 1,
                placeRecoveryConfig: placeRecoveryConfig,
                placeFailureRecoveryMode: _placeFailureRecoveryMode,
                postPlaceEgressMode: _postPlaceEgressMode));
        }

        public void ApplyOrchestratedContext(ExperimentRuntimeContext context)
        {
            if (context == null)
            {
                return;
            }

            _runMode = context.RunMode;
            _currentContext = context;

            if (!string.IsNullOrWhiteSpace(context.ParticipantId))
            {
                _participantId = context.ParticipantId;
            }

            if (!string.IsNullOrWhiteSpace(context.SessionId))
            {
                _sessionId = context.SessionId;
            }

            _conditionId = context.ConditionId;
            _conditionName = context.ConditionName;
            _conditionOrderIndex = context.ConditionOrderIndex;
            _robotEnabled = context.RobotEnabled;
            _voiceEnabled = context.VoiceEnabled;
            _assistanceMode = context.AssistanceMode.ToString();
            _spawnGenerationMode = context.SpawnGenerationMode.ToString();
            _roundId = context.RoundId;
            _roundIndex = context.RoundIndex;
            _roundIndexWithinCondition = context.RoundIndexWithinCondition;
            _roundsPerCondition = context.RoundsPerCondition;
            _globalRoundIndex = context.GlobalRoundIndex;
            _inputMode = context.InputMode;
            _placeFailureRecoveryMode = context.PlaceFailureRecoveryMode;
            _postPlaceEgressMode = context.PostPlaceEgressMode;
            ApplyPlaceRecoveryConfig(context.PlaceRecoveryConfig);

            if (!string.IsNullOrWhiteSpace(context.TaskId))
            {
                _taskId = context.TaskId;
            }

            _sessionMetadata = BuildSessionMetadata();
            _sessionConfigured = true;
            TiagoExperimentTelemetry.LogEvent("experiment_orchestrator_metadata_applied", BuildSessionPayload());
        }

        public bool ApplyAuthoritativeSessionIdentity(ExperimentDataPathResolver.SessionContext context)
        {
            string errorReason = context.Identity == null ? "session_identity_missing" : string.Empty;
            if (context.Identity == null ||
                !ExperimentDataPathResolver.TryValidateCurrentIdentity(context.Identity, out errorReason))
            {
                Debug.LogError(
                    $"{LogPrefix} authoritative_session_identity_rejected | participant_id={context.ParticipantId} session_id={context.SessionId} reason={errorReason}",
                    this);
                return false;
            }

            _participantId = context.ParticipantId;
            _sessionId = context.SessionId;
            _sessionMetadata = BuildSessionMetadata();
            _sessionConfigured = true;
            return string.Equals(_sessionMetadata.ParticipantId, context.ParticipantId, StringComparison.Ordinal) &&
                string.Equals(_sessionMetadata.SessionId, context.SessionId, StringComparison.Ordinal);
        }

        public void PrepareForNewSession()
        {
            if (_trialActive)
            {
                throw new InvalidOperationException("Cannot clear instrumentation context while a trial is active.");
            }

            _participantId = string.Empty;
            _sessionId = string.Empty;
            _conditionId = string.Empty;
            _conditionName = string.Empty;
            _conditionOrderIndex = -1;
            _robotEnabled = false;
            _voiceEnabled = false;
            _assistanceMode = string.Empty;
            _roundId = string.Empty;
            _roundIndex = 0;
            _roundIndexWithinCondition = 0;
            _globalRoundIndex = 0;
            _currentContext = null;
            _trialMetadata = null;
            _accumulator = null;
            _sessionMetadata = null;
            _sessionConfigured = false;
            _nextTrialIndex = 1;
            _lastTerminalTrialId = string.Empty;
            _lastTerminalTrialIndex = 0;
            _lastTerminalEventType = string.Empty;
            _lastTerminalState = string.Empty;
            _lastTerminalSuccess = false;
            _lastTerminalAborted = false;
        }

        private void ApplyPlaceRecoveryConfig(RobotAssistancePlaceRecoveryConfig config)
        {
            if (config == null)
            {
                return;
            }

            _allowNonSlotDynamicPlaceFallback = config.AllowNonSlotDynamicPlaceFallback;
            _useDynamicPlacePose = config.UseDynamicPlacePose;
            _useDepositZoneSlotAllocator = config.UseDepositZoneSlotAllocator;
            _maxExpectedPlaceDistance = config.MaxExpectedPlaceDistance;
            _maxRelaxedPlaceDistance = config.MaxRelaxedPlaceDistance;
            _placeCandidateReachabilityMargin = config.PlaceCandidateReachabilityMargin;
            _maxPlaceApproachRetries = config.MaxPlaceApproachRetries;
        }

        public bool IsSynchronizedWithContext(ExperimentRuntimeContext context, out string reason)
        {
            reason = string.Empty;
            if (context == null || _sessionMetadata == null)
            {
                reason = "context_or_session_metadata_missing";
                return false;
            }

            if (!string.Equals(_sessionMetadata.SessionId, context.SessionId, StringComparison.Ordinal) ||
                !string.Equals(_sessionMetadata.ConditionId, context.ConditionId, StringComparison.Ordinal) ||
                !string.Equals(_sessionMetadata.ConditionName, context.ConditionName, StringComparison.Ordinal) ||
                _sessionMetadata.ConditionOrderIndex != context.ConditionOrderIndex ||
                _sessionMetadata.RobotEnabled != context.RobotEnabled ||
                _sessionMetadata.VoiceEnabled != context.VoiceEnabled ||
                !string.Equals(_sessionMetadata.AssistanceMode, context.AssistanceMode.ToString(), StringComparison.Ordinal) ||
                !string.Equals(_sessionMetadata.SpawnGenerationMode, context.SpawnGenerationMode.ToString(), StringComparison.Ordinal) ||
                !string.Equals(_sessionMetadata.RoundId, context.RoundId, StringComparison.Ordinal) ||
                _sessionMetadata.RoundIndex != context.RoundIndex ||
                _sessionMetadata.RoundIndexWithinCondition != context.RoundIndexWithinCondition ||
                _sessionMetadata.RoundsPerCondition != context.RoundsPerCondition ||
                _sessionMetadata.GlobalRoundIndex != context.GlobalRoundIndex ||
                !string.Equals(_sessionMetadata.TaskId, context.TaskId, StringComparison.Ordinal))
            {
                reason = "instrumentation_context_mismatch";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static RobotAssistanceMode ParseAssistanceMode(string value)
        {
            return Enum.TryParse(value, out RobotAssistanceMode parsed)
                ? parsed
                : RobotAssistanceMode.AssistedSelection;
        }

        private static SpawnGenerationMode ParseSpawnMode(string value)
        {
            return Enum.TryParse(value, out SpawnGenerationMode parsed)
                ? parsed
                : SpawnGenerationMode.RandomBalanced;
        }

        public void StartTrial()
        {
            if (!_sessionConfigured)
            {
                ConfigureSession();
            }

            if (_trialActive)
            {
                Debug.LogWarning($"{LogPrefix} Trial already active. Start ignored.", this);
                return;
            }

            string runId = ResolveRunId();
            string trialId = _runMode == ExperimentRunMode.Orchestrated2x2 && _currentContext != null && !string.IsNullOrWhiteSpace(_currentContext.TrialId)
                ? _currentContext.TrialId
                : BuildTrialId(_nextTrialIndex);
            int trialIndex = _runMode == ExperimentRunMode.Orchestrated2x2 && _currentContext != null && _currentContext.TrialIndex > 0
                ? _currentContext.TrialIndex
                : _nextTrialIndex;
            string timestampStart = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            _metadataMismatchLoggedForTrial = false;
            RefreshSessionMetadataFromRuntime(trialId, trialIndex, allowMismatchEvent: true);
            _trialMetadata = new ExperimentTrialMetadata(_sessionMetadata, runId, trialId, trialIndex, timestampStart);
            _accumulator = new ExperimentTrialMetricsAccumulator(_trialMetadata, Time.time);
            _trialActive = true;
            _nextTrialIndex = Mathf.Max(_nextTrialIndex + 1, trialIndex + 1);

            TiagoExperimentTelemetry.LogEvent("experiment_trial_started", BuildTrialPayload());
            _lastTerminalTrialId = string.Empty;
            _lastTerminalTrialIndex = 0;
            _lastTerminalEventType = string.Empty;
            _lastTerminalState = string.Empty;
            _lastTerminalSuccess = false;
            _lastTerminalAborted = false;
            Debug.Log($"{LogPrefix} Trial started | trial={trialId} runId={runId}", this);
        }

        public void AbortTrial(string reason)
        {
            if (!_trialActive)
            {
                return;
            }

            CompleteTrial(success: false, aborted: true, eventType: "experiment_trial_aborted", failureReason: reason);
        }

        public void EndTrialManually(string reason)
        {
            if (!_trialActive)
            {
                return;
            }

            CompleteTrial(success: true, aborted: false, eventType: "experiment_trial_completed", failureReason: string.Empty, terminalState: string.IsNullOrWhiteSpace(reason) ? "manually_ended" : reason);
        }

        public void EndTrialManuallyIncomplete(string failureReason, string terminalState = "manually_ended_incomplete")
        {
            if (!_trialActive)
            {
                return;
            }

            CompleteTrial(
                success: false,
                aborted: false,
                eventType: "experiment_trial_failed",
                failureReason: string.IsNullOrWhiteSpace(failureReason) ? "manual_end_incomplete" : failureReason,
                terminalState: string.IsNullOrWhiteSpace(terminalState) ? "manually_ended_incomplete" : terminalState);
        }

        public void RecordPreparedTrialInvalid(string failureReason)
        {
            if (_trialActive)
            {
                return;
            }

            string trialId = _runMode == ExperimentRunMode.Orchestrated2x2 && _currentContext != null && !string.IsNullOrWhiteSpace(_currentContext.TrialId)
                ? _currentContext.TrialId
                : BuildTrialId(_nextTrialIndex);
            int trialIndex = _runMode == ExperimentRunMode.Orchestrated2x2 && _currentContext != null && _currentContext.TrialIndex > 0
                ? _currentContext.TrialIndex
                : _nextTrialIndex;
            string timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            RefreshSessionMetadataFromRuntime(trialId, trialIndex, allowMismatchEvent: false);

            var summary = new ExperimentTrialSummary
            {
                RunId = ResolveRunId(),
                ParticipantId = _sessionMetadata.ParticipantId,
                SessionId = _sessionMetadata.SessionId,
                TrialId = trialId,
                TrialIndex = trialIndex,
                ConditionId = _sessionMetadata.ConditionId,
                ConditionName = _sessionMetadata.ConditionName,
                ConditionOrderIndex = _sessionMetadata.ConditionOrderIndex,
                RobotEnabled = _sessionMetadata.RobotEnabled,
                VoiceEnabled = _sessionMetadata.VoiceEnabled,
                AssistanceMode = _sessionMetadata.AssistanceMode,
                SpawnGenerationMode = _sessionMetadata.SpawnGenerationMode,
                RoundId = _sessionMetadata.RoundId,
                TaskId = _sessionMetadata.TaskId,
                InputMode = _sessionMetadata.InputMode,
                DriveProfile = _sessionMetadata.DriveProfile,
                AutonomyPolicy = _sessionMetadata.AutonomyPolicy,
                SceneName = _sessionMetadata.SceneName,
                Success = false,
                Aborted = false,
                TerminalState = "invalid_before_start",
                FailureReason = string.IsNullOrWhiteSpace(failureReason) ? "invalid_before_start" : failureReason,
                TimestampStart = timestamp,
                TimestampEnd = timestamp,
                Notes = "prepared_trial_invalid_before_start"
            };

            summary.AllowNonSlotDynamicPlaceFallback = _sessionMetadata.AllowNonSlotDynamicPlaceFallback;
            summary.UseDynamicPlacePose = _sessionMetadata.UseDynamicPlacePose;
            summary.UseDepositZoneSlotAllocator = _sessionMetadata.UseDepositZoneSlotAllocator;
            summary.MaxExpectedPlaceDistance = _sessionMetadata.MaxExpectedPlaceDistance;
            summary.MaxRelaxedPlaceDistance = _sessionMetadata.MaxRelaxedPlaceDistance;
            summary.PlaceCandidateReachabilityMargin = _sessionMetadata.PlaceCandidateReachabilityMargin;
            summary.MaxPlaceApproachRetries = _sessionMetadata.MaxPlaceApproachRetries;
            summary.PlaceFailureRecoveryMode = _sessionMetadata.PlaceFailureRecoveryMode;
            summary.PostPlaceEgressMode = _sessionMetadata.PostPlaceEgressMode;

            TrialSummaryPaths summaryPaths = WriteSummary(summary);
            WriteSessionIndex(summary, summaryPaths);
            TiagoExperimentTelemetry.LogEvent("experiment_prepared_trial_invalid_summary_written", BuildSummaryPayload(summary));
        }

        private void HandleStructuredEvent(string eventType, Dictionary<string, object> payload, float unityTime)
        {
            if (!_trialActive || _accumulator == null)
            {
                return;
            }

            _accumulator.ObserveEvent(eventType, payload, unityTime);

            if (eventType == "task_completed")
            {
                if (ShouldKeepAssistedRoundTrialOpen())
                {
                    return;
                }

                CompleteTrial(success: true, aborted: false, eventType: "experiment_trial_completed", failureReason: string.Empty);
                return;
            }

            if ((eventType == "round_completed" || eventType == "assisted_round_completed") &&
                ShouldKeepAssistedRoundTrialOpen())
            {
                CompleteTrial(success: true, aborted: false, eventType: "experiment_trial_completed", failureReason: string.Empty);
                return;
            }

            if (eventType == "task_failed")
            {
                if (ShouldKeepAssistedRoundTrialOpen())
                {
                    return;
                }

                string reason = TryGetPayloadString(payload, "reason", out string payloadReason)
                    ? payloadReason
                    : "task_failed";
                CompleteTrial(success: false, aborted: false, eventType: "experiment_trial_failed", failureReason: reason);
            }
        }

        private bool ShouldKeepAssistedRoundTrialOpen()
        {
            if (!_completeAssistedRoundTrialOnRoundCompleted)
            {
                return false;
            }

            string taskId = _sessionMetadata != null ? _sessionMetadata.TaskId : _taskId;
            return string.Equals(taskId, "AssistedRoundPickAndPlace", StringComparison.OrdinalIgnoreCase);
        }

        private void CompleteTrial(bool success, bool aborted, string eventType, string failureReason, string terminalState = "")
        {
            if (!_trialActive || _accumulator == null)
            {
                return;
            }

            string timestampEnd = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            RefreshSessionMetadataFromRuntime(_trialMetadata.TrialId, _trialMetadata.TrialIndex, allowMismatchEvent: true);
            ExperimentTrialSummary summary = _accumulator.BuildSummary(success, aborted, failureReason, Time.time, timestampEnd);
            summary.RunId = ResolveRunId();
            summary.DriveProfile = _sessionMetadata.DriveProfile;
            summary.AutonomyPolicy = _sessionMetadata.AutonomyPolicy;
            summary.TerminalState = string.IsNullOrWhiteSpace(terminalState)
                ? (aborted ? "aborted" : (success ? "completed" : "failed"))
                : terminalState;
            Dictionary<string, object> payload = BuildSummaryPayload(summary);
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
            TrialSummaryPaths summaryPaths = WriteSummary(summary);
            WriteSessionIndex(summary, summaryPaths);

            _lastTerminalTrialId = summary.TrialId ?? string.Empty;
            _lastTerminalTrialIndex = summary.TrialIndex;
            _lastTerminalEventType = eventType ?? string.Empty;
            _lastTerminalState = summary.TerminalState ?? string.Empty;
            _lastTerminalSuccess = success;
            _lastTerminalAborted = aborted;
            _trialActive = false;
            _trialMetadata = null;
            _accumulator = null;
            Debug.Log($"{LogPrefix} Trial closed | event={eventType} success={success} aborted={aborted}", this);
        }

        private ExperimentSessionMetadata BuildSessionMetadata()
        {
            ExperimentDataPathResolver.SessionContext activeSession = ExperimentDataPathResolver.CurrentSession;
            bool hasAuthoritativeIdentity = activeSession.Identity != null &&
                ExperimentDataPathResolver.TryValidateCurrentIdentity(activeSession.Identity, out _);
            if (hasAuthoritativeIdentity)
            {
                _participantId = activeSession.ParticipantId;
                _sessionId = activeSession.SessionId;
            }
            else if (_runMode != ExperimentRunMode.Orchestrated2x2 &&
                     ExperimentDataPathResolver.RequiresAutomaticParticipantId(_participantId))
            {
                _participantId = ExperimentDataPathResolver.CreateParticipantId();
            }

            if (!hasAuthoritativeIdentity &&
                _runMode != ExperimentRunMode.Orchestrated2x2 &&
                ExperimentDataPathResolver.RequiresAutomaticSessionId(_sessionId))
            {
                _sessionId = ExperimentDataPathResolver.CreateSessionId();
            }

            string participantId = _participantId;
            string sessionId = _sessionId;
            string sceneName = ResolveSceneName();
            ExperimentRuntimeMetadataResolution resolution = ResolveRuntimeMetadata();
            return new ExperimentSessionMetadata(
                participantId,
                sessionId,
                _conditionId,
                _taskId,
                _inputMode,
                resolution.EffectiveDriveProfile,
                resolution.EffectiveAutonomyPolicy,
                sceneName,
                _notes,
                _conditionName,
                _conditionOrderIndex,
                _robotEnabled,
                _voiceEnabled,
                _assistanceMode,
                _spawnGenerationMode,
                _roundId,
                _roundIndex,
                _roundIndexWithinCondition,
                _roundsPerCondition,
                _globalRoundIndex,
                _allowNonSlotDynamicPlaceFallback,
                _useDynamicPlacePose,
                _useDepositZoneSlotAllocator,
                _maxExpectedPlaceDistance,
                _maxRelaxedPlaceDistance,
                _placeCandidateReachabilityMargin,
                _maxPlaceApproachRetries,
                _placeFailureRecoveryMode,
                _postPlaceEgressMode);
        }

        private void RefreshSessionMetadataFromRuntime(string trialId, int trialIndex, bool allowMismatchEvent)
        {
            if (_sessionMetadata == null)
            {
                _sessionMetadata = BuildSessionMetadata();
                return;
            }

            ExperimentRuntimeMetadataResolution resolution = ResolveRuntimeMetadata();
            _sessionMetadata = new ExperimentSessionMetadata(
                _sessionMetadata.ParticipantId,
                _sessionMetadata.SessionId,
                _sessionMetadata.ConditionId,
                _sessionMetadata.TaskId,
                _sessionMetadata.InputMode,
                resolution.EffectiveDriveProfile,
                resolution.EffectiveAutonomyPolicy,
                _sessionMetadata.SceneName,
                _sessionMetadata.Notes,
                _sessionMetadata.ConditionName,
                _sessionMetadata.ConditionOrderIndex,
                _sessionMetadata.RobotEnabled,
                _sessionMetadata.VoiceEnabled,
                _sessionMetadata.AssistanceMode,
                _sessionMetadata.SpawnGenerationMode,
                _sessionMetadata.RoundId,
                _sessionMetadata.RoundIndex,
                _sessionMetadata.RoundIndexWithinCondition,
                _sessionMetadata.RoundsPerCondition,
                _sessionMetadata.GlobalRoundIndex,
                _sessionMetadata.AllowNonSlotDynamicPlaceFallback,
                _sessionMetadata.UseDynamicPlacePose,
                _sessionMetadata.UseDepositZoneSlotAllocator,
                _sessionMetadata.MaxExpectedPlaceDistance,
                _sessionMetadata.MaxRelaxedPlaceDistance,
                _sessionMetadata.PlaceCandidateReachabilityMargin,
                _sessionMetadata.MaxPlaceApproachRetries,
                _sessionMetadata.PlaceFailureRecoveryMode,
                _sessionMetadata.PostPlaceEgressMode);

            if (allowMismatchEvent && resolution.HasMismatch && !_metadataMismatchLoggedForTrial)
            {
                LogMetadataMismatch(resolution, trialId, trialIndex);
                _metadataMismatchLoggedForTrial = true;
            }
        }

        private ExperimentRuntimeMetadataResolution ResolveRuntimeMetadata()
        {
            TiagoExperimentTelemetry.Snapshot snapshot = TiagoExperimentTelemetry.Latest;
            return ExperimentRuntimeMetadataResolver.Resolve(
                _driveProfile,
                _autonomyPolicy,
                snapshot.ActiveDriveProfile,
                snapshot.ActiveAutonomousPolicy);
        }

        private void LogMetadataMismatch(ExperimentRuntimeMetadataResolution resolution, string trialId, int trialIndex)
        {
            string message =
                $"{LogPrefix} experiment_metadata_mismatch | declared_drive_profile='{resolution.DeclaredDriveProfile}' active_drive_profile='{resolution.ActiveDriveProfile}' declared_autonomy_policy='{resolution.DeclaredAutonomyPolicy}' active_autonomy_policy='{resolution.ActiveAutonomyPolicy}' resolution_strategy={resolution.ResolutionStrategy}";
            Debug.LogWarning(message, this);

            TiagoExperimentTelemetry.LogEvent(
                "experiment_metadata_mismatch",
                new Dictionary<string, object>
                {
                    ["run_id"] = ResolveRunId(),
                    ["participant_id"] = _sessionMetadata != null ? _sessionMetadata.ParticipantId : _participantId,
                    ["session_id"] = _sessionMetadata != null ? _sessionMetadata.SessionId : _sessionId,
                    ["trial_id"] = trialId ?? string.Empty,
                    ["trial_index"] = trialIndex,
                    ["condition_id"] = _sessionMetadata != null ? _sessionMetadata.ConditionId : _conditionId,
                    ["declared_drive_profile"] = resolution.DeclaredDriveProfile,
                    ["active_drive_profile"] = resolution.ActiveDriveProfile,
                    ["declared_autonomy_policy"] = resolution.DeclaredAutonomyPolicy,
                    ["active_autonomy_policy"] = resolution.ActiveAutonomyPolicy,
                    ["effective_drive_profile"] = resolution.EffectiveDriveProfile,
                    ["effective_autonomy_policy"] = resolution.EffectiveAutonomyPolicy,
                    ["resolution_strategy"] = resolution.ResolutionStrategy
                });
        }

        private Dictionary<string, object> BuildSessionPayload()
        {
            string activeTrialId = ResolveActiveTrialId();
            int activeTrialIndex = ResolveActiveTrialIndex();
            return new Dictionary<string, object>
            {
                ["run_id"] = ResolveRunId(),
                ["participant_id"] = _sessionMetadata.ParticipantId,
                ["session_id"] = _sessionMetadata.SessionId,
                ["trial_id"] = activeTrialId,
                ["trial_index"] = activeTrialIndex,
                ["condition_id"] = _sessionMetadata.ConditionId,
                ["condition_name"] = _sessionMetadata.ConditionName,
                ["condition_order_index"] = _sessionMetadata.ConditionOrderIndex,
                ["robot_enabled"] = _sessionMetadata.RobotEnabled,
                ["voice_enabled"] = _sessionMetadata.VoiceEnabled,
                ["assistance_mode"] = _sessionMetadata.AssistanceMode,
                ["spawn_generation_mode"] = _sessionMetadata.SpawnGenerationMode,
                ["round_id"] = _sessionMetadata.RoundId,
                ["round_index"] = _sessionMetadata.RoundIndex,
                ["round_index_within_condition"] = _sessionMetadata.RoundIndexWithinCondition,
                ["rounds_per_condition"] = _sessionMetadata.RoundsPerCondition,
                ["global_round_index"] = _sessionMetadata.GlobalRoundIndex,
                ["allow_non_slot_dynamic_place_fallback"] = _sessionMetadata.AllowNonSlotDynamicPlaceFallback,
                ["use_dynamic_place_pose"] = _sessionMetadata.UseDynamicPlacePose,
                ["use_deposit_zone_slot_allocator"] = _sessionMetadata.UseDepositZoneSlotAllocator,
                ["max_expected_place_distance"] = _sessionMetadata.MaxExpectedPlaceDistance,
                ["max_relaxed_place_distance"] = _sessionMetadata.MaxRelaxedPlaceDistance,
                ["place_candidate_reachability_margin"] = _sessionMetadata.PlaceCandidateReachabilityMargin,
                ["max_place_approach_retries"] = _sessionMetadata.MaxPlaceApproachRetries,
                ["place_failure_recovery_mode"] = _sessionMetadata.PlaceFailureRecoveryMode,
                ["post_place_egress_mode"] = _sessionMetadata.PostPlaceEgressMode,
                ["task_id"] = _sessionMetadata.TaskId,
                ["input_mode"] = _sessionMetadata.InputMode,
                ["drive_profile"] = _sessionMetadata.DriveProfile,
                ["autonomy_policy"] = _sessionMetadata.AutonomyPolicy,
                ["scene_name"] = _sessionMetadata.SceneName,
                ["notes"] = _sessionMetadata.Notes
            };
        }

        private string ResolveActiveTrialId()
        {
            if (_trialActive && _trialMetadata != null && !string.IsNullOrWhiteSpace(_trialMetadata.TrialId))
            {
                return _trialMetadata.TrialId;
            }

            if (_runMode == ExperimentRunMode.Orchestrated2x2 && _currentContext != null)
            {
                return _currentContext.TrialId;
            }

            return string.Empty;
        }

        private int ResolveActiveTrialIndex()
        {
            if (_trialActive && _trialMetadata != null && _trialMetadata.TrialIndex > 0)
            {
                return _trialMetadata.TrialIndex;
            }

            if (_runMode == ExperimentRunMode.Orchestrated2x2 && _currentContext != null)
            {
                return _currentContext.TrialIndex;
            }

            return 0;
        }

        private Dictionary<string, object> BuildTrialPayload()
        {
            Dictionary<string, object> payload = BuildSessionPayload();
            payload["trial_id"] = _trialMetadata.TrialId;
            payload["trial_index"] = _trialMetadata.TrialIndex;
            payload["timestamp_start"] = _trialMetadata.TimestampStart;
            return payload;
        }

        private static Dictionary<string, object> BuildSummaryPayload(ExperimentTrialSummary summary)
        {
            return new Dictionary<string, object>
            {
                ["run_id"] = summary.RunId,
                ["participant_id"] = summary.ParticipantId,
                ["session_id"] = summary.SessionId,
                ["trial_id"] = summary.TrialId,
                ["trial_index"] = summary.TrialIndex,
                ["condition_id"] = summary.ConditionId,
                ["condition_name"] = summary.ConditionName,
                ["condition_order_index"] = summary.ConditionOrderIndex,
                ["robot_enabled"] = summary.RobotEnabled,
                ["voice_enabled"] = summary.VoiceEnabled,
                ["assistance_mode"] = summary.AssistanceMode,
                ["spawn_generation_mode"] = summary.SpawnGenerationMode,
                ["round_id"] = summary.RoundId,
                ["round_index"] = summary.RoundIndex,
                ["round_index_within_condition"] = summary.RoundIndexWithinCondition,
                ["rounds_per_condition"] = summary.RoundsPerCondition,
                ["global_round_index"] = summary.GlobalRoundIndex,
                ["allow_non_slot_dynamic_place_fallback"] = summary.AllowNonSlotDynamicPlaceFallback,
                ["use_dynamic_place_pose"] = summary.UseDynamicPlacePose,
                ["use_deposit_zone_slot_allocator"] = summary.UseDepositZoneSlotAllocator,
                ["max_expected_place_distance"] = summary.MaxExpectedPlaceDistance,
                ["max_relaxed_place_distance"] = summary.MaxRelaxedPlaceDistance,
                ["place_candidate_reachability_margin"] = summary.PlaceCandidateReachabilityMargin,
                ["max_place_approach_retries"] = summary.MaxPlaceApproachRetries,
                ["place_failure_recovery_mode"] = summary.PlaceFailureRecoveryMode,
                ["post_place_egress_mode"] = summary.PostPlaceEgressMode,
                ["task_id"] = summary.TaskId,
                ["input_mode"] = summary.InputMode,
                ["drive_profile"] = summary.DriveProfile,
                ["autonomy_policy"] = summary.AutonomyPolicy,
                ["scene_name"] = summary.SceneName,
                ["selected_target_id"] = summary.SelectedTargetId,
                ["selected_target_category"] = summary.SelectedTargetCategory,
                ["place_target_id"] = summary.PlaceTargetId,
                ["success"] = summary.Success,
                ["aborted"] = summary.Aborted,
                ["terminal_state"] = summary.TerminalState,
                ["failure_reason"] = summary.FailureReason,
                ["total_duration_seconds"] = summary.TotalDurationSeconds,
                ["navigation_to_pick_duration_seconds"] = summary.NavigationToPickDurationSeconds,
                ["pick_duration_seconds"] = summary.PickDurationSeconds,
                ["navigation_to_place_duration_seconds"] = summary.NavigationToPlaceDurationSeconds,
                ["place_duration_seconds"] = summary.PlaceDurationSeconds,
                ["error_count"] = summary.ErrorCount,
                ["non_terminal_warning_count"] = summary.NonTerminalWarningCount,
                ["non_terminal_warnings"] = summary.NonTerminalWarnings,
                ["timestamp_start"] = summary.TimestampStart,
                ["timestamp_end"] = summary.TimestampEnd,
                ["notes"] = summary.Notes
            };
        }

        private TrialSummaryPaths WriteSummary(ExperimentTrialSummary summary)
        {
            if (!TryResolveSummaryDirectory(summary, out string directory, out string identityError))
            {
                Debug.LogError(
                    $"{LogPrefix} experiment_trial_summary_write_rejected | reason={identityError} participant_id={summary?.ParticipantId ?? string.Empty} session_id={summary?.SessionId ?? string.Empty}",
                    this);
                return new TrialSummaryPaths(string.Empty, string.Empty, string.Empty);
            }

            TrialSummaryPathPlan pathPlan = BuildTrialSummaryPathPlan(summary, directory);

            try
            {
                Directory.CreateDirectory(directory);

                if (pathPlan.Shortened)
                {
                    LogSummaryPathShortened(summary, pathPlan);
                }

                EnsureParentDirectory(pathPlan.JsonlPath);
                File.AppendAllText(pathPlan.JsonlPath, SerializeJsonObject(BuildSummaryPayload(summary)) + Environment.NewLine, Encoding.UTF8);

                if (!_writeCsvSummary)
                {
                    return new TrialSummaryPaths(directory, string.Empty, pathPlan.JsonlPath);
                }

                EnsureParentDirectory(pathPlan.CsvPath);
                bool writeHeader = !File.Exists(pathPlan.CsvPath);
                using StreamWriter writer = new StreamWriter(pathPlan.CsvPath, append: true, Encoding.UTF8);
                if (writeHeader)
                {
                    writer.WriteLine(GetSummaryCsvHeader());
                }

                writer.WriteLine(ToSummaryCsvRow(summary));
                return new TrialSummaryPaths(directory, pathPlan.CsvPath, pathPlan.JsonlPath);
            }
            catch (Exception exception)
            {
                LogSummaryWriteFailed(summary, pathPlan, exception);
                return new TrialSummaryPaths(directory, string.Empty, string.Empty);
            }
        }

        private void WriteSessionIndex(ExperimentTrialSummary summary, TrialSummaryPaths summaryPaths)
        {
            bool? validityOverride = ResolveValidityOverride();
            ExperimentSessionTrialRecord record = ExperimentSessionTrialRecord.FromSummary(
                summary,
                summaryPaths.CsvPath,
                summaryPaths.JsonlPath,
                ResolveEventPath(),
                ResolveSamplePath(),
                ResolveManifestPath(),
                validityOverride,
                _manualExclusionReason);

            if (!record.ValidForAnalysis)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "experiment_trial_marked_invalid",
                    new Dictionary<string, object>
                    {
                        ["run_id"] = record.RunId,
                        ["participant_id"] = record.ParticipantId,
                        ["session_id"] = record.SessionId,
                        ["trial_id"] = record.TrialId,
                        ["trial_index"] = record.TrialIndex,
                        ["condition_id"] = record.ConditionId,
                        ["round_id"] = record.RoundId,
                        ["round_index_within_condition"] = record.RoundIndexWithinCondition,
                        ["rounds_per_condition"] = record.RoundsPerCondition,
                        ["valid_for_analysis"] = record.ValidForAnalysis,
                        ["exclusion_reason"] = record.ExclusionReason
                    });
            }

            ExperimentSessionIndexWriteResult result = ExperimentSessionIndexWriter.Append(summaryPaths.Directory, record);
            if (!result.Success)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "experiment_session_index_write_failed",
                    new Dictionary<string, object>
                    {
                        ["run_id"] = summary.RunId,
                        ["participant_id"] = summary.ParticipantId,
                        ["session_id"] = summary.SessionId,
                        ["trial_id"] = summary.TrialId,
                        ["trial_index"] = summary.TrialIndex,
                        ["condition_id"] = summary.ConditionId,
                        ["round_id"] = summary.RoundId,
                        ["round_index_within_condition"] = summary.RoundIndexWithinCondition,
                        ["rounds_per_condition"] = summary.RoundsPerCondition,
                        ["reason"] = result.ErrorMessage ?? string.Empty
                    });
                Debug.LogWarning($"{LogPrefix} experiment_session_index_write_failed | trial={summary.TrialId} reason={result.ErrorMessage}", this);
                return;
            }

            if (result.DuplicateTrialIdDetected)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "duplicate_trial_id_detected",
                    new Dictionary<string, object>
                    {
                        ["run_id"] = summary.RunId,
                        ["participant_id"] = summary.ParticipantId,
                        ["session_id"] = summary.SessionId,
                        ["condition_id"] = summary.ConditionId,
                        ["round_id"] = summary.RoundId,
                        ["original_trial_id"] = result.OriginalTrialId,
                        ["written_trial_id"] = result.WrittenTrialId,
                        ["resolution_strategy"] = "suffix_duplicate_trial_id"
                    });
                Debug.LogWarning($"{LogPrefix} duplicate_trial_id_detected | original={result.OriginalTrialId} written={result.WrittenTrialId}", this);
            }

            TiagoExperimentTelemetry.LogEvent(
                "experiment_session_index_updated",
                new Dictionary<string, object>
                {
                    ["run_id"] = summary.RunId,
                    ["participant_id"] = summary.ParticipantId,
                    ["session_id"] = summary.SessionId,
                    ["trial_id"] = result.WrittenTrialId,
                    ["trial_index"] = summary.TrialIndex,
                    ["condition_id"] = summary.ConditionId,
                    ["round_id"] = summary.RoundId,
                    ["round_index_within_condition"] = summary.RoundIndexWithinCondition,
                    ["rounds_per_condition"] = summary.RoundsPerCondition,
                    ["session_trials_csv_path"] = result.CsvPath,
                    ["session_trials_jsonl_path"] = result.JsonlPath,
                    ["session_trials_csv_file"] = result.CsvFileName,
                    ["session_trials_jsonl_file"] = result.JsonlFileName,
                    ["valid_for_analysis"] = record.ValidForAnalysis
                });

            UpdateManifestWithSessionIndexFiles(record.ManifestFilePath, result);
            ExperimentDataPathResolver.UpdateFileIndex(summaryPaths.Directory, emitLog: false);
            TiagoExperimentTelemetry.LogEvent(
                "p45f_file_index_updated",
                new Dictionary<string, object>
                {
                    ["run_id"] = summary.RunId,
                    ["participant_id"] = summary.ParticipantId,
                    ["session_id"] = summary.SessionId,
                    ["session_root"] = summaryPaths.Directory,
                    ["persistent_root"] = Application.persistentDataPath,
                    ["condition_id"] = summary.ConditionId,
                    ["round_id"] = summary.RoundId,
                    ["file_path"] = Path.Combine(summaryPaths.Directory, "file_index.csv"),
                    ["platform"] = Application.platform.ToString(),
                    ["package_name"] = Application.identifier
                });
        }

        private bool? ResolveValidityOverride()
        {
            if (_analysisValidityMode == AnalysisValidityMode.ForceValid)
            {
                return true;
            }

            if (_analysisValidityMode == AnalysisValidityMode.ForceInvalid)
            {
                return false;
            }

            return null;
        }

        private bool TryResolveSummaryDirectory(
            ExperimentTrialSummary summary,
            out string directory,
            out string errorReason)
        {
            directory = string.Empty;
            errorReason = string.Empty;
            ExperimentDataPathResolver.SessionContext context = ExperimentDataPathResolver.CurrentSession;
            if (context.Identity != null)
            {
                if (summary == null ||
                    !string.Equals(summary.ParticipantId, context.ParticipantId, StringComparison.Ordinal) ||
                    !string.Equals(summary.SessionId, context.SessionId, StringComparison.Ordinal))
                {
                    errorReason = "trial_summary_identity_mismatch";
                    return false;
                }

                if (!ExperimentDataPathResolver.TryValidateCurrentIdentity(context.Identity, out errorReason))
                {
                    return false;
                }

                if (summary.ConditionOrderIndex < 0 ||
                    summary.ConditionOrderIndex >= context.ConditionOrder.Length ||
                    !string.Equals(
                        context.ConditionOrder[summary.ConditionOrderIndex],
                        summary.ConditionId,
                        StringComparison.Ordinal))
                {
                    errorReason = "trial_summary_condition_order_mismatch";
                    return false;
                }

                TiagoExperimentLogger activeLogger = TiagoExperimentLogger.Active;
                if (activeLogger != null && !activeLogger.IsBoundTo(context.Identity))
                {
                    errorReason = "trial_summary_logger_identity_mismatch";
                    return false;
                }

                directory = context.SessionRoot;
                return true;
            }

            TiagoExperimentLogger logger = TiagoExperimentLogger.Active;
            if (logger != null && !string.IsNullOrWhiteSpace(logger.RunDirectory))
            {
                directory = logger.RunDirectory;
                return true;
            }

            string fallback = string.IsNullOrWhiteSpace(_fallbackSummaryFolder)
                ? "logs/experiments/step23_trial_summaries"
                : _fallbackSummaryFolder.Trim();
            directory = Path.IsPathRooted(fallback)
                ? fallback
                : Path.Combine(ExperimentDataPathResolver.ResolveDataRoot(), fallback);
            Directory.CreateDirectory(directory);
            return true;
        }

        private static TrialSummaryPathPlan BuildTrialSummaryPathPlan(ExperimentTrialSummary summary, string directory)
        {
            string sessionToken = Sanitize(string.IsNullOrWhiteSpace(summary.SessionId) ? "session" : summary.SessionId);
            string trialToken = BuildShortTrialToken(summary);
            string conditionToken = BuildShortConditionToken(summary.ConditionId);

            TrialSummaryPathPlan plan = CreateSummaryPathPlan(
                directory,
                $"{sessionToken}__{trialToken}__{conditionToken}__trial_summary",
                shortened: false,
                reason: string.Empty);
            if (!SummaryPathExceedsSafeLength(plan))
            {
                return plan;
            }

            plan = CreateSummaryPathPlan(
                directory,
                $"{sessionToken}__{trialToken}__trial_summary",
                shortened: true,
                reason: "condition_token_removed_for_path_length");
            if (!SummaryPathExceedsSafeLength(plan))
            {
                return plan;
            }

            plan = CreateSummaryPathPlan(
                directory,
                $"{trialToken}__{conditionToken}__trial_summary",
                shortened: true,
                reason: "session_token_removed_for_path_length");
            if (!SummaryPathExceedsSafeLength(plan))
            {
                return plan;
            }

            return CreateSummaryPathPlan(
                directory,
                $"{trialToken}__trial_summary",
                shortened: true,
                reason: "minimal_trial_summary_file_name_for_path_length");
        }

        private static TrialSummaryPathPlan CreateSummaryPathPlan(string directory, string prefix, bool shortened, string reason)
        {
            string safePrefix = Sanitize(prefix);
            string csvPath = Path.Combine(directory, $"{safePrefix}.csv");
            string jsonlPath = Path.Combine(directory, $"{safePrefix}.jsonl");
            return new TrialSummaryPathPlan(directory, safePrefix, csvPath, jsonlPath, shortened, reason);
        }

        private static bool SummaryPathExceedsSafeLength(TrialSummaryPathPlan plan)
        {
            return plan.CsvPathLength >= SafeWindowsPathLengthWarningThreshold ||
                   plan.JsonlPathLength >= SafeWindowsPathLengthWarningThreshold;
        }

        private static string BuildShortTrialToken(ExperimentTrialSummary summary)
        {
            if (summary.TrialIndex > 0)
            {
                return $"t{summary.TrialIndex:000}";
            }

            if (!string.IsNullOrWhiteSpace(summary.TrialId))
            {
                string digits = string.Empty;
                foreach (char ch in summary.TrialId)
                {
                    if (char.IsDigit(ch))
                    {
                        digits += ch;
                    }
                }

                if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                {
                    return $"t{parsed:000}";
                }
            }

            return "t000";
        }

        private static string BuildShortConditionToken(string conditionId)
        {
            if (string.IsNullOrWhiteSpace(conditionId))
            {
                return "Cxx";
            }

            string trimmed = conditionId.Trim();
            int separator = trimmed.IndexOf('_');
            string token = separator > 0 ? trimmed.Substring(0, separator) : trimmed;
            return token.Length <= 12 ? token : token.Substring(0, 12);
        }

        private static void EnsureParentDirectory(string path)
        {
            string parent = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parent))
            {
                throw new DirectoryNotFoundException($"Cannot resolve parent directory for path: {path}");
            }

            Directory.CreateDirectory(parent);
        }

        private void LogSummaryPathShortened(ExperimentTrialSummary summary, TrialSummaryPathPlan pathPlan)
        {
            TiagoExperimentTelemetry.LogEvent(
                "experiment_summary_path_shortened",
                new Dictionary<string, object>
                {
                    ["run_id"] = summary.RunId,
                    ["participant_id"] = summary.ParticipantId,
                    ["session_id"] = summary.SessionId,
                    ["trial_id"] = summary.TrialId,
                    ["trial_index"] = summary.TrialIndex,
                    ["condition_id"] = summary.ConditionId,
                    ["reason"] = pathPlan.ShorteningReason,
                    ["safe_path_length_threshold"] = SafeWindowsPathLengthWarningThreshold,
                    ["summary_csv_path"] = pathPlan.CsvPath,
                    ["summary_jsonl_path"] = pathPlan.JsonlPath,
                    ["summary_csv_path_length"] = pathPlan.CsvPathLength,
                    ["summary_jsonl_path_length"] = pathPlan.JsonlPathLength
                });
        }

        private void LogSummaryWriteFailed(ExperimentTrialSummary summary, TrialSummaryPathPlan pathPlan, Exception exception)
        {
            TiagoExperimentTelemetry.LogEvent(
                "experiment_trial_summary_write_failed",
                new Dictionary<string, object>
                {
                    ["run_id"] = summary.RunId,
                    ["participant_id"] = summary.ParticipantId,
                    ["session_id"] = summary.SessionId,
                    ["trial_id"] = summary.TrialId,
                    ["trial_index"] = summary.TrialIndex,
                    ["condition_id"] = summary.ConditionId,
                    ["summary_directory"] = pathPlan.Directory,
                    ["summary_csv_path"] = pathPlan.CsvPath,
                    ["summary_jsonl_path"] = pathPlan.JsonlPath,
                    ["summary_csv_path_length"] = pathPlan.CsvPathLength,
                    ["summary_jsonl_path_length"] = pathPlan.JsonlPathLength,
                    ["exception_type"] = exception.GetType().FullName,
                    ["exception_message"] = exception.Message
                });
            Debug.LogError($"{LogPrefix} experiment_trial_summary_write_failed | trial={summary.TrialId} path={pathPlan.JsonlPath} reason={exception.Message}", this);
        }

        private string ResolveRunId()
        {
            TiagoExperimentLogger logger = TiagoExperimentLogger.Active;
            return logger != null && !string.IsNullOrWhiteSpace(logger.RunId)
                ? logger.RunId
                : "run_pending";
        }

        private static string ResolveSamplePath()
        {
            TiagoExperimentLogger logger = TiagoExperimentLogger.Active;
            return logger != null ? logger.SamplePath : string.Empty;
        }

        private static string ResolveEventPath()
        {
            TiagoExperimentLogger logger = TiagoExperimentLogger.Active;
            return logger != null ? logger.EventPath : string.Empty;
        }

        private static string ResolveManifestPath()
        {
            TiagoExperimentLogger logger = TiagoExperimentLogger.Active;
            return logger != null ? logger.ManifestPath : string.Empty;
        }

        private static void UpdateManifestWithSessionIndexFiles(
            string manifestPath,
            ExperimentSessionIndexWriteResult result)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || result == null || !File.Exists(manifestPath))
            {
                return;
            }

            try
            {
                string json = File.ReadAllText(manifestPath, Encoding.UTF8).Trim();
                if (string.IsNullOrWhiteSpace(json) || json[0] != '{' || json[json.Length - 1] != '}')
                {
                    return;
                }

                TiagoExperimentLogger activeLogger = TiagoExperimentLogger.Active;
                if (activeLogger != null &&
                    !string.IsNullOrWhiteSpace(activeLogger.ManifestPath) &&
                    !string.Equals(
                        Path.GetFullPath(activeLogger.ManifestPath),
                        Path.GetFullPath(manifestPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogError(
                        $"{LogPrefix} session_manifest_invariant_failed | operation=session_index_update error_reason=manifest_path_mismatch manifest={manifestPath} active_manifest={activeLogger.ManifestPath}");
                    return;
                }

                if (activeLogger != null &&
                    !string.IsNullOrWhiteSpace(activeLogger.ManifestPath) &&
                    string.Equals(
                        Path.GetFullPath(activeLogger.ManifestPath),
                        Path.GetFullPath(manifestPath),
                        StringComparison.OrdinalIgnoreCase) &&
                    !activeLogger.TryValidateManifestJsonForActiveSession(json, out string manifestError))
                {
                    Debug.LogError(
                        $"{LogPrefix} session_manifest_invariant_failed | operation=session_index_update error_reason={manifestError} manifest={manifestPath}");
                    return;
                }

                json = RemoveJsonField(json, "session_trials_csv_file");
                json = RemoveJsonField(json, "session_trials_jsonl_file");
                json = RemoveJsonField(json, "session_trials_csv_path");
                json = RemoveJsonField(json, "session_trials_jsonl_path");

                string additions =
                    $"\"session_trials_csv_file\":\"{EscapeJson(result.CsvFileName)}\"," +
                    $"\"session_trials_jsonl_file\":\"{EscapeJson(result.JsonlFileName)}\"," +
                    $"\"session_trials_csv_path\":\"{EscapeJson(result.CsvPath)}\"," +
                    $"\"session_trials_jsonl_path\":\"{EscapeJson(result.JsonlPath)}\"";

                string body = json.Substring(1, json.Length - 2).Trim();
                string updated = string.IsNullOrWhiteSpace(body)
                    ? "{" + additions + "}"
                    : "{" + body.TrimEnd(',') + "," + additions + "}";
                if (activeLogger != null &&
                    !string.IsNullOrWhiteSpace(activeLogger.ManifestPath) &&
                    string.Equals(
                        Path.GetFullPath(activeLogger.ManifestPath),
                        Path.GetFullPath(manifestPath),
                        StringComparison.OrdinalIgnoreCase) &&
                    !activeLogger.TryValidateManifestJsonForActiveSession(updated, out manifestError))
                {
                    Debug.LogError(
                        $"{LogPrefix} session_manifest_invariant_failed | operation=session_index_update_post_apply error_reason={manifestError} manifest={manifestPath}");
                    return;
                }

                File.WriteAllText(manifestPath, updated, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Manifest update is best-effort; the session index files themselves remain authoritative.
            }
        }

        private static string RemoveJsonField(string json, string key)
        {
            string quotedKey = $"\"{key}\":";
            int keyIndex = json.IndexOf(quotedKey, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return json;
            }

            int start = keyIndex;
            if (start > 1 && json[start - 1] == ',')
            {
                start--;
            }

            int valueStart = keyIndex + quotedKey.Length;
            int end = FindJsonFieldEnd(json, valueStart);
            if (end < json.Length && json[end] == ',')
            {
                end++;
            }

            return json.Remove(start, end - start);
        }

        private static int FindJsonFieldEnd(string json, int valueStart)
        {
            bool inString = false;
            bool escaped = false;
            for (int i = valueStart; i < json.Length; i++)
            {
                char ch = json[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString && (ch == ',' || ch == '}'))
                {
                    return i;
                }
            }

            return json.Length;
        }

        private string BuildTrialId(int trialIndex)
        {
            string prefix = string.IsNullOrWhiteSpace(_trialIdPrefix) ? "trial" : _trialIdPrefix;
            return $"{prefix}_{trialIndex:000}";
        }

        private static bool TryGetPayloadString(Dictionary<string, object> payload, string key, out string value)
        {
            value = string.Empty;
            if (payload == null || !payload.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            value = raw.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static string ResolveSceneName()
        {
            Scene scene = SceneManager.GetActiveScene();
            return scene.IsValid() && !string.IsNullOrWhiteSpace(scene.name)
                ? scene.name
                : "scene_Unknown";
        }

        private static string GetSummaryCsvHeader()
        {
            return "runId,participantId,sessionId,trialId,trialIndex,conditionId,taskId,inputMode,driveProfile,autonomyPolicy,sceneName,selectedTargetId,selectedTargetCategory,placeTargetId,success,aborted,terminalState,failureReason,totalDurationSeconds,navigationToPickDurationSeconds,pickDurationSeconds,navigationToPlaceDurationSeconds,placeDurationSeconds,errorCount,nonTerminalWarningCount,nonTerminalWarnings,timestampStart,timestampEnd,notes,conditionName,conditionOrderIndex,robotEnabled,voiceEnabled,assistanceMode,spawnGenerationMode,roundId,roundIndex,roundIndexWithinCondition,roundsPerCondition,globalRoundIndex,allowNonSlotDynamicPlaceFallback,useDynamicPlacePose,useDepositZoneSlotAllocator,maxExpectedPlaceDistance,maxRelaxedPlaceDistance,placeCandidateReachabilityMargin,maxPlaceApproachRetries,placeFailureRecoveryMode,postPlaceEgressMode";
        }

        private static string ToSummaryCsvRow(ExperimentTrialSummary summary)
        {
            return string.Join(",",
                EscapeCsv(summary.RunId),
                EscapeCsv(summary.ParticipantId),
                EscapeCsv(summary.SessionId),
                EscapeCsv(summary.TrialId),
                summary.TrialIndex.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(summary.ConditionId),
                EscapeCsv(summary.TaskId),
                EscapeCsv(summary.InputMode),
                EscapeCsv(summary.DriveProfile),
                EscapeCsv(summary.AutonomyPolicy),
                EscapeCsv(summary.SceneName),
                EscapeCsv(summary.SelectedTargetId),
                EscapeCsv(summary.SelectedTargetCategory),
                EscapeCsv(summary.PlaceTargetId),
                summary.Success ? "true" : "false",
                summary.Aborted ? "true" : "false",
                EscapeCsv(summary.TerminalState),
                EscapeCsv(summary.FailureReason),
                FormatFloat(summary.TotalDurationSeconds),
                FormatFloat(summary.NavigationToPickDurationSeconds),
                FormatFloat(summary.PickDurationSeconds),
                FormatFloat(summary.NavigationToPlaceDurationSeconds),
                FormatFloat(summary.PlaceDurationSeconds),
                summary.ErrorCount.ToString(CultureInfo.InvariantCulture),
                summary.NonTerminalWarningCount.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(summary.NonTerminalWarnings),
                EscapeCsv(summary.TimestampStart),
                EscapeCsv(summary.TimestampEnd),
                EscapeCsv(summary.Notes),
                EscapeCsv(summary.ConditionName),
                summary.ConditionOrderIndex.ToString(CultureInfo.InvariantCulture),
                summary.RobotEnabled ? "true" : "false",
                summary.VoiceEnabled ? "true" : "false",
                EscapeCsv(summary.AssistanceMode),
                EscapeCsv(summary.SpawnGenerationMode),
                EscapeCsv(summary.RoundId),
                summary.RoundIndex.ToString(CultureInfo.InvariantCulture),
                summary.RoundIndexWithinCondition.ToString(CultureInfo.InvariantCulture),
                summary.RoundsPerCondition.ToString(CultureInfo.InvariantCulture),
                summary.GlobalRoundIndex.ToString(CultureInfo.InvariantCulture),
                summary.AllowNonSlotDynamicPlaceFallback ? "true" : "false",
                summary.UseDynamicPlacePose ? "true" : "false",
                summary.UseDepositZoneSlotAllocator ? "true" : "false",
                FormatFloat(summary.MaxExpectedPlaceDistance),
                FormatFloat(summary.MaxRelaxedPlaceDistance),
                FormatFloat(summary.PlaceCandidateReachabilityMargin),
                summary.MaxPlaceApproachRetries.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(summary.PlaceFailureRecoveryMode),
                EscapeCsv(summary.PostPlaceEgressMode));
        }

        private static string SerializeJsonObject(Dictionary<string, object> payload)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> pair in payload)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append('"').Append(EscapeJson(pair.Key)).Append("\":").Append(SerializeJsonValue(pair.Value));
                first = false;
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string SerializeJsonValue(object value)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string text:
                    return $"\"{EscapeJson(text)}\"";
                case bool flag:
                    return flag ? "true" : "false";
                case int intValue:
                    return intValue.ToString(CultureInfo.InvariantCulture);
                case float floatValue:
                    return float.IsNaN(floatValue) || float.IsInfinity(floatValue)
                        ? "null"
                        : floatValue.ToString("G9", CultureInfo.InvariantCulture);
                default:
                    return $"\"{EscapeJson(value.ToString())}\"";
            }
        }

        private static string FormatFloat(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? string.Empty
                : value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string EscapeCsv(string value)
        {
            value ??= string.Empty;
            if (!value.Contains(",") && !value.Contains("\"") && !value.Contains("\n") && !value.Contains("\r"))
            {
                return value;
            }

            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "value";
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value.Replace(' ', '_');
        }

        private readonly struct TrialSummaryPaths
        {
            public TrialSummaryPaths(string directory, string csvPath, string jsonlPath)
            {
                Directory = directory ?? string.Empty;
                CsvPath = csvPath ?? string.Empty;
                JsonlPath = jsonlPath ?? string.Empty;
            }

            public string Directory { get; }
            public string CsvPath { get; }
            public string JsonlPath { get; }
        }

        private readonly struct TrialSummaryPathPlan
        {
            public TrialSummaryPathPlan(string directory, string prefix, string csvPath, string jsonlPath, bool shortened, string shorteningReason)
            {
                Directory = directory ?? string.Empty;
                Prefix = prefix ?? string.Empty;
                CsvPath = csvPath ?? string.Empty;
                JsonlPath = jsonlPath ?? string.Empty;
                Shortened = shortened;
                ShorteningReason = shorteningReason ?? string.Empty;
            }

            public string Directory { get; }
            public string Prefix { get; }
            public string CsvPath { get; }
            public string JsonlPath { get; }
            public bool Shortened { get; }
            public string ShorteningReason { get; }
            public int CsvPathLength => CsvPath.Length;
            public int JsonlPathLength => JsonlPath.Length;
        }
    }
}
