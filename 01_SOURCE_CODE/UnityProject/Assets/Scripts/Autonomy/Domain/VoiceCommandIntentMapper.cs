namespace Autonomy.Domain
{
    public sealed class VoiceCommandIntentMapper : IVoiceCommandIntentMapper
    {
        private const string Source = "voice_command";

        public VoiceCommandIntentMappingResult Map(VoiceCommandNormalizationResult normalization)
        {
            if (normalization == null)
            {
                return Build(
                    null,
                    VoiceCommandIntentMappingStatus.NotMapped,
                    VoiceCommandIntentMappingError.NullNormalizationResult,
                    VoiceCommandIntentKind.None,
                    null,
                    string.Empty,
                    "normalization result is null");
            }

            if (TryMapDiagnosticControlIntent(normalization, out VoiceCommandIntentMappingResult diagnosticControlResult))
            {
                return diagnosticControlResult;
            }

            VoiceCommandIntentKind candidateKind = ResolveCandidateKind(normalization.Action);
            string candidate = BuildCandidateDescription(normalization, candidateKind);

            if (normalization.Status == VoiceCommandRecognitionStatus.Unrecognized)
            {
                return Build(
                    normalization,
                    VoiceCommandIntentMappingStatus.NotMapped,
                    VoiceCommandIntentMappingError.NormalizationUnrecognized,
                    candidateKind,
                    null,
                    candidate,
                    "normalizer did not recognize an executable voice command");
            }

            if (normalization.Status == VoiceCommandRecognitionStatus.Ambiguous)
            {
                return Build(
                    normalization,
                    VoiceCommandIntentMappingStatus.Ambiguous,
                    VoiceCommandIntentMappingError.NormalizationAmbiguous,
                    candidateKind,
                    null,
                    candidate,
                    string.IsNullOrWhiteSpace(normalization.AmbiguityReason)
                        ? "normalizer marked the command as ambiguous"
                        : normalization.AmbiguityReason);
            }

            switch (normalization.Action)
            {
                case VoiceCommandActionToken.Pick:
                    return MapPick(normalization, candidate);
                case VoiceCommandActionToken.Transport:
                    return MapTransport(normalization, candidate);
                case VoiceCommandActionToken.Stop:
                    return Build(
                        normalization,
                        VoiceCommandIntentMappingStatus.Mapped,
                        VoiceCommandIntentMappingError.ControlIntentNoBridgeTask,
                        VoiceCommandIntentKind.Stop,
                        null,
                        candidate,
                        "recognized stop intent; handled by the control route without producing a multimodal bridge task");
                case VoiceCommandActionToken.Resume:
                    return Build(
                        normalization,
                        VoiceCommandIntentMappingStatus.Mapped,
                        VoiceCommandIntentMappingError.ControlIntentNoBridgeTask,
                        VoiceCommandIntentKind.Resume,
                        null,
                        candidate,
                        "recognized resume intent; handled by the control route without producing a multimodal bridge task");
                case VoiceCommandActionToken.Unknown:
                    return Build(
                        normalization,
                        VoiceCommandIntentMappingStatus.Incomplete,
                        VoiceCommandIntentMappingError.MissingAction,
                        VoiceCommandIntentKind.None,
                        null,
                        candidate,
                        "recognized normalization result has no action token");
                default:
                    return Build(
                        normalization,
                        VoiceCommandIntentMappingStatus.NotMapped,
                        VoiceCommandIntentMappingError.UnsupportedAction,
                        VoiceCommandIntentKind.None,
                        null,
                        candidate,
                        "voice action token is not supported by the semantic mapper");
            }
        }

        private static VoiceCommandIntentMappingResult MapPick(
            VoiceCommandNormalizationResult normalization,
            string candidate)
        {
            if (normalization.Object != ObjectToken.Box)
            {
                return Build(
                    normalization,
                    VoiceCommandIntentMappingStatus.Incomplete,
                    VoiceCommandIntentMappingError.MissingObject,
                    VoiceCommandIntentKind.Pick,
                    null,
                    candidate,
                    "pick command requires a box object");
            }

            if (string.IsNullOrWhiteSpace(normalization.ObjectLabel))
            {
                return Build(
                    normalization,
                    VoiceCommandIntentMappingStatus.Incomplete,
                    VoiceCommandIntentMappingError.MissingObjectLabel,
                    VoiceCommandIntentKind.Pick,
                    null,
                    candidate,
                    "pick command requires an object label");
            }

            MultimodalTaskIntent intent = MultimodalTaskIntent.PickOnlyByCategory(normalization.ObjectLabel, Source);
            return Build(
                normalization,
                VoiceCommandIntentMappingStatus.Mapped,
                VoiceCommandIntentMappingError.None,
                VoiceCommandIntentKind.Pick,
                intent,
                candidate,
                "recognized pick command mapped to a pick-only task intent");
        }

        private static VoiceCommandIntentMappingResult MapTransport(
            VoiceCommandNormalizationResult normalization,
            string candidate)
        {
            if (normalization.Object != ObjectToken.Box)
            {
                return Build(
                    normalization,
                    VoiceCommandIntentMappingStatus.Incomplete,
                    VoiceCommandIntentMappingError.MissingObject,
                    VoiceCommandIntentKind.PickAndPlace,
                    null,
                    candidate,
                    "transport command requires a box object");
            }

            if (string.IsNullOrWhiteSpace(normalization.ObjectLabel))
            {
                return Build(
                    normalization,
                    VoiceCommandIntentMappingStatus.Incomplete,
                    VoiceCommandIntentMappingError.MissingObjectLabel,
                    VoiceCommandIntentKind.PickAndPlace,
                    null,
                    candidate,
                    "transport command requires an object label");
            }

            if (string.IsNullOrWhiteSpace(normalization.DestinationLabel))
            {
                return Build(
                    normalization,
                    VoiceCommandIntentMappingStatus.Incomplete,
                    VoiceCommandIntentMappingError.MissingDestinationLabel,
                    VoiceCommandIntentKind.PickAndPlace,
                    null,
                    candidate,
                    "transport command requires a destination label");
            }

            MultimodalTaskIntent intent = IsExplicitSpokenTargetLabel(normalization.ObjectLabel)
                ? MultimodalTaskIntent.PickAndPlaceByTargetId(
                    normalization.ObjectLabel,
                    FormatZoneTargetId(normalization.DestinationLabel),
                    Source)
                : MultimodalTaskIntent.PickAndPlaceByCategory(
                    normalization.ObjectLabel,
                    FormatZoneTargetId(normalization.DestinationLabel),
                    Source);
            return Build(
                normalization,
                VoiceCommandIntentMappingStatus.Mapped,
                VoiceCommandIntentMappingError.None,
                VoiceCommandIntentKind.PickAndPlace,
                intent,
                candidate,
                "recognized transport command mapped to a pick-and-place task intent");
        }

        private static VoiceCommandIntentKind ResolveCandidateKind(VoiceCommandActionToken action)
        {
            switch (action)
            {
                case VoiceCommandActionToken.Pick:
                    return VoiceCommandIntentKind.Pick;
                case VoiceCommandActionToken.Transport:
                    return VoiceCommandIntentKind.PickAndPlace;
                case VoiceCommandActionToken.Stop:
                    return VoiceCommandIntentKind.Stop;
                case VoiceCommandActionToken.Resume:
                    return VoiceCommandIntentKind.Resume;
                default:
                    return VoiceCommandIntentKind.None;
            }
        }

        private static bool TryMapDiagnosticControlIntent(
            VoiceCommandNormalizationResult normalization,
            out VoiceCommandIntentMappingResult result)
        {
            result = null;
            string text = normalization.CleanedTranscript;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = normalization.NormalizedText;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                text = normalization.RawTranscript;
            }

            text = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (IsCancelPendingOrderText(text))
            {
                result = Build(
                    normalization,
                    VoiceCommandIntentMappingStatus.Mapped,
                    VoiceCommandIntentMappingError.DiagnosticIntentNotExecutable,
                    VoiceCommandIntentKind.CancelPendingOrder,
                    null,
                    "Cancel pending order",
                    "recognized diagnostic cancel-pending-order intent; no pending-order manager is executed at P38");
                return true;
            }

            if (text == "mas cercana a mi" || text == "la mas cercana a mi" || text == "caja mas cercana a mi")
            {
                result = Build(
                    normalization,
                    VoiceCommandIntentMappingStatus.Mapped,
                    VoiceCommandIntentMappingError.DiagnosticIntentNotExecutable,
                    VoiceCommandIntentKind.NearestToUser,
                    null,
                    "Nearest box to user",
                    "recognized diagnostic nearest-to-user selector; no C11 arbitration action is executed at P38");
                return true;
            }

            if (text == "mas cercana al robot" || text == "la mas cercana al robot" || text == "caja mas cercana al robot")
            {
                result = Build(
                    normalization,
                    VoiceCommandIntentMappingStatus.Mapped,
                    VoiceCommandIntentMappingError.DiagnosticIntentNotExecutable,
                    VoiceCommandIntentKind.NearestToRobot,
                    null,
                    "Nearest box to robot",
                    "recognized diagnostic nearest-to-robot selector; no C11 arbitration action is executed at P38");
                return true;
            }

            if (IsNearestAmbiguityText(text))
            {
                result = Build(
                    normalization,
                    VoiceCommandIntentMappingStatus.Mapped,
                    VoiceCommandIntentMappingError.DiagnosticIntentNotExecutable,
                    VoiceCommandIntentKind.NearestAmbiguity,
                    null,
                    "Nearest box clarification required",
                    "recognized diagnostic nearest-box ambiguity; no clarification dialogue or C11 arbitration is executed at P38");
                return true;
            }

            return false;
        }

        private static bool IsCancelPendingOrderText(string text)
        {
            if (text == "cancela la orden pendiente" ||
                text == "cancelar la orden pendiente" ||
                text == "cancela orden pendiente" ||
                text == "la orden pendiente" ||
                text == "la la orden pendiente" ||
                text == "al orden pendiente" ||
                text == "cance la orden pendiente")
            {
                return true;
            }

            bool hasCancelEvidence = text.Contains("cance", System.StringComparison.Ordinal) ||
                text.Contains("cancel", System.StringComparison.Ordinal);
            bool hasOrderEvidence = text.Contains("orden", System.StringComparison.Ordinal) ||
                text.Contains("lador", System.StringComparison.Ordinal);
            bool hasPendingEvidence = text.Contains("pendiente", System.StringComparison.Ordinal) ||
                text.Contains("impendiente", System.StringComparison.Ordinal);
            return hasCancelEvidence && hasOrderEvidence && hasPendingEvidence;
        }

        private static bool IsNearestAmbiguityText(string text)
        {
            return text == "coge la caja mas cercana" ||
                text == "coge caja mas cercana" ||
                text == "coge la caza mas cercana" ||
                text == "coge caza mas cercana" ||
                text == "coge la casa mas cercana" ||
                text == "coge casa mas cercana" ||
                text == "la caja mas cercana" ||
                text == "caja mas cercana" ||
                text == "la caza mas cercana" ||
                text == "caza mas cercana" ||
                text == "la casa mas cercana" ||
                text == "casa mas cercana";
        }

        private static string BuildCandidateDescription(
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentKind kind)
        {
            if (normalization == null || kind == VoiceCommandIntentKind.None)
            {
                return string.Empty;
            }

            switch (kind)
            {
                case VoiceCommandIntentKind.Pick:
                    return $"Pick box {normalization.ObjectLabel}".Trim();
                case VoiceCommandIntentKind.PickAndPlace:
                    return $"PickAndPlace box {normalization.ObjectLabel} to zone {normalization.DestinationLabel}".Trim();
                case VoiceCommandIntentKind.Stop:
                    return "Stop robot";
                case VoiceCommandIntentKind.Resume:
                    return "Resume stopped task";
                default:
                    return string.Empty;
            }
        }

        private static string FormatZoneTargetId(string label)
        {
            if (string.Equals(label, "SELF", System.StringComparison.OrdinalIgnoreCase))
            {
                return "SELF";
            }

            return $"Zone{label}";
        }

        private static bool IsExplicitSpokenTargetLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            foreach (char ch in label)
            {
                if (char.IsDigit(ch))
                {
                    return true;
                }
            }

            return false;
        }

        private static VoiceCommandIntentMappingResult Build(
            VoiceCommandNormalizationResult normalization,
            VoiceCommandIntentMappingStatus status,
            VoiceCommandIntentMappingError error,
            VoiceCommandIntentKind kind,
            MultimodalTaskIntent taskIntent,
            string candidate,
            string explanation)
        {
            return new VoiceCommandIntentMappingResult(
                status,
                error,
                kind,
                taskIntent,
                normalization,
                candidate,
                explanation);
        }
    }
}
