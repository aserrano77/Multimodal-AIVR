namespace Autonomy.Domain
{
    public sealed class ExperimentSessionMetadata
    {
        public ExperimentSessionMetadata(
            string participantId,
            string sessionId,
            string conditionId,
            string taskId,
            string inputMode,
            string driveProfile,
            string autonomyPolicy,
            string sceneName,
            string notes,
            string conditionName = "",
            int conditionOrderIndex = 0,
            bool robotEnabled = false,
            bool voiceEnabled = false,
            string assistanceMode = "",
            string spawnGenerationMode = "",
            string roundId = "",
            int roundIndex = 1,
            int roundIndexWithinCondition = 1,
            int roundsPerCondition = 1,
            int globalRoundIndex = 1,
            bool allowNonSlotDynamicPlaceFallback = false,
            bool useDynamicPlacePose = false,
            bool useDepositZoneSlotAllocator = false,
            float maxExpectedPlaceDistance = 0f,
            float maxRelaxedPlaceDistance = 0f,
            float placeCandidateReachabilityMargin = 0f,
            int maxPlaceApproachRetries = 0,
            string placeFailureRecoveryMode = "",
            string postPlaceEgressMode = "")
        {
            ParticipantId = participantId ?? string.Empty;
            SessionId = sessionId ?? string.Empty;
            ConditionId = conditionId ?? string.Empty;
            ConditionName = conditionName ?? string.Empty;
            ConditionOrderIndex = conditionOrderIndex;
            RobotEnabled = robotEnabled;
            VoiceEnabled = voiceEnabled;
            AssistanceMode = assistanceMode ?? string.Empty;
            SpawnGenerationMode = spawnGenerationMode ?? string.Empty;
            RoundId = roundId ?? string.Empty;
            RoundIndex = roundIndex < 1 ? 1 : roundIndex;
            RoundIndexWithinCondition = roundIndexWithinCondition < 1 ? 1 : roundIndexWithinCondition;
            RoundsPerCondition = roundsPerCondition < 1 ? 1 : roundsPerCondition;
            GlobalRoundIndex = globalRoundIndex < 1 ? RoundIndex : globalRoundIndex;
            AllowNonSlotDynamicPlaceFallback = allowNonSlotDynamicPlaceFallback;
            UseDynamicPlacePose = useDynamicPlacePose;
            UseDepositZoneSlotAllocator = useDepositZoneSlotAllocator;
            MaxExpectedPlaceDistance = maxExpectedPlaceDistance;
            MaxRelaxedPlaceDistance = maxRelaxedPlaceDistance;
            PlaceCandidateReachabilityMargin = placeCandidateReachabilityMargin;
            MaxPlaceApproachRetries = maxPlaceApproachRetries;
            PlaceFailureRecoveryMode = placeFailureRecoveryMode ?? string.Empty;
            PostPlaceEgressMode = postPlaceEgressMode ?? string.Empty;
            TaskId = taskId ?? string.Empty;
            InputMode = inputMode ?? string.Empty;
            DriveProfile = driveProfile ?? string.Empty;
            AutonomyPolicy = autonomyPolicy ?? string.Empty;
            SceneName = sceneName ?? string.Empty;
            Notes = notes ?? string.Empty;
        }

        public string ParticipantId { get; }
        public string SessionId { get; }
        public string ConditionId { get; }
        public string ConditionName { get; }
        public int ConditionOrderIndex { get; }
        public bool RobotEnabled { get; }
        public bool VoiceEnabled { get; }
        public string AssistanceMode { get; }
        public string SpawnGenerationMode { get; }
        public string RoundId { get; }
        public int RoundIndex { get; }
        public int RoundIndexWithinCondition { get; }
        public int RoundsPerCondition { get; }
        public int GlobalRoundIndex { get; }
        public bool AllowNonSlotDynamicPlaceFallback { get; }
        public bool UseDynamicPlacePose { get; }
        public bool UseDepositZoneSlotAllocator { get; }
        public float MaxExpectedPlaceDistance { get; }
        public float MaxRelaxedPlaceDistance { get; }
        public float PlaceCandidateReachabilityMargin { get; }
        public int MaxPlaceApproachRetries { get; }
        public string PlaceFailureRecoveryMode { get; }
        public string PostPlaceEgressMode { get; }
        public string TaskId { get; }
        public string InputMode { get; }
        public string DriveProfile { get; }
        public string AutonomyPolicy { get; }
        public string SceneName { get; }
        public string Notes { get; }
    }
}
