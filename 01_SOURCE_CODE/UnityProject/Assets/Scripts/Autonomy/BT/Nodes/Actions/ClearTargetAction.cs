using System;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;

namespace Autonomy.BT.Nodes.Actions
{
    /// <summary>
    /// Acción encargada de aplicar la postcondición de tarea invalidando el objetivo actual.
    /// Resuelve el bucle de re-ejecución explícitamente dejando el árbol en un estado de "Idle lógico".
    /// </summary>
    public class ClearTargetAction : Node
    {
        private readonly IRobotBlackboard _blackboard;

        public ClearTargetAction(IRobotBlackboard blackboard)
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
        }

        public override NodeStatus Tick()
        {
            // Invalidación canónica del target: se elimina del Blackboard usando la API existente.
            // Es una operación idempotente que garantiza la postcondición de "ausencia de tarea".
            _blackboard.Remove(TaskBlackboardKeys.CurrentTarget);
            _blackboard.Remove(TaskBlackboardKeys.ResumeHeldObjectId);
            
            return NodeStatus.Success;
        }
    }
}
