using System.Threading.Tasks;
using Autonomy.Domain;

namespace Autonomy.Services
{
    public interface IASRService
    {
        ASRStatus Status { get; }
        string ActiveUtteranceId { get; }
        ASRBackend ActiveBackend { get; }

        Task<ASRResult> StartListeningAsync(ASRRequest request);
        Task<ASRResult> StopListeningAndTranscribeAsync(string manualTranscript = "");
    }
}
