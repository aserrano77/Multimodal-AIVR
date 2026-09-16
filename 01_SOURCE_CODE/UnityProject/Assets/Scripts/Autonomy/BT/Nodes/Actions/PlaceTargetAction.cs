using System;
using System.Collections.Generic;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Services;
using Autonomy.UnityIntegration;

namespace Autonomy.BT.Nodes.Actions
{
    public class PlaceTargetAction : Node
    {
        private readonly IReadOnlyRobotBlackboard _blackboard;
        private readonly IManipulationService _manipulationService;

        public PlaceTargetAction(IReadOnlyRobotBlackboard blackboard, IManipulationService manipulationService)
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
            _manipulationService = manipulationService ?? throw new ArgumentNullException(nameof(manipulationService));
        }

        public override NodeStatus Tick()
        {
            if (!_blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out var target) || target == null)
            {
                return NodeStatus.Failure;
            }

            return _manipulationService.Place(target.Id);
        }
    }
}
