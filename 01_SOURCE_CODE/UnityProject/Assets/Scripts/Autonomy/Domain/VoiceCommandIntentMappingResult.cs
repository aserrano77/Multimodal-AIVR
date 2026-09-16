using System;
using System.Collections.Generic;
using System.Linq;

namespace Autonomy.Domain
{
    public sealed class VoiceCommandIntentMappingResult
    {
        public VoiceCommandIntentMappingResult(
            VoiceCommandIntentMappingStatus status,
            VoiceCommandIntentMappingError error,
            VoiceCommandIntentKind intentKind,
            MultimodalTaskIntent taskIntent,
            VoiceCommandNormalizationResult normalization,
            string candidateDescription,
            string explanation)
        {
            Status = status;
            Error = error;
            IntentKind = intentKind;
            TaskIntent = taskIntent;
            Normalization = normalization;
            RawTranscript = normalization != null ? normalization.RawTranscript : string.Empty;
            CleanedTranscript = normalization != null ? normalization.CleanedTranscript : string.Empty;
            NormalizedText = normalization != null ? normalization.NormalizedText : string.Empty;
            NormalizationStatus = normalization != null ? normalization.Status : VoiceCommandRecognitionStatus.Unrecognized;
            Score = normalization != null ? normalization.Score : 0f;
            CanonicalPhrase = normalization != null ? normalization.CanonicalPhrase : string.Empty;
            CorrectionsApplied = normalization != null
                ? normalization.CorrectionsApplied.ToArray()
                : Array.Empty<string>();
            AmbiguityReason = normalization != null ? normalization.AmbiguityReason : string.Empty;
            CandidateDescription = candidateDescription ?? string.Empty;
            Explanation = explanation ?? string.Empty;
        }

        public VoiceCommandIntentMappingStatus Status { get; }
        public VoiceCommandIntentMappingError Error { get; }
        public VoiceCommandIntentKind IntentKind { get; }
        public MultimodalTaskIntent TaskIntent { get; }
        public VoiceCommandNormalizationResult Normalization { get; }
        public string RawTranscript { get; }
        public string CleanedTranscript { get; }
        public string NormalizedText { get; }
        public VoiceCommandRecognitionStatus NormalizationStatus { get; }
        public float Score { get; }
        public string CanonicalPhrase { get; }
        public IReadOnlyList<string> CorrectionsApplied { get; }
        public string AmbiguityReason { get; }
        public string CandidateDescription { get; }
        public string Explanation { get; }
        public bool HasExecutableTaskIntent => Status == VoiceCommandIntentMappingStatus.Mapped && TaskIntent != null;
        public bool IsAmbiguous => Status == VoiceCommandIntentMappingStatus.Ambiguous;
    }
}
