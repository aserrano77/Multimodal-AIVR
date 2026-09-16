using System.Threading.Tasks;
using Autonomy.Domain;
using Autonomy.Services;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public sealed class ManualTranscriptASRServiceTests
    {
        [Test]
        public async Task Manual_Service_Transitions_From_Listening_To_Completed()
        {
            ManualTranscriptASRService service = new();
            ASRRequest request = new(ASRBackend.ManualStub, "es", "TestMic", 16000, 4f, "utt_manual");

            ASRResult start = await service.StartListeningAsync(request);
            ASRResult stop = await service.StopListeningAndTranscribeAsync("hola robot");

            Assert.That(start.Success, Is.True);
            Assert.That(service.Status, Is.EqualTo(ASRStatus.Completed));
            Assert.That(stop.Success, Is.True);
            Assert.That(stop.Transcript, Is.EqualTo("hola robot"));
            Assert.That(stop.UtteranceId, Is.EqualTo("utt_manual"));
        }

        [Test]
        public async Task Manual_Service_Returns_Configured_Logistics_Transcript_Exactly_Normalized()
        {
            ManualTranscriptASRService service = new();
            ASRRequest request = new(ASRBackend.ManualStub, "es", string.Empty, 16000, 4f, "utt_pick_place");

            await service.StartListeningAsync(request);
            ASRResult stop = await service.StopListeningAndTranscribeAsync(" lleva la caja a zona A ");

            Assert.That(stop.Success, Is.True);
            Assert.That(stop.Backend, Is.EqualTo(ASRBackend.ManualStub));
            Assert.That(stop.Transcript, Is.EqualTo("lleva la caja a zona A"));
            Assert.That(stop.UtteranceId, Is.EqualTo("utt_pick_place"));
        }

        [Test]
        public async Task Manual_Service_Protects_Against_Duplicate_Start()
        {
            ManualTranscriptASRService service = new();
            ASRRequest request = new(ASRBackend.ManualStub, "es", string.Empty, 0, 0f, "utt_duplicate");

            await service.StartListeningAsync(request);
            ASRResult duplicate = await service.StartListeningAsync(request);

            Assert.That(duplicate.Success, Is.False);
            Assert.That(duplicate.ErrorReason, Is.EqualTo("asr_already_listening"));
        }

        [Test]
        public async Task Manual_Service_Returns_Failed_Result_For_Empty_Transcript()
        {
            ManualTranscriptASRService service = new();
            ASRRequest request = new(ASRBackend.ManualStub, "es", string.Empty, 0, 0f, "utt_empty");

            await service.StartListeningAsync(request);
            ASRResult result = await service.StopListeningAndTranscribeAsync(" ");

            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorReason, Is.EqualTo("manual_stub_empty_transcript"));
            Assert.That(service.Status, Is.EqualTo(ASRStatus.Failed));
        }
    }
}
