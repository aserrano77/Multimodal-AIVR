using System.Threading.Tasks;
using Autonomy.Domain;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    public interface IUnityAudioASRBackend
    {
        ASRBackend Backend { get; }
        string ModelName { get; }

        ASRBackendPreflightResult Preflight(ASRBackend requestedBackend, string language);

        Task<ASRResult> TranscribeAsync(
            AudioClip clip,
            ASRRequest request,
            string microphoneDevice,
            int sampleRate,
            long durationMs,
            ASRAudioAnalysis analysis,
            string diagnosticAudioPath);
    }
}
