using System.Collections.Generic;

namespace Autonomy.BT.Core
{
    public class Selector : CompositeNode
    {
        private int _currentChildIndex = 0;

        public Selector(IEnumerable<Node> children) : base(children) { }

        public override NodeStatus Tick()
        {
            // Si el selector no tiene hijos, el bucle no se ejecuta y devuelve Failure
            // (semántica de identidad estándar para la operación lógica OR).
            for (int i = _currentChildIndex; i < Children.Length; i++)
            {
                var status = Children[i].Tick();

                if (status == NodeStatus.Running)
                {
                    _currentChildIndex = i;
                    return NodeStatus.Running;
                }

                if (status == NodeStatus.Success)
                {
                    Reset();
                    return NodeStatus.Success;
                }
            }

            Reset();
            return NodeStatus.Failure;
        }

        public override void Reset()
        {
            _currentChildIndex = 0;
            base.Reset();
        }
    }
}
