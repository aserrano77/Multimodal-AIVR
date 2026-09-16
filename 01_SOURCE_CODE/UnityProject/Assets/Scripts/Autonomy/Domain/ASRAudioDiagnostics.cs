using System;

namespace Autonomy.Domain
{
    public static class ASRAudioDiagnostics
    {
        public static ASRAudioAnalysis Analyze(
            float[] samples,
            int channels,
            int frequency,
            float silenceRmsThreshold,
            float nonSilentSampleThreshold)
        {
            if (samples == null || samples.Length == 0)
            {
                return new ASRAudioAnalysis(0, channels, frequency, 0f, 0f, 0f, true);
            }

            double sumSquares = 0.0;
            float peak = 0f;
            int nonSilentSamples = 0;
            float absoluteThreshold = Math.Max(0f, nonSilentSampleThreshold);

            for (int i = 0; i < samples.Length; i++)
            {
                float absolute = Math.Abs(samples[i]);
                sumSquares += samples[i] * samples[i];
                if (absolute > peak)
                {
                    peak = absolute;
                }

                if (absolute >= absoluteThreshold)
                {
                    nonSilentSamples++;
                }
            }

            float rms = (float)Math.Sqrt(sumSquares / samples.Length);
            float nonSilentPercent = samples.Length > 0
                ? 100f * nonSilentSamples / samples.Length
                : 0f;
            bool silenceDetected = rms < Math.Max(0f, silenceRmsThreshold);

            return new ASRAudioAnalysis(
                samples.Length,
                channels,
                frequency,
                rms,
                peak,
                nonSilentPercent,
                silenceDetected);
        }
    }
}
