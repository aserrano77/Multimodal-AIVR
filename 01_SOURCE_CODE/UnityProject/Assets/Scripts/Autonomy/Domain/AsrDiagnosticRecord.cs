using System;
using System.Globalization;
using System.Text;

namespace Autonomy.Domain
{
    public sealed class AsrDiagnosticRecord
    {
        public string RunId { get; set; } = "";
        public string UtteranceId { get; set; } = "";
        public string TimestampStart { get; set; } = "";
        public string TimestampEnd { get; set; } = "";
        public long DurationMs { get; set; }
        public float VadStartTime { get; set; } = float.NaN;
        public float VadEndTime { get; set; } = float.NaN;
        public float SilenceDurationBeforeClose { get; set; } = float.NaN;
        public int SampleRate { get; set; }
        public int ChannelCount { get; set; }
        public string ClipPath { get; set; } = "";
        public string ModelName { get; set; } = "";
        public ASRModelSize ModelSize { get; set; } = ASRModelSize.Unknown;
        public string ModelFileName { get; set; } = "";
        public string ModelPath { get; set; } = "";
        public bool DiagnosticModelOverrideEnabled { get; set; }
        public ASRModelSize RequestedModelSize { get; set; } = ASRModelSize.Unknown;
        public ASRModelSize EffectiveModelSize { get; set; } = ASRModelSize.Unknown;
        public string EffectiveModelFileName { get; set; } = "";
        public string EffectiveModelPath { get; set; } = "";
        public bool ModelAvailableAtStart { get; set; }
        public bool ModelFallbackUsed { get; set; }
        public string ModelOverrideFailedReason { get; set; } = "";
        public string Language { get; set; } = "";
        public string RawTranscript { get; set; } = "";
        public string NormalizedText { get; set; } = "";
        public string IntentKind { get; set; } = "";
        public string TargetAlias { get; set; } = "";
        public string Destination { get; set; } = "";
        public long AsrLatencyMs { get; set; }
        public long NormalizationLatencyMs { get; set; }
        public long MappingLatencyMs { get; set; }
        public long TotalVoicePipelineLatencyMs { get; set; }
        public string ConditionId { get; set; } = "";
        public string TrialId { get; set; } = "";
        public string RoundId { get; set; } = "";
        public string ErrorReason { get; set; } = "";
        public bool Success { get; set; }
        public bool HasConfidence { get; set; }
        public float Confidence { get; set; } = float.NaN;
        public float Rms { get; set; }
        public float Peak { get; set; }
        public float NonSilentSamplePercent { get; set; }
        public bool SilenceDetected { get; set; }
        public string ExpectedPhrase { get; set; } = "";
        public string ExpectedNormalizedText { get; set; } = "";
        public string ExpectedIntent { get; set; } = "";
        public string ExpectedAlias { get; set; } = "";
        public string ExpectedDestination { get; set; } = "";

        public bool TranscriptExactMatch => Matches(ExpectedPhrase, RawTranscript);
        public bool NormalizedMatch => Matches(ExpectedNormalizedText, NormalizedText);
        public bool IntentMatch => Matches(ExpectedIntent, IntentKind);
        public bool AliasMatch => Matches(ExpectedAlias, TargetAlias);
        public bool DestinationMatch => Matches(ExpectedDestination, Destination);

        public static string CsvHeader()
        {
            return string.Join(",",
                "run_id",
                "utterance_id",
                "timestamp_start",
                "timestamp_end",
                "duration_ms",
                "vad_start_time",
                "vad_end_time",
                "silence_duration_before_close",
                "sample_rate",
                "channel_count",
                "clip_path",
                "model_name",
                "model_size",
                "model_file_name",
                "model_path",
                "diagnostic_model_override_enabled",
                "requested_model_size",
                "effective_model_size",
                "effective_model_file_name",
                "effective_model_path",
                "model_available_at_start",
                "model_fallback_used",
                "model_override_failed_reason",
                "language",
                "raw_transcript",
                "normalized_text",
                "intent_kind",
                "target_alias",
                "destination",
                "asr_latency_ms",
                "normalization_latency_ms",
                "mapping_latency_ms",
                "total_voice_pipeline_latency_ms",
                "condition_id",
                "trial_id",
                "round_id",
                "success",
                "error_reason",
                "has_confidence",
                "confidence",
                "rms",
                "peak",
                "non_silent_sample_percent",
                "silence_detected",
                "expected_phrase",
                "expected_normalized_text",
                "expected_intent",
                "expected_alias",
                "expected_destination",
                "transcript_exact_match",
                "normalized_match",
                "intent_match",
                "alias_match",
                "destination_match");
        }

