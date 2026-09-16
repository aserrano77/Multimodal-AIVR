namespace Autonomy.Domain
{
    public readonly struct VoiceVadSegmentDecision
    {
        public VoiceVadSegmentDecision(
            VoiceVadSegmentDecisionType type,
            float segmentStartTime,
            float segmentEndTime,
            string reason)
        {
            Type = type;
            SegmentStartTime = segmentStartTime;
            SegmentEndTime = segmentEndTime;
            Reason = reason ?? string.Empty;
        }

        public VoiceVadSegmentDecisionType Type { get; }
        public float SegmentStartTime { get; }
        public float SegmentEndTime { get; }
        public string Reason { get; }
        public float SegmentDuration => SegmentEndTime > SegmentStartTime
            ? SegmentEndTime - SegmentStartTime
            : 0f;

        public static VoiceVadSegmentDecision None { get; } = new(
            VoiceVadSegmentDecisionType.None,
            0f,
            0f,
            string.Empty);
    }
}
