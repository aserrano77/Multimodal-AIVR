using System;

namespace Autonomy.Domain
{
    public sealed class RobotVoiceFeedbackTtsFormatter
    {
        public TtsFeedbackUtterance Format(RobotVoiceFeedbackMessage message)
        {
            if (message == null)
            {
                return new TtsFeedbackUtterance(string.Empty, TtsFeedbackPriority.Normal, "message_null");
            }

            RobotVoiceFeedbackContext context = message.Context ?? new RobotVoiceFeedbackContext { Kind = message.Kind };
            string text = BuildText(message.Kind, context);
            return new TtsFeedbackUtterance(text, ResolvePriority(message.Kind), message.Kind.ToString());
        }

        private static string BuildText(RobotVoiceFeedbackKind kind, RobotVoiceFeedbackContext context)
        {
            switch (kind)
            {
                case RobotVoiceFeedbackKind.OrderAccepted:
                    return $"Orden aceptada. Voy a por {BoxPhrase(context)}.";
                case RobotVoiceFeedbackKind.TargetChanged:
                    return $"Orden aceptada: cambio de objetivo y voy a llevar {BoxPhrase(context, context.CurrentTargetAlias)} {DestinationPhrase(context)}.";
                case RobotVoiceFeedbackKind.CommandQueuedAsPending:
                    return $"Orden recibida: la ejecutar\u00e9 cuando termine de depositar {CurrentBoxPhrase(context)}.";
                case RobotVoiceFeedbackKind.PendingCommandReplaced:
                    return $"Orden pendiente actualizada: despu\u00e9s llevar\u00e9 {BoxPhrase(context)} {DestinationPhrase(context)}.";
                case RobotVoiceFeedbackKind.CommandRejectedAmbiguousTarget:
                    return "Orden rechazada: hay varias cajas compatibles. Indica el identificador de la caja.";
                case RobotVoiceFeedbackKind.CommandRejectedUnrecognized:
                    return "Orden rechazada: no he entendido la caja o el destino solicitados.";
                case RobotVoiceFeedbackKind.CommandRejectedNotExecutable:
                    return "Orden rechazada: no puedo ejecutar la tarea solicitada con la informaci\u00f3n disponible.";
                case RobotVoiceFeedbackKind.CommandRejectedTargetNotFound:
                    return "Orden rechazada. No encuentro esa caja.";
                case RobotVoiceFeedbackKind.CommandRejectedTargetUnavailable:
                    return $"Orden rechazada: {BoxPhrase(context)} ya no est\u00e1 disponible.";
                case RobotVoiceFeedbackKind.CommandRejectedTaskInProgress:
                case RobotVoiceFeedbackKind.CommandRejectedStoppedByVoice:
                    return "Estoy detenido. Di 'contin\u00faa' o 'retoma la tarea' para seguir.";
                case RobotVoiceFeedbackKind.RobotBusy:
                    return "Orden rechazada. Estoy ocupado.";
                case RobotVoiceFeedbackKind.ClarificationRequested:
                case RobotVoiceFeedbackKind.NearestReferenceAmbiguous:
                    return "Necesito una aclaraci\u00f3n: dime si te refieres a la caja m\u00e1s cercana a ti o al robot.";
                case RobotVoiceFeedbackKind.CurrentTaskStatus:
                    return $"Tarea en curso: estoy llevando {BoxPhrase(context)} {DestinationPhrase(context)}.";
                case RobotVoiceFeedbackKind.ControlCommandAcknowledged:
                    return ControlCommandText(context);
                case RobotVoiceFeedbackKind.AlreadyStopped:
                    return "Ya estoy parado.";
                case RobotVoiceFeedbackKind.PendingCommandCancelled:
                    return "Orden cancelada: detengo la orden pendiente.";
                default:
                    return string.IsNullOrWhiteSpace(context.Text)
                        ? "Orden recibida: inicio la tarea solicitada."
                        : SanitizeUserText(context.Text);
            }
        }

