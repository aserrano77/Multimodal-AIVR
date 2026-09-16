using System;
using System.Collections.Generic;
using System.Linq;

namespace Autonomy.Domain
{
    public enum VoiceTargetResolutionStatus
    {
        NotRequired,
        Resolved,
        Ambiguous,
        NotFound
    }

    public sealed class VoiceTargetCandidate
    {
        public VoiceTargetCandidate(
            string targetId,
            string category,
            string spokenLabel,
            string displayLabel,
            string voiceAlias,
            string placeTargetId,
            bool isAvailable = true)
        {
            TargetId = targetId ?? string.Empty;
            Category = Normalize(category);
            SpokenLabel = Normalize(spokenLabel);
            DisplayLabel = Normalize(displayLabel);
            VoiceAlias = Normalize(voiceAlias);
            PlaceTargetId = placeTargetId ?? string.Empty;
            IsAvailable = isAvailable;
        }

        public string TargetId { get; }
        public string Category { get; }
        public string SpokenLabel { get; }
        public string DisplayLabel { get; }
        public string VoiceAlias { get; }
        public string PlaceTargetId { get; }
        public bool IsAvailable { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
        }
    }

    public sealed class VoiceTargetResolutionResult
    {
        public VoiceTargetResolutionResult(
            VoiceTargetResolutionStatus status,
            string reason,
            string requestedLabel,
            string objectCategory,
            string objectSelectionMode,
            int candidateCount,
            VoiceTargetCandidate selectedCandidate)
        {
            Status = status;
            Reason = reason ?? string.Empty;
            RequestedLabel = requestedLabel ?? string.Empty;
            ObjectCategory = objectCategory ?? string.Empty;
            ObjectSelectionMode = objectSelectionMode ?? string.Empty;
            CandidateCount = candidateCount;
            SelectedCandidate = selectedCandidate;
        }

        public VoiceTargetResolutionStatus Status { get; }
        public string Reason { get; }
        public string RequestedLabel { get; }
        public string ObjectCategory { get; }
        public string ObjectSelectionMode { get; }
        public int CandidateCount { get; }
        public VoiceTargetCandidate SelectedCandidate { get; }
    }

    public static class VoiceTargetResolver
    {
        public static VoiceTargetResolutionResult Resolve(
            MultimodalTaskIntent intent,
            VoiceCommandNormalizationResult normalization,
            IEnumerable<VoiceTargetCandidate> candidates)
        {
            if (intent == null || intent.TaskFlow != AutonomousTaskFlow.PickAndPlace)
            {
                return Result(VoiceTargetResolutionStatus.NotRequired, "not_pick_and_place", string.Empty, string.Empty, string.Empty, 0, null);
            }

            string requestedLabel = intent.ObjectSelectionMode == MultimodalObjectSelectionMode.ExplicitTargetId
                ? intent.TargetId
                : !string.IsNullOrWhiteSpace(normalization?.ObjectLabel)
                    ? normalization.ObjectLabel
                    : intent.ObjectCategory;
            requestedLabel = Normalize(requestedLabel);
            if (string.IsNullOrWhiteSpace(requestedLabel))
            {
                return Result(VoiceTargetResolutionStatus.NotRequired, "missing_requested_label", requestedLabel, intent.ObjectCategory, string.Empty, 0, null);
            }

            List<VoiceTargetCandidate> available = (candidates ?? Array.Empty<VoiceTargetCandidate>())
                .Where(candidate => candidate != null && candidate.IsAvailable)
                .ToList();

            if (IsExplicitAlias(requestedLabel))
            {
                List<VoiceTargetCandidate> aliasMatches = available
                    .Where(candidate => MatchesAlias(candidate, requestedLabel))
                    .ToList();
                return ResolveMatches(aliasMatches, requestedLabel, string.Empty, "ExplicitTarget", "target_alias_not_found");
            }

            string category = Normalize(intent.ObjectCategory);
            if (string.IsNullOrWhiteSpace(category))
            {
                category = requestedLabel;
            }

            List<VoiceTargetCandidate> categoryMatches = available
                .Where(candidate => string.Equals(candidate.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return ResolveMatches(categoryMatches, requestedLabel, category, "CategoryUniqueMatch", "category_not_found");
        }

        private static VoiceTargetResolutionResult ResolveMatches(
            List<VoiceTargetCandidate> matches,
            string requestedLabel,
            string category,
            string resolvedMode,
            string notFoundReason)
        {
            int count = matches != null ? matches.Count : 0;
            if (count == 1)
            {
                return Result(VoiceTargetResolutionStatus.Resolved, "resolved", requestedLabel, category, resolvedMode, count, matches[0]);
            }

            if (count > 1)
            {
                return Result(VoiceTargetResolutionStatus.Ambiguous, "multiple_matching_boxes", requestedLabel, category, resolvedMode, count, null);
            }

            return Result(VoiceTargetResolutionStatus.NotFound, notFoundReason, requestedLabel, category, resolvedMode, 0, null);
        }

        private static bool MatchesAlias(VoiceTargetCandidate candidate, string requestedLabel)
        {
            return string.Equals(candidate.SpokenLabel, requestedLabel, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(candidate.DisplayLabel, requestedLabel, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(candidate.VoiceAlias, requestedLabel, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Normalize(candidate.TargetId), requestedLabel, StringComparison.OrdinalIgnoreCase) ||
                   MatchesNumericSuffix(candidate, requestedLabel);
        }

        private static bool MatchesNumericSuffix(VoiceTargetCandidate candidate, string requestedLabel)
        {
            if (!IsDigits(requestedLabel))
            {
                return false;
            }

            return EndsWithDigits(candidate.SpokenLabel, requestedLabel) ||
                   EndsWithDigits(candidate.DisplayLabel, requestedLabel) ||
                   EndsWithDigits(candidate.VoiceAlias, requestedLabel);
        }

        private static bool EndsWithDigits(string value, string digits)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.EndsWith(digits, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExplicitAlias(string label)
        {
            return !string.IsNullOrWhiteSpace(label) && label.Any(char.IsDigit);
        }

        private static bool IsDigits(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.All(char.IsDigit);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
        }

        private static VoiceTargetResolutionResult Result(
            VoiceTargetResolutionStatus status,
            string reason,
            string requestedLabel,
            string objectCategory,
            string objectSelectionMode,
            int candidateCount,
            VoiceTargetCandidate selectedCandidate)
        {
            return new VoiceTargetResolutionResult(
                status,
                reason,
                requestedLabel,
                objectCategory,
                objectSelectionMode,
                candidateCount,
                selectedCandidate);
        }
    }
}
