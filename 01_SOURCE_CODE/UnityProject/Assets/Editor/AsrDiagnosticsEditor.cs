using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using UnityEditor;
using UnityEngine;

namespace Autonomy.EditorTools
{
    public static class AsrDiagnosticsEditor
    {
        [MenuItem("Tools/ASR Diagnostics/Evaluate ASR Diagnostics Folder")]
        public static void EvaluateFolder()
        {
            string folder = EditorUtility.OpenFolderPanel(
                "Evaluate ASR Diagnostics Folder",
                Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "AsrDiagnostics")),
                "");
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            if (!AsrDiagnosticArtifactLocator.TryFindDiagnosticsCsv(folder, out string csvPath))
            {
                Debug.LogWarning($"[AsrDiagnosticsEditor] ASR diagnostics CSV not found | folder={folder}");
                return;
            }

            string summaryPath = EvaluateFolderPath(folder, csvPath);
            Debug.Log($"[AsrDiagnosticsEditor] ASR diagnostics evaluated | summary={summaryPath}");
        }

        public static string EvaluateFolderPath(string folder, string csvPath = "")
        {
            if (string.IsNullOrWhiteSpace(csvPath) && !AsrDiagnosticArtifactLocator.TryFindDiagnosticsCsv(folder, out csvPath))
            {
                return string.Empty;
            }

            List<AsrDiagnosticRecord> records = ReadRecords(csvPath);
            AsrDiagnosticSummary summary = AsrDiagnosticSummary.Calculate(records);
            string runId = records.Count > 0 && !string.IsNullOrWhiteSpace(records[0].RunId) ? records[0].RunId : "unknown_run";
            string modelSize = ResolveSummaryModelSize(records);
            string summaryPath = Path.Combine(
                folder,
                AsrDiagnosticRecorder.BuildExplicitArtifactFileName("asr_summary", runId, modelSize, ".md"));
            File.WriteAllText(summaryPath, summary.ToMarkdown(records), Encoding.UTF8);
            return summaryPath;
        }

        [MenuItem("Tools/ASR Diagnostics/Report Whisper Model Availability")]
        public static void ReportModelAvailability()
        {
            foreach (ASRModelSize size in new[] { ASRModelSize.Tiny, ASRModelSize.Base, ASRModelSize.Small })
            {
                string relativePath = Autonomy.UnityIntegration.WhisperModelConfiguration.BuildStreamingAssetsModelPath(size);
                string path = Path.Combine(Application.streamingAssetsPath, relativePath);
                Debug.Log($"[AsrDiagnosticsEditor] model={size.ToString().ToLowerInvariant()} available={File.Exists(path)} path={path}");
            }
        }

        private static List<AsrDiagnosticRecord> ReadRecords(string csvPath)
        {
            List<AsrDiagnosticRecord> records = new();
            string[] lines = File.ReadAllLines(csvPath);
            if (lines.Length <= 1)
            {
                return records;
            }

            string[] header = SplitCsvLine(lines[0]).ToArray();
            Dictionary<string, int> indices = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Length; i++)
            {
                indices[header[i]] = i;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] values = SplitCsvLine(lines[i]).ToArray();
                AsrDiagnosticRecord record = new()
                {
                    RunId = Get(values, indices, "run_id"),
                    UtteranceId = Get(values, indices, "utterance_id"),
                    DurationMs = Long(values, indices, "duration_ms"),
                    ModelName = Get(values, indices, "model_name"),
                    ModelSize = ModelSize(Get(values, indices, "model_size")),
                    ModelFileName = Get(values, indices, "model_file_name"),
                    ModelPath = Get(values, indices, "model_path"),
                    DiagnosticModelOverrideEnabled = Bool(Get(values, indices, "diagnostic_model_override_enabled")),
                    RequestedModelSize = ModelSize(Get(values, indices, "requested_model_size")),
                    EffectiveModelSize = ModelSize(Get(values, indices, "effective_model_size")),
                    EffectiveModelFileName = Get(values, indices, "effective_model_file_name"),
                    EffectiveModelPath = Get(values, indices, "effective_model_path"),
                    ModelAvailableAtStart = Bool(Get(values, indices, "model_available_at_start")),
                    ModelFallbackUsed = Bool(Get(values, indices, "model_fallback_used")),
                    Language = Get(values, indices, "language"),
                    RawTranscript = Get(values, indices, "raw_transcript"),
                    NormalizedText = Get(values, indices, "normalized_text"),
                    IntentKind = Get(values, indices, "intent_kind"),
                    TargetAlias = Get(values, indices, "target_alias"),
                    Destination = Get(values, indices, "destination"),
                    TotalVoicePipelineLatencyMs = Long(values, indices, "total_voice_pipeline_latency_ms"),
                    ErrorReason = Get(values, indices, "error_reason"),
                    ExpectedPhrase = Get(values, indices, "expected_phrase"),
                    ExpectedNormalizedText = Get(values, indices, "expected_normalized_text"),
                    ExpectedIntent = Get(values, indices, "expected_intent"),
                    ExpectedAlias = Get(values, indices, "expected_alias"),
                    ExpectedDestination = Get(values, indices, "expected_destination")
                };
                records.Add(record);
            }

            return records;
        }

        private static List<string> SplitCsvLine(string line)
        {
            List<string> values = new();
            StringBuilder current = new();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    values.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            values.Add(current.ToString());
            return values;
        }

        private static string Get(string[] values, Dictionary<string, int> indices, string key)
        {
            return indices.TryGetValue(key, out int index) && index >= 0 && index < values.Length
                ? values[index]
                : string.Empty;
        }

        private static long Long(string[] values, Dictionary<string, int> indices, string key)
        {
            return long.TryParse(Get(values, indices, key), out long value) ? value : 0L;
        }

        private static bool Bool(string value)
        {
            return bool.TryParse(value, out bool result) && result;
        }

        private static ASRModelSize ModelSize(string value)
        {
            if (Enum.TryParse(value, true, out ASRModelSize size))
            {
                return size;
            }

            return ASRModelSize.Unknown;
        }

        private static string ResolveSummaryModelSize(List<AsrDiagnosticRecord> records)
        {
            foreach (AsrDiagnosticRecord record in records)
            {
                ASRModelSize size = record.ResolveEffectiveModelSize();
                if (size != ASRModelSize.Unknown)
                {
                    return size.ToString().ToLowerInvariant();
                }
            }

            return "unknown";
        }
    }
}
