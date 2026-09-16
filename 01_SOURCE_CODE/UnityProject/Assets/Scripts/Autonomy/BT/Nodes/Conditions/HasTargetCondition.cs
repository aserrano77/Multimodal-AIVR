using System;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;

namespace Autonomy.BT.Nodes.Conditions
{
    /// <summary>
    /// Verifica si existe un objetivo válido asignado en el Blackboard.
    /// </summary>
    public class HasTargetCondition : Node
    {
        private readonly IReadOnlyRobotBlackboard _blackboard;

        public HasTargetCondition(IReadOnlyRobotBlackboard blackboard)
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
        }

        public override NodeStatus Tick()
        {
            // La API de RobotBlackboard.TryGet devuelve false si la clave no existe o si el valor es null.
            if (_blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out var target) && target != null)
            {
                return NodeStatus.Success;
            }

            return NodeStatus.Failure;
        }
    }
}
