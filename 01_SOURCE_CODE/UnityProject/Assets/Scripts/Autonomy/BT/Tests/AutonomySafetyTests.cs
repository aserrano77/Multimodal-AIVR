using System.Numerics;
using Autonomy.BT.Core;
using Autonomy.BT.Nodes.Actions;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.Integration;
using Autonomy.Services;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public class AutonomySafetyTests
    {
        private RobotFSM _fsm;
        private RobotBlackboard _blackboard;
        private BehaviorTreeRunner _btRunner;
        private NavigationServiceSpy _navService;
        private ManipulationServiceSpy _manipService;
        private SafetyServiceStub _safetyService;
        private RobotController _controller;

        [SetUp]
        public void SetUp()
        {
            _fsm = new RobotFSM(RobotMode.Autonomous);
            _blackboard = new RobotBlackboard();
            _navService = new NavigationServiceSpy();
            _manipService = new ManipulationServiceSpy();
            _safetyService = new SafetyServiceStub();
            
            // Creamos un árbol simple: una acción de navegación infinita (Running)
            var node = new NavigateToTargetAction(_blackboard, _navService);
            _btRunner = new BehaviorTreeRunner(node);
            
            _controller = new RobotController(_fsm, _blackboard, _btRunner, _navService, _manipService, _safetyService);

            // Configuramos un objetivo para que la acción no falle inmediatamente
            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, new TargetDescriptor("Box1", Vector3.Zero));
        }

        [Test]
        public void Controller_TransitionsToSafetyPause_WhenUnsafe()
        {
            _safetyService.IsSafe = false;
            
            _controller.Tick();

            Assert.AreEqual(RobotMode.SafetyPause, _fsm.CurrentMode);
        }

        [Test]
        public void Controller_StopsServices_WhenEnteringSafetyPause()
        {
            _safetyService.IsSafe = false;
            
            _controller.Tick();

            Assert.IsTrue(_navService.StopCalled);
            Assert.IsTrue(_manipService.StopCalled);
        }

        [Test]
        public void BT_DoesNotTick_WhenInSafetyPause()
        {
            _fsm.ForceSafetyPause();
            var initialTickCount = _navService.TickCount;

            _controller.Tick();

            Assert.AreEqual(initialTickCount, _navService.TickCount);
        }

        [Test]
        public void BT_PreservesState_AfterSafetyPause()
        {
            // 1. Primer tick: la acción se ejecuta y devuelve Running
            _controller.Tick();
            Assert.AreEqual(1, _navService.TickCount);

            // 2. Entramos en pausa
            _fsm.ForceSafetyPause();
            _controller.Tick();
            Assert.AreEqual(1, _navService.TickCount); // No ha incrementado

            // 3. Volvemos a Autonomous manualmente (como se acordó, no es automático)
            _fsm.TryChangeMode(RobotMode.Autonomous);
            _controller.Tick();
            
            // 4. Se ha ejecutado el segundo tick de la misma instancia de acción
            Assert.AreEqual(2, _navService.TickCount);
        }

        [Test]
        public void Controller_DoesNotResumeAutomatically_WhenSafeAgain()
        {
            _safetyService.IsSafe = false;
            _controller.Tick(); // Entra en SafetyPause
            
            _safetyService.IsSafe = true;
            _controller.Tick();

            // Debe seguir en SafetyPause
            Assert.AreEqual(RobotMode.SafetyPause, _fsm.CurrentMode);
        }

        #region Helpers

        private class NavigationServiceSpy : INavigationService
        {
            public int TickCount { get; private set; }
            public bool StopCalled { get; private set; }

            public NodeStatus MoveTo(Vector3 position)
            {
                TickCount++;
                return NodeStatus.Running;
            }

            public void Stop()
            {
                StopCalled = true;
            }
        }

        private class ManipulationServiceSpy : IManipulationService
        {
            public bool StopCalled { get; private set; }

            public NodeStatus Pick(TargetDescriptor target) => Pick(target?.Id);
            public NodeStatus Pick(string objectId) => NodeStatus.Running;
            public NodeStatus Place(string destinationId) => NodeStatus.Running;
            public void Stop()
            {
                StopCalled = true;
            }
        }

        #endregion
    }
}
