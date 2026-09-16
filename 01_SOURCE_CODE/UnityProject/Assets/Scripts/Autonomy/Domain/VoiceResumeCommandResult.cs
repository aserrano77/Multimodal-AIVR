namespace Autonomy.Domain
{
    public sealed class VoiceResumeCommandResult
    {
        private VoiceResumeCommandResult(bool accepted, string reason, string feedbackText)
        {
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            FeedbackText = feedbackText ?? string.Empty;
        }

        public bool Accepted { get; }
        public string Reason { get; }
        public string FeedbackText { get; }

        public static VoiceResumeCommandResult Accept(string reason, string feedbackText = "")
        {
            return new VoiceResumeCommandResult(true, reason, feedbackText);
        }

        public static VoiceResumeCommandResult Reject(string reason, string feedbackText = "")
        {
            return new VoiceResumeCommandResult(false, reason, feedbackText);
        }
    }
}
