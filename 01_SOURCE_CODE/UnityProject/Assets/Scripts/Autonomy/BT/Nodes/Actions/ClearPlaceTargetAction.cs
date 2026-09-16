using System;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;

namespace Autonomy.BT.Nodes.Actions
{
    public class ClearPlaceTargetAction : Node
    {
        private readonly IRobotBlackboard _blackboard;

        public ClearPlaceTargetAction(IRobotBlackboard blackboard)
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
        }

        public override NodeStatus Tick()
        {
            _blackboard.Remove(TaskBlackboardKeys.PlaceTarget);
            _blackboard.Remove(TaskBlackboardKeys.ResumeHeldObjectId);
            return NodeStatus.Success;
        }
    }
}
