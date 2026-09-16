namespace Autonomy.Domain
{
    public static class ASRBackendSelectionPolicy
    {
        public static ASRBackendSelection Select(
            ASRBackend requestedBackend,
            bool isAndroidRuntime,
            bool whisperAvailable,
            ASRBackendPreflightResult sherpaPreflight,
            bool c11ExperimentalCondition)
        {
            ASRBackend normalizedRequest = NormalizeRequestedBackend(requestedBackend);

            if (normalizedRequest == ASRBackend.ManualStub)
            {
                return new ASRBackendSelection(
                    requestedBackend,
                    ASRBackend.ManualStub,
                    null,
                    "diagnostic_stub_selected",
                    fallbackUsed: requestedBackend != ASRBackend.ManualStub);
            }

            if (normalizedRequest == ASRBackend.WhisperUnity)
            {
                return new ASRBackendSelection(
                    requestedBackend,
                    whisperAvailable ? ASRBackend.WhisperUnity : ASRBackend.Unsupported,
                    null,
                    whisperAvailable ? "manual_whisper_selected" : "whisper_unavailable_manual_selection",
                    fallbackUsed: false);
            }

            if (normalizedRequest == ASRBackend.SherpaOnnx)
            {
                return new ASRBackendSelection(
                    requestedBackend,
                    sherpaPreflight != null && sherpaPreflight.IsAvailable ? ASRBackend.SherpaOnnx : ASRBackend.Unsupported,
                    sherpaPreflight,
                    sherpaPreflight != null && sherpaPreflight.IsAvailable ? "manual_sherpa_selected" : "sherpa_unavailable_manual_selection",
                    fallbackUsed: false);
            }

            if (normalizedRequest != ASRBackend.Auto)
            {
                return new ASRBackendSelection(
                    requestedBackend,
                    ASRBackend.Unsupported,
                    sherpaPreflight,
                    "unsupported_backend_requested",
                    fallbackUsed: false);
            }

            if (isAndroidRuntime)
            {
                if (sherpaPreflight != null && sherpaPreflight.IsAvailable)
                {
                    return new ASRBackendSelection(
                        requestedBackend,
                        ASRBackend.SherpaOnnx,
                        sherpaPreflight,
                        "android_auto_sherpa_preflight_available",
                        fallbackUsed: false);
                }

                string reason = c11ExperimentalCondition
                    ? "android_auto_c11_sherpa_unavailable_no_silent_fallback"
                    : "android_auto_sherpa_unavailable_no_silent_fallback";
                return new ASRBackendSelection(
                    requestedBackend,
                    ASRBackend.Unsupported,
                    sherpaPreflight,
                    reason,
                    fallbackUsed: false);
            }

            if (whisperAvailable)
            {
                return new ASRBackendSelection(
                    requestedBackend,
                    ASRBackend.WhisperUnity,
                    null,
                    "editor_or_pc_auto_whisper_available",
                    fallbackUsed: false);
            }

            return new ASRBackendSelection(
                requestedBackend,
                ASRBackend.ManualStub,
                null,
                "editor_or_pc_auto_whisper_unavailable_diagnostic_stub",
                fallbackUsed: true);
        }

        public static ASRBackend NormalizeRequestedBackend(ASRBackend requestedBackend)
        {
            return requestedBackend == ASRBackend.DiagnosticStub
                ? ASRBackend.ManualStub
                : requestedBackend;
        }
    }
}
