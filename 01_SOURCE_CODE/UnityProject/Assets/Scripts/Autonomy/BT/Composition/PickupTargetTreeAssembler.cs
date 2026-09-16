using System;
using Autonomy.BT.Core;
using Autonomy.BT.Nodes.Actions;
using Autonomy.BT.Nodes.Conditions;
using Autonomy.BT.Nodes.Decorators;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Services;

namespace Autonomy.BT.Composition
{
    /// <summary>
    /// Ensamblador responsable de construir el árbol de comportamiento para la 
    /// tarea mínima de recogida de un objetivo.
    /// </summary>
    public class PickupTargetTreeAssembler
    {
        /// <summary>
        /// Ensambla el árbol autónomo mínimo actual: 
        /// TaskOutcomeDecoratorNode(
        ///     Sequence(HasTarget -> MarkInProgress -> Navigate -> Pickup -> ClearTarget -> MarkSucceeded)
        /// )
        /// </summary>
        public BehaviorTreeRunner Assemble(
            IRobotBlackboard blackboard,
            INavigationService navigationService,
            IManipulationService manipulationService,
            AutonomousTaskFlow taskFlow = AutonomousTaskFlow.PickOnly)
        {
            if (blackboard == null) throw new ArgumentNullException(nameof(blackboard));
            if (navigationService == null) throw new ArgumentNullException(nameof(navigationService));
            if (manipulationService == null) throw new ArgumentNullException(nameof(manipulationService));

            var condition = new HasTargetCondition(blackboard);
            var markInProgress = new MarkTaskInProgressAction(blackboard);
            var navigate = new NavigateToBlackboardTargetAction(
                blackboard,
                navigationService,
                TaskBlackboardKeys.CurrentTarget,
                "navigation_to_pick_started",
                "navigation_to_pick_succeeded",
                "navigation_to_pick_failed");
            var pickup = new PickUpTargetAction(blackboard, manipulationService);
            var clearTarget = new ClearTargetAction(blackboard);
            var markSucceeded = new MarkTaskSucceededAction(blackboard);

            Node[] nodes = taskFlow == AutonomousTaskFlow.PickAndPlace
                ? new Node[]
                {
                    condition,
                    markInProgress,
                    new LogBtEventAction("bt_pick_place_started"),
                    navigate,
                    pickup,
                    new HasPlaceTargetCondition(blackboard),
                    new NavigateToBlackboardTargetAction(
                        blackboard,
                        navigationService,
                        TaskBlackboardKeys.PlaceTarget,
                        "navigation_to_place_started",
                        "navigation_to_place_succeeded",
                        "navigation_to_place_failed"),
                    new PlaceTargetAction(blackboard, manipulationService),
                    clearTarget,
                    new ClearPlaceTargetAction(blackboard),
                    markSucceeded
                }
                : new Node[] { condition, markInProgress, navigate, pickup, clearTarget, markSucceeded };

            var sequence = new Sequence(nodes);

            var root = new TaskOutcomeDecoratorNode(blackboard, sequence);

            return new BehaviorTreeRunner(root);
        }
    }
}
