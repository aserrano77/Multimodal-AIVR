using System.Collections.Generic;

namespace Autonomy.Perception
{
    public interface IPerceptionService
    {
        IReadOnlyList<PerceivedObject> Scan(PerceptionQuery query);
        bool TrySelectTarget(PerceptionQuery query, out PerceivedObject selected, out IReadOnlyList<PerceivedObject> candidates, out string failureReason);
    }
}
