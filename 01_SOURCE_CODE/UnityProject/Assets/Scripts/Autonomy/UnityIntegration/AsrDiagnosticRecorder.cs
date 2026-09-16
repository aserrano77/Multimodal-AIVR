using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autonomy.Domain;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class AsrDiagnosticRecorder : MonoBehaviour
    {
        private const string LogPrefix = "[AsrDiagnosticRecorder]";

        [Header("Session")]
        [SerializeField] private bool _diagnosticModeEnabled;
        [SerializeField] private string _runLabel = "asr_diag";
        [SerializeField] private bool _copyDiagnosticWavsIntoRunFolder = true;
        [SerializeField] private bool _writeSummaryAfterEachUtterance = true;

        [Header("Optional Expected Utterances")]
        [SerializeField] private AsrDiagnosticExpectedUtterance[] _expectedUtterances = Array.Empty<AsrDiagnosticExpectedUtterance>();
        [SerializeField] private bool _useDefaultP38ExpectedUtterances;
        [SerializeField] private bool _advanceExpectedPhraseAfterEachUtterance = true;

        private readonly List<AsrDiagnosticRecord> _records = new();
        private StreamWriter _csvWriter;
        private StreamWriter _jsonlWriter;
        private string _runId = "";
        private string _runDirectory = "";
        private string _utterancesDirectory = "";
        private string _csvPath = "";
        private string _jsonlPath = "";
        private string _summaryPath = "";
        private int _recordSequence;
        private int _expectedIndex;

        public bool DiagnosticModeEnabled => _diagnosticModeEnabled;
        public string RunId => _runId;
        public string RunDirectory => _runDirectory;
        public string UtterancesDirectory => _utterancesDirectory;
        public IReadOnlyList<AsrDiagnosticRecord> Records => _records;

        public void SetDiagnosticModeEnabled(bool enabled)
        {
            _diagnosticModeEnabled = enabled;
        }

        private void OnDisable()
        {
            CloseWriters();
        }

        private void OnDestroy()
        {
            CloseWriters();
        }

        [ContextMenu("ASR Diagnostics/Start New Run")]
        public void StartNewRunFromInspector()
        {
            StartNewRun();
        }

        [ContextMenu("ASR Diagnostics/Write Summary")]
        public void WriteSummaryFromInspector()
        {
            WriteSummary();
        }

        public void StartNewRun()
        {
            CloseWriters();
            _records.Clear();
            _recordSequence = 0;
            _expectedIndex = 0;

            _runId = $"{_runLabel}_{DateTime.Now:yyyyMMdd_HHmmss}";
            string root = Path.Combine(ExperimentDataPathResolver.ResolveDataRoot(), "AsrDiagnostics");
            _runDirectory = Path.Combine(root, Sanitize(_runId));
            _utterancesDirectory = Path.Combine(_runDirectory, "utterances");
            Directory.CreateDirectory(_utterancesDirectory);

            _csvPath = string.Empty;
            _jsonlPath = string.Empty;
            _summaryPath = string.Empty;

            Debug.Log($"{LogPrefix} Diagnostic run started | run_id={_runId} directory={_runDirectory}", this);
        }

        public void RecordUtterance(AsrDiagnosticRecord record)
        {
            if (!_diagnosticModeEnabled || record == null)
            {
                return;
            }

            EnsureRunStarted();
            _recordSequence++;
            record.RunId = _runId;
            ApplyExpected(record);
            record.ClipPath = CopyClipIntoRunFolder(record.ClipPath, _recordSequence);
            EnsureWritersFor(record);
            _records.Add(record);

            _csvWriter.WriteLine(record.ToCsvRow());
            _jsonlWriter.WriteLine(record.ToJsonLine());
            _csvWriter.Flush();
            _jsonlWriter.Flush();

            if (_writeSummaryAfterEachUtterance)
            {
                WriteSummary();
            }
        }

        public void WriteSummary()
        {
            if (string.IsNullOrWhiteSpace(_runDirectory))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_summaryPath))
            {
                _summaryPath = Path.Combine(_runDirectory, BuildExplicitArtifactFileName("asr_summary", _runId, ResolveArtifactModelSize(), ".md"));
            }

            AsrDiagnosticSummary summary = AsrDiagnosticSummary.Calculate(_records);
            File.WriteAllText(
                _summaryPath,
                summary.ToMarkdown(_records),
                Encoding.UTF8);
        }

        public void LogDiagnosticEvent(string eventType, AsrDiagnosticRecord record = null, string reason = "")
        {
            if (!_diagnosticModeEnabled)
            {
                return;
            }

            EnsureRunStarted();
            Dictionary<string, object> payload = new()
            {
                ["run_id"] = _runId,
                ["utterance_id"] = record?.UtteranceId ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["diagnostic_directory"] = _runDirectory
            };
            TiagoExperimentTelemetry.LogEvent(eventType, payload);
        }

        private void EnsureRunStarted()
        {
            if (string.IsNullOrWhiteSpace(_runDirectory))
            {
                StartNewRun();
            }
        }

        private void EnsureWritersFor(AsrDiagnosticRecord record)
        {
            if (_csvWriter != null && _jsonlWriter != null)
            {
                return;
            }

            string modelSize = ResolveArtifactModelSize(record);
            _csvPath = Path.Combine(_runDirectory, BuildExplicitArtifactFileName("asr_diagnostics", _runId, modelSize, ".csv"));
            _jsonlPath = Path.Combine(_runDirectory, BuildExplicitArtifactFileName("asr_diagnostics", _runId, modelSize, ".jsonl"));
            _summaryPath = Path.Combine(_runDirectory, BuildExplicitArtifactFileName("asr_summary", _runId, modelSize, ".md"));
            _csvWriter = new StreamWriter(_csvPath, false, Encoding.UTF8);
            _jsonlWriter = new StreamWriter(_jsonlPath, false, Encoding.UTF8);
            _csvWriter.WriteLine(AsrDiagnosticRecord.CsvHeader());
            _csvWriter.Flush();
            _jsonlWriter.Flush();
        }

        private void ApplyExpected(AsrDiagnosticRecord record)
        {
            AsrDiagnosticExpectedUtterance[] expectedUtterances = ResolveExpectedUtterances();
            if (expectedUtterances == null || expectedUtterances.Length == 0)
            {
                return;
            }

            int index = Math.Min(_expectedIndex, expectedUtterances.Length - 1);
            AsrDiagnosticExpectedUtterance expected = expectedUtterances[index];
            if (expected != null && expected.HasAnyExpectedValue)
            {
                record.ExpectedPhrase = expected.ExpectedPhrase ?? string.Empty;
                record.ExpectedNormalizedText = expected.ExpectedNormalizedText ?? string.Empty;
                record.ExpectedIntent = expected.ExpectedIntent ?? string.Empty;
                record.ExpectedAlias = expected.ExpectedAlias ?? string.Empty;
                record.ExpectedDestination = expected.ExpectedDestination ?? string.Empty;
            }

            if (_advanceExpectedPhraseAfterEachUtterance)
            {
                _expectedIndex = Math.Min(_expectedIndex + 1, expectedUtterances.Length);
            }
        }

        private AsrDiagnosticExpectedUtterance[] ResolveExpectedUtterances()
        {
            if (_expectedUtterances != null && _expectedUtterances.Length > 0)
            {
                return _expectedUtterances;
            }

            return _useDefaultP38ExpectedUtterances
                ? CreateDefaultP38ExpectedUtterances()
                : Array.Empty<AsrDiagnosticExpectedUtterance>();
        }

        public static AsrDiagnosticExpectedUtterance[] CreateDefaultP38ExpectedUtterances()
        {
            return new[]
            {
                Expected("lleva la caja A1 a su zona", "lleva la caja A1 a su zona", "PickAndPlace", "A1", "SELF"),
                Expected("lleva A2 a su zona", "lleva la caja A2 a su zona", "PickAndPlace", "A2", "SELF"),
                Expected("mueve la caja B1", "mueve la caja B1 a su zona", "PickAndPlace", "B1", "SELF"),
                Expected("deposita la caja C1", "deposita la caja C1 a su zona", "PickAndPlace", "C1", "SELF"),
                Expected("coge la caja mas cercana", "coge la caja mas cercana", "NearestAmbiguity", "", ""),
                Expected("espera", "espera", "Stop", "", ""),
                Expected("continua", "continua", "Resume", "", ""),
                Expected("cancela la orden pendiente", "cancela la orden pendiente", "CancelPendingOrder", "", ""),
                Expected("mas cercana a mi", "mas cercana a mi", "NearestToUser", "", ""),
                Expected("mas cercana al robot", "mas cercana al robot", "NearestToRobot", "", "")
            };
        }

        private static AsrDiagnosticExpectedUtterance Expected(
            string phrase,
            string normalized,
            string intent,
            string alias,
            string destination)
        {
            return new AsrDiagnosticExpectedUtterance
            {
                ExpectedPhrase = phrase,
                ExpectedNormalizedText = normalized,
                ExpectedIntent = intent,
                ExpectedAlias = alias,
                ExpectedDestination = destination
            };
        }

        private string CopyClipIntoRunFolder(string sourcePath, int sequence)
        {
            if (!_copyDiagnosticWavsIntoRunFolder || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return sourcePath ?? string.Empty;
            }

            string fileName = $"utterance_{sequence:000}.wav";
            string destination = Path.Combine(_utterancesDirectory, fileName);
            File.Copy(sourcePath, destination, true);
            return destination;
        }

        public static string BuildExplicitArtifactFileName(string prefix, string runId, string modelSize, string extension)
        {
            string safePrefix = Sanitize(string.IsNullOrWhiteSpace(prefix) ? "asr_artifact" : prefix);
            string safeRunId = Sanitize(string.IsNullOrWhiteSpace(runId) ? "unknown_run" : runId);
            string safeModelSize = Sanitize(string.IsNullOrWhiteSpace(modelSize) ? "unknown" : modelSize.ToLowerInvariant());
            string safeExtension = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension;
            if (!safeExtension.StartsWith(".", StringComparison.Ordinal))
            {
                safeExtension = "." + safeExtension;
            }

            return $"{safePrefix}_{safeRunId}_{safeModelSize}{safeExtension}";
        }

        private string ResolveArtifactModelSize()
        {
            AsrDiagnosticRecord last = _records.Count > 0 ? _records[_records.Count - 1] : null;
            return ResolveArtifactModelSize(last);
        }

        private static string ResolveArtifactModelSize(AsrDiagnosticRecord record)
        {
            if (record != null && record.ResolveEffectiveModelSize() != ASRModelSize.Unknown)
            {
                return record.ResolveEffectiveModelSize().ToString().ToLowerInvariant();
            }

            return "unknown";
        }

        private void CloseWriters()
        {
            _csvWriter?.Dispose();
            _jsonlWriter?.Dispose();
            _csvWriter = null;
            _jsonlWriter = null;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "asr_diag";
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value.Replace(' ', '_');
        }
    }
}
