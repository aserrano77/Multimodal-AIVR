using System;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Services;

namespace Autonomy.BT.Nodes.Actions
{
    /// <summary>
    /// Acción para recoger el objetivo actual utilizando su identificador.
    /// </summary>
    public class PickUpTargetAction : Node
    {
        private readonly IReadOnlyRobotBlackboard _blackboard;
        private readonly IManipulationService _manipulationService;

        public PickUpTargetAction(IReadOnlyRobotBlackboard blackboard, IManipulationService manipulationService)
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
            _manipulationService = manipulationService ?? throw new ArgumentNullException(nameof(manipulationService));
        }

        public override NodeStatus Tick()
        {
            if (!_blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out var target) || target == null)
            {
                return NodeStatus.Failure;
            }

            if (_blackboard.TryGet(TaskBlackboardKeys.ResumeHeldObjectId, out string heldObjectId) &&
                string.Equals(heldObjectId, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                return NodeStatus.Success;
            }

            // Consumimos el ID semántico del objetivo para la tarea de manipulación.
            return _manipulationService.Pick(target);
        }
    }
}
