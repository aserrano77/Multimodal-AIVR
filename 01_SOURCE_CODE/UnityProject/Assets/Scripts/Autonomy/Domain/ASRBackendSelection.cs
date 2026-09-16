namespace Autonomy.Domain
{
    public sealed class ASRBackendSelection
    {
        public ASRBackendSelection(
            ASRBackend requestedBackend,
            ASRBackend effectiveBackend,
            ASRBackendPreflightResult preflight,
            string reason,
            bool fallbackUsed)
        {
            RequestedBackend = requestedBackend;
            EffectiveBackend = effectiveBackend;
            Preflight = preflight;
            Reason = reason ?? string.Empty;
            FallbackUsed = fallbackUsed;
        }

        public ASRBackend RequestedBackend { get; }
        public ASRBackend EffectiveBackend { get; }
        public ASRBackendPreflightResult Preflight { get; }
        public string Reason { get; }
        public bool FallbackUsed { get; }
        public bool IsAvailable => EffectiveBackend != ASRBackend.Unsupported;
    }
}
