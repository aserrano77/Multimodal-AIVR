namespace Autonomy.Domain
{
    public enum VoiceAutonomyCommandRoutingStatus
    {
        NotSubmitted,
        PendingConfirmation,
        ConfirmationAccepted,
        ConfirmationCanceled,
        ConfirmationExpired,
        NoPendingConfirmation,
        Submitted,
        BridgeRejected
    }
}
