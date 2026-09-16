using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Autonomy.Domain
{
    public enum ExperimentDataValidationStatus
    {
        Pass,
        Warn,
        Fail
    }

    public sealed class ExperimentDataValidationInput
    {
        public string RunDirectory { get; set; } = string.Empty;
        public string ManifestPath { get; set; } = string.Empty;
        public string EventsPath { get; set; } = string.Empty;
        public string SamplesPath { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
    }

    public sealed class ExperimentDataValidationReport
    {
        public ExperimentDataValidationStatus Status { get; set; } = ExperimentDataValidationStatus.Pass;
        public string RunId { get; set; } = "unknown";
        public string Scene { get; set; } = "unknown";
        public int EventCount { get; set; }
        public int SampleCount { get; set; }
        public bool SessionDetected { get; set; }
        public List<string> TrialsDetected { get; } = new();
        public List<string> TrialsStarted { get; } = new();
        public List<string> TrialsFailedSanity { get; } = new();
        public List<string> TrialsCompleted { get; } = new();
        public List<string> TrialsAborted { get; } = new();
        public List<string> TrialsInvalidBeforeStart { get; } = new();
        public List<string> TrialsFailedReset { get; } = new();
        public List<string> TrialsValidForAnalysis { get; } = new();
        public List<string> ConditionsDetected { get; } = new();
        public List<string> ConditionsStarted { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> RedFlags { get; } = new();
        public List<string> MissingFields { get; } = new();
        public List<string> OutOfOrderEvents { get; } = new();
        public List<string> IdInconsistencies { get; } = new();
        public List<string> ConditionFlagInconsistencies { get; } = new();
        public List<string> MetricSufficiencyWarnings { get; } = new();
        public List<string> MetricSufficiencyPasses { get; } = new();
        public string MarkdownPath { get; set; } = string.Empty;
        public string JsonPath { get; set; } = string.Empty;
    }

    public static class ExperimentDataConsistencyValidator
    {
        private const string ExpectedScene = "autonomous_demo_step22_multimodal_bridge";
        private const double RobotPoseResetPostSampleEpsilonSeconds = 0.075;
        private const double RobotPoseResetMaxPostSampleDelaySeconds = 1.0;

        private static readonly Dictionary<string, CanonicalCondition> CanonicalConditions = new(StringComparer.Ordinal)
        {
            ["C00_robot_off_voice_off"] = new CanonicalCondition(false, false, "Disabled"),
            ["C01_robot_off_voice_on"] = new CanonicalCondition(false, true, "Disabled"),
            ["C10_robot_on_voice_off"] = new CanonicalCondition(true, false, "AssistedSelection"),
            ["C11_robot_on_voice_on"] = new CanonicalCondition(true, true, "AssistedSelection")
        };

        private static readonly HashSet<string> FinalConditionIds = new(StringComparer.Ordinal)
        {
            "C00_robot_off_voice_off",
            "C10_robot_on_voice_off",
            "C11_robot_on_voice_on"
        };

        private static readonly string[] TrialSequence =
        {
            "experiment_trial_prepare_started",
            "experiment_condition_applied",
            "experiment_condition_orchestrator_applied",
            "experiment_trial_reset_started",
            "experiment_trial_sanity_check_passed",
            "experiment_trial_round_spawn_requested",
            "spawn_round_started",
            "round_started",
            "experiment_trial_started",
            "experiment_trial_started_by_orchestrator"
        };

        private static readonly string[] ContextFields =
        {
            "session_id",
            "trial_id",
            "trial_index",
            "condition_id",
            "condition_name",
            "robot_enabled",
            "voice_enabled",
            "assistance_mode",
            "spawn_generation_mode",
            "round_id"
        };

        public static ExperimentDataValidationReport ValidateRunDirectory(string runDirectory, string outputDirectory = "")
        {
            var input = new ExperimentDataValidationInput { RunDirectory = runDirectory ?? string.Empty, OutputDirectory = outputDirectory ?? string.Empty };
            ResolveArtifacts(input);
            return Validate(input);
        }

        public static ExperimentDataValidationReport ValidateFiles(string manifestPath, string eventsPath, string samplesPath, string outputDirectory = "")
        {
            return Validate(new ExperimentDataValidationInput
            {
                RunDirectory = ResolveCommonDirectory(manifestPath, eventsPath, samplesPath),
                ManifestPath = manifestPath ?? string.Empty,
                EventsPath = eventsPath ?? string.Empty,
                SamplesPath = samplesPath ?? string.Empty,
                OutputDirectory = outputDirectory ?? string.Empty
            });
        }

        public static ExperimentDataValidationReport Validate(ExperimentDataValidationInput input)
        {
            input ??= new ExperimentDataValidationInput();
            ResolveArtifacts(input);
            var report = new ExperimentDataValidationReport();
            Dictionary<string, object> manifest = ValidateManifest(input, report);
            List<EventRecord> events = ValidateEvents(input, report);
            ValidateSamples(input, report, manifest, events);
            ValidateRobotPoseResetSamples(input, events, report);
            ValidateEventSemantics(events, report);
            ValidateFinalConditionDiagnostics(manifest, events, report);
            ValidateSessionSummaries(input, manifest, events, report);
            ValidateMetricSufficiency(events, report);
            FinalizeStatus(report);
            ExportReports(input, report);
            return report;
        }

        private static void ResolveArtifacts(ExperimentDataValidationInput input)
        {
            if (string.IsNullOrWhiteSpace(input.RunDirectory) || !Directory.Exists(input.RunDirectory))
            {
                return;
            }

            input.ManifestPath = FirstExisting(input.ManifestPath, input.RunDirectory, "*manifest*.json");
            input.EventsPath = FirstExisting(input.EventsPath, input.RunDirectory, "*events*.jsonl");
            input.SamplesPath = FirstExisting(input.SamplesPath, input.RunDirectory, "*samples*.csv");
        }

        private static Dictionary<string, object> ValidateManifest(ExperimentDataValidationInput input, ExperimentDataValidationReport report)
        {
            var manifest = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(input.ManifestPath) || !File.Exists(input.ManifestPath))
            {
                report.RedFlags.Add("manifest_missing");
                return manifest;
            }

            try
            {
                manifest = MiniJsonParser.ParseObject(File.ReadAllText(input.ManifestPath));
            }
            catch (Exception ex)
            {
                report.RedFlags.Add($"manifest_unparseable:{ex.Message}");
                return manifest;
            }

            RequireManifestField(manifest, "run_id", report);
            RequireManifestField(manifest, "scene", report);
            RequireManifestField(manifest, "timestamp", report);
            RequireManifestField(manifest, "events_file", report);
            RequireManifestField(manifest, "samples_file", report);
            RequireManifestField(manifest, "manifest_file", report);

            report.RunId = GetString(manifest, "run_id", "unknown");
            report.Scene = GetString(manifest, "scene", "unknown");
            if (!string.Equals(report.Scene, ExpectedScene, StringComparison.Ordinal))
            {
                report.Warnings.Add($"scene_unexpected:{report.Scene}");
            }

            CheckReferencedFile(input, manifest, "events_file", report);
            CheckReferencedFile(input, manifest, "samples_file", report);
            CheckReferencedFile(input, manifest, "manifest_file", report);
            if (!string.IsNullOrWhiteSpace(input.RunDirectory) &&
                !string.Equals(report.RunId, "unknown", StringComparison.Ordinal) &&
                !Path.GetFileName(input.RunDirectory).Contains(report.RunId))
            {
                report.Warnings.Add($"run_id_not_visible_in_folder_name:{report.RunId}");
            }

            return manifest;
        }

        private static List<EventRecord> ValidateEvents(ExperimentDataValidationInput input, ExperimentDataValidationReport report)
        {
            var events = new List<EventRecord>();
            if (string.IsNullOrWhiteSpace(input.EventsPath) || !File.Exists(input.EventsPath))
            {
                report.RedFlags.Add("events_missing");
                return events;
            }

            int lineNumber = 0;
            double? previousTimestamp = null;
            foreach (string line in File.ReadLines(input.EventsPath))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    Dictionary<string, object> root = MiniJsonParser.ParseObject(line);
                    string name = FirstString(root, "event_type", "event_name", "type", "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        report.MissingFields.Add($"events.line{lineNumber}:event_name");
                    }

                    if (!TryGetTimestamp(root, out double timestamp))
                    {
                        report.MissingFields.Add($"events.line{lineNumber}:timestamp");
                    }
                    else if (previousTimestamp.HasValue && timestamp < previousTimestamp.Value)
                    {
                        if (IsTerminalLegacyTimestampReset(name))
                        {
                            report.Warnings.Add($"terminal_timestamp_reset:line{lineNumber}:{name}");
                        }
                        else
                        {
                            report.OutOfOrderEvents.Add($"line{lineNumber}:{name}");
                        }
                    }

                    previousTimestamp = double.IsNaN(timestamp) || IsTerminalLegacyTimestampReset(name)
                        ? previousTimestamp
                        : timestamp;
                    Dictionary<string, object> payload = GetDictionary(root, "payload") ?? root;
                    if (!root.ContainsKey("payload") && !HasStructuredFields(root))
                    {
                        report.MissingFields.Add($"events.line{lineNumber}:payload");
                    }

                    events.Add(new EventRecord(lineNumber, name, timestamp, root, payload));
                }
                catch (Exception ex)
                {
                    report.Warnings.Add($"events.line{lineNumber}.json_corrupt:{ex.Message}");
                }
            }

            report.EventCount = events.Count;
            if (events.Count == 0)
            {
                report.RedFlags.Add("events_empty_or_fully_corrupt");
            }

            return events;
        }

        private static void ValidateSamples(
            ExperimentDataValidationInput input,
            ExperimentDataValidationReport report,
            Dictionary<string, object> manifest,
            List<EventRecord> events)
        {
            if (string.IsNullOrWhiteSpace(input.SamplesPath) || !File.Exists(input.SamplesPath))
            {
                report.RedFlags.Add("samples_missing");
                return;
            }

            string[] lines = File.ReadAllLines(input.SamplesPath);
            if (lines.Length == 0)
            {
                report.RedFlags.Add("samples_no_header");
                return;
            }

            string[] header = ParseCsvLine(lines[0]);
            if (header.Length == 0 || string.IsNullOrWhiteSpace(header[0]))
            {
                report.RedFlags.Add("samples_header_empty");
                return;
            }

            string[] expected = { "timestamp_unity", "timestamp_wall", "run_id", "run_elapsed_time" };
            foreach (string column in expected)
            {
                if (!header.Contains(column, StringComparer.Ordinal))
                {
                    report.Warnings.Add($"samples_missing_column:{column}");
                }
            }

            int timestampIndex = Array.FindIndex(header, c => c == "timestamp_unity" || c == "run_elapsed_time");
            int runIdIndex = Array.FindIndex(header, c => c == "run_id");
            double? previous = null;
            int sampleRows = 0;
            string manifestRunId = GetString(manifest, "run_id", string.Empty);
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                sampleRows++;
                string[] row = ParseCsvLine(lines[i]);
                if (timestampIndex >= 0 && timestampIndex < row.Length && TryParseDouble(row[timestampIndex], out double timestamp))
                {
                    if (previous.HasValue && timestamp < previous.Value)
                    {
                        report.OutOfOrderEvents.Add($"samples.row{i + 1}");
                    }

                    previous = timestamp;
                }

                if (runIdIndex >= 0 && runIdIndex < row.Length && !string.IsNullOrWhiteSpace(manifestRunId) &&
                    !string.Equals(row[runIdIndex], manifestRunId, StringComparison.Ordinal))
                {
                    report.IdInconsistencies.Add($"samples.row{i + 1}.run_id:{row[runIdIndex]}!=manifest:{manifestRunId}");
                }
            }

            string[] trialContextColumns =
            {
                "session_id",
                "trial_id",
                "condition_id",
                "condition_name",
                "robot_enabled",
                "voice_enabled",
                "assistance_mode"
            };
            if (trialContextColumns.Any(column => !header.Contains(column, StringComparer.Ordinal)))
            {
                report.Warnings.Add("samples_are_run_level_only:trial_condition_attribution_requires_events_windows");
            }

            report.SampleCount = sampleRows;
            if (sampleRows == 0 && events.Any(e => e.Name == "experiment_trial_started_by_orchestrator"))
            {
                report.RedFlags.Add("samples_empty_for_trial_run");
            }
            else if (sampleRows == 0)
            {
                report.Warnings.Add("samples_empty");
            }
        }

        private static void ValidateRobotPoseResetSamples(
            ExperimentDataValidationInput input,
            List<EventRecord> events,
            ExperimentDataValidationReport report)
        {
            List<EventRecord> resetEvents = events
                .Where(e => e.Name == "robot_pose_reset_completed")
                .ToList();
            if (resetEvents.Count == 0)
            {
                return;
            }

            List<RobotPoseSample> samples = ReadRobotPoseSamples(input, report);
            if (samples.Count == 0)
            {
                AddWarningOnce(report, "robot_pose_reset_sample_validation_unavailable");
                return;
            }

            foreach (EventRecord ev in resetEvents)
            {
                if (!TryGetEventVectorXZ(ev, "robot_position_target", out double targetX, out double targetZ) ||
                    !TryGetEventDouble(ev, "robot_yaw_target_deg", out double targetYaw))
                {
                    AddWarningOnce(report, "robot_pose_reset_sample_validation_unavailable");
                    continue;
                }

                if (!TrySelectPostResetSample(samples, ev.Timestamp, out RobotPoseSample selectedSample, out string sampleSelectionReason))
                {
                    report.Warnings.Add($"robot_pose_reset_no_post_sample_available:line{ev.LineNumber}:event_time={ev.Timestamp.ToString("F3", CultureInfo.InvariantCulture)}");
                    continue;
                }

                double distance = Math.Sqrt(Math.Pow(selectedSample.RobotX - targetX, 2.0) + Math.Pow(selectedSample.RobotZ - targetZ, 2.0));
                double yawDelta = Math.Abs(MathDeltaAngle(selectedSample.RobotYawDegrees, targetYaw));
                string sampleTiming = selectedSample.RunElapsedTime >= ev.Timestamp ? "post" : "pre";
                if (distance > 0.25 || yawDelta > 10.0)
                {
                    report.RedFlags.Add(
                        $"robot_pose_reset_mismatch:line{ev.LineNumber}:sample_row={selectedSample.RowNumber}:event_time={ev.Timestamp.ToString("F3", CultureInfo.InvariantCulture)}:sample_time={selectedSample.RunElapsedTime.ToString("F3", CultureInfo.InvariantCulture)}:sample_timing={sampleTiming}:sample_selection={sampleSelectionReason}:distance_m={distance.ToString("F3", CultureInfo.InvariantCulture)}:yaw_delta_deg={yawDelta.ToString("F1", CultureInfo.InvariantCulture)}");
                }
                else
                {
                    report.MetricSufficiencyPasses.Add(
                        $"robot_pose_reset_sample_consistent:line{ev.LineNumber}:sample_row={selectedSample.RowNumber}:event_time={ev.Timestamp.ToString("F3", CultureInfo.InvariantCulture)}:sample_time={selectedSample.RunElapsedTime.ToString("F3", CultureInfo.InvariantCulture)}:sample_timing={sampleTiming}:sample_selection={sampleSelectionReason}");
                }
            }
        }

        private static bool TrySelectPostResetSample(
            IReadOnlyList<RobotPoseSample> samples,
            double eventTime,
            out RobotPoseSample selectedSample,
            out string selectionReason)
        {
            double preferredStart = eventTime + RobotPoseResetPostSampleEpsilonSeconds;
            foreach (RobotPoseSample sample in samples)
            {
                if (sample.RunElapsedTime >= preferredStart &&
                    sample.RunElapsedTime - eventTime <= RobotPoseResetMaxPostSampleDelaySeconds)
                {
                    selectedSample = sample;
                    selectionReason = "post_epsilon";
                    return true;
                }
            }

            foreach (RobotPoseSample sample in samples)
            {
                if (sample.RunElapsedTime >= eventTime &&
                    sample.RunElapsedTime - eventTime <= RobotPoseResetMaxPostSampleDelaySeconds)
                {
                    selectedSample = sample;
                    selectionReason = "first_post_available";
                    return true;
                }
            }

            selectedSample = default;
            selectionReason = "no_post_sample";
            return false;
        }

        private static List<RobotPoseSample> ReadRobotPoseSamples(ExperimentDataValidationInput input, ExperimentDataValidationReport report)
        {
            var samples = new List<RobotPoseSample>();
            if (string.IsNullOrWhiteSpace(input.SamplesPath) || !File.Exists(input.SamplesPath))
            {
                return samples;
            }

            string[] lines = File.ReadAllLines(input.SamplesPath);
            if (lines.Length == 0)
            {
                return samples;
            }

            string[] header = ParseCsvLine(lines[0]);
            int timeIndex = Array.FindIndex(header, column => string.Equals(column, "run_elapsed_time", StringComparison.Ordinal));
            int xIndex = Array.FindIndex(header, column => string.Equals(column, "robot_x", StringComparison.Ordinal));
            int zIndex = Array.FindIndex(header, column => string.Equals(column, "robot_z", StringComparison.Ordinal));
            int yawIndex = Array.FindIndex(header, column => string.Equals(column, "robot_yaw_deg", StringComparison.Ordinal));
            if (timeIndex < 0 || xIndex < 0 || zIndex < 0 || yawIndex < 0)
            {
                AddWarningOnce(report, "robot_pose_reset_sample_validation_unavailable");
                return samples;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] row = ParseCsvLine(lines[i]);
                if (timeIndex >= row.Length || xIndex >= row.Length || zIndex >= row.Length || yawIndex >= row.Length)
                {
                    continue;
                }

                if (!TryParseDouble(row[timeIndex], out double time) ||
                    !TryParseDouble(row[xIndex], out double x) ||
                    !TryParseDouble(row[zIndex], out double z) ||
                    !TryParseDouble(row[yawIndex], out double yaw))
                {
                    continue;
                }

                samples.Add(new RobotPoseSample(i + 1, time, x, z, yaw));
            }

            return samples;
        }

        private static void ValidateEventSemantics(List<EventRecord> events, ExperimentDataValidationReport report)
        {
            report.SessionDetected = events.Any(e => e.Name == "experiment_session_started");
            bool trialDetected = events.Any(e => TrialSequence.Contains(e.Name, StringComparer.Ordinal));
            if (trialDetected)
            {
                foreach (string expected in TrialSequence)
                {
                    if (!events.Any(e => e.Name == expected))
                    {
                        report.RedFlags.Add($"trial_event_missing:{expected}");
                    }
                }
            }

            if (events.Any(e => e.Name.Contains("zero_state", StringComparison.Ordinal)) &&
                !events.Any(e => e.Name == "experiment_zero_state_sanity_passed"))
            {
                report.RedFlags.Add("zero_state_sanity_event_missing");
            }

            bool sessionActive = false;
            bool trialActive = false;
            bool roundActive = false;
            bool conditionApplied = false;
            string effectiveSessionId = string.Empty;
            var preSessionIds = new HashSet<string>(StringComparer.Ordinal);
            var trialRounds = new Dictionary<string, string>(StringComparer.Ordinal);
            var roundTrials = new Dictionary<string, string>(StringComparer.Ordinal);
            var conditionRoundSlots = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
            var expectedRoundsByCondition = new Dictionary<string, int>(StringComparer.Ordinal);
            var resetEvents = new HashSet<string>(StringComparer.Ordinal);
            var conditionBlockCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var closedTrials = new HashSet<string>(StringComparer.Ordinal);
            var poseResetFailedTrials = new HashSet<string>(StringComparer.Ordinal);
            foreach (EventRecord ev in events)
            {
                var state = new SemanticState(sessionActive, trialActive, roundActive, conditionApplied);
                string eventSessionId = GetEventString(ev, "session_id");
                string eventTrialId = GetEventString(ev, "trial_id");
                string eventRoundId = GetEventString(ev, "round_id");
                if (!sessionActive && !string.IsNullOrWhiteSpace(eventSessionId))
                {
                    preSessionIds.Add(eventSessionId);
                }

                if (!string.IsNullOrWhiteSpace(eventTrialId) &&
                    closedTrials.Contains(eventTrialId) &&
                    IsPostTrialOperationalActivity(ev))
                {
                    string redFlag = ev.Name == "autonomy_request_submitted"
                        ? $"autonomy_request_after_trial_completed:line{ev.LineNumber}:trial={eventTrialId}"
                        : $"post_trial_operational_activity:line{ev.LineNumber}:trial={eventTrialId}:event={ev.Name}";
                    report.RedFlags.Add(redFlag);
                }

                if (ev.Name == "robot_pose_reset_failed" && !string.IsNullOrWhiteSpace(eventTrialId))
                {
                    poseResetFailedTrials.Add(eventTrialId);
                }

                if (!string.IsNullOrWhiteSpace(eventTrialId) &&
                    poseResetFailedTrials.Contains(eventTrialId) &&
                    IsPostResetFailureOperationalActivity(ev))
                {
                    report.RedFlags.Add($"operational_activity_after_robot_pose_reset_failed:line{ev.LineNumber}:trial={eventTrialId}:event={ev.Name}");
                }

                if (TryFindStaleRoundTarget(ev, eventRoundId, out string staleTarget, out string expectedRound))
                {
                    report.RedFlags.Add($"stale_target_cross_trial:line{ev.LineNumber}:trial={eventTrialId}:round={expectedRound}:target={staleTarget}:event={ev.Name}");
                }

                if (ev.Name == "experiment_session_started")
                {
                    sessionActive = true;
                    effectiveSessionId = FirstNonEmpty(eventSessionId, GetEventString(ev, "sessionId"));
                    foreach (string preSessionId in preSessionIds)
                    {
                        if (!string.IsNullOrWhiteSpace(effectiveSessionId) &&
                            !string.Equals(preSessionId, effectiveSessionId, StringComparison.Ordinal))
                        {
                            report.Warnings.Add($"pre_session_provisional_session_id:{preSessionId}->effective:{effectiveSessionId}");
                        }
                    }
                }

                if (ev.Name == "experiment_trial_prepare_started")
                {
                    trialActive = true;
                }

                if (ev.Name == "experiment_condition_applied")
                {
                    conditionApplied = true;
                }

                if (ev.Name == "round_started")
                {
                    roundActive = true;
                }

                if (IsTrialClosingEvent(ev) && !string.IsNullOrWhiteSpace(eventTrialId))
                {
                    closedTrials.Add(eventTrialId);
                }

                if ((ev.Name.Contains("spawn", StringComparison.Ordinal) || ev.Name == "round_started" || ev.Name == "experiment_trial_started") && !sessionActive)
                {
                    report.RedFlags.Add($"event_before_session:{ev.Name}:line{ev.LineNumber}");
                }

                if (ev.Name == "autonomy_request_submitted" && (!sessionActive || !trialActive || !roundActive))
                {
                    report.RedFlags.Add($"autonomy_request_before_active_session_trial_round:line{ev.LineNumber}");
                }

                ValidateContextFields(ev, report);
                ValidateCondition(ev, report, state);
                TrackIds(ev, report, trialRounds);
                ValidateRoundMetadata(ev, report, roundTrials, conditionRoundSlots, expectedRoundsByCondition);
                TrackResetDuplicate(ev, report, resetEvents);
                TrackConditionBlockFrequency(ev, conditionBlockCounts);
            }

            foreach (KeyValuePair<string, int> pair in conditionBlockCounts)
            {
                if (pair.Value >= 4)
                {
                    report.Warnings.Add($"high_frequency_condition_block_events:{pair.Key}:count={pair.Value}");
                }
            }

            bool runClosed = events.Any(e =>
                e.Name == "experiment_session_completed" ||
                e.Name == "run_finished" ||
                e.Name == "experiment_session_aborted");
            foreach (KeyValuePair<string, int> pair in expectedRoundsByCondition)
            {
                if (!runClosed || !conditionRoundSlots.TryGetValue(pair.Key, out Dictionary<int, string> slots))
                {
                    continue;
                }

                for (int i = 1; i <= pair.Value; i++)
                {
                    if (!slots.ContainsKey(i))
                    {
                        report.RedFlags.Add($"condition_round_missing:{pair.Key}:round={i}:expected={pair.Value}");
                    }
                }
            }
        }

        private static void ValidateContextFields(EventRecord ev, ExperimentDataValidationReport report)
        {
            bool shouldCarryContext = ev.Name == "experiment_condition_applied" ||
                ev.Name == "spawn_round_started" ||
                ev.Name == "round_started" ||
                ev.Name == "experiment_trial_started" ||
                ev.Name == "experiment_trial_started_by_orchestrator" ||
                ev.Name == "autonomy_request_submitted";
            if (!shouldCarryContext)
            {
                return;
            }

            foreach (string field in ContextFields)
            {
                if (!HasField(ev, field))
                {
                    report.MissingFields.Add($"events.line{ev.LineNumber}:{ev.Name}.{field}");
                }
            }
        }

        private static void ValidateCondition(EventRecord ev, ExperimentDataValidationReport report, SemanticState state)
        {
            string conditionId = GetEventString(ev, "condition_id");
            if (!string.IsNullOrWhiteSpace(conditionId))
            {
                if (string.Equals(conditionId, "uninitialized", StringComparison.Ordinal))
                {
                    if (IsUninitializedContextForbidden(ev, state))
                    {
                        report.RedFlags.Add($"uninitialized_condition_in_active_experiment_context:line{ev.LineNumber}:{ev.Name}");
                    }
                    else
                    {
                        AddWarningOnce(report, "pretrial_uninitialized_context");
                    }

                    return;
                }

                if (!report.ConditionsDetected.Contains(conditionId))
                {
                    report.ConditionsDetected.Add(conditionId);
                }

                if (!CanonicalConditions.TryGetValue(conditionId, out CanonicalCondition canonical))
                {
                    report.RedFlags.Add($"condition_id_non_canonical:{conditionId}:line{ev.LineNumber}");
                    return;
                }

                CompareBool(ev, "robot_enabled", canonical.RobotEnabled, report);
                CompareBool(ev, "voice_enabled", canonical.VoiceEnabled, report);
                string assistance = GetEventString(ev, "assistance_mode");
                if (!string.IsNullOrWhiteSpace(assistance) && !string.Equals(assistance, canonical.AssistanceMode, StringComparison.Ordinal))
                {
                    report.ConditionFlagInconsistencies.Add($"line{ev.LineNumber}:{conditionId}.assistance_mode={assistance}");
                }
            }

            bool? robot = GetEventBool(ev, "robot_enabled");
            bool? voice = GetEventBool(ev, "voice_enabled");
            string reason = GetEventString(ev, "reason");
            if (ev.Name == "autonomy_request_submitted" && robot == false)
            {
                report.RedFlags.Add($"autonomy_request_with_robot_disabled:line{ev.LineNumber}");
            }

            if (ev.Name == "autonomy_request_submitted" && voice == false && IsVoiceOrigin(ev))
            {
                report.RedFlags.Add($"autonomy_request_with_voice_disabled:line{ev.LineNumber}");
            }

            if (voice == false && GetEventBool(ev, "bridge_invoked") == true && IsVoiceOrigin(ev))
            {
                report.RedFlags.Add($"voice_bridge_invoked_when_voice_disabled:line{ev.LineNumber}");
            }

            if (ev.Name.Contains("voice", StringComparison.Ordinal) &&
                EventLooksExecutableVoice(ev) &&
                voice == false &&
                !IsVoiceBlockedByCondition(ev))
            {
                report.RedFlags.Add($"voice_executable_not_blocked_when_voice_disabled:line{ev.LineNumber}");
            }

            if (string.Equals(reason, "robot_disabled_by_condition", StringComparison.Ordinal) && robot == true)
            {
                report.ConditionFlagInconsistencies.Add($"robot_disabled_reason_with_robot_enabled:line{ev.LineNumber}");
            }

            if (string.Equals(reason, "voice_disabled_by_condition", StringComparison.Ordinal) && voice == true)
            {
                report.ConditionFlagInconsistencies.Add($"voice_disabled_reason_with_voice_enabled:line{ev.LineNumber}");
            }

            string assistanceMode = GetEventString(ev, "assistance_mode");
            if (string.Equals(assistanceMode, "Disabled", StringComparison.Ordinal) &&
                IsEffectiveRobotAssistance(ev))
            {
                report.RedFlags.Add($"assistance_effective_while_disabled:line{ev.LineNumber}:{ev.Name}");
            }

            if (robot == false && IsEffectiveRobotAssistance(ev))
            {
                report.RedFlags.Add($"robot_intervention_with_robot_disabled:line{ev.LineNumber}:{ev.Name}");
            }
        }

        private static void TrackIds(EventRecord ev, ExperimentDataValidationReport report, Dictionary<string, string> trialRounds)
        {
            string trialId = GetEventString(ev, "trial_id");
            string roundId = GetEventString(ev, "round_id");
            if (!string.IsNullOrWhiteSpace(trialId) && !report.TrialsDetected.Contains(trialId))
            {
                report.TrialsDetected.Add(trialId);
            }

            if (!string.IsNullOrWhiteSpace(trialId))
            {
                if (ev.Name == "experiment_trial_started_by_orchestrator")
                {
                    AddUnique(report.TrialsStarted, trialId);
                    string conditionId = GetEventString(ev, "condition_id");
                    if (!string.IsNullOrWhiteSpace(conditionId) && !string.Equals(conditionId, "uninitialized", StringComparison.Ordinal))
                    {
                        AddUnique(report.ConditionsStarted, conditionId);
                    }
                }
                else if (ev.Name == "experiment_trial_sanity_check_failed")
                {
                    AddUnique(report.TrialsFailedSanity, trialId);
                }
                else if (ev.Name == "experiment_trial_completed_by_orchestrator")
                {
                    AddUnique(report.TrialsCompleted, trialId);
                    if (IsValidForAnalysis(ev))
                    {
                        AddUnique(report.TrialsValidForAnalysis, trialId);
                    }
                }
                else if (ev.Name == "experiment_trial_aborted")
                {
                    AddUnique(report.TrialsAborted, trialId);
                }
                else if (ev.Name == "experiment_trial_marked_invalid" ||
                    ev.Name == "experiment_prepared_trial_invalid_summary_written")
                {
                    string terminalState = GetEventString(ev, "terminal_state");
                    string failureReason = GetEventString(ev, "failure_reason");
                    if (string.Equals(terminalState, "invalid_before_start", StringComparison.Ordinal) ||
                        string.Equals(failureReason, "robot_pose_reset_failed", StringComparison.Ordinal))
                    {
                        AddUnique(report.TrialsInvalidBeforeStart, trialId);
                    }

                    if (string.Equals(failureReason, "robot_pose_reset_failed", StringComparison.Ordinal))
                    {
                        AddUnique(report.TrialsFailedReset, trialId);
                    }
                }
                else if (ev.Name == "robot_pose_reset_failed")
                {
                    AddUnique(report.TrialsInvalidBeforeStart, trialId);
                    AddUnique(report.TrialsFailedReset, trialId);
                }
            }

            if (!string.IsNullOrWhiteSpace(trialId) && !string.IsNullOrWhiteSpace(roundId))
            {
                if (trialRounds.TryGetValue(trialId, out string existing) && !string.Equals(existing, roundId, StringComparison.Ordinal))
                {
                    report.IdInconsistencies.Add($"trial_round_changed:{trialId}:{existing}->{roundId}:line{ev.LineNumber}");
                }
                else
                {
                    trialRounds[trialId] = roundId;
                }
            }

            if (ev.Name == "spawn_box_created")
            {
                string boxId = FirstNonEmpty(GetEventString(ev, "box_id"), GetEventString(ev, "boxId"), GetEventString(ev, "object_id"));
                if (string.IsNullOrWhiteSpace(boxId))
                {
                    report.RedFlags.Add($"spawn_box_created_without_reconstructible_id:line{ev.LineNumber}");
                }
            }
        }

        private static void ValidateRoundMetadata(
            EventRecord ev,
            ExperimentDataValidationReport report,
            Dictionary<string, string> roundTrials,
            Dictionary<string, Dictionary<int, string>> conditionRoundSlots,
            Dictionary<string, int> expectedRoundsByCondition)
        {
            string trialId = GetEventString(ev, "trial_id");
            string roundId = GetEventString(ev, "round_id");
            string conditionId = GetEventString(ev, "condition_id");

            if (!string.IsNullOrWhiteSpace(roundId) && !string.IsNullOrWhiteSpace(trialId))
            {
                if (roundTrials.TryGetValue(roundId, out string existingTrial) &&
                    !string.Equals(existingTrial, trialId, StringComparison.Ordinal))
                {
                    report.IdInconsistencies.Add($"round_id_duplicate_across_trials:{roundId}:{existingTrial}->{trialId}:line{ev.LineNumber}");
                }
                else
                {
                    roundTrials[roundId] = trialId;
                }
            }

            bool hasRoundWithin = TryGetEventInt(ev, "round_index_within_condition", out int roundWithin);
            bool hasRoundsPerCondition = TryGetEventInt(ev, "rounds_per_condition", out int roundsPerCondition);
            if (hasRoundWithin && roundWithin < 1)
            {
                report.IdInconsistencies.Add($"round_index_within_condition_out_of_range:line{ev.LineNumber}:value={roundWithin}");
            }

            if (hasRoundsPerCondition && roundsPerCondition < 1)
            {
                report.IdInconsistencies.Add($"rounds_per_condition_out_of_range:line{ev.LineNumber}:value={roundsPerCondition}");
            }

            if (hasRoundWithin && hasRoundsPerCondition && roundWithin > roundsPerCondition)
            {
                report.IdInconsistencies.Add($"round_index_exceeds_rounds_per_condition:line{ev.LineNumber}:round={roundWithin}:rounds={roundsPerCondition}");
            }

            if (!string.IsNullOrWhiteSpace(conditionId) &&
                !string.Equals(conditionId, "uninitialized", StringComparison.Ordinal) &&
                hasRoundWithin &&
                hasRoundsPerCondition &&
                ev.Name == "experiment_trial_started_by_orchestrator")
            {
                if (!conditionRoundSlots.TryGetValue(conditionId, out Dictionary<int, string> slots))
                {
                    slots = new Dictionary<int, string>();
                    conditionRoundSlots[conditionId] = slots;
                }

                string slotOwner = $"{trialId}|{roundId}";
                if (slots.TryGetValue(roundWithin, out string existingOwner) &&
                    !string.Equals(existingOwner, slotOwner, StringComparison.Ordinal))
                {
                    report.IdInconsistencies.Add($"duplicate_condition_round_index:{conditionId}:round={roundWithin}:line{ev.LineNumber}");
                }
                else
                {
                    slots[roundWithin] = slotOwner;
                }

                if (!expectedRoundsByCondition.TryGetValue(conditionId, out int existingExpected))
                {
                    expectedRoundsByCondition[conditionId] = roundsPerCondition;
                }
                else if (existingExpected != roundsPerCondition)
                {
                    report.IdInconsistencies.Add($"rounds_per_condition_changed:{conditionId}:{existingExpected}->{roundsPerCondition}:line{ev.LineNumber}");
                }
            }
        }

        private static void ValidateMetricSufficiency(List<EventRecord> events, ExperimentDataValidationReport report)
        {
            bool hasRobotEnabledCondition = events.Any(e => GetEventBool(e, "robot_enabled") == true);
            AddMetric(report, events.Any(e => e.Name == "experiment_trial_prepare_started") && events.Any(e => e.Name == "experiment_trial_started_by_orchestrator"), "trial_time_derivable", "trial_time_not_derivable");
            AddMetric(report, events.Any(e => e.Name == "round_register_correct_deposit_attempt" || e.Name == "round_register_correct_deposit_rejected"), "classification_counts_derivable", "classification_counts_not_instrumented");
            AddMetric(report, events.Any(e => e.Name.Contains("collision", StringComparison.Ordinal) || e.Name.Contains("fallen", StringComparison.Ordinal) || e.Name.Contains("incident", StringComparison.Ordinal)), "incidents_derivable", "box_fall_or_incident_events_not_instrumented");
            AddMetric(report, events.Any(e => e.Name.Contains("pick", StringComparison.Ordinal) || e.Name.Contains("place", StringComparison.Ordinal) || e.Name.Contains("manipulation", StringComparison.Ordinal)), "manipulation_events_present", "manipulation_events_not_instrumented");
            AddMetric(report, events.Any(e => e.Name.Contains("voice", StringComparison.Ordinal) || e.Name == "autonomy_request_submitted"), "voice_usage_derivable", "voice_usage_not_observed");
            AddMetric(report, !hasRobotEnabledCondition || events.Any(IsEffectiveRobotAssistance) || events.Any(IsAutonomousSelectionEvent), "robot_interventions_derivable", "robot_interventions_not_observed");
            AddMetric(report, events.Any(e => GetEventString(e, "reason").EndsWith("_disabled_by_condition", StringComparison.Ordinal)), "condition_blocks_derivable", "condition_blocks_not_observed");
        }

        private static void ValidateFinalConditionDiagnostics(
            Dictionary<string, object> manifest,
            List<EventRecord> events,
            ExperimentDataValidationReport report)
        {
            ValidateLogContextConditionAlignment(manifest, events, report);
            foreach (string conditionId in FinalConditionIds)
            {
                List<EventRecord> conditionEvents = events
                    .Where(e => string.Equals(GetEventString(e, "condition_id"), conditionId, StringComparison.Ordinal))
                    .ToList();
                if (conditionEvents.Count == 0)
                {
                    continue;
                }

                ValidateFinalConditionFlags(conditionId, conditionEvents, report);
                if (string.Equals(conditionId, "C00_robot_off_voice_off", StringComparison.Ordinal))
                {
                    ValidateC00HasNoAutonomousRobotSelection(conditionEvents, report);
                }
                else
                {
                    ValidateP41SelectionPolicyDiagnostics(conditionId, conditionEvents, report);
                }

                if (string.Equals(conditionId, "C10_robot_on_voice_off", StringComparison.Ordinal))
                {
                    ValidateC10HasNoVoiceIntervention(conditionEvents, report);
                }
            }
        }

        private static void ValidateLogContextConditionAlignment(
            Dictionary<string, object> manifest,
            List<EventRecord> events,
            ExperimentDataValidationReport report)
        {
            if (string.Equals(GetString(manifest, "session_condition_mode", string.Empty), "multi_condition", StringComparison.Ordinal))
            {
                HashSet<string> plan = GetStringSet(manifest, "active_condition_plan");
                foreach (string conditionId in FinalConditionIds)
                {
                    if (!plan.Contains(conditionId))
                    {
                        report.Warnings.Add($"multi_condition_manifest_plan_missing:{conditionId}");
                    }
                }

                return;
            }

            string logContext = GetString(manifest, "log_context", string.Empty);
            if (string.IsNullOrWhiteSpace(logContext))
            {
                return;
            }

            List<string> detectedFinalConditions = events
                .Select(e => GetEventString(e, "condition_id"))
                .Where(condition => FinalConditionIds.Contains(condition))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (detectedFinalConditions.Count == 0)
            {
                return;
            }

            string conditionLogContext = GetString(manifest, "condition_log_context", string.Empty);
            if (!string.IsNullOrWhiteSpace(conditionLogContext) && detectedFinalConditions.Contains(conditionLogContext))
            {
                return;
            }

            if (FinalConditionIds.Contains(logContext) && !detectedFinalConditions.Contains(logContext))
            {
                report.Warnings.Add($"log_context_condition_mismatch_warning:log_context={logContext}:detected={string.Join("|", detectedFinalConditions)}");
                return;
            }

            if (!FinalConditionIds.Contains(logContext))
            {
                report.Warnings.Add($"log_context_condition_mismatch_warning:legacy_log_context={logContext}:detected={string.Join("|", detectedFinalConditions)}");
            }
        }

        private static void ValidateFinalConditionFlags(string conditionId, List<EventRecord> events, ExperimentDataValidationReport report)
        {
            if (!CanonicalConditions.TryGetValue(conditionId, out CanonicalCondition canonical))
            {
                return;
            }

            bool sawRobot = false;
            bool sawVoice = false;
            bool sawAssistance = false;
            foreach (EventRecord ev in events)
            {
                if (GetEventBool(ev, "robot_enabled").HasValue)
                {
                    sawRobot = true;
                }

                if (GetEventBool(ev, "voice_enabled").HasValue)
                {
                    sawVoice = true;
                }

                if (!string.IsNullOrWhiteSpace(GetEventString(ev, "assistance_mode")))
                {
                    sawAssistance = true;
                }
            }

            if (!sawRobot)
            {
                report.Warnings.Add($"condition_diagnostic_missing_robot_enabled:{conditionId}");
            }

            if (!sawVoice)
            {
                report.Warnings.Add($"condition_diagnostic_missing_voice_enabled:{conditionId}");
            }

            if (!sawAssistance)
            {
                report.Warnings.Add($"condition_diagnostic_missing_assistance_mode:{conditionId}");
            }
        }

        private static void ValidateC00HasNoAutonomousRobotSelection(List<EventRecord> events, ExperimentDataValidationReport report)
        {
            foreach (EventRecord ev in events)
            {
                if (IsAutonomousSelectionEvent(ev) || IsRoboticPickPlaceExecutionEvent(ev) || IsEffectiveRobotAssistance(ev))
                {
                    report.RedFlags.Add($"c00_robot_activity_not_allowed:line{ev.LineNumber}:{ev.Name}");
                }
            }
        }

        private static void ValidateC10HasNoVoiceIntervention(List<EventRecord> events, ExperimentDataValidationReport report)
        {
            foreach (EventRecord ev in events)
            {
                if (IsVoiceOperationalEffect(ev) &&
                    !IsVoiceBlockedByCondition(ev))
                {
                    report.RedFlags.Add($"c10_voice_intervention_not_allowed:line{ev.LineNumber}:{ev.Name}");
                }
            }
        }

        private static void ValidateP41SelectionPolicyDiagnostics(string conditionId, List<EventRecord> events, ExperimentDataValidationReport report)
        {
            List<EventRecord> selectionEvents = events.Where(IsAutonomousSelectionEvent).ToList();
            if (selectionEvents.Count == 0)
            {
                return;
            }

            bool sawLocalPickCost = false;
            bool sawRobotToPickupMetric = false;
            foreach (EventRecord ev in selectionEvents)
            {
                string policy = FirstNonEmpty(
                    GetEventString(ev, "policy"),
                    GetEventString(ev, "active_selection_policy"),
                    GetEventString(ev, "runtime_selection_policy"));
                if (!string.IsNullOrWhiteSpace(policy))
                {
                    if (string.Equals(policy, "LocalPickCost", StringComparison.Ordinal))
                    {
                        sawLocalPickCost = true;
                    }
                    else if (policy.Contains("PickPlace", StringComparison.OrdinalIgnoreCase) ||
                        policy.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                        policy.Contains("Place", StringComparison.OrdinalIgnoreCase))
                    {
                        report.RedFlags.Add($"p41_pick_place_benchmark_used_as_runtime_policy:line{ev.LineNumber}:policy={policy}");
                    }
                    else
                    {
                        if (DeclaresRuntimeSelectionPolicy(ev))
                        {
                            report.RedFlags.Add($"p41_runtime_policy_unexpected:line{ev.LineNumber}:policy={policy}");
                        }
                        else
                        {
                            report.Warnings.Add($"p41_selection_policy_unrecognized_legacy:line{ev.LineNumber}:policy={policy}");
                        }
                    }
                }

                string metric = FirstNonEmpty(GetEventString(ev, "selection_metric"), GetEventString(ev, "runtime_metric_name"));
                if (string.Equals(metric, "robot_to_pickup_cost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(metric, "RobotToPickupCost", StringComparison.Ordinal) ||
                    HasField(ev, "robot_to_pickup_cost"))
                {
                    sawRobotToPickupMetric = true;
                }

                bool? benchmarkActive = GetEventBool(ev, "benchmark_pick_place_total_active");
                if (benchmarkActive == true)
                {
                    report.RedFlags.Add($"p41_pick_place_benchmark_marked_active:line{ev.LineNumber}:{ev.Name}");
                }
            }

            if (!sawLocalPickCost)
            {
                report.Warnings.Add($"p41_local_pick_cost_policy_not_observed:{conditionId}");
            }

            if (!sawRobotToPickupMetric)
            {
                report.Warnings.Add($"p41_robot_to_pickup_metric_not_observed:{conditionId}");
            }
        }

        private static void ValidateSessionSummaries(
            ExperimentDataValidationInput input,
            Dictionary<string, object> manifest,
            List<EventRecord> events,
            ExperimentDataValidationReport report)
        {
            var referenced = new List<string>
            {
                GetString(manifest, "session_trials_csv_file", string.Empty),
                GetString(manifest, "session_trials_jsonl_file", string.Empty)
            }.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();

            if (referenced.Count == 0)
            {
                return;
            }

            var summaryTrials = new HashSet<string>(StringComparer.Ordinal);
            foreach (string fileName in referenced)
            {
                string path = ResolveRunFile(input, fileName);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    report.Warnings.Add($"session_summary_referenced_file_missing:{fileName}");
                    continue;
                }

                if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    ReadSummaryCsvTrials(path, summaryTrials, report);
                }
                else if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    ReadSummaryJsonlTrials(path, summaryTrials, report);
                }
            }

            if (summaryTrials.Count == 0)
            {
                report.Warnings.Add("session_summary_present_but_no_trials_read");
                return;
            }

            var detectedTrials = new HashSet<string>(report.TrialsDetected.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal);
            var startedTrials = new HashSet<string>(report.TrialsStarted.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal);
            var missingDetected = detectedTrials.Where(trial => !summaryTrials.Contains(trial)).ToList();
            var missingStarted = startedTrials.Where(trial => !summaryTrials.Contains(trial)).ToList();

            if (missingStarted.Count > 0)
            {
                report.RedFlags.Add($"session_summary_missing_started_trials:{string.Join("|", missingStarted)}");
            }
            else if (missingDetected.Count > 0)
            {
                report.Warnings.Add($"session_summary_partial_detected_trials:{string.Join("|", missingDetected)}");
            }

            if (report.ConditionsDetected.Count >= 4 && summaryTrials.Count < detectedTrials.Count)
            {
                report.Warnings.Add($"session_summary_partial_2x2_coverage:summary_trials={summaryTrials.Count}:detected_trials={detectedTrials.Count}");
            }
        }

        private static void AddMetric(ExperimentDataValidationReport report, bool ok, string pass, string warning)
        {
            if (ok)
            {
                report.MetricSufficiencyPasses.Add(pass);
            }
            else
            {
                report.MetricSufficiencyWarnings.Add(warning);
            }
        }

        private static void FinalizeStatus(ExperimentDataValidationReport report)
        {
            if (report.RedFlags.Count > 0 || report.IdInconsistencies.Count > 0 || report.ConditionFlagInconsistencies.Count > 0)
            {
                report.Status = ExperimentDataValidationStatus.Fail;
            }
            else if (report.Warnings.Count > 0 || report.MissingFields.Count > 0 || report.OutOfOrderEvents.Count > 0 || report.MetricSufficiencyWarnings.Count > 0)
            {
                report.Status = ExperimentDataValidationStatus.Warn;
            }
            else
            {
                report.Status = ExperimentDataValidationStatus.Pass;
            }
        }

        private static void ExportReports(ExperimentDataValidationInput input, ExperimentDataValidationReport report)
        {
            string outputRoot = string.IsNullOrWhiteSpace(input.OutputDirectory)
                ? Path.Combine(Directory.GetCurrentDirectory(), "Logs", "ExperimentDataValidation")
                : input.OutputDirectory;
            Directory.CreateDirectory(outputRoot);
            string token = Sanitize(string.Equals(report.RunId, "unknown", StringComparison.Ordinal) ? DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) : report.RunId);
            report.MarkdownPath = Path.Combine(outputRoot, $"{token}__experiment_data_validation.md");
            report.JsonPath = Path.Combine(outputRoot, $"{token}__experiment_data_validation.json");
            File.WriteAllText(report.MarkdownPath, ToMarkdown(report), Encoding.UTF8);
            File.WriteAllText(report.JsonPath, ToJson(report), Encoding.UTF8);
        }

        public static string ToMarkdown(ExperimentDataValidationReport report)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# Experiment Data Consistency Validation");
            builder.AppendLine();
            builder.AppendLine($"- status: {report.Status}");
            builder.AppendLine($"- run_id: {report.RunId}");
            builder.AppendLine($"- scene: {report.Scene}");
            builder.AppendLine($"- events: {report.EventCount}");
            builder.AppendLine($"- samples: {report.SampleCount}");
            builder.AppendLine($"- session_detected: {report.SessionDetected}");
            AppendList(builder, "trials_detected", report.TrialsDetected);
            AppendList(builder, "trials_started", report.TrialsStarted);
            AppendList(builder, "trials_failed_sanity", report.TrialsFailedSanity);
            AppendList(builder, "trials_completed", report.TrialsCompleted);
            AppendList(builder, "trials_aborted", report.TrialsAborted);
            AppendList(builder, "trials_invalid_before_start", report.TrialsInvalidBeforeStart);
            AppendList(builder, "trials_failed_reset", report.TrialsFailedReset);
            AppendList(builder, "trials_valid_for_analysis", report.TrialsValidForAnalysis);
            AppendList(builder, "conditions_detected", report.ConditionsDetected);
            AppendList(builder, "conditions_started", report.ConditionsStarted);
            AppendList(builder, "warnings", report.Warnings);
            AppendList(builder, "red_flags", report.RedFlags);
            AppendList(builder, "missing_fields", report.MissingFields);
            AppendList(builder, "out_of_order", report.OutOfOrderEvents);
            AppendList(builder, "id_inconsistencies", report.IdInconsistencies);
            AppendList(builder, "condition_flag_inconsistencies", report.ConditionFlagInconsistencies);
            AppendList(builder, "metric_sufficiency_passes", report.MetricSufficiencyPasses);
            AppendList(builder, "metric_sufficiency_warnings", report.MetricSufficiencyWarnings);
            return builder.ToString();
        }

        public static string ToJson(ExperimentDataValidationReport report)
        {
            var payload = new Dictionary<string, object>
            {
                ["status"] = report.Status.ToString(),
                ["run_id"] = report.RunId,
                ["scene"] = report.Scene,
                ["event_count"] = report.EventCount,
                ["sample_count"] = report.SampleCount,
                ["session_detected"] = report.SessionDetected,
                ["trials_detected"] = report.TrialsDetected,
                ["trials_started"] = report.TrialsStarted,
                ["trials_failed_sanity"] = report.TrialsFailedSanity,
                ["trials_completed"] = report.TrialsCompleted,
                ["trials_aborted"] = report.TrialsAborted,
                ["trials_invalid_before_start"] = report.TrialsInvalidBeforeStart,
                ["trials_failed_reset"] = report.TrialsFailedReset,
                ["trials_valid_for_analysis"] = report.TrialsValidForAnalysis,
                ["conditions_detected"] = report.ConditionsDetected,
                ["conditions_started"] = report.ConditionsStarted,
                ["warnings"] = report.Warnings,
                ["red_flags"] = report.RedFlags,
                ["missing_fields"] = report.MissingFields,
                ["out_of_order_events"] = report.OutOfOrderEvents,
                ["id_inconsistencies"] = report.IdInconsistencies,
                ["condition_flag_inconsistencies"] = report.ConditionFlagInconsistencies,
                ["metric_sufficiency_passes"] = report.MetricSufficiencyPasses,
                ["metric_sufficiency_warnings"] = report.MetricSufficiencyWarnings
            };
            return MiniJsonWriter.Write(payload);
        }

        private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
        {
            builder.AppendLine();
            builder.AppendLine($"## {title}");
            if (values.Count == 0)
            {
                builder.AppendLine("- none");
                return;
            }

            foreach (string value in values)
            {
                builder.AppendLine($"- {value}");
            }
        }

        private static void RequireManifestField(Dictionary<string, object> manifest, string field, ExperimentDataValidationReport report)
        {
            if (!manifest.ContainsKey(field) || manifest[field] == null || string.IsNullOrWhiteSpace(Convert.ToString(manifest[field], CultureInfo.InvariantCulture)))
            {
                report.MissingFields.Add($"manifest.{field}");
            }
        }

        private static void CheckReferencedFile(ExperimentDataValidationInput input, Dictionary<string, object> manifest, string field, ExperimentDataValidationReport report)
        {
            string file = GetString(manifest, field, string.Empty);
            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(input.RunDirectory))
            {
                return;
            }

            string path = Path.IsPathRooted(file) ? file : Path.Combine(input.RunDirectory, file);
            if (!File.Exists(path))
            {
                report.RedFlags.Add($"manifest_referenced_file_missing:{field}:{file}");
            }
        }

        private static bool HasStructuredFields(Dictionary<string, object> root)
        {
            return root.Count > 4;
        }

        private static bool TryGetTimestamp(Dictionary<string, object> root, out double timestamp)
        {
            foreach (string field in new[] { "timestamp_unity", "run_elapsed_time", "timestamp", "time", "frame_index" })
            {
                if (root.TryGetValue(field, out object value) && TryParseDouble(value, out timestamp))
                {
                    return true;
                }
            }

            timestamp = double.NaN;
            return false;
        }

        private static bool HasField(EventRecord ev, string field)
        {
            return ev.Payload.ContainsKey(field) || ev.Root.ContainsKey(field);
        }

        private static string GetEventString(EventRecord ev, string field)
        {
            if (ev.Payload.TryGetValue(field, out object payloadValue))
            {
                return Convert.ToString(payloadValue, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            return GetString(ev.Root, field, string.Empty);
        }

        private static bool? GetEventBool(EventRecord ev, string field)
        {
            object value = null;
            if (!ev.Payload.TryGetValue(field, out value))
            {
                ev.Root.TryGetValue(field, out value);
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value != null && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out bool parsed))
            {
                return parsed;
            }

            return null;
        }

        private static bool TryGetEventInt(EventRecord ev, string field, out int result)
        {
            object value = null;
            if (!ev.Payload.TryGetValue(field, out value))
            {
                ev.Root.TryGetValue(field, out value);
            }

            if (value is int intValue)
            {
                result = intValue;
                return true;
            }

            if (value is long longValue && longValue <= int.MaxValue && longValue >= int.MinValue)
            {
                result = (int)longValue;
                return true;
            }

            if (value != null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                result = parsed;
                return true;
            }

            result = 0;
            return false;
        }

        private static bool TryGetEventDouble(EventRecord ev, string field, out double result)
        {
            if (ev.Payload.TryGetValue(field, out object payloadValue) && TryParseDouble(payloadValue, out result))
            {
                return true;
            }

            if (ev.Root.TryGetValue(field, out object rootValue) && TryParseDouble(rootValue, out result))
            {
                return true;
            }

            result = double.NaN;
            return false;
        }

        private static bool TryGetEventVectorXZ(EventRecord ev, string field, out double x, out double z)
        {
            object value = null;
            if (!ev.Payload.TryGetValue(field, out value))
            {
                ev.Root.TryGetValue(field, out value);
            }

            if (value is Dictionary<string, object> vector)
            {
                bool hasX = TryGetDictionaryDouble(vector, "x", out x) || TryGetDictionaryDouble(vector, "X", out x);
                bool hasZ = TryGetDictionaryDouble(vector, "z", out z) || TryGetDictionaryDouble(vector, "Z", out z);
                if (hasX && hasZ)
                {
                    return true;
                }
            }

            x = double.NaN;
            z = double.NaN;
            return false;
        }

        private static bool TryGetDictionaryDouble(Dictionary<string, object> dictionary, string key, out double result)
        {
            if (dictionary.TryGetValue(key, out object value))
            {
                return TryParseDouble(value, out result);
            }

            result = double.NaN;
            return false;
        }

        private static double MathDeltaAngle(double current, double target)
        {
            double delta = Repeat(target - current, 360.0);
            if (delta > 180.0)
            {
                delta -= 360.0;
            }

            return delta;
        }

        private static double Repeat(double value, double length)
        {
            return value - Math.Floor(value / length) * length;
        }

        private static void CompareBool(EventRecord ev, string field, bool expected, ExperimentDataValidationReport report)
        {
            bool? actual = GetEventBool(ev, field);
            if (actual.HasValue && actual.Value != expected)
            {
                report.ConditionFlagInconsistencies.Add($"line{ev.LineNumber}:{GetEventString(ev, "condition_id")}.{field}={actual.Value}");
            }
        }

        private static bool EventLooksExecutableVoice(EventRecord ev)
        {
            if (IsVoiceBlockedByCondition(ev))
            {
                return false;
            }

            string status = FirstNonEmpty(GetEventString(ev, "status"), GetEventString(ev, "routing_status"), GetEventString(ev, "result"));
            return string.Equals(status, "accepted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "allowed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "submitted", StringComparison.OrdinalIgnoreCase) ||
                GetEventBool(ev, "bridge_invoked") == true ||
                ev.Name == "autonomy_request_submitted";
        }

        private static bool IsVoiceOperationalEffect(EventRecord ev)
        {
            if (!IsVoiceOrigin(ev))
            {
                return false;
            }

            if (GetEventBool(ev, "bridge_invoked") == true ||
                GetEventBool(ev, "submitted") == true ||
                GetEventBool(ev, "promoted_pending") == true ||
                GetEventBool(ev, "selection_modified") == true ||
                GetEventBool(ev, "blackboard_modified") == true)
            {
                return true;
            }

            string status = FirstNonEmpty(GetEventString(ev, "status"), GetEventString(ev, "routing_status"), GetEventString(ev, "result"));
            if (string.Equals(status, "accepted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "allowed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "submitted", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return ev.Name == "voice_command_execution" ||
                ev.Name == "voice_route_result" ||
                ev.Name == "autonomy_request_submitted" ||
                ev.Name.Contains("pending_promoted", StringComparison.OrdinalIgnoreCase) ||
                ev.Name.Contains("voice_pre_pick_replacement_accepted", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVoiceOrigin(EventRecord ev)
        {
            if (ev.Name.Contains("voice", StringComparison.OrdinalIgnoreCase) ||
                ev.Name.Contains("transcript", StringComparison.OrdinalIgnoreCase) ||
                ev.Name.Contains("asr", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string[] originFields =
            {
                GetEventString(ev, "intent_source"),
                GetEventString(ev, "source"),
                GetEventString(ev, "input_source"),
                GetEventString(ev, "origin"),
                GetEventString(ev, "connector"),
                GetEventString(ev, "component"),
                GetEventString(ev, "route"),
                GetEventString(ev, "request_source")
            };

            if (originFields.Any(IsVoiceOriginToken))
            {
                return true;
            }

            return false;
        }

        private static bool IsVoiceOriginToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.IndexOf("voice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("manual_transcript", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("manual transcript", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("transcript", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("VoiceAutonomyCommandConnector", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("voice_route", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("asr", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsVoiceBlockedByCondition(EventRecord ev)
        {
            string reason = GetEventString(ev, "reason");
            string status = FirstNonEmpty(GetEventString(ev, "status"), GetEventString(ev, "routing_status"), GetEventString(ev, "result"));
            return string.Equals(reason, "voice_disabled_by_condition", StringComparison.Ordinal) ||
                ev.Name == "voice_command_blocked_by_condition" ||
                ev.Name == "voice_command_rejected" ||
                (ev.Name == "voice_route_result" && string.Equals(reason, "voice_disabled_by_condition", StringComparison.Ordinal)) ||
                string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "not_executable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "suppressed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUninitializedContextForbidden(EventRecord ev, SemanticState state)
        {
            return state.TrialActive ||
                state.RoundActive ||
                state.ConditionApplied ||
                ev.Name == "experiment_condition_applied" ||
                ev.Name.Contains("spawn", StringComparison.Ordinal) ||
                ev.Name == "autonomy_request_submitted" ||
                IsEffectiveRobotAssistance(ev) ||
                EventLooksLikeManipulation(ev) ||
                (ev.Name.Contains("voice", StringComparison.Ordinal) && EventLooksExecutableVoice(ev));
        }

        private static bool IsEffectiveRobotAssistance(EventRecord ev)
        {
            if (IsAssistanceCleanupOrReset(ev) || IsRobotAssistanceBlocked(ev))
            {
                return false;
            }

            return ev.Name == "autonomy_request_submitted" ||
                ev.Name == "assisted_task_submitted" ||
                ev.Name == "assisted_box_assigned" ||
                ev.Name == "assisted_box_selected" ||
                ev.Name.Contains("assisted_selection_assigned", StringComparison.Ordinal) ||
                ev.Name.Contains("assisted_selection_selected", StringComparison.Ordinal) ||
                ev.Name.Contains("assisted_selection_suggested", StringComparison.Ordinal) ||
                ev.Name.Contains("assisted_target", StringComparison.Ordinal) ||
                ev.Name.Contains("assisted_pick_started", StringComparison.Ordinal) ||
                ev.Name.Contains("assisted_place_started", StringComparison.Ordinal) ||
                ev.Name.Contains("robot_intervention", StringComparison.Ordinal);
        }

        private static bool IsAutonomousSelectionEvent(EventRecord ev)
        {
            return ev.Name == "autonomous_selection_policy_evaluated" ||
                ev.Name == "autonomous_selection_candidate_scored" ||
                ev.Name == "autonomous_selection_candidate_selected" ||
                ev.Name == "assisted_selection_candidates_evaluated" ||
                ev.Name == "p40_trace_assisted_selection_decision" ||
                ev.Name.Contains("assisted_selection_assigned", StringComparison.Ordinal) ||
                ev.Name.Contains("assisted_selection_selected", StringComparison.Ordinal) ||
                ev.Name.Contains("assisted_selection_suggested", StringComparison.Ordinal);
        }

        private static bool DeclaresRuntimeSelectionPolicy(EventRecord ev)
        {
            return HasField(ev, "active_selection_policy") ||
                HasField(ev, "runtime_selection_policy") ||
                ev.Name == "autonomous_selection_policy_evaluated";
        }

        private static bool IsRoboticPickPlaceExecutionEvent(EventRecord ev)
        {
            return ev.Name == "autonomy_request_submitted" ||
                ev.Name == "assisted_task_submitted" ||
                ev.Name == "robot_controller_task_start_requested" ||
                ev.Name == "bt_pick_place_started" ||
                ev.Name == "navigation_to_pick_started" ||
                ev.Name == "navigation_to_place_started" ||
                ev.Name == "assisted_pick_started" ||
                ev.Name == "assisted_place_started";
        }

        private static bool IsRobotAssistanceBlocked(EventRecord ev)
        {
            return ev.Name == "robot_assistance_blocked_by_condition" ||
                ev.Name == "assisted_selection_blocked_detailed" ||
                ev.Name.Contains("blocked", StringComparison.Ordinal) ||
                string.Equals(GetEventString(ev, "reason"), "robot_disabled_by_condition", StringComparison.Ordinal);
        }

        private static bool IsAssistanceCleanupOrReset(EventRecord ev)
        {
            string reason = GetEventString(ev, "reason");
            return ev.Name == "assisted_round_state_cleared" &&
                (string.Equals(reason, "experiment_trial_reset", StringComparison.Ordinal) ||
                 string.Equals(reason, "round_reset", StringComparison.Ordinal) ||
                 string.Equals(reason, "round_started", StringComparison.Ordinal) ||
                 string.IsNullOrWhiteSpace(reason));
        }

        private static bool EventLooksLikeManipulation(EventRecord ev)
        {
            return ev.Name.Contains("pick", StringComparison.Ordinal) ||
                ev.Name.Contains("place", StringComparison.Ordinal) ||
                ev.Name.Contains("manipulation", StringComparison.Ordinal) ||
                ev.Name.Contains("deposit", StringComparison.Ordinal);
        }

        private static bool IsTrialClosingEvent(EventRecord ev)
        {
            return ev.Name == "experiment_trial_completed_by_orchestrator" ||
                ev.Name == "experiment_trial_aborted" ||
                ev.Name == "experiment_trial_marked_invalid";
        }

        private static bool IsPostTrialOperationalActivity(EventRecord ev)
        {
            if (IsTrialClosingEvent(ev) ||
                IsAssistanceCleanupOrReset(ev) ||
                IsTerminalLegacyTimestampReset(ev.Name) ||
                ev.Name.Contains("reset", StringComparison.Ordinal) ||
                ev.Name.Contains("cleanup", StringComparison.Ordinal) ||
                ev.Name.Contains("summary", StringComparison.Ordinal))
            {
                return false;
            }

            return ev.Name == "assisted_box_assigned" ||
                ev.Name == "assisted_box_selected" ||
                ev.Name == "autonomy_request_received_by_adapter" ||
                ev.Name == "autonomy_request_submitted" ||
                ev.Name == "assisted_task_submitted" ||
                ev.Name == "bt_pick_place_started" ||
                ev.Name == "navigation_to_pick_started" ||
                IsEffectiveRobotAssistance(ev) ||
                EventLooksLikeManipulation(ev);
        }

        private static bool IsPostResetFailureOperationalActivity(EventRecord ev)
        {
            if (ev.Name == "robot_pose_reset_failed" ||
                ev.Name == "experiment_prepared_trial_invalid_summary_written" ||
                ev.Name == "experiment_trial_prepare_completed" ||
                ev.Name.Contains("cleanup", StringComparison.Ordinal) ||
                ev.Name.Contains("summary", StringComparison.Ordinal) ||
                ev.Name.Contains("state_cleared", StringComparison.Ordinal))
            {
                return false;
            }

            return ev.Name == "autonomy_request_submitted" ||
                ev.Name == "assisted_task_submitted" ||
                ev.Name == "bt_pick_place_started" ||
                ev.Name == "navigation_to_pick_started" ||
                ev.Name == "assisted_box_assigned" ||
                ev.Name == "assisted_box_selected" ||
                ev.Name == "robot_controller_task_start_requested" ||
                ev.Name == "autonomy_request_accepted_by_adapter" ||
                IsEffectiveRobotAssistance(ev) ||
                EventLooksLikeManipulation(ev);
        }

        private static bool TryFindStaleRoundTarget(EventRecord ev, string roundId, out string targetId, out string expectedRound)
        {
            targetId = FirstNonEmpty(
                GetEventString(ev, "pickup_target_id"),
                GetEventString(ev, "requested_target_id"),
                GetEventString(ev, "target_id"),
                GetEventString(ev, "box_id"),
                GetEventString(ev, "object_id"));
            expectedRound = ExtractRoundToken(roundId);
            if (string.IsNullOrWhiteSpace(targetId) || string.IsNullOrWhiteSpace(expectedRound))
            {
                return false;
            }

            string targetRound = ExtractRoundToken(targetId);
            return !string.IsNullOrWhiteSpace(targetRound) &&
                !string.Equals(targetRound, expectedRound, StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractRoundToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            int index = value.IndexOf("round", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return string.Empty;
            }

            int cursor = index + "round".Length;
            var digits = new StringBuilder();
            while (cursor < value.Length && char.IsDigit(value[cursor]))
            {
                digits.Append(value[cursor]);
                cursor++;
            }

            return digits.Length == 0 ? string.Empty : $"round{digits}";
        }

        private static bool IsValidForAnalysis(EventRecord ev)
        {
            bool? valid = GetEventBool(ev, "valid_for_analysis");
            if (valid.HasValue)
            {
                return valid.Value;
            }

            string reason = GetEventString(ev, "reason");
            return !reason.Contains("invalid", StringComparison.OrdinalIgnoreCase) &&
                !reason.Contains("aborted", StringComparison.OrdinalIgnoreCase);
        }

        private static void TrackResetDuplicate(EventRecord ev, ExperimentDataValidationReport report, HashSet<string> resetEvents)
        {
            if (ev.Name != "round_reset" && ev.Name != "assisted_round_state_cleared")
            {
                return;
            }

            string trialId = GetEventString(ev, "trial_id");
            string roundId = GetEventString(ev, "round_id");
            string timestamp = double.IsNaN(ev.Timestamp) ? "unknown" : ev.Timestamp.ToString("G9", CultureInfo.InvariantCulture);
            string normalizedName = ev.Name == "round_reset" ? "reset" : "reset";
            string key = $"{normalizedName}|{trialId}|{roundId}|{timestamp}";
            if (!resetEvents.Add(key))
            {
                report.Warnings.Add($"idempotent_duplicate_reset_event:line{ev.LineNumber}:trial={trialId}:round={roundId}:timestamp={timestamp}");
            }
        }

        private static void TrackConditionBlockFrequency(EventRecord ev, Dictionary<string, int> conditionBlockCounts)
        {
            string reason = GetEventString(ev, "reason");
            if (!string.Equals(reason, "robot_disabled_by_condition", StringComparison.Ordinal) &&
                ev.Name != "robot_assistance_blocked_by_condition" &&
                ev.Name != "assisted_selection_blocked_detailed")
            {
                return;
            }

            string key = FirstNonEmpty(GetEventString(ev, "condition_id"), "unknown_condition");
            conditionBlockCounts.TryGetValue(key, out int count);
            conditionBlockCounts[key] = count + 1;
        }

        private static bool IsTerminalLegacyTimestampReset(string name)
        {
            return name == "final_sample_skipped" || name == "run_finished";
        }

        private static void AddWarningOnce(ExperimentDataValidationReport report, string warning)
        {
            if (!report.Warnings.Contains(warning))
            {
                report.Warnings.Add(warning);
            }
        }

        private static void AddUnique(List<string> values, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value))
            {
                values.Add(value);
            }
        }

        private static string ResolveRunFile(ExperimentDataValidationInput input, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(fileName))
            {
                return fileName;
            }

            return string.IsNullOrWhiteSpace(input.RunDirectory)
                ? fileName
                : Path.Combine(input.RunDirectory, fileName);
        }

        private static void ReadSummaryCsvTrials(string path, HashSet<string> summaryTrials, ExperimentDataValidationReport report)
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0)
            {
                report.Warnings.Add($"session_summary_csv_empty:{Path.GetFileName(path)}");
                return;
            }

            string[] header = ParseCsvLine(lines[0]);
            int trialIdIndex = Array.FindIndex(header, value => string.Equals(value, "trial_id", StringComparison.OrdinalIgnoreCase));
            if (trialIdIndex < 0)
            {
                report.Warnings.Add($"session_summary_csv_missing_trial_id:{Path.GetFileName(path)}");
                return;
            }

            ValidateSessionSummaryHeader(path, header, report);

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] row = ParseCsvLine(lines[i]);
                if (trialIdIndex < row.Length && !string.IsNullOrWhiteSpace(row[trialIdIndex]))
                {
                    summaryTrials.Add(row[trialIdIndex]);
                }
            }
        }

        private static void ReadSummaryJsonlTrials(string path, HashSet<string> summaryTrials, ExperimentDataValidationReport report)
        {
            int lineNumber = 0;
            foreach (string line in File.ReadLines(path))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    Dictionary<string, object> root = MiniJsonParser.ParseObject(line);
                    string trialId = FirstString(root, "trial_id", "trialId");
                    Dictionary<string, object> payload = GetDictionary(root, "payload");
                    if (string.IsNullOrWhiteSpace(trialId) && payload != null)
                    {
                        trialId = FirstString(payload, "trial_id", "trialId");
                    }

                    if (!string.IsNullOrWhiteSpace(trialId))
                    {
                        summaryTrials.Add(trialId);
                    }

                    ValidateSessionSummaryJsonlFields(path, lineNumber, root, payload, report);
                }
                catch (Exception ex)
                {
                    report.Warnings.Add($"session_summary_jsonl_corrupt:{Path.GetFileName(path)}:line{lineNumber}:{ex.Message}");
                }
            }
        }

        private static void ValidateSessionSummaryHeader(string path, string[] header, ExperimentDataValidationReport report)
        {
            string[] required =
            {
                "condition_id",
                "condition_name",
                "robot_enabled",
                "voice_enabled",
                "assistance_mode",
                "run_id",
                "session_id",
                "participant_id",
                "trial_index",
                "round_index",
                "valid_for_analysis",
                "exclusion_reason"
            };

            foreach (string field in required)
            {
                if (!header.Any(value => string.Equals(value, field, StringComparison.OrdinalIgnoreCase)))
                {
                    report.Warnings.Add($"session_summary_csv_missing_diagnostic_column:{Path.GetFileName(path)}:{field}");
                }
            }
        }

        private static void ValidateSessionSummaryJsonlFields(
            string path,
            int lineNumber,
            Dictionary<string, object> root,
            Dictionary<string, object> payload,
            ExperimentDataValidationReport report)
        {
            string[] required =
            {
                "condition_id",
                "condition_name",
                "robot_enabled",
                "voice_enabled",
                "assistance_mode",
                "run_id",
                "session_id",
                "participant_id",
                "trial_index",
                "round_index",
                "valid_for_analysis"
            };

            foreach (string field in required)
            {
                bool hasRoot = root != null && root.ContainsKey(field);
                bool hasPayload = payload != null && payload.ContainsKey(field);
                if (!hasRoot && !hasPayload)
                {
                    report.Warnings.Add($"session_summary_jsonl_missing_diagnostic_field:{Path.GetFileName(path)}:line{lineNumber}:{field}");
                }
            }
        }

        private static string FirstExisting(string current, string directory, string pattern)
        {
            if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
            {
                return current;
            }

            return Directory.GetFiles(directory, pattern).OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty;
        }

        private static string ResolveCommonDirectory(params string[] paths)
        {
            return paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetDirectoryName).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? string.Empty;
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> root, string key)
        {
            return root.TryGetValue(key, out object value) ? value as Dictionary<string, object> : null;
        }

        private static string FirstString(Dictionary<string, object> root, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = GetString(root, key, string.Empty);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static string GetString(Dictionary<string, object> dictionary, string key, string fallback)
        {
            if (dictionary.TryGetValue(key, out object value) && value != null)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
            }

            return fallback;
        }

        private static HashSet<string> GetStringSet(Dictionary<string, object> dictionary, string key)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);
            if (dictionary == null || !dictionary.TryGetValue(key, out object raw) || raw == null)
            {
                return values;
            }

            if (raw is IEnumerable<object> objects)
            {
                foreach (object item in objects)
                {
                    string value = Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        values.Add(value);
                    }
                }

                return values;
            }

            string text = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
            foreach (string part in text.Split(new[] { '|', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                values.Add(part.Trim());
            }

            return values;
        }

        private static bool TryParseDouble(object value, out double result)
        {
            return TryParseDouble(Convert.ToString(value, CultureInfo.InvariantCulture), out result);
        }

        private static bool TryParseDouble(string value, out double result)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static string[] ParseCsvLine(string line)
        {
            var values = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (c == ',' && !quoted)
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
            return values.ToArray();
        }

        private static string Sanitize(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(value) ? "run" : value;
        }

        private readonly struct CanonicalCondition
        {
            public CanonicalCondition(bool robotEnabled, bool voiceEnabled, string assistanceMode)
            {
                RobotEnabled = robotEnabled;
                VoiceEnabled = voiceEnabled;
                AssistanceMode = assistanceMode;
            }

            public bool RobotEnabled { get; }
            public bool VoiceEnabled { get; }
            public string AssistanceMode { get; }
        }

        private readonly struct SemanticState
        {
            public SemanticState(bool sessionActive, bool trialActive, bool roundActive, bool conditionApplied)
            {
                SessionActive = sessionActive;
                TrialActive = trialActive;
                RoundActive = roundActive;
                ConditionApplied = conditionApplied;
            }

            public bool SessionActive { get; }
            public bool TrialActive { get; }
            public bool RoundActive { get; }
            public bool ConditionApplied { get; }
        }

        private sealed class EventRecord
        {
            public EventRecord(int lineNumber, string name, double timestamp, Dictionary<string, object> root, Dictionary<string, object> payload)
            {
                LineNumber = lineNumber;
                Name = name ?? string.Empty;
                Timestamp = timestamp;
                Root = root;
                Payload = payload;
            }

            public int LineNumber { get; }
            public string Name { get; }
            public double Timestamp { get; }
            public Dictionary<string, object> Root { get; }
            public Dictionary<string, object> Payload { get; }
        }

        private readonly struct RobotPoseSample
        {
            public RobotPoseSample(int rowNumber, double runElapsedTime, double robotX, double robotZ, double robotYawDegrees)
            {
                RowNumber = rowNumber;
                RunElapsedTime = runElapsedTime;
                RobotX = robotX;
                RobotZ = robotZ;
                RobotYawDegrees = robotYawDegrees;
            }

            public int RowNumber { get; }
            public double RunElapsedTime { get; }
            public double RobotX { get; }
            public double RobotZ { get; }
            public double RobotYawDegrees { get; }
        }

        private sealed class MiniJsonParser
        {
            private readonly string _text;
            private int _index;

            private MiniJsonParser(string text)
            {
                _text = text ?? string.Empty;
            }

            public static Dictionary<string, object> ParseObject(string text)
            {
                var parser = new MiniJsonParser(text);
                object value = parser.ParseValue();
                return value as Dictionary<string, object> ?? throw new FormatException("root_not_object");
            }

            private object ParseValue()
            {
                SkipWhite();
                if (_index >= _text.Length)
                {
                    throw new FormatException("unexpected_end");
                }

                char c = _text[_index];
                if (c == '{') return ParseObjectValue();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == 't' || c == 'f') return ParseBool();
                if (c == 'n')
                {
                    Expect("null");
                    return null;
                }

                return ParseNumber();
            }

            private Dictionary<string, object> ParseObjectValue()
            {
                var dictionary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                _index++;
                SkipWhite();
                if (Peek('}'))
                {
                    _index++;
                    return dictionary;
                }

                while (true)
                {
                    string key = ParseString();
                    SkipWhite();
                    Expect(":");
                    dictionary[key] = ParseValue();
                    SkipWhite();
                    if (Peek('}'))
                    {
                        _index++;
                        return dictionary;
                    }

                    Expect(",");
                    SkipWhite();
                }
            }

            private List<object> ParseArray()
            {
                var values = new List<object>();
                _index++;
                SkipWhite();
                if (Peek(']'))
                {
                    _index++;
                    return values;
                }

                while (true)
                {
                    values.Add(ParseValue());
                    SkipWhite();
                    if (Peek(']'))
                    {
                        _index++;
                        return values;
                    }

                    Expect(",");
                }
            }

            private string ParseString()
            {
                Expect("\"");
                var builder = new StringBuilder();
                while (_index < _text.Length)
                {
                    char c = _text[_index++];
                    if (c == '"')
                    {
                        return builder.ToString();
                    }

                    if (c == '\\')
                    {
                        if (_index >= _text.Length) throw new FormatException("bad_escape");
                        char escaped = _text[_index++];
                        if (escaped == 'n') builder.Append('\n');
                        else if (escaped == 'r') builder.Append('\r');
                        else if (escaped == 't') builder.Append('\t');
                        else if (escaped == 'b') builder.Append('\b');
                        else if (escaped == 'f') builder.Append('\f');
                        else if (escaped == 'u')
                        {
                            string hex = _text.Substring(_index, 4);
                            builder.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _index += 4;
                        }
                        else builder.Append(escaped);
                    }
                    else
                    {
                        builder.Append(c);
                    }
                }

                throw new FormatException("unterminated_string");
            }

            private bool ParseBool()
            {
                if (_text.Substring(_index).StartsWith("true", StringComparison.Ordinal))
                {
                    _index += 4;
                    return true;
                }

                Expect("false");
                return false;
            }

            private object ParseNumber()
            {
                int start = _index;
                while (_index < _text.Length && "-+0123456789.eE".IndexOf(_text[_index]) >= 0)
                {
                    _index++;
                }

                string token = _text.Substring(start, _index - start);
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    return value;
                }

                throw new FormatException($"bad_number:{token}");
            }

            private void SkipWhite()
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                {
                    _index++;
                }
            }

            private bool Peek(char c)
            {
                return _index < _text.Length && _text[_index] == c;
            }

            private void Expect(string token)
            {
                if (!_text.Substring(_index).StartsWith(token, StringComparison.Ordinal))
                {
                    throw new FormatException($"expected:{token}");
                }

                _index += token.Length;
            }
        }

        private static class MiniJsonWriter
        {
            public static string Write(object value)
            {
                if (value == null) return "null";
                if (value is string text) return "\"" + Escape(text) + "\"";
                if (value is bool boolean) return boolean ? "true" : "false";
                if (value is int || value is long || value is float || value is double || value is decimal)
                {
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
                }

                if (value is IEnumerable<string> strings)
                {
                    return "[" + string.Join(",", strings.Select(Write)) + "]";
                }

                if (value is Dictionary<string, object> dictionary)
                {
                    return "{" + string.Join(",", dictionary.Select(pair => Write(pair.Key) + ":" + Write(pair.Value))) + "}";
                }

                return Write(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            private static string Escape(string value)
            {
                return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
            }
        }
    }
}
