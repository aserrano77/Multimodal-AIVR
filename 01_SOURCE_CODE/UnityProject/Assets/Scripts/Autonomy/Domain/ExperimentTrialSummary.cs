namespace Autonomy.Domain
{
    public sealed class ExperimentTrialSummary
    {
        public string RunId;
        public string ParticipantId;
        public string SessionId;
        public string TrialId;
        public int TrialIndex;
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
        public string TaskId;
        public string InputMode;
        public string DriveProfile;
        public string AutonomyPolicy;
        public string SceneName;
        public string SelectedTargetId;
        public string SelectedTargetCategory;
        public string PlaceTargetId;
        public bool Success;
        public string FailureReason;
        public float TotalDurationSeconds;
        public float NavigationToPickDurationSeconds;
        public float PickDurationSeconds;
        public float NavigationToPlaceDurationSeconds;
        public float PlaceDurationSeconds;
        public int ErrorCount;
        public int NonTerminalWarningCount;
        public string NonTerminalWarnings;
        public bool Aborted;
        public string TerminalState;
        public string TimestampStart;
        public string TimestampEnd;
        public string Notes;
    }
}
