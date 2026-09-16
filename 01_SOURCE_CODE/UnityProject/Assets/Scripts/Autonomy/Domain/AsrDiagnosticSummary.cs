using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Autonomy.Domain
{
    public sealed class AsrDiagnosticSummary
    {
        public int UtteranceCount { get; private set; }
        public float AverageDurationMs { get; private set; }
        public int EmptyTranscriptCount { get; private set; }
        public int LowConfidenceCount { get; private set; }
        public int IntentRecognizedCount { get; private set; }
        public int AliasRecognizedCount { get; private set; }
        public int DestinationRecognizedCount { get; private set; }
        public float AverageLatencyMs { get; private set; }
        public long P95LatencyMs { get; private set; }
        public float TranscriptExactAccuracy { get; private set; }
        public float NormalizedAccuracy { get; private set; }
        public float IntentAccuracy { get; private set; }
        public float AliasAccuracy { get; private set; }
        public float DestinationAccuracy { get; private set; }
        public int EmptyExpectedControlUtteranceCount { get; private set; }
        public IReadOnlyList<string> FrequentErrors { get; private set; } = Array.Empty<string>();

        public static AsrDiagnosticSummary Calculate(IEnumerable<AsrDiagnosticRecord> records, float lowConfidenceThreshold = 0.5f)
        {
            List<AsrDiagnosticRecord> list = records?.ToList() ?? new List<AsrDiagnosticRecord>();
            AsrDiagnosticSummary summary = new() { UtteranceCount = list.Count };
            if (list.Count == 0)
            {
                return summary;
            }

            summary.AverageDurationMs = (float)list.Average(record => Math.Max(0L, record.DurationMs));
            summary.EmptyTranscriptCount = list.Count(record => string.IsNullOrWhiteSpace(record.RawTranscript));
            summary.LowConfidenceCount = list.Count(record => record.HasConfidence && record.Confidence < lowConfidenceThreshold);
            summary.IntentRecognizedCount = list.Count(record => !string.IsNullOrWhiteSpace(record.IntentKind) && record.IntentKind != "None");
            summary.AliasRecognizedCount = list.Count(record => !string.IsNullOrWhiteSpace(record.TargetAlias));
            summary.DestinationRecognizedCount = list.Count(record => !string.IsNullOrWhiteSpace(record.Destination));
            summary.AverageLatencyMs = (float)list.Average(record => Math.Max(0L, record.TotalVoicePipelineLatencyMs));
            summary.P95LatencyMs = Percentile95(list.Select(record => Math.Max(0L, record.TotalVoicePipelineLatencyMs)).ToList());
            summary.TranscriptExactAccuracy = ExpectedAccuracy(list, record => record.ExpectedPhrase, record => record.TranscriptExactMatch);
            summary.NormalizedAccuracy = ExpectedAccuracy(list, record => record.ExpectedNormalizedText, record => record.NormalizedMatch);
            summary.IntentAccuracy = ExpectedAccuracy(list, record => record.ExpectedIntent, record => record.IntentMatch);
            summary.AliasAccuracy = ExpectedAccuracy(list, record => record.ExpectedAlias, record => record.AliasMatch);
            summary.DestinationAccuracy = ExpectedAccuracy(list, record => record.ExpectedDestination, record => record.DestinationMatch);
            summary.EmptyExpectedControlUtteranceCount = list.Count(record =>
                string.IsNullOrWhiteSpace(record.RawTranscript) &&
                IsExpectedControlIntent(record.ExpectedIntent));
            summary.FrequentErrors = list
                .Where(record => !string.IsNullOrWhiteSpace(record.ErrorReason))
                .GroupBy(record => record.ErrorReason)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Take(8)
                .Select(group => $"{group.Key}: {group.Count()}")
                .ToArray();
            return summary;
        }

        public string ToMarkdown(IEnumerable<AsrDiagnosticRecord> records)
        {
            List<AsrDiagnosticRecord> list = records?.ToList() ?? new List<AsrDiagnosticRecord>();
            StringBuilder builder = new();
            builder.AppendLine("# ASR diagnostic summary");
            builder.AppendLine();
            builder.AppendLine($"- run_id: {ResolveRunId(list)}");
            builder.AppendLine($"- requested_model_size: {ResolveRequestedModelSize(list)}");
            builder.AppendLine($"- effective_model_size: {ResolveModelSize(list)}");
            builder.AppendLine($"- model_size: {ResolveModelSize(list)}");
            builder.AppendLine($"- model_name: {ResolveSingleValue(list.Select(record => record.ModelName))}");
            builder.AppendLine($"- effective_model_file_name: {ResolveSingleValue(list.Select(record => record.ResolveEffectiveModelFileName()))}");
            builder.AppendLine($"- effective_model_path: {ResolveSingleValue(list.Select(record => record.ResolveEffectiveModelPath()))}");
            builder.AppendLine($"- model_available_at_start: {ResolveBoolState(list.Select(record => record.ModelAvailableAtStart))}");
            builder.AppendLine($"- model_fallback_used: {ResolveBoolState(list.Select(record => record.ModelFallbackUsed))}");
            builder.AppendLine($"- model_override_failed_reason: {ResolveOptionalReason(list.Select(record => record.ModelOverrideFailedReason))}");
            builder.AppendLine($"- diagnostic_model_override_enabled: {ResolveOverrideState(list)}");
            builder.AppendLine($"- utterances: {UtteranceCount}");
            builder.AppendLine($"- average_duration_ms: {AverageDurationMs:0.#}");
            builder.AppendLine($"- empty_transcripts: {EmptyTranscriptCount}");
            builder.AppendLine($"- low_confidence_transcripts: {LowConfidenceCount}");
            builder.AppendLine($"- intent_recognized: {IntentRecognizedCount}");
            builder.AppendLine($"- alias_recognized: {AliasRecognizedCount}");
            builder.AppendLine($"- destination_recognized: {DestinationRecognizedCount}");
            builder.AppendLine($"- average_total_voice_pipeline_latency_ms: {AverageLatencyMs:0.#}");
            builder.AppendLine($"- p95_total_voice_pipeline_latency_ms: {P95LatencyMs}");
            builder.AppendLine($"- transcript_exact_accuracy: {Percent(TranscriptExactAccuracy)}");
            builder.AppendLine($"- normalized_accuracy: {Percent(NormalizedAccuracy)}");
            builder.AppendLine($"- intent_accuracy: {Percent(IntentAccuracy)}");
            builder.AppendLine($"- alias_accuracy: {Percent(AliasAccuracy)}");
            builder.AppendLine($"- destination_accuracy: {Percent(DestinationAccuracy)}");
            builder.AppendLine($"- empty_expected_control_utterances: {EmptyExpectedControlUtteranceCount}");
            if (!HasAnyExpectedValues(list))
            {
                builder.AppendLine("- expected_values_note: no expected utterance set configured");
            }

            builder.AppendLine();
            builder.AppendLine("## Frequent errors");
            if (FrequentErrors.Count == 0)
            {
                builder.AppendLine("- none");
            }

            if (EmptyExpectedControlUtteranceCount > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## Critical control utterance failures");
                foreach (AsrDiagnosticRecord record in list.Where(record => string.IsNullOrWhiteSpace(record.RawTranscript) && IsExpectedControlIntent(record.ExpectedIntent)))
                {
                    builder.AppendLine($"- {record.UtteranceId}: expected_intent={record.ExpectedIntent}, expected_phrase='{record.ExpectedPhrase}', likely_asr_or_vad_empty_transcript=true");
                }
            }
            else
            {
                foreach (string error in FrequentErrors)
                {
                    builder.AppendLine($"- {error}");
                }
            }

            builder.AppendLine();
            builder.AppendLine("## Model comparison");
            foreach (IGrouping<string, AsrDiagnosticRecord> group in list.GroupBy(ResolveModelComparisonKey).OrderBy(group => group.Key))
            {
                AsrDiagnosticSummary modelSummary = Calculate(group);
                builder.AppendLine($"- {group.Key}: n={modelSummary.UtteranceCount}, intent={Percent(modelSummary.IntentAccuracy)}, alias={Percent(modelSummary.AliasAccuracy)}, destination={Percent(modelSummary.DestinationAccuracy)}, latency_p95_ms={modelSummary.P95LatencyMs}");
            }

            builder.AppendLine();
            builder.AppendLine("## Recommended decision criteria");
            builder.AppendLine("- intent accuracy >= 90%");
            builder.AppendLine("- alias accuracy >= 85-90%");
            builder.AppendLine("- perceived latency ideally < 1500-2000 ms from phrase end to feedback");
            return builder.ToString();
        }

        private static long Percentile95(List<long> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0L;
            }

            values.Sort();
            int index = (int)Math.Ceiling(values.Count * 0.95f) - 1;
            return values[Math.Max(0, Math.Min(values.Count - 1, index))];
        }

        private static float ExpectedAccuracy(
            List<AsrDiagnosticRecord> records,
            Func<AsrDiagnosticRecord, string> expected,
            Func<AsrDiagnosticRecord, bool> match)
        {
            List<AsrDiagnosticRecord> scoped = records.Where(record => !string.IsNullOrWhiteSpace(expected(record))).ToList();
            if (scoped.Count == 0)
            {
                return float.NaN;
            }

            return scoped.Count(match) / (float)scoped.Count;
        }

        private static string Percent(float value)
        {
            return float.IsNaN(value)
                ? "n/a"
                : (value * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        private static string ResolveRunId(List<AsrDiagnosticRecord> records)
        {
            return ResolveSingleValue(records.Select(record => record.RunId));
        }

        private static string ResolveModelSize(List<AsrDiagnosticRecord> records)
        {
            return ResolveSingleValue(records
                .Select(ResolveEffectiveModelSize)
                .Where(size => size != ASRModelSize.Unknown)
                .Select(size => size.ToString().ToLowerInvariant()));
        }

        private static string ResolveRequestedModelSize(List<AsrDiagnosticRecord> records)
        {
            return ResolveSingleValue(records
                .Select(record => record.RequestedModelSize)
                .Where(size => size != ASRModelSize.Unknown)
                .Select(size => size.ToString().ToLowerInvariant()));
        }

        private static string ResolveOverrideState(List<AsrDiagnosticRecord> records)
        {
            if (records == null || records.Count == 0)
            {
                return "unknown";
            }

            return records.Any(record => record.DiagnosticModelOverrideEnabled) ? "true" : "false";
        }

        private static string ResolveSingleValue(IEnumerable<string> values)
        {
            string[] distinct = values?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();

            return distinct.Length switch
            {
                0 => "unknown",
                1 => distinct[0],
                _ => string.Join("; ", distinct)
            };
        }

        private static string ResolveOptionalReason(IEnumerable<string> values)
        {
            string value = ResolveSingleValue(values);
            return value == "unknown" ? "none" : value;
        }

        private static string ResolveBoolState(IEnumerable<bool> values)
        {
            bool[] distinct = values?.Distinct().ToArray() ?? Array.Empty<bool>();
            return distinct.Length switch
            {
                0 => "unknown",
                1 => distinct[0] ? "true" : "false",
                _ => "mixed"
            };
        }

        private static string ResolveModelComparisonKey(AsrDiagnosticRecord record)
        {
            if (record == null)
            {
                return "unknown";
            }

            ASRModelSize effectiveSize = ResolveEffectiveModelSize(record);
            if (effectiveSize != ASRModelSize.Unknown)
            {
                return effectiveSize.ToString().ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(record.ModelFileName))
            {
                return record.ModelFileName;
            }

            if (!string.IsNullOrWhiteSpace(record.ModelName))
            {
                return record.ModelName;
            }

            return "unknown";
        }

        private static ASRModelSize ResolveEffectiveModelSize(AsrDiagnosticRecord record)
        {
            if (record == null)
            {
                return ASRModelSize.Unknown;
            }

            ASRModelSize explicitEffective = record.ResolveEffectiveModelSize();
            if (explicitEffective != ASRModelSize.Unknown)
            {
                return explicitEffective;
            }

            string probe = ((record.ModelName ?? string.Empty) + " " +
                (record.ResolveEffectiveModelFileName() ?? string.Empty) + " " +
                (record.ResolveEffectiveModelPath() ?? string.Empty)).ToLowerInvariant();
            if (probe.Contains("tiny"))
            {
                return ASRModelSize.Tiny;
            }

            if (probe.Contains("base"))
            {
                return ASRModelSize.Base;
            }

            if (probe.Contains("small"))
            {
                return ASRModelSize.Small;
            }

            return ASRModelSize.Unknown;
        }

        private static bool HasAnyExpectedValues(List<AsrDiagnosticRecord> records)
        {
            return records.Any(record =>
                !string.IsNullOrWhiteSpace(record.ExpectedPhrase) ||
                !string.IsNullOrWhiteSpace(record.ExpectedNormalizedText) ||
                !string.IsNullOrWhiteSpace(record.ExpectedIntent) ||
                !string.IsNullOrWhiteSpace(record.ExpectedAlias) ||
                !string.IsNullOrWhiteSpace(record.ExpectedDestination));
        }

        private static bool IsExpectedControlIntent(string expectedIntent)
        {
            return string.Equals(expectedIntent, "Stop", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expectedIntent, "Continue", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expectedIntent, "Resume", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expectedIntent, "CancelPendingOrder", StringComparison.OrdinalIgnoreCase);
        }
    }
}
