using System;
using System.Collections.Generic;

namespace Autonomy.Domain
{
    public sealed class VoiceAutonomyCommandRouter
    {
        private readonly IVoiceCommandNormalizer _normalizer;
        private readonly IVoiceCommandIntentMapper _intentMapper;
        private readonly Func<MultimodalTaskIntent, bool> _submitIntent;
        private readonly Action<string> _log;
        private readonly Action<string> _warn;
        private readonly VoiceConfirmationPolicy _confirmationPolicy;
        private readonly VoiceConfirmationResponseParser _confirmationResponseParser;
        private readonly VoiceConfirmationManager _confirmationManager;
        private readonly IVoiceUserFeedbackSink _feedbackSink;
        private readonly IRobotVoiceFeedbackSink _robotFeedbackSink;
        private readonly RobotVoiceFeedbackTemplateBuilder _robotFeedbackTemplateBuilder;
        private readonly IVoiceExperimentEventSink _experimentEventSink;
        private readonly Func<MultimodalTaskIntent, VoiceCommandExecutionGateResult> _executionGate;
        private readonly Func<string> _resolveSubmitRejectionReason;
        private readonly Func<VoiceCommandIntentMappingResult, VoiceCommandNormalizationResult, VoiceStopCommandResult> _applyStopCommand;
        private readonly Func<VoiceCommandIntentMappingResult, VoiceCommandNormalizationResult, VoiceResumeCommandResult> _applyResumeCommand;
        private readonly Action<VoiceCommandIntentMappingResult, VoiceCommandNormalizationResult> _applyCancelPendingOrder;
        private readonly Func<bool> _isVoiceStopLatched;
        private readonly Func<bool> _isExperimentPaused;
        private readonly Func<float> _getTimeSeconds;
        private string _currentTranscript = string.Empty;
        private string _currentVoiceInteractionId = string.Empty;
        private string _pendingVoiceInteractionId = string.Empty;
        private P40TraceMetadata _currentTraceMetadata;

        public VoiceAutonomyCommandRouter(
            IVoiceCommandNormalizer normalizer,
            IVoiceCommandIntentMapper intentMapper,
            Func<MultimodalTaskIntent, bool> submitIntent,
            Action<string> log = null,
            Action<string> warn = null,
            VoiceConfirmationPolicy confirmationPolicy = null,
            IVoiceUserFeedbackSink feedbackSink = null,
            IRobotVoiceFeedbackSink robotFeedbackSink = null,
            IVoiceExperimentEventSink experimentEventSink = null,
            Func<float> getTimeSeconds = null,
            VoiceConfirmationResponseParser confirmationResponseParser = null,
            VoiceConfirmationManager confirmationManager = null,
            Func<MultimodalTaskIntent, VoiceCommandExecutionGateResult> executionGate = null,
            Func<string> resolveSubmitRejectionReason = null,
            Func<VoiceCommandIntentMappingResult, VoiceCommandNormalizationResult, VoiceStopCommandResult> applyStopCommand = null,
            Func<VoiceCommandIntentMappingResult, VoiceCommandNormalizationResult, VoiceResumeCommandResult> applyResumeCommand = null,
            Action<VoiceCommandIntentMappingResult, VoiceCommandNormalizationResult> applyCancelPendingOrder = null,
            Func<bool> isVoiceStopLatched = null,
            Func<bool> isExperimentPaused = null)
        {
            _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
            _intentMapper = intentMapper ?? throw new ArgumentNullException(nameof(intentMapper));
            _submitIntent = submitIntent;
            _log = log;
            _warn = warn;
            _confirmationPolicy = confirmationPolicy ?? new VoiceConfirmationPolicy();
            _feedbackSink = feedbackSink;
            _robotFeedbackSink = robotFeedbackSink;
            _robotFeedbackTemplateBuilder = new RobotVoiceFeedbackTemplateBuilder();
            _experimentEventSink = experimentEventSink;
            _executionGate = executionGate;
            _resolveSubmitRejectionReason = resolveSubmitRejectionReason;
            _applyStopCommand = applyStopCommand;
            _applyResumeCommand = applyResumeCommand;
            _applyCancelPendingOrder = applyCancelPendingOrder;
            _isVoiceStopLatched = isVoiceStopLatched;
            _isExperimentPaused = isExperimentPaused;
            _getTimeSeconds = getTimeSeconds ?? (() => 0f);
            _confirmationResponseParser = confirmationResponseParser ?? new VoiceConfirmationResponseParser();
            _confirmationManager = confirmationManager ?? new VoiceConfirmationManager();
        }

        public VoiceConfirmationMode ConfirmationMode => _confirmationPolicy.Mode;
        public float ConfirmationTimeoutSeconds => _confirmationPolicy.TimeoutSeconds;
        public float ConfirmationLowConfidenceThreshold => _confirmationPolicy.LowConfidenceThreshold;
        public bool HasPendingConfirmation => _confirmationManager.HasPendingIntent;

        public void ClearPendingConfirmation()
        {
            _confirmationManager.Clear();
            _pendingVoiceInteractionId = string.Empty;
        }

