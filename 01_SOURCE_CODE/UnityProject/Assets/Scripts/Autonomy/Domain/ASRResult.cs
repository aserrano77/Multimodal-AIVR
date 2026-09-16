using System;
using System.Collections.Generic;

namespace Autonomy.Domain
{
    public sealed class ASRResult
    {
        private ASRResult(
            string utteranceId,
            ASRBackend backend,
            bool success,
            string transcript,
            string language,
            float confidence,
            long durationMs,
            string microphoneDevice,
            int sampleRate,
            string errorReason,
            ASRAudioAnalysis audioAnalysis,
            string diagnosticAudioPath)
        {
            UtteranceId = string.IsNullOrWhiteSpace(utteranceId) ? ASRUtteranceId.Create() : utteranceId.Trim();
            Backend = backend;
            Success = success;
            Transcript = NormalizeTranscript(transcript);
            Language = string.IsNullOrWhiteSpace(language) ? string.Empty : language.Trim();
            Confidence = confidence;
            DurationMs = Math.Max(0L, durationMs);
            MicrophoneDevice = microphoneDevice ?? string.Empty;
            SampleRate = Math.Max(0, sampleRate);
            ErrorReason = errorReason ?? string.Empty;
            AudioAnalysis = audioAnalysis;
            DiagnosticAudioPath = diagnosticAudioPath ?? string.Empty;
        }

        public string UtteranceId { get; }
        public ASRBackend Backend { get; }
        public bool Success { get; }
        public string Transcript { get; }
        public string Language { get; }
        public float Confidence { get; }
        public bool HasConfidence => !float.IsNaN(Confidence);
        public long DurationMs { get; }
        public string MicrophoneDevice { get; }
        public int SampleRate { get; }
        public string ErrorReason { get; }
        public ASRAudioAnalysis AudioAnalysis { get; }
        public string DiagnosticAudioPath { get; }

        public static ASRResult Successful(
            string utteranceId,
            ASRBackend backend,
            string transcript,
            string language,
            long durationMs,
            string microphoneDevice,
            int sampleRate,
            float confidence = float.NaN,
            ASRAudioAnalysis audioAnalysis = default,
            string diagnosticAudioPath = "")
        {
            return new ASRResult(
                utteranceId,
                backend,
                true,
                transcript,
                language,
                confidence,
                durationMs,
                microphoneDevice,
                sampleRate,
                string.Empty,
                audioAnalysis,
                diagnosticAudioPath);
        }

        public static ASRResult Failed(
            string utteranceId,
            ASRBackend backend,
            string errorReason,
            string language = "",
            long durationMs = 0L,
            string microphoneDevice = "",
            int sampleRate = 0,
            ASRAudioAnalysis audioAnalysis = default,
            string diagnosticAudioPath = "")
        {
            return new ASRResult(
                utteranceId,
                backend,
                false,
                string.Empty,
                language,
                float.NaN,
                durationMs,
                microphoneDevice,
                sampleRate,
                errorReason,
                audioAnalysis,
                diagnosticAudioPath);
        }

        public Dictionary<string, object> ToTelemetryPayload()
        {
            return new Dictionary<string, object>
            {
                ["utterance_id"] = UtteranceId,
                ["backend"] = Backend.ToString(),
                ["transcript"] = Transcript,
                ["language"] = Language,
                ["confidence"] = Confidence,
                ["duration_ms"] = DurationMs,
                ["microphone_device"] = MicrophoneDevice,
                ["sample_rate"] = SampleRate,
                ["error_reason"] = ErrorReason,
                ["valid_asr_result"] = Success,
                ["rms"] = AudioAnalysis.Rms,
                ["peak"] = AudioAnalysis.Peak,
                ["recorded_samples"] = AudioAnalysis.RecordedSamples,
                ["recorded_duration_ms"] = AudioAnalysis.RecordedDurationMs,
                ["non_silent_sample_percent"] = AudioAnalysis.NonSilentSamplePercent,
                ["silence_detected"] = AudioAnalysis.SilenceDetected,
                ["diagnostic_audio_path"] = DiagnosticAudioPath
            };
        }

        public static string NormalizeTranscript(string transcript)
        {
            return string.IsNullOrWhiteSpace(transcript) ? string.Empty : transcript.Trim();
        }
    }
}
