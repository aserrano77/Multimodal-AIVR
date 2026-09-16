using System.Numerics;
using Autonomy.BT.Core;

namespace Autonomy.Services
{
    public class NavigationServiceStub : INavigationService
    {
        private Vector3? _lastPosition;
        private readonly int _ticksToComplete;
        private int _elapsedTicks;

        /// <summary>
        /// Inicializa el stub con un número de ticks requeridos para completar la navegación.
        /// El valor por defecto es 2 (1 tick en Running, 1 tick para Success).
        /// </summary>
        public NavigationServiceStub(int ticksToComplete = 2)
        {
            _ticksToComplete = ticksToComplete;
        }

        /// <summary>
        /// Simulación determinista de navegación:
        /// - Permanece en Running hasta alcanzar _ticksToComplete.
        /// - Un cambio de posición o Stop() reinicia el contador.
        /// </summary>
        public NodeStatus MoveTo(Vector3 position)
        {
            if (_lastPosition == null || position != _lastPosition.Value)
            {
                _lastPosition = position;
                _elapsedTicks = 0;
            }

            _elapsedTicks++;

            if (_elapsedTicks < _ticksToComplete)
            {
                return NodeStatus.Running;
            }

            // Al alcanzar el éxito, limpiamos el estado
            _lastPosition = null;
            _elapsedTicks = 0;
            return NodeStatus.Success;
        }

        public void Stop()
        {
            _lastPosition = null;
            _elapsedTicks = 0;
        }
    }
}
