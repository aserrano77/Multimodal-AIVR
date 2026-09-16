using System;

namespace Autonomy.Domain
{
    public sealed class DebugRobotVoiceFeedbackSink : IRobotVoiceFeedbackSink
    {
        private readonly Action<RobotVoiceFeedbackMessage> _write;

        public DebugRobotVoiceFeedbackSink(Action<RobotVoiceFeedbackMessage> write)
        {
            _write = write;
        }

        public RobotVoiceFeedbackMessage LastMessage { get; private set; }

        public void Emit(RobotVoiceFeedbackMessage message)
        {
            if (message == null)
            {
                return;
            }

            LastMessage = message;
            _write?.Invoke(message);
        }
    }
}
