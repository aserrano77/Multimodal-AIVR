using Autonomy.BT.Core;
using Autonomy.Domain;

namespace Autonomy.Services
{
    public interface IManipulationService
    {
        NodeStatus Pick(TargetDescriptor target);
        NodeStatus Pick(string objectId);
        NodeStatus Place(string destinationId);
        void Stop();
    }
}
