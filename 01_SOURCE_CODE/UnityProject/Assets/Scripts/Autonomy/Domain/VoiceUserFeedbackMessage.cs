using System;

namespace Autonomy.Domain
{
    public sealed class VoiceUserFeedbackMessage
    {
        public VoiceUserFeedbackMessage(
            VoiceUserFeedbackType type,
            string text,
            string reason = "",
            MultimodalTaskIntent intent = null,
            VoiceCommandIntentMappingResult mapping = null)
        {
            Type = type;
            Text = text ?? string.Empty;
            Reason = reason ?? string.Empty;
            Intent = intent;
            Mapping = mapping;
            CreatedAtUtc = DateTime.UtcNow;
        }

        public VoiceUserFeedbackType Type { get; }
        public string Text { get; }
        public string Reason { get; }
        public MultimodalTaskIntent Intent { get; }
        public VoiceCommandIntentMappingResult Mapping { get; }
        public DateTime CreatedAtUtc { get; }
    }
}
