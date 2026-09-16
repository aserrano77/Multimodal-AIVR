using Autonomy.BT.Core;
using Autonomy.Domain;

namespace Autonomy.Services
{
    public class ManipulationServiceStub : IManipulationService
    {
        private string _lastPickTarget;
        private string _lastPlaceTarget;
        private readonly int _ticksToComplete;
        private int _elapsedTicks;

        /// <summary>
        /// Inicializa el stub con un número de ticks requeridos para completar la operación.
        /// El valor por defecto es 2 (1 tick en Running, 1 tick para Success).
        /// </summary>
        public ManipulationServiceStub(int ticksToComplete = 2)
        {
            _ticksToComplete = ticksToComplete;
        }

        public NodeStatus Pick(TargetDescriptor target)
        {
            return Pick(target?.Id);
        }

        /// <summary>
        /// Simulación determinista de Pick:
        /// - Permanece en Running hasta alcanzar _ticksToComplete.
        /// </summary>
        public NodeStatus Pick(string objectId)
        {
            if (objectId != _lastPickTarget)
            {
                _lastPickTarget = objectId;
                _lastPlaceTarget = null;
                _elapsedTicks = 0;
            }

            _elapsedTicks++;

            if (_elapsedTicks < _ticksToComplete)
            {
                return NodeStatus.Running;
            }

            _lastPickTarget = null;
            _elapsedTicks = 0;
            return NodeStatus.Success;
        }

        /// <summary>
        /// Simulación determinista de Place:
        /// - Permanece en Running hasta alcanzar _ticksToComplete.
        /// </summary>
        public NodeStatus Place(string destinationId)
        {
            if (destinationId != _lastPlaceTarget)
            {
                _lastPlaceTarget = destinationId;
                _lastPickTarget = null;
                _elapsedTicks = 0;
            }

            _elapsedTicks++;

            if (_elapsedTicks < _ticksToComplete)
            {
                return NodeStatus.Running;
            }

            _lastPlaceTarget = null;
            _elapsedTicks = 0;
            return NodeStatus.Success;
        }

        public void Stop()
        {
            _lastPickTarget = null;
            _lastPlaceTarget = null;
            _elapsedTicks = 0;
        }
    }
}
