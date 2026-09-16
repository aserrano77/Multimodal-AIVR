using System;

namespace Autonomy.Domain
{
    public sealed class VoiceVadSegmenter
    {
        private readonly float _voiceRmsThreshold;
        private readonly float _minVoiceDurationToOpenSeconds;
        private readonly float _silenceDurationToCloseSeconds;
        private readonly float _minUtteranceSeconds;
        private readonly float _maxUtteranceSeconds;
        private readonly float _cooldownSeconds;

        private float _candidateStartTime;
        private float _segmentStartTime;
        private float _silenceStartTime;
        private float _cooldownUntilTime;

        public VoiceVadSegmenter(
            float voiceRmsThreshold,
            float minVoiceDurationToOpenSeconds,
            float silenceDurationToCloseSeconds,
            float minUtteranceSeconds,
            float maxUtteranceSeconds,
            float cooldownSeconds)
        {
            _voiceRmsThreshold = Math.Max(0f, voiceRmsThreshold);
            _minVoiceDurationToOpenSeconds = Math.Max(0f, minVoiceDurationToOpenSeconds);
            _silenceDurationToCloseSeconds = Math.Max(0f, silenceDurationToCloseSeconds);
            _minUtteranceSeconds = Math.Max(0f, minUtteranceSeconds);
            _maxUtteranceSeconds = Math.Max(_minUtteranceSeconds, maxUtteranceSeconds);
            _cooldownSeconds = Math.Max(0f, cooldownSeconds);
            Reset();
        }

        public VoiceVadSegmentState State { get; private set; }
        public bool IsInSpeech => State == VoiceVadSegmentState.InSpeech;
        public float CurrentSegmentStartTime => _segmentStartTime;
        public float CurrentSilenceStartTime => _silenceStartTime;

        public void Reset()
        {
            State = VoiceVadSegmentState.Idle;
            _candidateStartTime = 0f;
            _segmentStartTime = 0f;
            _silenceStartTime = -1f;
            _cooldownUntilTime = 0f;
        }

        public VoiceVadSegmentDecision Process(float rms, float timeSeconds)
        {
            float safeRms = Math.Max(0f, rms);
            float safeTime = Math.Max(0f, timeSeconds);
            bool hasVoice = safeRms >= _voiceRmsThreshold;

            if (safeTime < _cooldownUntilTime)
            {
                State = VoiceVadSegmentState.Cooldown;
                return new VoiceVadSegmentDecision(
                    VoiceVadSegmentDecisionType.Cooldown,
                    0f,
                    0f,
                    "cooldown_active");
            }

            if (State == VoiceVadSegmentState.Cooldown)
            {
                State = VoiceVadSegmentState.Idle;
            }

            switch (State)
            {
                case VoiceVadSegmentState.Idle:
                    if (!hasVoice)
                    {
                        return VoiceVadSegmentDecision.None;
                    }

                    State = VoiceVadSegmentState.PotentialSpeech;
                    _candidateStartTime = safeTime;
                    return VoiceVadSegmentDecision.None;

                case VoiceVadSegmentState.PotentialSpeech:
                    if (!hasVoice)
                    {
                        State = VoiceVadSegmentState.Idle;
                        _candidateStartTime = 0f;
                        return VoiceVadSegmentDecision.None;
                    }

                    if (safeTime - _candidateStartTime < _minVoiceDurationToOpenSeconds)
                    {
                        return VoiceVadSegmentDecision.None;
                    }

                    State = VoiceVadSegmentState.InSpeech;
                    _segmentStartTime = _candidateStartTime;
                    _silenceStartTime = -1f;
                    return new VoiceVadSegmentDecision(
                        VoiceVadSegmentDecisionType.SpeechStarted,
                        _segmentStartTime,
                        safeTime,
                        "voice_sustained");

                case VoiceVadSegmentState.InSpeech:
                    return ProcessInSpeech(hasVoice, safeTime);

                default:
                    return VoiceVadSegmentDecision.None;
            }
        }

        public float GetCurrentSegmentDuration(float timeSeconds)
        {
            if (State != VoiceVadSegmentState.InSpeech || _segmentStartTime <= 0f && timeSeconds <= 0f)
            {
                return 0f;
            }

            return Math.Max(0f, Math.Max(0f, timeSeconds) - _segmentStartTime);
        }

        private VoiceVadSegmentDecision ProcessInSpeech(bool hasVoice, float timeSeconds)
        {
            float duration = timeSeconds - _segmentStartTime;
            if (_maxUtteranceSeconds > 0f && duration >= _maxUtteranceSeconds)
            {
                return CompleteSegment(
                    VoiceVadSegmentDecisionType.SegmentDiscarded,
                    timeSeconds,
                    "max_utterance_exceeded");
            }

            if (hasVoice)
            {
                _silenceStartTime = -1f;
                return VoiceVadSegmentDecision.None;
            }

            if (_silenceStartTime < 0f)
            {
                _silenceStartTime = timeSeconds;
                return VoiceVadSegmentDecision.None;
            }

            if (timeSeconds - _silenceStartTime < _silenceDurationToCloseSeconds)
            {
                return VoiceVadSegmentDecision.None;
            }

            if (duration < _minUtteranceSeconds)
            {
                return VoiceVadSegmentDecision.None;
            }

            return CompleteSegment(
                VoiceVadSegmentDecisionType.SegmentClosed,
                timeSeconds,
                "silence_closed_segment");
        }

        private VoiceVadSegmentDecision CompleteSegment(
            VoiceVadSegmentDecisionType type,
            float segmentEndTime,
            string reason)
        {
            var decision = new VoiceVadSegmentDecision(
                type,
                _segmentStartTime,
                segmentEndTime,
                reason);
            State = _cooldownSeconds > 0f
                ? VoiceVadSegmentState.Cooldown
                : VoiceVadSegmentState.Idle;
            _cooldownUntilTime = segmentEndTime + _cooldownSeconds;
            _candidateStartTime = 0f;
            _segmentStartTime = 0f;
            _silenceStartTime = -1f;
            return decision;
        }
    }
}
