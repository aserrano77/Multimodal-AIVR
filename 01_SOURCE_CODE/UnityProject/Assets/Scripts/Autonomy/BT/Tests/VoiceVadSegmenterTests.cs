using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class VoiceVadSegmenterTests
    {
        [Test]
        public void Short_Noise_Does_Not_Open_Or_Close_Utterance()
        {
            VoiceVadSegmenter segmenter = CreateSegmenter();

            Assert.That(segmenter.Process(0.03f, 0.00f).Type, Is.EqualTo(VoiceVadSegmentDecisionType.None));
            Assert.That(segmenter.Process(0.001f, 0.05f).Type, Is.EqualTo(VoiceVadSegmentDecisionType.None));

            Assert.That(segmenter.State, Is.EqualTo(VoiceVadSegmentState.Idle));
        }

        [Test]
        public void Sustained_Voice_Opens_Utterance()
        {
            VoiceVadSegmenter segmenter = CreateSegmenter();

            segmenter.Process(0.03f, 0.00f);
            VoiceVadSegmentDecision decision = segmenter.Process(0.03f, 0.21f);

            Assert.That(decision.Type, Is.EqualTo(VoiceVadSegmentDecisionType.SpeechStarted));
            Assert.That(segmenter.State, Is.EqualTo(VoiceVadSegmentState.InSpeech));
        }

        [Test]
        public void Sufficient_Silence_Closes_Utterance()
        {
            VoiceVadSegmenter segmenter = CreateSegmenter();

            segmenter.Process(0.03f, 0.00f);
            segmenter.Process(0.03f, 0.21f);
            segmenter.Process(0.03f, 0.70f);
            segmenter.Process(0.001f, 0.80f);
            VoiceVadSegmentDecision decision = segmenter.Process(0.001f, 1.51f);

            Assert.That(decision.Type, Is.EqualTo(VoiceVadSegmentDecisionType.SegmentClosed));
            Assert.That(decision.Reason, Is.EqualTo("silence_closed_segment"));
            Assert.That(decision.SegmentDuration, Is.GreaterThanOrEqualTo(1.5f));
        }

        [Test]
        public void Too_Short_Utterance_Waits_For_Minimum_Duration_Before_Closing()
        {
            VoiceVadSegmenter segmenter = CreateSegmenter(minUtteranceSeconds: 1.2f);

            segmenter.Process(0.03f, 0.00f);
            segmenter.Process(0.03f, 0.21f);
            segmenter.Process(0.001f, 0.30f);
            VoiceVadSegmentDecision decision = segmenter.Process(0.001f, 0.95f);

            Assert.That(decision.Type, Is.EqualTo(VoiceVadSegmentDecisionType.None));
            Assert.That(segmenter.State, Is.EqualTo(VoiceVadSegmentState.InSpeech));

            VoiceVadSegmentDecision closed = segmenter.Process(0.001f, 1.21f);

            Assert.That(closed.Type, Is.EqualTo(VoiceVadSegmentDecisionType.SegmentClosed));
            Assert.That(closed.Reason, Is.EqualTo("silence_closed_segment"));
            Assert.That(closed.SegmentDuration, Is.GreaterThanOrEqualTo(1.2f));
        }

        [Test]
        public void Cooldown_Blocks_Immediate_Duplicate_Processing()
        {
            VoiceVadSegmenter segmenter = CreateSegmenter(cooldownSeconds: 1.0f);

            segmenter.Process(0.03f, 0.00f);
            segmenter.Process(0.03f, 0.21f);
            segmenter.Process(0.03f, 0.70f);
            segmenter.Process(0.001f, 0.80f);
            segmenter.Process(0.001f, 1.51f);

            VoiceVadSegmentDecision decision = segmenter.Process(0.03f, 1.70f);

            Assert.That(decision.Type, Is.EqualTo(VoiceVadSegmentDecisionType.Cooldown));
            Assert.That(segmenter.State, Is.EqualTo(VoiceVadSegmentState.Cooldown));
        }

        [Test]
        public void PushToTalk_Mode_Remains_Available()
        {
            Assert.That(VoiceListeningMode.PushToTalk, Is.Not.EqualTo(VoiceListeningMode.ContinuousVad));
        }

        private static VoiceVadSegmenter CreateSegmenter(
            float minUtteranceSeconds = 0.45f,
            float cooldownSeconds = 1.25f)
        {
            return new VoiceVadSegmenter(
                voiceRmsThreshold: 0.015f,
                minVoiceDurationToOpenSeconds: 0.18f,
                silenceDurationToCloseSeconds: 0.65f,
                minUtteranceSeconds: minUtteranceSeconds,
                maxUtteranceSeconds: 4f,
                cooldownSeconds: cooldownSeconds);
        }
    }
}
