using System;
using Autonomy.BT.Core;
using Autonomy.Core;
using Autonomy.Domain;
using Autonomy.UnityIntegration;
using System.Collections.Generic;
using UnityEngine;

namespace Autonomy.BT.Nodes.Decorators
{
    public class TaskOutcomeDecoratorNode : DecoratorNode
    {
        private readonly IRobotBlackboard _blackboard;

        public TaskOutcomeDecoratorNode(IRobotBlackboard blackboard, Node child) : base(child)
        {
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
        }

        public override NodeStatus Tick()
        {
            var status = Child.Tick();

            if (status == NodeStatus.Failure)
            {
                if (_blackboard.TryGet(TaskBlackboardKeys.LastTaskStatus, out var currentStatus) && currentStatus == TaskStatus.InProgress)
                {
                    _blackboard.Set(TaskBlackboardKeys.LastTaskStatus, TaskStatus.Failed);
                    Debug.Log("[Autonomy] LastTaskStatus = Failed");
                    TiagoExperimentTelemetry.LogEvent(
                        "task_failed",
                        new Dictionary<string, object>
                        {
                            ["task_status"] = TaskStatus.Failed.ToString(),
                            ["reason"] = "bt_sequence_failure"
                        });
                    _blackboard.Remove(TaskBlackboardKeys.CurrentTarget);
                    _blackboard.Remove(TaskBlackboardKeys.PlaceTarget);
                }
            }

            return status;
        }
    }
}
