using System;

namespace Autonomy.UnityIntegration
{
    public sealed class ASRDebugOptions
    {
        public bool DebugLogging { get; set; }
        public bool SaveLastCapturedClipToWav { get; set; }
        public float SilenceRmsThreshold { get; set; } = 0.005f;
        public float NonSilentSampleThreshold { get; set; } = 0.01f;
        public Action<string> Log { get; set; }
        public Action<string> Warn { get; set; }
    }
}
