using System;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Services;

namespace Autonomy.BT.Nodes.Actions
{
    /// <summary>
    /// Acción para navegar hacia la posición del objetivo actual.
    /// </summary>
    public class NavigateToTargetAction : Node
    {
        private readonly IReadOnlyRobotBlackboard _blackboard;
        private readonly INavigationService _navigationService;

        public NavigateToTargetAction(IReadOnlyRobotBlackboard blackboard, INavigationService navigationService)
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        }

        public override NodeStatus Tick()
        {
            if (!_blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out var target) || target == null)
            {
                return NodeStatus.Failure;
            }

            return _navigationService.MoveTo(target.Position);
        }
    }
}
