namespace Autonomy.Domain
{
    public interface IVoiceCommandNormalizer
    {
        VoiceCommandNormalizationResult Normalize(string rawTranscript);
    }
}
