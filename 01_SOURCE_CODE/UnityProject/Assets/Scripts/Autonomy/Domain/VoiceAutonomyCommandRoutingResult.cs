namespace Autonomy.Domain
{
    public sealed class VoiceAutonomyCommandRoutingResult
    {
        public VoiceAutonomyCommandRoutingResult(
            VoiceAutonomyCommandRoutingStatus status,
            string reason,
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingResult mapping,
            MultimodalTaskIntent submittedIntent,
            VoiceUserFeedbackMessage feedbackMessage = null,
            MultimodalTaskIntent pendingIntent = null,
            RobotVoiceFeedbackMessage robotFeedbackMessage = null)
        {
            Status = status;
            Reason = reason ?? string.Empty;
            Normalization = normalization;
            Mapping = mapping;
            SubmittedIntent = submittedIntent;
            FeedbackMessage = feedbackMessage;
            PendingIntent = pendingIntent;
            RobotFeedbackMessage = robotFeedbackMessage;
        }

        public VoiceAutonomyCommandRoutingStatus Status { get; }
        public string Reason { get; }
        public VoiceCommandNormalizationResult Normalization { get; }
        public VoiceCommandIntentMappingResult Mapping { get; }
        public MultimodalTaskIntent SubmittedIntent { get; }
        public VoiceUserFeedbackMessage FeedbackMessage { get; }
        public MultimodalTaskIntent PendingIntent { get; }
        public RobotVoiceFeedbackMessage RobotFeedbackMessage { get; }
        public bool Submitted => Status == VoiceAutonomyCommandRoutingStatus.Submitted;
    }
}
