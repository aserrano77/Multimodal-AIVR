namespace Autonomy.Domain
{
    public sealed class RobotVoiceFeedbackContext
    {
        public RobotVoiceFeedbackKind Kind { get; init; }
        public string Text { get; init; } = string.Empty;
        public string RawTranscript { get; init; } = string.Empty;
        public string NormalizedText { get; init; } = string.Empty;
        public VoiceCommandIntentKind IntentKind { get; init; } = VoiceCommandIntentKind.None;
        public string TargetAlias { get; init; } = string.Empty;
        public string TargetId { get; init; } = string.Empty;
        public string Destination { get; init; } = string.Empty;
        public int CandidateCount { get; init; }
        public string RobotTaskState { get; init; } = string.Empty;
        public string ConditionId { get; init; } = string.Empty;
        public string TrialId { get; init; } = string.Empty;
        public string RoundId { get; init; } = string.Empty;
        public string PendingCommandId { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public bool Accepted { get; init; }
        public string PreviousTargetAlias { get; init; } = string.Empty;
        public string CurrentTargetAlias { get; init; } = string.Empty;
    }
}
