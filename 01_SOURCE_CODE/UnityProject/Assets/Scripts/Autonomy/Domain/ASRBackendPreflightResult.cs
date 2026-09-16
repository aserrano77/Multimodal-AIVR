using System;

namespace Autonomy.Domain
{
    public sealed class ASRBackendPreflightResult
    {
        private ASRBackendPreflightResult(
            ASRBackend requestedBackend,
            ASRBackend effectiveBackend,
            ASRBackendAvailability availability,
            string modelName,
            string modelPath,
            long modelMainFileSizeBytes,
            string platform,
            bool androidCompatible,
            string language,
            bool languageCompatible,
            string reason,
            string error,
            string modelLayout,
            string modelFilesReport,
            string runtimePath,
            string runtimeFilesReport,
            string runtimeBindingType,
            string architectureAbi)
        {
            RequestedBackend = requestedBackend;
            EffectiveBackend = effectiveBackend;
            Availability = availability;
            ModelName = modelName ?? string.Empty;
            ModelPath = modelPath ?? string.Empty;
            ModelMainFileSizeBytes = Math.Max(0L, modelMainFileSizeBytes);
            Platform = platform ?? string.Empty;
            AndroidCompatible = androidCompatible;
            Language = language ?? string.Empty;
            LanguageCompatible = languageCompatible;
            Reason = reason ?? string.Empty;
            Error = error ?? string.Empty;
            ModelLayout = modelLayout ?? string.Empty;
            ModelFilesReport = modelFilesReport ?? string.Empty;
            RuntimePath = runtimePath ?? string.Empty;
            RuntimeFilesReport = runtimeFilesReport ?? string.Empty;
            RuntimeBindingType = runtimeBindingType ?? string.Empty;
            ArchitectureAbi = architectureAbi ?? string.Empty;
        }

        public ASRBackend RequestedBackend { get; }
        public ASRBackend EffectiveBackend { get; }
        public ASRBackendAvailability Availability { get; }
        public bool IsAvailable => Availability == ASRBackendAvailability.Available;
        public string ModelName { get; }
        public string ModelPath { get; }
        public long ModelMainFileSizeBytes { get; }
        public string Platform { get; }
        public bool AndroidCompatible { get; }
        public string Language { get; }
        public bool LanguageCompatible { get; }
        public string Reason { get; }
        public string Error { get; }
        public string ModelLayout { get; }
        public string ModelFilesReport { get; }
        public string RuntimePath { get; }
        public string RuntimeFilesReport { get; }
        public string RuntimeBindingType { get; }
        public string ArchitectureAbi { get; }

        public static ASRBackendPreflightResult Available(
            ASRBackend requestedBackend,
            ASRBackend effectiveBackend,
            string modelName,
            string modelPath,
            long modelMainFileSizeBytes,
            string platform,
            bool androidCompatible,
            string language,
            bool languageCompatible,
            string reason = "available",
            string modelLayout = "",
            string modelFilesReport = "",
            string runtimePath = "",
            string runtimeFilesReport = "",
            string runtimeBindingType = "",
            string architectureAbi = "")
        {
            return new ASRBackendPreflightResult(
                requestedBackend,
                effectiveBackend,
                ASRBackendAvailability.Available,
                modelName,
                modelPath,
                modelMainFileSizeBytes,
                platform,
                androidCompatible,
                language,
                languageCompatible,
                reason,
                string.Empty,
                modelLayout,
                modelFilesReport,
                runtimePath,
                runtimeFilesReport,
                runtimeBindingType,
                architectureAbi);
        }

        public static ASRBackendPreflightResult Unavailable(
            ASRBackend requestedBackend,
            ASRBackend effectiveBackend,
            string modelName,
            string modelPath,
            string platform,
            string language,
            string reason,
            string error,
            long modelMainFileSizeBytes = 0L,
            bool androidCompatible = false,
            bool languageCompatible = false,
            string modelLayout = "",
            string modelFilesReport = "",
            string runtimePath = "",
            string runtimeFilesReport = "",
            string runtimeBindingType = "",
            string architectureAbi = "")
        {
            return new ASRBackendPreflightResult(
                requestedBackend,
                effectiveBackend,
                ASRBackendAvailability.Unavailable,
                modelName,
                modelPath,
                modelMainFileSizeBytes,
                platform,
                androidCompatible,
                language,
                languageCompatible,
                reason,
                error,
                modelLayout,
                modelFilesReport,
                runtimePath,
                runtimeFilesReport,
                runtimeBindingType,
                architectureAbi);
        }
    }
}
