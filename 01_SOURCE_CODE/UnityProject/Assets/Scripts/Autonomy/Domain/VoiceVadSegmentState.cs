namespace Autonomy.Domain
{
    public enum VoiceVadSegmentState
    {
        Idle,
        PotentialSpeech,
        InSpeech,
        Cooldown
    }
}