        private static TtsFeedbackPriority ResolvePriority(RobotVoiceFeedbackKind kind)
        {
            switch (kind)
            {
                case RobotVoiceFeedbackKind.CommandQueuedAsPending:
                case RobotVoiceFeedbackKind.PendingCommandReplaced:
                case RobotVoiceFeedbackKind.CommandRejectedAmbiguousTarget:
                case RobotVoiceFeedbackKind.CommandRejectedUnrecognized:
                case RobotVoiceFeedbackKind.CommandRejectedNotExecutable:
                case RobotVoiceFeedbackKind.CommandRejectedTargetNotFound:
                case RobotVoiceFeedbackKind.CommandRejectedTargetUnavailable:
                case RobotVoiceFeedbackKind.CommandRejectedTaskInProgress:
                case RobotVoiceFeedbackKind.CommandRejectedStoppedByVoice:
                case RobotVoiceFeedbackKind.RobotBusy:
                case RobotVoiceFeedbackKind.PendingCommandCancelled:
                case RobotVoiceFeedbackKind.ControlCommandAcknowledged:
                case RobotVoiceFeedbackKind.AlreadyStopped:
                    return TtsFeedbackPriority.Critical;
                default:
                    return TtsFeedbackPriority.Normal;
            }
        }

        private static string ControlCommandText(RobotVoiceFeedbackContext context)
        {
            switch (context.IntentKind)
            {
                case VoiceCommandIntentKind.Stop:
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
                    return "Orden recibida: contin\u00fao con la tarea.";
                case VoiceCommandIntentKind.CancelPendingOrder:
                    return "Orden cancelada: detengo la orden pendiente.";
                default:
                    return "Orden recibida: inicio la tarea solicitada.";
            }
        }

        private static string TaskPhrase(RobotVoiceFeedbackContext context)
        {
            if (!string.IsNullOrWhiteSpace(context.RobotTaskState))
            {
                string state = context.RobotTaskState.Trim().ToLowerInvariant();
                if (state.Contains("pick") || state.Contains("recog"))
                {
                    return $"recogiendo {CurrentBoxPhrase(context)}";
                }

                if (state.Contains("place") || state.Contains("deposit"))
                {
                    return $"depositando {CurrentBoxPhrase(context)}";
                }

                if (state.Contains("navig") || state.Contains("move"))
                {
                    return $"yendo hacia {CurrentBoxPhrase(context)}";
                }
            }

            if (!string.IsNullOrWhiteSpace(context.CurrentTargetAlias))
            {
                return $"depositando la caja {SanitizeUserText(context.CurrentTargetAlias)}";
            }

            if (!string.IsNullOrWhiteSpace(context.PreviousTargetAlias))
            {
                return $"recogiendo la caja {SanitizeUserText(context.PreviousTargetAlias)}";
            }

            return "ejecutando la tarea actual";
        }

        private static string CurrentBoxPhrase(RobotVoiceFeedbackContext context)
        {
            if (!string.IsNullOrWhiteSpace(context.CurrentTargetAlias))
            {
                return $"la caja {SanitizeUserText(context.CurrentTargetAlias)}";
            }

            if (!string.IsNullOrWhiteSpace(context.PreviousTargetAlias))
            {
                return $"la caja {SanitizeUserText(context.PreviousTargetAlias)}";
            }

            return "la caja actual";
        }

        private static string BoxPhrase(RobotVoiceFeedbackContext context, string preferredAlias = "")
        {
            string label = FirstNonEmpty(preferredAlias, context.TargetAlias, context.TargetId);
            return string.IsNullOrWhiteSpace(label)
                ? "la caja solicitada"
                : $"la caja {SanitizeUserText(label)}";
        }

        private static string DestinationPhrase(RobotVoiceFeedbackContext context)
        {
            string destination = context?.Destination ?? string.Empty;
            if (string.IsNullOrWhiteSpace(destination) ||
                string.Equals(destination, "SELF", StringComparison.OrdinalIgnoreCase))
            {
                return "a su zona de dep\u00f3sito";
            }

            const string zonePrefix = "Zone";
            if (destination.StartsWith(zonePrefix, StringComparison.OrdinalIgnoreCase) &&
                destination.Length > zonePrefix.Length)
            {
                return $"a la zona {SanitizeUserText(destination.Substring(zonePrefix.Length).ToUpperInvariant())}";
            }

            string trimmed = destination.Trim();
            if (trimmed.Length == 1 && char.IsLetterOrDigit(trimmed[0]))
            {
                return $"a la zona {trimmed.ToUpperInvariant()}";
            }

            return $"a {SanitizeUserText(trimmed)}";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string SanitizeUserText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim();
            return trimmed
                .Replace("_", " ")
                .Replace("AssistedSelection", "asistencia robotica")
                .Replace("PickAndPlace", "transporte de caja");
        }
    }
}
