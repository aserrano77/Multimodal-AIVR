using System;

namespace Autonomy.Domain
{
    public readonly struct ASRAudioAnalysis
    {
        public ASRAudioAnalysis(
            int recordedSamples,
            int channels,
            int frequency,
            float rms,
            float peak,
            float nonSilentSamplePercent,
            bool silenceDetected)
        {
            RecordedSamples = Math.Max(0, recordedSamples);
            Channels = Math.Max(0, channels);
            Frequency = Math.Max(0, frequency);
            Rms = Math.Max(0f, rms);
            Peak = Math.Max(0f, peak);
            NonSilentSamplePercent = Math.Max(0f, nonSilentSamplePercent);
            SilenceDetected = silenceDetected;
        }

        public int RecordedSamples { get; }
        public int Channels { get; }
        public int Frequency { get; }
        public float Rms { get; }
        public float Peak { get; }
        public float NonSilentSamplePercent { get; }
        public bool SilenceDetected { get; }
        public long RecordedDurationMs => Frequency <= 0 || Channels <= 0
            ? 0L
            : (long)Math.Round(1000.0 * RecordedSamples / Channels / Frequency);
    }
}
