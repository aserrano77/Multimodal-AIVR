using System;

namespace Autonomy.Domain
{
    public readonly struct ExperimentRuntimeMetadataResolution
    {
        public ExperimentRuntimeMetadataResolution(
            string effectiveDriveProfile,
            string effectiveAutonomyPolicy,
            string declaredDriveProfile,
            string declaredAutonomyPolicy,
            string activeDriveProfile,
            string activeAutonomyPolicy,
            string resolutionStrategy,
            bool hasActiveRuntimeMetadata,
            bool hasMismatch)
        {
            EffectiveDriveProfile = effectiveDriveProfile ?? string.Empty;
            EffectiveAutonomyPolicy = effectiveAutonomyPolicy ?? string.Empty;
            DeclaredDriveProfile = declaredDriveProfile ?? string.Empty;
            DeclaredAutonomyPolicy = declaredAutonomyPolicy ?? string.Empty;
            ActiveDriveProfile = activeDriveProfile ?? string.Empty;
            ActiveAutonomyPolicy = activeAutonomyPolicy ?? string.Empty;
            ResolutionStrategy = resolutionStrategy ?? string.Empty;
            HasActiveRuntimeMetadata = hasActiveRuntimeMetadata;
            HasMismatch = hasMismatch;
        }

        public string EffectiveDriveProfile { get; }
        public string EffectiveAutonomyPolicy { get; }
        public string DeclaredDriveProfile { get; }
        public string DeclaredAutonomyPolicy { get; }
        public string ActiveDriveProfile { get; }
        public string ActiveAutonomyPolicy { get; }
        public string ResolutionStrategy { get; }
        public bool HasActiveRuntimeMetadata { get; }
        public bool HasMismatch { get; }
    }

    public static class ExperimentRuntimeMetadataResolver
    {
        public static ExperimentRuntimeMetadataResolution Resolve(
            string declaredDriveProfile,
            string declaredAutonomyPolicy,
            string activeDriveProfile,
            string activeAutonomyPolicy)
        {
            declaredDriveProfile ??= string.Empty;
            declaredAutonomyPolicy ??= string.Empty;
            activeDriveProfile ??= string.Empty;
            activeAutonomyPolicy ??= string.Empty;

            bool hasActiveDriveProfile = !string.IsNullOrWhiteSpace(activeDriveProfile);
            bool hasActiveAutonomyPolicy = !string.IsNullOrWhiteSpace(activeAutonomyPolicy);
            bool hasActiveRuntimeMetadata = hasActiveDriveProfile && hasActiveAutonomyPolicy;

            if (!hasActiveRuntimeMetadata)
            {
                return new ExperimentRuntimeMetadataResolution(
                    declaredDriveProfile,
                    declaredAutonomyPolicy,
                    declaredDriveProfile,
                    declaredAutonomyPolicy,
                    activeDriveProfile,
                    activeAutonomyPolicy,
                    "manual_metadata_used_runtime_unavailable",
                    false,
                    false);
            }

            bool declaredDriveMissing = string.IsNullOrWhiteSpace(declaredDriveProfile);
            bool declaredPolicyMissing = string.IsNullOrWhiteSpace(declaredAutonomyPolicy);
            bool mismatch = (!declaredDriveMissing && !EqualsOrdinalIgnoreCase(declaredDriveProfile, activeDriveProfile)) ||
                (!declaredPolicyMissing && !EqualsOrdinalIgnoreCase(declaredAutonomyPolicy, activeAutonomyPolicy));

            string strategy = mismatch
                ? "active_runtime_metadata_overrode_manual"
                : (declaredDriveMissing || declaredPolicyMissing
                    ? "active_runtime_metadata_filled_empty_fields"
                    : "manual_metadata_matches_runtime");

            return new ExperimentRuntimeMetadataResolution(
                activeDriveProfile,
                activeAutonomyPolicy,
                declaredDriveProfile,
                declaredAutonomyPolicy,
                activeDriveProfile,
                activeAutonomyPolicy,
                strategy,
                true,
                mismatch);
        }

        private static bool EqualsOrdinalIgnoreCase(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
