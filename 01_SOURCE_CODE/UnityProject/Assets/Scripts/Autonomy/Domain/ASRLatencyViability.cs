using System;

namespace Autonomy.Domain
{
    public static class ASRLatencyViability
    {
        public static ASRLatencyViabilityStatus Evaluate(long latencyMs)
        {
            long safeLatencyMs = Math.Max(0L, latencyMs);
            if (safeLatencyMs <= 1500L)
            {
                return ASRLatencyViabilityStatus.Good;
            }

            if (safeLatencyMs <= 2500L)
            {
                return ASRLatencyViabilityStatus.Acceptable;
            }

            if (safeLatencyMs <= 5000L)
            {
                return ASRLatencyViabilityStatus.Borderline;
            }

            return ASRLatencyViabilityStatus.NotViable;
        }
    }
}