        public VoiceAutonomyCommandRoutingResult RouteTranscript(
            string transcript,
            string voiceInteractionId = "",
            bool forceExperimentPauseRejection = false)
        {
            _currentTranscript = transcript ?? string.Empty;
            _currentVoiceInteractionId = string.IsNullOrWhiteSpace(voiceInteractionId)
                ? string.Empty
                : voiceInteractionId;
            _currentTraceMetadata = new P40TraceMetadata
            {
                RequestId = P40TraceContext.NextRequestId("voice_command"),
                VoiceInteractionId = _currentVoiceInteractionId,
                Producer = "voice_command",
                CausalParentId = _currentVoiceInteractionId,
                RawTranscript = _currentTranscript
            };
            EmitP40VoiceLifecycle("received", null, null, null, "received", "transcript_entered_router", false);
            EmitVoiceCommandReceivedDiagnostic();
            if (forceExperimentPauseRejection || IsExperimentPaused())
            {
                return RejectCommandWhileExperimentPaused();
            }

            if (TryExpirePendingConfirmation(out VoiceAutonomyCommandRoutingResult expired))
            {
                return expired;
            }

            VoiceConfirmationResponse confirmationResponse = _confirmationResponseParser.Parse(transcript);
            if (confirmationResponse == VoiceConfirmationResponse.Affirmative)
            {
                if (ShouldRejectPickAndPlaceForVoiceStopLatch(_confirmationManager.PendingIntent))
                {
                    return RejectPickAndPlaceWhileStopped(
                        _confirmationManager.PendingMapping?.Normalization,
                        _confirmationManager.PendingMapping,
                        _confirmationManager.PendingIntent);
                }

                return AcceptPendingConfirmation();
            }

            if (confirmationResponse == VoiceConfirmationResponse.Negative)
            {
                return CancelPendingConfirmation();
            }

            VoiceCommandNormalizationResult normalization = _normalizer.Normalize(transcript);
            UpdateTraceFromNormalization(normalization);
            EmitP40VoiceLifecycle("normalized", normalization, null, null, "normalized", normalization.Status.ToString(), false);
            EmitVoiceCommandNormalized(normalization);
            VoiceCommandIntentMappingResult mapping = _intentMapper.Map(normalization);
            UpdateTraceFromMapping(mapping);
            EmitP40VoiceLifecycle("mapped", normalization, mapping, mapping?.TaskIntent, "mapped", mapping?.Status.ToString() ?? string.Empty, false);
            EmitVoiceIntentMapped(mapping);
            EmitVoiceCommandControlPathDiagnostic(mapping, normalization);

            if (normalization.Status != VoiceCommandRecognitionStatus.Recognized)
            {
                string reason = $"normalization_not_executable_{normalization.Status}";
                Warn($"{reason} | raw='{normalization.RawTranscript}' normalized='{normalization.NormalizedText}' ambiguity='{normalization.AmbiguityReason}'");
                return CompleteRoute(NotSubmitted(reason, normalization, mapping, BuildNonExecutableFeedback(normalization, mapping, reason)), false);
            }

            if (mapping.Status != VoiceCommandIntentMappingStatus.Mapped)
            {
                string reason = $"mapping_not_executable_{mapping.Status}";
                Warn($"{reason} | candidate='{mapping.CandidateDescription}' explanation='{mapping.Explanation}'");
                return CompleteRoute(NotSubmitted(reason, normalization, mapping, BuildNonExecutableFeedback(normalization, mapping, reason)), false);
            }

            if (mapping.TaskIntent == null)
            {
                if (mapping.IntentKind == VoiceCommandIntentKind.Stop)
                {
                    if (!TryPassExecutionGate(null, out VoiceCommandExecutionGateResult stopGateResult))
                    {
                        return CompleteRoute(NotSubmitted(stopGateResult.Reason, normalization, mapping, BuildConditionBlockedFeedback(stopGateResult, mapping)), false);
                    }

                    return TryApplyStopCommand(normalization, mapping);
                }

                if (mapping.IntentKind == VoiceCommandIntentKind.Resume)
                {
                    if (!TryPassExecutionGate(null, out VoiceCommandExecutionGateResult resumeGateResult))
                    {
                        return CompleteRoute(NotSubmitted(resumeGateResult.Reason, normalization, mapping, BuildConditionBlockedFeedback(resumeGateResult, mapping)), false);
                    }

                    return TryApplyResumeCommand(normalization, mapping);
                }

                if (mapping.IntentKind == VoiceCommandIntentKind.CancelPendingOrder)
                {
                    if (!TryPassExecutionGate(null, out VoiceCommandExecutionGateResult cancelGateResult))
                    {
                        return CompleteRoute(NotSubmitted(cancelGateResult.Reason, normalization, mapping, BuildConditionBlockedFeedback(cancelGateResult, mapping)), false);
                    }

                    _applyCancelPendingOrder?.Invoke(mapping, normalization);
                }

                string reason = $"mapped_intent_without_executable_bridge_task_{mapping.IntentKind}";
                Warn($"{reason} | candidate='{mapping.CandidateDescription}' explanation='{mapping.Explanation}'");
                return CompleteRoute(NotSubmitted(reason, normalization, mapping, BuildNonExecutableFeedback(normalization, mapping, reason)), false);
            }

            if (mapping.IntentKind != VoiceCommandIntentKind.PickAndPlace ||
                mapping.TaskIntent.TaskFlow != AutonomousTaskFlow.PickAndPlace)
            {
                string reason = $"unsupported_operational_intent_{mapping.IntentKind}";
                Warn($"{reason} | task_flow={mapping.TaskIntent.TaskFlow} object_category='{mapping.TaskIntent.ObjectCategory}' place_target='{mapping.TaskIntent.PlaceTargetId}'");
                return CompleteRoute(NotSubmitted(reason, normalization, mapping, BuildNonExecutableFeedback(normalization, mapping, reason)), false);
            }

            bool executable = _confirmationPolicy.IsExecutablePickAndPlace(mapping);
            bool requiresConfirmation = _confirmationPolicy.RequiresConfirmation(mapping);
            Log($"confirmation_policy_evaluated | mode={_confirmationPolicy.Mode} executable={executable} score={mapping.Score:0.###} threshold={_confirmationPolicy.LowConfidenceThreshold:0.###} requires_confirmation={requiresConfirmation}");

            if (!TryPassExecutionGate(mapping.TaskIntent, out VoiceCommandExecutionGateResult gateResult))
            {
                VoiceUserFeedbackMessage blockedFeedback = BuildFeedback(
                    VoiceUserFeedbackType.CommandBlockedByCondition,
                    string.IsNullOrWhiteSpace(gateResult.FeedbackText) ? "Orden bloqueada por la condicion experimental." : gateResult.FeedbackText,
                    gateResult.Reason,
                    mapping.TaskIntent,
                    mapping);
                return CompleteRoute(NotSubmitted(gateResult.Reason, normalization, mapping, blockedFeedback), false);
            }

            if (ShouldRejectPickAndPlaceForVoiceStopLatch(mapping.TaskIntent))
            {
                return RejectPickAndPlaceWhileStopped(normalization, mapping, mapping.TaskIntent);
            }

            if (requiresConfirmation)
            {
                bool replacingPending = _confirmationManager.HasPendingIntent;
                _confirmationManager.SetPending(
                    mapping.TaskIntent,
                    mapping,
                    _getTimeSeconds(),
                    _confirmationPolicy.TimeoutSeconds);
                _pendingVoiceInteractionId = _currentVoiceInteractionId;
                if (replacingPending)
                {
                    Log("pending_intent_replaced");
                }

                VoiceUserFeedbackMessage feedback = BuildFeedback(
                    VoiceUserFeedbackType.ConfirmationRequested,
                    "Confirmas ejecutar la orden?",
                    "confirmation_required",
                    mapping.TaskIntent,
                    mapping);
                Log($"pending_intent_created | intent_kind={mapping.IntentKind} expires_at={_confirmationManager.ExpiresAtSeconds:0.###}");
                Log($"bridge_invocation_suppressed_until_confirmation | intent_kind={mapping.IntentKind} object_category='{mapping.TaskIntent.ObjectCategory}' place_target='{mapping.TaskIntent.PlaceTargetId}'");
                return CompleteRoute(EmitAndBuild(
                    VoiceAutonomyCommandRoutingStatus.PendingConfirmation,
                    "pending_confirmation",
                    normalization,
                    mapping,
                    null,
                    feedback,
                    mapping.TaskIntent), false);
            }

            return SubmitExecutableIntent(mapping.TaskIntent, normalization, mapping);
        }

        public VoiceAutonomyCommandRoutingResult CheckPendingConfirmationTimeout()
        {
            if (IsExperimentPaused())
            {
                return null;
            }

            return TryExpirePendingConfirmation(out VoiceAutonomyCommandRoutingResult expired)
                ? expired
                : null;
        }

        private bool IsExperimentPaused()
        {
            return _isExperimentPaused != null && _isExperimentPaused();
        }

        private VoiceAutonomyCommandRoutingResult RejectCommandWhileExperimentPaused()
        {
            const string reason = "experiment_paused_command_rejected";
            Log($"voice_command_rejected_while_paused | reason={reason} raw='{_currentTranscript}'");
            EmitExperimentEvent(
                "voice_command_rejected_while_paused",
                new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["rejection_reason"] = reason,
                    ["request_source"] = "voice_command",
                    ["transcript"] = _currentTranscript,
                    ["voice_interaction_id"] = _currentVoiceInteractionId,
                    ["bridge_invoked"] = false,
                    ["pending_confirmation_preserved"] = _confirmationManager.HasPendingIntent
                });
            // Deliberately no feedback message: a command heard while the global
            // pause modal is open must neither reach the robot nor be queued/spoken.
            return CompleteRoute(NotSubmitted(reason, null, null, null), false);
        }

