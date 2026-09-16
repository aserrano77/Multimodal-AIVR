using System;
using System.Collections.Generic;
using System.Linq;

namespace Autonomy.Domain
{
    public sealed class VoiceCommandNormalizationResult
    {
        public VoiceCommandNormalizationResult(
            string rawTranscript,
            string cleanedTranscript,
            string normalizedText,
            VoiceCommandRecognitionStatus status,
            float score,
            string canonicalPhrase,
            VoiceCommandActionToken action,
            ObjectToken objectToken,
            string objectLabel,
            string destinationLabel,
            IEnumerable<string> correctionsApplied,
            string ambiguityReason)
        {
            RawTranscript = rawTranscript ?? string.Empty;
            CleanedTranscript = cleanedTranscript ?? string.Empty;
            NormalizedText = normalizedText ?? string.Empty;
            Status = status;
            Score = Math.Max(0f, Math.Min(1f, score));
            CanonicalPhrase = canonicalPhrase ?? string.Empty;
            Action = action;
            Object = objectToken;
            ObjectLabel = NormalizeLabel(objectLabel);
            DestinationLabel = NormalizeLabel(destinationLabel);
            CorrectionsApplied = (correctionsApplied ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            AmbiguityReason = ambiguityReason ?? string.Empty;
        }

        public string RawTranscript { get; }
        public string CleanedTranscript { get; }
        public string NormalizedText { get; }
        public VoiceCommandRecognitionStatus Status { get; }
        public float Score { get; }
        public string CanonicalPhrase { get; }
        public VoiceCommandActionToken Action { get; }
        public ObjectToken Object { get; }
        public string ObjectLabel { get; }
        public string DestinationLabel { get; }
        public IReadOnlyList<string> CorrectionsApplied { get; }
        public string AmbiguityReason { get; }

        public override string ToString()
        {
            string corrections = CorrectionsApplied.Count == 0 ? "none" : string.Join("; ", CorrectionsApplied);
            return $"raw='{RawTranscript}' cleaned='{CleanedTranscript}' normalized='{NormalizedText}' status={Status} score={Score:0.###} action={Action} object={Object} object_label={ObjectLabel} destination_label={DestinationLabel} corrections={corrections} ambiguity='{AmbiguityReason}'";
        }

        private static string NormalizeLabel(string label)
        {
            return string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim().ToUpperInvariant();
        }
    }
}
