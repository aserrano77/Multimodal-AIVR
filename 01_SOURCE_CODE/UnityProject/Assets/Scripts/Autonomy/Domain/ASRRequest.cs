using System;

namespace Autonomy.Domain
{
    public sealed class ASRRequest
    {
        public ASRRequest(
            ASRBackend requestedBackend,
            string language,
            string microphoneDevice,
            int sampleRate,
            float maxCaptureSeconds,
            string utteranceId = "")
        {
            RequestedBackend = requestedBackend;
            Language = string.IsNullOrWhiteSpace(language) ? "es" : language.Trim();
            MicrophoneDevice = microphoneDevice ?? string.Empty;
            SampleRate = Math.Max(0, sampleRate);
            MaxCaptureSeconds = Math.Max(0f, maxCaptureSeconds);
            UtteranceId = string.IsNullOrWhiteSpace(utteranceId)
                ? ASRUtteranceId.Create()
                : utteranceId.Trim();
        }

        public ASRBackend RequestedBackend { get; }
        public string Language { get; }
        public string MicrophoneDevice { get; }
        public int SampleRate { get; }
        public float MaxCaptureSeconds { get; }
        public string UtteranceId { get; }
    }
}