        public string ToCsvRow()
        {
            return string.Join(",",
                Csv(RunId),
                Csv(UtteranceId),
                Csv(TimestampStart),
                Csv(TimestampEnd),
                DurationMs.ToString(CultureInfo.InvariantCulture),
                Float(VadStartTime),
                Float(VadEndTime),
                Float(SilenceDurationBeforeClose),
                SampleRate.ToString(CultureInfo.InvariantCulture),
                ChannelCount.ToString(CultureInfo.InvariantCulture),
                Csv(ClipPath),
                Csv(ModelName),
                Csv(ModelSize.ToString().ToLowerInvariant()),
                Csv(ModelFileName),
                Csv(ModelPath),
                Bool(DiagnosticModelOverrideEnabled),
                Csv(RequestedModelSize.ToString().ToLowerInvariant()),
                Csv(ResolveEffectiveModelSize().ToString().ToLowerInvariant()),
                Csv(ResolveEffectiveModelFileName()),
                Csv(ResolveEffectiveModelPath()),
                Bool(ModelAvailableAtStart),
                Bool(ModelFallbackUsed),
                Csv(ModelOverrideFailedReason),
                Csv(Language),
                Csv(RawTranscript),
                Csv(NormalizedText),
                Csv(IntentKind),
                Csv(TargetAlias),
                Csv(Destination),
                AsrLatencyMs.ToString(CultureInfo.InvariantCulture),
                NormalizationLatencyMs.ToString(CultureInfo.InvariantCulture),
                MappingLatencyMs.ToString(CultureInfo.InvariantCulture),
                TotalVoicePipelineLatencyMs.ToString(CultureInfo.InvariantCulture),
                Csv(ConditionId),
                Csv(TrialId),
                Csv(RoundId),
                Bool(Success),
                Csv(ErrorReason),
                Bool(HasConfidence),
                Float(Confidence),
                Float(Rms),
                Float(Peak),
                Float(NonSilentSamplePercent),
                Bool(SilenceDetected),
                Csv(ExpectedPhrase),
                Csv(ExpectedNormalizedText),
                Csv(ExpectedIntent),
                Csv(ExpectedAlias),
                Csv(ExpectedDestination),
                Bool(TranscriptExactMatch),
                Bool(NormalizedMatch),
                Bool(IntentMatch),
                Bool(AliasMatch),
                Bool(DestinationMatch));
        }

