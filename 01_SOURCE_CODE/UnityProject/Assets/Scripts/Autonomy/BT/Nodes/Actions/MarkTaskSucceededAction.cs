using System;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using System.Collections.Generic;
using UnityEngine;

namespace Autonomy.BT.Nodes.Actions
{
    public class MarkTaskSucceededAction : Node
    {
        private readonly IRobotBlackboard _blackboard;

        public MarkTaskSucceededAction(IRobotBlackboard blackboard)
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
        }

        public override NodeStatus Tick()
        {
            _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.Succeeded);
            Debug.Log("[Autonomy] LastTaskStatus = Succeeded");
            TiagoExperimentTelemetry.LogEvent(
                "task_completed",
                new Dictionary<string, object>
                {
                    ["task_status"] = TaskStatus.Succeeded.ToString()
                });
            return NodeStatus.Success;
        }
    }
}
