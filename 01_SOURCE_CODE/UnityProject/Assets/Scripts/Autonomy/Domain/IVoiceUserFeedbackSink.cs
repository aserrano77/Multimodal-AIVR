namespace Autonomy.Domain
{
    public interface IVoiceUserFeedbackSink
    {
        void Emit(VoiceUserFeedbackMessage message);
    }
}
