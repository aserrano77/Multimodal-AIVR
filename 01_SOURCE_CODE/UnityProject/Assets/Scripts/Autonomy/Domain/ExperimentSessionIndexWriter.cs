using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Autonomy.Domain
{
    public sealed class ExperimentSessionIndexWriteResult
    {
        public bool Success;
        public bool DuplicateTrialIdDetected;
        public string OriginalTrialId;
        public string WrittenTrialId;
        public string CsvPath;
        public string JsonlPath;
        public string CsvFileName;
        public string JsonlFileName;
        public string ErrorMessage;
    }

    public static class ExperimentSessionIndexWriter
    {
        public const string LegacyCsvFileName = "session_trials.csv";
        public const string LegacyJsonlFileName = "session_trials.jsonl";

        public static ExperimentSessionIndexWriteResult Append(string directory, ExperimentSessionTrialRecord record)
        {
            var result = new ExperimentSessionIndexWriteResult
            {
                Success = false,
                OriginalTrialId = record != null ? record.TrialId : string.Empty,
                WrittenTrialId = record != null ? record.TrialId : string.Empty
            };

            try
            {
                if (record == null)
                {
                    result.ErrorMessage = "record_null";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(directory))
                {
                    result.ErrorMessage = "directory_missing";
                    return result;
                }

                Directory.CreateDirectory(directory);
                string csvFileName = BuildCsvFileName(record);
                string jsonlFileName = BuildJsonlFileName(record);
                string csvPath = Path.Combine(directory, csvFileName);
                string jsonlPath = Path.Combine(directory, jsonlFileName);
                result.CsvPath = csvPath;
                result.JsonlPath = jsonlPath;
                result.CsvFileName = csvFileName;
                result.JsonlFileName = jsonlFileName;

                string originalTrialId = record.TrialId;
                if (ContainsTrial(csvPath, record.SessionId, record.TrialId))
                {
                    result.DuplicateTrialIdDetected = true;
                    record.TrialId = ResolveDuplicateTrialId(csvPath, record.SessionId, record.TrialId);
                    result.WrittenTrialId = record.TrialId;
                }

                bool writeHeader = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
                using (StreamWriter writer = new StreamWriter(csvPath, append: true, Encoding.UTF8))
                {
                    if (writeHeader)
                    {
                        writer.WriteLine(GetCsvHeader());
                    }

                    writer.WriteLine(ToCsvRow(record));
                }

                File.AppendAllText(jsonlPath, ToJson(record) + Environment.NewLine, Encoding.UTF8);
                result.OriginalTrialId = originalTrialId;
                result.Success = true;
                return result;
            }
            catch (Exception exception)
            {
                result.ErrorMessage = exception.Message;
                return result;
            }
        }

        public static string BuildCsvFileName(ExperimentSessionTrialRecord record)
        {
            return $"{BuildSessionFilePrefix(record)}__session_trials.csv";
        }

        public static string BuildJsonlFileName(ExperimentSessionTrialRecord record)
        {
            return $"{BuildSessionFilePrefix(record)}__session_trials.jsonl";
        }

        public static string BuildSessionFilePrefix(ExperimentSessionTrialRecord record)
        {
            if (record == null)
            {
                return "session_unknown";
            }

            string participant = SanitizeToken(record.ParticipantId);
            string sessionId = SanitizeToken(record.SessionId);
            string timestamp = ExtractTimestampToken(sessionId);
            string baseToken;

            if (!string.IsNullOrWhiteSpace(participant) && !string.IsNullOrWhiteSpace(timestamp))
            {
                baseToken = $"{participant}_{timestamp}";
            }
            else if (!string.IsNullOrWhiteSpace(participant) && !string.IsNullOrWhiteSpace(sessionId))
            {
                baseToken = $"{participant}_{sessionId}";
            }
            else if (!string.IsNullOrWhiteSpace(timestamp))
            {
                baseToken = $"session_{timestamp}";
            }
            else if (!string.IsNullOrWhiteSpace(sessionId))
            {
                baseToken = $"session_{sessionId}";
            }
            else
            {
                baseToken = "session_unknown";
            }

            var builder = new StringBuilder(baseToken);

            return builder.ToString();
        }

        public static string GetCsvHeader()
        {
            return "session_id,participant_id,condition_id,trial_id,trial_index,task_id,input_mode,drive_profile,autonomy_policy,run_id,scene,selected_target_id,selected_target_category,place_target_id,success,aborted,terminal_state,failure_reason,error_count,non_terminal_warning_count,non_terminal_warnings,total_duration_seconds,navigation_to_pick_duration_seconds,pick_duration_seconds,navigation_to_place_duration_seconds,place_duration_seconds,timestamp_start,timestamp_end,valid_for_analysis,exclusion_reason,trial_summary_csv_path,trial_summary_jsonl_path,events_file_path,samples_file_path,manifest_file_path,condition_name,condition_order_index,robot_enabled,voice_enabled,assistance_mode,spawn_generation_mode,round_id,round_index,round_index_within_condition,rounds_per_condition,global_round_index,allow_non_slot_dynamic_place_fallback,use_dynamic_place_pose,use_deposit_zone_slot_allocator,max_expected_place_distance,max_relaxed_place_distance,place_candidate_reachability_margin,max_place_approach_retries,place_failure_recovery_mode,post_place_egress_mode";
        }

        public static string ToCsvRow(ExperimentSessionTrialRecord record)
        {
            return string.Join(",",
                EscapeCsv(record.SessionId),
                EscapeCsv(record.ParticipantId),
                EscapeCsv(record.ConditionId),
                EscapeCsv(record.TrialId),
                record.TrialIndex.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(record.TaskId),
                EscapeCsv(record.InputMode),
                EscapeCsv(record.DriveProfile),
                EscapeCsv(record.AutonomyPolicy),
                EscapeCsv(record.RunId),
                EscapeCsv(record.Scene),
                EscapeCsv(record.SelectedTargetId),
                EscapeCsv(record.SelectedTargetCategory),
                EscapeCsv(record.PlaceTargetId),
                record.Success ? "true" : "false",
                record.Aborted ? "true" : "false",
                EscapeCsv(record.TerminalState),
                EscapeCsv(record.FailureReason),
                record.ErrorCount.ToString(CultureInfo.InvariantCulture),
                record.NonTerminalWarningCount.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(record.NonTerminalWarnings),
                FormatFloat(record.TotalDurationSeconds),
                FormatFloat(record.NavigationToPickDurationSeconds),
                FormatFloat(record.PickDurationSeconds),
                FormatFloat(record.NavigationToPlaceDurationSeconds),
                FormatFloat(record.PlaceDurationSeconds),
                EscapeCsv(record.TimestampStart),
                EscapeCsv(record.TimestampEnd),
                record.ValidForAnalysis ? "true" : "false",
                EscapeCsv(record.ExclusionReason),
                EscapeCsv(record.TrialSummaryCsvPath),
                EscapeCsv(record.TrialSummaryJsonlPath),
                EscapeCsv(record.EventsFilePath),
                EscapeCsv(record.SamplesFilePath),
                EscapeCsv(record.ManifestFilePath),
                EscapeCsv(record.ConditionName),
                record.ConditionOrderIndex.ToString(CultureInfo.InvariantCulture),
                record.RobotEnabled ? "true" : "false",
                record.VoiceEnabled ? "true" : "false",
                EscapeCsv(record.AssistanceMode),
                EscapeCsv(record.SpawnGenerationMode),
                EscapeCsv(record.RoundId),
                record.RoundIndex.ToString(CultureInfo.InvariantCulture),
                record.RoundIndexWithinCondition.ToString(CultureInfo.InvariantCulture),
                record.RoundsPerCondition.ToString(CultureInfo.InvariantCulture),
                record.GlobalRoundIndex.ToString(CultureInfo.InvariantCulture),
                record.AllowNonSlotDynamicPlaceFallback ? "true" : "false",
                record.UseDynamicPlacePose ? "true" : "false",
                record.UseDepositZoneSlotAllocator ? "true" : "false",
                FormatFloat(record.MaxExpectedPlaceDistance),
                FormatFloat(record.MaxRelaxedPlaceDistance),
                FormatFloat(record.PlaceCandidateReachabilityMargin),
                record.MaxPlaceApproachRetries.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(record.PlaceFailureRecoveryMode),
                EscapeCsv(record.PostPlaceEgressMode));
        }

        public static string ToJson(ExperimentSessionTrialRecord record)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            AppendJson(builder, "session_id", record.SessionId);
            AppendJson(builder, "participant_id", record.ParticipantId);
            AppendJson(builder, "condition_id", record.ConditionId);
            AppendJson(builder, "condition_name", record.ConditionName);
            AppendJson(builder, "condition_order_index", record.ConditionOrderIndex);
            AppendJson(builder, "robot_enabled", record.RobotEnabled);
            AppendJson(builder, "voice_enabled", record.VoiceEnabled);
            AppendJson(builder, "assistance_mode", record.AssistanceMode);
            AppendJson(builder, "spawn_generation_mode", record.SpawnGenerationMode);
            AppendJson(builder, "round_id", record.RoundId);
            AppendJson(builder, "round_index", record.RoundIndex);
            AppendJson(builder, "round_index_within_condition", record.RoundIndexWithinCondition);
            AppendJson(builder, "rounds_per_condition", record.RoundsPerCondition);
            AppendJson(builder, "global_round_index", record.GlobalRoundIndex);
            AppendJson(builder, "allow_non_slot_dynamic_place_fallback", record.AllowNonSlotDynamicPlaceFallback);
            AppendJson(builder, "use_dynamic_place_pose", record.UseDynamicPlacePose);
            AppendJson(builder, "use_deposit_zone_slot_allocator", record.UseDepositZoneSlotAllocator);
            AppendJson(builder, "max_expected_place_distance", record.MaxExpectedPlaceDistance);
            AppendJson(builder, "max_relaxed_place_distance", record.MaxRelaxedPlaceDistance);
            AppendJson(builder, "place_candidate_reachability_margin", record.PlaceCandidateReachabilityMargin);
            AppendJson(builder, "max_place_approach_retries", record.MaxPlaceApproachRetries);
            AppendJson(builder, "place_failure_recovery_mode", record.PlaceFailureRecoveryMode);
            AppendJson(builder, "post_place_egress_mode", record.PostPlaceEgressMode);
            AppendJson(builder, "trial_id", record.TrialId);
            AppendJson(builder, "trial_index", record.TrialIndex);
            AppendJson(builder, "task_id", record.TaskId);
            AppendJson(builder, "input_mode", record.InputMode);
            AppendJson(builder, "drive_profile", record.DriveProfile);
            AppendJson(builder, "autonomy_policy", record.AutonomyPolicy);
            AppendJson(builder, "run_id", record.RunId);
            AppendJson(builder, "scene", record.Scene);
            AppendJson(builder, "selected_target_id", record.SelectedTargetId);
            AppendJson(builder, "selected_target_category", record.SelectedTargetCategory);
            AppendJson(builder, "place_target_id", record.PlaceTargetId);
            AppendJson(builder, "success", record.Success);
            AppendJson(builder, "aborted", record.Aborted);
            AppendJson(builder, "terminal_state", record.TerminalState);
            AppendJson(builder, "failure_reason", record.FailureReason);
            AppendJson(builder, "error_count", record.ErrorCount);
            AppendJson(builder, "non_terminal_warning_count", record.NonTerminalWarningCount);
            AppendJson(builder, "non_terminal_warnings", record.NonTerminalWarnings);
            AppendJson(builder, "total_duration_seconds", record.TotalDurationSeconds);
            AppendJson(builder, "navigation_to_pick_duration_seconds", record.NavigationToPickDurationSeconds);
            AppendJson(builder, "pick_duration_seconds", record.PickDurationSeconds);
            AppendJson(builder, "navigation_to_place_duration_seconds", record.NavigationToPlaceDurationSeconds);
            AppendJson(builder, "place_duration_seconds", record.PlaceDurationSeconds);
            AppendJson(builder, "timestamp_start", record.TimestampStart);
            AppendJson(builder, "timestamp_end", record.TimestampEnd);
            AppendJson(builder, "valid_for_analysis", record.ValidForAnalysis);
            AppendJson(builder, "exclusion_reason", record.ExclusionReason);
            AppendJson(builder, "trial_summary_csv_path", record.TrialSummaryCsvPath);
            AppendJson(builder, "trial_summary_jsonl_path", record.TrialSummaryJsonlPath);
            AppendJson(builder, "events_file_path", record.EventsFilePath);
            AppendJson(builder, "samples_file_path", record.SamplesFilePath);
            AppendJson(builder, "manifest_file_path", record.ManifestFilePath, isLast: true);
            builder.Append('}');
            return builder.ToString();
        }

        private static bool ContainsTrial(string csvPath, string sessionId, string trialId)
        {
            if (!File.Exists(csvPath))
            {
                return false;
            }

            string sessionToken = $"{EscapeCsv(sessionId)},";
            string trialToken = $",{EscapeCsv(trialId)},";
            foreach (string line in File.ReadLines(csvPath))
            {
                if (line.StartsWith(sessionToken, StringComparison.Ordinal) && line.Contains(trialToken))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ResolveDuplicateTrialId(string csvPath, string sessionId, string trialId)
        {
            for (int i = 1; i < 1000; i++)
            {
                string candidate = $"{trialId}_dup{i:00}";
                if (!ContainsTrial(csvPath, sessionId, candidate))
                {
                    return candidate;
                }
            }

            return $"{trialId}_dup{DateTime.UtcNow:yyyyMMddHHmmss}";
        }

        private static string BuildProfilePolicyToken(ExperimentSessionTrialRecord record)
        {
            string driveProfile = SanitizeToken(record.DriveProfile);
            string autonomyPolicy = SanitizeToken(record.AutonomyPolicy);
            if (!string.IsNullOrWhiteSpace(driveProfile) && !string.IsNullOrWhiteSpace(autonomyPolicy))
            {
                return $"{driveProfile}-{autonomyPolicy}";
            }

            return !string.IsNullOrWhiteSpace(driveProfile) ? driveProfile : autonomyPolicy;
        }

        private static string ExtractTimestampToken(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return string.Empty;
            }

            for (int i = 0; i <= sessionId.Length - 15; i++)
            {
                string candidate = sessionId.Substring(i, 15);
                if (IsTimestampToken(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static bool IsTimestampToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 15 || value[8] != '_')
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (i == 8)
                {
                    continue;
                }

                if (!char.IsDigit(value[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static string SanitizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Trim());
            for (int i = 0; i < builder.Length; i++)
            {
                char ch = builder[i];
                bool safe = char.IsLetterOrDigit(ch) || ch == '_' || ch == '-';
                builder[i] = safe ? ch : '_';
            }

            string sanitized = builder.ToString();
            while (sanitized.Contains("__", StringComparison.Ordinal))
            {
                sanitized = sanitized.Replace("__", "_");
            }

            return sanitized.Trim('_');
        }

        private static void AppendJson(StringBuilder builder, string key, string value, bool isLast = false)
        {
            builder.Append('"').Append(EscapeJson(key)).Append("\":\"").Append(EscapeJson(value)).Append('"');
            if (!isLast)
            {
                builder.Append(',');
            }
        }

        private static void AppendJson(StringBuilder builder, string key, int value, bool isLast = false)
        {
            builder.Append('"').Append(EscapeJson(key)).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
            if (!isLast)
            {
                builder.Append(',');
            }
        }

        private static void AppendJson(StringBuilder builder, string key, float value, bool isLast = false)
        {
            builder.Append('"').Append(EscapeJson(key)).Append("\":").Append(FormatJsonNumber(value));
            if (!isLast)
            {
                builder.Append(',');
            }
        }

        private static void AppendJson(StringBuilder builder, string key, bool value, bool isLast = false)
        {
            builder.Append('"').Append(EscapeJson(key)).Append("\":").Append(value ? "true" : "false");
            if (!isLast)
            {
                builder.Append(',');
            }
        }

        private static string FormatFloat(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? string.Empty
                : value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string FormatJsonNumber(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? "null"
                : value.ToString("G9", CultureInfo.InvariantCulture);
        }

        private static string EscapeCsv(string value)
        {
            value ??= string.Empty;
            if (!value.Contains(",") && !value.Contains("\"") && !value.Contains("\n") && !value.Contains("\r"))
            {
                return value;
            }

            return $"\"{value.Replace("\"", "\"\"")}\"";
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
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }
    }
}
