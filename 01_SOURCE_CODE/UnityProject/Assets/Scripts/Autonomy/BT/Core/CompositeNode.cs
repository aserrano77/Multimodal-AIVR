using System;
using System.Collections.Generic;

namespace Autonomy.BT.Core
{
    /// <summary>
    /// Base para nodos con múltiples hijos. Se permite una colección vacía; 
    /// la semántica resultante (Success para Sequence, Failure para Selector) 
    /// es intencional y consistente con la lógica clásica de Behavior Trees.
    /// </summary>
    public abstract class CompositeNode : Node
    {
        protected readonly Node[] Children;

        protected CompositeNode(IEnumerable<Node> children)
        {
            if (children == null) throw new ArgumentNullException(nameof(children));

            // Materializamos el enumerable para evitar reevaluaciones costosas y
            // garantizar que la estructura del árbol sea inmutable tras la construcción.
            var nodes = new List<Node>(children);
            foreach (var child in nodes)
            {
                if (child == null) throw new ArgumentException("A child node in the collection is null.", nameof(children));
            }

            Children = nodes.ToArray();
        }

        public override void Reset()
        {
            foreach (var child in Children)
            {
                child.Reset();
            }
        }
    }
}
