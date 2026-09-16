namespace Autonomy.Domain
{
    public sealed class ExperimentSessionTrialRecord
    {
        public string SessionId;
        public string ParticipantId;
        public string ConditionId;
        public string ConditionName;
        public int ConditionOrderIndex;
        public bool RobotEnabled;
        public bool VoiceEnabled;
        public string AssistanceMode;
        public string SpawnGenerationMode;
        public string RoundId;
        public int RoundIndex;
        public int RoundIndexWithinCondition;
        public int RoundsPerCondition;
        public int GlobalRoundIndex;
        public bool AllowNonSlotDynamicPlaceFallback;
        public bool UseDynamicPlacePose;
        public bool UseDepositZoneSlotAllocator;
        public float MaxExpectedPlaceDistance;
        public float MaxRelaxedPlaceDistance;
        public float PlaceCandidateReachabilityMargin;
        public int MaxPlaceApproachRetries;
        public string PlaceFailureRecoveryMode;
        public string PostPlaceEgressMode;
        public string TrialId;
        public int TrialIndex;
        public string TaskId;
        public string InputMode;
        public string DriveProfile;
        public string AutonomyPolicy;
        public string RunId;
        public string Scene;
        public string SelectedTargetId;
        public string SelectedTargetCategory;
        public string PlaceTargetId;
        public bool Success;
        public bool Aborted;
        public string TerminalState;
        public string FailureReason;
        public int ErrorCount;
        public int NonTerminalWarningCount;
        public string NonTerminalWarnings;
        public float TotalDurationSeconds;
        public float NavigationToPickDurationSeconds;
        public float PickDurationSeconds;
        public float NavigationToPlaceDurationSeconds;
        public float PlaceDurationSeconds;
        public string TimestampStart;
        public string TimestampEnd;
        public bool ValidForAnalysis;
        public string ExclusionReason;
        public string TrialSummaryCsvPath;
        public string TrialSummaryJsonlPath;
        public string EventsFilePath;
        public string SamplesFilePath;
        public string ManifestFilePath;

        public static ExperimentSessionTrialRecord FromSummary(
            ExperimentTrialSummary summary,
            string trialSummaryCsvPath,
            string trialSummaryJsonlPath,
            string eventsFilePath,
            string samplesFilePath,
            string manifestFilePath,
            bool? validForAnalysisOverride = null,
            string exclusionReasonOverride = "")
        {
            bool autoValid = summary.Success && !summary.Aborted && string.IsNullOrWhiteSpace(summary.FailureReason);
            bool valid = validForAnalysisOverride ?? autoValid;
            string exclusionReason = exclusionReasonOverride ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exclusionReason) && !valid)
            {
                exclusionReason = summary.Aborted
                    ? "aborted"
                    : (!string.IsNullOrWhiteSpace(summary.FailureReason) ? summary.FailureReason : "manual_override");
            }

            return new ExperimentSessionTrialRecord
            {
                SessionId = summary.SessionId,
                ParticipantId = summary.ParticipantId,
                ConditionId = summary.ConditionId,
                ConditionName = summary.ConditionName,
                ConditionOrderIndex = summary.ConditionOrderIndex,
                RobotEnabled = summary.RobotEnabled,
                VoiceEnabled = summary.VoiceEnabled,
                AssistanceMode = summary.AssistanceMode,
                SpawnGenerationMode = summary.SpawnGenerationMode,
                RoundId = summary.RoundId,
                RoundIndex = summary.RoundIndex,
                RoundIndexWithinCondition = summary.RoundIndexWithinCondition,
                RoundsPerCondition = summary.RoundsPerCondition,
                GlobalRoundIndex = summary.GlobalRoundIndex,
                AllowNonSlotDynamicPlaceFallback = summary.AllowNonSlotDynamicPlaceFallback,
                UseDynamicPlacePose = summary.UseDynamicPlacePose,
                UseDepositZoneSlotAllocator = summary.UseDepositZoneSlotAllocator,
                MaxExpectedPlaceDistance = summary.MaxExpectedPlaceDistance,
                MaxRelaxedPlaceDistance = summary.MaxRelaxedPlaceDistance,
                PlaceCandidateReachabilityMargin = summary.PlaceCandidateReachabilityMargin,
                MaxPlaceApproachRetries = summary.MaxPlaceApproachRetries,
                PlaceFailureRecoveryMode = summary.PlaceFailureRecoveryMode,
                PostPlaceEgressMode = summary.PostPlaceEgressMode,
                TrialId = summary.TrialId,
                TrialIndex = summary.TrialIndex,
                TaskId = summary.TaskId,
                InputMode = summary.InputMode,
                DriveProfile = summary.DriveProfile,
                AutonomyPolicy = summary.AutonomyPolicy,
                RunId = summary.RunId,
                Scene = summary.SceneName,
                SelectedTargetId = summary.SelectedTargetId,
                SelectedTargetCategory = summary.SelectedTargetCategory,
                PlaceTargetId = summary.PlaceTargetId,
                Success = summary.Success,
                Aborted = summary.Aborted,
                TerminalState = string.IsNullOrWhiteSpace(summary.TerminalState)
                    ? (summary.Aborted ? "aborted" : (summary.Success ? "completed" : "failed"))
                    : summary.TerminalState,
                FailureReason = summary.FailureReason,
                ErrorCount = summary.ErrorCount,
                NonTerminalWarningCount = summary.NonTerminalWarningCount,
                NonTerminalWarnings = summary.NonTerminalWarnings,
                TotalDurationSeconds = summary.TotalDurationSeconds,
                NavigationToPickDurationSeconds = summary.NavigationToPickDurationSeconds,
                PickDurationSeconds = summary.PickDurationSeconds,
                NavigationToPlaceDurationSeconds = summary.NavigationToPlaceDurationSeconds,
                PlaceDurationSeconds = summary.PlaceDurationSeconds,
                TimestampStart = summary.TimestampStart,
                TimestampEnd = summary.TimestampEnd,
                ValidForAnalysis = valid,
                ExclusionReason = valid ? string.Empty : exclusionReason,
                TrialSummaryCsvPath = trialSummaryCsvPath,
                TrialSummaryJsonlPath = trialSummaryJsonlPath,
                EventsFilePath = eventsFilePath,
                SamplesFilePath = samplesFilePath,
                ManifestFilePath = manifestFilePath
            };
        }
    }
}
