using System.Collections.Generic;

namespace Autonomy.BT.Core
{
    public class Sequence : CompositeNode
    {
        private int _currentChildIndex = 0;

        public Sequence(IEnumerable<Node> children) : base(children) { }

        public override NodeStatus Tick()
        {
            // Si la secuencia no tiene hijos, el bucle no se ejecuta y devuelve Success
            // (semántica de identidad estándar para la operación lógica AND).
            for (int i = _currentChildIndex; i < Children.Length; i++)
            {
                var status = Children[i].Tick();

                if (status == NodeStatus.Running)
                {
                    _currentChildIndex = i;
                    return NodeStatus.Running;
                }

                if (status == NodeStatus.Failure)
                {
                    Reset();
                    return NodeStatus.Failure;
                }
            }

            Reset();
            return NodeStatus.Success;
        }

        public override void Reset()
        {
            _currentChildIndex = 0;
            base.Reset();
        }
    }
}
