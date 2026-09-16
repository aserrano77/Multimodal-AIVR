namespace Autonomy.Domain
{
    public enum RobotVoiceFeedbackKind
    {
        OrderAccepted,
        TargetChanged,
        CommandQueuedAsPending,
        PendingCommandReplaced,
        CommandRejectedAmbiguousTarget,
        CommandRejectedUnrecognized,
        CommandRejectedNotExecutable,
        CommandRejectedTargetNotFound,
        CommandRejectedTargetUnavailable,
        CommandRejectedTaskInProgress,
        CommandRejectedStoppedByVoice,
        RobotBusy,
        ClarificationRequested,
        NearestReferenceAmbiguous,
        CurrentTaskStatus,
        ControlCommandAcknowledged,
        AlreadyStopped,
        PendingCommandCancelled
    }
}
