using System;

namespace Autonomy.Domain
{
    public sealed class RobotVoiceFeedbackTemplateBuilder
    {
        public RobotVoiceFeedbackMessage Build(RobotVoiceFeedbackContext context)
        {
            context ??= new RobotVoiceFeedbackContext { Kind = RobotVoiceFeedbackKind.ControlCommandAcknowledged };
            string text = string.IsNullOrWhiteSpace(context.Text)
                ? BuildText(context)
                : context.Text;
            return new RobotVoiceFeedbackMessage(context.Kind, text, context);
        }

        public RobotVoiceFeedbackContext BuildContext(
            VoiceUserFeedbackMessage feedback,
            VoiceCommandNormalizationResult normalization = null,
            VoiceCommandIntentMappingResult mapping = null,
            MultimodalTaskIntent intent = null)
        {
            mapping ??= feedback?.Mapping;
            intent ??= feedback?.Intent ?? mapping?.TaskIntent;
            normalization ??= mapping?.Normalization;
            string reason = feedback?.Reason ?? string.Empty;
            return new RobotVoiceFeedbackContext
            {
                Kind = ResolveKind(feedback, mapping, intent, reason),
                RawTranscript = normalization?.RawTranscript ?? mapping?.RawTranscript ?? string.Empty,
                NormalizedText = normalization?.NormalizedText ?? mapping?.NormalizedText ?? string.Empty,
                IntentKind = mapping?.IntentKind ?? VoiceCommandIntentKind.None,
                TargetAlias = ResolveTargetAlias(normalization, intent),
                TargetId = intent?.TargetId ?? string.Empty,
                Destination = ResolveDestination(normalization, intent),
                CandidateCount = 0,
                Reason = reason,
                Accepted = ResolveAccepted(feedback)
            };
        }

        private static bool ResolveAccepted(VoiceUserFeedbackMessage feedback)
        {
            return feedback?.Type == VoiceUserFeedbackType.CommandExecuted ||
                feedback?.Type == VoiceUserFeedbackType.ConfirmationAccepted;
        }

        private static RobotVoiceFeedbackKind ResolveKind(
            VoiceUserFeedbackMessage feedback,
            VoiceCommandIntentMappingResult mapping,
            MultimodalTaskIntent intent,
            string reason)
        {
            if (mapping?.IntentKind == VoiceCommandIntentKind.NearestAmbiguity)
            {
                return RobotVoiceFeedbackKind.NearestReferenceAmbiguous;
            }

            if (mapping?.IntentKind == VoiceCommandIntentKind.CancelPendingOrder)
            {
                return RobotVoiceFeedbackKind.PendingCommandCancelled;
            }

            if (mapping?.IntentKind == VoiceCommandIntentKind.Stop &&
                string.Equals(reason, "already_stopped_idempotent_noop", StringComparison.OrdinalIgnoreCase))
            {
                return RobotVoiceFeedbackKind.AlreadyStopped;
            }

            if (mapping?.IntentKind == VoiceCommandIntentKind.Continue ||
                mapping?.IntentKind == VoiceCommandIntentKind.Resume ||
                mapping?.IntentKind == VoiceCommandIntentKind.Stop ||
                mapping?.IntentKind == VoiceCommandIntentKind.NearestToUser ||
                mapping?.IntentKind == VoiceCommandIntentKind.NearestToRobot)
            {
                return RobotVoiceFeedbackKind.ControlCommandAcknowledged;
            }

            if (mapping?.IntentKind == VoiceCommandIntentKind.PickAndPlace &&
                mapping.Status != VoiceCommandIntentMappingStatus.Mapped)
            {
                return HasTargetEvidence(mapping)
                    ? RobotVoiceFeedbackKind.CommandRejectedTargetNotFound
                    : RobotVoiceFeedbackKind.CommandRejectedNotExecutable;
            }

            if (IsTrulyUnrecognized(feedback, mapping, reason))
            {
                return RobotVoiceFeedbackKind.CommandRejectedUnrecognized;
            }

            if (string.Equals(reason, "task_in_progress", StringComparison.OrdinalIgnoreCase))
            {
                return RobotVoiceFeedbackKind.RobotBusy;
            }

            if (string.Equals(reason, "voice_stop_latched_pick_and_place_rejected", StringComparison.OrdinalIgnoreCase))
            {
                return RobotVoiceFeedbackKind.CommandRejectedStoppedByVoice;
            }

            if (string.Equals(mapping?.Explanation, "target_alias_not_found", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mapping?.Explanation, "category_not_found", StringComparison.OrdinalIgnoreCase))
            {
                return RobotVoiceFeedbackKind.CommandRejectedTargetNotFound;
            }

            if (string.Equals(mapping?.Explanation, "multiple_matching_boxes", StringComparison.OrdinalIgnoreCase))
            {
                return RobotVoiceFeedbackKind.CommandRejectedAmbiguousTarget;
            }

            if (feedback?.Type == VoiceUserFeedbackType.CommandExecuted)
            {
                return RobotVoiceFeedbackKind.OrderAccepted;
            }

            if (feedback?.Type == VoiceUserFeedbackType.CommandBlockedByCondition)
            {
                return RobotVoiceFeedbackKind.CommandRejectedTaskInProgress;
            }

            return RobotVoiceFeedbackKind.ControlCommandAcknowledged;
        }

