using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Autonomy.Domain;

namespace Autonomy.Services
{
    public sealed class ManualTranscriptASRService : IASRService
    {
        private readonly Stopwatch _stopwatch = new();
        private ASRRequest _activeRequest;

        public ASRStatus Status { get; private set; } = ASRStatus.Idle;
        public string ActiveUtteranceId => _activeRequest != null ? _activeRequest.UtteranceId : string.Empty;
        public ASRBackend ActiveBackend => ASRBackend.ManualStub;

        public Task<ASRResult> StartListeningAsync(ASRRequest request)
        {
            if (Status == ASRStatus.Listening || Status == ASRStatus.Transcribing)
            {
                return Task.FromResult(ASRResult.Failed(
                    ActiveUtteranceId,
                    ASRBackend.ManualStub,
                    "asr_already_listening",
                    request?.Language ?? string.Empty,
                    _stopwatch.ElapsedMilliseconds,
                    request?.MicrophoneDevice ?? string.Empty,
                    request?.SampleRate ?? 0));
            }

            _activeRequest = request ?? new ASRRequest(ASRBackend.ManualStub, "es", string.Empty, 0, 0f);
            _stopwatch.Restart();
            Status = ASRStatus.Listening;

            return Task.FromResult(ASRResult.Successful(
                _activeRequest.UtteranceId,
                ASRBackend.ManualStub,
                string.Empty,
                _activeRequest.Language,
                0L,
                _activeRequest.MicrophoneDevice,
                _activeRequest.SampleRate));
        }

        public Task<ASRResult> StopListeningAndTranscribeAsync(string manualTranscript = "")
        {
            if (Status != ASRStatus.Listening || _activeRequest == null)
            {
                Status = ASRStatus.Failed;
                return Task.FromResult(ASRResult.Failed(
                    string.Empty,
                    ASRBackend.ManualStub,
                    "asr_not_listening"));
            }

            Status = ASRStatus.Transcribing;
            _stopwatch.Stop();

            ASRResult result;
            string transcript = ASRResult.NormalizeTranscript(manualTranscript);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                Status = ASRStatus.Failed;
                result = ASRResult.Failed(
                    _activeRequest.UtteranceId,
                    ASRBackend.ManualStub,
                    "manual_stub_empty_transcript",
                    _activeRequest.Language,
                    _stopwatch.ElapsedMilliseconds,
                    _activeRequest.MicrophoneDevice,
                    _activeRequest.SampleRate);
            }
            else
            {
                Status = ASRStatus.Completed;
                result = ASRResult.Successful(
                    _activeRequest.UtteranceId,
                    ASRBackend.ManualStub,
                    transcript,
                    _activeRequest.Language,
                    _stopwatch.ElapsedMilliseconds,
                    _activeRequest.MicrophoneDevice,
                    _activeRequest.SampleRate);
            }

            _activeRequest = null;
            return Task.FromResult(result);
        }

        public void Reset()
        {
            _stopwatch.Reset();
            _activeRequest = null;
            Status = ASRStatus.Idle;
        }
    }
}
