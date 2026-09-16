using System.Collections.Generic;

namespace Autonomy.Domain
{
    public interface IVoiceExperimentEventSink
    {
        void Emit(string eventType, Dictionary<string, object> payload);
    }
}
