using Autonomy.Domain;
using Autonomy.UnityIntegration;
using NUnit.Framework;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Autonomy.BT.Tests
{
    public sealed class AsrDiagnosticRecordTests
    {
        [Test]
        public void Csv_And_Jsonl_Include_Core_Diagnostic_Fields()
        {
            AsrDiagnosticRecord record = new()
            {
                RunId = "asr_diag_20260606_010203",
                UtteranceId = "utt_001",
                RawTranscript = "lleva la caja A1 a su zona",
                NormalizedText = "lleva caja a1 zona a",
                IntentKind = "PickAndPlace",
                TargetAlias = "A1",
                Destination = "zone_a",
                ModelName = "ggml-tiny",
                ModelSize = ASRModelSize.Tiny,
                EffectiveModelSize = ASRModelSize.Tiny,
                ModelFileName = "ggml-tiny.bin",
                EffectiveModelFileName = "ggml-tiny.bin",
                ModelPath = "Whisper/ggml-tiny.bin",
                EffectiveModelPath = "Whisper/ggml-tiny.bin",
                DiagnosticModelOverrideEnabled = true,
                ModelOverrideFailedReason = "",
                AsrLatencyMs = 1200,
                NormalizationLatencyMs = 2,
                MappingLatencyMs = 3,
                TotalVoicePipelineLatencyMs = 1205
            };

            string csv = record.ToCsvRow();
            string json = record.ToJsonLine();

            Assert.That(AsrDiagnosticRecord.CsvHeader(), Does.Contain("utterance_id"));
            Assert.That(AsrDiagnosticRecord.CsvHeader(), Does.Contain("run_id"));
            Assert.That(csv, Does.Contain("\"utt_001\""));
            Assert.That(csv, Does.Contain("\"asr_diag_20260606_010203\""));
            Assert.That(csv, Does.Contain("\"tiny\""));
            Assert.That(csv, Does.Contain("\"ggml-tiny.bin\""));
            Assert.That(csv, Does.Contain("true"));
            Assert.That(csv, Does.Contain("\"PickAndPlace\""));
            Assert.That(json, Does.Contain("\"run_id\":\"asr_diag_20260606_010203\""));
            Assert.That(json, Does.Contain("\"utterance_id\":\"utt_001\""));
            Assert.That(json, Does.Contain("\"model_size\":\"tiny\""));
            Assert.That(json, Does.Contain("\"model_file_name\":\"ggml-tiny.bin\""));
            Assert.That(json, Does.Contain("\"diagnostic_model_override_enabled\":true"));
            Assert.That(json, Does.Contain("\"model_override_failed_reason\":\"\""));
            Assert.That(json, Does.Contain("\"total_voice_pipeline_latency_ms\":1205"));
        }

        [Test]
        public void Expected_Metrics_Calculate_Accuracy_For_Controlled_Utterances()
        {
            AsrDiagnosticRecord ok = new()
            {
                RawTranscript = "espera",
                NormalizedText = "espera",
                IntentKind = "Stop",
                TargetAlias = "A1",
                Destination = "zone_a",
                ExpectedPhrase = "espera",
                ExpectedNormalizedText = "espera",
                ExpectedIntent = "Stop",
                ExpectedAlias = "A1",
                ExpectedDestination = "zone_a"
            };
            AsrDiagnosticRecord fail = new()
            {
                RawTranscript = "a uno",
                NormalizedText = "a uno",
                IntentKind = "None",
                TargetAlias = "",
                Destination = "",
                ExpectedPhrase = "A1",
                ExpectedNormalizedText = "A1",
                ExpectedIntent = "PickAndPlace",
                ExpectedAlias = "A1",
                ExpectedDestination = "zone_a"
            };

            AsrDiagnosticSummary summary = AsrDiagnosticSummary.Calculate(new[] { ok, fail });

            Assert.That(summary.TranscriptExactAccuracy, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(summary.NormalizedAccuracy, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(summary.IntentAccuracy, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(summary.AliasAccuracy, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(summary.DestinationAccuracy, Is.EqualTo(0.5f).Within(0.001f));
        }

        [Test]
        public void Summary_Computes_Average_And_P95_Latency()
        {
            AsrDiagnosticSummary summary = AsrDiagnosticSummary.Calculate(new[]
            {
                new AsrDiagnosticRecord { DurationMs = 1000, TotalVoicePipelineLatencyMs = 100 },
                new AsrDiagnosticRecord { DurationMs = 2000, TotalVoicePipelineLatencyMs = 200 },
                new AsrDiagnosticRecord { DurationMs = 3000, TotalVoicePipelineLatencyMs = 900 }
            });

            Assert.That(summary.AverageDurationMs, Is.EqualTo(2000f).Within(0.001f));
            Assert.That(summary.AverageLatencyMs, Is.EqualTo(400f).Within(0.001f));
            Assert.That(summary.P95LatencyMs, Is.EqualTo(900));
        }

        [Test]
        public void Markdown_Includes_Run_Id_And_Model_Size_Comparison_Key()
        {
            AsrDiagnosticRecord record = new()
            {
                RunId = "asr_diag_tiny_run",
                ModelName = "ggml-tiny",
                ModelSize = ASRModelSize.Tiny,
                ModelFileName = "ggml-tiny.bin",
                ModelPath = "Whisper/ggml-tiny.bin",
                DiagnosticModelOverrideEnabled = true,
                ExpectedIntent = "Stop",
                IntentKind = "Stop",
                TotalVoicePipelineLatencyMs = 123
            };

            AsrDiagnosticSummary summary = AsrDiagnosticSummary.Calculate(new[] { record });
            string markdown = summary.ToMarkdown(new[] { record });

            Assert.That(markdown, Does.Contain("- run_id: asr_diag_tiny_run"));
            Assert.That(markdown, Does.Contain("- model_size: tiny"));
            Assert.That(markdown, Does.Contain("- effective_model_file_name: ggml-tiny.bin"));
            Assert.That(markdown, Does.Contain("- effective_model_path: Whisper/ggml-tiny.bin"));
            Assert.That(markdown, Does.Contain("- model_override_failed_reason: none"));
            Assert.That(markdown, Does.Contain("- diagnostic_model_override_enabled: true"));
            Assert.That(markdown, Does.Contain("- tiny: n=1"));
            Assert.That(markdown, Does.Not.Contain("- unknown: n=1"));
        }

        [Test]
        public void Explicit_Artifact_File_Name_Includes_Run_Id_And_Model_Size()
        {
            string fileName = AsrDiagnosticRecorder.BuildExplicitArtifactFileName(
                "asr_diagnostics",
                "asr_diag_20260606_010203",
                "tiny",
                ".csv");

            Assert.That(fileName, Is.EqualTo("asr_diagnostics_asr_diag_20260606_010203_tiny.csv"));
        }

        [Test]
        public void Default_P38_Expected_Utterances_Use_Self_For_Su_Zona()
        {
            AsrDiagnosticExpectedUtterance[] utterances = AsrDiagnosticRecorder.CreateDefaultP38ExpectedUtterances();

            Assert.That(utterances[0].ExpectedPhrase, Is.EqualTo("lleva la caja A1 a su zona"));
            Assert.That(utterances[0].ExpectedDestination, Is.EqualTo("SELF"));
            Assert.That(utterances[1].ExpectedDestination, Is.EqualTo("SELF"));
            Assert.That(utterances[2].ExpectedDestination, Is.EqualTo("SELF"));
            Assert.That(utterances[3].ExpectedDestination, Is.EqualTo("SELF"));
        }

        [Test]
        public void Recorder_Generates_Only_Traceable_Artifacts_And_Locator_Finds_Csv()
        {
            GameObject host = new("asr_diag_test");
            AsrDiagnosticRecorder recorder = host.AddComponent<AsrDiagnosticRecorder>();
            SetPrivate(recorder, "_diagnosticModeEnabled", true);

            recorder.RecordUtterance(new AsrDiagnosticRecord
            {
                UtteranceId = "utt_artifact",
                ModelSize = ASRModelSize.Small,
                EffectiveModelSize = ASRModelSize.Small,
                RawTranscript = "espera",
                NormalizedText = "espera",
                IntentKind = "Stop"
            });
            recorder.WriteSummary();

            string runDirectory = recorder.RunDirectory;
            Assert.That(File.Exists(Path.Combine(runDirectory, "asr_diagnostics.csv")), Is.False);
            Assert.That(File.Exists(Path.Combine(runDirectory, "asr_diagnostics.jsonl")), Is.False);
            Assert.That(File.Exists(Path.Combine(runDirectory, "asr_summary.md")), Is.False);
            Assert.That(File.Exists(Path.Combine(runDirectory, $"asr_diagnostics_{recorder.RunId}_small.csv")), Is.True);
            Assert.That(File.Exists(Path.Combine(runDirectory, $"asr_diagnostics_{recorder.RunId}_small.jsonl")), Is.True);
            Assert.That(File.Exists(Path.Combine(runDirectory, $"asr_summary_{recorder.RunId}_small.md")), Is.True);
            Assert.That(AsrDiagnosticArtifactLocator.TryFindDiagnosticsCsv(runDirectory, out string csvPath), Is.True);
            Assert.That(Path.GetFileName(csvPath), Is.EqualTo($"asr_diagnostics_{recorder.RunId}_small.csv"));

            Object.DestroyImmediate(host);
        }

        [Test]
        public void Summary_Without_Expected_Values_Shows_Na_And_Clear_Note()
        {
            AsrDiagnosticRecord record = new()
            {
                RunId = "asr_diag_no_expected",
                ModelSize = ASRModelSize.Base,
                RawTranscript = "lleva la caja A1",
                NormalizedText = "lleva caja A1",
                IntentKind = "PickAndPlace",
                TargetAlias = "A1",
                Destination = "zone_a"
            };

            AsrDiagnosticSummary summary = AsrDiagnosticSummary.Calculate(new[] { record });
            string markdown = summary.ToMarkdown(new[] { record });

            Assert.That(float.IsNaN(summary.TranscriptExactAccuracy), Is.True);
            Assert.That(float.IsNaN(summary.NormalizedAccuracy), Is.True);
            Assert.That(float.IsNaN(summary.IntentAccuracy), Is.True);
            Assert.That(float.IsNaN(summary.AliasAccuracy), Is.True);
            Assert.That(float.IsNaN(summary.DestinationAccuracy), Is.True);
            Assert.That(markdown, Does.Contain("- transcript_exact_accuracy: n/a"));
            Assert.That(markdown, Does.Contain("- normalized_accuracy: n/a"));
            Assert.That(markdown, Does.Contain("no expected utterance set configured"));
        }

        [Test]
        public void Summary_With_Self_Destination_Expected_Values_Computes_Destination_Accuracy()
        {
            AsrDiagnosticRecord record = new()
            {
                RunId = "asr_diag_self",
                ModelSize = ASRModelSize.Small,
                RawTranscript = "lleva la caja a uno a su zona",
                NormalizedText = "lleva la caja A1 a su zona",
                IntentKind = "PickAndPlace",
                TargetAlias = "A1",
                Destination = "SELF",
                ExpectedPhrase = "lleva la caja A1 a su zona",
                ExpectedNormalizedText = "lleva la caja A1 a su zona",
                ExpectedIntent = "PickAndPlace",
                ExpectedAlias = "A1",
                ExpectedDestination = "SELF"
            };

            AsrDiagnosticSummary summary = AsrDiagnosticSummary.Calculate(new[] { record });
            string markdown = summary.ToMarkdown(new[] { record });

            Assert.That(summary.NormalizedAccuracy, Is.EqualTo(1f).Within(0.001f));
            Assert.That(summary.IntentAccuracy, Is.EqualTo(1f).Within(0.001f));
            Assert.That(summary.AliasAccuracy, Is.EqualTo(1f).Within(0.001f));
            Assert.That(summary.DestinationAccuracy, Is.EqualTo(1f).Within(0.001f));
            Assert.That(markdown, Does.Contain("- destination_accuracy: 100%"));
        }

        [Test]
        public void Summary_Marks_Empty_Expected_Control_Utterance_As_Critical_Asr_Or_Vad_Failure()
        {
            AsrDiagnosticRecord record = new()
            {
                UtteranceId = "utt_empty_control",
                RawTranscript = "",
                ExpectedPhrase = "espera",
                ExpectedNormalizedText = "espera",
                ExpectedIntent = "Stop"
            };

            AsrDiagnosticSummary summary = AsrDiagnosticSummary.Calculate(new[] { record });
            string markdown = summary.ToMarkdown(new[] { record });

            Assert.That(summary.EmptyExpectedControlUtteranceCount, Is.EqualTo(1));
            Assert.That(markdown, Does.Contain("- empty_expected_control_utterances: 1"));
            Assert.That(markdown, Does.Contain("likely_asr_or_vad_empty_transcript=true"));
        }

        [Test]
        public void Whisper_Model_Configuration_Tolerates_Missing_Manager()
        {
            EnsureWhisperModelFile(ASRModelSize.Base);
            bool configured = WhisperModelConfiguration.TryApplyModel(null, ASRModelSize.Base, out string message);

            Assert.That(configured, Is.False);
            Assert.That(message, Is.EqualTo("whisper_manager_not_assigned_or_not_found"));
            Assert.That(WhisperModelConfiguration.BuildStreamingAssetsModelPath(ASRModelSize.Small), Is.EqualTo("Whisper/ggml-small.bin"));
        }

        [Test]
        public void Whisper_Model_Override_Applies_To_Resolved_Manager_With_Public_ModelPath()
        {
            EnsureWhisperModelFile(ASRModelSize.Small);
            GameObject host = new("fake_whisper_manager");
            FakeWhisperManager manager = host.AddComponent<FakeWhisperManager>();

            bool configured = WhisperModelConfiguration.TryApplyModel(manager, ASRModelSize.Small, out WhisperModelApplyResult result);
            WhisperModelRuntimeInfo info = WhisperModelConfiguration.ResolveRuntimeInfo(manager, ASRModelSize.Small, diagnosticOverrideEnabled: true);

            Assert.That(configured, Is.True);
            Assert.That(manager.ModelPath, Is.EqualTo("Whisper/ggml-small.bin"));
            Assert.That(manager.IsModelPathInStreamingAssets, Is.True);
            Assert.That(result.RequestedModelSize, Is.EqualTo(ASRModelSize.Small));
            Assert.That(result.EffectiveModelSize, Is.EqualTo(ASRModelSize.Small));
            Assert.That(result.EffectiveModelFileName, Is.EqualTo("ggml-small.bin"));
            string normalizedResolvedPath = NormalizePathSeparators(result.ResolvedModelPath);
            Assert.That(normalizedResolvedPath, Does.EndWith("Whisper/ggml-small.bin"));
            Assert.That(info.EffectiveModelSize, Is.EqualTo(ASRModelSize.Small));
            Assert.That(info.EffectiveModelFileName, Is.EqualTo("ggml-small.bin"));
            Assert.That(info.EffectiveModelPath, Is.EqualTo("Whisper/ggml-small.bin"));
            Assert.That(info.ModelFallbackUsed, Is.False);

            Object.DestroyImmediate(host);
        }

        [Test]
        public void Whisper_Model_Override_Fails_Without_Ambiguous_Run_When_Manager_Is_Missing()
        {
            EnsureWhisperModelFile(ASRModelSize.Small);
            GameObject host = new("voice_controller_missing_whisper");
            VoiceRecognitionController controller = host.AddComponent<VoiceRecognitionController>();
            AsrDiagnosticRecorder recorder = host.AddComponent<AsrDiagnosticRecorder>();
            SetPrivate(controller, "_enableAsrDiagnosticMode", true);
            SetPrivate(controller, "_diagnosticOverrideWhisperModel", true);
            SetPrivate(controller, "_diagnosticModelSize", ASRModelSize.Small);
            SetPrivate(controller, "_asrDiagnosticRecorder", recorder);

            MethodInfo configure = typeof(VoiceRecognitionController).GetMethod(
                "ConfigureDiagnosticWhisperModelIfRequested",
                BindingFlags.Instance | BindingFlags.NonPublic);
            configure.Invoke(controller, null);

            Assert.That((bool)GetPrivate(controller, "_enableAsrDiagnosticMode"), Is.False);
            Assert.That(recorder.DiagnosticModeEnabled, Is.False);
            Assert.That(recorder.RunDirectory, Is.Empty);

            Object.DestroyImmediate(host);
        }

        [Test]
        public void Voice_Controller_Auto_Resolves_Recorder_On_Same_GameObject()
        {
            GameObject host = new("voice_controller_recorder");
            VoiceRecognitionController controller = host.AddComponent<VoiceRecognitionController>();
            AsrDiagnosticRecorder recorder = host.AddComponent<AsrDiagnosticRecorder>();
            SetPrivate(controller, "_enableAsrDiagnosticMode", true);

            MethodInfo resolver = typeof(VoiceRecognitionController).GetMethod(
                "TryResolveAsrDiagnosticRecorder",
                BindingFlags.Instance | BindingFlags.NonPublic);
            resolver.Invoke(controller, new object[] { false });

            Assert.That(GetPrivate(controller, "_asrDiagnosticRecorder"), Is.SameAs(recorder));
            Assert.That(recorder.DiagnosticModeEnabled, Is.True);

            Object.DestroyImmediate(host);
        }

        [Test]
        public void Whisper_Model_Availability_Reports_Tiny_Base_Small_When_Files_Exist()
        {
            string directory = Path.Combine(Application.streamingAssetsPath, "Whisper");
            Directory.CreateDirectory(directory);
            string[] files =
            {
                Path.Combine(directory, "ggml-tiny.bin"),
                Path.Combine(directory, "ggml-base.bin"),
                Path.Combine(directory, "ggml-small.bin")
            };
            bool[] existed = new bool[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                existed[i] = File.Exists(files[i]);
                if (!existed[i])
                {
                    File.WriteAllText(files[i], "test");
                }
            }

            Assert.That(WhisperModelConfiguration.IsModelAvailable(ASRModelSize.Tiny), Is.True);
            Assert.That(WhisperModelConfiguration.IsModelAvailable(ASRModelSize.Base), Is.True);
            Assert.That(WhisperModelConfiguration.IsModelAvailable(ASRModelSize.Small), Is.True);

            for (int i = 0; i < files.Length; i++)
            {
                if (!existed[i] && File.Exists(files[i]))
                {
                    File.Delete(files[i]);
                }
            }
        }

        [Test]
        public void Runtime_Model_Info_Records_Requested_And_Effective_Model_Metadata()
        {
            WhisperModelRuntimeInfo info = new(
                ASRModelSize.Small,
                ASRModelSize.Small,
                "ggml-small.bin",
                "Whisper/ggml-small.bin",
                true,
                false);

            AsrDiagnosticRecord record = new()
            {
                RequestedModelSize = info.RequestedModelSize,
                EffectiveModelSize = info.EffectiveModelSize,
                EffectiveModelFileName = info.EffectiveModelFileName,
                EffectiveModelPath = info.EffectiveModelPath,
                ModelAvailableAtStart = info.ModelAvailableAtStart,
                ModelFallbackUsed = info.ModelFallbackUsed
            };

            string json = record.ToJsonLine();
            Assert.That(json, Does.Contain("\"requested_model_size\":\"small\""));
            Assert.That(json, Does.Contain("\"effective_model_size\":\"small\""));
            Assert.That(json, Does.Contain("\"effective_model_file_name\":\"ggml-small.bin\""));
            Assert.That(json, Does.Contain("\"effective_model_path\":\"Whisper/ggml-small.bin\""));
            Assert.That(json, Does.Contain("\"model_available_at_start\":true"));
            Assert.That(json, Does.Contain("\"model_fallback_used\":false"));
        }

        private sealed class FakeWhisperManager : MonoBehaviour
        {
            public string ModelPath { get; set; } = "Whisper/ggml-tiny.bin";
            public bool IsModelPathInStreamingAssets { get; set; }
            public bool IsLoaded { get; set; }
            public bool IsLoading { get; set; }
        }

        private static void EnsureWhisperModelFile(ASRModelSize modelSize)
        {
            string relativePath = WhisperModelConfiguration.BuildStreamingAssetsModelPath(modelSize);
            string path = Path.Combine(Application.streamingAssetsPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "test");
            }
        }

        private static void SetPrivate(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(target, value);
        }

        private static object GetPrivate(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field.GetValue(target);
        }

        private static string NormalizePathSeparators(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }
    }
}
