using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Autonomy.Domain
{
    public sealed class RobotVoiceFeedbackDiagnosticSummary
    {
        public RobotVoiceFeedbackDiagnosticSummary(
            string runId,
            int totalFeedbackCount,
            IReadOnlyDictionary<string, int> countsByKind,
            int acceptedCount,
            int rejectedCount,
            int acknowledgedCount,
            int clarificationCount,
            bool hasAcceptanceData)
        {
            RunId = runId ?? string.Empty;
            TotalFeedbackCount = totalFeedbackCount;
            CountsByKind = countsByKind ?? new Dictionary<string, int>();
            AcceptedCount = acceptedCount;
            RejectedCount = rejectedCount;
            AcknowledgedCount = acknowledgedCount;
            ClarificationCount = clarificationCount;
            HasAcceptanceData = hasAcceptanceData;
        }

        public string RunId { get; }
        public int TotalFeedbackCount { get; }
        public IReadOnlyDictionary<string, int> CountsByKind { get; }
        public int AcceptedCount { get; }
        public int RejectedCount { get; }
        public int AcknowledgedCount { get; }
        public int ClarificationCount { get; }
        public bool HasAcceptanceData { get; }

        public static RobotVoiceFeedbackDiagnosticSummary Calculate(
            IEnumerable<RobotVoiceFeedbackDiagnosticRecord> records,
            string runId)
        {
            RobotVoiceFeedbackDiagnosticRecord[] rows = (records ?? Enumerable.Empty<RobotVoiceFeedbackDiagnosticRecord>()).ToArray();
            Dictionary<string, int> counts = rows
                .GroupBy(record => string.IsNullOrWhiteSpace(record.FeedbackKind) ? "Unknown" : record.FeedbackKind)
                .OrderBy(group => group.Key)
                .ToDictionary(group => group.Key, group => group.Count());

            return new RobotVoiceFeedbackDiagnosticSummary(
                runId,
                rows.Length,
                counts,
                rows.Count(record => string.Equals(record.ResolveFeedbackOutcome(), "accepted", System.StringComparison.OrdinalIgnoreCase)),
                rows.Count(record => string.Equals(record.ResolveFeedbackOutcome(), "rejected", System.StringComparison.OrdinalIgnoreCase)),
                rows.Count(record => string.Equals(record.ResolveFeedbackOutcome(), "acknowledged", System.StringComparison.OrdinalIgnoreCase)),
                rows.Count(record => string.Equals(record.ResolveFeedbackOutcome(), "clarification", System.StringComparison.OrdinalIgnoreCase)),
                rows.Length > 0);
        }

        public string ToMarkdown(IEnumerable<RobotVoiceFeedbackDiagnosticRecord> records)
        {
            RobotVoiceFeedbackDiagnosticRecord[] rows = (records ?? Enumerable.Empty<RobotVoiceFeedbackDiagnosticRecord>()).ToArray();
            StringBuilder builder = new();
            builder.AppendLine("# Voice Feedback Diagnostic Summary");
            builder.AppendLine();
            builder.AppendLine($"- run_id: {RunId}");
            builder.AppendLine($"- total_feedbacks: {TotalFeedbackCount}");
            if (HasAcceptanceData)
            {
                builder.AppendLine($"- accepted_feedbacks: {AcceptedCount}");
                builder.AppendLine($"- rejected_feedbacks: {RejectedCount}");
                builder.AppendLine($"- acknowledged_feedbacks: {AcknowledgedCount}");
                builder.AppendLine($"- clarification_feedbacks: {ClarificationCount}");
            }
            else
            {
                builder.AppendLine("- accepted_feedbacks: n/a");
                builder.AppendLine("- rejected_feedbacks: n/a");
                builder.AppendLine("- acknowledged_feedbacks: n/a");
                builder.AppendLine("- clarification_feedbacks: n/a");
            }

            builder.AppendLine();
            builder.AppendLine("## Counts By Feedback Kind");
            if (CountsByKind.Count == 0)
            {
                builder.AppendLine("- none");
            }
            else
            {
                foreach (KeyValuePair<string, int> pair in CountsByKind)
                {
                    builder.AppendLine($"- {pair.Key}: {pair.Value}");
                }
            }

            if (rows.Length == 0)
            {
                builder.AppendLine();
                builder.AppendLine("## Warnings");
                builder.AppendLine("- no voice feedback events were emitted in this run");
            }

            builder.AppendLine();
            builder.AppendLine("## Feedback Events");
            builder.AppendLine("| seq | intent | alias | destination | feedback_kind | feedback_text |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (RobotVoiceFeedbackDiagnosticRecord record in rows.Take(50))
            {
                builder.AppendLine(
                    $"| {record.SequenceIndex} | {Cell(record.Intent)} | {Cell(record.TargetAlias)} | {Cell(record.Destination)} | {Cell(record.FeedbackKind)} | {Cell(record.FeedbackText)} |");
            }

            return builder.ToString();
        }

        private static string Cell(string value)
        {
            return (value ?? string.Empty)
                .Replace("|", "\\|")
                .Replace("\r", " ")
                .Replace("\n", " ");
        }
    }
}