        private VoiceAutonomyCommandRoutingResult AcceptPendingConfirmation()
        {
            if (!_confirmationManager.TryAccept(_getTimeSeconds(), out MultimodalTaskIntent intent, out VoiceCommandIntentMappingResult mapping))
            {
                Log("confirmation_without_pending");
                VoiceUserFeedbackMessage feedback = BuildFeedback(
                    VoiceUserFeedbackType.NoPendingConfirmation,
                    "No hay ninguna orden pendiente de confirmacion.",
                    "no_pending_confirmation");
                return CompleteRoute(EmitAndBuild(
                    VoiceAutonomyCommandRoutingStatus.NoPendingConfirmation,
                    "no_pending_confirmation",
                    null,
                    null,
                    null,
                    feedback,
                    null), false);
            }

            Log("pending_intent_confirmed");
            string confirmedVoiceInteractionId = _pendingVoiceInteractionId;
            _pendingVoiceInteractionId = string.Empty;
            Emit(BuildFeedback(
                VoiceUserFeedbackType.ConfirmationAccepted,
                "Orden confirmada.",
                "confirmation_accepted",
                intent,
                mapping));

            return SubmitExecutableIntent(
                intent,
                mapping?.Normalization,
                mapping,
                VoiceAutonomyCommandRoutingStatus.ConfirmationAccepted,
                confirmedVoiceInteractionId);
        }

        private VoiceAutonomyCommandRoutingResult CancelPendingConfirmation()
        {
            if (!_confirmationManager.TryCancel(out MultimodalTaskIntent intent, out VoiceCommandIntentMappingResult mapping))
            {
                Log("confirmation_without_pending");
                VoiceUserFeedbackMessage noPending = BuildFeedback(
                    VoiceUserFeedbackType.NoPendingConfirmation,
                    "No hay ninguna orden pendiente de confirmacion.",
                    "no_pending_confirmation");
                return CompleteRoute(EmitAndBuild(
                    VoiceAutonomyCommandRoutingStatus.NoPendingConfirmation,
                    "no_pending_confirmation",
                    null,
                    null,
                    null,
                    noPending,
                    null), false);
            }

            Log("pending_intent_cancelled");
            string canceledVoiceInteractionId = _pendingVoiceInteractionId;
            _pendingVoiceInteractionId = string.Empty;
            VoiceUserFeedbackMessage feedback = BuildFeedback(
                VoiceUserFeedbackType.ConfirmationCanceled,
                "Orden cancelada.",
                "confirmation_canceled",
                intent,
                mapping);
            return CompleteRoute(EmitAndBuild(
                VoiceAutonomyCommandRoutingStatus.ConfirmationCanceled,
                "confirmation_canceled",
                mapping?.Normalization,
                mapping,
                null,
                feedback,
                null), false, canceledVoiceInteractionId);
        }

        private bool TryExpirePendingConfirmation(out VoiceAutonomyCommandRoutingResult result)
        {
            if (!_confirmationManager.TryExpire(_getTimeSeconds(), out MultimodalTaskIntent intent, out VoiceCommandIntentMappingResult mapping))
            {
                result = null;
                return false;
            }

            Log("pending_intent_expired");
            string expiredVoiceInteractionId = _pendingVoiceInteractionId;
            _pendingVoiceInteractionId = string.Empty;
            VoiceUserFeedbackMessage feedback = BuildFeedback(
                VoiceUserFeedbackType.ConfirmationExpired,
                "La confirmacion ha expirado.",
                "confirmation_expired",
                intent,
                mapping);
            result = CompleteRoute(EmitAndBuild(
                VoiceAutonomyCommandRoutingStatus.ConfirmationExpired,
                "confirmation_expired",
                mapping?.Normalization,
                mapping,
                null,
                feedback,
                null), false, expiredVoiceInteractionId);
            return true;
        }

        private VoiceAutonomyCommandRoutingResult SubmitExecutableIntent(
            MultimodalTaskIntent intent,
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping,
            VoiceAutonomyCommandRoutingStatus acceptedStatus = VoiceAutonomyCommandRoutingStatus.Submitted,
            string overrideVoiceInteractionId = "")
        {
            if (!TryPassExecutionGate(intent, out VoiceCommandExecutionGateResult gateResult))
            {
                VoiceUserFeedbackMessage blockedFeedback = BuildFeedback(
                    VoiceUserFeedbackType.CommandBlockedByCondition,
                    string.IsNullOrWhiteSpace(gateResult.FeedbackText) ? "Orden bloqueada por la condicion experimental." : gateResult.FeedbackText,
                    gateResult.Reason,
                    intent,
                    mapping);
                return CompleteRoute(NotSubmitted(gateResult.Reason, normalization, mapping, blockedFeedback), false, overrideVoiceInteractionId);
            }

            if (_submitIntent == null)
            {
                const string reason = "bridge_submitter_missing";
                Warn(reason);
                return CompleteRoute(NotSubmitted(reason, normalization, mapping, null), false, overrideVoiceInteractionId);
            }

            Log($"submitting_pick_and_place_to_bridge | target_id='{intent.TargetId}' object_category='{intent.ObjectCategory}' place_target='{intent.PlaceTargetId}' source='{intent.Source}'");
            RegisterTraceIntent(intent, normalization, mapping);
            EmitP40VoiceLifecycle("bridge_submitted", normalization, mapping, intent, "submitted", "bridge_submitter_invoked", true);
            bool accepted = _submitIntent(intent);
            if (!accepted)
            {
                string reason = ResolveSubmitRejectionReason();
                Warn(reason);
                if (_currentTraceMetadata != null)
                {
                    _currentTraceMetadata.LastDecision = "rejected";
                    _currentTraceMetadata.LastReason = reason;
                    P40TraceContext.RecordLastVoiceCommand(_currentTraceMetadata);
                }
                VoiceUserFeedbackMessage rejectedFeedback = BuildFeedback(
                    VoiceUserFeedbackType.CommandBlockedByCondition,
                    string.Equals(reason, "task_in_progress", StringComparison.OrdinalIgnoreCase)
                        ? "El robot ya esta ejecutando una tarea. Espera a que termine."
                        : "No se ha podido enviar la orden al robot.",
                    reason,
                    intent,
                    mapping);
                return CompleteRoute(new VoiceAutonomyCommandRoutingResult(
                    VoiceAutonomyCommandRoutingStatus.BridgeRejected,
                    reason,
                    normalization,
                    mapping,
                    intent,
                    Emit(rejectedFeedback)), true, overrideVoiceInteractionId);
            }

            VoiceUserFeedbackMessage feedback = BuildFeedback(
                VoiceUserFeedbackType.CommandExecuted,
                BuildExecutedText(intent),
                acceptedStatus == VoiceAutonomyCommandRoutingStatus.ConfirmationAccepted ? "submitted_after_confirmation" : "submitted_pick_and_place",
                intent,
                mapping);
            VoiceAutonomyCommandRoutingStatus status = acceptedStatus == VoiceAutonomyCommandRoutingStatus.ConfirmationAccepted
                ? VoiceAutonomyCommandRoutingStatus.ConfirmationAccepted
                : VoiceAutonomyCommandRoutingStatus.Submitted;
            return CompleteRoute(new VoiceAutonomyCommandRoutingResult(
                status,
                feedback.Reason,
                normalization,
                mapping,
                intent,
                Emit(feedback)), true, overrideVoiceInteractionId);
        }

        private void UpdateTraceFromNormalization(VoiceCommandNormalizationResult normalization)
        {
            if (_currentTraceMetadata == null || normalization == null)
            {
                return;
            }

            _currentTraceMetadata.RawTranscript = normalization.RawTranscript;
            _currentTraceMetadata.NormalizedText = normalization.NormalizedText;
            _currentTraceMetadata.TargetAlias = normalization.ObjectLabel;
            _currentTraceMetadata.RequestedDestination = normalization.DestinationLabel;
        }

