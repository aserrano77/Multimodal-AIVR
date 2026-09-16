using System.Numerics;
using Autonomy.BT.Core;
using Autonomy.BT.Nodes.Actions;
using Autonomy.BT.Nodes.Conditions;
using Autonomy.BT.Nodes.Decorators;
using Autonomy.Core;
using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public class TaskStatusTests
    {
        private RobotBlackboard _blackboard;

        [SetUp]
        public void SetUp()
        {
            _blackboard = new RobotBlackboard();
        }

        [Test]
        public void MarkTaskInProgressAction_Sets_Status_To_InProgress()
        {
            var action = new MarkTaskInProgressAction(_blackboard);
            var status = action.Tick();

            Assert.AreEqual(NodeStatus.Success, status);
            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var taskStatus);
            Assert.AreEqual(TaskStatus.InProgress, taskStatus);
        }

        [Test]
        public void MarkTaskSucceededAction_Sets_Status_To_Succeeded()
        {
            var action = new MarkTaskSucceededAction(_blackboard);
            var status = action.Tick();

            Assert.AreEqual(NodeStatus.Success, status);
            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var taskStatus);
            Assert.AreEqual(TaskStatus.Succeeded, taskStatus);
        }

        [Test]
        public void TaskOutcomeDecoratorNode_Sets_Failed_And_Clears_Target_On_Child_Failure_If_InProgress()
        {
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("Test", Vector3.Zero));
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.InProgress);

            var alwaysFailsChild = new MockNode(NodeStatus.Failure);
            var decorator = new TaskOutcomeDecoratorNode(_blackboard, alwaysFailsChild);

            var result = decorator.Tick();

            Assert.AreEqual(NodeStatus.Failure, result);
            
            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var failStatus);
            Assert.AreEqual(TaskStatus.Failed, failStatus);

            var hasTarget = _blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out _);
            Assert.IsFalse(hasTarget);
        }

        [Test]
        public void TaskOutcomeDecoratorNode_Does_Not_Contaminate_State_If_Not_InProgress()
        {
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.None);

            var alwaysFailsChild = new MockNode(NodeStatus.Failure);
            var decorator = new TaskOutcomeDecoratorNode(_blackboard, alwaysFailsChild);

            var result = decorator.Tick();

            Assert.AreEqual(NodeStatus.Failure, result);
            
            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var unchangedStatus);
            Assert.AreEqual(TaskStatus.None, unchangedStatus);
        }

        private class MockNode : Node
        {
            private readonly NodeStatus _statusToReturn;
            public MockNode(NodeStatus statusToReturn) => _statusToReturn = statusToReturn;
            public override NodeStatus Tick() => _statusToReturn;
        }
    }
}
