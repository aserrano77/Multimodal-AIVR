namespace Autonomy.Domain
{
    public enum VoiceVadSegmentDecisionType
    {
        None,
        SpeechStarted,
        SegmentClosed,
        SegmentDiscarded,
        Cooldown
    }
}
