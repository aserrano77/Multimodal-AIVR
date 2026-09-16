namespace Autonomy.Domain
{
    public interface IRobotVoiceFeedbackSink
    {
        void Emit(RobotVoiceFeedbackMessage message);
    }
}
