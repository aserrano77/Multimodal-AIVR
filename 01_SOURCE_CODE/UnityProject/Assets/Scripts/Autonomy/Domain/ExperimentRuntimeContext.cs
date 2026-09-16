using System.Collections.Generic;

namespace Autonomy.Domain
{
    public sealed class ExperimentRuntimeContext
    {
        public ExperimentRuntimeContext(
            string participantId,
            string sessionId,
            string trialId,
            int trialIndex,
            string conditionId,
            string conditionName,
            int conditionOrderIndex,
            bool robotEnabled,
            bool voiceEnabled,
            RobotAssistanceMode assistanceMode,
            SpawnGenerationMode spawnGenerationMode,
            string taskId,
            string inputMode,
            string roundId,
            int roundIndex = 1,
            int roundIndexWithinCondition = 1,
            int roundsPerCondition = 1,
            int globalRoundIndex = 1,
            RobotAssistancePlaceRecoveryConfig placeRecoveryConfig = null,
            string placeFailureRecoveryMode = "",
            string postPlaceEgressMode = "",
            ExperimentRunMode runMode = ExperimentRunMode.Orchestrated2x2)
        {
            ParticipantId = participantId ?? string.Empty;
            SessionId = sessionId ?? string.Empty;
            TrialId = trialId ?? string.Empty;
            TrialIndex = trialIndex;
            ConditionId = conditionId ?? string.Empty;
            ConditionName = conditionName ?? string.Empty;
            ConditionOrderIndex = conditionOrderIndex;
            RobotEnabled = robotEnabled;
            VoiceEnabled = voiceEnabled;
            AssistanceMode = robotEnabled ? assistanceMode : RobotAssistanceMode.Disabled;
            SpawnGenerationMode = spawnGenerationMode;
            TaskId = taskId ?? string.Empty;
            InputMode = inputMode ?? string.Empty;
            RoundId = roundId ?? string.Empty;
            RoundIndex = roundIndex < 1 ? 1 : roundIndex;
            RoundIndexWithinCondition = roundIndexWithinCondition < 1 ? 1 : roundIndexWithinCondition;
            RoundsPerCondition = roundsPerCondition < 1 ? 1 : roundsPerCondition;
            GlobalRoundIndex = globalRoundIndex < 1 ? RoundIndex : globalRoundIndex;
            PlaceRecoveryConfig = placeRecoveryConfig;
            PlaceFailureRecoveryMode = placeFailureRecoveryMode ?? string.Empty;
            PostPlaceEgressMode = postPlaceEgressMode ?? string.Empty;
            RunMode = runMode;
        }

        public string ParticipantId { get; }
        public string SessionId { get; }
        public string TrialId { get; }
        public int TrialIndex { get; }
        public string ConditionId { get; }
        public string ConditionName { get; }
        public int ConditionOrderIndex { get; }
        public bool RobotEnabled { get; }
        public bool VoiceEnabled { get; }
        public RobotAssistanceMode AssistanceMode { get; }
        public SpawnGenerationMode SpawnGenerationMode { get; }
        public string TaskId { get; }
        public string InputMode { get; }
        public string RoundId { get; }
        public int RoundIndex { get; }
        public int RoundIndexWithinCondition { get; }
        public int RoundsPerCondition { get; }
        public int GlobalRoundIndex { get; }
        public RobotAssistancePlaceRecoveryConfig PlaceRecoveryConfig { get; }
        public string PlaceFailureRecoveryMode { get; }
        public string PostPlaceEgressMode { get; }
        public ExperimentRunMode RunMode { get; }

        public ExperimentConditionConfig ToConditionConfig()
        {
            return new ExperimentConditionConfig(RobotEnabled, VoiceEnabled, ConditionName, AssistanceMode);
        }

        public Dictionary<string, object> ToPayload()
        {
            var payload = new Dictionary<string, object>
            {
                ["participant_id"] = ParticipantId,
                ["session_id"] = SessionId,
                ["trial_id"] = TrialId,
                ["trial_index"] = TrialIndex,
                ["condition_id"] = ConditionId,
                ["condition_name"] = ConditionName,
                ["condition_order_index"] = ConditionOrderIndex,
                ["robot_enabled"] = RobotEnabled,
                ["voice_enabled"] = VoiceEnabled,
                ["condition_2x2"] = ConditionName,
                ["assistance_mode"] = AssistanceMode.ToString(),
                ["spawn_generation_mode"] = SpawnGenerationMode.ToString(),
                ["task_id"] = TaskId,
                ["input_mode"] = InputMode,
                ["round_id"] = RoundId,
                ["round_index"] = RoundIndex,
                ["round_index_within_condition"] = RoundIndexWithinCondition,
                ["rounds_per_condition"] = RoundsPerCondition,
                ["global_round_index"] = GlobalRoundIndex,
                ["experiment_run_mode"] = RunMode.ToString(),
                ["place_failure_recovery_mode"] = PlaceFailureRecoveryMode,
                ["post_place_egress_mode"] = PostPlaceEgressMode
            };

            if (PlaceRecoveryConfig != null)
            {
                foreach (KeyValuePair<string, object> pair in PlaceRecoveryConfig.ToPayload())
                {
                    payload[pair.Key] = pair.Value;
                }
            }

            return payload;
        }

        public bool MatchesConditionPreset(out string reason)
        {
            reason = string.Empty;
            switch (ConditionId)
            {
                case "C00_robot_off_voice_off":
                    return Expect(false, false, out reason);
                case "C01_robot_off_voice_on":
                    return Expect(false, true, out reason);
                case "C10_robot_on_voice_off":
                    return Expect(true, false, out reason);
                case "C11_robot_on_voice_on":
                    return Expect(true, true, out reason);
                default:
                    reason = "unknown_condition_id";
                    return false;
            }
        }

        private bool Expect(bool robotEnabled, bool voiceEnabled, out string reason)
        {
            if (RobotEnabled == robotEnabled && VoiceEnabled == voiceEnabled)
            {
                reason = string.Empty;
                return true;
            }

            reason = $"condition_id_flags_mismatch_expected_robot_{robotEnabled}_voice_{voiceEnabled}";
            return false;
        }
    }
}
