using System;

namespace Autonomy.BT.Core
{
    /// <summary>
    /// Punto de entrada para la evaluación del árbol. No mantiene estado interno
    /// de ejecución; delega completamente el flujo y el estado de los nodos en la raíz.
    /// </summary>
    public class BehaviorTreeRunner
    {
        private readonly Node _root;

        public BehaviorTreeRunner(Node root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public NodeStatus Tick()
        {
            return _root.Tick();
        }

        public void Reset()
        {
            _root.Reset();
        }
    }
}
