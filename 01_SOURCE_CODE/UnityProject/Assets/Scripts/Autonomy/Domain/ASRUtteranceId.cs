using System;
using System.Threading;

namespace Autonomy.Domain
{
    public static class ASRUtteranceId
    {
        private static int _sequence;

        public static string Create()
        {
            int sequence = Interlocked.Increment(ref _sequence);
            return $"utt_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{sequence:0000}";
        }
    }
}
