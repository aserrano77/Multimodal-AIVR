namespace Autonomy.Domain
{
    public interface IVoiceCommandIntentMapper
    {
        VoiceCommandIntentMappingResult Map(VoiceCommandNormalizationResult normalization);
    }
}
