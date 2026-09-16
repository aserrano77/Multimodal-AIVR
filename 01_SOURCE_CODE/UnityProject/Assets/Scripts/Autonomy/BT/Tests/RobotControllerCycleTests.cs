using System.Numerics;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Integration;
using Autonomy.Services;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public class RobotControllerCycleTests
    {
        private RobotFSM _fsm;
        private RobotBlackboard _blackboard;
        private MockBTRunner _mockRunner;
        private RobotController _controller;
        private SafetyServiceSpy _safetyService;
        private NavigationServiceSpy _navSpy;
        private ManipulationServiceSpy _manipSpy;

        [SetUp]
        public void SetUp()
        {
            _fsm = new RobotFSM(RobotMode.Idle);
            _blackboard = new RobotBlackboard();
            _mockRunner = new MockBTRunner();
            _safetyService = new SafetyServiceSpy { IsSafe = true };
            _navSpy = new NavigationServiceSpy();
            _manipSpy = new ManipulationServiceSpy();

            _controller = new RobotController(
                _fsm,
                _blackboard,
                _mockRunner.Runner,
                _navSpy,
                _manipSpy,
                _safetyService
            );
        }

        [Test]
        public void Idle_WithoutTarget_DoesNotStartBT()
        {
            _controller.Tick();

            Assert.AreEqual(RobotMode.Idle, _fsm.CurrentMode);
            Assert.AreEqual(0, _mockRunner.TickCount);
        }

        [Test]
        public void Idle_WithTarget_TransitionsToAutonomous_AndTicksBT()
        {
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("Box1", Vector3.Zero));

            _controller.Tick();

            Assert.AreEqual(RobotMode.Autonomous, _fsm.CurrentMode);
            Assert.AreEqual(1, _mockRunner.TickCount);
            
            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var status);
            Assert.AreEqual(TaskStatus.None, status);
        }

        [Test]
        public void Idle_WithTarget_ButUnsafe_DoesNotTransitionToAutonomous()
        {
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("BoxUnsafe", Vector3.Zero));
            _safetyService.IsSafe = false;

            _controller.Tick();

            Assert.AreEqual(RobotMode.Idle, _fsm.CurrentMode);
            Assert.AreEqual(0, _mockRunner.TickCount);
        }

        [Test]
        public void Autonomous_WhenUnsafe_TransitionsToSafetyPause_AndDoesNotTickBT_AndStopsServices()
        {
            _fsm.TryChangeMode(RobotMode.Autonomous);
            _safetyService.IsSafe = false;

            _controller.Tick();

            Assert.AreEqual(RobotMode.SafetyPause, _fsm.CurrentMode);
            Assert.AreEqual(0, _mockRunner.TickCount);
            Assert.IsTrue(_navSpy.StopCalled);
            Assert.IsTrue(_manipSpy.StopCalled);
        }

        [Test]
        public void Autonomous_WhenTaskSucceeds_ReturnsToIdle_AndActivatesCooldownOnSameTarget()
        {
            _fsm.TryChangeMode(RobotMode.Autonomous);
            var target = new TargetDescriptor("BoxEnd", Vector3.Zero);
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, target);
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.Succeeded);

            _controller.Tick(); // Sale a Idle

            Assert.AreEqual(RobotMode.Idle, _fsm.CurrentMode);
            
            // Verificamos protección conservadora (Cooldown retiene el target fallido)
            _controller.Tick();
            Assert.AreEqual(RobotMode.Idle, _fsm.CurrentMode, "El cooldown debe prevenir la reentrada si el mismo target persiste.");
        }

        [Test]
        public void Cooldown_IsLifted_WhenTargetIsRemoved()
        {
            _fsm.TryChangeMode(RobotMode.Autonomous);
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("BoxEnd", Vector3.Zero));
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.Succeeded);
            _controller.Tick(); // Sale a Idle, activa cooldown

            // Retiramos físicamente el target
            _blackboard.Remove(TaskBlackboardKeys.CurrentTarget);
            _controller.Tick(); // Detecta ausencia, levanta cooldown
            
            // Inyectamos nuevo target
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("BoxNew", Vector3.Zero));
            _controller.Tick(); // Arranca

            Assert.AreEqual(RobotMode.Autonomous, _fsm.CurrentMode);
        }

        [Test]
        public void Cooldown_IsLifted_WhenTargetIsReplacedDirectly()
        {
            _fsm.TryChangeMode(RobotMode.Autonomous);
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("BoxFail", Vector3.Zero));
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.Failed);
            _controller.Tick(); // Sale a Idle, activa cooldown por "BoxFail"

            Assert.AreEqual(RobotMode.Idle, _fsm.CurrentMode);

            // Reemplazo directo sin hacer Remove() intermedio
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("BoxNewDirect", Vector3.One));
            
            _controller.Tick(); // El controlador compara y ve que es un target distinto

            Assert.AreEqual(RobotMode.Autonomous, _fsm.CurrentMode, "El sistema debe arrancar al detectar un target estructuralmente distinto.");
            Assert.AreEqual(1, _mockRunner.TickCount);
        }

        [Test]
        public void Cooldown_IsMaintained_WhenTargetIsReplacedWithStructurallyIdenticalInstance()
        {
            _fsm.TryChangeMode(RobotMode.Autonomous);
            
            var originalTarget = new TargetDescriptor("BoxIdentical", Vector3.Zero);
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, originalTarget);
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.Failed);
            
            _controller.Tick(); // Activa cooldown
            Assert.AreEqual(RobotMode.Idle, _fsm.CurrentMode);

            // Inyectamos una NUEVA INSTANCIA, pero con el MISMO VALOR estructural
            var identicalNewTarget = new TargetDescriptor("BoxIdentical", Vector3.Zero);
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, identicalNewTarget);
            
            _controller.Tick(); // Evalúa reentrada

            // El cooldown debe discriminar por valor (TargetDescriptor es un record), no por referencia.
            // Por tanto, debe detectar que es lógicamente el mismo y bloquear el arranque.
            Assert.AreEqual(RobotMode.Idle, _fsm.CurrentMode, "El sistema no debe arrancar si el target reemplazado es idéntico por valor.");
            Assert.AreEqual(0, _mockRunner.TickCount);
        }

        [Test]
        public void Autonomous_WhenTaskInProgress_RemainsAutonomous()
        {
            _fsm.TryChangeMode(RobotMode.Autonomous);
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.InProgress);

            _controller.Tick();

            Assert.AreEqual(RobotMode.Autonomous, _fsm.CurrentMode);
            Assert.AreEqual(1, _mockRunner.TickCount);
        }

        [Test]
        public void ResetForNewTask_WhenPreviousTaskSucceededInAutonomous_ClearsTerminalState_AndAllowsNextTick()
        {
            _fsm.TryChangeMode(RobotMode.Autonomous);
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.Succeeded);
            int resetCountBefore = _mockRunner.ResetCount;

            bool accepted = _controller.TryResetForNewTask(
                out var previousStatus,
                out var previousMode,
                out bool wasTerminal,
                out string rejectionReason);

            Assert.IsTrue(accepted);
            Assert.AreEqual(TaskStatus.Succeeded, previousStatus);
            Assert.AreEqual(RobotMode.Autonomous, previousMode);
            Assert.IsTrue(wasTerminal);
            Assert.AreEqual(string.Empty, rejectionReason);
            Assert.Greater(_mockRunner.ResetCount, resetCountBefore);

            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var resetStatus);
            Assert.AreEqual(TaskStatus.None, resetStatus);

            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("BoxNext", Vector3.One));
            _controller.Tick();

            Assert.AreEqual(RobotMode.Autonomous, _fsm.CurrentMode);
            Assert.AreEqual(1, _mockRunner.TickCount);
        }

        [Test]
        public void ResetForNewTask_Rejects_WhenTaskIsInProgress()
        {
            _fsm.TryChangeMode(RobotMode.Autonomous);
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.InProgress);
            int resetCountBefore = _mockRunner.ResetCount;

            bool accepted = _controller.TryResetForNewTask(
                out var previousStatus,
                out var previousMode,
                out bool wasTerminal,
                out string rejectionReason);

            Assert.IsFalse(accepted);
            Assert.AreEqual(TaskStatus.InProgress, previousStatus);
            Assert.AreEqual(RobotMode.Autonomous, previousMode);
            Assert.IsFalse(wasTerminal);
            Assert.AreEqual("task_in_progress", rejectionReason);
            Assert.AreEqual(resetCountBefore, _mockRunner.ResetCount);
        }

        [Test]
        public void ResetForExperimentTrial_StopsServices_ResetsBt_AndClearsTargets()
        {
            _fsm.TryChangeMode(RobotMode.Autonomous);
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("round003_box00_A", Vector3.Zero));
            _blackboard.Set(TaskBlackboardKeys.PlaceTarget, new TargetDescriptor("ZoneA", Vector3.One));
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.InProgress);
            int resetCountBefore = _mockRunner.ResetCount;

            _controller.ResetForExperimentTrial();

            Assert.AreEqual(RobotMode.Idle, _fsm.CurrentMode);
            Assert.IsFalse(_blackboard.TryGet(TaskBlackboardKeys.CurrentTarget, out _));
            Assert.IsFalse(_blackboard.TryGet(TaskBlackboardKeys.PlaceTarget, out _));
            _blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out TaskStatus status);
            Assert.AreEqual(TaskStatus.None, status);
            Assert.Greater(_mockRunner.ResetCount, resetCountBefore);
            Assert.IsTrue(_navSpy.StopCalled);
            Assert.IsTrue(_manipSpy.StopCalled);
        }

        #region Helpers

        private class SafetyServiceSpy : ISafetyService
        {
            public bool IsSafe { get; set; } = true;
            public bool IsSafeToOperate() => IsSafe;
        }

        private class MockBTRunner
        {
            public int TickCount { get; private set; }
            public bool ResetCalled { get; private set; }
            public int ResetCount { get; private set; }
            public BehaviorTreeRunner Runner { get; }

            public MockBTRunner()
            {
                Runner = new BehaviorTreeRunner(new MockNode(this));
            }

            private class MockNode : Node
            {
                private readonly MockBTRunner _parent;
                public MockNode(MockBTRunner parent) => _parent = parent;

                public override NodeStatus Tick()
                {
                    _parent.TickCount++;
                    return NodeStatus.Running;
                }

                public override void Reset()
                {
                    _parent.ResetCalled = true;
                    _parent.ResetCount++;
                }
            }
        }

        private class NavigationServiceSpy : INavigationService
        {
            public bool StopCalled { get; private set; }
            public NodeStatus MoveTo(Vector3 position) => NodeStatus.Running;
            public void Stop() => StopCalled = true;
        }

        private class ManipulationServiceSpy : IManipulationService
        {
            public bool StopCalled { get; private set; }
            public NodeStatus Pick(TargetDescriptor target) => Pick(target?.Id);
            public NodeStatus Pick(string objectId) => NodeStatus.Running;
            public NodeStatus Place(string destinationId) => NodeStatus.Running;
            public void Stop() => StopCalled = true;
        }

        #endregion
    }
}
