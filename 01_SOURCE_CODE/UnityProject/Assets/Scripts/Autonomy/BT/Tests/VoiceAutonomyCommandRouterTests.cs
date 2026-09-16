using Autonomy.Domain;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class VoiceAutonomyCommandRouterTests
    {
        private VoiceCommandNormalizer _normalizer;
        private VoiceCommandIntentMapper _mapper;
        private int _submitCount;
        private MultimodalTaskIntent _submittedIntent;
        private List<VoiceUserFeedbackMessage> _feedback;
        private List<RobotVoiceFeedbackMessage> _robotFeedback;
        private List<VoiceExperimentEventRecord> _events;
        private float _nowSeconds;

        [SetUp]
        public void SetUp()
        {
            _normalizer = new VoiceCommandNormalizer();
            _mapper = new VoiceCommandIntentMapper();
            _submitCount = 0;
            _submittedIntent = null;
            _feedback = new List<VoiceUserFeedbackMessage>();
            _robotFeedback = new List<RobotVoiceFeedbackMessage>();
            _events = new List<VoiceExperimentEventRecord>();
            _nowSeconds = 0f;
        }

        [Test]
        public void Recognized_Transport_Command_Submits_PickAndPlace_To_Bridge()
        {
            VoiceAutonomyCommandRouter router = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled));
            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("lleva la caja a zona A", "voice_attempt_001");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.Submitted));
            Assert.That(_submitCount, Is.EqualTo(1));
            Assert.That(_submittedIntent, Is.Not.Null);
            Assert.That(_submittedIntent.TaskFlow, Is.EqualTo(AutonomousTaskFlow.PickAndPlace));
            Assert.That(_submittedIntent.ObjectSelectionMode, Is.EqualTo(MultimodalObjectSelectionMode.Category));
            Assert.That(_submittedIntent.ObjectCategory, Is.EqualTo("A"));
            Assert.That(_submittedIntent.PlaceTargetId, Is.EqualTo("ZoneA"));
            Assert.That(_submittedIntent.Source, Is.EqualTo("voice_command"));
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.CommandExecuted));
            Assert.That(EventTypes(), Does.Contain("voice_command_normalized"));
            Assert.That(EventTypes(), Does.Contain("voice_intent_mapped"));
            Assert.That(EventTypes(), Does.Contain("voice_feedback_emitted"));
            Assert.That(EventTypes(), Does.Contain("voice_feedback_spoken"));
            Assert.That(EventTypes(), Does.Contain("voice_route_result"));
            Assert.That(EventTypes(), Does.Contain("voice_command_received_diagnostic"));
            Assert.That(EventTypes(), Does.Contain("voice_command_routing_diagnostic"));
            Assert.That(EventTypes(), Does.Contain("voice_command_execution_gate_diagnostic"));
            VoiceExperimentEventRecord feedback = LastEvent("voice_feedback_spoken");
            Assert.That(feedback.Payload["feedback_kind"], Is.EqualTo("OrderAccepted"));
            Assert.That(feedback.Payload["feedback_text"], Is.EqualTo("Orden recibida: llevar\u00e9 la caja A a la zona A."));
            Assert.That(feedback.Payload["accepted"], Is.True);
            Assert.That(_robotFeedback.Last().Kind, Is.EqualTo(RobotVoiceFeedbackKind.OrderAccepted));
            Assert.That(_robotFeedback.Last().Text, Is.EqualTo("Orden recibida: llevar\u00e9 la caja A a la zona A."));
            VoiceExperimentEventRecord route = LastEvent("voice_route_result");
            Assert.That(route.Payload["routing_status"], Is.EqualTo("Submitted"));
            Assert.That(route.Payload["submitted"], Is.True);
            Assert.That(route.Payload["bridge_invoked"], Is.True);
            Assert.That(route.Payload["voice_interaction_id"], Is.EqualTo("voice_attempt_001"));
            VoiceExperimentEventRecord receivedDiagnostic = LastEvent("voice_command_received_diagnostic");
            Assert.That(receivedDiagnostic.Payload["raw_transcript"], Is.EqualTo("lleva la caja a zona A"));
            Assert.That(receivedDiagnostic.Payload["decision"], Is.EqualTo("received"));
            Assert.That(receivedDiagnostic.Payload["voice_interaction_id"], Is.EqualTo("voice_attempt_001"));
            VoiceExperimentEventRecord routingDiagnostic = LastEvent("voice_command_routing_diagnostic");
            Assert.That(routingDiagnostic.Payload["decision"], Is.EqualTo("accepted"));
            Assert.That(routingDiagnostic.Payload["target_id"], Is.EqualTo(string.Empty));
            Assert.That(routingDiagnostic.Payload["destination"], Is.EqualTo("ZoneA"));
            VoiceExperimentEventRecord gateDiagnostic = LastEvent("voice_command_execution_gate_diagnostic");
            Assert.That(gateDiagnostic.Payload["allowed"], Is.True);
            Assert.That(gateDiagnostic.Payload["decision"], Is.EqualTo("allow"));
            Assert.That(EventTypes(), Does.Contain("p40_trace_voice_command_lifecycle"));
            VoiceExperimentEventRecord p40Bridge = _events.Last(record =>
                record.EventType == "p40_trace_voice_command_lifecycle" &&
                record.Payload["phase"].ToString() == "bridge_submitted");
            Assert.That(p40Bridge.Payload["voice_interaction_id"], Is.EqualTo("voice_attempt_001"));
            Assert.That(p40Bridge.Payload["request_id"].ToString(), Is.Not.Empty);
            Assert.That(p40Bridge.Payload["producer"], Is.EqualTo("voice_command"));
            Assert.That(p40Bridge.Payload["requested_destination"], Is.EqualTo("A"));
            Assert.That(p40Bridge.Payload["resolved_destination"], Is.EqualTo("ZoneA"));
            Assert.That(p40Bridge.Payload["submitted_destination"], Is.EqualTo("ZoneA"));
        }

        [Test]
        public void AcceptedVisibleAlias_KeepsTechnicalDestination_ButSpeaksNaturalZoneLabel()
        {
            VoiceAutonomyCommandRoutingResult result = RoutePrebuiltNormalization(
                new VoiceCommandNormalizationResult(
                    "lleva la caja A1 a zona A",
                    "lleva la caja A1 a zona A",
                    "lleva la caja A1 a zona A",
                    VoiceCommandRecognitionStatus.Recognized,
                    0.95f,
                    "lleva la caja A1 a zona A",
                    VoiceCommandActionToken.Transport,
                    ObjectToken.Box,
                    "A1",
                    "A",
                    null,
                    string.Empty));

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.Submitted));
            Assert.That(result.SubmittedIntent.TargetId, Is.EqualTo("A1"));
            Assert.That(result.SubmittedIntent.PlaceTargetId, Is.EqualTo("ZoneA"));

            VoiceExperimentEventRecord feedback = LastEvent("voice_feedback_spoken");
            Assert.That(feedback.Payload["target_alias"], Is.EqualTo("A1"));
            Assert.That(feedback.Payload["destination"], Is.EqualTo("A"));
            Assert.That(feedback.Payload["feedback_text"], Is.EqualTo("Orden recibida: llevar\u00e9 la caja A1 a la zona A."));
            Assert.That(feedback.Payload["feedback_text"].ToString(), Does.Not.Contain(" a A."));
        }

        [Test]
        public void Ambiguous_Command_Does_Not_Submit_To_Bridge()
        {
            VoiceAutonomyCommandRoutingResult result = Route("coge la caja mas cercana");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.CommandAmbiguous));
        }

        [Test]
        public void Unrecognized_Command_Does_Not_Submit_To_Bridge()
        {
            VoiceAutonomyCommandRoutingResult result = Route("bada");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(result.Normalization.Status, Is.EqualTo(VoiceCommandRecognitionStatus.Unrecognized));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.CommandUnrecognized));
            Assert.That(_robotFeedback.Last().Kind, Is.EqualTo(RobotVoiceFeedbackKind.CommandRejectedUnrecognized));
            Assert.That(_robotFeedback.Last().Text, Is.EqualTo("No he entendido la orden. Repite el comando indicando la caja y el destino."));
            Assert.That(_robotFeedback.Last().Text, Is.Not.EqualTo("Comando recibido."));
            VoiceExperimentEventRecord feedback = LastEvent("voice_feedback_spoken");
            Assert.That(feedback.Payload["feedback_kind"], Is.EqualTo("CommandRejectedUnrecognized"));
            Assert.That(feedback.Payload["feedback_outcome"], Is.EqualTo("rejected"));
        }

        [Test]
        public void Incomplete_Transport_Command_Does_Not_Submit_To_Bridge()
        {
            VoiceAutonomyCommandRoutingResult result = RoutePrebuiltNormalization(
                new VoiceCommandNormalizationResult(
                    "lleva la caja",
                    "lleva la caja",
                    "lleva la caja",
                    VoiceCommandRecognitionStatus.Recognized,
                    0.72f,
                    "lleva la caja",
                    VoiceCommandActionToken.Transport,
                    ObjectToken.Box,
                    "A",
                    string.Empty,
                    null,
                    string.Empty));

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(result.Mapping.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Incomplete));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.CommandIncomplete));
        }

        [TestCase("detente")]
        [TestCase("para robot")]
        public void Stop_Command_Does_Not_Submit_To_Bridge_Without_Operational_Stop_Support(string transcript)
        {
            VoiceAutonomyCommandRoutingResult result = Route(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(result.Mapping.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.Mapping.IntentKind, Is.EqualTo(VoiceCommandIntentKind.Stop));
            Assert.That(result.SubmittedIntent, Is.Null);
            Assert.That(_submitCount, Is.Zero);
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.StopUnavailable));
            Assert.That(_robotFeedback.Last().Kind, Is.EqualTo(RobotVoiceFeedbackKind.ControlCommandAcknowledged));
            Assert.That(_robotFeedback.Last().Text, Is.EqualTo("No puedo detener la tarea en este estado."));
            Assert.That(LastEvent("voice_feedback_spoken").Payload["feedback_outcome"], Is.EqualTo("acknowledged"));
            Assert.That(EventTypes(), Does.Contain("voice_command_control_path_diagnostic"));
            VoiceExperimentEventRecord controlPath = LastEvent("voice_command_control_path_diagnostic");
            Assert.That(controlPath.Payload["intent_kind"], Is.EqualTo("Stop"));
            Assert.That(controlPath.Payload["decision"], Is.EqualTo("control_path_unavailable"));
            Assert.That(controlPath.Payload["bridge_invoked"], Is.False);
        }

        [TestCase("para")]
        [TestCase("para robot")]
        public void Stop_Command_Applies_Operational_Stop_WhenHandlerAvailable(string transcript)
        {
            int stopCount = 0;
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                applyStopCommand: (mapping, normalization) =>
                {
                    stopCount++;
                    Assert.That(mapping.IntentKind, Is.EqualTo(VoiceCommandIntentKind.Stop));
                    Assert.That(normalization.Action, Is.EqualTo(VoiceCommandActionToken.Stop));
                    return VoiceStopCommandResult.Accept("active_task_cancelled_by_voice_stop");
                });

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript(transcript, "voice_stop_001");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.Submitted));
            Assert.That(result.SubmittedIntent, Is.Null);
            Assert.That(stopCount, Is.EqualTo(1));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.CommandExecuted));
            Assert.That(_robotFeedback.Last().Kind, Is.EqualTo(RobotVoiceFeedbackKind.ControlCommandAcknowledged));
            Assert.That(_robotFeedback.Last().Text, Is.EqualTo("Orden recibida: detengo la tarea actual."));
            Assert.That(LastEvent("voice_feedback_spoken").Payload["accepted"], Is.True);
            Assert.That(EventTypes(), Does.Contain("voice_stop_command_lifecycle"));
            Assert.That(LastEvent("voice_command_control_path_diagnostic").Payload["decision"], Is.EqualTo("control_path_available"));
            Assert.That(LastEvent("voice_command_routing_diagnostic").Payload["decision"], Is.EqualTo("accepted"));
        }

        [Test]
        public void RepeatedStop_PropagatesAlreadyStoppedAsSpecificRobotFeedback_ExactlyOnce()
        {
            int stopCount = 0;
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                applyStopCommand: (mapping, normalization) => ++stopCount == 1
                    ? VoiceStopCommandResult.Accept("active_task_cancelled_by_voice_stop")
                    : VoiceStopCommandResult.Accept("already_stopped_idempotent_noop", "Ya estoy parado."));

            VoiceAutonomyCommandRoutingResult first = router.RouteTranscript("para", "voice_stop_first");
            VoiceAutonomyCommandRoutingResult second = router.RouteTranscript("detente", "voice_stop_repeated");

            Assert.That(first.Reason, Is.EqualTo("active_task_cancelled_by_voice_stop"));
            Assert.That(second.Reason, Is.EqualTo("already_stopped_idempotent_noop"));
            Assert.That(second.FeedbackMessage.Text, Is.EqualTo("Ya estoy parado."));
            Assert.That(_feedback, Has.Count.EqualTo(2));
            Assert.That(_robotFeedback, Has.Count.EqualTo(2));
            Assert.That(_robotFeedback[0].Kind, Is.EqualTo(RobotVoiceFeedbackKind.ControlCommandAcknowledged));
            Assert.That(_robotFeedback[0].Text, Is.EqualTo("Orden recibida: detengo la tarea actual."));
            Assert.That(_robotFeedback[1].Kind, Is.EqualTo(RobotVoiceFeedbackKind.AlreadyStopped));
            Assert.That(_robotFeedback[1].Text, Is.EqualTo("Ya estoy parado."));
            VoiceExperimentEventRecord spoken = LastEvent("voice_feedback_spoken");
            Assert.That(spoken.Payload["reason"], Is.EqualTo("already_stopped_idempotent_noop"));
            Assert.That(spoken.Payload["robot_feedback_reason"], Is.EqualTo("AlreadyStopped"));
            Assert.That(spoken.Payload["feedback_text"], Is.EqualTo("Ya estoy parado."));
        }

        [Test]
        public void FourConsecutiveStops_KeepFirstAcknowledgement_ThenEmitThreeAlreadyStoppedMessages()
        {
            int stopCount = 0;
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                applyStopCommand: (mapping, normalization) => ++stopCount == 1
                    ? VoiceStopCommandResult.Accept("safe_stop_with_held_object")
                    : VoiceStopCommandResult.Accept("already_stopped_idempotent_noop", "Ya estoy parado."));

            router.RouteTranscript("para");
            router.RouteTranscript("para");
            router.RouteTranscript("detente");
            router.RouteTranscript("para robot");

            Assert.That(stopCount, Is.EqualTo(4));
            Assert.That(_robotFeedback, Has.Count.EqualTo(4));
            Assert.That(_robotFeedback[0].Kind, Is.EqualTo(RobotVoiceFeedbackKind.ControlCommandAcknowledged));
            Assert.That(_robotFeedback[0].Text, Is.EqualTo("Orden recibida: me detengo y mantengo el objeto sujeto."));
            Assert.That(_robotFeedback.Skip(1).All(message => message.Kind == RobotVoiceFeedbackKind.AlreadyStopped), Is.True);
            Assert.That(_robotFeedback.Skip(1).All(message => message.Text == "Ya estoy parado."), Is.True);
        }

        [Test]
        public void Resume_Command_Uses_Control_Route_Not_Bridge_WhenHandlerAvailable()
        {
            int resumeCount = 0;
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                applyResumeCommand: (mapping, normalization) =>
                {
                    resumeCount++;
                    Assert.That(mapping.IntentKind, Is.EqualTo(VoiceCommandIntentKind.Resume));
                    Assert.That(normalization.Action, Is.EqualTo(VoiceCommandActionToken.Resume));
                    return VoiceResumeCommandResult.Accept("pre_pick_task_restored");
                });

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("retoma la tarea actual", "voice_resume_001");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.Submitted));
            Assert.That(result.SubmittedIntent, Is.Null);
            Assert.That(resumeCount, Is.EqualTo(1));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.CommandExecuted));
            Assert.That(_robotFeedback.Last().Text, Is.EqualTo("Orden recibida: reanudo la tarea detenida."));
            Assert.That(EventTypes(), Does.Contain("voice_resume_command_lifecycle"));
            Assert.That(LastEvent("voice_command_control_path_diagnostic").Payload["intent_kind"], Is.EqualTo("Resume"));
            Assert.That(LastEvent("voice_command_control_path_diagnostic").Payload["decision"], Is.EqualTo("control_path_available"));
            Assert.That(LastEvent("voice_route_result").Payload["bridge_invoked"], Is.False);
        }

        [TestCase("para")]
        [TestCase("detente")]
        [TestCase("continua")]
        [TestCase("reanuda")]
        [TestCase("retoma")]
        [TestCase("cancela la orden pendiente")]
        public void VoiceOff_Condition_Blocks_Control_Commands_Before_Handler(string transcript)
        {
            int stopCount = 0;
            int resumeCount = 0;
            int cancelCount = 0;
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                applyStopCommand: (mapping, normalization) =>
                {
                    stopCount++;
                    return VoiceStopCommandResult.Accept("should_not_run");
                },
                applyResumeCommand: (mapping, normalization) =>
                {
                    resumeCount++;
                    return VoiceResumeCommandResult.Accept("should_not_run");
                },
                applyCancelPendingOrder: (mapping, normalization) => cancelCount++,
                executionGate: intent => new ExperimentConditionConfig(true, false, "robot_on_voice_off").EvaluateVoiceCommand(intent));

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript(transcript, "voice_off_control");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(result.Reason, Is.EqualTo("voice_disabled_by_condition"));
            Assert.That(stopCount, Is.Zero);
            Assert.That(resumeCount, Is.Zero);
            Assert.That(cancelCount, Is.Zero);
            Assert.That(_submitCount, Is.Zero);
            Assert.That(LastEvent("voice_command_execution_gate_diagnostic").Payload["allowed"], Is.False);
            Assert.That(LastEvent("voice_command_blocked_by_condition").Payload["reason"], Is.EqualTo("voice_disabled_by_condition"));
        }

        [Test]
        public void VoiceOff_Condition_Blocks_PickAndPlace_Before_Bridge()
        {
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                executionGate: intent => new ExperimentConditionConfig(true, false, "robot_on_voice_off").EvaluateVoiceCommand(intent));

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("lleva la caja B2 a su zona", "voice_off_pick");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(result.Reason, Is.EqualTo("voice_disabled_by_condition"));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(LastEvent("voice_command_blocked_by_condition").Payload["reason"], Is.EqualTo("voice_disabled_by_condition"));
        }

        [Test]
        public void VoiceOn_Condition_Allows_Control_Command_Handler()
        {
            int stopCount = 0;
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                applyStopCommand: (mapping, normalization) =>
                {
                    stopCount++;
                    return VoiceStopCommandResult.Accept("active_task_cancelled_by_voice_stop");
                },
                executionGate: intent => new ExperimentConditionConfig(true, true, "robot_on_voice_on").EvaluateVoiceCommand(intent));

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("para", "voice_on_stop");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.Submitted));
            Assert.That(stopCount, Is.EqualTo(1));
            Assert.That(_submitCount, Is.Zero);
        }

        [Test]
        public void Resume_Command_WithoutSnapshot_Returns_Functional_Rejection()
        {
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                applyResumeCommand: (mapping, normalization) => VoiceResumeCommandResult.Reject(
                    "no_stopped_task_to_resume",
                    "No hay ninguna tarea detenida que pueda reanudar."));

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("reanuda", "voice_resume_empty");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.ResumeUnavailable));
            Assert.That(_robotFeedback.Last().Text, Is.EqualTo("No hay ninguna tarea detenida que pueda reanudar."));
            Assert.That(_robotFeedback.Last().Text, Does.Not.Contain("reanudo"));
            Assert.That(LastEvent("voice_resume_command_lifecycle").Payload["accepted"], Is.False);
            Assert.That(_submitCount, Is.Zero);
        }

        [Test]
        public void PickOnly_Command_Does_Not_Submit_As_PickAndPlace()
        {
            VoiceAutonomyCommandRoutingResult result = Route("coge la caja A");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(result.Mapping.Status, Is.EqualTo(VoiceCommandIntentMappingStatus.Mapped));
            Assert.That(result.Mapping.IntentKind, Is.EqualTo(VoiceCommandIntentKind.Pick));
            Assert.That(result.Mapping.TaskIntent.TaskFlow, Is.EqualTo(AutonomousTaskFlow.PickOnly));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.PickOnlyUnavailable));
        }

        [Test]
        public void AlwaysForExecutable_PickAndPlace_Remains_Pending_Without_Submitting()
        {
            VoiceAutonomyCommandRouter router = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.AlwaysForExecutable));

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("lleva la caja a zona A");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.PendingConfirmation));
            Assert.That(result.PendingIntent, Is.Not.Null);
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.ConfirmationRequested));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(EventTypes(), Does.Contain("voice_confirmation_pending"));
            VoiceExperimentEventRecord route = LastEvent("voice_route_result");
            Assert.That(route.Payload["routing_status"], Is.EqualTo("PendingConfirmation"));
            Assert.That(route.Payload["submitted"], Is.False);
            Assert.That(route.Payload["bridge_invoked"], Is.False);
        }

        [Test]
        public void StopLatched_PickAndPlace_Rejected_BeforeBridgePendingOrTaskMutation()
        {
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.AlwaysForExecutable),
                isVoiceStopLatched: () => true);

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("lleva la caja A1 a su zona", "voice_stop_latched_pick");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(result.Submitted, Is.False);
            Assert.That(result.SubmittedIntent, Is.Null);
            Assert.That(result.PendingIntent, Is.Null);
            Assert.That(router.HasPendingConfirmation, Is.False);
            Assert.That(_submitCount, Is.Zero);
            Assert.That(_submittedIntent, Is.Null);
            Assert.That(result.Mapping.IntentKind, Is.EqualTo(VoiceCommandIntentKind.PickAndPlace));
            Assert.That(result.Reason, Is.EqualTo("voice_stop_latched_pick_and_place_rejected"));
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.CommandBlockedByCondition));
            Assert.That(_robotFeedback.Last().Kind, Is.EqualTo(RobotVoiceFeedbackKind.CommandRejectedStoppedByVoice));
            Assert.That(_robotFeedback.Last().Text, Is.EqualTo("Estoy detenido. Di 'contin\u00faa' o 'retoma la tarea' para seguir."));
            Assert.That(EventTypes(), Does.Not.Contain("voice_confirmation_pending"));
            Assert.That(EventTypes(), Does.Not.Contain("voice_confirmation_accepted"));
            Assert.That(_events.Where(record => record.EventType == "p40_trace_voice_command_lifecycle")
                .Any(record => record.Payload.TryGetValue("phase", out object phase) && phase.ToString() == "bridge_submitted"), Is.False);

            VoiceExperimentEventRecord stopLatch = LastEvent("voice_pick_and_place_rejected_by_stop_latch");
            Assert.That(stopLatch.Payload["reason"], Is.EqualTo("voice_stop_latched_pick_and_place_rejected"));
            Assert.That(stopLatch.Payload["bridge_invoked"], Is.False);
            Assert.That(stopLatch.Payload["pending_created"], Is.False);
            Assert.That(stopLatch.Payload["pending_confirmation_created"], Is.False);
            Assert.That(stopLatch.Payload["p40b_pending_created"], Is.False);
            Assert.That(stopLatch.Payload["p40a_replacement_requested"], Is.False);
            Assert.That(stopLatch.Payload["bt_reset_requested"], Is.False);
            Assert.That(stopLatch.Payload["new_task_started"], Is.False);
            Assert.That(stopLatch.Payload["navigation_manipulation_activated"], Is.False);
            Assert.That(LastEvent("voice_command_rejected").Payload["rejection_type"], Is.EqualTo("stop_latched"));
            Assert.That(LastEvent("voice_route_result").Payload["bridge_invoked"], Is.False);
        }

        [Test]
        public void StopLatched_Resume_Accepted_AndUnlocksLatch()
        {
            bool stopLatched = true;
            int resumeCount = 0;
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                applyResumeCommand: (mapping, normalization) =>
                {
                    resumeCount++;
                    stopLatched = false;
                    return VoiceResumeCommandResult.Accept("pre_pick_task_restored");
                },
                isVoiceStopLatched: () => stopLatched);

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("retoma la tarea", "voice_resume_after_latch");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.Submitted));
            Assert.That(result.SubmittedIntent, Is.Null);
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.CommandExecuted));
            Assert.That(resumeCount, Is.EqualTo(1));
            Assert.That(stopLatched, Is.False);
            Assert.That(_submitCount, Is.Zero);
            Assert.That(LastEvent("voice_resume_command_lifecycle").Payload["accepted"], Is.True);
            Assert.That(LastEvent("voice_resume_command_lifecycle").Payload["reason"], Is.EqualTo("pre_pick_task_restored"));
            Assert.That(LastEvent("voice_route_result").Payload["bridge_invoked"], Is.False);
        }

        [Test]
        public void StopLatched_Cancel_KeepsExistingCancelSemantics()
        {
            bool cancelHookInvoked = false;
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                applyCancelPendingOrder: (mapping, normalization) => cancelHookInvoked = true,
                isVoiceStopLatched: () => true);

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("cancela la orden pendiente", "voice_cancel_while_latched");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(result.Mapping.IntentKind, Is.EqualTo(VoiceCommandIntentKind.CancelPendingOrder));
            Assert.That(result.PendingIntent, Is.Null);
            Assert.That(cancelHookInvoked, Is.True);
            Assert.That(_submitCount, Is.Zero);
            Assert.That(EventTypes(), Does.Not.Contain("voice_pick_and_place_rejected_by_stop_latch"));
            Assert.That(EventTypes(), Does.Not.Contain("voice_resume_command_lifecycle"));
            Assert.That(_robotFeedback.Last().Kind, Is.EqualTo(RobotVoiceFeedbackKind.PendingCommandCancelled));
            Assert.That(_robotFeedback.Last().Text, Is.EqualTo("Cancelo la orden pendiente."));
        }

        [Test]
        public void NoStopLatched_PickAndPlace_PreservesPendingConfirmationBehavior()
        {
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.AlwaysForExecutable),
                isVoiceStopLatched: () => false);

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("lleva la caja a zona A", "voice_no_latch_pick");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.PendingConfirmation));
            Assert.That(result.PendingIntent, Is.Not.Null);
            Assert.That(result.PendingIntent.TaskFlow, Is.EqualTo(AutonomousTaskFlow.PickAndPlace));
            Assert.That(router.HasPendingConfirmation, Is.True);
            Assert.That(_submitCount, Is.Zero);
            Assert.That(EventTypes(), Does.Contain("voice_confirmation_pending"));
            Assert.That(EventTypes(), Does.Not.Contain("voice_pick_and_place_rejected_by_stop_latch"));
        }

        [TestCase("si")]
        [TestCase("sí")]
        [TestCase("confirma")]
        public void Affirmative_Confirmation_With_Pending_Intent_Submits_Exactly_That_Intent(string confirmation)
        {
            VoiceAutonomyCommandRouter router = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.AlwaysForExecutable));
            VoiceAutonomyCommandRoutingResult pending = router.RouteTranscript("lleva la caja a zona A");

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript(confirmation);

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.ConfirmationAccepted));
            Assert.That(_submitCount, Is.EqualTo(1));
            Assert.That(_submittedIntent, Is.SameAs(pending.PendingIntent));
            Assert.That(_submittedIntent.TaskFlow, Is.EqualTo(AutonomousTaskFlow.PickAndPlace));
            Assert.That(_feedback.Any(message => message.Type == VoiceUserFeedbackType.ConfirmationAccepted), Is.True);
            Assert.That(_feedback.Any(message => message.Type == VoiceUserFeedbackType.CommandExecuted), Is.True);
            Assert.That(EventTypes(), Does.Contain("voice_confirmation_accepted"));
            VoiceExperimentEventRecord accepted = LastEvent("voice_confirmation_accepted");
            Assert.That(accepted.Payload["submitted"], Is.True);
            Assert.That(accepted.Payload["bridge_invoked"], Is.True);
        }

        [Test]
        public void Confirmation_Events_Keep_Original_Voice_Interaction_Id()
        {
            VoiceAutonomyCommandRouter router = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.AlwaysForExecutable));

            router.RouteTranscript("lleva la caja a zona A", "voice_attempt_original");
            router.RouteTranscript("no", "voice_attempt_response");

            VoiceExperimentEventRecord cancelled = LastEvent("voice_confirmation_cancelled");
            Assert.That(cancelled.Payload["voice_interaction_id"], Is.EqualTo("voice_attempt_original"));
            Assert.That(cancelled.Payload["voice_response_interaction_id"], Is.EqualTo("voice_attempt_response"));
        }

        [Test]
        public void Affirmative_Confirmation_Without_Pending_Intent_Does_Not_Submit()
        {
            VoiceAutonomyCommandRouter router = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.AlwaysForExecutable));

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("confirma");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NoPendingConfirmation));
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.NoPendingConfirmation));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(EventTypes(), Does.Contain("voice_confirmation_without_pending"));
        }

        [TestCase("no")]
        [TestCase("cancelar")]
        public void Negative_Confirmation_With_Pending_Intent_Cancels_Without_Submitting(string cancellation)
        {
            VoiceAutonomyCommandRouter router = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.AlwaysForExecutable));
            router.RouteTranscript("lleva la caja a zona A");

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript(cancellation);

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.ConfirmationCanceled));
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.ConfirmationCanceled));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(EventTypes(), Does.Contain("voice_confirmation_cancelled"));

            VoiceAutonomyCommandRoutingResult second = router.RouteTranscript("si");
            Assert.That(second.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NoPendingConfirmation));
            Assert.That(_submitCount, Is.Zero);
        }

        [Test]
        public void Timeout_Discards_Pending_Intent_Without_Submitting()
        {
            VoiceAutonomyCommandRouter router = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.AlwaysForExecutable, timeoutSeconds: 2f));
            router.RouteTranscript("lleva la caja a zona A");
            _nowSeconds = 2.1f;

            VoiceAutonomyCommandRoutingResult result = router.CheckPendingConfirmationTimeout();

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.ConfirmationExpired));
            Assert.That(result.FeedbackMessage.Type, Is.EqualTo(VoiceUserFeedbackType.ConfirmationExpired));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(EventTypes(), Does.Contain("voice_confirmation_expired"));

            VoiceAutonomyCommandRoutingResult confirm = router.RouteTranscript("si");
            Assert.That(confirm.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NoPendingConfirmation));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(EventTypes(), Does.Contain("voice_confirmation_without_pending"));
        }

        [TestCase("bada", "unrecognized")]
        [TestCase("para", "unsupported_stop")]
        [TestCase("coge la caja A", "unsupported_pick_only")]
        [TestCase("para robot", "unsupported_stop")]
        public void NonExecutable_Commands_Emit_Rejection_Events_Without_Bridge(string transcript, string rejectionType)
        {
            VoiceAutonomyCommandRoutingResult result = Route(transcript);

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(EventTypes(), Does.Contain("voice_command_rejected"));
            VoiceExperimentEventRecord rejection = LastEvent("voice_command_rejected");
            Assert.That(rejection.Payload["rejection_type"], Is.EqualTo(rejectionType));
            Assert.That(rejection.Payload["bridge_invoked"], Is.False);
            Assert.That(EventTypes(), Does.Contain("voice_command_rejected_diagnostic"));
            VoiceExperimentEventRecord rejectionDiagnostic = LastEvent("voice_command_rejected_diagnostic");
            Assert.That(rejectionDiagnostic.Payload["rejection_type"], Is.EqualTo(rejectionType));
            Assert.That(rejectionDiagnostic.Payload["decision"], Is.EqualTo("rejected"));
            VoiceExperimentEventRecord route = LastEvent("voice_route_result");
            Assert.That(route.Payload["submitted"], Is.False);
            Assert.That(route.Payload["bridge_invoked"], Is.False);
        }

        [Test]
        public void Incomplete_Command_Emits_Rejection_Event_Without_Bridge()
        {
            VoiceAutonomyCommandRoutingResult result = RoutePrebuiltNormalization(
                new VoiceCommandNormalizationResult(
                    "lleva la caja",
                    "lleva la caja",
                    "lleva la caja",
                    VoiceCommandRecognitionStatus.Recognized,
                    0.72f,
                    "lleva la caja",
                    VoiceCommandActionToken.Transport,
                    ObjectToken.Box,
                    "A",
                    string.Empty,
                    null,
                    string.Empty));

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(_submitCount, Is.Zero);
            VoiceExperimentEventRecord rejection = LastEvent("voice_command_rejected");
            Assert.That(rejection.Payload["rejection_type"], Is.EqualTo("incomplete"));
            Assert.That(rejection.Payload["bridge_invoked"], Is.False);
        }

        [Test]
        public void Without_Experiment_Event_Sink_Functional_Behavior_Remains_Unchanged()
        {
            VoiceAutonomyCommandRouter router = new(
                _normalizer,
                _mapper,
                SubmitIntent,
                confirmationPolicy: new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                feedbackSink: new CapturingFeedbackSink(_feedback),
                getTimeSeconds: () => _nowSeconds);

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("lleva la caja a zona A");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.Submitted));
            Assert.That(_submitCount, Is.EqualTo(1));
            Assert.That(_events, Is.Empty);
        }

        [Test]
        public void New_Executable_Command_Replaces_Previous_Pending_Intent()
        {
            VoiceAutonomyCommandRouter router = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.AlwaysForExecutable));
            VoiceAutonomyCommandRoutingResult first = router.RouteTranscript("lleva la caja a zona A");
            VoiceAutonomyCommandRoutingResult second = router.RouteTranscript("lleva la caja B a zona B");

            Assert.That(first.PendingIntent.ObjectCategory, Is.EqualTo("A"));
            Assert.That(second.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.PendingConfirmation));
            Assert.That(second.PendingIntent.ObjectCategory, Is.EqualTo("B"));
            Assert.That(second.PendingIntent.PlaceTargetId, Is.EqualTo("ZoneB"));
            Assert.That(_submitCount, Is.Zero);

            router.RouteTranscript("si");
            Assert.That(_submitCount, Is.EqualTo(1));
            Assert.That(_submittedIntent.ObjectCategory, Is.EqualTo("B"));
            Assert.That(_submittedIntent.PlaceTargetId, Is.EqualTo("ZoneB"));
        }

        [Test]
        public void Bridge_Is_Invoked_Only_For_Direct_Or_Confirmed_Executable_Intents()
        {
            VoiceAutonomyCommandRouter router = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.AlwaysForExecutable));

            router.RouteTranscript("bada");
            router.RouteTranscript("lleva la caja");
            router.RouteTranscript("para robot");
            router.RouteTranscript("coge la caja A");
            router.RouteTranscript("si");
            Assert.That(_submitCount, Is.Zero);

            router.RouteTranscript("lleva la caja a zona A");
            Assert.That(_submitCount, Is.Zero);

            router.RouteTranscript("si");
            Assert.That(_submitCount, Is.EqualTo(1));

            VoiceAutonomyCommandRouter directRouter = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled));
            directRouter.RouteTranscript("lleva la caja B a zona B");
            Assert.That(_submitCount, Is.EqualTo(2));
        }

        [Test]
        public void Bridge_Rejection_TaskInProgress_Emits_Specific_Feedback_And_Event()
        {
            VoiceAutonomyCommandRouter router = new(
                _normalizer,
                _mapper,
                intent =>
                {
                    _submitCount++;
                    _submittedIntent = intent;
                    return false;
                },
                confirmationPolicy: new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                feedbackSink: new CapturingFeedbackSink(_feedback),
                robotFeedbackSink: new CapturingRobotFeedbackSink(_robotFeedback),
                experimentEventSink: new CapturingExperimentEventSink(_events),
                getTimeSeconds: () => _nowSeconds,
                resolveSubmitRejectionReason: () => "task_in_progress");

            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("lleva la caja A a zona A");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.BridgeRejected));
            Assert.That(result.Reason, Is.EqualTo("task_in_progress"));
            Assert.That(result.FeedbackMessage.Text, Is.EqualTo("El robot ya esta ejecutando una tarea. Espera a que termine."));
            Assert.That(_robotFeedback.Last().Kind, Is.EqualTo(RobotVoiceFeedbackKind.RobotBusy));
            Assert.That(_robotFeedback.Last().Text, Is.EqualTo("Estoy ocupado ahora. Terminar\u00e9 la acci\u00f3n actual antes de aceptar otra orden."));
            Assert.That(EventTypes(), Does.Contain("voice_command_rejected_task_in_progress"));
            VoiceExperimentEventRecord rejection = LastEvent("voice_command_rejected");
            Assert.That(rejection.Payload["rejection_type"], Is.EqualTo("task_in_progress"));
        }

        [Test]
        public void NearestAmbiguity_EmitsStructuredClarification_WithoutSubmitting()
        {
            VoiceAutonomyCommandRoutingResult result = Route("coge la caja mas cercana");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(result.Mapping.IntentKind, Is.EqualTo(VoiceCommandIntentKind.NearestAmbiguity));
            Assert.That(_submitCount, Is.Zero);
            Assert.That(_robotFeedback.Last().Kind, Is.EqualTo(RobotVoiceFeedbackKind.NearestReferenceAmbiguous));
            Assert.That(_robotFeedback.Last().Text, Is.EqualTo("\u00bfM\u00e1s cercana a ti o m\u00e1s cercana a m\u00ed?"));
        }

        [Test]
        public void CancelPendingOrder_EmitsAcknowledgement_AndInvokesPendingCancelHook()
        {
            bool cancelHookInvoked = false;
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                applyCancelPendingOrder: (mapping, normalization) => cancelHookInvoked = true);
            VoiceAutonomyCommandRoutingResult result = router.RouteTranscript("cancela la orden pendiente");

            Assert.That(result.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.NotSubmitted));
            Assert.That(result.Mapping.IntentKind, Is.EqualTo(VoiceCommandIntentKind.CancelPendingOrder));
            Assert.That(result.PendingIntent, Is.Null);
            Assert.That(_submitCount, Is.Zero);
            Assert.That(cancelHookInvoked, Is.True);
            Assert.That(_robotFeedback.Last().Kind, Is.EqualTo(RobotVoiceFeedbackKind.PendingCommandCancelled));
            Assert.That(_robotFeedback.Last().Text, Is.EqualTo("Cancelo la orden pendiente."));
            Assert.That(LastEvent("voice_feedback_spoken").Payload["feedback_outcome"], Is.EqualTo("acknowledged"));
        }

        [Test]
        public void Voice_Routing_Does_Not_Expose_RobotBlackboard_Dependency()
        {
            string domainAssembly = typeof(VoiceAutonomyCommandRouter).Assembly.FullName;
            string blackboardAssembly = typeof(Autonomy.Core.RobotBlackboard).Assembly.FullName;

            Assert.That(domainAssembly, Is.EqualTo(blackboardAssembly));
            Assert.That(typeof(VoiceAutonomyCommandRouter).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Any(field => field.FieldType == typeof(Autonomy.Core.RobotBlackboard)), Is.False);
        }

        [Test]
        public void ExperimentPause_RejectsVoiceBeforeStopResumeOrBridge_WithoutFeedbackOrPendingMutation()
        {
            bool paused = true;
            int stopCalls = 0;
            int resumeCalls = 0;
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled),
                applyStopCommand: (_, _) =>
                {
                    stopCalls++;
                    return VoiceStopCommandResult.Accept("should_not_run");
                },
                applyResumeCommand: (_, _) =>
                {
                    resumeCalls++;
                    return VoiceResumeCommandResult.Accept("should_not_run");
                },
                isExperimentPaused: () => paused);

            VoiceAutonomyCommandRoutingResult stop = router.RouteTranscript("para");
            VoiceAutonomyCommandRoutingResult resume = router.RouteTranscript("continua");
            VoiceAutonomyCommandRoutingResult pick = router.RouteTranscript("lleva la caja A a la zona A");

            Assert.That(new[] { stop.Reason, resume.Reason, pick.Reason }, Is.All.EqualTo("experiment_paused_command_rejected"));
            Assert.That(stopCalls, Is.Zero);
            Assert.That(resumeCalls, Is.Zero);
            Assert.That(_submitCount, Is.Zero);
            Assert.That(router.HasPendingConfirmation, Is.False);
            Assert.That(_feedback, Is.Empty);
            Assert.That(_robotFeedback, Is.Empty);
            Assert.That(EventTypes().Count(eventType => eventType == "voice_command_rejected_while_paused"), Is.EqualTo(3));
        }

        [Test]
        public void ExperimentPause_DoesNotConsumeConfirmationCreatedBeforePause()
        {
            bool paused = false;
            VoiceAutonomyCommandRouter router = CreateRouter(
                new VoiceConfirmationPolicy(VoiceConfirmationMode.AlwaysForExecutable),
                isExperimentPaused: () => paused);
            VoiceAutonomyCommandRoutingResult pending = router.RouteTranscript("lleva la caja a zona A");
            Assert.That(pending.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.PendingConfirmation));

            paused = true;
            VoiceAutonomyCommandRoutingResult rejected = router.RouteTranscript("si");

            Assert.That(rejected.Reason, Is.EqualTo("experiment_paused_command_rejected"));
            Assert.That(router.HasPendingConfirmation, Is.True);
            Assert.That(_submitCount, Is.Zero);

            paused = false;
            VoiceAutonomyCommandRoutingResult accepted = router.RouteTranscript("si");
            Assert.That(accepted.Status, Is.EqualTo(VoiceAutonomyCommandRoutingStatus.ConfirmationAccepted));
            Assert.That(_submitCount, Is.EqualTo(1));
        }

        private VoiceAutonomyCommandRoutingResult Route(string transcript)
        {
            VoiceAutonomyCommandRouter router = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled));
            return router.RouteTranscript(transcript);
        }

        private VoiceAutonomyCommandRoutingResult RoutePrebuiltNormalization(VoiceCommandNormalizationResult normalization)
        {
            var normalizer = new StubNormalizer(normalization);
            VoiceAutonomyCommandRouter router = CreateRouter(new VoiceConfirmationPolicy(VoiceConfirmationMode.Disabled), normalizer);
            return router.RouteTranscript(normalization.RawTranscript);
        }

        private VoiceAutonomyCommandRouter CreateRouter(
            VoiceConfirmationPolicy policy,
            IVoiceCommandNormalizer normalizer = null,
            System.Func<VoiceCommandIntentMappingResult, VoiceCommandNormalizationResult, VoiceStopCommandResult> applyStopCommand = null,
            System.Func<VoiceCommandIntentMappingResult, VoiceCommandNormalizationResult, VoiceResumeCommandResult> applyResumeCommand = null,
            System.Action<VoiceCommandIntentMappingResult, VoiceCommandNormalizationResult> applyCancelPendingOrder = null,
            System.Func<MultimodalTaskIntent, VoiceCommandExecutionGateResult> executionGate = null,
            System.Func<bool> isVoiceStopLatched = null,
            System.Func<bool> isExperimentPaused = null)
        {
            return new VoiceAutonomyCommandRouter(
                normalizer ?? _normalizer,
                _mapper,
                SubmitIntent,
                confirmationPolicy: policy,
                feedbackSink: new CapturingFeedbackSink(_feedback),
                robotFeedbackSink: new CapturingRobotFeedbackSink(_robotFeedback),
                experimentEventSink: new CapturingExperimentEventSink(_events),
                getTimeSeconds: () => _nowSeconds,
                executionGate: executionGate,
                applyStopCommand: applyStopCommand,
                applyResumeCommand: applyResumeCommand,
                applyCancelPendingOrder: applyCancelPendingOrder,
                isVoiceStopLatched: isVoiceStopLatched,
                isExperimentPaused: isExperimentPaused);
        }

        private IEnumerable<string> EventTypes()
        {
            return _events.Select(record => record.EventType);
        }

        private VoiceExperimentEventRecord LastEvent(string eventType)
        {
            return _events.Last(record => record.EventType == eventType);
        }

        private bool SubmitIntent(MultimodalTaskIntent intent)
        {
            _submitCount++;
            _submittedIntent = intent;
            return true;
        }

        private sealed class StubNormalizer : IVoiceCommandNormalizer
        {
            private readonly VoiceCommandNormalizationResult _result;

            public StubNormalizer(VoiceCommandNormalizationResult result)
            {
                _result = result;
            }

            public VoiceCommandNormalizationResult Normalize(string transcript)
            {
                return _result;
            }
        }

        private sealed class CapturingFeedbackSink : IVoiceUserFeedbackSink
        {
            private readonly List<VoiceUserFeedbackMessage> _messages;

            public CapturingFeedbackSink(List<VoiceUserFeedbackMessage> messages)
            {
                _messages = messages;
            }

            public void Emit(VoiceUserFeedbackMessage message)
            {
                _messages.Add(message);
            }
        }

        private sealed class CapturingRobotFeedbackSink : IRobotVoiceFeedbackSink
        {
            private readonly List<RobotVoiceFeedbackMessage> _messages;

            public CapturingRobotFeedbackSink(List<RobotVoiceFeedbackMessage> messages)
            {
                _messages = messages;
            }

            public void Emit(RobotVoiceFeedbackMessage message)
            {
                _messages.Add(message);
            }
        }

        private sealed class CapturingExperimentEventSink : IVoiceExperimentEventSink
        {
            private readonly List<VoiceExperimentEventRecord> _events;

            public CapturingExperimentEventSink(List<VoiceExperimentEventRecord> events)
            {
                _events = events;
            }

            public void Emit(string eventType, Dictionary<string, object> payload)
            {
                _events.Add(new VoiceExperimentEventRecord(eventType, payload));
            }
        }

        private readonly struct VoiceExperimentEventRecord
        {
            public VoiceExperimentEventRecord(string eventType, Dictionary<string, object> payload)
            {
                EventType = eventType;
                Payload = payload;
            }

            public string EventType { get; }
            public Dictionary<string, object> Payload { get; }
        }
    }
}
