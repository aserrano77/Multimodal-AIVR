using Autonomy.Domain;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    [DisallowMultipleComponent]
    public sealed class RobotVoiceFeedbackDiagnosticRecorder : MonoBehaviour, IRobotVoiceFeedbackSink
    {
        private const string LogPrefix = "[RobotVoiceFeedbackDiagnosticRecorder]";

        [Header("Session")]
        [SerializeField] private bool _diagnosticModeEnabled;
        [SerializeField] private string _runLabel = "feedback_diag";
        [SerializeField] private bool _writeSummaryAfterEachFeedback = true;
        [SerializeField] private string _sinkType = "robot_voice_feedback_diagnostic_recorder";

        private readonly List<RobotVoiceFeedbackDiagnosticRecord> _records = new();
        private string _runId = "";
        private string _runDirectory = "";
        private string _csvPath = "";
        private string _jsonlPath = "";
        private string _summaryPath = "";
        private int _sequenceIndex;

        public bool DiagnosticModeEnabled => _diagnosticModeEnabled;
        public string RunId => _runId;
        public string RunDirectory => _runDirectory;
        public string CsvPath => _csvPath;
        public string JsonlPath => _jsonlPath;
        public string SummaryPath => _summaryPath;
        public IReadOnlyList<RobotVoiceFeedbackDiagnosticRecord> Records => _records;

        public void SetDiagnosticModeEnabled(bool enabled)
        {
            _diagnosticModeEnabled = enabled;
        }

        private void OnDisable()
        {
            FinalizeRun();
        }

        private void OnDestroy()
        {
            FinalizeRun();
        }

        [ContextMenu("Voice Feedback Diagnostics/Start New Run")]
        public void StartNewRunFromInspector()
        {
            StartNewRun();
        }

        [ContextMenu("Voice Feedback Diagnostics/Write Summary")]
        public void WriteSummaryFromInspector()
        {
            WriteSummary();
        }

        public void StartNewRun()
        {
            FinalizeRun();
            _records.Clear();
            _sequenceIndex = 0;

            _runId = $"{_runLabel}_{DateTime.Now:yyyyMMdd_HHmmss}";
            string root = Path.Combine(ExperimentDataPathResolver.ResolveDataRoot(), "VoiceFeedbackDiagnostics");
            _runDirectory = Path.Combine(root, Sanitize(_runId));
            Directory.CreateDirectory(_runDirectory);

            _csvPath = Path.Combine(_runDirectory, BuildArtifactFileName("voice_feedback_events", _runId, ".csv"));
            _jsonlPath = Path.Combine(_runDirectory, BuildArtifactFileName("voice_feedback_events", _runId, ".jsonl"));
            _summaryPath = Path.Combine(_runDirectory, BuildArtifactFileName("voice_feedback_summary", _runId, ".md"));
            InitializeArtifactFiles();
            WriteSummary();

            Debug.Log($"{LogPrefix} Diagnostic run started | run_id={_runId} directory={_runDirectory}", this);
        }

        public void Emit(RobotVoiceFeedbackMessage message)
        {
            if (!_diagnosticModeEnabled || message == null)
            {
                return;
            }

            EnsureRunStarted();
            _sequenceIndex++;
            RobotVoiceFeedbackDiagnosticRecord record = RobotVoiceFeedbackDiagnosticRecord.FromMessage(
                message,
                _runId,
                _sequenceIndex,
                _sinkType);
            Record(record);
        }

        public void Record(RobotVoiceFeedbackDiagnosticRecord record)
        {
            if (!_diagnosticModeEnabled || record == null)
            {
                return;
            }

            EnsureRunStarted();
            if (record.SequenceIndex <= 0)
            {
                _sequenceIndex++;
                record.SequenceIndex = _sequenceIndex;
            }

            if (string.IsNullOrWhiteSpace(record.RunId))
            {
                record.RunId = _runId;
            }

            if (string.IsNullOrWhiteSpace(record.Timestamp))
            {
                record.Timestamp = DateTime.UtcNow.ToString("O");
            }

            if (string.IsNullOrWhiteSpace(record.SinkType))
            {
                record.SinkType = _sinkType;
            }

            _records.Add(record);
            File.AppendAllText(_csvPath, record.ToCsvRow() + Environment.NewLine, Encoding.UTF8);
            File.AppendAllText(_jsonlPath, record.ToJsonLine() + Environment.NewLine, Encoding.UTF8);

            if (_writeSummaryAfterEachFeedback)
            {
                WriteSummary();
            }
        }

        public void FinalizeRun()
        {
            if (string.IsNullOrWhiteSpace(_runDirectory))
            {
                return;
            }

            WriteSummary();
        }

        public void WriteSummary()
        {
            if (string.IsNullOrWhiteSpace(_runDirectory))
            {
                return;
            }

            RobotVoiceFeedbackDiagnosticSummary summary = RobotVoiceFeedbackDiagnosticSummary.Calculate(_records, _runId);
            File.WriteAllText(_summaryPath, summary.ToMarkdown(_records), Encoding.UTF8);
        }

        public static string BuildArtifactFileName(string prefix, string runId, string extension)
        {
            string safePrefix = Sanitize(string.IsNullOrWhiteSpace(prefix) ? "voice_feedback_events" : prefix);
            string safeRunId = Sanitize(string.IsNullOrWhiteSpace(runId) ? "unknown_run" : runId);
            string safeExtension = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension;
            if (!safeExtension.StartsWith(".", StringComparison.Ordinal))
            {
                safeExtension = "." + safeExtension;
            }

            return $"{safePrefix}_{safeRunId}{safeExtension}";
        }

        private void EnsureRunStarted()
        {
            if (string.IsNullOrWhiteSpace(_runDirectory))
            {
                StartNewRun();
            }
        }

        private void InitializeArtifactFiles()
        {
            Directory.CreateDirectory(_runDirectory);
            File.WriteAllText(_csvPath, RobotVoiceFeedbackDiagnosticRecord.CsvHeader() + Environment.NewLine, Encoding.UTF8);
            File.WriteAllText(_jsonlPath, string.Empty, Encoding.UTF8);
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "feedback_diag";
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value.Replace(' ', '_');
        }
    }
}