        private void UpdateTraceFromMapping(VoiceCommandIntentMappingResult mapping)
        {
            if (_currentTraceMetadata == null || mapping == null)
            {
                return;
            }

            _currentTraceMetadata.IntentKind = mapping.IntentKind.ToString();
            _currentTraceMetadata.ResolvedDestination = mapping.TaskIntent?.PlaceTargetId ?? _currentTraceMetadata.ResolvedDestination;
            _currentTraceMetadata.SubmittedDestination = mapping.TaskIntent?.PlaceTargetId ?? _currentTraceMetadata.SubmittedDestination;
        }

        private void RegisterTraceIntent(
            MultimodalTaskIntent intent,
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping)
        {
            if (intent == null)
            {
                return;
            }

            _currentTraceMetadata ??= new P40TraceMetadata
            {
                RequestId = P40TraceContext.NextRequestId("voice_command"),
                VoiceInteractionId = _currentVoiceInteractionId,
                Producer = "voice_command",
                CausalParentId = _currentVoiceInteractionId
            };
            UpdateTraceFromNormalization(normalization);
            UpdateTraceFromMapping(mapping);
            _currentTraceMetadata.SubmittedDestination = intent.PlaceTargetId;
            _currentTraceMetadata.ResolvedDestination = intent.PlaceTargetId;
            P40TraceContext.RegisterIntent(intent, _currentTraceMetadata);
        }

        private bool TryPassExecutionGate(MultimodalTaskIntent intent, out VoiceCommandExecutionGateResult gateResult)
        {
            if (_executionGate == null)
            {
                gateResult = VoiceCommandExecutionGateResult.Allow("no_execution_gate_configured");
                EmitVoiceCommandExecutionGateDiagnostic(intent, gateResult, "allow");
                return true;
            }

            gateResult = _executionGate(intent);
            if (gateResult.Allowed)
            {
                EmitVoiceCommandExecutionGateDiagnostic(intent, gateResult, "allow");
                EmitVoiceCommandConditionGate("voice_command_allowed_by_condition", gateResult.Reason, true, intent);
                return true;
            }

            EmitVoiceCommandExecutionGateDiagnostic(intent, gateResult, "reject");
            EmitVoiceCommandConditionGate("voice_command_blocked_by_condition", gateResult.Reason, false, intent);
            Warn($"voice_command_blocked_by_condition | reason={gateResult.Reason}");
            return false;
        }

        private bool ShouldRejectPickAndPlaceForVoiceStopLatch(MultimodalTaskIntent intent)
        {
            return intent != null &&
                   intent.TaskFlow == AutonomousTaskFlow.PickAndPlace &&
                   IsVoiceStopLatched();
        }

