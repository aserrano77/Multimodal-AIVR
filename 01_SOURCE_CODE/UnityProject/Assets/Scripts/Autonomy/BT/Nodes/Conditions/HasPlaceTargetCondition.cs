using System;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;

namespace Autonomy.BT.Nodes.Conditions
{
    public class HasPlaceTargetCondition : Node
    {
        private readonly IReadOnlyRobotBlackboard _blackboard;

        public HasPlaceTargetCondition(IReadOnlyRobotBlackboard blackboard)
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
        }

        public override NodeStatus Tick()
        {
            return _blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out var target) && target != null
                ? NodeStatus.Success
                : NodeStatus.Failure;
        }
    }
}
