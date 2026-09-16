using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autonomy.Core;
using Autonomy.Domain;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class ExperimentSessionOrchestrator : MonoBehaviour
    {
        private const string LogPrefix = "[ExperimentSessionOrchestrator]";
        private const string PauseLogPrefix = "[P46D-PAUSE]";
        private const string DefaultTaskId = "AssistedRoundPickAndPlace";
        private const float IncidentRoundPalletUnsafeDistanceMeters = 0.35f;
        public const string ConditionOrderAssignmentMissingForResumeReason = "condition_order_assignment_missing_for_resume";
        public const string ConditionOrderAssignmentMissingForRestartReason = "condition_order_assignment_missing_for_restart";

        [Header("Session")]
        [SerializeField] private ExperimentRunMode _runMode = ExperimentRunMode.Orchestrated2x2;
        [SerializeField] private string _participantId = "";
        [SerializeField] private string _sessionId = "";
        [SerializeField] private string _taskId = DefaultTaskId;
        [SerializeField] private string _inputMode = "MultimodalSimulated";
        [SerializeField] private int _selectedConditionIndex;
        [SerializeField, Min(1)] private int _roundsPerCondition = 2;
        [SerializeField] private SpawnGenerationMode _defaultSpawnGenerationMode = SpawnGenerationMode.RandomBalanced;
        [SerializeField] private bool _autoCreateDefaultConditions = true;
        [SerializeField] private List<Experiment2x2ConditionDefinition> _conditions = new();

        [Header("References")]
        [SerializeField] private ExperimentConditionConfigBehaviour _conditionConfig;
        [SerializeField] private ExperimentInstrumentationController _instrumentation;
        [SerializeField] private SpawnManager _spawnManager;
        [SerializeField] private RoundManager _roundManager;
        [SerializeField] private RobotAssistanceRoundCoordinator _assistanceCoordinator;
        [SerializeField] private VoiceRecognitionController _voiceRecognitionController;
        [SerializeField] private VoiceAutonomyCommandConnector _voiceConnector;
        [SerializeField] private MultimodalAutonomyCommandBridge _commandBridge;
        [SerializeField] private AutonomousRobotAdapter _robotAdapter;
        [SerializeField] private ExperimentXrRigResetter _xrRigResetter;

        [Header("Inspector Commands")]
        [SerializeField] private bool _startSession;
        [SerializeField] private bool _startNextTrial;
        [SerializeField] private bool _restartCurrentTrial;
        [SerializeField] private bool _endCurrentTrial;
        [SerializeField] private bool _endSession;
        [SerializeField] private bool _debugRun2x2SanitySequence;

        [Header("Debug Voice")]
        [SerializeField] private string _manualTranscriptForSanity = "lleva la caja a a la zona a";
        [SerializeField] private float _debugSequenceStepDelaySeconds = 0.25f;
        [SerializeField] private int _cleanupWaitTimeoutFrames = 8;
        [SerializeField] private float _robotPoseResetToleranceMeters = 0.15f;
        [SerializeField] private float _robotPoseResetYawToleranceDegrees = 5f;

        private bool _sessionActive;
        private bool _trialActive;
        private bool _currentTrialCompleted;
        private bool _trialTransitionBusy;
        private int _nextTrialIndex = 1;
        private int _currentPlanEntryIndex = -1;
        private int _nextPlanEntryIndex;
        private int _internalTrialAttemptIndex;
        private int _currentTrialIndex;
        private int _currentRoundIndex = 1;
        private int _currentRoundIndexWithinCondition = 1;
        private int _currentGlobalRoundIndex = 1;
        private string _currentTrialId = string.Empty;
        private string _currentRoundId = string.Empty;
        private Experiment2x2ConditionDefinition _currentCondition;
        private ExperimentRuntimeContext _currentContext;
        private Coroutine _debugSequenceCoroutine;
        private Coroutine _trialPreparationCoroutine;
        private Coroutine _trialEndCleanupCoroutine;
        private ExperimentSimulationPauseAuthority.PauseLease _trialXrGatePauseLease;
        private Vector3 _initialRobotPosition;
        private Quaternion _initialRobotRotation;
        private Transform _robotPoseRoot;
        private Vector3 _initialRobotRootPosition;
        private Quaternion _initialRobotRootRotation;
        private Transform _authoritativePhysicsRoot;
        private Vector3 _initialAuthoritativePhysicsPosition;
        private Quaternion _initialAuthoritativePhysicsRotation;
        private bool _hasInitialRobotPose;
        private Transform _robotPoseReference;
        private RoundManager _subscribedRoundManager;
        private string _completedTrialId = string.Empty;
        private readonly HashSet<string> _orchestratorCompletionLoggedTrialIds = new();
        private int _startNextTrialInvocationCounter;
        private int _startNextTrialDepth;
        private bool _isStartNextTrialRunning;
        private int _activeStartNextTrialInvocationId;
        private bool _completionDuringStartNextTrialDetected;
        private bool _lastTrialClosedByTechnicalIncident;
        private bool _protocolRestartFirstTrialPending;
        private bool _sessionResumedFromSavedExit;
        private string _resumedSavedExitOriginalSessionId = string.Empty;
        private string _resumedSavedExitQuestionnaireCode = string.Empty;
        private bool _telemetryDiagnosticsSubscribed;
        private bool _loggingStartNextTrialReentrantDiagnostic;
        private List<ExperimentTrialPlanEntry> _sessionTrialPlan = new();
        private readonly List<string> _runtimeConditionOrderIds = new();

        public bool SessionActive => _sessionActive;
        public bool TrialActive => _trialActive || (_instrumentation != null && _instrumentation.TrialActive);
        public bool TrialTransitionBusy => _trialTransitionBusy || _trialPreparationCoroutine != null || _trialEndCleanupCoroutine != null;
        public string ParticipantId => _participantId ?? string.Empty;
        public string SessionId => _sessionId ?? string.Empty;
        public string CurrentConditionId => _currentContext != null ? _currentContext.ConditionId : ResolveCurrentConditionIdForDiagnostics();
        public string CurrentConditionName => _currentContext != null ? _currentContext.ConditionName : (_currentCondition != null ? _currentCondition.ConditionName : string.Empty);
        public string CurrentTrialId => _currentTrialId ?? string.Empty;
        public string CurrentRoundId => _currentRoundId ?? string.Empty;
        public int RoundsPerCondition => Mathf.Max(1, _roundsPerCondition);
        public int CurrentRoundIndexWithinCondition => _currentRoundIndexWithinCondition;
        public int CurrentRoundIndex => _currentRoundIndex;
        public int CurrentGlobalRoundIndex => _currentGlobalRoundIndex;
        public int TotalPlannedTrials => GetPlannedTrialCount();
        public int NextTrialIndex => _nextTrialIndex;
        public int CurrentPlanEntryIndex => _currentPlanEntryIndex;
        public int NextPlanEntryIndex => _nextPlanEntryIndex;
        public int InternalTrialAttemptIndex => _internalTrialAttemptIndex;
        public IReadOnlyList<string> RuntimeConditionOrderIds => _runtimeConditionOrderIds;
        public bool SessionResumedFromSavedExit => _sessionResumedFromSavedExit;
        public string LastSavedExitResumeFailureReason { get; private set; } = string.Empty;
        public string LastRuntimeRestartFailureReason { get; private set; } = string.Empty;
        public bool LastRuntimeRestartFailureWasAfterPointOfNoReturn { get; private set; }
        public string RestartDiagnosticRobotMode => _robotAdapter != null && _robotAdapter.Blackboard != null ? _robotAdapter.Blackboard.CurrentMode.ToString() : string.Empty;
        public string RestartDiagnosticCurrentTask => _robotAdapter != null ? _robotAdapter.ActiveP40TaskInstanceId ?? string.Empty : string.Empty;
        public bool RestartDiagnosticPendingTask => _assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox;
        public string RestartDiagnosticHeldObjectId => _robotAdapter != null ? _robotAdapter.HeldObjectId ?? string.Empty : string.Empty;

        private void Awake()
        {
            EnsureDefaultConditions();
            TryResolveReferences();
            ConfigureOrchestratedConsumers();
            CaptureInitialRobotPoseIfNeeded();
            SubscribeTelemetryDiagnostics();
        }

        private void OnEnable()
        {
            SubscribeTelemetryDiagnostics();
        }

        private void OnDisable()
        {
            Coroutine trialPreparationCoroutine = _trialPreparationCoroutine;
            _trialPreparationCoroutine = null;
            if (trialPreparationCoroutine != null)
            {
                StopCoroutine(trialPreparationCoroutine);
            }

            Coroutine trialEndCleanupCoroutine = _trialEndCleanupCoroutine;
            _trialEndCleanupCoroutine = null;
            if (trialEndCleanupCoroutine != null)
            {
                StopCoroutine(trialEndCleanupCoroutine);
            }

            _trialTransitionBusy = false;
            ReleaseTrialXrGatePause();
            UnsubscribeRoundCompleted();
            UnsubscribeTelemetryDiagnostics();
        }

        private void ReleaseTrialXrGatePause()
        {
            ExperimentSimulationPauseAuthority.PauseLease lease = _trialXrGatePauseLease;
            _trialXrGatePauseLease = null;
            lease?.Dispose();
        }

        private void Update()
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                return;
            }

            if (_startSession)
            {
                _startSession = false;
                StartSession();
            }

            if (_startNextTrial)
            {
                _startNextTrial = false;
                StartNextTrial();
            }

            if (_restartCurrentTrial)
            {
                _restartCurrentTrial = false;
                RestartCurrentTrial();
            }

            if (_endCurrentTrial)
            {
                _endCurrentTrial = false;
                EndCurrentTrial();
            }

            if (_endSession)
            {
                _endSession = false;
                EndSession();
            }

            if (_debugRun2x2SanitySequence)
            {
                _debugRun2x2SanitySequence = false;
                DebugRun2x2SanitySequence();
            }
        }

        [ContextMenu("Experiment/Start Session")]
        public void StartSession()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning($"{LogPrefix} Start Session only runs in Play Mode.", this);
                return;
            }

            TryResolveReferences();
            ConfigureOrchestratedConsumers();
            EnsureDefaultConditions();
            TiagoExperimentLogger.Active?.PrepareForSessionTransition("orchestrator_new_session_start");
            Debug.Log($"[P46D-03] orchestrator_start_session_before_prepare | participant_id={_participantId ?? string.Empty} session_id={_sessionId ?? string.Empty} session_active={_sessionActive} trial_active={_trialActive}");
            ExperimentDataPathResolver.PrepareForNewSessionStart();
            Debug.Log($"[P46D-03] orchestrator_start_session_after_prepare | has_active_storage_session={ExperimentDataPathResolver.HasActiveSession} storage_session_closed={ExperimentDataPathResolver.IsSessionClosed}");
            if (_runMode == ExperimentRunMode.Orchestrated2x2 &&
                (_trialActive || (_instrumentation != null && _instrumentation.TrialActive) || (_roundManager != null && _roundManager.RoundActive)))
            {
                LogEvent("experiment_session_aborted", BuildFailurePayload("active_trial_or_round_before_start_session"));
                return;
            }

            ResetPreSessionDiagnosticContext();
            _instrumentation?.PrepareForNewSession();

            Debug.Log($"[P46D-03] orchestrator_start_session_before_ids | participant_id={_participantId ?? string.Empty} session_id={_sessionId ?? string.Empty}");
            if (ExperimentDataPathResolver.RequiresAutomaticParticipantId(_participantId))
            {
                _participantId = ExperimentDataPathResolver.CreateParticipantId();
            }

            if (ExperimentDataPathResolver.RequiresAutomaticSessionId(_sessionId))
            {
                _sessionId = ExperimentDataPathResolver.CreateSessionId();
            }
            Debug.Log($"[P46D-03] orchestrator_start_session_after_ids | participant_id={_participantId ?? string.Empty} session_id={_sessionId ?? string.Empty}");

            if (_runtimeConditionOrderIds.Count == 0 && !TryConfigureNewParticipationConditionOrder())
            {
                LogEvent(
                    "experiment_session_start_failed",
                    BuildFailurePayload("condition_order_assignment_failed"));
                return;
            }

            List<string> conditionOrder = ResolveRuntimeConditionSequence()
                .Where(condition => condition != null)
                .Select(condition => condition.ConditionId)
                .ToList();
            Debug.Log($"[P46D-03] orchestrator_start_session_before_configure_session | participant_id={_participantId ?? string.Empty} session_id={_sessionId ?? string.Empty} condition_count={conditionOrder.Count}");
            ExperimentDataPathResolver.SessionContext storageContext = ExperimentDataPathResolver.ConfigureSession(
                _participantId,
                _sessionId,
                conditionOrder,
                RoundsPerCondition);
            Debug.Log($"[P46D-03] orchestrator_start_session_after_configure_session | participant_id={storageContext.ParticipantId} session_id={storageContext.SessionId} questionnaire_code={storageContext.QuestionnaireCode} session_root={storageContext.SessionRoot}");
            if (string.IsNullOrWhiteSpace(storageContext.SessionRoot) ||
                string.IsNullOrWhiteSpace(storageContext.QuestionnaireCode))
            {
                LogEvent("experiment_session_start_failed", BuildFailurePayload("questionnaire_identity_persistence_failed"));
                return;
            }

            _participantId = storageContext.ParticipantId;
            _sessionId = storageContext.SessionId;

            if (_instrumentation != null && !_instrumentation.ApplyAuthoritativeSessionIdentity(storageContext))
            {
                ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                    _sessionId,
                    ExperimentSessionIdHistoryStore.AbandonedStatus,
                    "instrumentation_identity_sync_failed",
                    out _);
                ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.AbandonedStatus);
                LogEvent("experiment_session_start_failed", BuildFailurePayload("instrumentation_identity_sync_failed"));
                return;
            }

            TiagoExperimentLogger activeLogger = TiagoExperimentLogger.Active;
            if (activeLogger != null && !activeLogger.BindToSession(storageContext, "orchestrator_new_session", createRunFilesImmediately: true))
            {
                ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                    _sessionId,
                    ExperimentSessionIdHistoryStore.AbandonedStatus,
                    "logger_identity_bind_failed",
                    out _);
                ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.AbandonedStatus);
                LogEvent("experiment_session_start_failed", BuildFailurePayload("logger_identity_bind_failed"));
                return;
            }

            if (!ExperimentDataPathResolver.EmitQuestionnaireCodeGenerated(storageContext))
            {
                ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                    _sessionId,
                    ExperimentSessionIdHistoryStore.AbandonedStatus,
                    "questionnaire_generation_event_identity_failed",
                    out _);
                ExperimentDataPathResolver.EndCurrentSession(ExperimentSessionIdHistoryStore.AbandonedStatus);
                return;
            }

            Debug.Log($"[P46D-03] orchestrator_start_session_before_activate | participant_id={_participantId ?? string.Empty} session_id={_sessionId ?? string.Empty}");
            _internalTrialAttemptIndex = 0;
            _sessionActive = true;
            ResetPlanForSession();
            LogEvent(
                "p45f_participant_id_selected",
                new Dictionary<string, object>
                {
                    ["participant_id"] = _participantId,
                    ["session_id"] = _sessionId,
                    ["session_root"] = storageContext.SessionRoot,
                    ["persistent_root"] = storageContext.PersistentRoot,
                    ["platform"] = Application.platform.ToString(),
                    ["package_name"] = Application.identifier
                });
            LogEvent("experiment_session_started", BuildBasePayload());
        }

        public bool StartSessionFromRuntime(int participantId, IReadOnlyList<string> conditionOrderIds)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning($"{LogPrefix} Runtime session start only runs in Play Mode.", this);
                return false;
            }

            if (_sessionActive || _trialActive || (_instrumentation != null && _instrumentation.TrialActive))
            {
                LogEvent("experiment_runtime_session_start_blocked", BuildFailurePayload("session_or_trial_already_active"));
                return false;
            }

            TryResolveReferences();
            EnsureDefaultConditions();
            _participantId = string.Empty;
            _sessionId = string.Empty;
            _runtimeConditionOrderIds.Clear();
            if (conditionOrderIds != null && conditionOrderIds.Count > 0 &&
                !ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(conditionOrderIds))
            {
                LogEvent(
                    "experiment_runtime_session_start_blocked",
                    BuildFailurePayload("invalid_authoritative_condition_order"));
                return false;
            }

            if (ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(conditionOrderIds))
            {
                if (!TryConfigureRuntimeConditionOrder(conditionOrderIds))
                {
                    LogEvent(
                        "experiment_runtime_session_start_blocked",
                        BuildFailurePayload("invalid_authoritative_condition_order"));
                    return false;
                }
            }

            StartSession();
            LogEvent("experiment_runtime_protocol_session_started", BuildRuntimeProtocolPayload("runtime_session_started"));
            return _sessionActive;
        }

        public bool StartSessionFromSavedExitCheckpoint(
            ExperimentSessionIdHistoryEntry checkpoint,
            IReadOnlyList<string> conditionOrderIds)
        {
            LastSavedExitResumeFailureReason = string.Empty;
            if (!Application.isPlaying)
            {
                Debug.LogWarning($"{LogPrefix} Saved-exit checkpoint resume only runs in Play Mode.", this);
                LastSavedExitResumeFailureReason = "play_mode_required";
                return false;
            }

            if (checkpoint == null || string.IsNullOrWhiteSpace(checkpoint.session_id))
            {
                return FailSavedExitResume("checkpoint_missing");
            }

            if (_sessionActive || _trialActive || (_instrumentation != null && _instrumentation.TrialActive))
            {
                return FailSavedExitResume("session_or_trial_already_active");
            }

            _instrumentation?.PrepareForNewSession();

            TryResolveReferences();
            ConfigureOrchestratedConsumers();
            EnsureDefaultConditions();
            _sessionResumedFromSavedExit = false;
            _resumedSavedExitOriginalSessionId = string.Empty;
            _resumedSavedExitQuestionnaireCode = string.Empty;
            string checkpointParticipantId = checkpoint.user_id ?? string.Empty;
            string checkpointSessionId = checkpoint.session_id ?? string.Empty;
            if (!TryResolveExistingConditionOrderAssignment(
                    checkpointParticipantId,
                    ConditionOrderAssignmentMissingForResumeReason,
                    "condition_order_assignment_unreadable_for_resume",
                    out ExperimentConditionOrderAssignment participantAssignment,
                    out string assignmentFailureReason))
            {
                return FailSavedExitResume(assignmentFailureReason);
            }

            ExperimentDataPathResolver.TryReadPersistedSessionConditionOrder(
                checkpointParticipantId,
                checkpointSessionId,
                out List<string> persistedSessionOrder,
                out string sessionOrderReadReason);
            if (!ExperimentSessionIdHistoryStore.TryResolveSavedExitCheckpoint(
                    checkpoint,
                    persistedSessionOrder,
                    conditionOrderIds,
                    participantAssignment.condition_order_ids,
                    out ExperimentSavedExitCheckpointResolution checkpointResolution,
                    out string resumeValidationResult))
            {
                Debug.LogError($"{PauseLogPrefix} saved_exit_resume_checkpoint_invalid | previous_session_id={checkpointSessionId} questionnaire_code={checkpoint.questionnaire_code} saved_condition_id={checkpoint.saved_condition_id} saved_condition_order_index={checkpoint.saved_condition_order_index} saved_visible_prueba={checkpoint.saved_visible_prueba} saved_round_index={checkpoint.saved_round_index} resume_policy={checkpoint.resume_policy} resume_validation_result={resumeValidationResult} session_order_read_result={sessionOrderReadReason}");
                return FailSavedExitResume(resumeValidationResult);
            }

            if (!checkpointResolution.ConditionOrderIds.SequenceEqual(
                    participantAssignment.condition_order_ids,
                    StringComparer.Ordinal))
            {
                return FailSavedExitResume("condition_order_assignment_mismatch_for_resume");
            }

            _participantId = checkpointParticipantId;
            _sessionId = checkpointSessionId;
            _runtimeConditionOrderIds.Clear();
            if (!TryConfigureRuntimeConditionOrder(checkpointResolution.ConditionOrderIds))
            {
                return FailSavedExitResume("invalid_authoritative_condition_order");
            }

            List<string> conditionOrder = ResolveRuntimeConditionSequence()
                .Where(condition => condition != null)
                .Select(condition => condition.ConditionId)
                .ToList();
            TiagoExperimentLogger.Active?.PrepareForSessionTransition("orchestrator_saved_exit_resume");
            ExperimentDataPathResolver.PrepareForNewSessionStart();
            ExperimentDataPathResolver.SessionContext storageContext = ExperimentDataPathResolver.ConfigureExistingSessionForResume(
                checkpoint.user_id,
                checkpoint.session_id,
                checkpoint.questionnaire_code,
                conditionOrder,
                RoundsPerCondition,
                checkpoint.questionnaire_code_scheme);
            if (string.IsNullOrWhiteSpace(storageContext.SessionRoot))
            {
                return FailSavedExitResume("resume_storage_configuration_failed");
            }

            _participantId = storageContext.ParticipantId;
            _sessionId = storageContext.SessionId;
            if (_instrumentation != null && !_instrumentation.ApplyAuthoritativeSessionIdentity(storageContext))
            {
                return FailSavedExitResume("resume_instrumentation_identity_sync_failed");
            }

            TiagoExperimentLogger resumeLogger = TiagoExperimentLogger.Active;
            if (resumeLogger != null && !resumeLogger.BindToSession(storageContext, "orchestrator_saved_exit_resume", createRunFilesImmediately: true))
            {
                return FailSavedExitResume("resume_logger_identity_bind_failed");
            }
            _sessionActive = true;
            ResetPlanForSession();
            _internalTrialAttemptIndex = checkpointResolution.InternalTrialAttemptIndex;

            string savedConditionId = checkpointResolution.ConditionId;
            if (string.IsNullOrWhiteSpace(savedConditionId))
            {
                Debug.LogWarning($"{PauseLogPrefix} saved_exit_resume_checkpoint_invalid | previous_session_id={checkpoint.session_id} questionnaire_code={checkpoint.questionnaire_code} reason=condition_id_empty saved_visible_prueba={checkpoint.saved_visible_prueba} saved_round_index={checkpoint.saved_round_index}");
                return FailSavedExitResume("condition_id_empty");
            }

            Debug.Log($"{PauseLogPrefix} saved_exit_resume_checkpoint_validated | previous_session_id={checkpoint.session_id} questionnaire_code={checkpoint.questionnaire_code} saved_condition_id={savedConditionId} saved_condition_order_index={checkpointResolution.ConditionOrderIndex} saved_visible_prueba={checkpointResolution.VisiblePruebaNumber} saved_round_index={checkpointResolution.SavedRoundIndex} resume_policy={checkpointResolution.ResumePolicy} resume_validation_result={resumeValidationResult} order_source={checkpointResolution.OrderSource} compatibility_fallback={checkpointResolution.CompatibilityFallback}");
            if (!SeekNextPlanEntryToConditionStart(savedConditionId, out string seekReason))
            {
                Debug.LogWarning($"{PauseLogPrefix} saved_exit_resume_checkpoint_invalid | previous_session_id={checkpoint.session_id} questionnaire_code={checkpoint.questionnaire_code} saved_condition_id={savedConditionId} reason={seekReason}");
                return FailSavedExitResume(seekReason);
            }

            ExperimentRuntimeProtocolSnapshot snapshot = GetRuntimeProtocolSnapshot();
            string originalOrder = string.Join(" -> ", checkpointResolution.ConditionOrderIds);
            string restoredOrder = string.Join(" -> ", _runtimeConditionOrderIds);
            bool conditionOrderMatch = checkpointResolution.ConditionOrderIds.SequenceEqual(_runtimeConditionOrderIds, StringComparer.Ordinal);
            bool resumeStateMatch = conditionOrderMatch &&
                string.Equals(savedConditionId, snapshot?.NextConditionId, StringComparison.Ordinal) &&
                checkpointResolution.ConditionOrderIndex == (snapshot?.NextConditionOrderIndex ?? -1) &&
                checkpointResolution.VisiblePruebaNumber == (snapshot?.NextVisiblePruebaNumber ?? 0) &&
                checkpointResolution.RestoredRoundIndex == (snapshot?.NextRoundIndexWithinCondition ?? 0);
            Debug.Log($"{PauseLogPrefix} saved_exit_resume_validation | saved_exit_original_condition_order={originalOrder} saved_exit_restored_condition_order={restoredOrder} condition_order_match={conditionOrderMatch} saved_condition_id={savedConditionId} restored_condition_id={snapshot?.NextConditionId ?? string.Empty} saved_condition_order_index={checkpointResolution.ConditionOrderIndex} restored_condition_order_index={snapshot?.NextConditionOrderIndex ?? -1} saved_visible_prueba={checkpointResolution.VisiblePruebaNumber} restored_visible_prueba={snapshot?.NextVisiblePruebaNumber ?? 0} saved_round_index={checkpointResolution.SavedRoundIndex} restored_round_index={snapshot?.NextRoundIndexWithinCondition ?? 0} resume_policy={checkpointResolution.ResumePolicy} partial_trial_close_reason={checkpointResolution.PartialTrialCloseReason} internal_trial_attempt_index={checkpointResolution.InternalTrialAttemptIndex} visible_prueba_number={snapshot?.NextVisiblePruebaNumber ?? 0} resume_validation_result={(resumeStateMatch ? resumeValidationResult : "restored_state_mismatch")}");
            if (!resumeStateMatch)
            {
                Debug.LogError($"{PauseLogPrefix} saved_exit_resume_checkpoint_invalid | previous_session_id={checkpoint.session_id} questionnaire_code={checkpoint.questionnaire_code} reason=restored_state_mismatch");
                _sessionActive = false;
                ClearRuntimePlan();
                ExperimentDataPathResolver.PrepareForNewSessionStart();
                return FailSavedExitResume("restored_state_mismatch");
            }

            _sessionResumedFromSavedExit = true;
            _resumedSavedExitOriginalSessionId = checkpoint.session_id ?? string.Empty;
            _resumedSavedExitQuestionnaireCode = checkpoint.questionnaire_code ?? string.Empty;
            if (!ExperimentSessionIdHistoryStore.TryUpdateSessionStatus(
                    _sessionId,
                    ExperimentSessionIdHistoryStore.ActiveStatus,
                    "saved_exit_resume_validated",
                    out _))
            {
                _sessionActive = false;
                return FailSavedExitResume("resume_status_transition_rejected");
            }
            Debug.Log($"{PauseLogPrefix} saved_exit_resume_session_linked | previous_session_id={checkpoint.session_id} resumed_session_id={_sessionId} questionnaire_code={checkpoint.questionnaire_code} saved_condition_id={savedConditionId} resume_policy={checkpoint.resume_policy}");
            Debug.Log($"{PauseLogPrefix} saved_exit_original_marked_resumed | previous_session_id={checkpoint.session_id} resumed_session_id={_sessionId} questionnaire_code={checkpoint.questionnaire_code} new_status={ExperimentSessionIdHistoryStore.StartedStatus}");

            Debug.Log($"{PauseLogPrefix} saved_exit_resume_condition_reset | previous_session_id={checkpoint.session_id} questionnaire_code={checkpoint.questionnaire_code} saved_condition_id={savedConditionId} next_condition={snapshot?.NextConditionId ?? string.Empty} saved_visible_prueba={checkpointResolution.VisiblePruebaNumber} resume_policy={checkpointResolution.ResumePolicy}");
            Debug.Log($"{PauseLogPrefix} saved_exit_resume_round_reset | previous_session_id={checkpoint.session_id} questionnaire_code={checkpoint.questionnaire_code} next_round={snapshot?.NextRoundIndexWithinCondition ?? 0} saved_round_index={checkpointResolution.SavedRoundIndex}");
            Debug.Log($"{PauseLogPrefix} saved_exit_resume_ready | previous_session_id={checkpoint.session_id} questionnaire_code={checkpoint.questionnaire_code} session_id={_sessionId} participant_id={_participantId} next_condition={snapshot?.NextConditionId ?? string.Empty} next_round={snapshot?.NextRoundIndexWithinCondition ?? 0} visible_prueba_number={snapshot?.NextVisiblePruebaNumber ?? 0}");
            LogEvent("experiment_runtime_saved_exit_resumed_from_checkpoint", BuildRuntimeProtocolPayload("saved_exit_resume_checkpoint"));
            return _sessionActive;
        }

        private bool FailSavedExitResume(string reason)
        {
            LastSavedExitResumeFailureReason = string.IsNullOrWhiteSpace(reason)
                ? "saved_exit_resume_failed"
                : reason.Trim();
            Dictionary<string, object> payload = BuildFailurePayload(LastSavedExitResumeFailureReason);
            payload["failure_reason"] = LastSavedExitResumeFailureReason;
            LogEvent("experiment_runtime_saved_exit_resume_failed", payload);
            return false;
        }

        public void StartCurrentConditionFromRuntime()
        {
            StartNextTrialFromRuntime();
        }

        public void StartNextTrialFromRuntime()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning($"{LogPrefix} Runtime trial advance only runs in Play Mode.", this);
                return;
            }

            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                LogEvent("experiment_runtime_protocol_advance_blocked_while_paused", BuildRuntimeProtocolPayload("experiment_paused"));
                return;
            }

            if (HasCurrentTrialForAdvance() && !CanAdvanceCurrentTrialFromRuntime(out string advanceReason))
            {
                LogEvent("experiment_runtime_protocol_advance_blocked", BuildRuntimeProtocolPayload(advanceReason));
                return;
            }

            LogEvent("experiment_runtime_protocol_advance_requested", BuildRuntimeProtocolPayload("runtime_advance_requested"));
            StartNextTrial();
        }

        public void EndSessionFromRuntime()
        {
            EndSessionFromRuntime(ExperimentSessionIdHistoryStore.CompletedStatus, "runtime_protocol");
        }

        public void EndSessionFromRuntime(string historyStatus, string source)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning($"{LogPrefix} Runtime session end only runs in Play Mode.", this);
                return;
            }

            LogEvent("experiment_runtime_protocol_end_requested", BuildRuntimeProtocolPayload("runtime_end_requested"));
            EndSessionWithStatus(
                string.IsNullOrWhiteSpace(historyStatus) ? ExperimentSessionIdHistoryStore.CompletedStatus : historyStatus,
                source);
        }

        public bool RestartSessionFromRuntime(string source)
        {
            LastRuntimeRestartFailureReason = string.Empty;
            LastRuntimeRestartFailureWasAfterPointOfNoReturn = false;
            if (!Application.isPlaying)
            {
                Debug.LogWarning($"{LogPrefix} Runtime session restart only runs in Play Mode.", this);
                return FailRuntimeRestart("play_mode_required", source);
            }

            TryResolveReferences();
            string previousParticipantId = _participantId;
            if (!_sessionActive || string.IsNullOrWhiteSpace(previousParticipantId))
            {
                return FailRuntimeRestart("active_session_required_for_restart", source);
            }

            if (!TryResolveExistingConditionOrderAssignment(
                    previousParticipantId,
                    ConditionOrderAssignmentMissingForRestartReason,
                    "condition_order_assignment_unreadable_for_restart",
                    out ExperimentConditionOrderAssignment participantAssignment,
                    out string assignmentFailureReason))
            {
                return FailRuntimeRestart(assignmentFailureReason, source);
            }

            string[] currentConditionOrder = _runtimeConditionOrderIds.ToArray();
            if (ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(currentConditionOrder) &&
                !currentConditionOrder.SequenceEqual(participantAssignment.condition_order_ids, StringComparer.Ordinal))
            {
                return FailRuntimeRestart("condition_order_assignment_mismatch_for_restart", source);
            }

            string[] previousConditionOrder = participantAssignment.condition_order_ids.ToArray();
            Debug.Log($"[P46D-PAUSE] protocol_restart_begin | source={source ?? string.Empty} session_active={_sessionActive} trial_active={_trialActive} instrumentation_trial_active={(_instrumentation != null && _instrumentation.TrialActive)} round_active={(_roundManager != null && _roundManager.RoundActive)} session_id={_sessionId ?? string.Empty}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_before_cleanup_snapshot | {BuildPauseRoundSkipStateFragment()}");
            EndSessionWithStatus(ExperimentSessionIdHistoryStore.RestartedStatus, source ?? "runtime_restart");
            LastRuntimeRestartFailureWasAfterPointOfNoReturn = true;
            Debug.Log($"[P46D-PAUSE] previous_session_marked_restarted | source={source ?? string.Empty} previous_session_id={_sessionId ?? string.Empty} history_status={ExperimentSessionIdHistoryStore.RestartedStatus}");

            ForceCleanRuntimeStateForProtocolRestart(source ?? "runtime_restart");
            Debug.Log($"[P46D-PAUSE] protocol_restart_after_cleanup_snapshot | {BuildPauseRoundSkipStateFragment()}");
            _participantId = previousParticipantId;
            _sessionId = string.Empty;
            _sessionResumedFromSavedExit = false;
            _resumedSavedExitOriginalSessionId = string.Empty;
            _resumedSavedExitQuestionnaireCode = string.Empty;
            _runtimeConditionOrderIds.Clear();
            if (!TryConfigureRuntimeConditionOrder(previousConditionOrder))
            {
                return FailRuntimeRestart("invalid_authoritative_condition_order", source);
            }

            StartSession();
            bool introPrepared = PrepareFirstTrialIntroAfterProtocolRestart(source ?? "runtime_restart");
            ExperimentRuntimeProtocolSnapshot snapshot = GetRuntimeProtocolSnapshot();
            Debug.Log($"[P46D-PAUSE] new_session_created_after_restart | restarted={_sessionActive} session_id={_sessionId ?? string.Empty} participant_id={_participantId ?? string.Empty} next_condition={snapshot?.NextConditionId ?? string.Empty}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_condition_reset | session_active={snapshot != null && snapshot.SessionActive} next_condition={snapshot?.NextConditionId ?? string.Empty} current_condition={snapshot?.CurrentConditionId ?? string.Empty}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_round_reset | next_round={snapshot?.NextRoundIndexWithinCondition ?? 0} current_round={snapshot?.CurrentRoundIndexWithinCondition ?? 0} global_round={snapshot?.CurrentGlobalRoundIndex ?? 0}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_trial_reset | next_plan_entry_index={snapshot?.NextPlanEntryIndex ?? 0} current_trial={snapshot?.CurrentTrialId ?? string.Empty} trial_active={snapshot != null && snapshot.TrialActive}");
            LogEvent("experiment_runtime_session_restarted", BuildRuntimeProtocolPayload(source ?? "runtime_restart"));
            if (!_sessionActive || snapshot == null || !introPrepared || string.IsNullOrWhiteSpace(snapshot.NextConditionId))
            {
                Debug.LogError($"[P46D-PAUSE] protocol_restart_failed | session_active={_sessionActive} next_condition={snapshot?.NextConditionId ?? string.Empty} trial_active={snapshot != null && snapshot.TrialActive} round_active={snapshot != null && snapshot.RoundActive} intro_prepared={introPrepared} source={source ?? string.Empty}");
                return FailRuntimeRestart("runtime_restart_session_start_failed", source);
            }
            else
            {
                Debug.Log($"[P46D-PAUSE] protocol_restart_ready_for_first_trial | session_active={_sessionActive} next_condition={snapshot.NextConditionId} session_id={_sessionId ?? string.Empty}");
                Debug.Log($"[P46D-PAUSE] protocol_restart_scene_state_ready | session_active={_sessionActive} next_condition={snapshot.NextConditionId} next_round={snapshot.NextRoundIndexWithinCondition} round_active={snapshot.RoundActive} trial_active={snapshot.TrialActive}");
                Debug.Log($"[P46D-PAUSE] protocol_restart_start_screen_or_protocol_ready | session_active={_sessionActive} can_advance={snapshot.CanAdvance} advance_reason={snapshot.AdvanceReason} has_next_trial={snapshot.HasNextTrial}");
                Debug.Log($"[P46D-PAUSE] protocol_restart_no_trial_autostart | session_id={_sessionId ?? string.Empty} condition_id={snapshot.NextConditionId} visible_prueba={snapshot.NextTrialIndex} trial_index={snapshot.NextTrialIndex} round_index={snapshot.NextRoundIndexWithinCondition} trial_active={snapshot.TrialActive} round_active={snapshot.RoundActive} current_task={(_robotAdapter != null ? _robotAdapter.ActiveP40TaskInstanceId : string.Empty)} pending_task={(_assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox)} held_object_id={(_robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty)}");
                Debug.Log($"[P46D-PAUSE] protocol_restart_robot_idle_before_begin | session_id={_sessionId ?? string.Empty} condition_id={snapshot.NextConditionId} visible_prueba={snapshot.NextTrialIndex} robot_mode={(_robotAdapter != null && _robotAdapter.Blackboard != null ? _robotAdapter.Blackboard.CurrentMode.ToString() : string.Empty)} current_task={(_robotAdapter != null ? _robotAdapter.ActiveP40TaskInstanceId : string.Empty)} pending_task={(_assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox)} held_object_id={(_robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty)}");
                Debug.Log($"[P46D-PAUSE] protocol_restart_autonomy_not_launched | session_id={_sessionId ?? string.Empty} condition_id={snapshot.NextConditionId} visible_prueba={snapshot.NextTrialIndex} has_assigned_box={(_assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox)} current_target={ReadBlackboardTargetId(TaskBlackboardKeys.CurrentTarget)} place_target={ReadBlackboardTargetId(TaskBlackboardKeys.PlaceTarget)}");
                Debug.Log($"[P46D-PAUSE] protocol_restart_no_exceptions | source={source ?? string.Empty} session_id={_sessionId ?? string.Empty}");
            }

            LastRuntimeRestartFailureWasAfterPointOfNoReturn = false;
            return true;
        }

        private bool FailRuntimeRestart(string reason, string source)
        {
            LastRuntimeRestartFailureReason = string.IsNullOrWhiteSpace(reason)
                ? "runtime_restart_failed"
                : reason.Trim();
            Dictionary<string, object> payload = BuildFailurePayload(LastRuntimeRestartFailureReason);
            payload["failure_reason"] = LastRuntimeRestartFailureReason;
            payload["restart_source"] = source ?? string.Empty;
            LogEvent("experiment_runtime_session_restart_failed", payload);
            Debug.LogError(
                $"{PauseLogPrefix} protocol_restart_failed | reason={LastRuntimeRestartFailureReason} source={source ?? string.Empty}",
                this);
            return false;
        }

        private void ForceCleanRuntimeStateForProtocolRestart(string source)
        {
            string reason = string.IsNullOrWhiteSpace(source) ? "protocol_restart" : source;
            int boxesBefore = CountActiveRoundBoxes();
            int palletsBefore = CountActiveRoundPallets();
            int childrenBefore = _spawnManager != null ? _spawnManager.ActiveRoundChildCount : 0;
            Debug.Log($"[P46D-PAUSE] protocol_restart_world_reset_begin | source={reason} session_id={_sessionId ?? string.Empty} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} active_round_path={GetTransformPath(_spawnManager != null ? _spawnManager.activeRoundContainer : null)} active_round_children={childrenBefore} box_count={boxesBefore} pallet_count={palletsBefore} held_object_id={(_robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty)}");
            if (_trialPreparationCoroutine != null)
            {
                StopCoroutine(_trialPreparationCoroutine);
                _trialPreparationCoroutine = null;
            }

            if (_trialEndCleanupCoroutine != null)
            {
                StopCoroutine(_trialEndCleanupCoroutine);
                _trialEndCleanupCoroutine = null;
            }

            ReleaseTrialXrGatePause();

            if (_debugSequenceCoroutine != null)
            {
                StopCoroutine(_debugSequenceCoroutine);
                _debugSequenceCoroutine = null;
            }

            _trialTransitionBusy = false;
            _trialActive = false;
            DeactivateVoicePipelineAtTrialBoundary(reason);
            _currentTrialCompleted = false;
            _completedTrialId = string.Empty;
            _voiceConnector?.ResetForNewExperimentTrial(reason);
            _assistanceCoordinator?.ResetForNewExperimentTrial(reason);
            _commandBridge?.ResetForNewExperimentTrial(reason);
            string heldBeforeCleanup = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty;
            if (!string.IsNullOrWhiteSpace(heldBeforeCleanup))
            {
                bool heldCleared = _robotAdapter.ClearHeldObjectForExperimentBoundary(reason);
                Debug.Log($"[P46D-PAUSE] protocol_restart_held_object_cleared | held_object_id_before={heldBeforeCleanup} held_object_id_after={(_robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty)} cleared={heldCleared} session_id={_sessionId ?? string.Empty} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode}");
            }

            _robotAdapter?.ClearExperimentRuntimeState(reason);
            Debug.Log($"[P46D-PAUSE] protocol_restart_current_task_cancelled | active_task={(_robotAdapter != null ? _robotAdapter.ActiveP40TaskInstanceId : string.Empty)} active_request_id={(_robotAdapter != null ? _robotAdapter.ActiveP40RequestId : string.Empty)} held_object_id={(_robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty)}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_pending_task_cleared | has_assigned_box={(_assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox)} explicit_intent_processing={(_assistanceCoordinator != null && _assistanceCoordinator.ExplicitIntentProcessing)} active_request_id={(_robotAdapter != null ? _robotAdapter.ActiveP40RequestId : string.Empty)}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_blackboard_cleared | has_robot={_robotAdapter != null} robot_mode={(_robotAdapter != null && _robotAdapter.Blackboard != null ? _robotAdapter.Blackboard.CurrentMode.ToString() : string.Empty)} has_current_target={HasBlackboardTarget(TaskBlackboardKeys.CurrentTarget)} has_place_target={HasBlackboardTarget(TaskBlackboardKeys.PlaceTarget)} task_status={ReadRobotTaskStatus()}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_navigation_reset | has_robot={_robotAdapter != null} active_request_id={(_robotAdapter != null ? _robotAdapter.ActiveP40RequestId : string.Empty)} held_object_id={(_robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty)}");
            _spawnManager?.ClearCurrentRound();
            _roundManager?.NotifyRoundReset();
            Debug.Log($"[P46D-PAUSE] protocol_restart_active_round_destroyed | source={reason} active_round_path={GetTransformPath(_spawnManager != null ? _spawnManager.activeRoundContainer : null)} children_before={childrenBefore} children_after_request={(_spawnManager != null ? _spawnManager.ActiveRoundChildCount : 0)}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_boxes_destroyed | source={reason} boxes_before={boxesBefore} boxes_after_request={CountActiveRoundBoxes()}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_pallet_destroyed | source={reason} pallets_before={palletsBefore} pallets_after_request={CountActiveRoundPallets()}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_ui_reset | source={reason} trial_transition_busy={_trialTransitionBusy}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_round_reset | source={reason} round_active={(_roundManager != null && _roundManager.RoundActive)} active_round_child_count={(_spawnManager != null && _spawnManager.activeRoundContainer != null ? _spawnManager.activeRoundContainer.childCount : 0)}");
            Debug.Log($"[P46D-PAUSE] protocol_restart_trial_reset | source={reason} trial_active={_trialActive} instrumentation_trial_active={(_instrumentation != null && _instrumentation.TrialActive)}");
        }

        private bool PrepareFirstTrialIntroAfterProtocolRestart(string source)
        {
            if (!_sessionActive)
            {
                Debug.LogError($"[P46D-PAUSE] protocol_restart_failed | reason=session_not_active_before_intro source={source ?? string.Empty}");
                return false;
            }

            ExperimentTrialPlanEntry nextEntry = PeekNextPlanEntry();
            if (nextEntry == null || nextEntry.Condition == null)
            {
                Debug.LogError($"[P46D-PAUSE] protocol_restart_failed | reason=first_plan_entry_missing_for_intro source={source ?? string.Empty} next_plan_entry_index={_nextPlanEntryIndex} total_planned_trials={GetPlannedTrialCount()}");
                return false;
            }

            _protocolRestartFirstTrialPending = true;
            Debug.Log($"[P46D-PAUSE] protocol_restart_intro_screen_requested | source={source ?? string.Empty} session_id={_sessionId ?? string.Empty} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={nextEntry.Condition.ConditionId} visible_prueba={nextEntry.ConditionOrderIndex + 1} condition_order_index={nextEntry.ConditionOrderIndex} trial_index={nextEntry.TrialIndex} round_index={nextEntry.RoundIndexWithinCondition} next_plan_entry_index={_nextPlanEntryIndex}");
            return !_trialActive &&
                (_instrumentation == null || !_instrumentation.TrialActive) &&
                !_trialTransitionBusy &&
                _trialPreparationCoroutine == null &&
                _nextPlanEntryIndex >= 0 &&
                _nextPlanEntryIndex < GetPlannedTrialCount();
        }

        public bool CloseCurrentTrialForTechnicalIncidentFromRuntime(string source)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning($"{LogPrefix} Runtime technical incident close only runs in Play Mode.", this);
                return false;
            }

            if (!_sessionActive)
            {
                LogEvent("experiment_runtime_technical_incident_close_rejected", BuildFailurePayload("session_not_active"));
                return false;
            }

            if (TrialTransitionBusy)
            {
                LogEvent("experiment_runtime_technical_incident_close_rejected", BuildFailurePayload("trial_transition_busy"));
                return false;
            }

            TryResolveReferences();
            string reason = "technical_incident_runtime_operator";
            bool afterPause = ExperimentPauseMenuController.WasContinueRecentlyAcknowledged();
            if (afterPause)
            {
                Debug.Log($"{PauseLogPrefix} protocol_round_skip_after_pause_detected | source={source ?? string.Empty} frames_since_continue={Time.frameCount - ExperimentPauseMenuController.LastContinueFrame} seconds_since_continue={(Time.realtimeSinceStartup - ExperimentPauseMenuController.LastContinueRealtime):0.###} session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode}");
            }

            Debug.Log($"{PauseLogPrefix} protocol_round_skip_requested | source={source ?? string.Empty} session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={CurrentConditionId} trial_index={_currentTrialIndex} round_index={_currentRoundIndexWithinCondition} active_round_path={GetTransformPath(_spawnManager != null ? _spawnManager.activeRoundContainer : null)}");
            Debug.Log($"{PauseLogPrefix} protocol_round_skip_state_before_cleanup | {BuildPauseRoundSkipStateFragment()}");
            Dictionary<string, object> payload = BuildRuntimeProtocolPayload(reason);
            CollectActiveRoundBoxCounts(out int totalBoxes, out int completedBoxes);
            payload["source"] = source ?? string.Empty;
            payload["technical_incident"] = true;
            payload["valid_for_analysis"] = false;
            payload["aborted"] = true;
            payload["total_boxes"] = totalBoxes;
            payload["completed_boxes"] = completedBoxes;
            payload["round_active"] = _roundManager != null && _roundManager.RoundActive;
            payload["round_finished"] = _roundManager != null && _roundManager.RoundFinished;
            payload["trial_active"] = _trialActive;
            payload["instrumentation_trial_active"] = _instrumentation != null && _instrumentation.TrialActive;
            LogEvent("experiment_runtime_technical_incident_close_confirmed", payload);
            Debug.Log($"{PauseLogPrefix} protocol_round_skip_current_trial_closing | source={source ?? string.Empty} session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={CurrentConditionId} trial_index={_currentTrialIndex} round_index={_currentRoundIndexWithinCondition} total_boxes={totalBoxes} completed_boxes={completedBoxes} active_round_path={GetTransformPath(_spawnManager != null ? _spawnManager.activeRoundContainer : null)}");

            if (_instrumentation != null && _instrumentation.TrialActive)
            {
                _instrumentation.AbortTrial(reason);
            }

            Debug.Log($"{PauseLogPrefix} protocol_round_skip_autonomy_cleanup_begin | session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={CurrentConditionId} trial_index={_currentTrialIndex} round_index={_currentRoundIndexWithinCondition} robot_pose={FormatTransformPose(_robotAdapter != null ? _robotAdapter.NavigationReference : null)}");
            string heldBeforeCleanup = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty;
            if (!string.IsNullOrWhiteSpace(heldBeforeCleanup))
            {
                bool heldCleared = _robotAdapter.ClearHeldObjectForExperimentBoundary(reason);
                Debug.Log($"{PauseLogPrefix} protocol_round_skip_held_object_cleared | held_object_id_before={heldBeforeCleanup} held_object_id_after={(_robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty)} cleared={heldCleared} session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={CurrentConditionId} trial_index={_currentTrialIndex} round_index={_currentRoundIndexWithinCondition}");
            }

            _robotAdapter?.ClearExperimentRuntimeState(reason);
            Debug.Log($"{PauseLogPrefix} protocol_round_skip_current_task_cancelled | active_task={(_robotAdapter != null ? _robotAdapter.ActiveP40TaskInstanceId : string.Empty)} active_request_id={(_robotAdapter != null ? _robotAdapter.ActiveP40RequestId : string.Empty)} held_object_id={(_robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty)}");
            _assistanceCoordinator?.ResetForNewExperimentTrial(reason);
            _commandBridge?.ResetForNewExperimentTrial(reason);
            Debug.Log($"{PauseLogPrefix} protocol_round_skip_pending_task_cleared | has_assigned_box={(_assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox)} explicit_intent_processing={(_assistanceCoordinator != null && _assistanceCoordinator.ExplicitIntentProcessing)} active_request_id={(_robotAdapter != null ? _robotAdapter.ActiveP40RequestId : string.Empty)}");
            Debug.Log($"{PauseLogPrefix} protocol_round_skip_blackboard_cleared | has_robot={_robotAdapter != null} robot_mode={(_robotAdapter != null && _robotAdapter.Blackboard != null ? _robotAdapter.Blackboard.CurrentMode.ToString() : string.Empty)} has_current_target={HasBlackboardTarget(TaskBlackboardKeys.CurrentTarget)} has_place_target={HasBlackboardTarget(TaskBlackboardKeys.PlaceTarget)} task_status={ReadRobotTaskStatus()}");
            Debug.Log($"{PauseLogPrefix} protocol_round_skip_navigation_reset | has_robot={_robotAdapter != null} active_request_id={(_robotAdapter != null ? _robotAdapter.ActiveP40RequestId : string.Empty)} held_object_id={(_robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty)}");
            Debug.Log($"{PauseLogPrefix} protocol_round_skip_autonomy_cleanup_done | session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={CurrentConditionId} trial_index={_currentTrialIndex} round_index={_currentRoundIndexWithinCondition}");
            Debug.Log($"{PauseLogPrefix} protocol_round_skip_state_after_cleanup | {BuildPauseRoundSkipStateFragment()}");
            _lastTrialClosedByTechnicalIncident = true;
            MarkCurrentTrialCompletedByOrchestrator(reason, _roundManager != null ? _roundManager.CurrentRound : null);
            return true;
        }

        public bool CanAdvanceCurrentTrialFromRuntime(out string reason)
        {
            if (!_sessionActive)
            {
                reason = "session_not_active";
                return false;
            }

            if (TrialTransitionBusy)
            {
                reason = "trial_transition_busy";
                return false;
            }

            if (TryGetHeldOrSelectedActiveRoundBox(out BoxMetadata heldBox))
            {
                reason = "round_box_still_grabbed_or_selected:" + heldBox.gameObject.name;
                return false;
            }

            if (HasCurrentTrialForAdvance() && !_lastTrialClosedByTechnicalIncident && !IsCurrentRoundCompleted())
            {
                reason = "round_not_completed";
                return false;
            }

            if (EvaluateCanAdvanceTrialWithoutMutation(out reason, out _))
            {
                return true;
            }

            return IsCompletedTrialAwaitingRuntimeAdvance(out reason);
        }

        public ExperimentRuntimeProtocolSnapshot GetRuntimeProtocolSnapshot()
        {
            TryResolveReferences();
            ExperimentTrialPlanEntry nextEntry = PeekNextPlanEntry();
            ExperimentTrialPlanEntry currentEntry = _sessionTrialPlan != null &&
                _currentPlanEntryIndex >= 0 &&
                _currentPlanEntryIndex < _sessionTrialPlan.Count
                    ? _sessionTrialPlan[_currentPlanEntryIndex]
                    : null;
            bool canAdvance = CanAdvanceCurrentTrialFromRuntime(out string advanceReason);
            CollectActiveRoundBoxCounts(out int totalBoxes, out int completedBoxes);
            return new ExperimentRuntimeProtocolSnapshot(
                _sessionActive,
                TrialActive,
                TrialTransitionBusy,
                _participantId,
                _sessionId,
                CurrentConditionId,
                CurrentConditionName,
                _currentTrialId,
                _currentRoundId,
                _currentRoundIndexWithinCondition,
                RoundsPerCondition,
                _currentGlobalRoundIndex,
                TotalPlannedTrials,
                _currentPlanEntryIndex,
                _nextPlanEntryIndex,
                _sessionTrialPlan != null ? _sessionTrialPlan.Count : 0,
                nextEntry != null && nextEntry.Condition != null ? nextEntry.Condition.ConditionId : string.Empty,
                nextEntry != null && nextEntry.Condition != null ? nextEntry.Condition.ConditionName : string.Empty,
                nextEntry != null ? nextEntry.RoundIndexWithinCondition : 0,
                nextEntry != null,
                canAdvance,
                advanceReason,
                _roundManager != null && _roundManager.RoundActive,
                IsCurrentRoundCompleted(),
                totalBoxes,
                completedBoxes,
                BuildRuntimeConditionOrderSummary(),
                _runtimeConditionOrderIds,
                currentEntry?.ConditionOrderIndex ?? -1,
                nextEntry?.ConditionOrderIndex ?? -1,
                _internalTrialAttemptIndex);
        }

        [ContextMenu("Experiment/Start Next Trial")]
        public void StartNextTrial()
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                LogEvent("experiment_start_next_trial_blocked_while_paused", BuildRuntimeProtocolPayload("experiment_paused"));
                return;
            }

            int invocationId = ++_startNextTrialInvocationCounter;
            bool wasRunning = _isStartNextTrialRunning;
            _startNextTrialDepth++;
            _isStartNextTrialRunning = true;
            _activeStartNextTrialInvocationId = invocationId;
            _completionDuringStartNextTrialDetected = false;
            if (wasRunning)
            {
                LogStartNextTrialDiagnostic("experiment_start_next_trial_reentrant_call_detected", "reentrant_call", "entered", invocationId);
            }

            LogStartNextTrialDiagnostic("experiment_start_next_trial_entered", "entered", "entered", invocationId);
            try
            {
                StartNextTrialCore(invocationId);
            }
            catch (Exception ex)
            {
                LogStartNextTrialException(invocationId, ex);
                Debug.LogException(ex, this);
                throw;
            }
            finally
            {
                LogStartNextTrialDiagnostic("experiment_start_next_trial_finally", "finally", "finally", invocationId);
                _startNextTrialDepth = Mathf.Max(0, _startNextTrialDepth - 1);
                if (_startNextTrialDepth == 0)
                {
                    _isStartNextTrialRunning = false;
                    _activeStartNextTrialInvocationId = 0;
                }
            }
        }

        private void StartNextTrialCore(int invocationId)
        {
            if (!EnsureCanRunCommand("Start Next Trial"))
            {
                LogStartNextTrialReturning(invocationId, "ensure_can_run_command_failed");
                return;
            }

            LogStartNextTrialDiagnostic("experiment_start_next_trial_requested", "requested", "requested", invocationId);
            LogStateContradictionsIfAny("requested", invocationId, "requested");

            if (!_sessionActive)
            {
                StartSession();
            }

            if (_trialTransitionBusy || _trialPreparationCoroutine != null || _trialEndCleanupCoroutine != null)
            {
                LogStartNextTrialDiagnostic("experiment_start_next_trial_blocked", "trial_cleanup_or_prepare_already_pending", "blocked", invocationId);
                LogStartNextTrialReturning(invocationId, "trial_cleanup_or_prepare_already_pending");
                return;
            }

            if (HasCurrentTrialForAdvance() && TryGetHeldOrSelectedActiveRoundBox(out BoxMetadata heldBox))
            {
                string heldReason = "round_box_still_grabbed_or_selected:" + heldBox.gameObject.name;
                LogStartNextTrialDiagnostic("experiment_start_next_trial_blocked", heldReason, "blocked", invocationId);
                LogStartNextTrialReturning(invocationId, heldReason);
                return;
            }

            if (HasCurrentTrialForAdvance() && !_lastTrialClosedByTechnicalIncident && !IsCurrentRoundCompleted())
            {
                LogStartNextTrialDiagnostic("experiment_start_next_trial_blocked", "round_not_completed", "blocked", invocationId);
                LogStartNextTrialReturning(invocationId, "round_not_completed");
                return;
            }

            LogStartNextTrialDiagnostic("experiment_start_next_trial_before_block_decision", "before_block_decision", "before_block_decision", invocationId);
            if (_trialActive || (_instrumentation != null && _instrumentation.TrialActive))
            {
                LogStartNextTrialDiagnostic("experiment_start_next_trial_before_reconcile", "before_reconcile", "before_reconcile", invocationId);
                bool reconciled = TryFinalizeCompletedActiveTrialForAdvance("start_next_trial_before_active_block", out string reconcileReason);
                LogStartNextTrialDiagnostic("experiment_start_next_trial_after_reconcile", reconciled ? reconcileReason : "reconcile_failed:" + reconcileReason, "after_reconcile", invocationId);
                if (reconciled)
                {
                    LogStartNextTrialDiagnostic("experiment_start_next_trial_reconciled_completed_trial", "completed_trial_reconciled_before_advance:" + reconcileReason, "reconciled", invocationId);
                }
                else
                {
                    LogStateContradictionsIfAny("blocked_active_trial", invocationId, "trial_already_active_before_start_next_trial");
                    LogStartNextTrialDiagnostic("experiment_start_next_trial_blocked", "trial_already_active_before_start_next_trial:" + reconcileReason, "blocked", invocationId);
                    LogStartNextTrialReturning(invocationId, "trial_already_active_before_start_next_trial:" + reconcileReason);
                    return;
                }
            }

            ExperimentTrialPlanEntry pendingPlanEntry = PeekNextPlanEntry();
            if (pendingPlanEntry?.Condition != null &&
                string.Equals(pendingPlanEntry.Condition.ConditionId, ExperimentCompensatedConditionOrder.C11, StringComparison.Ordinal))
            {
                ExperimentVoicePipelineBootstrap voiceBootstrap = ExperimentVoicePipelineBootstrap.EnsureExists();
                voiceBootstrap.EnsurePreparationStarted();
                if (!voiceBootstrap.IsPrepared)
                {
                    LogStartNextTrialDiagnostic(
                        "c11_trial_start_blocked_voice_pipeline_not_ready",
                        voiceBootstrap.ReadinessToken,
                        "blocked",
                        invocationId);
                    LogStartNextTrialReturning(invocationId, "voice_pipeline_not_ready:" + voiceBootstrap.ReadinessToken);
                    return;
                }
            }

            ExperimentTrialPlanEntry planEntry = AdvanceToNextPlanEntry();
            if (planEntry == null || planEntry.Condition == null)
            {
                if (_lastTrialClosedByTechnicalIncident)
                {
                    Debug.Log($"{PauseLogPrefix} protocol_round_skip_no_next_round_protocol_completed | session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} current_condition_id={CurrentConditionId} current_trial_id={_currentTrialId} current_round_index={_currentRoundIndexWithinCondition} next_plan_entry_index={_nextPlanEntryIndex} total_planned_trials={GetPlannedTrialCount()}");
                    _lastTrialClosedByTechnicalIncident = false;
                }

                LogStartNextTrialDiagnostic("experiment_start_next_trial_blocked", "no_planned_trial_available", "blocked", invocationId);
                LogStartNextTrialReturning(invocationId, "no_planned_trial_available");
                return;
            }

            LogStartNextTrialDiagnostic("experiment_start_next_trial_advanced", planEntry.Condition.ConditionId, "advanced", invocationId);
            _trialPreparationCoroutine = StartCoroutine(BeginTrialCoroutine(planEntry, resetFirst: true));
            LogStartNextTrialDiagnostic("experiment_start_next_trial_prepare_coroutine_started", planEntry.Condition.ConditionId, "prepare_coroutine_started", invocationId);
            LogStartNextTrialReturning(invocationId, "prepare_coroutine_started:" + planEntry.Condition.ConditionId);
        }

        [ContextMenu("Experiment/Restart Current Trial")]
        public void RestartCurrentTrial()
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                LogEvent("experiment_restart_current_trial_blocked_while_paused", BuildRuntimeProtocolPayload("experiment_paused"));
                return;
            }

            if (!EnsureCanRunCommand("Restart Current Trial"))
            {
                return;
            }

            if (_currentCondition == null || _currentTrialIndex <= 0)
            {
                LogEvent("experiment_trial_reset_failed", BuildFailurePayload("no_current_trial"));
                return;
            }

            if (_trialActive)
            {
                _instrumentation?.AbortTrial("restart_current_trial");
                LogEvent("experiment_trial_completed_by_orchestrator", BuildTrialPayload("restart_aborted_previous"));
            }

            if (_trialPreparationCoroutine != null || _trialEndCleanupCoroutine != null)
            {
                LogEvent("experiment_trial_reset_failed", BuildFailurePayload("trial_cleanup_or_prepare_already_pending"));
                return;
            }

            ExperimentTrialPlanEntry currentPlan = ResolveCurrentPlanEntry();
            if (currentPlan == null)
            {
                currentPlan = new ExperimentTrialPlanEntry(
                    _currentCondition,
                    _selectedConditionIndex,
                    _currentTrialIndex,
                    _currentRoundIndex,
                    _currentRoundIndexWithinCondition,
                    RoundsPerCondition,
                    _currentGlobalRoundIndex);
            }

            _trialPreparationCoroutine = StartCoroutine(BeginTrialCoroutine(currentPlan, resetFirst: true));
        }

        [ContextMenu("Experiment/End Current Trial")]
        public void EndCurrentTrial()
        {
            if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
            {
                LogEvent("experiment_end_current_trial_blocked_while_paused", BuildRuntimeProtocolPayload("experiment_paused"));
                return;
            }

            if (!_trialActive)
            {
                LogDuplicateCompletionIfNeeded("end_current_trial_after_completion");
                return;
            }

            if (ShouldMarkManualEndIncomplete(out string incompleteReason))
            {
                LogEvent("experiment_manual_end_evaluated", BuildManualEndPayload(false, incompleteReason));
                _instrumentation?.EndTrialManuallyIncomplete(incompleteReason, "manually_ended_incomplete");
            }
            else
            {
                LogEvent("experiment_manual_end_evaluated", BuildManualEndPayload(true, "manual_end_complete"));
                _instrumentation?.EndTrialManually("manually_ended");
            }

            MarkCurrentTrialCompletedByOrchestrator("ended_by_orchestrator", null);
            if (_trialEndCleanupCoroutine == null)
            {
                _trialEndCleanupCoroutine = StartCoroutine(CleanupAfterTrialClosed("ended_by_orchestrator"));
            }
        }

        private void EndCurrentTrialForSavedExit(string source)
        {
            if (!_trialActive && (_instrumentation == null || !_instrumentation.TrialActive))
            {
                return;
            }

            const string closeReason = ExperimentSessionIdHistoryStore.SavedExitIncompleteConditionRestartReason;
            Dictionary<string, object> payload = BuildTrialPayload(closeReason);
            payload["partial_trial_close_reason"] = closeReason;
            payload["session_end_source"] = source ?? string.Empty;
            payload["internal_trial_attempt_index"] = _internalTrialAttemptIndex;
            payload["visible_prueba_number"] = _currentContext != null ? _currentContext.ConditionOrderIndex + 1 : 0;
            payload["counts_as_condition_completed"] = false;
            payload["valid_for_completed_results"] = false;
            LogEvent("experiment_saved_exit_partial_trial_closed", payload);
            _instrumentation?.EndTrialManuallyIncomplete(closeReason, closeReason);
            _trialActive = false;
            DeactivateVoicePipelineAtTrialBoundary(closeReason);
            _currentTrialCompleted = false;
            _completedTrialId = string.Empty;
            if (_trialEndCleanupCoroutine == null)
            {
                _trialEndCleanupCoroutine = StartCoroutine(CleanupAfterTrialClosed(closeReason));
            }
        }

        [ContextMenu("Experiment/End Session")]
        public void EndSession()
        {
            EndSessionWithStatus(ExperimentSessionIdHistoryStore.CompletedStatus, "orchestrator_end_session");
        }

        private void EndSessionWithStatus(string historyStatus, string source)
        {
            if (!_sessionActive && ExperimentDataPathResolver.IsSessionClosed)
            {
                Debug.LogError(
                    $"{LogPrefix} session_end_rejected_already_closed | session_id={ExperimentDataPathResolver.CurrentSessionId} requested_status={historyStatus ?? string.Empty} source={source ?? string.Empty}");
                return;
            }

            string endingSessionId = _sessionId ?? string.Empty;
            string endingQuestionnaireCode = ExperimentDataPathResolver.CurrentQuestionnaireCode;
            string resolvedHistoryStatus = string.IsNullOrWhiteSpace(historyStatus)
                ? ExperimentSessionIdHistoryStore.CompletedStatus
                : historyStatus;
            if (_sessionResumedFromSavedExit &&
                string.Equals(resolvedHistoryStatus, ExperimentSessionIdHistoryStore.ClosedWithoutCompletionStatus, StringComparison.Ordinal) &&
                !string.Equals(source, "exit_to_start_without_save", StringComparison.Ordinal))
            {
                resolvedHistoryStatus = ExperimentSessionIdHistoryStore.SavedExitStatus;
                Debug.Log($"{PauseLogPrefix} saved_exit_resume_exit_without_save_preserves_checkpoint | previous_session_id={_resumedSavedExitOriginalSessionId} session_id={endingSessionId} questionnaire_code={_resumedSavedExitQuestionnaireCode} requested_status={historyStatus} persisted_status={resolvedHistoryStatus}");
            }

            if (_trialActive || (_instrumentation != null && _instrumentation.TrialActive))
            {
                bool finalized = TryFinalizeCompletedActiveTrialForAdvance("end_session_finalize_completed_trial", out string reconcileReason);
                if (!finalized)
                {
                    LogEvent("experiment_session_end_trial_finalize_failed", BuildFailurePayload(reconcileReason));
                    if (string.Equals(resolvedHistoryStatus, ExperimentSessionIdHistoryStore.SavedExitStatus, StringComparison.Ordinal))
                    {
                        EndCurrentTrialForSavedExit(source);
                    }
                    else
                    {
                        EndCurrentTrial();
                    }
                }
            }

            if (_debugSequenceCoroutine != null)
            {
                StopCoroutine(_debugSequenceCoroutine);
                _debugSequenceCoroutine = null;
                LogEvent("experiment_2x2_sanity_sequence_failed", BuildFailurePayload("session_ended"));
            }

            _sessionActive = false;
            Dictionary<string, object> payload = BuildBasePayload();
            payload["session_history_status"] = resolvedHistoryStatus;
            payload["session_end_source"] = source ?? string.Empty;
            ClearRuntimePlan();
            LogEvent("experiment_session_completed", payload);
            ExperimentDataPathResolver.EndCurrentSession(payload["session_history_status"].ToString());

            if (_sessionResumedFromSavedExit &&
                string.Equals(resolvedHistoryStatus, ExperimentSessionIdHistoryStore.CompletedStatus, StringComparison.Ordinal))
            {
                string originalSessionId = string.IsNullOrWhiteSpace(_resumedSavedExitOriginalSessionId)
                    ? endingSessionId
                    : _resumedSavedExitOriginalSessionId;
                ExperimentSessionIdHistoryStore.UpdateSessionStatus(originalSessionId, ExperimentSessionIdHistoryStore.CompletedStatus);
                Debug.Log($"{PauseLogPrefix} saved_exit_original_marked_completed_after_resume | previous_session_id={originalSessionId} completed_session_id={endingSessionId} questionnaire_code={_resumedSavedExitQuestionnaireCode} new_status={ExperimentSessionIdHistoryStore.CompletedStatus}");
                Debug.Log($"{PauseLogPrefix} saved_exit_pending_cleared_after_completion | previous_session_id={originalSessionId} completed_session_id={endingSessionId} questionnaire_code={_resumedSavedExitQuestionnaireCode}");
                bool stillPending = IsHistorySessionPending(originalSessionId);
                Debug.Log($"{PauseLogPrefix} saved_exit_no_pending_session_after_completion | previous_session_id={originalSessionId} completed_session_id={endingSessionId} questionnaire_code={_resumedSavedExitQuestionnaireCode} pending={stillPending}");
                _sessionResumedFromSavedExit = false;
                _resumedSavedExitOriginalSessionId = string.Empty;
                _resumedSavedExitQuestionnaireCode = string.Empty;
            }
        }

        [ContextMenu("Experiment/Debug Run 2x2 Sanity Sequence")]
        public void DebugRun2x2SanitySequence()
        {
            if (!EnsureCanRunCommand("Debug Run 2x2 Sanity Sequence"))
            {
                return;
            }

            if (_debugSequenceCoroutine != null)
            {
                StopCoroutine(_debugSequenceCoroutine);
            }

            if (!_sessionActive)
            {
                StartSession();
            }

            _debugSequenceCoroutine = StartCoroutine(Run2x2SanitySequence());
        }

        [ContextMenu("Experiment/Log Zero-State Snapshot")]
        public void LogZeroStateSnapshot()
        {
            TryResolveReferences();

            var failures = new List<string>();
            if (_sessionActive)
            {
                failures.Add("session_active_without_manual_zero_expectation");
            }

            if (_trialActive || (_instrumentation != null && _instrumentation.TrialActive))
            {
                failures.Add("trial_active_without_manual_zero_expectation");
            }

            if (_roundManager != null && _roundManager.RoundActive)
            {
                failures.Add("round_active_without_manual_zero_expectation");
            }

            if (_spawnManager != null && _spawnManager.activeRoundContainer != null && _spawnManager.activeRoundContainer.childCount > 0)
            {
                failures.Add("active_round_container_not_empty");
            }

            if (_assistanceCoordinator != null && (_assistanceCoordinator.RoundInitialized || _assistanceCoordinator.HasAssignedBox || _assistanceCoordinator.ExplicitIntentProcessing))
            {
                failures.Add("assistance_state_not_idle");
            }

            if (_conditionConfig != null)
            {
                ExperimentConditionConfig condition = _conditionConfig.CurrentCondition;
                if (condition == null ||
                    condition.RobotEnabled ||
                    condition.VoiceEnabled ||
                    condition.AssistanceMode != RobotAssistanceMode.Disabled ||
                    !string.Equals(condition.ConditionName, "uninitialized", StringComparison.Ordinal))
                {
                    failures.Add("condition_gate_not_neutral_uninitialized");
                }
            }

            if (_robotAdapter != null)
            {
                if (!string.IsNullOrWhiteSpace(_robotAdapter.HeldObjectId))
                {
                    failures.Add("robot_holding_object");
                }

                if (_robotAdapter.Blackboard != null)
                {
                    if (_robotAdapter.Blackboard.CurrentMode != RobotMode.Idle)
                    {
                        failures.Add("robot_mode_not_idle");
                    }

                    if (_robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out TaskStatus status) &&
                        status == TaskStatus.InProgress)
                    {
                        failures.Add("robot_task_in_progress");
                    }
                }
            }

            Dictionary<string, object> payload = BuildBasePayload();
            payload["zero_state_passed"] = failures.Count == 0;
            payload["failures"] = string.Join("|", failures);
            payload["session_active"] = _sessionActive;
            payload["trial_active"] = _trialActive || (_instrumentation != null && _instrumentation.TrialActive);
            payload["round_active"] = _roundManager != null && _roundManager.RoundActive;
            payload["active_round_child_count"] = _spawnManager != null && _spawnManager.activeRoundContainer != null ? _spawnManager.activeRoundContainer.childCount : 0;
            payload["assistance_round_initialized"] = _assistanceCoordinator != null && _assistanceCoordinator.RoundInitialized;
            payload["assistance_has_assigned_box"] = _assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox;
            payload["assistance_explicit_intent_processing"] = _assistanceCoordinator != null && _assistanceCoordinator.ExplicitIntentProcessing;
            payload["voice_pending_confirmation"] = _voiceConnector != null && _voiceConnector.HasPendingConfirmation;
            payload["condition_gate_name"] = _conditionConfig != null && _conditionConfig.CurrentCondition != null ? _conditionConfig.CurrentCondition.ConditionName : string.Empty;
            payload["condition_gate_robot_enabled"] = _conditionConfig != null && _conditionConfig.CurrentCondition != null && _conditionConfig.CurrentCondition.RobotEnabled;
            payload["condition_gate_voice_enabled"] = _conditionConfig != null && _conditionConfig.CurrentCondition != null && _conditionConfig.CurrentCondition.VoiceEnabled;
            payload["condition_gate_assistance_mode"] = _conditionConfig != null && _conditionConfig.CurrentCondition != null ? _conditionConfig.CurrentCondition.AssistanceMode.ToString() : string.Empty;
            payload["robot_held_object_id"] = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty;
            payload["robot_blackboard_mode"] = _robotAdapter != null && _robotAdapter.Blackboard != null ? _robotAdapter.Blackboard.CurrentMode.ToString() : string.Empty;
            LogEvent(failures.Count == 0 ? "experiment_zero_state_sanity_passed" : "experiment_zero_state_sanity_failed", payload);
        }

        [ContextMenu("Experiment Debug/Dump Orchestrator State")]
        public void DumpOrchestratorState()
        {
            TryResolveReferences();
            Dictionary<string, object> payload = BuildStartNextTrialDiagnosticPayload("manual_debug_dump", "debug_dump", 0);
            payload["active_condition_plan"] = BuildActiveConditionPlanSummary();
            payload["debug_probe_can_advance"] = EvaluateCanAdvanceTrialWithoutMutation(out string probeReason, out _);
            payload["debug_probe_reason"] = probeReason;
            LogEvent("experiment_debug_orchestrator_state_dumped", payload);
            LogStateContradictionsIfAny("debug_dump", 0, "manual_debug_dump");
            Debug.Log(BuildReadableOrchestratorState(payload), this);
        }

        [ContextMenu("Experiment Debug/Probe Can Advance Trial")]
        public void ProbeCanAdvanceTrial()
        {
            TryResolveReferences();
            LogEvent("experiment_debug_probe_can_advance_started", BuildStartNextTrialDiagnosticPayload("probe_started", "debug_probe_started", 0));
            bool canAdvance = EvaluateCanAdvanceTrialWithoutMutation(out string reason, out ExperimentTrialAdvanceSnapshot snapshot);
            Dictionary<string, object> payload = BuildStartNextTrialDiagnosticPayload(reason, "debug_probe_result", 0);
            payload["can_advance_trial"] = canAdvance;
            payload["advance_probe_reason"] = reason;
            payload["advance_probe_orchestrator_trial_active"] = snapshot.OrchestratorTrialActive;
            payload["advance_probe_instrumentation_trial_active"] = snapshot.InstrumentationTrialActive;
            payload["advance_probe_instrumentation_trial_completed"] = snapshot.InstrumentationTrialCompleted;
            payload["advance_probe_round_completed"] = snapshot.RoundCompleted;
            payload["advance_probe_robot_runtime_busy"] = snapshot.RobotRuntimeBusy;
            LogEvent("experiment_debug_probe_can_advance_result", payload);
            LogStateContradictionsIfAny("debug_probe", 0, reason);
            Debug.Log(BuildReadableOrchestratorState(payload), this);
        }

        private IEnumerator BeginTrialCoroutine(ExperimentTrialPlanEntry planEntry, bool resetFirst)
        {
            bool restartBoundary = _protocolRestartFirstTrialPending;
            if (restartBoundary)
            {
                _protocolRestartFirstTrialPending = false;
            }

            _trialTransitionBusy = true;
            try
            {
            ExperimentRuntimeContext context = BuildContext(planEntry);
            _currentContext = context;
            _currentTrialCompleted = false;
            _completedTrialId = string.Empty;
            LogEvent("experiment_trial_prepare_started", BuildConditionSequencePayload(context, string.Empty));
            if (_conditionConfig == null || _instrumentation == null || _spawnManager == null || _roundManager == null)
            {
                string reason = "critical_reference_missing_before_condition_apply";
                if (restartBoundary)
                {
                    Debug.LogError($"{PauseLogPrefix} protocol_restart_failed | reason={reason} session_id={_sessionId ?? string.Empty} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode}");
                }

                LogEvent("experiment_trial_sanity_check_failed", BuildFailurePayload(reason));
                LogEvent("experiment_trial_prepare_completed", BuildFailurePayload(reason));
                yield break;
            }

            ApplyCondition(context);
            LogEvent("experiment_condition_started", BuildConditionSequencePayload(context, "condition_started"));
            if (context.VoiceEnabled)
            {
                ExperimentVoicePipelineBootstrap.EnsureExists().LogRuntimePhase(
                    "c11_condition_entered",
                    0L,
                    "condition_applied_before_trial",
                    context.ConditionId,
                    context.ConditionOrderIndex + 1,
                    context.RoundIndexWithinCondition);
            }

            if (resetFirst && !TryResetForTrial(out string resetFailure))
            {
                if (restartBoundary)
                {
                    Debug.LogError($"{PauseLogPrefix} protocol_restart_failed | reason={resetFailure} session_id={_sessionId ?? string.Empty} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context?.ConditionId ?? string.Empty}");
                }

                LogEvent("experiment_trial_prepare_completed", BuildFailurePayload(resetFailure));
                yield break;
            }

            if (resetFirst)
            {
                yield return WaitForActiveRoundCleanup("before_sanity_check");
                if (_spawnManager != null && _spawnManager.ActiveRoundChildCount > 0)
                {
                    string reason = "active_round_cleanup_timeout";
                    Dictionary<string, object> payload = BuildFailurePayload(reason);
                    payload["active_round_child_count"] = _spawnManager.ActiveRoundChildCount;
                    payload["active_round_residual_children"] = _spawnManager.DescribeActiveRoundChildren();
                    LogEvent("experiment_trial_reset_failed", payload);
                    LogEvent("experiment_trial_prepare_completed", payload);
                    if (restartBoundary)
                    {
                        Debug.LogError($"{PauseLogPrefix} protocol_restart_failed | reason={reason} session_id={_sessionId ?? string.Empty} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} active_round_children={_spawnManager.ActiveRoundChildCount}");
                    }
                    yield break;
                }
            }

            if (resetFirst)
            {
                string xrGateSource = $"trial_alignment:{context?.ConditionId ?? string.Empty}:{context?.TrialIndex ?? 0}";
                if (!ExperimentSimulationPauseAuthority.NormalizeForRunningScene(xrGateSource, this))
                {
                    MarkPreparedTrialInvalidAndBlock("pause_owner_active_before_xr_gate");
                    yield break;
                }

                ExperimentLocomotionStateGuard.CaptureAndLog("experiment_locomotion_state_before_xr_gate", xrGateSource, false);
                _trialXrGatePauseLease = ExperimentSimulationPauseAuthority.Acquire("experimental_xr_alignment_gate", xrGateSource, this);
                ExperimentXrRigAlignmentResult alignmentResult = null;
                try
                {
                    yield return ApplyUserRigResetForTrial(context, result => alignmentResult = result);
                }
                finally
                {
                    ReleaseTrialXrGatePause();
                }

                Dictionary<string, object> gatePayload = BuildContextPayload(
                    context,
                    alignmentResult != null && alignmentResult.Succeeded
                        ? "alignment_succeeded"
                        : alignmentResult?.FailureReason ?? "xr_rig_resetter_missing");
                gatePayload["alignment_succeeded"] = alignmentResult != null && alignmentResult.Succeeded;
                gatePayload["alignment_timed_out"] = alignmentResult != null && alignmentResult.TimedOut;
                gatePayload["alignment_attempts"] = alignmentResult?.AttemptCount ?? 0;
                gatePayload["position_error_m"] = alignmentResult?.PositionErrorMeters ?? -1f;
                gatePayload["horizontal_error_m"] = alignmentResult?.HorizontalErrorMeters ?? -1f;
                gatePayload["yaw_error_deg"] = alignmentResult?.YawErrorDegrees ?? -1f;
                gatePayload["fallback"] = alignmentResult != null && alignmentResult.Succeeded
                    ? "none"
                    : "continue_trial_fail_soft";
                LogEvent("experiment_xr_alignment_gate_released", gatePayload);

                ExperimentLocomotionStateReport locomotion = ExperimentLocomotionStateGuard.RestoreAndValidate(xrGateSource, true);
                ExperimentLocomotionStateGuard.CaptureAndLog("experiment_locomotion_state_after_xr_gate", xrGateSource, true);
                ExperimentLocomotionStateGuard.CaptureAndLog("experiment_locomotion_state_before_user_control", xrGateSource, true);
                if (!locomotion.IsValid)
                {
                    MarkPreparedTrialInvalidAndBlock("locomotion_state_invalid:" + locomotion.FailureReason);
                    yield break;
                }
            }

            ApplyRobotPoseResetForTrial(context);

            Physics.SyncTransforms();
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();

            if (!VerifyRobotPoseReset(context, out string poseFailure))
            {
                if (restartBoundary)
                {
                    Debug.LogError($"{PauseLogPrefix} protocol_restart_failed | reason={poseFailure} session_id={_sessionId ?? string.Empty} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context?.ConditionId ?? string.Empty}");
                }

                MarkPreparedTrialInvalidAndBlock(poseFailure);
                yield break;
            }
            else if (restartBoundary)
            {
                Debug.Log($"{PauseLogPrefix} protocol_restart_robot_pose_reset | session_id={_sessionId ?? string.Empty} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context?.ConditionId ?? string.Empty} trial_index={context?.TrialIndex ?? 0} round_index={context?.RoundIndexWithinCondition ?? 0} robot_pose={FormatTransformPose(_robotAdapter != null ? _robotAdapter.NavigationReference : null)}");
            }

            if (_spawnManager != null && _spawnManager.ActiveRoundChildCount > 0)
            {
                yield return WaitForActiveRoundCleanup("before_sanity_check_after_pose_reset");
            }

            if (!RunSanityChecks(context, out List<string> failures))
            {
                string failureReason = string.Join("|", failures);
                if (restartBoundary)
                {
                    Debug.LogError($"{PauseLogPrefix} protocol_restart_failed | reason={failureReason} session_id={_sessionId ?? string.Empty} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context?.ConditionId ?? string.Empty}");
                }

                MarkPreparedTrialInvalidAndBlock(failureReason);
                yield break;
            }

            bool incidentBoundary = _lastTrialClosedByTechnicalIncident;
            if (incidentBoundary)
            {
                Debug.Log($"{PauseLogPrefix} protocol_round_skip_next_round_begin | session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context.ConditionId} trial_index={context.TrialIndex} round_index={context.RoundIndexWithinCondition} robot_pose={FormatTransformPose(_robotAdapter != null ? _robotAdapter.NavigationReference : null)}");
            }

            SpawnRoundForCurrentTrial(context);
            if (incidentBoundary)
            {
                Debug.Log($"{PauseLogPrefix} protocol_round_skip_boxes_spawn_requested | session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context.ConditionId} trial_index={context.TrialIndex} round_index={context.RoundIndexWithinCondition} active_round_path={GetTransformPath(_spawnManager != null ? _spawnManager.activeRoundContainer : null)}");
            }

            Physics.SyncTransforms();
            yield return null;
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();

            if (incidentBoundary)
            {
                LogIncidentRoundSpawnState(context, "protocol_round_skip_boxes_spawned");
                LogIncidentPalletState(context);
                Debug.Log($"{PauseLogPrefix} protocol_round_skip_robot_pose_before_next_assist | session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context.ConditionId} trial_index={context.TrialIndex} round_index={context.RoundIndexWithinCondition} robot_pose={FormatTransformPose(_robotAdapter != null ? _robotAdapter.NavigationReference : null)}");
            }
            else if (restartBoundary)
            {
                LogIncidentRoundSpawnState(context, "protocol_restart_first_round_boxes_spawned");
                LogIncidentPalletState(context);
            }

            if (context.VoiceEnabled)
            {
                bool voiceActivated = _voiceRecognitionController != null &&
                    _voiceRecognitionController.SetExperimentVoicePipelineActive(
                        true,
                        context.ConditionId,
                        context.ConditionOrderIndex + 1,
                        context.RoundIndexWithinCondition,
                        "before_c11_trial_started");
                if (!voiceActivated)
                {
                    MarkPreparedTrialInvalidAndBlock("voice_pipeline_activation_failed");
                    yield break;
                }
            }

            _instrumentation?.StartTrial();
            _trialActive = true;
            _currentTrialCompleted = false;
            _completedTrialId = string.Empty;
            string assistBlockReason = string.Empty;
            bool assistReady = !incidentBoundary || IsIncidentRoundReadyForAssistance(context, out assistBlockReason);
            if (assistReady)
            {
                _assistanceCoordinator?.InitializeRoundFromScene();
                if (incidentBoundary)
                {
                    Debug.Log($"{PauseLogPrefix} protocol_round_skip_assist_launched | session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context.ConditionId} trial_index={context.TrialIndex} round_index={context.RoundIndexWithinCondition}");
                }
            }
            else
            {
                _assistanceCoordinator?.ResetForNewExperimentTrial(assistBlockReason);
                _commandBridge?.ResetForNewExperimentTrial(assistBlockReason);
                _robotAdapter?.ClearExperimentRuntimeState(assistBlockReason);
                Debug.LogWarning($"{PauseLogPrefix} protocol_round_skip_assist_deferred_until_ready | reason={assistBlockReason} session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context.ConditionId} trial_index={context.TrialIndex} round_index={context.RoundIndexWithinCondition} robot_pose={FormatTransformPose(_robotAdapter != null ? _robotAdapter.NavigationReference : null)}");
            }

            LogEvent("experiment_trial_started_by_orchestrator", BuildTrialPayload("trial_started"));
            if (context.VoiceEnabled)
            {
                ExperimentVoicePipelineBootstrap.EnsureExists().LogRuntimePhase(
                    "c11_trial_started",
                    0L,
                    "voice_pipeline_already_active",
                    context.ConditionId,
                    context.ConditionOrderIndex + 1,
                    context.RoundIndexWithinCondition);
            }
            LogEvent("experiment_trial_prepare_completed", BuildTrialPayload("trial_prepared"));
            if (incidentBoundary)
            {
                Debug.Log($"{PauseLogPrefix} protocol_round_skip_ready | session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context.ConditionId} trial_index={context.TrialIndex} round_index={context.RoundIndexWithinCondition} assist_ready={assistReady} assist_block_reason={assistBlockReason ?? string.Empty} buttons_should_be_active=True active_round_path={GetTransformPath(_spawnManager != null ? _spawnManager.activeRoundContainer : null)}");
                _lastTrialClosedByTechnicalIncident = false;
            }
            else if (restartBoundary)
            {
                Debug.Log($"{PauseLogPrefix} protocol_restart_operational_ready | session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context.ConditionId} trial_index={context.TrialIndex} round_index={context.RoundIndexWithinCondition} assist_ready={assistReady} assist_block_reason={assistBlockReason ?? string.Empty} buttons_should_be_active=True active_round_path={GetTransformPath(_spawnManager != null ? _spawnManager.activeRoundContainer : null)} trial_active={_trialActive} round_active={(_roundManager != null && _roundManager.RoundActive)} box_count={CountActiveRoundBoxes()} pallet_count={CountActiveRoundPallets()} robot_pose={FormatTransformPose(_robotAdapter != null ? _robotAdapter.NavigationReference : null)}");
            }

            }
            finally
            {
                _trialTransitionBusy = false;
                _trialPreparationCoroutine = null;
                ReleaseTrialXrGatePause();
            }
        }

        private void HandleRoundCompleted(ExperimentalRoundSnapshot snapshot)
        {
            if (_runMode != ExperimentRunMode.Orchestrated2x2)
            {
                return;
            }

            if (snapshot == null || !snapshot.RoundFinished)
            {
                return;
            }

            MarkCurrentTrialCompletedByOrchestrator("round_completed_observed_by_orchestrator", snapshot);
        }

        private bool TryFinalizeCompletedActiveTrialForAdvance(string reason, out string reconcileReason)
        {
            if (TryGetHeldOrSelectedActiveRoundBox(out BoxMetadata heldBox))
            {
                reconcileReason = "round_box_still_grabbed_or_selected:" + heldBox.gameObject.name;
                return false;
            }

            if (!_lastTrialClosedByTechnicalIncident && !IsCurrentRoundCompleted())
            {
                reconcileReason = "round_not_completed";
                return false;
            }

            CollectActiveRoundBoxCounts(out int totalBoxes, out int completedCount);
            int assignedCount = _assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox ? 1 : 0;
            int pendingCount = Mathf.Max(0, totalBoxes - completedCount - assignedCount);
            bool instrumentationTerminal = IsInstrumentationCurrentTrialTerminal();
            bool roundCompleted = IsCurrentRoundCompleted();
            bool robotRuntimeBusy = IsRobotRuntimeBusy();
            var snapshot = new ExperimentTrialAdvanceSnapshot(
                _trialActive,
                _instrumentation != null && _instrumentation.TrialActive,
                instrumentationTerminal,
                roundCompleted,
                robotRuntimeBusy,
                totalBoxes,
                completedCount,
                pendingCount,
                assignedCount,
                _currentTrialId,
                _instrumentation != null ? _instrumentation.LastTerminalTrialId : string.Empty);
            if (!ExperimentTrialAdvanceReconciler.CanFinalizeCompletedTrialForAdvance(snapshot, out reconcileReason))
            {
                return false;
            }

            if (_instrumentation != null && _instrumentation.TrialActive && (roundCompleted || (totalBoxes > 0 && completedCount >= totalBoxes && pendingCount == 0 && assignedCount == 0)))
            {
                _instrumentation.EndTrialManually("completed");
            }

            return MarkCurrentTrialCompletedByOrchestrator(reason, _roundManager != null ? _roundManager.CurrentRound : null);
        }

        private bool IsCompletedTrialAwaitingRuntimeAdvance(out string reason)
        {
            reason = string.Empty;
            if (_currentPlanEntryIndex < 0 || string.IsNullOrWhiteSpace(_currentTrialId))
            {
                reason = "no_current_trial";
                return false;
            }

            CollectActiveRoundBoxCounts(out int totalBoxes, out int completedCount);
            CollectAssistanceCounts(out int assistedPendingCount, out int assistedAssignedCount, out int assistedCompletedCount);
            int assignedCount = _assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox
                ? Math.Max(1, assistedAssignedCount)
                : assistedAssignedCount;
            int mergedCompletedCount = Math.Max(completedCount, assistedCompletedCount);
            int mergedTotalBoxes = Math.Max(totalBoxes, mergedCompletedCount + assignedCount + assistedPendingCount);
            int pendingCount = assistedPendingCount > 0
                ? assistedPendingCount
                : Math.Max(0, mergedTotalBoxes - mergedCompletedCount - assignedCount);

            if (IsInstrumentationCurrentTrialTerminal())
            {
                reason = "ready_to_advance:instrumentation_terminal_trial";
                return true;
            }

            if (_currentTrialCompleted || _orchestratorCompletionLoggedTrialIds.Contains(_currentTrialId))
            {
                reason = "ready_to_advance:orchestrator_trial_completed";
                return true;
            }

            if (IsCurrentRoundCompleted() || (_assistanceCoordinator != null && _assistanceCoordinator.AssistanceRoundCompleted))
            {
                reason = "ready_to_advance:round_completed";
                return true;
            }

            bool allBoxesCompleted = mergedTotalBoxes > 0 &&
                mergedCompletedCount >= mergedTotalBoxes &&
                pendingCount == 0 &&
                assignedCount == 0;
            if (allBoxesCompleted && !IsRobotRuntimeBusy())
            {
                reason = "ready_to_advance:all_boxes_completed";
                return true;
            }

            reason = "trial_incomplete";
            return false;
        }

        private bool MarkCurrentTrialCompletedByOrchestrator(string reason, ExperimentalRoundSnapshot snapshot)
        {
            string trialId = !string.IsNullOrWhiteSpace(_currentTrialId)
                ? _currentTrialId
                : (_currentContext != null ? _currentContext.TrialId : string.Empty);
            if (string.IsNullOrWhiteSpace(trialId))
            {
                return false;
            }

            if (_currentTrialCompleted || _orchestratorCompletionLoggedTrialIds.Contains(trialId))
            {
                LogDuplicateCompletionIfNeeded(reason);
                _trialActive = false;
                DeactivateVoicePipelineAtTrialBoundary(reason);
                return true;
            }

            _currentTrialCompleted = true;
            _completedTrialId = trialId;
            _orchestratorCompletionLoggedTrialIds.Add(trialId);
            _trialActive = false;
            DeactivateVoicePipelineAtTrialBoundary(reason);

            Dictionary<string, object> payload = BuildTrialPayload(reason);
            payload["orchestrator_trial_completed"] = true;
            payload["instrumentation_trial_active"] = _instrumentation != null && _instrumentation.TrialActive;
            payload["round_finished"] = snapshot != null ? snapshot.RoundFinished : (_roundManager != null && _roundManager.RoundFinished);
            payload["round_active"] = snapshot != null ? snapshot.RoundActive : (_roundManager != null && _roundManager.RoundActive);
            payload["total_boxes"] = snapshot != null ? snapshot.TotalBoxes : (_roundManager != null ? _roundManager.TotalBoxesInRound : 0);
            payload["deposited_boxes"] = snapshot != null ? snapshot.DepositedBoxes : (_roundManager != null ? _roundManager.DepositedBoxes : 0);
            AddStartNextTrialDiagnostics(payload, reason);
            LogEvent("experiment_trial_completed_by_orchestrator", payload);
            return true;
        }

        private void DeactivateVoicePipelineAtTrialBoundary(string reason)
        {
            string conditionId = _currentContext?.ConditionId ?? CurrentConditionId ?? string.Empty;
            int visibleTrial = _currentContext != null ? _currentContext.ConditionOrderIndex + 1 : 0;
            int round = _currentContext?.RoundIndexWithinCondition ?? _currentRoundIndexWithinCondition;
            _voiceRecognitionController?.SetExperimentVoicePipelineActive(
                false,
                conditionId,
                visibleTrial,
                round,
                "trial_boundary:" + (reason ?? string.Empty));
        }

        private bool IsCurrentRoundCompleted()
        {
            return _roundManager != null && _roundManager.RoundFinished && !_roundManager.RoundActive;
        }

        private bool IsInstrumentationCurrentTrialTerminal()
        {
            if (_instrumentation == null || _instrumentation.TrialActive)
            {
                return false;
            }

            string currentTrialId = !string.IsNullOrWhiteSpace(_currentTrialId)
                ? _currentTrialId
                : (_currentContext != null ? _currentContext.TrialId : string.Empty);
            if (string.IsNullOrWhiteSpace(currentTrialId))
            {
                return false;
            }

            return string.Equals(_instrumentation.LastTerminalTrialId, currentTrialId, StringComparison.Ordinal);
        }

        private bool IsRobotRuntimeBusy()
        {
            if (_robotAdapter == null)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(_robotAdapter.HeldObjectId))
            {
                return true;
            }

            RobotMode? authoritativeMode = _robotAdapter.AuthoritativeRobotMode;
            if (!authoritativeMode.HasValue || authoritativeMode.Value != RobotMode.Idle)
            {
                return true;
            }

            if (_robotAdapter.Blackboard == null)
            {
                return true;
            }

            return _robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out TaskStatus status) &&
                status == TaskStatus.InProgress;
        }

        private void CollectActiveRoundBoxCounts(out int totalBoxes, out int completedCount)
        {
            totalBoxes = 0;
            completedCount = 0;
            Transform container = _spawnManager != null ? _spawnManager.activeRoundContainer : null;
            if (container != null)
            {
                BoxMetadata[] boxes = container.GetComponentsInChildren<BoxMetadata>(true);
                foreach (BoxMetadata box in boxes)
                {
                    if (box == null)
                    {
                        continue;
                    }

                    totalBoxes++;
                    if (box.isDeposited)
                    {
                        completedCount++;
                    }
                }
            }

            if (_roundManager != null)
            {
                totalBoxes = Mathf.Max(totalBoxes, _roundManager.TotalBoxesInRound);
                completedCount = Mathf.Max(completedCount, _roundManager.DepositedBoxes);
            }
        }

        private bool HasCurrentTrialForAdvance()
        {
            return _currentPlanEntryIndex >= 0 && !string.IsNullOrWhiteSpace(_currentTrialId);
        }

        private bool TryGetHeldOrSelectedActiveRoundBox(out BoxMetadata heldBox)
        {
            heldBox = null;
            Transform container = _spawnManager != null ? _spawnManager.activeRoundContainer : null;
            if (container == null && _roundManager != null)
            {
                container = _roundManager.ActiveRoundContainer;
            }
            if (container == null)
            {
                return false;
            }

            foreach (BoxMetadata box in container.GetComponentsInChildren<BoxMetadata>(true))
            {
                if (box != null && box.IsGrabbedOrSelected)
                {
                    heldBox = box;
                    return true;
                }
            }

            return false;
        }

        private void LogDuplicateCompletionIfNeeded(string reason)
        {
            string trialId = !string.IsNullOrWhiteSpace(_currentTrialId)
                ? _currentTrialId
                : (_currentContext != null ? _currentContext.TrialId : string.Empty);
            if (string.IsNullOrWhiteSpace(trialId) || !_orchestratorCompletionLoggedTrialIds.Contains(trialId))
            {
                return;
            }

            Dictionary<string, object> payload = BuildTrialPayload(string.IsNullOrWhiteSpace(reason) ? "duplicate_completion" : reason);
            payload["duplicate_completion_ignored"] = true;
            payload["completed_trial_id"] = _completedTrialId;
            payload["instrumentation_trial_active"] = _instrumentation != null && _instrumentation.TrialActive;
            payload["round_finished"] = _roundManager != null && _roundManager.RoundFinished;
            payload["round_active"] = _roundManager != null && _roundManager.RoundActive;
            LogEvent("experiment_trial_completion_duplicate_ignored", payload);
        }

        private void ApplyCondition(ExperimentRuntimeContext context)
        {
            _currentCondition = context != null && _conditions != null && context.ConditionOrderIndex >= 0 && context.ConditionOrderIndex < _conditions.Count
                ? _conditions[context.ConditionOrderIndex]
                : ResolveSelectedCondition();
            _currentTrialIndex = context.TrialIndex;
            _currentTrialId = context.TrialId;
            _currentRoundId = context.RoundId;
            _currentRoundIndex = context.RoundIndex;
            _currentRoundIndexWithinCondition = context.RoundIndexWithinCondition;
            _currentGlobalRoundIndex = context.GlobalRoundIndex;

            _instrumentation?.ApplyOrchestratedContext(context);

            _conditionConfig.SetRunMode(_runMode);
            _conditionConfig.ApplyConditionFromOrchestrator(
                context.RobotEnabled,
                context.VoiceEnabled,
                context.ConditionName,
                context.AssistanceMode,
                "experiment_session_orchestrator");

            if (!context.VoiceEnabled)
            {
                _voiceRecognitionController?.SetExperimentVoicePipelineActive(
                    false,
                    context.ConditionId,
                    context.ConditionOrderIndex + 1,
                    context.RoundIndexWithinCondition,
                    "voice_disabled_by_condition");
            }

            if (_spawnManager != null)
            {
                _spawnManager.spawnGenerationMode = context.SpawnGenerationMode;
            }

            LogEvent("experiment_condition_orchestrator_applied", BuildTrialPayload("condition_applied"));
        }

        private bool TryResetForTrial(out string failureReason)
        {
            failureReason = string.Empty;
            LogEvent("experiment_trial_reset_started", BuildTrialPayload("reset_started"));

            if (_robotAdapter != null && !string.IsNullOrWhiteSpace(_robotAdapter.HeldObjectId))
            {
                failureReason = "robot_holding_object";
                LogEvent("experiment_trial_reset_failed", BuildFailurePayload(failureReason));
                return false;
            }

            _voiceConnector?.ResetForNewExperimentTrial("experiment_trial_reset");
            _assistanceCoordinator?.ResetForNewExperimentTrial("experiment_trial_reset");
            _commandBridge?.ResetForNewExperimentTrial("experiment_trial_reset");
            _robotAdapter?.ClearExperimentRuntimeState("experiment_trial_reset");
            LogActiveRoundCleanupSnapshot("active_round_children_before_reset");
            _spawnManager?.ClearCurrentRound();
            _roundManager?.NotifyRoundReset();
            LogActiveRoundCleanupSnapshot("active_round_children_after_reset_request");

            LogEvent("experiment_trial_reset_completed", BuildTrialPayload("reset_completed"));
            return true;
        }

        private void MarkPreparedTrialInvalidAndBlock(string failureReason)
        {
            string reason = string.IsNullOrWhiteSpace(failureReason) ? "invalid_before_start" : failureReason;
            _robotAdapter?.ClearExperimentRuntimeState(reason);
            _assistanceCoordinator?.ResetForNewExperimentTrial(reason);
            _commandBridge?.ResetForNewExperimentTrial(reason);
            _instrumentation?.RecordPreparedTrialInvalid(reason);
            LogEvent("experiment_trial_prepare_completed", BuildFailurePayload(reason));
            _trialTransitionBusy = false;
            _trialPreparationCoroutine = null;
        }

        private bool ShouldMarkManualEndIncomplete(out string reason)
        {
            reason = string.Empty;
            var reasons = new List<string>();
            CollectActiveRoundBoxCounts(out int totalBoxes, out int completedBoxes);
            int pendingBoxes = Mathf.Max(0, totalBoxes - completedBoxes);
            if (totalBoxes > 0 && pendingBoxes > 0)
            {
                reasons.Add($"boxes_pending:{completedBoxes}/{totalBoxes}");
            }

            if (_roundManager != null && _roundManager.RoundActive && !_roundManager.RoundFinished)
            {
                reasons.Add("round_not_complete");
            }

            if (_robotAdapter != null)
            {
                if (!string.IsNullOrWhiteSpace(_robotAdapter.HeldObjectId))
                {
                    reasons.Add("robot_holding_object");
                }

                RobotMode? authoritativeMode = _robotAdapter.AuthoritativeRobotMode;
                if (!authoritativeMode.HasValue)
                {
                    reasons.Add("robot_mode_unavailable");
                }
                else if (authoritativeMode.Value != RobotMode.Idle)
                {
                    reasons.Add("robot_not_idle");
                }

                if (_robotAdapter.Blackboard != null)
                {
                    if (_robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out TaskStatus status) &&
                        status == TaskStatus.InProgress)
                    {
                        reasons.Add("task_in_progress");
                    }

                    if (_robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor currentTarget) &&
                        currentTarget != null)
                    {
                        reasons.Add("current_target_active");
                    }

                    if (_robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor placeTarget) &&
                        placeTarget != null)
                    {
                        reasons.Add("place_target_active");
                    }
                }
            }

            if (_assistanceCoordinator != null && (_assistanceCoordinator.HasAssignedBox || _assistanceCoordinator.ExplicitIntentProcessing))
            {
                reasons.Add("assistance_active");
            }

            reason = reasons.Count > 0
                ? "operator_terminated_incomplete:" + string.Join("|", reasons)
                : string.Empty;
            return reasons.Count > 0;
        }

        private Dictionary<string, object> BuildManualEndPayload(bool manualEndAllowed, string manualEndReason)
        {
            CollectActiveRoundBoxCounts(out int totalBoxes, out int completedBoxes);
            int pendingBoxes = Mathf.Max(0, totalBoxes - completedBoxes);
            Dictionary<string, object> payload = BuildTrialPayload(manualEndReason);
            payload["completed_boxes"] = completedBoxes;
            payload["pending_boxes"] = pendingBoxes;
            payload["total_boxes"] = totalBoxes;
            payload["completion_ratio"] = totalBoxes > 0 ? completedBoxes / (float)totalBoxes : 0f;
            payload["manual_end_requested"] = true;
            payload["manual_end_allowed"] = manualEndAllowed;
            payload["manual_end_reason"] = manualEndReason ?? string.Empty;
            payload["round_completed"] = IsCurrentRoundCompleted();
            payload["round_active"] = _roundManager != null && _roundManager.RoundActive;
            payload["round_finished"] = _roundManager != null && _roundManager.RoundFinished;
            return payload;
        }

        private bool RunSanityChecks(ExperimentRuntimeContext context, out List<string> failures)
        {
            failures = new List<string>();
            LogEvent("experiment_trial_sanity_check_started", BuildTrialPayload("sanity_started"));

            AddMissing(failures, _conditionConfig, "condition_config_missing");
            AddMissing(failures, _instrumentation, "instrumentation_missing");
            AddMissing(failures, _spawnManager, "spawn_manager_missing");
            AddMissing(failures, _roundManager, "round_manager_missing");
            AddMissing(failures, _assistanceCoordinator, "assistance_coordinator_missing");
            AddMissing(failures, _robotAdapter, "robot_adapter_missing");
            AddMissing(failures, _commandBridge, "bridge_missing");

            if (context != null && context.VoiceEnabled)
            {
                AddMissing(failures, _voiceConnector, "voice_connector_missing_for_voice_condition");
            }

            if (context != null && !context.MatchesConditionPreset(out string conditionReason))
            {
                failures.Add(conditionReason);
            }

            if (CountConditionProviders() > 1)
            {
                failures.Add("multiple_condition_providers_active");
            }

            if (_conditionConfig != null && context != null)
            {
                ExperimentConditionConfig applied = _conditionConfig.CurrentCondition;
                if (applied == null ||
                    applied.RobotEnabled != context.RobotEnabled ||
                    applied.VoiceEnabled != context.VoiceEnabled ||
                    applied.AssistanceMode != context.AssistanceMode ||
                    !string.Equals(applied.ConditionName, context.ConditionName, StringComparison.Ordinal))
                {
                    failures.Add("condition_config_mismatch");
                }
            }

            if (_instrumentation != null && !_instrumentation.IsSynchronizedWithContext(context, out string instrumentationReason))
            {
                failures.Add(instrumentationReason);
            }

            if (_instrumentation != null && _instrumentation.TrialActive)
            {
                failures.Add("instrumentation_trial_already_active");
            }

            if (_spawnManager != null && context != null && _spawnManager.spawnGenerationMode != context.SpawnGenerationMode)
            {
                failures.Add("spawn_mode_effective_mismatch");
            }

            if (_roundManager != null && _roundManager.RoundActive)
            {
                failures.Add("round_manager_active_round_not_clean");
            }

            if (_spawnManager != null && _spawnManager.activeRoundContainer != null && _spawnManager.activeRoundContainer.childCount > 0)
            {
                failures.Add("active_round_container_not_empty");
            }

            if (_assistanceCoordinator != null)
            {
                if (_assistanceCoordinator.HasAssignedBox)
                {
                    failures.Add("assistance_assigned_box_not_clean");
                }

                if (_assistanceCoordinator.ReservedDepositSlotCount > 0 || _assistanceCoordinator.OccupiedDepositSlotCount > 0)
                {
                    failures.Add("deposit_slots_not_clean");
                }

                if (_assistanceCoordinator.PendingApproachCandidateCacheCount > 0)
                {
                    failures.Add("candidate_cache_not_clean");
                }

                if (_assistanceCoordinator.RoundInitialized || _assistanceCoordinator.ExplicitIntentProcessing)
                {
                    failures.Add("assistance_active_before_trial");
                }
            }

            if (_robotAdapter != null && !string.IsNullOrWhiteSpace(_robotAdapter.HeldObjectId))
            {
                failures.Add("robot_holding_object");
            }

            if (_voiceConnector != null && _voiceConnector.HasPendingConfirmation)
            {
                failures.Add("voice_confirmation_pending");
            }

            string eventType = failures.Count == 0
                ? "experiment_trial_sanity_check_passed"
                : "experiment_trial_sanity_check_failed";
            Dictionary<string, object> payload = BuildTrialPayload(failures.Count == 0 ? "sanity_passed" : "sanity_failed");
            payload["failures"] = string.Join("|", failures);
            LogEvent(eventType, payload);
            return failures.Count == 0;
        }

        private void SpawnRoundForCurrentTrial(ExperimentRuntimeContext context)
        {
            Dictionary<string, object> requestPayload = BuildTrialPayload("round_spawn_requested");
            requestPayload["spawn_generation_mode"] = context.SpawnGenerationMode.ToString();
            LogEvent("experiment_trial_round_spawn_requested", requestPayload);

            _spawnManager.SpawnNewRound();

            List<string> boxIds = CollectActiveRoundBoxIds();
            Dictionary<string, object> completedPayload = BuildTrialPayload("round_spawn_completed");
            completedPayload["spawn_generation_mode"] = context.SpawnGenerationMode.ToString();
            completedPayload["box_count"] = boxIds.Count;
            completedPayload["box_ids"] = string.Join("|", boxIds);
            LogEvent("experiment_trial_round_spawn_completed", completedPayload);
        }

        private bool IsIncidentRoundReadyForAssistance(ExperimentRuntimeContext context, out string blockReason)
        {
            blockReason = string.Empty;
            int activeChildren = _spawnManager != null ? _spawnManager.ActiveRoundChildCount : 0;
            List<string> boxIds = CollectActiveRoundBoxIds();
            if (_spawnManager == null || _spawnManager.activeRoundContainer == null)
            {
                blockReason = "incident_round_active_round_missing";
                Debug.LogWarning($"{PauseLogPrefix} protocol_round_skip_failed | reason={blockReason} session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context?.ConditionId ?? string.Empty} trial_index={context?.TrialIndex ?? 0} round_index={context?.RoundIndexWithinCondition ?? 0}");
                return false;
            }

            if (activeChildren <= 0 || boxIds.Count <= 0)
            {
                blockReason = $"incident_round_boxes_not_ready:children={activeChildren}:boxes={boxIds.Count}";
                Debug.LogWarning($"{PauseLogPrefix} protocol_round_skip_failed | reason={blockReason} session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context?.ConditionId ?? string.Empty} trial_index={context?.TrialIndex ?? 0} round_index={context?.RoundIndexWithinCondition ?? 0}");
                return false;
            }

            float minPalletDistance = CalculateMinRobotDistanceToPallet(out string closestPalletPath, out Bounds closestBounds);
            if (!float.IsNaN(minPalletDistance) && minPalletDistance < IncidentRoundPalletUnsafeDistanceMeters)
            {
                ApplyRobotPoseResetForTrial(context);
                Physics.SyncTransforms();
                minPalletDistance = CalculateMinRobotDistanceToPallet(out closestPalletPath, out closestBounds);
                if (!float.IsNaN(minPalletDistance) && minPalletDistance < IncidentRoundPalletUnsafeDistanceMeters)
                {
                    blockReason = $"incident_round_robot_too_close_to_pallet:{minPalletDistance:0.###}";
                    Debug.LogWarning($"{PauseLogPrefix} protocol_round_skip_assist_deferred_until_ready | reason={blockReason} closest_pallet={closestPalletPath} pallet_bounds_center={closestBounds.center} pallet_bounds_size={closestBounds.size} threshold={IncidentRoundPalletUnsafeDistanceMeters:0.###} session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context?.ConditionId ?? string.Empty} trial_index={context?.TrialIndex ?? 0} round_index={context?.RoundIndexWithinCondition ?? 0}");
                    return false;
                }
            }

            return true;
        }

        private string BuildPauseRoundSkipStateFragment()
        {
            CollectActiveRoundBoxCounts(out int totalBoxes, out int completedBoxes);
            return
                $"session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} " +
                $"condition_id={CurrentConditionId} trial_index={_currentTrialIndex} round_index={_currentRoundIndexWithinCondition} " +
                $"trial_active={_trialActive} instrumentation_trial_active={(_instrumentation != null && _instrumentation.TrialActive)} " +
                $"transition_busy={TrialTransitionBusy} round_active={(_roundManager != null && _roundManager.RoundActive)} " +
                $"round_finished={(_roundManager != null && _roundManager.RoundFinished)} total_boxes={totalBoxes} completed_boxes={completedBoxes} " +
                $"active_round_path={GetTransformPath(_spawnManager != null ? _spawnManager.activeRoundContainer : null)} " +
                $"active_round_children={(_spawnManager != null ? _spawnManager.ActiveRoundChildCount : 0)} " +
                $"robot_pose={FormatTransformPose(_robotAdapter != null ? _robotAdapter.NavigationReference : null)} " +
                $"robot_mode={(_robotAdapter != null && _robotAdapter.Blackboard != null ? _robotAdapter.Blackboard.CurrentMode.ToString() : string.Empty)} " +
                $"current_task={(_robotAdapter != null ? _robotAdapter.ActiveP40TaskInstanceId : string.Empty)} " +
                $"pending_task={(_robotAdapter != null ? _robotAdapter.ActiveP40RequestId : string.Empty)} " +
                $"task_status={ReadRobotTaskStatus()} held_object_id={(_robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty)} " +
                $"current_target_id={(_robotAdapter != null ? _robotAdapter.ActiveP40TargetId : string.Empty)} " +
                $"place_target_id={(_robotAdapter != null ? _robotAdapter.ActiveP40PlaceTargetId : string.Empty)} " +
                $"blackboard_current_target={ReadBlackboardTargetId(TaskBlackboardKeys.CurrentTarget)} " +
                $"blackboard_place_target={ReadBlackboardTargetId(TaskBlackboardKeys.PlaceTarget)} " +
                $"assistance_initialized={(_assistanceCoordinator != null && _assistanceCoordinator.RoundInitialized)} " +
                $"assistance_has_assigned_box={(_assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox)}";
        }

        private void LogIncidentRoundSpawnState(ExperimentRuntimeContext context, string eventName)
        {
            List<string> boxIds = CollectActiveRoundBoxIds();
            Debug.Log($"{PauseLogPrefix} {eventName} | session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context?.ConditionId ?? string.Empty} trial_index={context?.TrialIndex ?? 0} round_index={context?.RoundIndexWithinCondition ?? 0} active_round_path={GetTransformPath(_spawnManager != null ? _spawnManager.activeRoundContainer : null)} active_round_children={(_spawnManager != null ? _spawnManager.ActiveRoundChildCount : 0)} box_count={boxIds.Count} box_ids={string.Join("|", boxIds)}");
        }

        private void LogIncidentPalletState(ExperimentRuntimeContext context)
        {
            float minDistance = CalculateMinRobotDistanceToPallet(out string closestPalletPath, out Bounds bounds);
            int palletCount = CountActiveRoundPallets();
            Debug.Log($"{PauseLogPrefix} protocol_round_skip_pallet_state | session_id={_sessionId} questionnaire_code={ExperimentDataPathResolver.CurrentQuestionnaireCode} condition_id={context?.ConditionId ?? string.Empty} trial_index={context?.TrialIndex ?? 0} round_index={context?.RoundIndexWithinCondition ?? 0} pallet_count={palletCount} closest_pallet={closestPalletPath} closest_distance={minDistance:0.###} bounds_center={bounds.center} bounds_size={bounds.size} active_round_path={GetTransformPath(_spawnManager != null ? _spawnManager.activeRoundContainer : null)}");
        }

        private float CalculateMinRobotDistanceToPallet(out string closestPalletPath, out Bounds closestBounds)
        {
            closestPalletPath = string.Empty;
            closestBounds = default;
            Transform robot = _robotAdapter != null ? _robotAdapter.NavigationReference : null;
            Transform container = _spawnManager != null ? _spawnManager.activeRoundContainer : null;
            if (robot == null || container == null)
            {
                return float.NaN;
            }

            float minDistance = float.PositiveInfinity;
            Collider[] colliders = container.GetComponentsInChildren<Collider>(true);
            foreach (Collider collider in colliders)
            {
                if (collider == null || !IsPalletTransform(collider.transform))
                {
                    continue;
                }

                Vector3 closest = collider.ClosestPoint(robot.position);
                float distance = Vector3.Distance(robot.position, closest);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    closestPalletPath = GetTransformPath(collider.transform);
                    closestBounds = collider.bounds;
                }
            }

            return float.IsPositiveInfinity(minDistance) ? float.NaN : minDistance;
        }

        private int CountActiveRoundPallets()
        {
            Transform container = _spawnManager != null ? _spawnManager.activeRoundContainer : null;
            if (container == null)
            {
                return 0;
            }

            int count = 0;
            foreach (Transform child in container.GetComponentsInChildren<Transform>(true))
            {
                if (IsPalletTransform(child))
                {
                    count++;
                }
            }

            return count;
        }

        private int CountActiveRoundBoxes()
        {
            Transform container = _spawnManager != null ? _spawnManager.activeRoundContainer : null;
            return container != null ? container.GetComponentsInChildren<BoxMetadata>(true).Length : 0;
        }

        private static bool IsPalletTransform(Transform transform)
        {
            return transform != null &&
                transform.name.IndexOf("pallet", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsHistorySessionPending(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            IReadOnlyList<ExperimentSessionIdHistoryEntry> entries = ExperimentSessionIdHistoryStore.LoadRecent(100);
            foreach (ExperimentSessionIdHistoryEntry entry in entries)
            {
                if (entry == null || !string.Equals(entry.session_id, sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                return ExperimentSavedExitResumePromptState.IsPendingPromptStatus(entry.status);
            }

            return false;
        }

        private IEnumerator Run2x2SanitySequence()
        {
            LogEvent("experiment_2x2_sanity_sequence_started", BuildBasePayload());
            List<ExperimentTrialPlanEntry> plan = BuildExpandedTrialPlan();
            _sessionTrialPlan = new List<ExperimentTrialPlanEntry>(plan);
            foreach (ExperimentTrialPlanEntry planEntry in plan)
            {
                Experiment2x2ConditionDefinition condition = planEntry.Condition;
                _selectedConditionIndex = planEntry.ConditionOrderIndex;
                _currentPlanEntryIndex = planEntry.TrialIndex - 1;
                _nextPlanEntryIndex = Mathf.Max(_nextPlanEntryIndex, _currentPlanEntryIndex + 1);
                LogEvent("experiment_2x2_sanity_condition_started", BuildConditionPayload(condition, planEntry.ConditionOrderIndex, planEntry.TrialIndex, "sanity_condition_started"));

                _nextTrialIndex = Mathf.Max(_nextTrialIndex, planEntry.TrialIndex + 1);
                yield return BeginTrialCoroutine(planEntry, resetFirst: true);

                if (condition.VoiceEnabled && _voiceConnector != null && !string.IsNullOrWhiteSpace(_manualTranscriptForSanity))
                {
                    LogEvent("experiment_2x2_sanity_manual_transcript_submitted", BuildTrialPayload("manual_transcript_debug_source"));
                    _voiceConnector.ProcessFinalTranscript(_manualTranscriptForSanity, "debug_manual_transcript", $"sanity_{condition.ConditionId}");
                }

                LogEvent("experiment_2x2_sanity_condition_completed", BuildTrialPayload("sanity_condition_completed"));
                yield return new WaitForSeconds(Mathf.Max(0f, _debugSequenceStepDelaySeconds));
            }

            _debugSequenceCoroutine = null;
            LogEvent("experiment_2x2_sanity_sequence_completed", BuildBasePayload());
        }

        private bool EnsureCanRunCommand(string commandName)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning($"{LogPrefix} {commandName} only runs in Play Mode.", this);
                return false;
            }

            TryResolveReferences();
            EnsureDefaultConditions();
            return true;
        }

        private void TryResolveReferences()
        {
            _conditionConfig ??= FindFirstObjectByType<ExperimentConditionConfigBehaviour>();
            _instrumentation ??= FindFirstObjectByType<ExperimentInstrumentationController>();
            _spawnManager ??= FindFirstObjectByType<SpawnManager>();
            _roundManager ??= FindFirstObjectByType<RoundManager>();
            _assistanceCoordinator ??= FindFirstObjectByType<RobotAssistanceRoundCoordinator>();
            _voiceRecognitionController ??= FindFirstObjectByType<VoiceRecognitionController>();
            _voiceConnector ??= FindFirstObjectByType<VoiceAutonomyCommandConnector>();
            _commandBridge ??= FindFirstObjectByType<MultimodalAutonomyCommandBridge>();
            _robotAdapter ??= FindFirstObjectByType<AutonomousRobotAdapter>();
            _xrRigResetter ??= FindFirstObjectByType<ExperimentXrRigResetter>();
            SyncRoundCompletedSubscription();
        }

        private void SyncRoundCompletedSubscription()
        {
            if (_subscribedRoundManager == _roundManager)
            {
                return;
            }

            UnsubscribeRoundCompleted();
            if (_roundManager != null)
            {
                _roundManager.RoundCompleted += HandleRoundCompleted;
                _subscribedRoundManager = _roundManager;
                LogRoundCompletionSubscription("experiment_orchestrator_round_completion_subscription_added", _roundManager);
            }
        }

        private void SubscribeTelemetryDiagnostics()
        {
            if (_telemetryDiagnosticsSubscribed)
            {
                return;
            }

            TiagoExperimentTelemetry.StructuredEventLogged += HandleStructuredEventForStartNextTrialDiagnostics;
            _telemetryDiagnosticsSubscribed = true;
        }

        private void UnsubscribeTelemetryDiagnostics()
        {
            if (!_telemetryDiagnosticsSubscribed)
            {
                return;
            }

            TiagoExperimentTelemetry.StructuredEventLogged -= HandleStructuredEventForStartNextTrialDiagnostics;
            _telemetryDiagnosticsSubscribed = false;
        }

        private void HandleStructuredEventForStartNextTrialDiagnostics(string eventType, Dictionary<string, object> payload, float unityTime)
        {
            if (_startNextTrialDepth <= 0 || _loggingStartNextTrialReentrantDiagnostic)
            {
                return;
            }

            bool terminalEvent = IsTrialTerminalTelemetryEvent(eventType);
            bool assistanceInvalidTrial = string.Equals(eventType, "assistance_blocked_by_inactive_or_invalid_trial", StringComparison.Ordinal);
            if (!terminalEvent && !assistanceInvalidTrial)
            {
                return;
            }

            try
            {
                _loggingStartNextTrialReentrantDiagnostic = true;
                if (terminalEvent)
                {
                    _completionDuringStartNextTrialDetected = true;
                    Dictionary<string, object> terminalPayload = BuildStartNextTrialDiagnosticPayload("terminal_event_during_start_next_trial", "reentrant_completion", _activeStartNextTrialInvocationId);
                    terminalPayload["observed_event_type"] = eventType ?? string.Empty;
                    terminalPayload["observed_event_unity_time"] = unityTime;
                    terminalPayload["observed_event_reason"] = GetPayloadString(payload, "reason");
                    terminalPayload["observed_event_trial_id"] = GetPayloadString(payload, "trial_id");
                    LogEvent("experiment_start_next_trial_reentrant_completion_detected", terminalPayload);
                }

                if (assistanceInvalidTrial)
                {
                    Dictionary<string, object> assistancePayload = BuildStartNextTrialDiagnosticPayload("assistance_invalid_trial_during_start_next_trial", "reentrant_assistance_block", _activeStartNextTrialInvocationId);
                    assistancePayload["observed_event_type"] = eventType ?? string.Empty;
                    assistancePayload["observed_event_unity_time"] = unityTime;
                    assistancePayload["observed_event_reason"] = GetPayloadString(payload, "reason");
                    LogEvent("experiment_start_next_trial_reentrant_assistance_block_detected", assistancePayload);
                    if (_trialActive && string.Equals(GetPayloadString(payload, "reason"), "trial_not_started_by_orchestrator", StringComparison.Ordinal))
                    {
                        LogStateContradictionsIfAny("assistance_invalid_trial_event", _activeStartNextTrialInvocationId, "assistance_trial_not_started_while_orchestrator_trial_active");
                    }
                }
            }
            finally
            {
                _loggingStartNextTrialReentrantDiagnostic = false;
            }
        }

        private static bool IsTrialTerminalTelemetryEvent(string eventType)
        {
            return string.Equals(eventType, "experiment_trial_completed", StringComparison.Ordinal) ||
                string.Equals(eventType, "experiment_trial_failed", StringComparison.Ordinal) ||
                string.Equals(eventType, "experiment_trial_aborted", StringComparison.Ordinal) ||
                string.Equals(eventType, "experiment_trial_completed_by_orchestrator", StringComparison.Ordinal);
        }

        private void UnsubscribeRoundCompleted()
        {
            if (_subscribedRoundManager != null)
            {
                RoundManager removed = _subscribedRoundManager;
                _subscribedRoundManager.RoundCompleted -= HandleRoundCompleted;
                _subscribedRoundManager = null;
                LogRoundCompletionSubscription("experiment_orchestrator_round_completion_subscription_removed", removed);
            }
        }

        private void LogRoundCompletionSubscription(string eventType, RoundManager roundManager)
        {
            if (!_sessionActive && !ExperimentDataPathResolver.HasActiveSession)
            {
                return;
            }

            Dictionary<string, object> payload = BuildBasePayload();
            payload["round_manager_name"] = roundManager != null ? roundManager.name : string.Empty;
            payload["round_manager_instance_id"] = roundManager != null ? roundManager.GetInstanceID() : 0;
            payload["round_manager_path"] = roundManager != null ? GetPath(roundManager.transform) : string.Empty;
            LogEvent(eventType, payload);
        }

        private void ConfigureOrchestratedConsumers()
        {
            if (_runMode != ExperimentRunMode.Orchestrated2x2)
            {
                return;
            }

            _conditionConfig?.SetRunMode(_runMode);
            _instrumentation?.SetRunMode(_runMode);
            _assistanceCoordinator?.SetExperimentRunMode(_runMode);
            _roundManager?.SetAutoStartNextRound(false, "orchestrated_2x2");
            CaptureInitialRobotPoseIfNeeded();
        }

        private Experiment2x2ConditionDefinition ResolveSelectedCondition()
        {
            EnsureDefaultConditions();
            if (_conditions == null || _conditions.Count == 0)
            {
                _conditions = Experiment2x2ConditionDefinition.CreateDefaultPresets(_defaultSpawnGenerationMode);
            }

            _selectedConditionIndex = Mathf.Clamp(_selectedConditionIndex, 0, _conditions.Count - 1);
            return _conditions[_selectedConditionIndex];
        }

        private void EnsureDefaultConditions()
        {
            if (!_autoCreateDefaultConditions)
            {
                return;
            }

            if (_conditions == null || _conditions.Count == 0)
            {
                _conditions = Experiment2x2ConditionDefinition.CreateDefaultPresets(_defaultSpawnGenerationMode);
                return;
            }

            Experiment2x2ConditionDefinition.EnsureCanonicalPresetMatrix(_conditions, _defaultSpawnGenerationMode);
        }

        public bool EnsureCanonicalConditionMatrix()
        {
            if (_conditions == null)
            {
                _conditions = new List<Experiment2x2ConditionDefinition>();
            }

            return Experiment2x2ConditionDefinition.EnsureCanonicalPresetMatrix(_conditions, _defaultSpawnGenerationMode);
        }

        private void OnValidate()
        {
            _roundsPerCondition = Mathf.Max(1, _roundsPerCondition);
            if (_autoCreateDefaultConditions)
            {
                EnsureCanonicalConditionMatrix();
            }
        }

        private List<string> CollectActiveRoundBoxIds()
        {
            var ids = new List<string>();
            Transform container = _spawnManager != null ? _spawnManager.activeRoundContainer : null;
            if (container == null)
            {
                return ids;
            }

            BoxMetadata[] boxes = container.GetComponentsInChildren<BoxMetadata>(true);
            foreach (BoxMetadata box in boxes)
            {
                if (box != null)
                {
                    ids.Add(box.gameObject.name);
                }
            }

            return ids;
        }

        private IEnumerator WaitForActiveRoundCleanup(string reason)
        {
            int before = _spawnManager != null ? _spawnManager.ActiveRoundChildCount : 0;
            Dictionary<string, object> startedPayload = BuildTrialPayload(reason);
            startedPayload["active_round_children_before_reset"] = before;
            startedPayload["cleanup_timeout_frames"] = _cleanupWaitTimeoutFrames;
            LogEvent("active_round_cleanup_wait_started", startedPayload);

            int waitedFrames = 0;
            while (_spawnManager != null &&
                   _spawnManager.ActiveRoundChildCount > 0 &&
                   waitedFrames < Mathf.Max(1, _cleanupWaitTimeoutFrames))
            {
                if (ExperimentRuntimePauseCoordinator.IsExperimentPaused)
                {
                    waitedFrames++;
                    yield return null;
                    continue;
                }

                waitedFrames++;
                yield return null;
            }

            int after = _spawnManager != null ? _spawnManager.ActiveRoundChildCount : 0;
            Dictionary<string, object> completedPayload = BuildTrialPayload(reason);
            completedPayload["active_round_children_before_reset"] = before;
            completedPayload["active_round_children_after_reset"] = after;
            completedPayload["cleanup_waited_frames"] = waitedFrames;
            completedPayload["active_round_residual_children"] = _spawnManager != null ? _spawnManager.DescribeActiveRoundChildren() : string.Empty;
            LogEvent(after == 0 ? "active_round_cleanup_completed" : "active_round_cleanup_timeout", completedPayload);
        }

        private IEnumerator CleanupAfterTrialClosed(string reason)
        {
            _trialTransitionBusy = true;
            try
            {
                if (TryResetForTrial(out string failureReason))
                {
                    yield return WaitForActiveRoundCleanup(reason);
                }
                else
                {
                    LogEvent("experiment_trial_cleanup_failed", BuildFailurePayload(failureReason));
                }
            }
            finally
            {
                _trialTransitionBusy = false;
                _trialEndCleanupCoroutine = null;
            }
        }

        private IEnumerator ApplyUserRigResetForTrial(
            ExperimentRuntimeContext context,
            Action<ExperimentXrRigAlignmentResult> completed)
        {
            if (_xrRigResetter == null)
            {
                Dictionary<string, object> payload = BuildContextPayload(context, "xr_rig_resetter_missing");
                payload["xr_rig_reset_configured"] = false;
                LogEvent("experiment_user_rig_reset_skipped", payload);
                completed?.Invoke(null);
                yield break;
            }

            ExperimentXrRigAlignmentResult result = null;
            yield return _xrRigResetter.AlignForTrialCoroutine(context, value => result = value);
            if (result == null || !result.Succeeded)
            {
                string reason = result?.FailureReason ?? "xr_alignment_result_missing";
                Dictionary<string, object> payload = BuildContextPayload(context, reason);
                payload["xr_rig_reset_configured"] = _xrRigResetter.IsConfigured;
                payload["alignment_timed_out"] = result != null && result.TimedOut;
                payload["alignment_attempts"] = result?.AttemptCount ?? 0;
                LogEvent("experiment_user_rig_reset_skipped", payload);
            }

            completed?.Invoke(result);
        }

        private void LogActiveRoundCleanupSnapshot(string eventType)
        {
            Dictionary<string, object> payload = BuildTrialPayload(eventType);
            payload["active_round_child_count"] = _spawnManager != null ? _spawnManager.ActiveRoundChildCount : 0;
            payload["active_round_residual_children"] = _spawnManager != null ? _spawnManager.DescribeActiveRoundChildren() : string.Empty;
            LogEvent(eventType, payload);
        }

        private void CaptureInitialRobotPoseIfNeeded()
        {
            if (_hasInitialRobotPose)
            {
                return;
            }

            _robotPoseReference = ResolveRobotPoseReference();
            if (_robotPoseReference == null)
            {
                return;
            }

            _initialRobotPosition = _robotPoseReference.position;
            _initialRobotRotation = _robotPoseReference.rotation;
            _robotPoseRoot = _robotPoseReference.root != null ? _robotPoseReference.root : _robotPoseReference;
            _initialRobotRootPosition = _robotPoseRoot.position;
            _initialRobotRootRotation = _robotPoseRoot.rotation;
            _authoritativePhysicsRoot = ResolveAuthoritativePhysicsRoot(_robotPoseReference);
            _initialAuthoritativePhysicsPosition = _authoritativePhysicsRoot != null ? _authoritativePhysicsRoot.position : Vector3.zero;
            _initialAuthoritativePhysicsRotation = _authoritativePhysicsRoot != null ? _authoritativePhysicsRoot.rotation : Quaternion.identity;
            _hasInitialRobotPose = true;
            TiagoExperimentTelemetry.LogEvent(
                "robot_initial_pose_captured",
                new Dictionary<string, object>
                {
                    ["robot"] = _robotPoseReference.name,
                    ["robot_pose_reference_path"] = GetPath(_robotPoseReference),
                    ["robot_pose_reference_root"] = GetRootName(_robotPoseReference),
                    ["position"] = _initialRobotPosition,
                    ["root_position"] = _initialRobotRootPosition,
                    ["authoritative_physics_root"] = GetPath(_authoritativePhysicsRoot),
                    ["authoritative_physics_root_position"] = _initialAuthoritativePhysicsPosition,
                    ["yaw_deg"] = _initialRobotRotation.eulerAngles.y,
                    ["root_yaw_deg"] = _initialRobotRootRotation.eulerAngles.y,
                    ["authoritative_physics_root_yaw_deg"] = _initialAuthoritativePhysicsRotation.eulerAngles.y,
                    ["frame_count"] = Time.frameCount,
                    ["time_since_start"] = Time.time
                });
        }

        private void ApplyRobotPoseResetForTrial(ExperimentRuntimeContext context)
        {
            CaptureInitialRobotPoseIfNeeded();
            _robotPoseReference = ResolveRobotPoseReference();
            if (!_hasInitialRobotPose || _robotPoseReference == null)
            {
                return;
            }

            Vector3 beforePosition = _robotPoseReference.position;
            Quaternion beforeRotation = _robotPoseReference.rotation;
            Dictionary<string, object> started = BuildContextPayload(context, "robot_pose_reset_started");
            started["robot_position_before"] = beforePosition;
            started["robot_yaw_before_deg"] = beforeRotation.eulerAngles.y;
            started["robot_position_target"] = _initialRobotPosition;
            started["robot_yaw_target_deg"] = _initialRobotRotation.eulerAngles.y;
            started["robot_pose_reference"] = GetPath(_robotPoseReference);
            AppendRobotPoseDiagnostics(started, "before_reset");
            LogEvent("robot_pose_reset_started", started);
            LogEvent("robot_pose_reset_diagnostics", started);

            _robotAdapter?.PrepareForExperimentalPoseReset();
            ApplyAuthoritativeRobotTeleport();

            Dictionary<string, object> applied = BuildContextPayload(context, "robot_pose_reset_applied");
            applied["robot_position_before"] = beforePosition;
            applied["robot_yaw_before_deg"] = beforeRotation.eulerAngles.y;
            applied["robot_position_target"] = _initialRobotPosition;
            applied["robot_yaw_target_deg"] = _initialRobotRotation.eulerAngles.y;
            applied["robot_position_after"] = _robotPoseReference.position;
            applied["robot_yaw_after_deg"] = _robotPoseReference.rotation.eulerAngles.y;
            applied["robot_pose_reference"] = GetPath(_robotPoseReference);
            applied["robot_pose_reference_root"] = GetRootName(_robotPoseReference);
            AppendRobotPoseDiagnostics(applied, "after_apply");
            LogEvent("robot_pose_reset_applied", applied);
            LogEvent("robot_pose_reset_diagnostics", applied);
        }

        private bool VerifyRobotPoseReset(ExperimentRuntimeContext context, out string failureReason)
        {
            failureReason = string.Empty;
            _robotPoseReference = ResolveRobotPoseReference();
            if (!_hasInitialRobotPose || _robotPoseReference == null)
            {
                return true;
            }

            Vector3 current = _robotPoseReference.position;
            float distance = Vector3.Distance(Flatten(current), Flatten(_initialRobotPosition));
            float yawDelta = Mathf.Abs(Mathf.DeltaAngle(_robotPoseReference.eulerAngles.y, _initialRobotRotation.eulerAngles.y));
            bool passed = distance <= Mathf.Max(0.01f, _robotPoseResetToleranceMeters) &&
                yawDelta <= Mathf.Max(0.1f, _robotPoseResetYawToleranceDegrees);

            Dictionary<string, object> payload = BuildContextPayload(context, passed ? "robot_pose_reset_completed" : "robot_pose_reset_failed");
            payload["robot_pose_reference"] = GetPath(_robotPoseReference);
            payload["robot_pose_reference_root"] = GetRootName(_robotPoseReference);
            payload["robot_pose_reference_source"] = ResolveRobotPoseReferenceSource();
            payload["robot_position_target"] = _initialRobotPosition;
            payload["robot_yaw_target_deg"] = _initialRobotRotation.eulerAngles.y;
            payload["robot_position_observed"] = current;
            payload["robot_yaw_observed_deg"] = _robotPoseReference.eulerAngles.y;
            payload["robot_pose_reset_distance_m"] = distance;
            payload["robot_pose_reset_yaw_delta_deg"] = yawDelta;
            payload["robot_pose_reset_tolerance_m"] = _robotPoseResetToleranceMeters;
            payload["robot_pose_reset_yaw_tolerance_deg"] = _robotPoseResetYawToleranceDegrees;
            AppendRobotRuntimeState(payload);
            AppendRobotPoseDiagnostics(payload, passed ? "verified_after_fixed_update" : "failed_after_fixed_update");
            LogEvent(passed ? "robot_pose_reset_completed" : "robot_pose_reset_failed", payload);
            LogEvent("robot_pose_reset_diagnostics", payload);

            if (!passed)
            {
                failureReason = "robot_pose_reset_failed";
            }

            return passed;
        }

        private Transform ResolveRobotPoseReference()
        {
            if (TiagoExperimentLogger.Active != null && TiagoExperimentLogger.Active.RobotReference != null)
            {
                return TiagoExperimentLogger.Active.RobotReference;
            }

            return _robotAdapter != null ? _robotAdapter.NavigationReference : null;
        }

        private string ResolveRobotPoseReferenceSource()
        {
            if (TiagoExperimentLogger.Active != null && TiagoExperimentLogger.Active.RobotReference != null)
            {
                return "tiago_experiment_logger_robot_reference";
            }

            return _robotAdapter != null && _robotAdapter.NavigationReference != null
                ? "autonomous_robot_adapter_navigation_reference"
                : "missing";
        }

        private void ApplyAuthoritativeRobotTeleport()
        {
            Transform poseReference = _robotPoseReference != null ? _robotPoseReference : ResolveRobotPoseReference();
            Transform root = poseReference != null && poseReference.root != null ? poseReference.root : poseReference;
            Transform physicsRoot = _authoritativePhysicsRoot != null ? _authoritativePhysicsRoot : ResolveAuthoritativePhysicsRoot(poseReference);

            NavMeshAgent agent = ResolveNavMeshAgent(poseReference);
            if (agent != null && agent.enabled)
            {
                if (agent.isOnNavMesh)
                {
                    agent.ResetPath();
                }

                agent.Warp(_initialRobotPosition);
            }

            Rigidbody rigidbody = ResolveRigidbody(poseReference);
            if (rigidbody != null)
            {
                rigidbody.linearVelocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
                rigidbody.position = _initialAuthoritativePhysicsPosition;
                rigidbody.rotation = _initialAuthoritativePhysicsRotation;
                rigidbody.MovePosition(_initialAuthoritativePhysicsPosition);
                rigidbody.MoveRotation(_initialAuthoritativePhysicsRotation);
                rigidbody.Sleep();
            }

            ArticulationBody articulationRoot = ResolveArticulationRoot(poseReference);
            if (articulationRoot != null)
            {
                articulationRoot.linearVelocity = Vector3.zero;
                articulationRoot.angularVelocity = Vector3.zero;
                TryTeleportArticulationRoot(articulationRoot, _initialAuthoritativePhysicsPosition, _initialAuthoritativePhysicsRotation);
            }

            if (physicsRoot != null)
            {
                physicsRoot.SetPositionAndRotation(_initialAuthoritativePhysicsPosition, _initialAuthoritativePhysicsRotation);
            }

            if (root != null && root != poseReference && root != physicsRoot)
            {
                root.SetPositionAndRotation(_initialRobotRootPosition, _initialRobotRootRotation);
            }

            if (poseReference != null)
            {
                poseReference.SetPositionAndRotation(_initialRobotPosition, _initialRobotRotation);
            }
        }

        private static bool TryTeleportArticulationRoot(ArticulationBody articulationRoot, Vector3 position, Quaternion rotation)
        {
            if (articulationRoot == null)
            {
                return false;
            }

            try
            {
                System.Reflection.MethodInfo method = typeof(ArticulationBody).GetMethod(
                    "TeleportRoot",
                    new[] { typeof(Vector3), typeof(Quaternion) });
                if (method != null)
                {
                    method.Invoke(articulationRoot, new object[] { position, rotation });
                    return true;
                }
            }
            catch (Exception)
            {
                // Transform write below is the fallback for Unity versions without a usable TeleportRoot path.
            }

            articulationRoot.transform.SetPositionAndRotation(position, rotation);
            return false;
        }

        private Transform ResolveAuthoritativePhysicsRoot(Transform poseReference)
        {
            ArticulationBody articulationRoot = ResolveArticulationRoot(poseReference);
            if (articulationRoot != null)
            {
                return articulationRoot.transform;
            }

            Rigidbody rigidbody = ResolveRigidbody(poseReference);
            if (rigidbody != null)
            {
                return rigidbody.transform;
            }

            NavMeshAgent agent = ResolveNavMeshAgent(poseReference);
            if (agent != null)
            {
                return agent.transform;
            }

            return poseReference != null && poseReference.root != null ? poseReference.root : poseReference;
        }

        private NavMeshAgent ResolveNavMeshAgent(Transform poseReference)
        {
            if (poseReference == null)
            {
                return null;
            }

            return poseReference.GetComponentInParent<NavMeshAgent>() ??
                poseReference.GetComponentInChildren<NavMeshAgent>(true) ??
                (_robotAdapter != null ? _robotAdapter.GetComponentInChildren<NavMeshAgent>(true) : null);
        }

        private static Rigidbody ResolveRigidbody(Transform poseReference)
        {
            if (poseReference == null)
            {
                return null;
            }

            return poseReference.GetComponentInParent<Rigidbody>() ??
                poseReference.GetComponentInChildren<Rigidbody>(true);
        }

        private static ArticulationBody ResolveArticulationRoot(Transform poseReference)
        {
            if (poseReference == null)
            {
                return null;
            }

            ArticulationBody[] bodies = poseReference.GetComponentsInParent<ArticulationBody>(true);
            foreach (ArticulationBody body in bodies)
            {
                if (body != null && body.isRoot)
                {
                    return body;
                }
            }

            bodies = poseReference.GetComponentsInChildren<ArticulationBody>(true);
            foreach (ArticulationBody body in bodies)
            {
                if (body != null && body.isRoot)
                {
                    return body;
                }
            }

            return bodies.Length > 0 ? bodies[0] : null;
        }

        private void AppendRobotPoseDiagnostics(Dictionary<string, object> payload, string phase)
        {
            Transform poseReference = _robotPoseReference != null ? _robotPoseReference : ResolveRobotPoseReference();
            Transform navReference = _robotAdapter != null ? _robotAdapter.NavigationReference : null;
            Transform adapterRoot = _robotAdapter != null ? _robotAdapter.transform : null;
            Transform topRoot = poseReference != null && poseReference.root != null ? poseReference.root : poseReference;
            Transform baseFootprint = FindTransformByName(topRoot, "base_footprint");
            Transform baseLink = FindTransformByName(topRoot, "base_link");
            NavMeshAgent agent = ResolveNavMeshAgent(poseReference);
            Rigidbody rigidbody = ResolveRigidbody(poseReference);
            ArticulationBody articulationRoot = ResolveArticulationRoot(poseReference);

            payload["robot_pose_reset_diagnostics_phase"] = phase ?? string.Empty;
            payload["diag_robot_reference"] = DescribeTransform(poseReference);
            payload["diag_navigation_reference"] = DescribeTransform(navReference);
            payload["diag_adapter_root"] = DescribeTransform(adapterRoot);
            payload["diag_top_robot_root"] = DescribeTransform(topRoot);
            payload["diag_base_footprint"] = DescribeTransform(baseFootprint);
            payload["diag_base_link"] = DescribeTransform(baseLink);
            payload["diag_navmesh_agent"] = DescribeNavMeshAgent(agent);
            payload["diag_rigidbody"] = DescribeRigidbody(rigidbody);
            payload["diag_articulation_root"] = DescribeArticulationBody(articulationRoot);
            payload["diag_samples_source"] = DescribeTransform(TiagoExperimentLogger.Active != null ? TiagoExperimentLogger.Active.RobotReference : null);
        }

        private void AppendRobotRuntimeState(Dictionary<string, object> payload)
        {
            payload["robot_held_object_id"] = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty;
            payload["robot_mode"] = _robotAdapter != null && _robotAdapter.Blackboard != null
                ? _robotAdapter.Blackboard.CurrentMode.ToString()
                : string.Empty;

            if (_robotAdapter != null && _robotAdapter.Blackboard != null)
            {
                payload["robot_task_status"] = _robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out TaskStatus status)
                    ? status.ToString()
                    : string.Empty;
                payload["robot_current_target_id"] = _robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor currentTarget) && currentTarget != null
                    ? currentTarget.Id
                    : string.Empty;
                payload["robot_place_target_id"] = _robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor placeTarget) && placeTarget != null
                    ? placeTarget.Id
                    : string.Empty;
                payload["robot_has_active_target"] = currentTarget != null || placeTarget != null;
            }
            else
            {
                payload["robot_task_status"] = string.Empty;
                payload["robot_current_target_id"] = string.Empty;
                payload["robot_place_target_id"] = string.Empty;
                payload["robot_has_active_target"] = false;
            }
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0f;
            return value;
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            string path = transform.name;
            Transform current = transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private static string GetRootName(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            return transform.root != null ? transform.root.name : transform.name;
        }

        private static Transform FindTransformByName(Transform root, string transformName)
        {
            if (root == null || string.IsNullOrWhiteSpace(transformName))
            {
                return null;
            }

            if (string.Equals(root.name, transformName, StringComparison.Ordinal))
            {
                return root;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindTransformByName(root.GetChild(i), transformName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static Dictionary<string, object> DescribeTransform(Transform transform)
        {
            return new Dictionary<string, object>
            {
                ["exists"] = transform != null,
                ["name"] = transform != null ? transform.name : string.Empty,
                ["path"] = GetPath(transform),
                ["position"] = transform != null ? transform.position : Vector3.zero,
                ["yaw_deg"] = transform != null ? transform.eulerAngles.y : 0f
            };
        }

        private static Dictionary<string, object> DescribeNavMeshAgent(NavMeshAgent agent)
        {
            return new Dictionary<string, object>
            {
                ["exists"] = agent != null,
                ["enabled"] = agent != null && agent.enabled,
                ["path_pending"] = agent != null && agent.enabled && agent.pathPending,
                ["has_path"] = agent != null && agent.enabled && agent.hasPath,
                ["is_on_navmesh"] = agent != null && agent.enabled && agent.isOnNavMesh,
                ["transform"] = agent != null ? GetPath(agent.transform) : string.Empty,
                ["position"] = agent != null ? agent.transform.position : Vector3.zero,
                ["next_position"] = agent != null ? agent.nextPosition : Vector3.zero
            };
        }

        private static Dictionary<string, object> DescribeRigidbody(Rigidbody rigidbody)
        {
            return new Dictionary<string, object>
            {
                ["exists"] = rigidbody != null,
                ["name"] = rigidbody != null ? rigidbody.name : string.Empty,
                ["path"] = rigidbody != null ? GetPath(rigidbody.transform) : string.Empty,
                ["is_kinematic"] = rigidbody != null && rigidbody.isKinematic,
                ["detect_collisions"] = rigidbody != null && rigidbody.detectCollisions,
                ["position"] = rigidbody != null ? rigidbody.position : Vector3.zero,
                ["yaw_deg"] = rigidbody != null ? rigidbody.rotation.eulerAngles.y : 0f,
                ["linear_velocity"] = rigidbody != null ? rigidbody.linearVelocity : Vector3.zero,
                ["angular_velocity"] = rigidbody != null ? rigidbody.angularVelocity : Vector3.zero
            };
        }

        private static Dictionary<string, object> DescribeArticulationBody(ArticulationBody body)
        {
            return new Dictionary<string, object>
            {
                ["exists"] = body != null,
                ["name"] = body != null ? body.name : string.Empty,
                ["path"] = body != null ? GetPath(body.transform) : string.Empty,
                ["is_root"] = body != null && body.isRoot,
                ["immovable"] = body != null && body.immovable,
                ["position"] = body != null ? body.transform.position : Vector3.zero,
                ["yaw_deg"] = body != null ? body.transform.eulerAngles.y : 0f,
                ["linear_velocity"] = body != null ? body.linearVelocity : Vector3.zero,
                ["angular_velocity"] = body != null ? body.angularVelocity : Vector3.zero
            };
        }

        private Dictionary<string, object> BuildBasePayload()
        {
            var payload = new Dictionary<string, object>
            {
                ["participant_id"] = _participantId ?? string.Empty,
                ["session_id"] = _sessionId ?? string.Empty,
                ["questionnaire_code"] = ExperimentDataPathResolver.CurrentQuestionnaireCode,
                ["questionnaire_code_scheme"] = ExperimentDataPathResolver.CurrentQuestionnaireCodeScheme,
                ["condition_order_ids"] = _runtimeConditionOrderIds.ToArray(),
                ["task_id"] = string.IsNullOrWhiteSpace(_taskId) ? DefaultTaskId : _taskId,
                ["input_mode"] = _inputMode ?? string.Empty,
                ["experiment_run_mode"] = _runMode.ToString(),
                ["trial_active"] = _trialActive,
                ["round_active"] = _roundManager != null && _roundManager.RoundActive,
                ["scene"] = SceneManager.GetActiveScene().name,
                ["orchestrator"] = name,
                ["frame_count"] = Time.frameCount,
                ["time_since_start"] = Time.time
            };
            payload["rounds_per_condition"] = RoundsPerCondition;
            payload["total_planned_trials"] = GetPlannedTrialCount();
            payload["next_trial_index"] = _nextTrialIndex;
            return payload;
        }

        private void LogStartNextTrialDiagnostic(string eventType, string reason, string methodPhase, int invocationId)
        {
            LogEvent(eventType, BuildStartNextTrialDiagnosticPayload(reason, methodPhase, invocationId));
        }

        private void AddStartNextTrialDiagnostics(Dictionary<string, object> payload, string reason)
        {
            if (payload == null)
            {
                return;
            }

            Dictionary<string, object> diagnosticPayload = BuildStartNextTrialDiagnosticPayload(reason, "embedded", _activeStartNextTrialInvocationId);
            foreach (KeyValuePair<string, object> pair in diagnosticPayload)
            {
                payload[pair.Key] = pair.Value;
            }
        }

        private Dictionary<string, object> BuildStartNextTrialDiagnosticPayload(string returnReason, string methodPhase, int invocationId)
        {
            Dictionary<string, object> payload = BuildTrialPayload(returnReason);
            CollectActiveRoundBoxCounts(out int roundTotalBoxes, out int roundCompletedBoxes);
            CollectAssistanceCounts(out int assistedPendingCount, out int assistedAssignedCount, out int assistedCompletedCount);

            int assignedCount = _assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox
                ? Math.Max(1, assistedAssignedCount)
                : assistedAssignedCount;
            int completedCount = Math.Max(roundCompletedBoxes, assistedCompletedCount);
            int totalBoxes = Math.Max(roundTotalBoxes, completedCount + assignedCount + assistedPendingCount);
            int pendingCount = assistedPendingCount > 0
                ? assistedPendingCount
                : Math.Max(0, totalBoxes - completedCount - assignedCount);

            bool roundCompleted = IsCurrentRoundCompleted() || (_assistanceCoordinator != null && _assistanceCoordinator.AssistanceRoundCompleted);
            bool instrumentationTerminal = IsInstrumentationCurrentTrialTerminal();
            bool robotBusy = IsRobotRuntimeBusy();
            bool hasAssignedTarget = HasAssignedTargetForDiagnostics();
            bool hasPendingVoiceCommand = HasPendingVoiceCommandForDiagnostics();
            ExperimentTrialPlanEntry nextEntry = PeekNextPlanEntry();
            int activeOrchestratorCount = CountActiveOrchestrators(out int totalOrchestratorCount, out string orchestratorPaths);

            payload["return_reason"] = returnReason ?? string.Empty;
            payload["method_phase"] = methodPhase ?? string.Empty;
            payload["method_name"] = nameof(StartNextTrial);
            payload["diagnostic_version"] = "P44D";
            payload["startNextTrialInvocationId"] = invocationId;
            payload["startNextTrialDepth"] = _startNextTrialDepth;
            payload["isStartNextTrialRunning"] = _isStartNextTrialRunning;
            payload["completionDuringStartNextTrialDetected"] = _completionDuringStartNextTrialDetected;
            payload["_trialActive"] = _trialActive;
            payload["_roundActive"] = _roundManager != null && _roundManager.RoundActive;
            payload["_sessionStarted"] = _sessionActive;
            payload["_nextTrialIndex"] = _nextTrialIndex;
            payload["_currentTrialIndex"] = _currentTrialIndex;
            payload["_currentTrialId"] = _currentTrialId ?? string.Empty;
            payload["_currentConditionId"] = ResolveCurrentConditionIdForDiagnostics();
            payload["planCount"] = _sessionTrialPlan != null ? _sessionTrialPlan.Count : 0;
            payload["nextConditionId"] = nextEntry != null && nextEntry.Condition != null ? nextEntry.Condition.ConditionId : string.Empty;
            payload["roundCompleted"] = roundCompleted;
            payload["instrumentationLastTerminalTrialId"] = _instrumentation != null ? _instrumentation.LastTerminalTrialId : string.Empty;
            payload["instrumentationLastTerminalTrialIndex"] = _instrumentation != null ? _instrumentation.LastTerminalTrialIndex : 0;
            payload["instrumentationLastTerminalState"] = _instrumentation != null ? _instrumentation.LastTerminalState : string.Empty;
            payload["assistedCompletedCount"] = assistedCompletedCount;
            payload["assistedPendingCount"] = assistedPendingCount;
            payload["assistedAssignedCount"] = assistedAssignedCount;
            payload["roundTotalBoxes"] = roundTotalBoxes;
            payload["roundCompletedBoxes"] = roundCompletedBoxes;
            payload["robotBusy"] = robotBusy;
            payload["heldObjectId"] = _robotAdapter != null ? _robotAdapter.HeldObjectId : string.Empty;
            payload["hasAssignedTarget"] = hasAssignedTarget;
            payload["hasPendingVoiceCommand"] = hasPendingVoiceCommand;
            payload["Time.frameCount"] = Time.frameCount;
            payload["Time.time"] = Time.time;

            payload["currentTrialId"] = _currentTrialId ?? string.Empty;
            payload["currentTrialIndex"] = _currentTrialIndex;
            payload["currentPlanEntryIndex"] = _currentPlanEntryIndex;
            payload["nextPlanEntryIndex"] = _nextPlanEntryIndex;
            payload["nextTrialIndex"] = _nextTrialIndex;
            payload["currentConditionId"] = ResolveCurrentConditionIdForDiagnostics();
            payload["completedCount"] = completedCount;
            payload["pendingCount"] = pendingCount;
            payload["assignedCount"] = assignedCount;
            payload["totalBoxes"] = totalBoxes;
            payload["instrumentationTrialCompleted"] = instrumentationTerminal;
            payload["instrumentationTrialActive"] = _instrumentation != null && _instrumentation.TrialActive;
            payload["instrumentationLastTerminalEventType"] = _instrumentation != null ? _instrumentation.LastTerminalEventType : string.Empty;
            payload["robotRuntimeBusy"] = robotBusy;
            payload["reason"] = returnReason ?? string.Empty;

            payload["Application.isPlaying"] = Application.isPlaying;
            payload["gameObject.GetInstanceID()"] = gameObject != null ? gameObject.GetInstanceID() : 0;
            payload["gameObject.name"] = gameObject != null ? gameObject.name : string.Empty;
            payload["scene_name"] = SceneManager.GetActiveScene().name;
            payload["component_enabled"] = enabled;
            payload["component_active_in_hierarchy"] = gameObject != null && gameObject.activeInHierarchy;
            payload["active_orchestrator_count"] = activeOrchestratorCount;
            payload["total_orchestrator_count"] = totalOrchestratorCount;
            payload["orchestrator_paths"] = orchestratorPaths;
            payload["active_condition_plan"] = BuildActiveConditionPlanSummary();
            payload["round_manager_instance_id"] = _roundManager != null ? _roundManager.GetInstanceID() : 0;
            payload["round_manager_name"] = _roundManager != null ? _roundManager.name : string.Empty;
            payload["round_manager_path"] = _roundManager != null ? GetPath(_roundManager.transform) : string.Empty;
            payload["assistance_round_initialized"] = _assistanceCoordinator != null && _assistanceCoordinator.RoundInitialized;
            payload["assistance_round_completed"] = _assistanceCoordinator != null && _assistanceCoordinator.AssistanceRoundCompleted;
            payload["assistance_explicit_intent_processing"] = _assistanceCoordinator != null && _assistanceCoordinator.ExplicitIntentProcessing;
            AppendRobotRuntimeState(payload);
            return payload;
        }

        private void LogStartNextTrialReturning(int invocationId, string reason)
        {
            LogStartNextTrialDiagnostic("experiment_start_next_trial_returning", reason, "returning", invocationId);
        }

        private void LogStartNextTrialException(int invocationId, Exception exception)
        {
            Dictionary<string, object> payload = BuildStartNextTrialDiagnosticPayload("exception", "exception", invocationId);
            payload["exception_type"] = exception != null ? exception.GetType().FullName : string.Empty;
            payload["exception_message"] = exception != null ? exception.Message : string.Empty;
            payload["exception_stack_trace"] = exception != null ? exception.StackTrace : string.Empty;
            LogEvent("experiment_start_next_trial_exception", payload);
        }

        private bool EvaluateCanAdvanceTrialWithoutMutation(out string reason, out ExperimentTrialAdvanceSnapshot snapshot)
        {
            CollectActiveRoundBoxCounts(out int totalBoxes, out int completedCount);
            CollectAssistanceCounts(out int assistedPendingCount, out int assistedAssignedCount, out int assistedCompletedCount);
            int assignedCount = _assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox
                ? Math.Max(1, assistedAssignedCount)
                : assistedAssignedCount;
            int mergedCompletedCount = Math.Max(completedCount, assistedCompletedCount);
            int mergedTotalBoxes = Math.Max(totalBoxes, mergedCompletedCount + assignedCount + assistedPendingCount);
            int pendingCount = assistedPendingCount > 0
                ? assistedPendingCount
                : Math.Max(0, mergedTotalBoxes - mergedCompletedCount - assignedCount);

            snapshot = new ExperimentTrialAdvanceSnapshot(
                _trialActive,
                _instrumentation != null && _instrumentation.TrialActive,
                IsInstrumentationCurrentTrialTerminal(),
                IsCurrentRoundCompleted() || (_assistanceCoordinator != null && _assistanceCoordinator.AssistanceRoundCompleted),
                IsRobotRuntimeBusy(),
                mergedTotalBoxes,
                mergedCompletedCount,
                pendingCount,
                assignedCount,
                _currentTrialId,
                _instrumentation != null ? _instrumentation.LastTerminalTrialId : string.Empty);
            return ExperimentTrialAdvanceReconciler.CanFinalizeCompletedTrialForAdvance(snapshot, out reason);
        }

        private void LogStateContradictionsIfAny(string methodPhase, int invocationId, string reason)
        {
            var contradictions = new List<string>();
            CollectActiveRoundBoxCounts(out int totalBoxes, out int completedCount);
            CollectAssistanceCounts(out int assistedPendingCount, out int assistedAssignedCount, out int assistedCompletedCount);
            int assignedCount = _assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox
                ? Math.Max(1, assistedAssignedCount)
                : assistedAssignedCount;
            int mergedCompletedCount = Math.Max(completedCount, assistedCompletedCount);
            int mergedTotalBoxes = Math.Max(totalBoxes, mergedCompletedCount + assignedCount + assistedPendingCount);
            int pendingCount = assistedPendingCount > 0
                ? assistedPendingCount
                : Math.Max(0, mergedTotalBoxes - mergedCompletedCount - assignedCount);

            if (_trialActive && IsInstrumentationCurrentTrialTerminal())
            {
                contradictions.Add("instrumentation_terminal_but_orchestrator_trial_active");
            }

            if (_trialActive && (IsCurrentRoundCompleted() || (_assistanceCoordinator != null && _assistanceCoordinator.AssistanceRoundCompleted)))
            {
                contradictions.Add("round_completed_but_orchestrator_trial_active");
            }

            if (_trialActive && mergedTotalBoxes > 0 && mergedCompletedCount >= mergedTotalBoxes && pendingCount == 0 && assignedCount == 0)
            {
                contradictions.Add("all_boxes_completed_but_orchestrator_trial_active");
            }

            if (_trialActive &&
                _assistanceCoordinator != null &&
                !_assistanceCoordinator.RoundInitialized &&
                !string.IsNullOrWhiteSpace(reason) &&
                reason.IndexOf("trial_not_started", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                contradictions.Add("assistance_trial_not_started_but_orchestrator_trial_active");
            }

            if (contradictions.Count == 0)
            {
                return;
            }

            Dictionary<string, object> payload = BuildStartNextTrialDiagnosticPayload(reason, methodPhase, invocationId);
            payload["contradiction_count"] = contradictions.Count;
            payload["contradictions"] = string.Join("|", contradictions);
            LogEvent("experiment_state_contradiction_detected", payload);
        }

        private void CollectAssistanceCounts(out int pendingCount, out int assignedCount, out int completedCount)
        {
            if (_assistanceCoordinator == null)
            {
                pendingCount = 0;
                assignedCount = 0;
                completedCount = 0;
                return;
            }

            pendingCount = Mathf.Max(0, _assistanceCoordinator.PendingBoxCount);
            assignedCount = Mathf.Max(0, _assistanceCoordinator.AssignedBoxCount);
            completedCount = Mathf.Max(0, _assistanceCoordinator.CompletedBoxCount);
        }

        private ExperimentTrialPlanEntry PeekNextPlanEntry()
        {
            if (_sessionTrialPlan == null || _nextPlanEntryIndex < 0 || _nextPlanEntryIndex >= _sessionTrialPlan.Count)
            {
                return null;
            }

            return _sessionTrialPlan[_nextPlanEntryIndex];
        }

        private string ResolveCurrentConditionIdForDiagnostics()
        {
            if (_currentContext != null && !string.IsNullOrWhiteSpace(_currentContext.ConditionId))
            {
                return _currentContext.ConditionId;
            }

            if (_currentCondition != null && !string.IsNullOrWhiteSpace(_currentCondition.ConditionId))
            {
                return _currentCondition.ConditionId;
            }

            return _conditionConfig != null && _conditionConfig.CurrentCondition != null
                ? _conditionConfig.CurrentCondition.ConditionName ?? string.Empty
                : string.Empty;
        }

        private bool HasAssignedTargetForDiagnostics()
        {
            if (_assistanceCoordinator != null && _assistanceCoordinator.HasAssignedBox)
            {
                return true;
            }

            if (_robotAdapter == null || _robotAdapter.Blackboard == null)
            {
                return false;
            }

            bool hasCurrentTarget = _robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out TargetDescriptor currentTarget) && currentTarget != null;
            bool hasPlaceTarget = _robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out TargetDescriptor placeTarget) && placeTarget != null;
            return hasCurrentTarget || hasPlaceTarget;
        }

        private bool HasPendingVoiceCommandForDiagnostics()
        {
            return (_voiceConnector != null && _voiceConnector.HasPendingConfirmation) ||
                (_robotAdapter != null && _robotAdapter.HasPendingP40BVoiceOrder);
        }

        private int CountActiveOrchestrators(out int totalCount, out string paths)
        {
            ExperimentSessionOrchestrator[] orchestrators = FindObjectsByType<ExperimentSessionOrchestrator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            totalCount = orchestrators != null ? orchestrators.Length : 0;
            int activeCount = 0;
            var descriptions = new List<string>();
            if (orchestrators != null)
            {
                foreach (ExperimentSessionOrchestrator orchestrator in orchestrators)
                {
                    if (orchestrator == null)
                    {
                        continue;
                    }

                    bool active = orchestrator.enabled && orchestrator.gameObject.activeInHierarchy;
                    if (active)
                    {
                        activeCount++;
                    }

                    descriptions.Add($"{GetPath(orchestrator.transform)}#{orchestrator.GetInstanceID()} active={active}");
                }
            }

            paths = string.Join(" | ", descriptions);
            return activeCount;
        }

        private string BuildActiveConditionPlanSummary()
        {
            var ids = new List<string>();
            if (_runtimeConditionOrderIds.Count > 0)
            {
                ids.AddRange(_runtimeConditionOrderIds);
            }
            else
            {
                EnsureDefaultConditions();
                if (_conditions != null)
                {
                    foreach (Experiment2x2ConditionDefinition condition in _conditions)
                    {
                        if (condition != null)
                        {
                            ids.Add(condition.ConditionId);
                        }
                    }
                }
            }

            return string.Join(",", ids);
        }

        private string BuildRuntimeConditionOrderSummary()
        {
            return _runtimeConditionOrderIds.Count > 0
                ? ExperimentCompensatedConditionOrder.FormatOrder(_runtimeConditionOrderIds)
                : ExperimentCompensatedConditionOrder.FormatOrder(BuildExpandedTrialPlan()
                    .ConvertAll(entry => entry.Condition != null ? entry.Condition.ConditionId : string.Empty)
                    .FindAll(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct()
                    .ToList());
        }

        private Dictionary<string, object> BuildRuntimeProtocolPayload(string reason)
        {
            Dictionary<string, object> payload = BuildBasePayload();
            ExperimentTrialPlanEntry currentEntry = _sessionTrialPlan != null &&
                _currentPlanEntryIndex >= 0 &&
                _currentPlanEntryIndex < _sessionTrialPlan.Count
                    ? _sessionTrialPlan[_currentPlanEntryIndex]
                    : null;
            ExperimentTrialPlanEntry nextEntry = PeekNextPlanEntry();
            payload["reason"] = reason ?? string.Empty;
            payload["runtime_protocol_ui"] = true;
            payload["participant_numeric_id"] = ExperimentCompensatedConditionOrder.ParseParticipantNumber(_participantId);
            payload["condition_order"] = BuildRuntimeConditionOrderSummary();
            payload["current_condition_id"] = CurrentConditionId;
            payload["current_condition_name"] = CurrentConditionName;
            payload["current_round_index_within_condition"] = _currentRoundIndexWithinCondition;
            payload["rounds_per_condition"] = RoundsPerCondition;
            payload["current_plan_entry_index"] = _currentPlanEntryIndex;
            payload["next_plan_entry_index"] = _nextPlanEntryIndex;
            payload["internal_trial_attempt_index"] = _internalTrialAttemptIndex;
            if (currentEntry != null)
            {
                payload["condition_order_index"] = currentEntry.ConditionOrderIndex;
                payload["visible_prueba_number"] = currentEntry.ConditionOrderIndex + 1;
            }
            payload["next_condition_order_index"] = nextEntry != null ? nextEntry.ConditionOrderIndex : -1;
            payload["next_visible_prueba_number"] = nextEntry != null ? nextEntry.ConditionOrderIndex + 1 : 0;
            payload["next_condition_id"] = nextEntry != null && nextEntry.Condition != null ? nextEntry.Condition.ConditionId : string.Empty;
            payload["next_condition_name"] = nextEntry != null && nextEntry.Condition != null ? nextEntry.Condition.ConditionName : string.Empty;
            return payload;
        }

        private string BuildReadableOrchestratorState(Dictionary<string, object> payload)
        {
            var lines = new List<string>
            {
                $"{LogPrefix} P44D orchestrator state dump",
                $"session={ReadPayload(payload, "_sessionStarted")} trialActive={ReadPayload(payload, "_trialActive")} roundActive={ReadPayload(payload, "_roundActive")}",
                $"trial={ReadPayload(payload, "_currentTrialId")} currentIndex={ReadPayload(payload, "_currentTrialIndex")} nextIndex={ReadPayload(payload, "_nextTrialIndex")}",
                $"condition={ReadPayload(payload, "_currentConditionId")} nextCondition={ReadPayload(payload, "nextConditionId")}",
                $"plan={ReadPayload(payload, "active_condition_plan")} planCount={ReadPayload(payload, "planCount")} nextPlanEntryIndex={ReadPayload(payload, "nextPlanEntryIndex")}",
                $"boxes completed={ReadPayload(payload, "completedCount")} pending={ReadPayload(payload, "pendingCount")} assigned={ReadPayload(payload, "assignedCount")} total={ReadPayload(payload, "totalBoxes")}",
                $"instrumentation terminalTrial={ReadPayload(payload, "instrumentationLastTerminalTrialId")} terminalIndex={ReadPayload(payload, "instrumentationLastTerminalTrialIndex")} terminalState={ReadPayload(payload, "instrumentationLastTerminalState")}",
                $"roundManager completed={ReadPayload(payload, "roundCompleted")} total={ReadPayload(payload, "roundTotalBoxes")} deposited={ReadPayload(payload, "roundCompletedBoxes")}",
                $"assistance initialized={ReadPayload(payload, "assistance_round_initialized")} completed={ReadPayload(payload, "assistance_round_completed")} completedCount={ReadPayload(payload, "assistedCompletedCount")} pending={ReadPayload(payload, "assistedPendingCount")} assigned={ReadPayload(payload, "assistedAssignedCount")}",
                $"robotBusy={ReadPayload(payload, "robotBusy")} held={ReadPayload(payload, "heldObjectId")} hasAssignedTarget={ReadPayload(payload, "hasAssignedTarget")} pendingVoice={ReadPayload(payload, "hasPendingVoiceCommand")}",
                $"orchestrators active={ReadPayload(payload, "active_orchestrator_count")} total={ReadPayload(payload, "total_orchestrator_count")} paths={ReadPayload(payload, "orchestrator_paths")}",
                $"phase={ReadPayload(payload, "method_phase")} reason={ReadPayload(payload, "return_reason")} canAdvance={ReadPayload(payload, "can_advance_trial")} probeReason={ReadPayload(payload, "advance_probe_reason")}"
            };
            return string.Join(Environment.NewLine, lines);
        }

        private static string ReadPayload(Dictionary<string, object> payload, string key)
        {
            if (payload == null || string.IsNullOrWhiteSpace(key) || !payload.TryGetValue(key, out object value) || value == null)
            {
                return string.Empty;
            }

            return value.ToString();
        }

        private static string GetPayloadString(Dictionary<string, object> payload, string key)
        {
            if (payload == null || string.IsNullOrWhiteSpace(key) || !payload.TryGetValue(key, out object value) || value == null)
            {
                return string.Empty;
            }

            return value.ToString();
        }

        private Dictionary<string, object> BuildTrialPayload(string reason)
        {
            Dictionary<string, object> payload = _currentContext != null
                ? BuildContextPayload(_currentContext, reason)
                : BuildConditionPayload(_currentCondition, _selectedConditionIndex, _currentTrialIndex, reason);
            payload["trial_id"] = _currentTrialId;
            payload["round_id"] = _currentRoundId;
            return payload;
        }

        private Dictionary<string, object> BuildConditionPayload(Experiment2x2ConditionDefinition condition, int conditionOrderIndex, int trialIndex, string reason)
        {
            Dictionary<string, object> payload = BuildBasePayload();
            if (condition != null)
            {
                foreach (KeyValuePair<string, object> pair in condition.ToPayload(conditionOrderIndex))
                {
                    payload[pair.Key] = pair.Value;
                }
            }

            payload["trial_index"] = trialIndex;
            payload["round_index"] = trialIndex;
            payload["round_index_within_condition"] = RoundsPerCondition == 1
                ? 1
                : ((Mathf.Max(1, trialIndex) - 1) % RoundsPerCondition) + 1;
            payload["rounds_per_condition"] = RoundsPerCondition;
            payload["global_round_index"] = trialIndex;
            payload["reason"] = reason ?? string.Empty;
            AddPlaceRecoveryConfig(payload);
            return payload;
        }

        public List<ExperimentTrialPlanEntry> BuildExpandedTrialPlan()
        {
            EnsureDefaultConditions();
            return ExperimentTrialPlanBuilder.Build(ResolveRuntimeConditionSequence(), RoundsPerCondition);
        }

        private bool TryConfigureRuntimeConditionOrder(IReadOnlyList<string> conditionOrderIds)
        {
            _runtimeConditionOrderIds.Clear();
            if (!ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(conditionOrderIds))
            {
                return false;
            }

            foreach (string conditionId in conditionOrderIds)
            {
                _runtimeConditionOrderIds.Add(conditionId);
            }

            return true;
        }

        private bool TryConfigureNewParticipationConditionOrder()
        {
            try
            {
                ExperimentConditionOrderAssignment assignment =
                    ExperimentConditionOrderAssignmentStore.GetOrCreate(_participantId);
                if (!ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(assignment.condition_order_ids))
                {
                    Debug.LogError(
                        $"{LogPrefix} condition_order_assignment_failed | participant_id={_participantId ?? string.Empty} reason=assigned_order_invalid");
                    return false;
                }

                if (!TryConfigureRuntimeConditionOrder(assignment.condition_order_ids))
                {
                    Debug.LogError(
                        $"{LogPrefix} condition_order_assignment_failed | participant_id={_participantId ?? string.Empty} reason=assigned_order_invalid");
                    return false;
                }

                Debug.Log(
                    $"{LogPrefix} condition_order_assignment_resolved | participant_id={_participantId ?? string.Empty} algorithm={ExperimentConditionOrderAssignmentStore.Algorithm} sequence_prefix={assignment.sequence_prefix} block_index={assignment.block_index} block_position={assignment.block_position} assignment_source={assignment.assignment_source} condition_order={ExperimentCompensatedConditionOrder.FormatOrder(assignment.condition_order_ids)}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"{LogPrefix} condition_order_assignment_failed | participant_id={_participantId ?? string.Empty} reason={ex.GetType().Name}:{ex.Message}");
                return false;
            }
        }

        private bool TryResolveExistingConditionOrderAssignment(
            string participantId,
            string missingReason,
            string unreadableReason,
            out ExperimentConditionOrderAssignment assignment,
            out string failureReason)
        {
            assignment = null;
            failureReason = string.Empty;
            try
            {
                if (!ExperimentConditionOrderAssignmentStore.TryGetExisting(participantId, out assignment))
                {
                    failureReason = missingReason;
                    return false;
                }
            }
            catch (Exception ex)
            {
                failureReason = unreadableReason;
                Debug.LogError(
                    $"{LogPrefix} condition_order_assignment_read_failed | participant_id={participantId ?? string.Empty} reason={ex.GetType().Name}:{ex.Message}");
                return false;
            }

            if (!ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(assignment.condition_order_ids))
            {
                assignment = null;
                failureReason = unreadableReason;
                return false;
            }

            return true;
        }

        private List<Experiment2x2ConditionDefinition> ResolveRuntimeConditionSequence()
        {
            EnsureDefaultConditions();
            if (_conditions == null || _conditions.Count == 0)
            {
                return new List<Experiment2x2ConditionDefinition>();
            }

            if (_runtimeConditionOrderIds.Count == 0)
            {
                return new List<Experiment2x2ConditionDefinition>(_conditions);
            }

            var ordered = new List<Experiment2x2ConditionDefinition>();
            foreach (string conditionId in _runtimeConditionOrderIds)
            {
                Experiment2x2ConditionDefinition condition = _conditions.Find(item =>
                    item != null && string.Equals(item.ConditionId, conditionId, StringComparison.Ordinal));
                if (condition != null)
                {
                    ordered.Add(condition);
                }
            }

            return ExperimentCompensatedConditionOrder.IsFinalWithinSubjectOrder(ordered.ConvertAll(item => item.ConditionId))
                ? ordered
                : new List<Experiment2x2ConditionDefinition>(_conditions);
        }

        private int GetPlannedTrialCount()
        {
            if (_sessionTrialPlan != null && _sessionTrialPlan.Count > 0)
            {
                return _sessionTrialPlan.Count;
            }

            int conditionCount = _conditions != null ? _conditions.Count : 0;
            return conditionCount * RoundsPerCondition;
        }

        private void ResetPlanForSession()
        {
            _sessionTrialPlan = BuildExpandedTrialPlan();
            _currentPlanEntryIndex = -1;
            _nextPlanEntryIndex = 0;
            _nextTrialIndex = _sessionTrialPlan.Count > 0 ? _sessionTrialPlan[0].TrialIndex : 1;
            _currentTrialIndex = 0;
            _currentTrialId = string.Empty;
            _currentRoundId = string.Empty;
            _currentRoundIndex = 1;
            _currentRoundIndexWithinCondition = 1;
            _currentGlobalRoundIndex = 1;
            _currentContext = null;
            _currentCondition = null;
            _trialActive = false;
            _currentTrialCompleted = false;
            _completedTrialId = string.Empty;
            _orchestratorCompletionLoggedTrialIds.Clear();
        }

        private void ResetPreSessionDiagnosticContext()
        {
            _currentTrialIndex = 0;
            _currentTrialId = string.Empty;
            _currentRoundId = string.Empty;
            _currentContext = null;
            _currentCondition = null;
            if (_conditionConfig != null)
            {
                _conditionConfig.SetRunMode(_runMode);
                _conditionConfig.ApplyConditionFromOrchestrator(
                    robotEnabled: false,
                    voiceEnabled: false,
                    conditionName: "uninitialized",
                    assistanceMode: RobotAssistanceMode.Disabled,
                    source: "new_session_context_reset");
            }
        }

        private bool SeekNextPlanEntryToConditionStart(string conditionId, out string reason)
        {
            reason = string.Empty;
            EnsureSessionPlanReady();
            if (string.IsNullOrWhiteSpace(conditionId))
            {
                reason = "condition_id_empty";
                return false;
            }

            for (int i = 0; i < _sessionTrialPlan.Count; i++)
            {
                ExperimentTrialPlanEntry entry = _sessionTrialPlan[i];
                if (entry == null || entry.Condition == null)
                {
                    continue;
                }

                if (!string.Equals(entry.Condition.ConditionId, conditionId, StringComparison.Ordinal))
                {
                    continue;
                }

                _currentPlanEntryIndex = -1;
                _nextPlanEntryIndex = i;
                _nextTrialIndex = entry.TrialIndex;
                _currentTrialIndex = 0;
                _currentTrialId = string.Empty;
                _currentRoundId = string.Empty;
                _currentRoundIndex = entry.RoundIndex;
                _currentRoundIndexWithinCondition = 1;
                _currentGlobalRoundIndex = entry.GlobalRoundIndex;
                _currentContext = null;
                _currentCondition = null;
                reason = "condition_start_found";
                return true;
            }

            reason = "condition_id_not_in_plan";
            return false;
        }

        private void ClearRuntimePlan()
        {
            _sessionTrialPlan = new List<ExperimentTrialPlanEntry>();
            _runtimeConditionOrderIds.Clear();
            _currentPlanEntryIndex = -1;
            _nextPlanEntryIndex = 0;
            _internalTrialAttemptIndex = 0;
            _nextTrialIndex = 1;
            _currentTrialIndex = 0;
            _currentTrialId = string.Empty;
            _currentRoundId = string.Empty;
            _currentContext = null;
            _currentCondition = null;
            _trialActive = false;
            _currentTrialCompleted = false;
            _completedTrialId = string.Empty;
            _orchestratorCompletionLoggedTrialIds.Clear();
        }

        private ExperimentTrialPlanEntry AdvanceToNextPlanEntry()
        {
            EnsureSessionPlanReady();
            if (_sessionTrialPlan.Count == 0 || _nextPlanEntryIndex < 0 || _nextPlanEntryIndex >= _sessionTrialPlan.Count)
            {
                return null;
            }

            _currentPlanEntryIndex = _nextPlanEntryIndex;
            _nextPlanEntryIndex++;
            _internalTrialAttemptIndex++;
            ExperimentTrialPlanEntry entry = _sessionTrialPlan[_currentPlanEntryIndex];
            _nextTrialIndex = _nextPlanEntryIndex < _sessionTrialPlan.Count
                ? _sessionTrialPlan[_nextPlanEntryIndex].TrialIndex
                : entry.TrialIndex + 1;
            ReflectSelectedCondition(entry);
            return entry;
        }

        private ExperimentTrialPlanEntry ResolveCurrentPlanEntry()
        {
            EnsureSessionPlanReady();
            if (_sessionTrialPlan.Count == 0 || _currentPlanEntryIndex < 0 || _currentPlanEntryIndex >= _sessionTrialPlan.Count)
            {
                return null;
            }

            ExperimentTrialPlanEntry entry = _sessionTrialPlan[_currentPlanEntryIndex];
            ReflectSelectedCondition(entry);
            return entry;
        }

        private void EnsureSessionPlanReady()
        {
            if (_sessionTrialPlan == null)
            {
                _sessionTrialPlan = new List<ExperimentTrialPlanEntry>();
            }

            if (_sessionTrialPlan.Count == 0)
            {
                _sessionTrialPlan = BuildExpandedTrialPlan();
            }
        }

        private void ReflectSelectedCondition(ExperimentTrialPlanEntry entry)
        {
            if (entry == null || _conditions == null || _conditions.Count == 0)
            {
                return;
            }

            _selectedConditionIndex = Mathf.Clamp(entry.ConditionOrderIndex, 0, _conditions.Count - 1);
        }

        private ExperimentRuntimeContext BuildContext(ExperimentTrialPlanEntry planEntry)
        {
            Experiment2x2ConditionDefinition condition = planEntry.Condition;
            int conditionOrderIndex = planEntry.ConditionOrderIndex;
            int trialIndex = planEntry.TrialIndex;
            string trialId = $"trial_{trialIndex:000}";
            string roundId = RoundsPerCondition == 1
                ? $"{_sessionId}_{trialId}_round"
                : $"{_sessionId}_trial_{trialIndex:000}_round_{planEntry.RoundIndexWithinCondition:00}";
            return new ExperimentRuntimeContext(
                _participantId,
                _sessionId,
                trialId,
                trialIndex,
                condition.ConditionId,
                condition.ConditionName,
                conditionOrderIndex,
                condition.RobotEnabled,
                condition.VoiceEnabled,
                condition.AssistanceMode,
                condition.SpawnGenerationMode,
                string.IsNullOrWhiteSpace(_taskId) ? DefaultTaskId : _taskId,
                _inputMode,
                roundId,
                roundIndex: planEntry.RoundIndex,
                roundIndexWithinCondition: planEntry.RoundIndexWithinCondition,
                roundsPerCondition: planEntry.RoundsPerCondition,
                globalRoundIndex: planEntry.GlobalRoundIndex,
                placeRecoveryConfig: ResolvePlaceRecoveryConfig(),
                placeFailureRecoveryMode: ResolvePlaceFailureRecoveryMode(),
                postPlaceEgressMode: ResolvePostPlaceEgressMode(),
                runMode: _runMode);
        }

        private Dictionary<string, object> BuildContextPayload(ExperimentRuntimeContext context, string reason)
        {
            Dictionary<string, object> payload = BuildBasePayload();
            if (context != null)
            {
                foreach (KeyValuePair<string, object> pair in context.ToPayload())
                {
                    payload[pair.Key] = pair.Value;
                }
            }

            payload["reason"] = reason ?? string.Empty;
            return payload;
        }

        private Dictionary<string, object> BuildConditionSequencePayload(ExperimentRuntimeContext context, string reason)
        {
            Dictionary<string, object> payload = BuildContextPayload(context, reason);
            ExperimentTrialPlanEntry nextEntry = _sessionTrialPlan != null && _nextPlanEntryIndex >= 0 && _nextPlanEntryIndex < _sessionTrialPlan.Count
                ? _sessionTrialPlan[_nextPlanEntryIndex]
                : null;

            payload["requested_condition_id"] = context != null ? context.ConditionId : string.Empty;
            payload["selected_condition_id"] = context != null ? context.ConditionId : string.Empty;
            payload["next_condition_id"] = nextEntry != null && nextEntry.Condition != null ? nextEntry.Condition.ConditionId : string.Empty;
            payload["condition_sequence_index"] = context != null ? context.ConditionOrderIndex : _selectedConditionIndex;
            payload["selected_condition_index"] = _selectedConditionIndex;
            payload["prueba_index"] = context != null ? ResolveRuntimeTestIndex(context) : 0;
            payload["runtime_test_label"] = context != null ? $"Prueba {ResolveRuntimeTestIndex(context)}" : string.Empty;
            payload["condition_semantics"] = context != null ? ConditionSemantics(context.ConditionId) : string.Empty;
            payload["order_index"] = context != null ? context.ConditionOrderIndex : -1;
            payload["compensated_order_id"] = BuildRuntimeConditionOrderSummary();
            payload["current_plan_entry_index"] = _currentPlanEntryIndex;
            payload["next_plan_entry_index"] = _nextPlanEntryIndex;
            payload["internal_trial_attempt_index"] = _internalTrialAttemptIndex;
            payload["visible_prueba_number"] = context != null ? context.ConditionOrderIndex + 1 : 0;
            payload["next_condition_index"] = nextEntry != null ? nextEntry.ConditionOrderIndex : -1;
            return payload;
        }

        private static int ResolveRuntimeTestIndex(ExperimentRuntimeContext context)
        {
            if (context == null)
            {
                return 0;
            }

            int rounds = Mathf.Max(1, context.RoundsPerCondition);
            return Mathf.Clamp(((Mathf.Max(1, context.GlobalRoundIndex) - 1) / rounds) + 1, 1, 3);
        }

        private static string ConditionSemantics(string conditionId)
        {
            return conditionId switch
            {
                ExperimentCompensatedConditionOrder.C00 => "robot_off_voice_off",
                ExperimentCompensatedConditionOrder.C10 => "robot_on_voice_off",
                ExperimentCompensatedConditionOrder.C11 => "robot_on_voice_on",
                _ => "unknown"
            };
        }

        private RobotAssistancePlaceRecoveryConfig ResolvePlaceRecoveryConfig()
        {
            return _assistanceCoordinator != null ? _assistanceCoordinator.PlaceRecoveryConfig : null;
        }

        private string ResolvePlaceFailureRecoveryMode()
        {
            return _robotAdapter != null ? _robotAdapter.PlaceFailureRecoveryModeName : string.Empty;
        }

        private string ResolvePostPlaceEgressMode()
        {
            return _assistanceCoordinator != null ? _assistanceCoordinator.PostPlaceEgressModeName : string.Empty;
        }

        private void AddPlaceRecoveryConfig(Dictionary<string, object> payload)
        {
            RobotAssistancePlaceRecoveryConfig config = ResolvePlaceRecoveryConfig();
            if (payload == null || config == null)
            {
                return;
            }

            foreach (KeyValuePair<string, object> pair in config.ToPayload())
            {
                payload[pair.Key] = pair.Value;
            }
        }

        private Dictionary<string, object> BuildFailurePayload(string reason)
        {
            Dictionary<string, object> payload = BuildTrialPayload(reason);
            payload["success"] = false;
            payload["failure_reason"] = reason ?? string.Empty;
            return payload;
        }

        private static void AddMissing(List<string> failures, UnityEngine.Object reference, string reason)
        {
            if (reference == null)
            {
                failures.Add(reason);
            }
        }

        private static void LogEvent(string eventType, Dictionary<string, object> payload)
        {
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            string path = transform.name;
            Transform current = transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private static string FormatTransformPose(Transform transform)
        {
            return transform == null
                ? string.Empty
                : $"position={transform.position} rotation={transform.eulerAngles}";
        }

        private bool HasBlackboardTarget(BlackboardKey<TargetDescriptor> key)
        {
            return _robotAdapter != null &&
                _robotAdapter.Blackboard != null &&
                _robotAdapter.Blackboard.TryGet(key, out TargetDescriptor target) &&
                target != null;
        }

        private string ReadBlackboardTargetId(BlackboardKey<TargetDescriptor> key)
        {
            return _robotAdapter != null &&
                _robotAdapter.Blackboard != null &&
                _robotAdapter.Blackboard.TryGet(key, out TargetDescriptor target) &&
                target != null
                    ? target.Id ?? string.Empty
                    : string.Empty;
        }

        private string ReadRobotTaskStatus()
        {
            return _robotAdapter != null &&
                _robotAdapter.Blackboard != null &&
                _robotAdapter.Blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out TaskStatus status)
                    ? status.ToString()
                    : string.Empty;
        }

        private int CountConditionProviders()
        {
            int count = 0;
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour is IExperimentConditionProvider)
                {
                    count++;
                }
            }

            return count;
        }

        private static string Sanitize(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "participant"
                : value.Replace(' ', '_').Replace('/', '_').Replace('\\', '_');
        }
    }

    public sealed class ExperimentRuntimeProtocolSnapshot
    {
        public ExperimentRuntimeProtocolSnapshot(
            bool sessionActive,
            bool trialActive,
            bool trialTransitionBusy,
            string participantId,
            string sessionId,
            string currentConditionId,
            string currentConditionName,
            string currentTrialId,
            string currentRoundId,
            int currentRoundIndexWithinCondition,
            int roundsPerCondition,
            int currentGlobalRoundIndex,
            int totalPlannedTrials,
            int currentPlanEntryIndex,
            int nextPlanEntryIndex,
            int planCount,
            string nextConditionId,
            string nextConditionName,
            int nextRoundIndexWithinCondition,
            bool hasNextTrial,
            bool canAdvance,
            string advanceReason,
            bool roundActive,
            bool roundCompleted,
            int totalBoxes,
            int completedBoxes,
            string conditionOrderSummary,
            IReadOnlyList<string> conditionOrderIds,
            int currentConditionOrderIndex,
            int nextConditionOrderIndex,
            int internalTrialAttemptIndex)
        {
            SessionActive = sessionActive;
            TrialActive = trialActive;
            TrialTransitionBusy = trialTransitionBusy;
            ParticipantId = participantId ?? string.Empty;
            SessionId = sessionId ?? string.Empty;
            CurrentConditionId = currentConditionId ?? string.Empty;
            CurrentConditionName = currentConditionName ?? string.Empty;
            CurrentTrialId = currentTrialId ?? string.Empty;
            CurrentRoundId = currentRoundId ?? string.Empty;
            CurrentRoundIndexWithinCondition = currentRoundIndexWithinCondition;
            RoundsPerCondition = roundsPerCondition;
            CurrentGlobalRoundIndex = currentGlobalRoundIndex;
            TotalPlannedTrials = totalPlannedTrials;
            CurrentPlanEntryIndex = currentPlanEntryIndex;
            NextPlanEntryIndex = nextPlanEntryIndex;
            PlanCount = planCount;
            NextConditionId = nextConditionId ?? string.Empty;
            NextConditionName = nextConditionName ?? string.Empty;
            NextRoundIndexWithinCondition = nextRoundIndexWithinCondition;
            HasNextTrial = hasNextTrial;
            CanAdvance = canAdvance;
            AdvanceReason = advanceReason ?? string.Empty;
            RoundActive = roundActive;
            RoundCompleted = roundCompleted;
            TotalBoxes = totalBoxes;
            CompletedBoxes = completedBoxes;
            ConditionOrderSummary = conditionOrderSummary ?? string.Empty;
            ConditionOrderIds = conditionOrderIds == null ? Array.Empty<string>() : conditionOrderIds.ToArray();
            CurrentConditionOrderIndex = currentConditionOrderIndex;
            NextConditionOrderIndex = nextConditionOrderIndex;
            InternalTrialAttemptIndex = internalTrialAttemptIndex;
        }

        public bool SessionActive { get; }
        public bool TrialActive { get; }
        public bool TrialTransitionBusy { get; }
        public string ParticipantId { get; }
        public string SessionId { get; }
        public string CurrentConditionId { get; }
        public string CurrentConditionName { get; }
        public string CurrentTrialId { get; }
        public string CurrentRoundId { get; }
        public int CurrentRoundIndexWithinCondition { get; }
        public int RoundsPerCondition { get; }
        public int CurrentGlobalRoundIndex { get; }
        public int TotalPlannedTrials { get; }
        public int CurrentPlanEntryIndex { get; }
        public int CurrentTrialIndex => CurrentPlanEntryIndex >= 0 ? CurrentPlanEntryIndex + 1 : 0;
        public int NextPlanEntryIndex { get; }
        public int NextTrialIndex => NextPlanEntryIndex >= 0 ? NextPlanEntryIndex + 1 : 0;
        public int PlanCount { get; }
        public string NextConditionId { get; }
        public string NextConditionName { get; }
        public int NextRoundIndexWithinCondition { get; }
        public bool HasNextTrial { get; }
        public bool CanAdvance { get; }
        public string AdvanceReason { get; }
        public bool RoundActive { get; }
        public bool RoundCompleted { get; }
        public int TotalBoxes { get; }
        public int CompletedBoxes { get; }
        public string ConditionOrderSummary { get; }
        public IReadOnlyList<string> ConditionOrderIds { get; }
        public int CurrentConditionOrderIndex { get; }
        public int NextConditionOrderIndex { get; }
        public int CurrentVisiblePruebaNumber => CurrentConditionOrderIndex >= 0 ? CurrentConditionOrderIndex + 1 : 0;
        public int NextVisiblePruebaNumber => NextConditionOrderIndex >= 0 ? NextConditionOrderIndex + 1 : 0;
        public int InternalTrialAttemptIndex { get; }
    }
}
