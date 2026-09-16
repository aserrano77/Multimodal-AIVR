using System;
using Autonomy.Core;
using Autonomy.Domain;

namespace Autonomy.Provisioning
{
    /// <summary>
    /// Punto de entrada de escritura (push) para asignar objetivos al Blackboard sin exponerlo.
    /// </summary>
    public class ManualTargetProvisioner
    {
        private readonly IRobotBlackboard _blackboard;

        public ManualTargetProvisioner(IRobotBlackboard blackboard)
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
        }

        public void SeedTarget(TargetDescriptor target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            _blackboard.Set(TaskBlackboardKeys.CurrentTarget, target);
        }

        public void ClearTarget()
        {
            _blackboard.Remove(TaskBlackboardKeys.CurrentTarget);
        }
    }
}
