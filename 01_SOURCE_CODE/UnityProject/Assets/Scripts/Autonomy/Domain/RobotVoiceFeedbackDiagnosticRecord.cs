using System;
using System.Globalization;
using System.Text;

namespace Autonomy.Domain
{
    public sealed class RobotVoiceFeedbackDiagnosticRecord
    {
        public string Timestamp { get; set; } = "";
        public string RunId { get; set; } = "";
        public int SequenceIndex { get; set; }
        public string FeedbackKind { get; set; } = "";
        public string FeedbackText { get; set; } = "";
        public string Transcript { get; set; } = "";
        public string NormalizedText { get; set; } = "";
        public string Intent { get; set; } = "";
        public string TargetAlias { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string Destination { get; set; } = "";
        public string Reason { get; set; } = "";
        public string ConditionId { get; set; } = "";
        public string TrialId { get; set; } = "";
        public string RoundId { get; set; } = "";
        public bool Accepted { get; set; }
        public string FeedbackOutcome { get; set; } = "";
        public string SinkType { get; set; } = "";

        public static RobotVoiceFeedbackDiagnosticRecord FromMessage(
            RobotVoiceFeedbackMessage message,
            string runId,
            int sequenceIndex,
            string sinkType)
        {
            RobotVoiceFeedbackContext context = message?.Context;
            return new RobotVoiceFeedbackDiagnosticRecord
            {
                Timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                RunId = runId ?? string.Empty,
                SequenceIndex = sequenceIndex,
                FeedbackKind = message?.Kind.ToString() ?? string.Empty,
                FeedbackText = message?.Text ?? string.Empty,
                Transcript = context?.RawTranscript ?? string.Empty,
                NormalizedText = context?.NormalizedText ?? string.Empty,
                Intent = context?.IntentKind.ToString() ?? string.Empty,
                TargetAlias = context?.TargetAlias ?? string.Empty,
                TargetId = context?.TargetId ?? string.Empty,
                Destination = context?.Destination ?? string.Empty,
                Reason = context?.Reason ?? string.Empty,
                ConditionId = context?.ConditionId ?? string.Empty,
                TrialId = context?.TrialId ?? string.Empty,
                RoundId = context?.RoundId ?? string.Empty,
                Accepted = context?.Accepted ?? false,
                FeedbackOutcome = ResolveOutcome(message?.Kind ?? RobotVoiceFeedbackKind.ControlCommandAcknowledged, context?.Accepted ?? false),
                SinkType = sinkType ?? string.Empty
            };
        }

        public static string ResolveOutcome(RobotVoiceFeedbackKind kind, bool accepted)
        {
            if (accepted || kind == RobotVoiceFeedbackKind.OrderAccepted)
            {
                return "accepted";
            }

            switch (kind)
            {
                case RobotVoiceFeedbackKind.ControlCommandAcknowledged:
                case RobotVoiceFeedbackKind.PendingCommandCancelled:
                    return "acknowledged";
                case RobotVoiceFeedbackKind.ClarificationRequested:
                case RobotVoiceFeedbackKind.NearestReferenceAmbiguous:
                    return "clarification";
                case RobotVoiceFeedbackKind.CommandRejectedAmbiguousTarget:
                    return "clarification";
                case RobotVoiceFeedbackKind.CurrentTaskStatus:
                    return "status";
                default:
                    return "rejected";
            }
        }

        public static string CsvHeader()
        {
            return string.Join(",",
                "timestamp",
                "run_id",
                "sequence_index",
                "feedback_kind",
                "feedback_text",
                "transcript",
                "normalized_text",
                "intent",
                "target_alias",
                "target_id",
                "destination",
                "reason",
                "condition_id",
                "trial_id",
                "round_id",
                "accepted",
                "feedback_outcome",
                "sink_type");
        }

        public string ToCsvRow()
        {
            return string.Join(",",
                Csv(Timestamp),
                Csv(RunId),
                SequenceIndex.ToString(CultureInfo.InvariantCulture),
                Csv(FeedbackKind),
                Csv(FeedbackText),
                Csv(Transcript),
                Csv(NormalizedText),
                Csv(Intent),
                Csv(TargetAlias),
                Csv(TargetId),
                Csv(Destination),
                Csv(Reason),
                Csv(ConditionId),
                Csv(TrialId),
                Csv(RoundId),
                Accepted ? "true" : "false",
                Csv(ResolveFeedbackOutcome()),
                Csv(SinkType));
        }

        public string ToJsonLine()
        {
            StringBuilder builder = new();
            builder.Append('{');
            Append(builder, "timestamp", Timestamp);
            Append(builder, "run_id", RunId);
            Append(builder, "sequence_index", SequenceIndex);
            Append(builder, "feedback_kind", FeedbackKind);
            Append(builder, "feedback_text", FeedbackText);
            Append(builder, "transcript", Transcript);
            Append(builder, "normalized_text", NormalizedText);
            Append(builder, "intent", Intent);
            Append(builder, "target_alias", TargetAlias);
            Append(builder, "target_id", TargetId);
            Append(builder, "destination", Destination);
            Append(builder, "reason", Reason);
            Append(builder, "condition_id", ConditionId);
            Append(builder, "trial_id", TrialId);
            Append(builder, "round_id", RoundId);
            Append(builder, "accepted", Accepted);
            Append(builder, "feedback_outcome", ResolveFeedbackOutcome());
            Append(builder, "sink_type", SinkType, last: true);
            builder.Append('}');
            return builder.ToString();
        }

        public string ResolveFeedbackOutcome()
        {
            if (!string.IsNullOrWhiteSpace(FeedbackOutcome))
            {
                return FeedbackOutcome;
            }

            return Enum.TryParse(FeedbackKind, out RobotVoiceFeedbackKind kind)
                ? ResolveOutcome(kind, Accepted)
                : (Accepted ? "accepted" : "rejected");
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static void Append(StringBuilder builder, string key, string value, bool last = false)
        {
            builder.Append('"').Append(EscapeJson(key)).Append("\":\"").Append(EscapeJson(value ?? string.Empty)).Append('"');
            if (!last)
            {
                builder.Append(',');
            }
        }

        private static void Append(StringBuilder builder, string key, int value, bool last = false)
        {
            builder.Append('"').Append(EscapeJson(key)).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
            if (!last)
            {
                builder.Append(',');
            }
        }

        private static void Append(StringBuilder builder, string key, bool value, bool last = false)
        {
            builder.Append('"').Append(EscapeJson(key)).Append("\":").Append(value ? "true" : "false");
            if (!last)
            {
                builder.Append(',');
            }
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
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }
    }
}
