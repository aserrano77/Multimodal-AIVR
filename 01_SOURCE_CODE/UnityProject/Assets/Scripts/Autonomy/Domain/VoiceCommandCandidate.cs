namespace Autonomy.Domain
{
    public sealed class VoiceCommandCandidate
    {
        public VoiceCommandCandidate(
            VoiceCommandActionToken action,
            ObjectToken objectToken,
            string objectLabel,
            string destinationLabel,
            string canonicalPhrase,
            float score)
        {
            Action = action;
            Object = objectToken;
            ObjectLabel = NormalizeLabel(objectLabel);
            DestinationLabel = NormalizeLabel(destinationLabel);
            CanonicalPhrase = canonicalPhrase ?? string.Empty;
            Score = score;
        }

        public VoiceCommandActionToken Action { get; }
        public ObjectToken Object { get; }
        public string ObjectLabel { get; }
        public string DestinationLabel { get; }
        public string CanonicalPhrase { get; }
        public float Score { get; }

        private static string NormalizeLabel(string label)
        {
            return string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim().ToUpperInvariant();
        }
    }
}
