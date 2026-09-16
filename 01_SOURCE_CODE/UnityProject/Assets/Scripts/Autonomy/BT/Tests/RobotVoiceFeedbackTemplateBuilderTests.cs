using Autonomy.Domain;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class RobotVoiceFeedbackTemplateBuilderTests
    {
        private RobotVoiceFeedbackTemplateBuilder _builder;

        [SetUp]
        public void SetUp()
        {
            _builder = new RobotVoiceFeedbackTemplateBuilder();
        }

        [Test]
        public void OrderAccepted_WithAliasAndSelfDestination_UsesClosedSpanishTemplate()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.OrderAccepted,
                TargetAlias = "A1",
                Destination = "SELF"
            });

            Assert.That(message.Text, Is.EqualTo("Orden recibida: llevar\u00e9 la caja A1 a su zona."));
        }

        [TestCase("A")]
        [TestCase("ZoneA")]
        public void OrderAccepted_WithZoneDestination_UsesNaturalZoneLabel(string destination)
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.OrderAccepted,
                TargetAlias = "A",
                Destination = destination
            });

            Assert.That(message.Text, Is.EqualTo("Orden recibida: llevar\u00e9 la caja A a la zona A."));
            Assert.That(message.Text, Does.Not.Contain(" a A."));
        }

        [TestCase("A")]
        [TestCase("ZoneA")]
        public void OrderAccepted_WithVisibleAliasAndInternalZoneDestination_UsesNaturalZoneLabel(string destination)
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.OrderAccepted,
                TargetAlias = "A1",
                Destination = destination
            });

            Assert.That(message.Context.Destination, Is.EqualTo(destination));
            Assert.That(message.Text, Is.EqualTo("Orden recibida: llevar\u00e9 la caja A1 a la zona A."));
            Assert.That(message.Text, Does.Not.Contain(" a A."));
            Assert.That(message.Text, Does.Not.Contain("a ZoneA"));
        }

        [Test]
        public void ClarificationRequested_ForNearestAmbiguity_AsksReference()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.NearestReferenceAmbiguous,
                IntentKind = VoiceCommandIntentKind.NearestAmbiguity
            });

            Assert.That(message.Text, Is.EqualTo("\u00bfM\u00e1s cercana a ti o m\u00e1s cercana a m\u00ed?"));
        }

        [Test]
        public void TargetNotFound_WithAlias_UsesTargetNotFoundTemplate()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.CommandRejectedTargetNotFound,
                TargetAlias = "A2"
            });

            Assert.That(message.Text, Is.EqualTo("No encuentro la caja A2."));
        }

        [Test]
        public void CommandRejectedUnrecognized_UsesExplicitNotUnderstoodTemplate()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.CommandRejectedUnrecognized,
                IntentKind = VoiceCommandIntentKind.None,
                Reason = "normalization_not_executable_Unrecognized"
            });

            Assert.That(message.Text, Is.EqualTo("No he entendido la orden. Repite el comando indicando la caja y el destino."));
            Assert.That(message.Text, Is.Not.EqualTo("Comando recibido."));
        }

        [Test]
        public void UnrecognizedIntent_FromMapping_UsesCommandRejectedUnrecognized()
        {
            VoiceUserFeedbackMessage feedback = BuildMappedFeedback(
                VoiceCommandIntentKind.None,
                VoiceCommandIntentMappingStatus.NotMapped,
                string.Empty,
                string.Empty,
                "normalization_not_executable_Unrecognized");

            RobotVoiceFeedbackMessage message = _builder.Build(_builder.BuildContext(feedback));

            Assert.That(message.Kind, Is.EqualTo(RobotVoiceFeedbackKind.CommandRejectedUnrecognized));
            Assert.That(message.Text, Is.EqualTo("No he entendido la orden. Repite el comando indicando la caja y el destino."));
        }

        [TestCase("A1", "SELF")]
        [TestCase("Z9", "SELF")]
        public void PickAndPlaceNotMapped_WithAlias_UsesTargetNotFoundNotUnrecognized(string alias, string destination)
        {
            VoiceUserFeedbackMessage feedback = BuildMappedFeedback(
                VoiceCommandIntentKind.PickAndPlace,
                VoiceCommandIntentMappingStatus.NotMapped,
                alias,
                destination,
                "mapping_not_executable_NotMapped");

            RobotVoiceFeedbackMessage message = _builder.Build(_builder.BuildContext(feedback));

            Assert.That(message.Kind, Is.EqualTo(RobotVoiceFeedbackKind.CommandRejectedTargetNotFound));
            Assert.That(message.Kind, Is.Not.EqualTo(RobotVoiceFeedbackKind.CommandRejectedUnrecognized));
            Assert.That(message.Text, Is.EqualTo($"No encuentro la caja {alias}."));
            Assert.That(message.Text, Does.Not.Contain("No he entendido la orden"));
        }

        [Test]
        public void ControlCommandAcknowledged_Stop_Rejected_UsesHonestTemplate()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.ControlCommandAcknowledged,
                IntentKind = VoiceCommandIntentKind.Stop
            });

            Assert.That(message.Text, Is.EqualTo("No puedo detener la tarea en este estado."));
        }

        [Test]
        public void ControlCommandAcknowledged_Stop_Accepted_UsesCancellationTemplate()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.ControlCommandAcknowledged,
                IntentKind = VoiceCommandIntentKind.Stop,
                Accepted = true
            });

            Assert.That(message.Text, Is.EqualTo("Orden recibida: detengo la tarea actual."));
        }

        [Test]
        public void ControlCommandAcknowledged_Stop_Holding_UsesSafeHoldTemplate()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.ControlCommandAcknowledged,
                IntentKind = VoiceCommandIntentKind.Stop,
                Accepted = true,
                Reason = "safe_stop_with_held_object"
            });

            Assert.That(message.Text, Is.EqualTo("Orden recibida: me detengo y mantengo el objeto sujeto."));
        }

        [Test]
        public void RepeatedStopReason_UsesAlreadyStoppedStructuredKind()
        {
            VoiceCommandNormalizationResult normalization = new(
                "detente",
                "detente",
                "detente",
                VoiceCommandRecognitionStatus.Recognized,
                1f,
                "detente",
                VoiceCommandActionToken.Stop,
                ObjectToken.Unknown,
                string.Empty,
                string.Empty,
                null,
                string.Empty);
            VoiceCommandIntentMappingResult mapping = new(
                VoiceCommandIntentMappingStatus.Mapped,
                VoiceCommandIntentMappingError.None,
                VoiceCommandIntentKind.Stop,
                null,
                normalization,
                "Stop",
                "recognized stop intent");
            VoiceUserFeedbackMessage feedback = new(
                VoiceUserFeedbackType.CommandExecuted,
                "Ya estoy parado.",
                "already_stopped_idempotent_noop",
                null,
                mapping);

            RobotVoiceFeedbackMessage message = _builder.Build(_builder.BuildContext(feedback));

            Assert.That(message.Kind, Is.EqualTo(RobotVoiceFeedbackKind.AlreadyStopped));
            Assert.That(message.Context.Reason, Is.EqualTo("already_stopped_idempotent_noop"));
            Assert.That(message.Text, Is.EqualTo("Ya estoy parado."));
        }

        [Test]
        public void ControlCommandAcknowledged_Resume_Accepted_UsesResumeTemplate()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.ControlCommandAcknowledged,
                IntentKind = VoiceCommandIntentKind.Resume,
                Accepted = true
            });

            Assert.That(message.Text, Is.EqualTo("Orden recibida: reanudo la tarea detenida."));
        }

        [Test]
        public void ControlCommandAcknowledged_Resume_WithoutSnapshot_UsesHonestRejection()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.ControlCommandAcknowledged,
                IntentKind = VoiceCommandIntentKind.Resume,
                Reason = "no_stopped_task_to_resume"
            });

            Assert.That(message.Text, Is.EqualTo("No hay ninguna tarea detenida que pueda reanudar."));
            Assert.That(message.Text, Does.Not.Contain("reanudo"));
        }

        [Test]
        public void ControlCommandAcknowledged_Resume_Unsafe_UsesSafetyRejection()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.ControlCommandAcknowledged,
                IntentKind = VoiceCommandIntentKind.Resume,
                Reason = "stopped_task_held_object_inconsistent"
            });

            Assert.That(message.Text, Is.EqualTo("No puedo reanudar la tarea detenida porque el estado ya no es seguro."));
        }

        [Test]
        public void RobotBusy_UsesTaskInProgressTemplate()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.RobotBusy
            });

            Assert.That(message.Text, Is.EqualTo("Estoy ocupado ahora. Terminar\u00e9 la acci\u00f3n actual antes de aceptar otra orden."));
        }

        [Test]
        public void PendingCommandReplaced_IsTemplateOnly()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.PendingCommandReplaced,
                TargetAlias = "B1"
            });

            Assert.That(message.Text, Is.EqualTo("Actualizo la orden pendiente: despu\u00e9s llevar\u00e9 B1."));
        }

        [Test]
        public void CancelPendingOrder_IsAcknowledgementOnly()
        {
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.PendingCommandCancelled,
                IntentKind = VoiceCommandIntentKind.CancelPendingOrder
            });

            Assert.That(message.Text, Is.EqualTo("Cancelo la orden pendiente."));
        }

        [Test]
        public void CancelPendingOrder_RecognizedIntent_TakesPrecedenceOverUnrecognizedReason()
        {
            VoiceCommandNormalizationResult normalization = new(
                "cancela la orden pendiente",
                "cancela la orden pendiente",
                "cancela la orden pendiente",
                VoiceCommandRecognitionStatus.Recognized,
                1f,
                "cancela la orden pendiente",
                VoiceCommandActionToken.Unknown,
                ObjectToken.Unknown,
                string.Empty,
                string.Empty,
                null,
                string.Empty);
            VoiceCommandIntentMappingResult mapping = new(
                VoiceCommandIntentMappingStatus.Mapped,
                VoiceCommandIntentMappingError.DiagnosticIntentNotExecutable,
                VoiceCommandIntentKind.CancelPendingOrder,
                null,
                normalization,
                "Cancel pending order",
                "recognized diagnostic cancel-pending-order intent");
            VoiceUserFeedbackMessage feedback = new(
                VoiceUserFeedbackType.CommandBlockedByCondition,
                "Cancelo la orden pendiente.",
                "normalization_not_executable_Unrecognized",
                null,
                mapping);

            RobotVoiceFeedbackMessage message = _builder.Build(_builder.BuildContext(feedback));

            Assert.That(message.Kind, Is.EqualTo(RobotVoiceFeedbackKind.PendingCommandCancelled));
            Assert.That(message.Kind, Is.Not.EqualTo(RobotVoiceFeedbackKind.CommandRejectedUnrecognized));
            Assert.That(message.Text, Is.EqualTo("Cancelo la orden pendiente."));
        }

        [Test]
        public void DebugSink_CapturesLastMessage()
        {
            var emitted = new List<RobotVoiceFeedbackMessage>();
            var sink = new DebugRobotVoiceFeedbackSink(emitted.Add);
            RobotVoiceFeedbackMessage message = _builder.Build(new RobotVoiceFeedbackContext
            {
                Kind = RobotVoiceFeedbackKind.OrderAccepted,
                TargetAlias = "A1",
                Destination = "SELF"
            });

            sink.Emit(message);

            Assert.That(sink.LastMessage, Is.SameAs(message));
            Assert.That(emitted.Single(), Is.SameAs(message));
        }

        private static VoiceUserFeedbackMessage BuildMappedFeedback(
            VoiceCommandIntentKind intentKind,
            VoiceCommandIntentMappingStatus status,
            string alias,
            string destination,
            string reason)
        {
            VoiceCommandNormalizationResult normalization = new(
                string.IsNullOrWhiteSpace(alias) ? "bada" : $"lleva la caja {alias} a su zona",
                string.IsNullOrWhiteSpace(alias) ? "bada" : $"lleva la caja {alias} a su zona",
                string.IsNullOrWhiteSpace(alias) ? "bada" : $"lleva la caja {alias} a su zona",
                string.IsNullOrWhiteSpace(alias)
                    ? VoiceCommandRecognitionStatus.Unrecognized
                    : VoiceCommandRecognitionStatus.Recognized,
                string.IsNullOrWhiteSpace(alias) ? 0f : 0.95f,
                string.Empty,
                intentKind == VoiceCommandIntentKind.PickAndPlace
                    ? VoiceCommandActionToken.Transport
                    : VoiceCommandActionToken.Unknown,
                intentKind == VoiceCommandIntentKind.PickAndPlace
                    ? ObjectToken.Box
                    : ObjectToken.Unknown,
                alias,
                destination,
                null,
                string.Empty);
            VoiceCommandIntentMappingResult mapping = new(
                status,
                status == VoiceCommandIntentMappingStatus.Mapped
                    ? VoiceCommandIntentMappingError.None
                    : VoiceCommandIntentMappingError.NormalizationUnrecognized,
                intentKind,
                null,
                normalization,
                string.Empty,
                "mapping diagnostic");

            return new VoiceUserFeedbackMessage(
                VoiceUserFeedbackType.CommandUnrecognized,
                "Comando no reconocido.",
                reason,
                null,
                mapping);
        }
    }
}
