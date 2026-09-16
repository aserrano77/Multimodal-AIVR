namespace Autonomy.Domain
{
    public enum VoiceCommandIntentMappingError
    {
        None,
        NullNormalizationResult,
        NormalizationUnrecognized,
        NormalizationAmbiguous,
        MissingAction,
        UnsupportedAction,
        MissingObject,
        MissingObjectLabel,
        MissingDestinationLabel,
        StopIntentNotExecutable,
        ControlIntentNoBridgeTask,
        DiagnosticIntentNotExecutable
    }
}
