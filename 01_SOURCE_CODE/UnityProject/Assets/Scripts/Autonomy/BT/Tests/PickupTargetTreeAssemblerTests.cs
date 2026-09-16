using System.Numerics;
using Autonomy.BT.Composition;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Services;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public class PickupTargetTreeAssemblerTests
    {
        private RobotBlackboard _blackboard;
        private NavigationServiceSpy _navService;
        private ManipulationServiceSpy _manipService;
        private PickupTargetTreeAssembler _assembler;

        [SetUp]
        public void SetUp()
        {
            _blackboard = new RobotBlackboard();
            _navService = new NavigationServiceSpy();
            _manipService = new ManipulationServiceSpy();
            _assembler = new PickupTargetTreeAssembler();
        }

        [Test]
        public void Assembler_Returns_Valid_Runner()
        {
            var runner = _assembler.Assemble(_blackboard, _navService, _manipService);
            Assert.IsNotNull(runner);
        }

        [Test]
        public void AssembledTree_Fails_When_No_Target_And_Does_Not_Contaminate_Status()
        {
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.Succeeded);
            var runner = _assembler.Assemble(_blackboard, _navService, _manipService);
            
            // Primer Tick: falla en HasTargetCondition (ausencia de tarea).
            // El decorador no debe sobreescribir el estado previo si no estaba InProgress.
            Assert.AreEqual(NodeStatus.Failure, runner.Tick());

            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var endStatus);
            Assert.AreEqual(TaskStatus.Succeeded, endStatus);
        }

        [Test]
        public void AssembledTree_Progresses_Through_Sequence_To_Success_And_Clears_Target()
        {
            // Setup: Blackboard con un objetivo válido
            var target = new TargetDescriptor("Box1", new Vector3(1, 1, 1));
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, target);

            var runner = _assembler.Assemble(_blackboard, _navService, _manipService);

            // Tick 1: 
            // - Decorator -> Sequence -> HasTarget (Success) -> MarkInProgress (Success) -> Navigate Starts (Running)
            Assert.AreEqual(NodeStatus.Running, runner.Tick());
            Assert.AreEqual(1, _navService.CallCount);
            Assert.AreEqual(0, _manipService.CallCount);
            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var progressStatus);
            Assert.AreEqual(TaskStatus.InProgress, progressStatus);

            // Tick 2: 
            // - Navigate completes (Success)
            // - Pickup Starts (Running)
            Assert.AreEqual(NodeStatus.Running, runner.Tick());
            Assert.AreEqual(2, _navService.CallCount);
            Assert.AreEqual(1, _manipService.CallCount);

            // Tick 3: 
            // - Pickup completes (Success)
            // - ClearTargetAction executes (Success inmediato)
            // - MarkSucceeded executes (Success inmediato)
            // - Sequence completes (Success)
            Assert.AreEqual(NodeStatus.Success, runner.Tick());
            Assert.AreEqual(2, _manipService.CallCount);

            // Verificación del Bloque 15: LastTaskStatus debe ser Succeeded
            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var endStatus2);
            Assert.AreEqual(TaskStatus.Succeeded, endStatus2);

            // Verificación del Bloque 14: el target debe haber sido eliminado.
            bool hasTarget = _blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out _);
            Assert.IsFalse(hasTarget, "El objetivo debió ser eliminado del Blackboard tras completar la secuencia.");
        }

        [Test]
        public void PickAndPlaceTree_Uses_Separate_Pick_And_Place_Targets()
        {
            var pickTarget = new TargetDescriptor("Box1", new Vector3(1, 0, 1));
            var placeTarget = new TargetDescriptor("PlacePoint_A", new Vector3(4, 0, 4));
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, pickTarget);
            _blackboard.Set(TaskBlackboardKeys.PlaceTarget, placeTarget);

            var runner = _assembler.Assemble(_blackboard, _navService, _manipService, AutonomousTaskFlow.PickAndPlace);

            Assert.AreEqual(NodeStatus.Running, runner.Tick());
            Assert.AreEqual(pickTarget.Position, _navService.LastPosition);

            Assert.AreEqual(NodeStatus.Running, runner.Tick());
            Assert.AreEqual(pickTarget.Id, _manipService.LastPickObjectId);

            Assert.AreEqual(NodeStatus.Success, runner.Tick());
            Assert.AreEqual(placeTarget.Position, _navService.LastPosition);
            Assert.AreEqual(placeTarget.Id, _manipService.LastPlaceDestinationId);

            Assert.IsFalse(_blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out _));
            Assert.IsFalse(_blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out _));
            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var endStatus);
            Assert.AreEqual(TaskStatus.Succeeded, endStatus);
        }

        [Test]
        public void PickAndPlaceTree_Fails_When_PlaceTarget_Is_Missing()
        {
            var pickTarget = new TargetDescriptor("Box1", new Vector3(1, 0, 1));
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, pickTarget);

            var runner = _assembler.Assemble(_blackboard, _navService, _manipService, AutonomousTaskFlow.PickAndPlace);

            Assert.AreEqual(NodeStatus.Running, runner.Tick());
            Assert.AreEqual(NodeStatus.Running, runner.Tick());
            Assert.AreEqual(NodeStatus.Failure, runner.Tick());

            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var endStatus);
            Assert.AreEqual(TaskStatus.Failed, endStatus);
            Assert.IsFalse(_blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out _));
        }

        [Test]
        public void AssembledTree_Handles_Failure_Sets_LastTaskStatus_Failed_And_Clears_Target()
        {
            // Setup: Blackboard con un objetivo válido
            var target = new TargetDescriptor("Box2", new Vector3(2, 2, 2));
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, target);

            _navService.ShouldFail = true;

            var runner = _assembler.Assemble(_blackboard, _navService, _manipService);

            // Tick 1:
            // - Decorator -> Sequence -> HasTarget (Success) -> MarkInProgress (Success) -> Navigate (Failure)
            // - Sequence returns Failure
            // - Decorator intercepts Failure, sees InProgress, sets Failed, clears CurrentTarget, returns Failure.
            Assert.AreEqual(NodeStatus.Failure, runner.Tick());

            // Verificación del Bloque 15: LastTaskStatus debe ser Failed
            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var failStatus);
            Assert.AreEqual(TaskStatus.Failed, failStatus);

            // Verificación de limpieza del target
            bool hasTarget = _blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out _);
            Assert.IsFalse(hasTarget, "El objetivo debió ser eliminado del Blackboard tras el fallo.");
        }

        #region Service Spies

        private class NavigationServiceSpy : INavigationService
        {
            public int CallCount { get; private set; }
            public Vector3 LastPosition { get; private set; }
            public bool ShouldFail { get; set; }
            public NodeStatus MoveTo(Vector3 position)
            {
                CallCount++;
                LastPosition = position;
                if (ShouldFail) return NodeStatus.Failure;
                return CallCount >= 2 ? NodeStatus.Success : NodeStatus.Running;
            }
            public void Stop() { }
        }

        private class ManipulationServiceSpy : IManipulationService
        {
            public int CallCount { get; private set; }
            public string LastPickObjectId { get; private set; }
            public string LastPlaceDestinationId { get; private set; }
            public NodeStatus Pick(TargetDescriptor target) => Pick(target?.Id);
            public NodeStatus Pick(string objectId)
            {
                CallCount++;
                LastPickObjectId = objectId;
                return CallCount >= 2 ? NodeStatus.Success : NodeStatus.Running;
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
