using System.Numerics;
using Autonomy.BT.Core;
using Autonomy.BT.Nodes.Actions;
using Autonomy.BT.Nodes.Conditions;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Services;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public class AutonomyNodesTests
    {
        private RobotBlackboard _blackboard;
        private NavigationServiceSpy _navService;
        private ManipulationServiceSpy _manipService;

        [SetUp]
        public void SetUp()
        {
            _blackboard = new RobotBlackboard();
            _navService = new NavigationServiceSpy();
            _manipService = new ManipulationServiceSpy();
        }

        [Test]
        public void HasTargetCondition_Fails_WhenBlackboardEmpty()
        {
            var condition = new HasTargetCondition(_blackboard);
            Assert.AreEqual(NodeStatus.Failure, condition.Tick());
        }

        [Test]
        public void HasTargetCondition_Succeeds_WhenTargetIsSet()
        {
            var target = new TargetDescriptor("Box1", Vector3.Zero);
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, target);

            var condition = new HasTargetCondition(_blackboard);
            Assert.AreEqual(NodeStatus.Success, condition.Tick());
        }

        [Test]
        public void NavigateToTargetAction_Consumes_Correct_Position()
        {
            var position = new Vector3(10, 20, 30);
            var target = new TargetDescriptor("Box1", position);
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, target);

            var navAction = new NavigateToTargetAction(_blackboard, _navService);
            navAction.Tick();

            // Verificamos que se llamó con la posición correcta
            Assert.AreEqual(position, _navService.LastPosition);
        }

        [Test]
        public void PickUpTargetAction_Consumes_Correct_Id()
        {
            const string targetId = "Target_Alpha";
            var target = new TargetDescriptor(targetId, Vector3.Zero);
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, target);

            var pickAction = new PickUpTargetAction(_blackboard, _manipService);
            pickAction.Tick();

            // Verificamos que se llamó con el ID correcto
            Assert.AreEqual(targetId, _manipService.LastPickObjectId);
        }

        [Test]
        public void PickUpTargetAction_SkipsPick_WhenResumeAlreadyHoldsCurrentTarget()
        {
            const string targetId = "Target_Alpha";
            var target = new TargetDescriptor(targetId, Vector3.Zero);
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, target);
            _blackboard.Set(TaskBlackboardKeys.ResumeHeldObjectId, targetId);

            var pickAction = new PickUpTargetAction(_blackboard, _manipService);
            NodeStatus status = pickAction.Tick();

            Assert.AreEqual(NodeStatus.Success, status);
            Assert.IsNull(_manipService.LastPickObjectId);
        }

        [Test]
        public void NavigateToBlackboardTargetAction_SkipsPickNavigation_WhenResumeAlreadyHoldsCurrentTarget()
        {
            const string targetId = "Target_Alpha";
            var target = new TargetDescriptor(targetId, new Vector3(4, 0, 1));
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, target);
            _blackboard.Set(TaskBlackboardKeys.ResumeHeldObjectId, targetId);

            var navAction = new NavigateToBlackboardTargetAction(
                _blackboard,
                _navService,
                TaskBlackboardKeys.CurrentTarget,
                "navigation_to_pick_started",
                "navigation_to_pick_succeeded",
                "navigation_to_pick_failed");

            NodeStatus status = navAction.Tick();

            Assert.AreEqual(NodeStatus.Success, status);
            Assert.IsNull(_navService.LastPosition);
        }

        [Test]
        public void NavigateToBlackboardTargetAction_ContinuesDirectlyToPlace_WhenResumeAlreadyHoldsCurrentTarget()
        {
            const string heldObjectId = "Target_Alpha";
            var place = new TargetDescriptor("ZoneA", new Vector3(8, 0, 2));
            _blackboard.Set(TaskBlackboardKeys.PlaceTarget, place);
            _blackboard.Set(TaskBlackboardKeys.ResumeHeldObjectId, heldObjectId);

            var navAction = new NavigateToBlackboardTargetAction(
                _blackboard,
                _navService,
                TaskBlackboardKeys.PlaceTarget,
                "navigation_to_place_started",
                "navigation_to_place_succeeded",
                "navigation_to_place_failed");

            NodeStatus status = navAction.Tick();

            Assert.AreEqual(NodeStatus.Running, status);
            Assert.AreEqual(place.Position, _navService.LastPosition);
        }

        [Test]
        public void PlaceTargetAction_Consumes_Separate_PlaceTarget_Id()
        {
            const string placeTargetId = "PlacePoint_A";
            var target = new TargetDescriptor(placeTargetId, Vector3.Zero);
            _blackboard.Set(TaskBlackboardKeys.PlaceTarget, target);

            var placeAction = new PlaceTargetAction(_blackboard, _manipService);
            placeAction.Tick();

            Assert.AreEqual(placeTargetId, _manipService.LastPlaceDestinationId);
        }

        [Test]
        public void Sequence_Holds_Running_State_Across_Ticks()
        {
            var target = new TargetDescriptor("Box1", new Vector3(1, 2, 3));
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, target);

            var condition = new HasTargetCondition(_blackboard);
            var navAction = new NavigateToTargetAction(_blackboard, _navService);
            var pickAction = new PickUpTargetAction(_blackboard, _manipService);

            var sequence = new Sequence(new Node[] { condition, navAction, pickAction });

            // Tick 1: Condition Success, NavAction starts (Running)
            Assert.AreEqual(NodeStatus.Running, sequence.Tick());
            Assert.IsNotNull(_navService.LastPosition);

            // Tick 2: NavAction completes (Success), PickAction starts (Running)
            Assert.AreEqual(NodeStatus.Running, sequence.Tick());
            Assert.IsNotNull(_manipService.LastPickObjectId);
            
            // Tick 3: PickAction completes (Success), Sequence Success
            Assert.AreEqual(NodeStatus.Success, sequence.Tick());
        }

        #region Service Spies

        private class NavigationServiceSpy : INavigationService
        {
            public Vector3? LastPosition { get; private set; }
            private int _callCount = 0;

            public NodeStatus MoveTo(Vector3 position)
            {
                LastPosition = position;
                _callCount++;
                return _callCount == 1 ? NodeStatus.Running : NodeStatus.Success;
            }

            public void Stop() { }
        }

        private class ManipulationServiceSpy : IManipulationService
        {
            public string LastPickObjectId { get; private set; }
            public string LastPlaceDestinationId { get; private set; }
            private int _callCount = 0;

            public NodeStatus Pick(TargetDescriptor target)
            {
                return Pick(target?.Id);
            }

            public NodeStatus Pick(string objectId)
            {
                LastPickObjectId = objectId;
                _callCount++;
                return _callCount == 1 ? NodeStatus.Running : NodeStatus.Success;
            }

            public NodeStatus Place(string destinationId)
            {
                LastPlaceDestinationId = destinationId;
                return NodeStatus.Success;
            }

            public void Stop() { }
        }

        #endregion
    }
}