        private bool IsVoiceStopLatched()
        {
            if (_isVoiceStopLatched == null)
            {
                return false;
            }

            try
            {
                return _isVoiceStopLatched();
            }
            catch (Exception exception)
            {
                Warn($"voice_stop_latch_state_query_failed | {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        private VoiceAutonomyCommandRoutingResult RejectPickAndPlaceWhileStopped(
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping,
            MultimodalTaskIntent intent)
        {
            const string reason = "voice_stop_latched_pick_and_place_rejected";
            Warn($"{reason} | bridge_invoked=false pending_created=false bt_reset_requested=false new_task_started=false target_id='{intent?.TargetId ?? string.Empty}' destination='{intent?.PlaceTargetId ?? string.Empty}'");
            EmitP40VoiceLifecycle("rejected", normalization, mapping, intent, "rejected", reason, false);
            EmitPickAndPlaceRejectedByStopLatch(normalization, mapping, intent);
            VoiceUserFeedbackMessage feedback = BuildFeedback(
                VoiceUserFeedbackType.CommandBlockedByCondition,
                "Estoy detenido. Di 'contin\u00faa' o 'retoma la tarea' para seguir.",
                reason,
                intent,
                mapping);
            return CompleteRoute(NotSubmitted(reason, normalization, mapping, feedback), false);
        }

        private VoiceAutonomyCommandRoutingResult TryApplyStopCommand(
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping)
        {
            if (_applyStopCommand == null)
            {
                const string unavailableReason = "stop_intent_has_no_operational_control_route";
                Warn(unavailableReason);
                EmitStopCommandLifecycle("rejected", unavailableReason, false, false);
                return CompleteRoute(NotSubmitted(unavailableReason, normalization, mapping, BuildNonExecutableFeedback(normalization, mapping, unavailableReason)), false);
            }

            VoiceStopCommandResult stopResult = _applyStopCommand(mapping, normalization) ??
                VoiceStopCommandResult.Reject("stop_command_handler_returned_null");
            string reason = string.IsNullOrWhiteSpace(stopResult.Reason)
                ? (stopResult.Accepted ? "stop_command_accepted" : "stop_command_rejected")
                : stopResult.Reason;
            string feedbackText = !string.IsNullOrWhiteSpace(stopResult.FeedbackText)
                ? stopResult.FeedbackText
                : stopResult.Accepted
                    ? "Orden recibida: detengo la tarea actual."
                    : "No puedo detener la tarea en este estado.";
            VoiceUserFeedbackType feedbackType = stopResult.Accepted
                ? VoiceUserFeedbackType.CommandExecuted
                : VoiceUserFeedbackType.StopUnavailable;

            EmitStopCommandLifecycle(stopResult.Accepted ? "accepted" : "rejected", reason, stopResult.Accepted, false);
            VoiceUserFeedbackMessage feedback = BuildFeedback(feedbackType, feedbackText, reason, null, mapping);
            return CompleteRoute(EmitAndBuild(
                stopResult.Accepted ? VoiceAutonomyCommandRoutingStatus.Submitted : VoiceAutonomyCommandRoutingStatus.NotSubmitted,
                reason,
                normalization,
                mapping,
                null,
                feedback,
                null), false);
        }

        private VoiceAutonomyCommandRoutingResult TryApplyResumeCommand(
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping)
        {
            if (_applyResumeCommand == null)
            {
                const string unavailableReason = "resume_intent_has_no_operational_control_route";
                Warn(unavailableReason);
                EmitResumeCommandLifecycle("rejected", unavailableReason, false, false);
                return CompleteRoute(NotSubmitted(unavailableReason, normalization, mapping, BuildNonExecutableFeedback(normalization, mapping, unavailableReason)), false);
            }

            VoiceResumeCommandResult resumeResult = _applyResumeCommand(mapping, normalization) ??
                VoiceResumeCommandResult.Reject("resume_command_handler_returned_null");
            string reason = string.IsNullOrWhiteSpace(resumeResult.Reason)
                ? (resumeResult.Accepted ? "resume_command_accepted" : "resume_command_rejected")
                : resumeResult.Reason;
            string feedbackText = !string.IsNullOrWhiteSpace(resumeResult.FeedbackText)
                ? resumeResult.FeedbackText
                : resumeResult.Accepted
                    ? "Orden recibida: reanudo la tarea detenida."
                    : DefaultResumeRejectionFeedback(reason);
            VoiceUserFeedbackType feedbackType = resumeResult.Accepted
                ? VoiceUserFeedbackType.CommandExecuted
                : VoiceUserFeedbackType.ResumeUnavailable;

            EmitResumeCommandLifecycle(resumeResult.Accepted ? "accepted" : "rejected", reason, resumeResult.Accepted, false);
            VoiceUserFeedbackMessage feedback = BuildFeedback(feedbackType, feedbackText, reason, null, mapping);
            return CompleteRoute(EmitAndBuild(
                resumeResult.Accepted ? VoiceAutonomyCommandRoutingStatus.Submitted : VoiceAutonomyCommandRoutingStatus.NotSubmitted,
                reason,
                normalization,
                mapping,
                null,
                feedback,
                null), false);
        }

        private static string DefaultResumeRejectionFeedback(string reason)
        {
            return string.Equals(reason, "no_stopped_task_to_resume", StringComparison.OrdinalIgnoreCase)
                ? "No hay ninguna tarea detenida que pueda reanudar."
                : "No puedo reanudar la tarea detenida porque el estado ya no es seguro.";
        }

        private VoiceAutonomyCommandRoutingResult NotSubmitted(
            string reason,
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping,
            VoiceUserFeedbackMessage feedback)
        {
            return new VoiceAutonomyCommandRoutingResult(
                VoiceAutonomyCommandRoutingStatus.NotSubmitted,
                reason,
                normalization,
                mapping,
                null,
                Emit(feedback));
        }

        private VoiceUserFeedbackMessage BuildNonExecutableFeedback(
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping,
            string reason)
        {
            if (normalization == null || normalization.Status == VoiceCommandRecognitionStatus.Unrecognized)
            {
                return BuildFeedback(VoiceUserFeedbackType.CommandUnrecognized, "Comando no reconocido.", reason, null, mapping);
            }

            if (normalization.Status == VoiceCommandRecognitionStatus.Ambiguous || mapping?.Status == VoiceCommandIntentMappingStatus.Ambiguous)
            {
                if (mapping != null && string.Equals(mapping.Explanation, "multiple_matching_boxes", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildFeedback(VoiceUserFeedbackType.CommandAmbiguous, "Hay varias cajas de ese tipo. Indica el codigo de la caja.", reason, null, mapping);
                }

                return BuildFeedback(VoiceUserFeedbackType.CommandAmbiguous, "No he entendido la orden con suficiente seguridad.", reason, null, mapping);
            }

            if (mapping != null &&
                (string.Equals(mapping.Explanation, "target_alias_not_found", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mapping.Explanation, "category_not_found", StringComparison.OrdinalIgnoreCase)))
            {
                return BuildFeedback(VoiceUserFeedbackType.CommandUnrecognized, "No encuentro esa caja en la ronda activa.", reason, null, mapping);
            }

            if (mapping != null && mapping.Status == VoiceCommandIntentMappingStatus.Incomplete)
            {
                return BuildFeedback(VoiceUserFeedbackType.CommandIncomplete, "Falta informacion para completar la orden.", reason, null, mapping);
            }

            if (mapping != null && mapping.IntentKind == VoiceCommandIntentKind.NearestAmbiguity)
            {
                return BuildFeedback(VoiceUserFeedbackType.CommandAmbiguous, "Mas cercana a ti o mas cercana a mi?", reason, null, mapping);
            }

            if (mapping != null && mapping.IntentKind == VoiceCommandIntentKind.CancelPendingOrder)
            {
                return BuildFeedback(VoiceUserFeedbackType.CommandBlockedByCondition, "Cancelo la orden pendiente.", reason, null, mapping);
            }

            if (mapping != null && mapping.IntentKind == VoiceCommandIntentKind.Continue)
            {
                return BuildFeedback(VoiceUserFeedbackType.CommandBlockedByCondition, "Continuo con la tarea.", reason, null, mapping);
            }

            if (mapping != null && mapping.IntentKind == VoiceCommandIntentKind.Stop)
            {
                return BuildFeedback(VoiceUserFeedbackType.StopUnavailable, "La parada por voz todavia no esta disponible en este modo.", reason, null, mapping);
            }

            if (mapping != null && mapping.IntentKind == VoiceCommandIntentKind.Resume)
            {
                return BuildFeedback(VoiceUserFeedbackType.ResumeUnavailable, DefaultResumeRejectionFeedback(reason), reason, null, mapping);
            }

            if (mapping != null && mapping.IntentKind == VoiceCommandIntentKind.Pick)
            {
                return BuildFeedback(VoiceUserFeedbackType.PickOnlyUnavailable, "La orden de solo recoger todavia no esta disponible en este flujo.", reason, mapping.TaskIntent, mapping);
            }

            return BuildFeedback(VoiceUserFeedbackType.CommandUnrecognized, "Comando no reconocido.", reason, null, mapping);
        }

        private VoiceUserFeedbackMessage BuildConditionBlockedFeedback(
            VoiceCommandExecutionGateResult gateResult,
            VoiceCommandIntentMappingResult mapping)
        {
            string feedbackText = string.IsNullOrWhiteSpace(gateResult.FeedbackText)
                ? "Orden bloqueada por la condicion experimental."
                : gateResult.FeedbackText;
            return BuildFeedback(
                VoiceUserFeedbackType.CommandBlockedByCondition,
                feedbackText,
                gateResult.Reason,
                null,
                mapping);
        }

        private VoiceAutonomyCommandRoutingResult EmitAndBuild(
            VoiceAutonomyCommandRoutingStatus status,
            string reason,
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping,
            MultimodalTaskIntent submittedIntent,
            VoiceUserFeedbackMessage feedback,
            MultimodalTaskIntent pendingIntent)
        {
            return new VoiceAutonomyCommandRoutingResult(
                status,
                reason,
                normalization,
                mapping,
                submittedIntent,
                Emit(feedback),
                pendingIntent);
        }

        private VoiceUserFeedbackMessage BuildFeedback(
            VoiceUserFeedbackType type,
            string text,
            string reason,
            MultimodalTaskIntent intent = null,
            VoiceCommandIntentMappingResult mapping = null)
        {
            return new VoiceUserFeedbackMessage(type, text, reason, intent, mapping);
        }

        private VoiceUserFeedbackMessage Emit(VoiceUserFeedbackMessage feedback)
        {
            if (feedback != null)
            {
                _feedbackSink?.Emit(feedback);
                EmitFeedbackEvent(feedback);
                EmitRobotFeedback(feedback);
            }

            return feedback;
        }

        private void EmitRobotFeedback(VoiceUserFeedbackMessage feedback)
        {
            RobotVoiceFeedbackContext context = _robotFeedbackTemplateBuilder.BuildContext(
                feedback,
                feedback.Mapping?.Normalization,
                feedback.Mapping,
                feedback.Intent ?? feedback.Mapping?.TaskIntent);
            RobotVoiceFeedbackMessage robotMessage = _robotFeedbackTemplateBuilder.Build(context);
            _robotFeedbackSink?.Emit(robotMessage);
            EmitRobotFeedbackEvent(robotMessage);
        }

        private VoiceAutonomyCommandRoutingResult CompleteRoute(
            VoiceAutonomyCommandRoutingResult result,
            bool bridgeInvoked,
            string overrideVoiceInteractionId = "")
        {
            if (result == null)
            {
                return null;
            }

            switch (result.Status)
            {
                case VoiceAutonomyCommandRoutingStatus.PendingConfirmation:
                    EmitConfirmationPending(result, overrideVoiceInteractionId);
                    break;
                case VoiceAutonomyCommandRoutingStatus.ConfirmationAccepted:
                    EmitConfirmationAccepted(result, bridgeInvoked, overrideVoiceInteractionId);
                    break;
                case VoiceAutonomyCommandRoutingStatus.ConfirmationCanceled:
                    EmitConfirmationCancelled(result, overrideVoiceInteractionId);
                    break;
                case VoiceAutonomyCommandRoutingStatus.ConfirmationExpired:
                    EmitConfirmationExpired(result, overrideVoiceInteractionId);
                    break;
                case VoiceAutonomyCommandRoutingStatus.NoPendingConfirmation:
                    EmitConfirmationWithoutPending(result, overrideVoiceInteractionId);
                    break;
                case VoiceAutonomyCommandRoutingStatus.NotSubmitted:
                    EmitCommandRejected(result, overrideVoiceInteractionId);
                    break;
                case VoiceAutonomyCommandRoutingStatus.BridgeRejected:
                    EmitCommandRejected(result, overrideVoiceInteractionId);
                    if (string.Equals(result.Reason, "task_in_progress", StringComparison.OrdinalIgnoreCase))
                    {
                        EmitTaskInProgressRejected(result, overrideVoiceInteractionId);
                    }

                    break;
            }

            EmitRouteResult(result, bridgeInvoked, overrideVoiceInteractionId);
            return result;
        }

        private void EmitVoiceCommandNormalized(VoiceCommandNormalizationResult normalization)
        {
            if (normalization == null)
            {
                return;
            }

            Dictionary<string, object> payload = BuildNormalizationPayload(normalization);
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent("voice_command_normalized", payload);
        }

        private void EmitVoiceIntentMapped(VoiceCommandIntentMappingResult mapping)
        {
            if (mapping == null)
            {
                return;
            }

            Dictionary<string, object> payload = BuildMappingPayload(mapping);
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent("voice_intent_mapped", payload);
        }

        private void EmitRouteResult(VoiceAutonomyCommandRoutingResult result, bool bridgeInvoked, string overrideVoiceInteractionId)
        {
            Dictionary<string, object> payload = BuildRoutingPayload(result, bridgeInvoked);
            AddVoiceInteractionPayload(payload, overrideVoiceInteractionId);
            EmitExperimentEvent("voice_route_result", payload);
            EmitExperimentEvent("voice_command_routing_diagnostic", BuildDiagnosticRoutingPayload(result, bridgeInvoked, overrideVoiceInteractionId));
        }

        private void EmitP40VoiceLifecycle(
            string phase,
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping,
            MultimodalTaskIntent intent,
            string decision,
            string reason,
            bool bridgeSubmitted)
        {
            Dictionary<string, object> payload = P40TraceContext.ToPayload(_currentTraceMetadata);
            payload["phase"] = phase ?? string.Empty;
            payload["transcript"] = normalization?.RawTranscript ?? _currentTranscript;
            payload["raw_transcript"] = normalization?.RawTranscript ?? _currentTranscript;
            payload["normalized_text"] = normalization?.NormalizedText ?? _currentTraceMetadata?.NormalizedText ?? string.Empty;
            payload["intent"] = mapping?.IntentKind.ToString() ?? _currentTraceMetadata?.IntentKind ?? string.Empty;
            payload["intent_kind"] = mapping?.IntentKind.ToString() ?? _currentTraceMetadata?.IntentKind ?? string.Empty;
            payload["target_alias"] = normalization?.ObjectLabel ?? _currentTraceMetadata?.TargetAlias ?? string.Empty;
            payload["target_id"] = intent?.TargetId ?? mapping?.TaskIntent?.TargetId ?? string.Empty;
            payload["requested_destination"] = normalization?.DestinationLabel ?? _currentTraceMetadata?.RequestedDestination ?? string.Empty;
            payload["symbolic_destination"] = normalization?.DestinationLabel ?? _currentTraceMetadata?.RequestedDestination ?? string.Empty;
            payload["resolved_destination"] = intent?.PlaceTargetId ?? mapping?.TaskIntent?.PlaceTargetId ?? _currentTraceMetadata?.ResolvedDestination ?? string.Empty;
            payload["submitted_destination"] = bridgeSubmitted
                ? intent?.PlaceTargetId ?? string.Empty
                : _currentTraceMetadata?.SubmittedDestination ?? string.Empty;
            payload["destination"] = payload["resolved_destination"];
            payload["bridge_submitted"] = bridgeSubmitted;
            payload["decision"] = decision ?? string.Empty;
            payload["reason"] = reason ?? string.Empty;
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent("p40_trace_voice_command_lifecycle", payload);
        }

        private void EmitVoiceCommandReceivedDiagnostic()
        {
            Dictionary<string, object> payload = new()
            {
                ["raw_transcript"] = _currentTranscript,
                ["transcript"] = _currentTranscript,
                ["decision"] = "received",
                ["reason"] = "transcript_entered_router"
            };
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent("voice_command_received_diagnostic", payload);
        }

        private void EmitFeedbackEvent(VoiceUserFeedbackMessage feedback)
        {
            Dictionary<string, object> payload = new()
            {
                ["feedback_type"] = feedback.Type.ToString(),
                ["message"] = feedback.Text,
                ["related_status"] = feedback.Mapping?.Status.ToString() ?? string.Empty,
                ["reason"] = feedback.Reason
            };
            AddIntentPayload(payload, feedback.Intent ?? feedback.Mapping?.TaskIntent);
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent("voice_feedback_emitted", payload);
        }

        private void EmitRobotFeedbackEvent(RobotVoiceFeedbackMessage feedback)
        {
            if (feedback == null)
            {
                return;
            }

            RobotVoiceFeedbackContext context = feedback.Context;
            Dictionary<string, object> payload = new()
            {
                ["feedback_kind"] = feedback.Kind.ToString(),
                ["robot_feedback_reason"] = feedback.Kind.ToString(),
                ["feedback_text"] = feedback.Text,
                ["raw_transcript"] = context?.RawTranscript ?? string.Empty,
                ["normalized_text"] = context?.NormalizedText ?? string.Empty,
                ["intent_kind"] = context?.IntentKind.ToString() ?? string.Empty,
                ["target_alias"] = context?.TargetAlias ?? string.Empty,
                ["target_id"] = context?.TargetId ?? string.Empty,
                ["destination"] = context?.Destination ?? string.Empty,
                ["reason"] = context?.Reason ?? string.Empty,
                ["condition_id"] = context?.ConditionId ?? string.Empty,
                ["trial_id"] = context?.TrialId ?? string.Empty,
                ["round_id"] = context?.RoundId ?? string.Empty,
                ["accepted"] = context?.Accepted ?? false,
                ["feedback_outcome"] = RobotVoiceFeedbackDiagnosticRecord.ResolveOutcome(feedback.Kind, context?.Accepted ?? false)
            };
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent("voice_feedback_spoken", payload);
        }

        private void EmitConfirmationPending(VoiceAutonomyCommandRoutingResult result, string overrideVoiceInteractionId)
        {
            Dictionary<string, object> payload = BuildMappingPayload(result.Mapping);
            payload["intent_kind"] = result.Mapping?.IntentKind.ToString() ?? string.Empty;
            payload["expires_in_seconds"] = _confirmationPolicy.TimeoutSeconds;
            AddIntentPayload(payload, result.PendingIntent);
            AddVoiceInteractionPayload(payload, overrideVoiceInteractionId);
            EmitExperimentEvent("voice_confirmation_pending", payload);
        }

        private void EmitConfirmationAccepted(VoiceAutonomyCommandRoutingResult result, bool bridgeInvoked, string overrideVoiceInteractionId)
        {
            Dictionary<string, object> payload = BuildMappingPayload(result.Mapping);
            payload["intent_kind"] = result.Mapping?.IntentKind.ToString() ?? string.Empty;
            payload["submitted"] = result.SubmittedIntent != null;
            payload["bridge_invoked"] = bridgeInvoked;
            AddIntentPayload(payload, result.SubmittedIntent);
            AddVoiceInteractionPayload(payload, overrideVoiceInteractionId);
            payload["voice_response_interaction_id"] = _currentVoiceInteractionId;
            EmitExperimentEvent("voice_confirmation_accepted", payload);
        }

        private void EmitConfirmationCancelled(VoiceAutonomyCommandRoutingResult result, string overrideVoiceInteractionId)
        {
            Dictionary<string, object> payload = BuildMappingPayload(result.Mapping);
            payload["intent_kind"] = result.Mapping?.IntentKind.ToString() ?? string.Empty;
            payload["submitted"] = false;
            AddIntentPayload(payload, result.Mapping?.TaskIntent);
            AddVoiceInteractionPayload(payload, overrideVoiceInteractionId);
            payload["voice_response_interaction_id"] = _currentVoiceInteractionId;
            EmitExperimentEvent("voice_confirmation_cancelled", payload);
        }

        private void EmitConfirmationExpired(VoiceAutonomyCommandRoutingResult result, string overrideVoiceInteractionId)
        {
            Dictionary<string, object> payload = BuildMappingPayload(result.Mapping);
            payload["intent_kind"] = result.Mapping?.IntentKind.ToString() ?? string.Empty;
            payload["submitted"] = false;
            AddIntentPayload(payload, result.Mapping?.TaskIntent);
            AddVoiceInteractionPayload(payload, overrideVoiceInteractionId);
            EmitExperimentEvent("voice_confirmation_expired", payload);
        }

        private void EmitConfirmationWithoutPending(VoiceAutonomyCommandRoutingResult result, string overrideVoiceInteractionId)
        {
            Dictionary<string, object> payload = new()
            {
                ["transcript"] = result.Normalization?.RawTranscript ?? _currentTranscript,
                ["submitted"] = false,
                ["reason"] = result.Reason
            };
            AddVoiceInteractionPayload(payload, overrideVoiceInteractionId);
            EmitExperimentEvent("voice_confirmation_without_pending", payload);
        }

        private void EmitCommandRejected(VoiceAutonomyCommandRoutingResult result, string overrideVoiceInteractionId)
        {
            Dictionary<string, object> payload = BuildMappingPayload(result.Mapping);
            if (result.Normalization != null)
            {
                AddNormalizationPayload(payload, result.Normalization);
            }

            payload["rejection_type"] = ResolveRejectionType(result);
            payload["intent_kind"] = result.Mapping?.IntentKind.ToString() ?? string.Empty;
            payload["reason"] = result.Reason;
            payload["bridge_invoked"] = false;
            AddVoiceInteractionPayload(payload, overrideVoiceInteractionId);
            EmitExperimentEvent("voice_command_rejected", payload);
            EmitExperimentEvent("voice_command_rejected_diagnostic", new Dictionary<string, object>(payload)
            {
                ["decision"] = "rejected",
                ["routing_status"] = result.Status.ToString()
            });
        }

        private void EmitVoiceCommandConditionGate(string eventType, string reason, bool allowed, MultimodalTaskIntent intent)
        {
            Dictionary<string, object> payload = new()
            {
                ["allowed"] = allowed,
                ["reason"] = reason ?? string.Empty,
                ["bridge_invoked"] = false
            };
            AddIntentPayload(payload, intent);
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent(eventType, payload);
        }

        private void EmitVoiceCommandExecutionGateDiagnostic(
            MultimodalTaskIntent intent,
            VoiceCommandExecutionGateResult gateResult,
            string decision)
        {
            Dictionary<string, object> payload = new()
            {
                ["allowed"] = gateResult.Allowed,
                ["reason"] = gateResult.Reason ?? string.Empty,
                ["feedback_text"] = gateResult.FeedbackText ?? string.Empty,
                ["decision"] = decision ?? string.Empty,
                ["bridge_invoked"] = false
            };
            AddIntentPayload(payload, intent);
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent("voice_command_execution_gate_diagnostic", payload);
        }

        private void EmitVoiceCommandControlPathDiagnostic(
            VoiceCommandIntentMappingResult mapping,
            VoiceCommandNormalizationResult normalization)
        {
            if (mapping == null ||
                (mapping.IntentKind != VoiceCommandIntentKind.Stop &&
                 mapping.IntentKind != VoiceCommandIntentKind.Resume))
            {
                return;
            }

            Dictionary<string, object> payload = BuildMappingPayload(mapping);
            AddNormalizationPayload(payload, normalization);
            bool hasOperationalRoute = mapping.IntentKind == VoiceCommandIntentKind.Stop
                ? _applyStopCommand != null
                : _applyResumeCommand != null;
            string controlName = mapping.IntentKind == VoiceCommandIntentKind.Stop ? "stop" : "resume";
            payload["decision"] = hasOperationalRoute ? "control_path_available" : "control_path_unavailable";
            payload["reason"] = hasOperationalRoute
                ? $"{controlName}_intent_has_operational_control_route"
                : $"{controlName}_intent_has_no_operational_control_route";
            payload["bridge_invoked"] = false;
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent("voice_command_control_path_diagnostic", payload);
        }

        private void EmitStopCommandLifecycle(string decision, string reason, bool accepted, bool bridgeInvoked)
        {
            Dictionary<string, object> payload = new()
            {
                ["intent_kind"] = VoiceCommandIntentKind.Stop.ToString(),
                ["decision"] = decision ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["accepted"] = accepted,
                ["bridge_invoked"] = bridgeInvoked
            };
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent("voice_stop_command_lifecycle", payload);
        }

        private void EmitResumeCommandLifecycle(string decision, string reason, bool accepted, bool bridgeInvoked)
        {
            Dictionary<string, object> payload = new()
            {
                ["intent_kind"] = VoiceCommandIntentKind.Resume.ToString(),
                ["decision"] = decision ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["accepted"] = accepted,
                ["bridge_invoked"] = bridgeInvoked
            };
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent("voice_resume_command_lifecycle", payload);
        }

        private void EmitPickAndPlaceRejectedByStopLatch(
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping,
            MultimodalTaskIntent intent)
        {
            Dictionary<string, object> payload = BuildMappingPayload(mapping);
            AddNormalizationPayload(payload, normalization);
            AddIntentPayload(payload, intent);
            payload["reason"] = "voice_stop_latched_pick_and_place_rejected";
            payload["rejection_type"] = "stop_latched";
            payload["decision"] = "rejected";
            payload["voice_stop_latched"] = true;
            payload["stop_latch_active"] = true;
            payload["bridge_invoked"] = false;
            payload["pending_created"] = false;
            payload["pending_confirmation_created"] = false;
            payload["p40b_pending_created"] = false;
            payload["p40a_replacement_requested"] = false;
            payload["bt_reset_requested"] = false;
            payload["new_task_started"] = false;
            payload["navigation_manipulation_activated"] = false;
            AddVoiceInteractionPayload(payload);
            EmitExperimentEvent("voice_pick_and_place_rejected_by_stop_latch", payload);
        }

        private void EmitTaskInProgressRejected(VoiceAutonomyCommandRoutingResult result, string overrideVoiceInteractionId)
        {
            Dictionary<string, object> payload = BuildMappingPayload(result.Mapping);
            if (result.Normalization != null)
            {
                AddNormalizationPayload(payload, result.Normalization);
            }

            payload["reason"] = "task_in_progress";
            payload["bridge_invoked"] = true;
            AddIntentPayload(payload, result.SubmittedIntent ?? result.Mapping?.TaskIntent);
            AddVoiceInteractionPayload(payload, overrideVoiceInteractionId);
            EmitExperimentEvent("voice_command_rejected_task_in_progress", payload);
        }

        private Dictionary<string, object> BuildRoutingPayload(VoiceAutonomyCommandRoutingResult result, bool bridgeInvoked)
        {
            Dictionary<string, object> payload = BuildMappingPayload(result.Mapping);
            payload["routing_status"] = result.Status.ToString();
            payload["intent_kind"] = result.Mapping?.IntentKind.ToString() ?? string.Empty;
            payload["submitted"] = result.SubmittedIntent != null;
            payload["requires_confirmation"] = result.Status == VoiceAutonomyCommandRoutingStatus.PendingConfirmation;
            payload["reason"] = result.Reason;
            payload["bridge_invoked"] = bridgeInvoked;
            AddIntentPayload(payload, result.SubmittedIntent ?? result.PendingIntent ?? result.Mapping?.TaskIntent);
            return payload;
        }

        private Dictionary<string, object> BuildDiagnosticRoutingPayload(
            VoiceAutonomyCommandRoutingResult result,
            bool bridgeInvoked,
            string overrideVoiceInteractionId)
        {
            Dictionary<string, object> payload = BuildRoutingPayload(result, bridgeInvoked);
            object resolvedDestination = payload.TryGetValue("destination", out object currentDestination)
                ? currentDestination
                : string.Empty;
            if (result?.Normalization != null)
            {
                AddNormalizationPayload(payload, result.Normalization);
                payload["requested_destination"] = result.Normalization.DestinationLabel;
            }

            payload["resolved_destination"] = resolvedDestination;
            payload["destination"] = resolvedDestination;
            payload["decision"] = ResolveDiagnosticDecision(result);
            AddVoiceInteractionPayload(payload, overrideVoiceInteractionId);
            return payload;
        }

        private static Dictionary<string, object> BuildNormalizationPayload(VoiceCommandNormalizationResult normalization)
        {
            Dictionary<string, object> payload = new();
            AddNormalizationPayload(payload, normalization);
            return payload;
        }

        private static Dictionary<string, object> BuildMappingPayload(VoiceCommandIntentMappingResult mapping)
        {
            Dictionary<string, object> payload = new();
            if (mapping == null)
            {
                return payload;
            }

            payload["normalized_text"] = mapping.NormalizedText;
            payload["intent_kind"] = mapping.IntentKind.ToString();
            payload["mapping_status"] = mapping.Status.ToString();
            payload["mapping_error"] = mapping.Error.ToString();
            payload["score"] = mapping.Score;
            payload["ambiguity_reason"] = mapping.AmbiguityReason;
            payload["missing_slots"] = mapping.Status == VoiceCommandIntentMappingStatus.Incomplete ? mapping.Explanation : string.Empty;
            payload["diagnostic_reason"] = mapping.Explanation;
            AddIntentPayload(payload, mapping.TaskIntent);
            return payload;
        }

        private static void AddNormalizationPayload(Dictionary<string, object> payload, VoiceCommandNormalizationResult normalization)
        {
            if (normalization == null)
            {
                return;
            }

            payload["raw_transcript"] = normalization.RawTranscript;
            payload["normalized_text"] = normalization.NormalizedText;
            payload["recognized_tokens"] = normalization.CanonicalPhrase;
            payload["normalization_status"] = normalization.Status.ToString();
            payload["diagnostic_reason"] = normalization.AmbiguityReason;
            payload["score"] = normalization.Score;
            payload["action"] = normalization.Action.ToString();
            payload["object"] = normalization.Object.ToString();
            payload["object_label"] = normalization.ObjectLabel;
            payload["destination"] = normalization.DestinationLabel;
        }

        private static void AddIntentPayload(Dictionary<string, object> payload, MultimodalTaskIntent intent)
        {
            if (intent == null)
            {
                return;
            }

            payload["task_flow"] = intent.TaskFlow.ToString();
            payload["object_selection_mode"] = intent.ObjectSelectionMode.ToString();
            payload["target"] = intent.TargetId;
            payload["target_id"] = intent.TargetId;
            payload["object_category"] = intent.ObjectCategory;
            payload["destination"] = intent.PlaceTargetId;
            payload["intent_source"] = intent.Source;
        }

        private void AddVoiceInteractionPayload(Dictionary<string, object> payload, string overrideVoiceInteractionId = "")
        {
            string voiceInteractionId = !string.IsNullOrWhiteSpace(overrideVoiceInteractionId)
                ? overrideVoiceInteractionId
                : _currentVoiceInteractionId;
            if (!string.IsNullOrWhiteSpace(voiceInteractionId))
            {
                payload["voice_interaction_id"] = voiceInteractionId;
            }
        }

        private static string ResolveRejectionType(VoiceAutonomyCommandRoutingResult result)
        {
            if (result?.Mapping?.IntentKind == VoiceCommandIntentKind.Stop)
            {
                return "unsupported_stop";
            }

            if (result?.Mapping?.IntentKind == VoiceCommandIntentKind.Resume)
            {
                return "resume_unavailable";
            }

            if (result?.Mapping?.IntentKind == VoiceCommandIntentKind.Pick)
            {
                return "unsupported_pick_only";
            }

            if (result?.Mapping?.Status == VoiceCommandIntentMappingStatus.Incomplete)
            {
                return "incomplete";
            }

            if (result?.Mapping?.Status == VoiceCommandIntentMappingStatus.Ambiguous ||
                result?.Normalization?.Status == VoiceCommandRecognitionStatus.Ambiguous)
            {
                return "ambiguous";
            }

            if (result?.Normalization?.Status == VoiceCommandRecognitionStatus.Unrecognized)
            {
                return "unrecognized";
            }

            if (result?.Reason != null && result.Reason.StartsWith("unsupported_operational_intent_", StringComparison.Ordinal))
            {
                return "unsupported_intent";
            }

            if (string.Equals(result?.Reason, "task_in_progress", StringComparison.OrdinalIgnoreCase))
            {
                return "task_in_progress";
            }

            if (string.Equals(result?.Reason, "voice_stop_latched_pick_and_place_rejected", StringComparison.OrdinalIgnoreCase))
            {
                return "stop_latched";
            }

            return "unsupported_intent";
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

        private string ResolveSubmitRejectionReason()
        {
            string reason = _resolveSubmitRejectionReason?.Invoke();
            return string.IsNullOrWhiteSpace(reason) ? "bridge_rejected_intent" : reason;
        }

        private void EmitExperimentEvent(string eventType, Dictionary<string, object> payload)
        {
            _experimentEventSink?.Emit(eventType, payload ?? new Dictionary<string, object>());
        }

        private static string BuildExecutedText(MultimodalTaskIntent intent)
        {
            if (intent == null)
            {
                return "Orden recibida.";
            }

            string boxLabel = !string.IsNullOrWhiteSpace(intent.TargetId) ? intent.TargetId : intent.ObjectCategory;
            return $"Orden recibida: llevar caja {boxLabel} a zona {FormatZoneLabel(intent.PlaceTargetId)}.";
        }

        private static string FormatZoneLabel(string placeTargetId)
        {
            const string prefix = "Zone";
            if (!string.IsNullOrWhiteSpace(placeTargetId) &&
                placeTargetId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                placeTargetId.Length > prefix.Length)
            {
                return placeTargetId.Substring(prefix.Length).ToUpperInvariant();
            }

            return placeTargetId ?? string.Empty;
        }

        private void Log(string message)
        {
            _log?.Invoke(message);
        }

        private void Warn(string message)
        {
            _warn?.Invoke(message);
        }
    }
}
