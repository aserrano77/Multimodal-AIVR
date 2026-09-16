using System;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;

namespace Autonomy.BT.Nodes.Actions
{
    public class MarkTaskInProgressAction : Node
    {
        private readonly IRobotBlackboard _blackboard;

        public MarkTaskInProgressAction(IRobotBlackboard blackboard)
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
        }

        public override NodeStatus Tick()
        {
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.InProgress);
            return NodeStatus.Success;
        }
    }
}
