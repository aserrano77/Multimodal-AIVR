using Autonomy.Domain;
using Autonomy.Core;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class VoiceAutonomyCommandConnector : MonoBehaviour
    {
        private const string LogPrefix = "[VoiceAutonomyCommandConnector]";
        private const string P44IDebugCommandSource = "p44i_debug_injected_transcript";
        private const string P44IDebugSource = "p44i_debug";

        [Header("References")]
        [SerializeField] private MultimodalAutonomyCommandBridge _commandBridge;
        [SerializeField] private bool _autoFindBridgeInParents = true;
        [SerializeField] private bool _autoFindBridgeInScene = true;

        [Header("Minimal Voice Feedback")]
        [SerializeField] private bool _enableMinimalVoiceFeedback = true;
        [SerializeField] private MonoBehaviour _feedbackSinkComponent;

        [Header("Structured Robot Voice Feedback")]
        [SerializeField] private bool _enableStructuredRobotVoiceFeedback = true;
        [SerializeField] private MonoBehaviour _robotFeedbackSinkComponent;
        [SerializeField] private bool _useDebugRobotVoiceFeedbackLog = true;

        [Header("Voice Feedback Diagnostics")]
        [SerializeField] private bool _enableStructuredVoiceFeedbackDiagnostics;
        [SerializeField] private RobotVoiceFeedbackDiagnosticRecorder _robotVoiceFeedbackDiagnosticRecorder;

        [Header("Voice Experiment Logging")]
        [SerializeField] private bool _enableVoiceExperimentLogging = true;
        [SerializeField] private MonoBehaviour _voiceExperimentEventSinkComponent;

        [Header("Experiment Condition Gate")]
        [SerializeField] private MonoBehaviour _experimentConditionProviderComponent;
        [SerializeField] private MonoBehaviour _explicitIntentActivityComponent;

        [Header("Voice Confirmation")]
        [SerializeField] private VoiceConfirmationMode _confirmationMode = VoiceConfirmationMode.Disabled;
        [SerializeField] private float _confirmationTimeoutSeconds = 8f;
        [SerializeField] private float _confirmationLowConfidenceThreshold = 0.75f;

        private IVoiceCommandNormalizer _commandNormalizer;
        private IVoiceCommandIntentMapper _intentMapper;
        private VoiceAutonomyCommandRouter _router;
        private VoiceConfirmationMode _routerConfirmationMode;
        private float _routerConfirmationTimeoutSeconds;
        private float _routerConfirmationLowConfidenceThreshold;
        private bool _routerVoiceExperimentLoggingEnabled;
        private bool _lastBridgeSubmitInvoked;
        private string _lastBridgeSubmitReason;
        private string _lastBridgeSubmitFailureReason;
        private string _activeCommandSource = "voice_command";
        private bool _p40SubscriptionSnapshotLogged;

        private void Awake()
        {
            Initialize(new VoiceCommandNormalizer(), new VoiceCommandIntentMapper());
        }

        private void Update()
        {
            _router?.CheckPendingConfirmationTimeout();
        }

        public VoiceAutonomyCommandRoutingResult ProcessFinalTranscript(
            string transcript,
            string commandSource = "voice_command",
            string voiceInteractionId = "",
            bool forceExperimentPauseRejection = false)
        {
            EnsureInitialized();
            TryResolveBridge();
            EnsureRouterConfigurationCurrent();
            _activeCommandSource = string.IsNullOrWhiteSpace(commandSource) ? "voice_command" : commandSource;
            _lastBridgeSubmitInvoked = false;
            _lastBridgeSubmitReason = "not_invoked";
            LogP40SubscriptionSnapshotIfNeeded(voiceInteractionId);
            Debug.Log($"{LogPrefix} ProcessFinalTranscript | raw='{transcript}' | confirmation_mode_runtime={_router.ConfirmationMode} | confirmation_mode={_confirmationMode}", this);
            VoiceAutonomyCommandRoutingResult result = _router.RouteTranscript(transcript, voiceInteractionId, forceExperimentPauseRejection);
            bool requiresConfirmation = result.Status == VoiceAutonomyCommandRoutingStatus.PendingConfirmation;
            string intentKind = result.Mapping?.IntentKind.ToString() ?? string.Empty;
            Debug.Log($"{LogPrefix} RoutingResult | status={result.Status} | intent_kind={intentKind} | requires_confirmation={requiresConfirmation} | reason='{result.Reason}' | submitted={result.Submitted}", this);
            string bridgeReason = _lastBridgeSubmitInvoked
                ? _lastBridgeSubmitReason
                : (requiresConfirmation ? "suppressed_until_confirmation" : "not_invoked");
            Debug.Log($"{LogPrefix} BridgeSubmit | invoked={_lastBridgeSubmitInvoked} | reason={bridgeReason}", this);
            LogRuntimeStateDiagnostics(result, transcript, _activeCommandSource, string.IsNullOrWhiteSpace(voiceInteractionId) ? string.Empty : voiceInteractionId);
            _activeCommandSource = "voice_command";
            return result;
        }

        public bool HasPendingConfirmation => _router != null && _router.HasPendingConfirmation;

        [ContextMenu("P44I Debug/Inject Transcript: C1 SELF")]
        public void P44IInjectTranscriptC1Self()
        {
            InjectP44IDebugTranscript("C1_SELF", "lleva la caja C1 a su zona de deposito");
        }

        [ContextMenu("P44I Debug/Inject Transcript: B2 SELF")]
        public void P44IInjectTranscriptB2Self()
        {
            InjectP44IDebugTranscript("B2_SELF", "lleva la caja B2 a su zona de deposito");
        }

        [ContextMenu("P44I Debug/Inject Transcript: Stop")]
        public void P44IInjectTranscriptStop()
        {
            InjectP44IDebugTranscript("Stop", "para");
        }

        [ContextMenu("P44I Debug/Inject Transcript: Resume")]
        public void P44IInjectTranscriptResume()
        {
            InjectP44IDebugTranscript("Resume", "retoma la tarea");
        }

        [ContextMenu("P44I Debug/Dump Voice Stop Resume State")]
        public void P44IDumpVoiceStopResumeState()
        {
            Dictionary<string, object> payload = BuildP44IVoiceStopResumeStatePayload("context_menu");
            TiagoExperimentTelemetry.LogEvent("p44i_voice_stop_resume_state_dump", payload);
            Debug.Log(
                $"{LogPrefix} P44I state | condition={GetPayloadText(payload, "condition_id")} trial={GetPayloadText(payload, "trial_id")} active={GetPayloadText(payload, "active_target_id")} place={GetPayloadText(payload, "active_place_target_id")} held={GetPayloadText(payload, "held_object_id")} pending={GetPayloadText(payload, "pending_target_id")} paused={GetPayloadText(payload, "paused_pending_target_id")} latch={GetPayloadText(payload, "stop_latch_active")} snapshot={GetPayloadText(payload, "resume_snapshot_available")}",
                this);
        }

        private VoiceAutonomyCommandRoutingResult InjectP44IDebugTranscript(string label, string transcript)
        {
            string voiceInteractionId = $"p44i_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{label}";
            Dictionary<string, object> requestedPayload = BuildP44IBasePayload(label, transcript, voiceInteractionId);
            TiagoExperimentTelemetry.LogEvent("p44i_debug_transcript_injection_requested", requestedPayload);

            if (!Application.isPlaying)
            {
                requestedPayload["reason"] = "not_in_play_mode";
                TiagoExperimentTelemetry.LogEvent("p44i_debug_transcript_injection_skipped", requestedPayload);
                Debug.LogWarning($"{LogPrefix} P44I injection skipped outside Play Mode | label={label} transcript='{transcript}'", this);
                return null;
            }

            VoiceAutonomyCommandRoutingResult result = ProcessFinalTranscript(transcript, P44IDebugCommandSource, voiceInteractionId);
            Dictionary<string, object> resultPayload = BuildP44IBasePayload(label, transcript, voiceInteractionId);
            resultPayload["routing_status"] = result?.Status.ToString() ?? string.Empty;
            resultPayload["reason"] = result?.Reason ?? string.Empty;
            resultPayload["submitted"] = result?.Submitted ?? false;
            resultPayload["normalized_text"] = result?.Normalization?.NormalizedText ?? string.Empty;
            resultPayload["intent_kind"] = result?.Mapping?.IntentKind.ToString() ?? string.Empty;
            resultPayload["target_id"] = result?.SubmittedIntent?.TargetId ?? result?.PendingIntent?.TargetId ?? result?.Mapping?.TaskIntent?.TargetId ?? string.Empty;
            resultPayload["destination_id"] = result?.SubmittedIntent?.PlaceTargetId ?? result?.PendingIntent?.PlaceTargetId ?? result?.Mapping?.TaskIntent?.PlaceTargetId ?? string.Empty;
            TiagoExperimentTelemetry.LogEvent("p44i_debug_transcript_injection_result", resultPayload);
            P44IDumpVoiceStopResumeState();
            return result;
        }

        public void ResetForNewExperimentTrial(string reason)
        {
            EnsureInitialized();
            _router.ClearPendingConfirmation();
            _lastBridgeSubmitInvoked = false;
            _lastBridgeSubmitReason = "reset_for_new_experiment_trial";
            _lastBridgeSubmitFailureReason = string.Empty;
            _activeCommandSource = "voice_command";
            TiagoExperimentTelemetry.LogEvent(
                "voice_connector_reset_for_experiment_trial",
                new Dictionary<string, object>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["voice_connector"] = name
                });
        }

        internal void Initialize(
            IVoiceCommandNormalizer commandNormalizer,
            IVoiceCommandIntentMapper intentMapper)
        {
            _commandNormalizer = commandNormalizer;
            VoiceConfirmationPolicy policy = BuildConfirmationPolicy();
            IVoiceUserFeedbackSink feedbackSink = ResolveFeedbackSink();
            IRobotVoiceFeedbackSink robotFeedbackSink = ResolveRobotFeedbackSink();
            IVoiceExperimentEventSink experimentEventSink = ResolveExperimentEventSink();
            _intentMapper = new VoiceSceneTargetResolvingIntentMapper(
                intentMapper,
                BuildActiveRoundVoiceTargetCandidates,
                experimentEventSink);
            _router = new VoiceAutonomyCommandRouter(
                _commandNormalizer,
                _intentMapper,
                SubmitIntentToBridge,
                message => Debug.Log($"{LogPrefix} {message}", this),
                message => Debug.LogWarning($"{LogPrefix} {message}", this),
                policy,
                feedbackSink,
                robotFeedbackSink,
                experimentEventSink,
                () => Time.time,
                executionGate: EvaluateExecutionGate,
                resolveSubmitRejectionReason: () => _lastBridgeSubmitFailureReason,
                applyStopCommand: ApplyStopCommand,
                applyResumeCommand: ApplyResumeCommand,
                applyCancelPendingOrder: ApplyCancelPendingOrder,
                isVoiceStopLatched: IsVoiceStopLatched,
                isExperimentPaused: () => ExperimentRuntimePauseCoordinator.IsExperimentPaused);
            _routerConfirmationMode = policy.Mode;
            _routerConfirmationTimeoutSeconds = policy.TimeoutSeconds;
            _routerConfirmationLowConfidenceThreshold = policy.LowConfidenceThreshold;
            _routerVoiceExperimentLoggingEnabled = _enableVoiceExperimentLogging;
            TryResolveBridge();
            Debug.Log(
                $"{LogPrefix} Runtime config | enableMinimalVoiceFeedback={_enableMinimalVoiceFeedback} enableVoiceExperimentLogging={_enableVoiceExperimentLogging} confirmationMode={_confirmationMode} confirmationTimeoutSeconds={_confirmationTimeoutSeconds:0.###} confirmationLowConfidenceThreshold={_confirmationLowConfidenceThreshold:0.###} commandBridge assigned={_commandBridge != null} feedbackSink resolved={feedbackSink != null} experimentEventSink resolved={experimentEventSink != null} router initialized={_router != null} confirmationPolicy effective mode={policy.Mode}",
                this);
        }

        private void EnsureInitialized()
        {
            if (_router != null)
            {
                return;
            }

            Initialize(new VoiceCommandNormalizer(), new VoiceCommandIntentMapper());
        }

        private bool SubmitIntentToBridge(MultimodalTaskIntent intent)
        {
            _lastBridgeSubmitInvoked = true;
            if (_commandBridge == null)
            {
                _lastBridgeSubmitReason = "bridge_missing";
                _lastBridgeSubmitFailureReason = "bridge_missing";
                Debug.LogWarning($"{LogPrefix} bridge_not_assigned_or_found | voice intent was not submitted", this);
                return false;
            }

            IExplicitAutonomyIntentActivity activity = ResolveExplicitIntentActivity();
            activity?.NotifyExplicitIntentProcessingStarted(intent, _activeCommandSource);
            bool accepted = _commandBridge.SubmitIntent(intent);
            activity?.NotifyExplicitIntentProcessingFinished(intent, _activeCommandSource, accepted);
            _lastBridgeSubmitReason = accepted ? "bridge_accepted" : "bridge_rejected";
            _lastBridgeSubmitFailureReason = accepted ? string.Empty : _commandBridge.LastRejectionReason;
            return accepted;
        }

        private bool IsVoiceStopLatched()
        {
            TryResolveBridge();
            return _commandBridge != null &&
                   _commandBridge.RobotAdapter != null &&
                   _commandBridge.RobotAdapter.VoiceStopLatched;
        }

        private VoiceStopCommandResult ApplyStopCommand(
            VoiceCommandIntentMappingResult mapping,
            VoiceCommandNormalizationResult normalization)
        {
            TryResolveBridge();
            AutonomousRobotAdapter adapter = _commandBridge != null ? _commandBridge.RobotAdapter : null;
            if (adapter == null)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "voice_stop_command_rejected",
                    new Dictionary<string, object>
                    {
                        ["reason"] = "adapter_missing",
                        ["raw_transcript"] = normalization?.RawTranscript ?? string.Empty,
                        ["normalized_text"] = normalization?.NormalizedText ?? string.Empty,
                        ["intent_kind"] = mapping?.IntentKind.ToString() ?? string.Empty
                    });
                return VoiceStopCommandResult.Reject("adapter_missing");
            }

            return adapter.ApplyVoiceStopCommand(
                normalization?.RawTranscript ?? string.Empty,
                normalization?.NormalizedText ?? string.Empty,
                mapping?.IntentKind.ToString() ?? VoiceCommandIntentKind.Stop.ToString());
        }

        private VoiceResumeCommandResult ApplyResumeCommand(
            VoiceCommandIntentMappingResult mapping,
            VoiceCommandNormalizationResult normalization)
        {
            TryResolveBridge();
            AutonomousRobotAdapter adapter = _commandBridge != null ? _commandBridge.RobotAdapter : null;
            if (adapter == null)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "voice_resume_command_rejected",
                    new Dictionary<string, object>
                    {
                        ["reason"] = "adapter_missing",
                        ["raw_transcript"] = normalization?.RawTranscript ?? string.Empty,
                        ["normalized_text"] = normalization?.NormalizedText ?? string.Empty,
                        ["intent_kind"] = mapping?.IntentKind.ToString() ?? string.Empty
                    });
                return VoiceResumeCommandResult.Reject("adapter_missing");
            }

            return adapter.ApplyVoiceResumeCommand(
                normalization?.RawTranscript ?? string.Empty,
                normalization?.NormalizedText ?? string.Empty,
                mapping?.IntentKind.ToString() ?? VoiceCommandIntentKind.Resume.ToString());
        }

        private void ApplyCancelPendingOrder(
            VoiceCommandIntentMappingResult mapping,
            VoiceCommandNormalizationResult normalization)
        {
            TryResolveBridge();
            AutonomousRobotAdapter adapter = _commandBridge != null ? _commandBridge.RobotAdapter : null;
            if (adapter == null)
            {
                TiagoExperimentTelemetry.LogEvent(
                    "voice_cancel_pending_voice_order_not_found",
                    new Dictionary<string, object>
                    {
                        ["reason"] = "adapter_missing",
                        ["raw_transcript"] = normalization?.RawTranscript ?? string.Empty,
                        ["normalized_text"] = normalization?.NormalizedText ?? string.Empty,
                        ["intent_kind"] = mapping?.IntentKind.ToString() ?? string.Empty
                    });
                return;
            }

            adapter.CancelPendingVoiceOrder(
                normalization?.RawTranscript ?? string.Empty,
                normalization?.NormalizedText ?? string.Empty,
                mapping?.IntentKind.ToString() ?? VoiceCommandIntentKind.CancelPendingOrder.ToString());
        }

        private VoiceCommandExecutionGateResult EvaluateExecutionGate(MultimodalTaskIntent intent)
        {
            ExperimentConditionConfig condition = ResolveExperimentCondition();
            VoiceCommandExecutionGateResult result = condition.EvaluateVoiceCommand(intent);
            Dictionary<string, object> payload = condition.ToPayload();
            payload["reason"] = result.Reason;
            payload["allowed"] = result.Allowed;
            payload["bridge_invoked"] = false;
            payload["command_source"] = _activeCommandSource;
            payload["source"] = IsP44IDebugSource(_activeCommandSource) ? P44IDebugSource : _activeCommandSource;
            payload["is_debug_injected"] = IsP44IDebugSource(_activeCommandSource);
            payload["task_flow"] = intent != null ? intent.TaskFlow.ToString() : string.Empty;
            payload["object_selection_mode"] = intent != null ? intent.ObjectSelectionMode.ToString() : string.Empty;
            payload["target"] = intent != null ? intent.TargetId : string.Empty;
            payload["target_id"] = intent != null ? intent.TargetId : string.Empty;
            payload["object_category"] = intent != null ? intent.ObjectCategory : string.Empty;
            payload["destination"] = intent != null ? intent.PlaceTargetId : string.Empty;
            TiagoExperimentTelemetry.LogEvent(
                result.Allowed ? "voice_command_allowed_by_condition" : "voice_command_blocked_by_condition",
                payload);
            return result;
        }

        private ExperimentConditionConfig ResolveExperimentCondition()
        {
            if (_experimentConditionProviderComponent is IExperimentConditionProvider configuredProvider &&
                configuredProvider.CurrentCondition != null)
            {
                return configuredProvider.CurrentCondition;
            }

            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour is IExperimentConditionProvider sceneProvider && sceneProvider.CurrentCondition != null)
                {
                    return sceneProvider.CurrentCondition;
                }
            }

            return new ExperimentConditionConfig(true, true, "default_robot_on_voice_on");
        }

        private IExplicitAutonomyIntentActivity ResolveExplicitIntentActivity()
        {
            if (_explicitIntentActivityComponent is IExplicitAutonomyIntentActivity configuredActivity)
            {
                return configuredActivity;
            }

            return _experimentConditionProviderComponent as IExplicitAutonomyIntentActivity;
        }

        private void TryResolveBridge()
        {
            if (_commandBridge != null || !_autoFindBridgeInParents)
            {
                return;
            }

            _commandBridge = GetComponent<MultimodalAutonomyCommandBridge>();
            if (_commandBridge == null)
            {
                _commandBridge = GetComponentInParent<MultimodalAutonomyCommandBridge>();
            }

            if (_commandBridge == null && _autoFindBridgeInScene)
            {
                _commandBridge = FindFirstObjectByType<MultimodalAutonomyCommandBridge>();
            }
        }

        private void LogRuntimeStateDiagnostics(
            VoiceAutonomyCommandRoutingResult result,
            string transcript,
            string commandSource,
            string voiceInteractionId)
        {
            if (!_enableVoiceExperimentLogging)
            {
                return;
            }

            Dictionary<string, object> payload = BuildVoiceCommandRuntimeStatePayload(result, transcript, commandSource, voiceInteractionId);
            TiagoExperimentTelemetry.LogEvent("task_state_at_voice_command", new Dictionary<string, object>(payload));
            TiagoExperimentTelemetry.LogEvent("round_assignment_state_at_voice_command", BuildRoundAssignmentPayload(payload));
            TiagoExperimentTelemetry.LogEvent("p40_trace_round_assignment_snapshot", BuildP40RoundAssignmentPayload(payload));
            TiagoExperimentTelemetry.LogEvent("p40_trace_navigation_liveness_snapshot", BuildP40NavigationLivenessPayload(payload));
        }

        private Dictionary<string, object> BuildP44IBasePayload(string label, string transcript, string voiceInteractionId)
        {
            Dictionary<string, object> payload = BuildP44IVoiceStopResumeStatePayload(label ?? string.Empty);
            payload["label"] = label ?? string.Empty;
            payload["raw_transcript"] = transcript ?? string.Empty;
            payload["voice_interaction_id"] = voiceInteractionId ?? string.Empty;
            payload["command_source"] = P44IDebugCommandSource;
            payload["source"] = P44IDebugSource;
            payload["is_debug_injected"] = true;
            payload["application_is_playing"] = Application.isPlaying;
            payload["voice_connector"] = name;
            payload["voice_connector_instance_id"] = GetInstanceID();
            payload["scene"] = gameObject.scene.IsValid() ? gameObject.scene.name : string.Empty;
            return payload;
        }

        private Dictionary<string, object> BuildP44IVoiceStopResumeStatePayload(string reason)
        {
            TryResolveBridge();
            AutonomousRobotAdapter adapter = _commandBridge != null ? _commandBridge.RobotAdapter : null;
            Dictionary<string, object> payload = adapter != null
                ? adapter.BuildP44IVoiceStopResumeStatePayload(reason)
                : new Dictionary<string, object> { ["reason"] = reason ?? string.Empty };
            ExperimentConditionConfig condition = ResolveExperimentCondition();
            ExperimentInstrumentationController instrumentation = FindFirstObjectByType<ExperimentInstrumentationController>();
            string instrumentationConditionId = ResolveInstrumentationString(instrumentation, "_conditionId");
            string instrumentationConditionName = ResolveInstrumentationString(instrumentation, "_conditionName");
            payload["diagnostic_version"] = "P44I";
            payload["command_source"] = P44IDebugCommandSource;
            payload["source"] = P44IDebugSource;
            payload["is_debug_injected"] = true;
            payload["condition_id"] = !string.IsNullOrWhiteSpace(instrumentationConditionId)
                ? instrumentationConditionId
                : condition != null ? condition.ConditionName : string.Empty;
            payload["condition_name"] = !string.IsNullOrWhiteSpace(instrumentationConditionName)
                ? instrumentationConditionName
                : condition != null ? condition.ConditionName : string.Empty;
            payload["robot_enabled"] = condition != null && condition.RobotEnabled;
            payload["voice_enabled"] = condition != null && condition.VoiceEnabled;
            payload["assistance_mode"] = condition != null ? condition.AssistanceMode.ToString() : string.Empty;
            payload["trial_id"] = ResolveCurrentTrialId(instrumentation);
            payload["trial_active"] = instrumentation != null && instrumentation.TrialActive;
            payload["last_terminal_trial_id"] = instrumentation != null ? instrumentation.LastTerminalTrialId : string.Empty;
            payload["last_terminal_state"] = instrumentation != null ? instrumentation.LastTerminalState : string.Empty;
            payload["adapter_available"] = adapter != null;
            payload["bridge_available"] = _commandBridge != null;
            payload["voice_connector"] = name;
            payload["voice_connector_instance_id"] = GetInstanceID();
            return payload;
        }

        private static bool IsP44IDebugSource(string commandSource)
        {
            return string.Equals(commandSource, P44IDebugCommandSource, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveInstrumentationString(ExperimentInstrumentationController instrumentation, string fieldName)
        {
            if (instrumentation == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return string.Empty;
            }

            FieldInfo field = typeof(ExperimentInstrumentationController)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null ? field.GetValue(instrumentation) as string ?? string.Empty : string.Empty;
        }

        private static string ResolveCurrentTrialId(ExperimentInstrumentationController instrumentation)
        {
            if (instrumentation == null)
            {
                return string.Empty;
            }

            FieldInfo trialMetadataField = typeof(ExperimentInstrumentationController)
                .GetField("_trialMetadata", BindingFlags.Instance | BindingFlags.NonPublic);
            object trialMetadata = trialMetadataField != null ? trialMetadataField.GetValue(instrumentation) : null;
            if (trialMetadata != null)
            {
                PropertyInfo trialIdProperty = trialMetadata.GetType().GetProperty("TrialId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                string trialId = trialIdProperty != null ? trialIdProperty.GetValue(trialMetadata) as string : string.Empty;
                if (!string.IsNullOrWhiteSpace(trialId))
                {
                    return trialId;
                }
            }

            return instrumentation.LastTerminalTrialId;
        }

        private Dictionary<string, object> BuildVoiceCommandRuntimeStatePayload(
            VoiceAutonomyCommandRoutingResult result,
            string transcript,
            string commandSource,
            string voiceInteractionId)
        {
            AutonomousRobotAdapter adapter = _commandBridge != null ? _commandBridge.RobotAdapter : null;
            IReadOnlyRobotBlackboard blackboard = adapter != null ? adapter.Blackboard : null;
            TaskStatus taskStatus = TaskStatus.None;
            bool hasTaskStatus = blackboard != null && blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out taskStatus);
            TargetDescriptor currentTarget = null;
            TargetDescriptor placeTarget = null;
            bool hasCurrentTarget = blackboard != null && blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out currentTarget) && currentTarget != null;
            bool hasPlaceTarget = blackboard != null && blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out placeTarget) && placeTarget != null;
            string heldObjectId = adapter != null ? adapter.HeldObjectId : string.Empty;
            string assignedBoxId = ResolveAssignedBoxId(out bool hasAssignedBox, out int assignedCount, out string assignmentSource);
            string phase = InferTaskPhase(taskStatus, hasCurrentTarget, hasPlaceTarget, heldObjectId, adapter != null ? adapter.ManipulationState : string.Empty);
            bool robotBusy = taskStatus == TaskStatus.InProgress ||
                             (blackboard != null && blackboard.CurrentMode == RobotMode.Autonomous);

            Dictionary<string, object> payload = new()
            {
                ["raw_transcript"] = transcript ?? string.Empty,
                ["normalized_text"] = result?.Normalization?.NormalizedText ?? string.Empty,
                ["intent_kind"] = result?.Mapping?.IntentKind.ToString() ?? string.Empty,
                ["target_alias"] = ResolveTargetAlias(result),
                ["target_id"] = ResolveTargetId(result),
                ["destination"] = ResolveDestination(result),
                ["requested_destination"] = result?.Normalization?.DestinationLabel ?? string.Empty,
                ["symbolic_destination"] = result?.Normalization?.DestinationLabel ?? string.Empty,
                ["resolved_destination"] = ResolveDestination(result),
                ["submitted_destination"] = result?.SubmittedIntent?.PlaceTargetId ?? string.Empty,
                ["current_task_status"] = hasTaskStatus ? taskStatus.ToString() : "Unknown",
                ["task_status"] = hasTaskStatus ? taskStatus.ToString() : "Unknown",
                ["current_target"] = currentTarget != null ? currentTarget.Id : string.Empty,
                ["current_target_id"] = currentTarget != null ? currentTarget.Id : string.Empty,
                ["current_place_target_id"] = placeTarget != null ? placeTarget.Id : string.Empty,
                ["held_object"] = heldObjectId,
                ["held_object_id"] = heldObjectId,
                ["assigned_box"] = assignedBoxId,
                ["assigned_box_id"] = assignedBoxId,
                ["has_assigned_box"] = hasAssignedBox,
                ["assigned_count"] = assignedCount,
                ["assignment_source"] = assignmentSource,
                ["robot_busy"] = robotBusy,
                ["phase"] = phase,
                ["inferred_phase"] = phase,
                ["decision"] = ResolveDiagnosticDecision(result),
                ["reason"] = result?.Reason ?? string.Empty,
                ["routing_status"] = result?.Status.ToString() ?? string.Empty,
                ["submitted"] = result?.SubmittedIntent != null,
                ["bridge_invoked"] = _lastBridgeSubmitInvoked,
                ["bridge_submit_reason"] = _lastBridgeSubmitReason ?? string.Empty,
                ["bridge_submit_failure_reason"] = _lastBridgeSubmitFailureReason ?? string.Empty,
                ["request_id"] = ResolveRequestIdFromLastVoice(),
                ["active_task_instance_id"] = adapter != null ? adapter.ActiveP40TaskInstanceId : string.Empty,
                ["producer"] = adapter != null && !string.IsNullOrWhiteSpace(adapter.ActiveP40Producer)
                    ? adapter.ActiveP40Producer
                    : P40TraceContext.NormalizeProducer(commandSource),
                ["command_source"] = commandSource ?? string.Empty,
                ["source"] = IsP44IDebugSource(commandSource) ? P44IDebugSource : commandSource ?? string.Empty,
                ["is_debug_injected"] = IsP44IDebugSource(commandSource),
                ["voice_connector"] = name,
                ["adapter_available"] = adapter != null,
                ["blackboard_available"] = blackboard != null,
                ["robot_mode"] = blackboard != null ? blackboard.CurrentMode.ToString() : string.Empty,
                ["manipulation_state"] = adapter != null ? adapter.ManipulationState : string.Empty
            };

            if (!string.IsNullOrWhiteSpace(voiceInteractionId))
            {
                payload["voice_interaction_id"] = voiceInteractionId;
            }

            return payload;
        }

        private void LogP40SubscriptionSnapshotIfNeeded(string voiceInteractionId)
        {
            if (_p40SubscriptionSnapshotLogged || !_enableVoiceExperimentLogging)
            {
                return;
            }

            _p40SubscriptionSnapshotLogged = true;
            TryResolveBridge();
            VoiceRecognitionController controller = FindFirstObjectByType<VoiceRecognitionController>();
            TiagoExperimentTelemetry.LogEvent(
                "p40_trace_subscription_snapshot",
                new Dictionary<string, object>
                {
                    ["voice_interaction_id"] = voiceInteractionId ?? string.Empty,
                    ["voice_connector_name"] = name,
                    ["voice_connector_instance_id"] = GetInstanceID(),
                    ["voice_recognition_controller_name"] = controller != null ? controller.name : string.Empty,
                    ["voice_recognition_controller_instance_id"] = controller != null ? controller.GetInstanceID() : 0,
                    ["router_initialized"] = _router != null,
                    ["router_hash"] = _router != null ? _router.GetHashCode() : 0,
                    ["bridge_name"] = _commandBridge != null ? _commandBridge.name : string.Empty,
                    ["bridge_instance_id"] = _commandBridge != null ? _commandBridge.GetInstanceID() : 0,
                    ["connector_count"] = FindObjectsByType<VoiceAutonomyCommandConnector>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                    ["voice_controller_count"] = FindObjectsByType<VoiceRecognitionController>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length
                });
        }

        private Dictionary<string, object> BuildRoundAssignmentPayload(Dictionary<string, object> statePayload)
        {
            return new Dictionary<string, object>
            {
                ["raw_transcript"] = GetPayloadText(statePayload, "raw_transcript"),
                ["normalized_text"] = GetPayloadText(statePayload, "normalized_text"),
                ["intent_kind"] = GetPayloadText(statePayload, "intent_kind"),
                ["target_alias"] = GetPayloadText(statePayload, "target_alias"),
                ["target_id"] = GetPayloadText(statePayload, "target_id"),
                ["destination"] = GetPayloadText(statePayload, "destination"),
                ["assigned_box"] = GetPayloadText(statePayload, "assigned_box"),
                ["assigned_box_id"] = GetPayloadText(statePayload, "assigned_box_id"),
                ["has_assigned_box"] = statePayload.TryGetValue("has_assigned_box", out object hasAssignedBox) && hasAssignedBox is bool assigned && assigned,
                ["assigned_count"] = statePayload.TryGetValue("assigned_count", out object assignedCount) ? assignedCount : 0,
                ["assignment_source"] = GetPayloadText(statePayload, "assignment_source"),
                ["current_task_status"] = GetPayloadText(statePayload, "current_task_status"),
                ["current_target"] = GetPayloadText(statePayload, "current_target"),
                ["held_object"] = GetPayloadText(statePayload, "held_object"),
                ["robot_busy"] = statePayload.TryGetValue("robot_busy", out object busy) && busy is bool robotBusy && robotBusy,
                ["phase"] = GetPayloadText(statePayload, "phase"),
                ["decision"] = GetPayloadText(statePayload, "decision"),
                ["reason"] = GetPayloadText(statePayload, "reason"),
                ["voice_interaction_id"] = GetPayloadText(statePayload, "voice_interaction_id")
            };
        }

        private Dictionary<string, object> BuildP40RoundAssignmentPayload(Dictionary<string, object> statePayload)
        {
            Dictionary<string, object> payload = BuildRoundAssignmentPayload(statePayload);
            payload["request_id"] = ResolveRequestIdFromLastVoice();
            payload["active_task_instance_id"] = GetPayloadText(statePayload, "active_task_instance_id");
            payload["held_object_id"] = GetPayloadText(statePayload, "held_object_id");
            payload["boxes"] = BuildP40RoundBoxSummaries();
            payload["box_count"] = (payload["boxes"] as List<Dictionary<string, object>>)?.Count ?? 0;
            return payload;
        }

        private Dictionary<string, object> BuildP40NavigationLivenessPayload(Dictionary<string, object> statePayload)
        {
            AutonomousRobotAdapter adapter = _commandBridge != null ? _commandBridge.RobotAdapter : null;
            TiagoExperimentTelemetry.Snapshot latest = TiagoExperimentTelemetry.Latest;
            return new Dictionary<string, object>
            {
                ["request_id"] = ResolveRequestIdFromLastVoice(),
                ["voice_interaction_id"] = GetPayloadText(statePayload, "voice_interaction_id"),
                ["active_task_instance_id"] = GetPayloadText(statePayload, "active_task_instance_id"),
                ["target_id"] = GetPayloadText(statePayload, "target_id"),
                ["navigation_target_id"] = GetPayloadText(statePayload, "current_target_id"),
                ["path_source"] = latest.ActivePathSource,
                ["remaining_distance"] = latest.RemainingDistance,
                ["active_segment_index"] = latest.ActiveSegmentIndex,
                ["path_age_seconds"] = -1f,
                ["path_belongs_to_current_task"] = adapter != null && !string.IsNullOrWhiteSpace(GetPayloadText(statePayload, "current_target_id")),
                ["navigation_phase"] = GetPayloadText(statePayload, "phase")
            };
        }

        private List<Dictionary<string, object>> BuildP40RoundBoxSummaries()
        {
            var boxes = new List<Dictionary<string, object>>();
            Transform activeRound = ResolveActiveRoundContainer();
            if (activeRound == null)
            {
                return boxes;
            }

            string heldObjectId = _commandBridge != null && _commandBridge.RobotAdapter != null
                ? _commandBridge.RobotAdapter.HeldObjectId
                : string.Empty;
            MonoBehaviour[] behaviours = activeRound.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null || !string.Equals(behaviour.GetType().Name, "BoxMetadata", StringComparison.Ordinal))
                {
                    continue;
                }

                string boxId = behaviour.gameObject.name;
                bool deposited = GetMemberBool(behaviour, "isDeposited");
                boxes.Add(new Dictionary<string, object>
                {
                    ["box_id"] = boxId,
                    ["alias"] = GetMemberText(behaviour, "voiceAlias"),
                    ["spoken_label"] = GetMemberText(behaviour, "spokenLabel"),
                    ["category"] = GetMemberText(behaviour, "boxType"),
                    ["is_assigned"] = false,
                    ["is_held"] = string.Equals(heldObjectId, boxId, StringComparison.OrdinalIgnoreCase),
                    ["is_completed"] = deposited,
                    ["is_deposited"] = deposited,
                    ["assigned_to_task_instance_id"] = string.Empty,
                    ["assigned_source"] = string.Empty
                });
            }

            return boxes;
        }

        private string ResolveAssignedBoxId(out bool hasAssignedBox, out int assignedCount, out string assignmentSource)
        {
            RobotAssistanceRoundCoordinator coordinator = FindFirstObjectByType<RobotAssistanceRoundCoordinator>();
            if (coordinator == null)
            {
                hasAssignedBox = false;
                assignedCount = 0;
                assignmentSource = "round_coordinator_not_found";
                return string.Empty;
            }

            string assignedBoxId = coordinator.AssignedBoxId;
            hasAssignedBox = coordinator.HasAssignedBox;
            assignedCount = hasAssignedBox ? 1 : 0;
            assignmentSource = "robot_assistance_round_coordinator";
            return assignedBoxId ?? string.Empty;
        }

        private static string InferTaskPhase(
            TaskStatus taskStatus,
            bool hasCurrentTarget,
            bool hasPlaceTarget,
            string heldObjectId,
            string manipulationState)
        {
            if (taskStatus == TaskStatus.None || taskStatus == TaskStatus.Succeeded || taskStatus == TaskStatus.Failed)
            {
                return "idle";
            }

            if (taskStatus != TaskStatus.InProgress)
            {
                return "unknown";
            }

            if (!string.IsNullOrWhiteSpace(heldObjectId))
            {
                return hasPlaceTarget ? "placing" : "holding";
            }

            if (!string.IsNullOrWhiteSpace(manipulationState) &&
                manipulationState.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "holding";
            }

            if (hasCurrentTarget)
            {
                return "pre_pick";
            }

            return "unknown";
        }

        private static string ResolveTargetAlias(VoiceAutonomyCommandRoutingResult result)
        {
            if (!string.IsNullOrWhiteSpace(result?.Normalization?.ObjectLabel))
            {
                return result.Normalization.ObjectLabel;
            }

            return result?.Mapping?.TaskIntent?.TargetId ?? string.Empty;
        }

        private static string ResolveTargetId(VoiceAutonomyCommandRoutingResult result)
        {
            return result?.SubmittedIntent?.TargetId ??
                   result?.PendingIntent?.TargetId ??
                   result?.Mapping?.TaskIntent?.TargetId ??
                   string.Empty;
        }

        private static string ResolveDestination(VoiceAutonomyCommandRoutingResult result)
        {
            if (!string.IsNullOrWhiteSpace(result?.SubmittedIntent?.PlaceTargetId))
            {
                return result.SubmittedIntent.PlaceTargetId;
            }

            if (!string.IsNullOrWhiteSpace(result?.PendingIntent?.PlaceTargetId))
            {
                return result.PendingIntent.PlaceTargetId;
            }

            if (!string.IsNullOrWhiteSpace(result?.Mapping?.TaskIntent?.PlaceTargetId))
            {
                return result.Mapping.TaskIntent.PlaceTargetId;
            }

            return result?.Normalization?.DestinationLabel ?? string.Empty;
        }

        private static string ResolveDiagnosticDecision(VoiceAutonomyCommandRoutingResult result)
        {
            if (result == null)
            {
                return "unknown";
            }

            switch (result.Status)
            {
                case VoiceAutonomyCommandRoutingStatus.Submitted:
                case VoiceAutonomyCommandRoutingStatus.ConfirmationAccepted:
                    return "accepted";
                case VoiceAutonomyCommandRoutingStatus.PendingConfirmation:
                    return "pending_confirmation";
                case VoiceAutonomyCommandRoutingStatus.BridgeRejected:
                case VoiceAutonomyCommandRoutingStatus.NotSubmitted:
                    return "rejected";
                case VoiceAutonomyCommandRoutingStatus.ConfirmationCanceled:
                case VoiceAutonomyCommandRoutingStatus.ConfirmationExpired:
                case VoiceAutonomyCommandRoutingStatus.NoPendingConfirmation:
                    return "ignored";
                default:
                    return "unknown";
            }
        }

        private static string GetPayloadText(Dictionary<string, object> payload, string key)
        {
            return payload != null && payload.TryGetValue(key, out object value) && value != null
                ? value.ToString()
                : string.Empty;
        }

        private static string ResolveRequestIdFromLastVoice()
        {
            return P40TraceContext.LastVoiceCommand != null
                ? P40TraceContext.LastVoiceCommand.RequestId
                : string.Empty;
        }

        private void EnsureRouterConfigurationCurrent()
        {
            if (_router == null ||
                _routerConfirmationMode != _confirmationMode ||
                _routerVoiceExperimentLoggingEnabled != _enableVoiceExperimentLogging ||
                Mathf.Abs(_routerConfirmationTimeoutSeconds - Mathf.Max(0.1f, _confirmationTimeoutSeconds)) > 0.0001f ||
                Mathf.Abs(_routerConfirmationLowConfidenceThreshold - Mathf.Clamp01(_confirmationLowConfidenceThreshold)) > 0.0001f)
            {
                Debug.Log($"{LogPrefix} router_reinitialize_due_to_runtime_config | confirmationMode={_confirmationMode}", this);
                Initialize(_commandNormalizer ?? new VoiceCommandNormalizer(), new VoiceCommandIntentMapper());
            }
        }

        private VoiceConfirmationPolicy BuildConfirmationPolicy()
        {
            return new VoiceConfirmationPolicy(
                _confirmationMode,
                _confirmationTimeoutSeconds,
                _confirmationLowConfidenceThreshold);
        }

        private IVoiceUserFeedbackSink ResolveFeedbackSink()
        {
            if (!_enableMinimalVoiceFeedback)
            {
                return null;
            }

            if (_feedbackSinkComponent is IVoiceUserFeedbackSink configuredSink)
            {
                return configuredSink;
            }

            return new DebugLogFeedbackSink(message => Debug.Log($"{LogPrefix} feedback | {message.Type} | {message.Text}", this));
        }

        private IRobotVoiceFeedbackSink ResolveRobotFeedbackSink()
        {
            if (!_enableStructuredRobotVoiceFeedback)
            {
                return null;
            }

            if (_robotFeedbackSinkComponent is IRobotVoiceFeedbackSink configuredRobotSink)
            {
                return BuildRobotFeedbackSinkWithDiagnostics(configuredRobotSink);
            }

            if (_feedbackSinkComponent is IRobotVoiceFeedbackSink configuredSharedSink)
            {
                return BuildRobotFeedbackSinkWithDiagnostics(configuredSharedSink);
            }

            StructuredTtsFeedbackSink localTtsSink = GetComponent<StructuredTtsFeedbackSink>();
            if (localTtsSink != null)
            {
                return BuildRobotFeedbackSinkWithDiagnostics(localTtsSink);
            }

            return _useDebugRobotVoiceFeedbackLog
                ? BuildRobotFeedbackSinkWithDiagnostics(new DebugRobotVoiceFeedbackSink(message => Debug.Log($"{LogPrefix} robot_feedback | {message.Kind} | {message.Text}", this)))
                : BuildRobotFeedbackSinkWithDiagnostics(null);
        }

        private IRobotVoiceFeedbackSink BuildRobotFeedbackSinkWithDiagnostics(IRobotVoiceFeedbackSink primarySink)
        {
            IRobotVoiceFeedbackSink diagnosticSink = ResolveRobotVoiceFeedbackDiagnosticSink();
            if (primarySink == null)
            {
                return diagnosticSink;
            }

            if (diagnosticSink == null || ReferenceEquals(primarySink, diagnosticSink))
            {
                return primarySink;
            }

            return new CompositeRobotVoiceFeedbackSink(primarySink, diagnosticSink);
        }

        private IRobotVoiceFeedbackSink ResolveRobotVoiceFeedbackDiagnosticSink()
        {
            if (!_enableStructuredVoiceFeedbackDiagnostics)
            {
                if (_robotVoiceFeedbackDiagnosticRecorder != null)
                {
                    _robotVoiceFeedbackDiagnosticRecorder.SetDiagnosticModeEnabled(false);
                }

                return null;
            }

            if (_robotVoiceFeedbackDiagnosticRecorder == null)
            {
                _robotVoiceFeedbackDiagnosticRecorder = GetComponent<RobotVoiceFeedbackDiagnosticRecorder>();
            }

            if (_robotVoiceFeedbackDiagnosticRecorder == null)
            {
                _robotVoiceFeedbackDiagnosticRecorder = gameObject.AddComponent<RobotVoiceFeedbackDiagnosticRecorder>();
            }

            _robotVoiceFeedbackDiagnosticRecorder.SetDiagnosticModeEnabled(true);
            return _robotVoiceFeedbackDiagnosticRecorder;
        }

        private IVoiceExperimentEventSink ResolveExperimentEventSink()
        {
            if (!_enableVoiceExperimentLogging)
            {
                return null;
            }

            if (_voiceExperimentEventSinkComponent is IVoiceExperimentEventSink configuredSink)
            {
                return configuredSink;
            }

            return new TiagoVoiceExperimentEventSink(
                () => _activeCommandSource,
                () => _confirmationMode.ToString(),
                () => name);
        }

        private IReadOnlyList<VoiceTargetCandidate> BuildActiveRoundVoiceTargetCandidates()
        {
            Transform activeRound = ResolveActiveRoundContainer();
            var candidates = new List<VoiceTargetCandidate>();
            var seenTargetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (activeRound != null)
            {
                MonoBehaviour[] behaviours = activeRound.GetComponentsInChildren<MonoBehaviour>(true);
                foreach (MonoBehaviour behaviour in behaviours)
                {
                    TryAddVoiceTargetCandidate(behaviour, candidates, seenTargetIds);
                }
            }

            RobotAssistanceRoundCoordinator coordinator = FindFirstObjectByType<RobotAssistanceRoundCoordinator>();
            string heldObjectId = _commandBridge != null && _commandBridge.RobotAdapter != null
                ? _commandBridge.RobotAdapter.HeldObjectId
                : string.Empty;
            string assignedBoxId = coordinator != null ? coordinator.AssignedBoxId : string.Empty;
            MonoBehaviour[] allBehaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (MonoBehaviour behaviour in allBehaviours)
            {
                if (!IsBoxMetadata(behaviour))
                {
                    continue;
                }

                string targetId = behaviour.gameObject.name;
                bool belongsToRound = coordinator != null && coordinator.IsKnownRoundBox(targetId);
                bool isAssignedOrHeld = string.Equals(targetId, assignedBoxId, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(targetId, heldObjectId, StringComparison.OrdinalIgnoreCase);
                if (belongsToRound || isAssignedOrHeld)
                {
                    TryAddVoiceTargetCandidate(behaviour, candidates, seenTargetIds);
                }
            }

            return candidates;
        }

        private static bool TryAddVoiceTargetCandidate(
            MonoBehaviour behaviour,
            List<VoiceTargetCandidate> candidates,
            HashSet<string> seenTargetIds)
        {
            if (!IsBoxMetadata(behaviour))
            {
                return false;
            }

            string targetId = behaviour.gameObject.name;
            if (string.IsNullOrWhiteSpace(targetId) || seenTargetIds.Contains(targetId))
            {
                return false;
            }

            string category = GetMemberText(behaviour, "boxType");
            string spokenLabel = GetMemberText(behaviour, "spokenLabel");
            string displayLabel = GetMemberText(behaviour, "displayLabel");
            string voiceAlias = GetMemberText(behaviour, "voiceAlias");
            bool deposited = GetMemberBool(behaviour, "isDeposited");
            string placeTargetId = string.IsNullOrWhiteSpace(category) ? string.Empty : $"Zone{category.ToUpperInvariant()}";
            candidates.Add(new VoiceTargetCandidate(targetId, category, spokenLabel, displayLabel, voiceAlias, placeTargetId, !deposited));
            seenTargetIds.Add(targetId);
            return true;
        }

        private static bool IsBoxMetadata(MonoBehaviour behaviour)
        {
            return behaviour != null && string.Equals(behaviour.GetType().Name, "BoxMetadata", StringComparison.Ordinal);
        }

        private static Transform ResolveActiveRoundContainer()
        {
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null || !string.Equals(behaviour.GetType().Name, "SpawnManager", StringComparison.Ordinal))
                {
                    continue;
                }

                FieldInfo field = behaviour.GetType().GetField("activeRoundContainer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.GetValue(behaviour) is Transform transform)
                {
                    return transform;
                }
            }

            return null;
        }

        private static string GetMemberText(object target, string memberName)
        {
            object value = GetMemberValue(target, memberName);
            return value != null ? value.ToString() : string.Empty;
        }

        private static bool GetMemberBool(object target, string memberName)
        {
            object value = GetMemberValue(target, memberName);
            return value is bool flag && flag;
        }

        private static object GetMemberValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName))
            {
                return null;
            }

            Type type = target.GetType();
            FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return field.GetValue(target);
            }

            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property != null ? property.GetValue(target) : null;
        }

        private void Reset()
        {
            TryResolveBridge();
        }

        private void OnValidate()
        {
            _confirmationTimeoutSeconds = Mathf.Max(0.1f, _confirmationTimeoutSeconds);
            _confirmationLowConfidenceThreshold = Mathf.Clamp01(_confirmationLowConfidenceThreshold);
        }

        private sealed class VoiceSceneTargetResolvingIntentMapper : IVoiceCommandIntentMapper
        {
            private readonly IVoiceCommandIntentMapper _inner;
            private readonly System.Func<IReadOnlyList<VoiceTargetCandidate>> _getCandidates;
            private readonly IVoiceExperimentEventSink _eventSink;

            public VoiceSceneTargetResolvingIntentMapper(
                IVoiceCommandIntentMapper inner,
                System.Func<IReadOnlyList<VoiceTargetCandidate>> getCandidates,
                IVoiceExperimentEventSink eventSink)
            {
                _inner = inner ?? new VoiceCommandIntentMapper();
                _getCandidates = getCandidates;
                _eventSink = eventSink;
            }

            public VoiceCommandIntentMappingResult Map(VoiceCommandNormalizationResult normalization)
            {
                VoiceCommandIntentMappingResult mapped = _inner.Map(normalization);
                if (mapped == null ||
                    mapped.Status != VoiceCommandIntentMappingStatus.Mapped ||
                    mapped.TaskIntent == null ||
                    mapped.TaskIntent.TaskFlow != AutonomousTaskFlow.PickAndPlace)
                {
                    return mapped;
                }

                VoiceTargetResolutionResult resolution = VoiceTargetResolver.Resolve(
                    mapped.TaskIntent,
                    normalization,
                    _getCandidates?.Invoke());

                switch (resolution.Status)
                {
                    case VoiceTargetResolutionStatus.Resolved:
                        if (IsSelfPlaceTarget(mapped))
                        {
                            EmitTargetResolution("voice_self_zone_resolved", normalization, mapped, resolution);
                        }

                        EmitTargetResolution("voice_target_alias_resolved", normalization, mapped, resolution);
                        return BuildResolvedMapping(mapped, resolution);
                    case VoiceTargetResolutionStatus.Ambiguous:
                        EmitTargetResolution("voice_target_ambiguous", normalization, mapped, resolution);
                        return BuildDiagnosticMapping(mapped, VoiceCommandIntentMappingStatus.Ambiguous, "multiple_matching_boxes", resolution);
                    case VoiceTargetResolutionStatus.NotFound:
                        EmitTargetResolution("voice_target_not_found", normalization, mapped, resolution);
                        return BuildDiagnosticMapping(mapped, VoiceCommandIntentMappingStatus.NotMapped, resolution.Reason, resolution);
                    default:
                        return mapped;
                }
            }

            private static VoiceCommandIntentMappingResult BuildResolvedMapping(
                VoiceCommandIntentMappingResult mapped,
                VoiceTargetResolutionResult resolution)
            {
                VoiceTargetCandidate selected = resolution.SelectedCandidate;
                string placeTargetId = string.Equals(mapped.TaskIntent.PlaceTargetId, "SELF", StringComparison.OrdinalIgnoreCase)
                    ? selected.PlaceTargetId
                    : mapped.TaskIntent.PlaceTargetId;
                MultimodalTaskIntent resolvedIntent = MultimodalTaskIntent.PickAndPlaceByTargetId(
                    selected.TargetId,
                    placeTargetId,
                    mapped.TaskIntent.Source);
                return new VoiceCommandIntentMappingResult(
                    VoiceCommandIntentMappingStatus.Mapped,
                    VoiceCommandIntentMappingError.None,
                    mapped.IntentKind,
                    resolvedIntent,
                    mapped.Normalization,
                    $"PickAndPlace target {selected.TargetId} to {placeTargetId}",
                    resolution.ObjectSelectionMode);
            }

            private static VoiceCommandIntentMappingResult BuildDiagnosticMapping(
                VoiceCommandIntentMappingResult mapped,
                VoiceCommandIntentMappingStatus status,
                string explanation,
                VoiceTargetResolutionResult resolution)
            {
                return new VoiceCommandIntentMappingResult(
                    status,
                    VoiceCommandIntentMappingError.NormalizationAmbiguous,
                    mapped.IntentKind,
                    null,
                    mapped.Normalization,
                    mapped.CandidateDescription,
                    explanation);
            }

            private void EmitTargetResolution(
                string eventType,
                VoiceCommandNormalizationResult normalization,
                VoiceCommandIntentMappingResult mapped,
                VoiceTargetResolutionResult resolution)
            {
                var payload = new Dictionary<string, object>
                {
                    ["raw_transcript"] = normalization != null ? normalization.RawTranscript : string.Empty,
                    ["normalized_text"] = normalization != null ? normalization.NormalizedText : string.Empty,
                    ["spoken_label"] = resolution.RequestedLabel,
                    ["box_alias"] = resolution.RequestedLabel,
                    ["target_alias"] = resolution.RequestedLabel,
                    ["target_id"] = resolution.SelectedCandidate != null ? resolution.SelectedCandidate.TargetId : string.Empty,
                    ["target_category"] = resolution.SelectedCandidate != null ? resolution.SelectedCandidate.Category : string.Empty,
                    ["object_category"] = !string.IsNullOrWhiteSpace(resolution.ObjectCategory)
                        ? resolution.ObjectCategory
                        : mapped?.TaskIntent?.ObjectCategory ?? string.Empty,
                    ["candidate_count"] = resolution.CandidateCount,
                    ["ambiguity_reason"] = resolution.Status == VoiceTargetResolutionStatus.Ambiguous ? resolution.Reason : string.Empty,
                    ["reason"] = resolution.Reason,
                    ["object_selection_mode"] = resolution.ObjectSelectionMode,
                    ["destination"] = ResolveLoggedDestination(mapped, resolution),
                    ["destination_id"] = ResolveLoggedDestination(mapped, resolution),
                    ["destination_category"] = ExtractZoneCategory(ResolveLoggedDestination(mapped, resolution)),
                    ["resolution_source"] = IsSelfPlaceTarget(mapped) ? "self_zone_from_box_metadata" : "explicit_or_mapped_destination",
                    ["is_self_zone"] = IsSelfPlaceTarget(mapped),
                    ["category_match"] = string.Equals(
                        resolution.SelectedCandidate != null ? resolution.SelectedCandidate.Category : string.Empty,
                        ExtractZoneCategory(ResolveLoggedDestination(mapped, resolution)),
                        StringComparison.OrdinalIgnoreCase)
                };
                _eventSink?.Emit(eventType, payload);
            }

            private static bool IsSelfPlaceTarget(VoiceCommandIntentMappingResult mapped)
            {
                return string.Equals(mapped?.TaskIntent?.PlaceTargetId, "SELF", StringComparison.OrdinalIgnoreCase);
            }

            private static string ResolveLoggedDestination(VoiceCommandIntentMappingResult mapped, VoiceTargetResolutionResult resolution)
            {
                return resolution.SelectedCandidate != null && IsSelfPlaceTarget(mapped)
                    ? resolution.SelectedCandidate.PlaceTargetId
                    : mapped?.TaskIntent?.PlaceTargetId ?? string.Empty;
            }

            private static string ExtractZoneCategory(string placeTargetId)
            {
                if (string.IsNullOrWhiteSpace(placeTargetId))
                {
                    return string.Empty;
                }

                string trimmed = placeTargetId.Trim();
                return trimmed.StartsWith("Zone", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 4
                    ? trimmed.Substring(4).ToUpperInvariant()
                    : trimmed.ToUpperInvariant();
            }
        }

        private sealed class DebugLogFeedbackSink : IVoiceUserFeedbackSink
        {
            private readonly System.Action<VoiceUserFeedbackMessage> _log;

            public DebugLogFeedbackSink(System.Action<VoiceUserFeedbackMessage> log)
            {
                _log = log;
            }

            public void Emit(VoiceUserFeedbackMessage message)
            {
                if (message != null)
                {
                    _log?.Invoke(message);
                }
            }
        }

        private sealed class CompositeRobotVoiceFeedbackSink : IRobotVoiceFeedbackSink
        {
            private readonly IRobotVoiceFeedbackSink _first;
            private readonly IRobotVoiceFeedbackSink _second;

            public CompositeRobotVoiceFeedbackSink(IRobotVoiceFeedbackSink first, IRobotVoiceFeedbackSink second)
            {
                _first = first;
                _second = second;
            }

            public void Emit(RobotVoiceFeedbackMessage message)
            {
                _first?.Emit(message);
                _second?.Emit(message);
            }
        }

        private sealed class TiagoVoiceExperimentEventSink : IVoiceExperimentEventSink
        {
            private readonly System.Func<string> _getCommandSource;
            private readonly System.Func<string> _getConfirmationMode;
            private readonly System.Func<string> _getConnectorName;

            public TiagoVoiceExperimentEventSink(
                System.Func<string> getCommandSource,
                System.Func<string> getConfirmationMode,
                System.Func<string> getConnectorName)
            {
                _getCommandSource = getCommandSource;
                _getConfirmationMode = getConfirmationMode;
                _getConnectorName = getConnectorName;
            }

            public void Emit(string eventType, Dictionary<string, object> payload)
            {
                Dictionary<string, object> enriched = payload != null
                    ? new Dictionary<string, object>(payload)
                    : new Dictionary<string, object>();
                string commandSource = _getCommandSource?.Invoke() ?? "voice_command";
                enriched["command_source"] = commandSource;
                enriched["source"] = commandSource;
                enriched["confirmation_mode"] = _getConfirmationMode?.Invoke() ?? string.Empty;
                enriched["voice_connector"] = _getConnectorName?.Invoke() ?? string.Empty;
                enriched["scene"] = SceneManager.GetActiveScene().name;
                TiagoExperimentTelemetry.LogEvent(eventType, enriched);
            }
        }
    }
}
