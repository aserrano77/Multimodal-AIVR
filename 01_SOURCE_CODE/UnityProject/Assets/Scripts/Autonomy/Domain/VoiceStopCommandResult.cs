namespace Autonomy.Domain
{
    public sealed class VoiceStopCommandResult
    {
        private VoiceStopCommandResult(bool accepted, string reason, string feedbackText)
        {
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            FeedbackText = feedbackText ?? string.Empty;
        }

        public bool Accepted { get; }
        public string Reason { get; }
        public string FeedbackText { get; }

        public static VoiceStopCommandResult Accept(string reason, string feedbackText = "")
        {
            return new VoiceStopCommandResult(true, reason, feedbackText);
        }

        public static VoiceStopCommandResult Reject(string reason, string feedbackText = "")
        {
            return new VoiceStopCommandResult(false, reason, feedbackText);
        }
    }
}
