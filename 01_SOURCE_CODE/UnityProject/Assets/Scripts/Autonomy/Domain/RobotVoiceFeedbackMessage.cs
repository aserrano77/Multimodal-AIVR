using System;

namespace Autonomy.Domain
{
    public sealed class RobotVoiceFeedbackMessage
    {
        public RobotVoiceFeedbackMessage(RobotVoiceFeedbackKind kind, string text, RobotVoiceFeedbackContext context)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            Context = context;
            CreatedAtUtc = DateTime.UtcNow;
        }

        public RobotVoiceFeedbackKind Kind { get; }
        public string Text { get; }
        public RobotVoiceFeedbackContext Context { get; }
        public DateTime CreatedAtUtc { get; }
    }
}
