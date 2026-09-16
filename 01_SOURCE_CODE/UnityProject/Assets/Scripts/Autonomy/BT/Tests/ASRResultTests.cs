using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class ASRResultTests
    {
        [Test]
        public void Successful_Result_Normalizes_Transcript_And_Serializes_Telemetry()
        {
            ASRResult result = ASRResult.Successful(
                "utt_test",
                ASRBackend.ManualStub,
                "  recoge la caja A  ",
                "es",
                1234,
                "Quest Mic",
                16000);

            var payload = result.ToTelemetryPayload();

            Assert.That(result.Transcript, Is.EqualTo("recoge la caja A"));
            Assert.That(payload["utterance_id"], Is.EqualTo("utt_test"));
            Assert.That(payload["backend"], Is.EqualTo("ManualStub"));
            Assert.That(payload["valid_asr_result"], Is.EqualTo(true));
            Assert.That(payload["duration_ms"], Is.EqualTo(1234));
        }

        [Test]
        public void Failed_Result_Keeps_Error_Reason()
        {
            ASRResult result = ASRResult.Failed("utt_failed", ASRBackend.Unsupported, "backend_missing");

            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorReason, Is.EqualTo("backend_missing"));
            Assert.That(result.ToTelemetryPayload()["error_reason"], Is.EqualTo("backend_missing"));
        }

        [Test]
        public void UtteranceId_Generator_Returns_NonEmpty_Unique_Ids()
        {
            string first = ASRUtteranceId.Create();
            string second = ASRUtteranceId.Create();

            Assert.That(first, Does.StartWith("utt_"));
            Assert.That(second, Does.StartWith("utt_"));
            Assert.That(second, Is.Not.EqualTo(first));
        }
    }
}
