namespace Autonomy.Domain
{
    public enum VoiceUserFeedbackType
    {
        CommandExecuted,
        CommandAmbiguous,
        CommandIncomplete,
        CommandUnrecognized,
        PickOnlyUnavailable,
        StopUnavailable,
        ResumeUnavailable,
        ConfirmationRequested,
        ConfirmationAccepted,
        ConfirmationCanceled,
        ConfirmationExpired,
        NoPendingConfirmation,
        CommandBlockedByCondition
    }
}
