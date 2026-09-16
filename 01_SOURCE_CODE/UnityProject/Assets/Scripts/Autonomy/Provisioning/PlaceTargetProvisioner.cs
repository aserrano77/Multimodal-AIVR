using Autonomy.Core;
using Autonomy.Domain;

namespace Autonomy.Provisioning
{
    public sealed class PlaceTargetProvisioner
    {
        private readonly IRobotBlackboard _blackboard;

        public PlaceTargetProvisioner(IRobotBlackboard blackboard)
        {
            _blackboard = blackboard;
        }

        public void SeedTarget(TargetDescriptor target)
        {
            _blackboard.Set(TaskBlackboardKeys.PlaceTarget, target);
        }
    }
}