        public string ToJsonLine()
        {
            StringBuilder builder = new();
            builder.Append('{');
            Append(builder, "run_id", RunId);
            Append(builder, "utterance_id", UtteranceId);
            Append(builder, "timestamp_start", TimestampStart);
            Append(builder, "timestamp_end", TimestampEnd);
            Append(builder, "duration_ms", DurationMs);
            Append(builder, "vad_start_time", VadStartTime);
            Append(builder, "vad_end_time", VadEndTime);
            Append(builder, "silence_duration_before_close", SilenceDurationBeforeClose);
            Append(builder, "sample_rate", SampleRate);
            Append(builder, "channel_count", ChannelCount);
            Append(builder, "clip_path", ClipPath);
            Append(builder, "model_name", ModelName);
            Append(builder, "model_size", ModelSize.ToString().ToLowerInvariant());
            Append(builder, "model_file_name", ModelFileName);
            Append(builder, "model_path", ModelPath);
            Append(builder, "diagnostic_model_override_enabled", DiagnosticModelOverrideEnabled);
            Append(builder, "requested_model_size", RequestedModelSize.ToString().ToLowerInvariant());
            Append(builder, "effective_model_size", ResolveEffectiveModelSize().ToString().ToLowerInvariant());
            Append(builder, "effective_model_file_name", ResolveEffectiveModelFileName());
            Append(builder, "effective_model_path", ResolveEffectiveModelPath());
            Append(builder, "model_available_at_start", ModelAvailableAtStart);
            Append(builder, "model_fallback_used", ModelFallbackUsed);
            Append(builder, "model_override_failed_reason", ModelOverrideFailedReason);
            Append(builder, "language", Language);
            Append(builder, "raw_transcript", RawTranscript);
            Append(builder, "normalized_text", NormalizedText);
            Append(builder, "intent_kind", IntentKind);
            Append(builder, "target_alias", TargetAlias);
            Append(builder, "destination", Destination);
            Append(builder, "asr_latency_ms", AsrLatencyMs);
            Append(builder, "normalization_latency_ms", NormalizationLatencyMs);
            Append(builder, "mapping_latency_ms", MappingLatencyMs);
            Append(builder, "total_voice_pipeline_latency_ms", TotalVoicePipelineLatencyMs);
            Append(builder, "condition_id", ConditionId);
            Append(builder, "trial_id", TrialId);
            Append(builder, "round_id", RoundId);
            Append(builder, "success", Success);
            Append(builder, "error_reason", ErrorReason);
            Append(builder, "has_confidence", HasConfidence);
            Append(builder, "confidence", Confidence);
            Append(builder, "rms", Rms);
            Append(builder, "peak", Peak);
            Append(builder, "non_silent_sample_percent", NonSilentSamplePercent);
            Append(builder, "silence_detected", SilenceDetected);
            Append(builder, "expected_phrase", ExpectedPhrase);
            Append(builder, "expected_normalized_text", ExpectedNormalizedText);
            Append(builder, "expected_intent", ExpectedIntent);
            Append(builder, "expected_alias", ExpectedAlias);
            Append(builder, "expected_destination", ExpectedDestination);
            Append(builder, "transcript_exact_match", TranscriptExactMatch);
            Append(builder, "normalized_match", NormalizedMatch);
            Append(builder, "intent_match", IntentMatch);
            Append(builder, "alias_match", AliasMatch);
            Append(builder, "destination_match", DestinationMatch, last: true);
            builder.Append('}');
            return builder.ToString();
        }

        public static bool Matches(string expected, string actual)
        {
            if (string.IsNullOrWhiteSpace(expected))
            {
                return false;
            }

            return string.Equals(NormalizeForMatch(expected), NormalizeForMatch(actual), StringComparison.OrdinalIgnoreCase);
        }

        public ASRModelSize ResolveEffectiveModelSize()
        {
            return EffectiveModelSize != ASRModelSize.Unknown ? EffectiveModelSize : ModelSize;
        }

        public string ResolveEffectiveModelFileName()
        {
            return !string.IsNullOrWhiteSpace(EffectiveModelFileName) ? EffectiveModelFileName : ModelFileName;
        }

        public string ResolveEffectiveModelPath()
        {
            return !string.IsNullOrWhiteSpace(EffectiveModelPath) ? EffectiveModelPath : ModelPath;
        }

        private static string NormalizeForMatch(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string Float(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? ""
                : value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void Append(StringBuilder builder, string key, string value, bool last = false)
        {
            builder.Append('"').Append(EscapeJson(key)).Append("\":\"").Append(EscapeJson(value ?? string.Empty)).Append('"');
            if (!last)
            {
                builder.Append(',');
            }
        }

        private static void Append(StringBuilder builder, string key, long value, bool last = false)
        {
            builder.Append('"').Append(EscapeJson(key)).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
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

        private static void Append(StringBuilder builder, string key, float value, bool last = false)
        {
            builder.Append('"').Append(EscapeJson(key)).Append("\":");
            builder.Append(float.IsNaN(value) || float.IsInfinity(value) ? "null" : value.ToString("0.###", CultureInfo.InvariantCulture));
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