        private static bool IsTrulyUnrecognized(
            VoiceUserFeedbackMessage feedback,
            VoiceCommandIntentMappingResult mapping,
            string reason)
        {
            bool hasRecognizedIntent = mapping != null && mapping.IntentKind != VoiceCommandIntentKind.None;
            if (hasRecognizedIntent)
            {
                return false;
            }

            return feedback?.Type == VoiceUserFeedbackType.CommandUnrecognized ||
                mapping?.IntentKind == VoiceCommandIntentKind.None ||
                string.Equals(reason, "normalization_not_executable_Unrecognized", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasTargetEvidence(VoiceCommandIntentMappingResult mapping)
        {
            return !string.IsNullOrWhiteSpace(mapping?.Normalization?.ObjectLabel) ||
                !string.IsNullOrWhiteSpace(mapping?.TaskIntent?.TargetId) ||
                !string.IsNullOrWhiteSpace(mapping?.TaskIntent?.ObjectCategory);
        }

        private static string BuildText(RobotVoiceFeedbackContext context)
        {
            switch (context.Kind)
            {
                case RobotVoiceFeedbackKind.OrderAccepted:
                    return $"Orden recibida: llevar\u00e9 la caja {BoxLabel(context)} {DestinationPhrase(context)}.";
                case RobotVoiceFeedbackKind.TargetChanged:
                    return $"Cambio de objetivo: llevar\u00e9 {ValueOrFallback(context.CurrentTargetAlias, BoxLabel(context))} en lugar de {ValueOrFallback(context.PreviousTargetAlias, "la anterior")}.";
                case RobotVoiceFeedbackKind.CommandQueuedAsPending:
                    return $"Estoy colocando {ValueOrFallback(context.CurrentTargetAlias, "la caja actual")}. Despu\u00e9s llevar\u00e9 {BoxLabel(context)}.";
                case RobotVoiceFeedbackKind.PendingCommandReplaced:
                    return $"Actualizo la orden pendiente: despu\u00e9s llevar\u00e9 {BoxLabel(context)}.";
                case RobotVoiceFeedbackKind.CommandRejectedAmbiguousTarget:
                    return "Hay varias cajas compatibles. Indica el alias de la caja, por ejemplo B1 o B2.";
                case RobotVoiceFeedbackKind.CommandRejectedUnrecognized:
                    return "No he entendido la orden. Repite el comando indicando la caja y el destino.";
                case RobotVoiceFeedbackKind.CommandRejectedNotExecutable:
                    return "He entendido la orden, pero no puedo ejecutarla con la informacion disponible.";
                case RobotVoiceFeedbackKind.CommandRejectedTargetNotFound:
                    return $"No encuentro la caja {BoxLabel(context)}.";
                case RobotVoiceFeedbackKind.CommandRejectedTargetUnavailable:
                    return $"La caja {BoxLabel(context)} ya no est\u00e1 disponible.";
                case RobotVoiceFeedbackKind.CommandRejectedTaskInProgress:
                case RobotVoiceFeedbackKind.CommandRejectedStoppedByVoice:
                    return "Estoy detenido. Di 'contin\u00faa' o 'retoma la tarea' para seguir.";
                case RobotVoiceFeedbackKind.RobotBusy:
                    return "Estoy ocupado ahora. Terminar\u00e9 la acci\u00f3n actual antes de aceptar otra orden.";
                case RobotVoiceFeedbackKind.ClarificationRequested:
                case RobotVoiceFeedbackKind.NearestReferenceAmbiguous:
                    return "\u00bfM\u00e1s cercana a ti o m\u00e1s cercana a m\u00ed?";
                case RobotVoiceFeedbackKind.CurrentTaskStatus:
                    return $"Ahora estoy llevando {BoxLabel(context)} {DestinationPhrase(context)}.";
                case RobotVoiceFeedbackKind.ControlCommandAcknowledged:
                    return BuildControlAcknowledgement(context);
                case RobotVoiceFeedbackKind.AlreadyStopped:
                    return "Ya estoy parado.";
                case RobotVoiceFeedbackKind.PendingCommandCancelled:
                    return "Cancelo la orden pendiente.";
                default:
                    return "Comando recibido.";
            }
        }

        private static string BuildControlAcknowledgement(RobotVoiceFeedbackContext context)
        {
            switch (context.IntentKind)
            {
                case VoiceCommandIntentKind.Stop:
                    if (!context.Accepted)
                    {
                        return "No puedo detener la tarea en este estado.";
                    }

                    if (string.Equals(context.Reason, "safe_stop_with_held_object", StringComparison.OrdinalIgnoreCase))
                    {
                        return "Orden recibida: me detengo y mantengo el objeto sujeto.";
                    }

                    if (string.Equals(context.Reason, "no_active_task_to_cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        return "No hay tarea activa; permanezco detenido.";
                    }

                    return "Orden recibida: detengo la tarea actual.";
                case VoiceCommandIntentKind.Resume:
                    if (context.Accepted)
                    {
                        return "Orden recibida: reanudo la tarea detenida.";
                    }

                    if (string.Equals(context.Reason, "no_stopped_task_to_resume", StringComparison.OrdinalIgnoreCase))
                    {
                        return "No hay ninguna tarea detenida que pueda reanudar.";
                    }

                    return "No puedo reanudar la tarea detenida porque el estado ya no es seguro.";
                case VoiceCommandIntentKind.Continue:
                    return "Contin\u00fao con la tarea.";
                case VoiceCommandIntentKind.CancelPendingOrder:
                    return "Cancelo la orden pendiente.";
                default:
                    return "Comando recibido.";
            }
        }

        private static string DestinationPhrase(RobotVoiceFeedbackContext context)
        {
            string destination = context?.Destination ?? string.Empty;
            if (string.Equals(destination, "SELF", StringComparison.OrdinalIgnoreCase))
            {
                return "a su zona";
            }

            const string zonePrefix = "Zone";
            if (destination.StartsWith(zonePrefix, StringComparison.OrdinalIgnoreCase) && destination.Length > zonePrefix.Length)
            {
                return $"a la zona {destination.Substring(zonePrefix.Length).ToUpperInvariant()}";
            }

            string simpleZoneLabel = SimpleZoneLabelOrEmpty(destination);
            if (!string.IsNullOrWhiteSpace(simpleZoneLabel))
            {
                return $"a la zona {simpleZoneLabel}";
            }

            return string.IsNullOrWhiteSpace(destination) ? "a su zona" : $"a {destination}";
        }

        private static string SimpleZoneLabelOrEmpty(string destination)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                return string.Empty;
            }

            string trimmed = destination.Trim();
            return trimmed.Length == 1 && char.IsLetterOrDigit(trimmed[0])
                ? trimmed.ToUpperInvariant()
                : string.Empty;
        }

        private static string BoxLabel(RobotVoiceFeedbackContext context)
        {
            return ValueOrFallback(context?.TargetAlias, ValueOrFallback(context?.TargetId, "indicada"));
        }

        private static string ResolveTargetAlias(VoiceCommandNormalizationResult normalization, MultimodalTaskIntent intent)
        {
            if (!string.IsNullOrWhiteSpace(normalization?.ObjectLabel))
            {
                return normalization.ObjectLabel;
            }

            if (!string.IsNullOrWhiteSpace(intent?.TargetId))
            {
                return intent.TargetId;
            }

            return intent?.ObjectCategory ?? string.Empty;
        }

        private static string ResolveDestination(VoiceCommandNormalizationResult normalization, MultimodalTaskIntent intent)
        {
            if (!string.IsNullOrWhiteSpace(normalization?.DestinationLabel))
            {
                return normalization.DestinationLabel;
            }

            return intent?.PlaceTargetId ?? string.Empty;
        }

        private static string ValueOrFallback(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
